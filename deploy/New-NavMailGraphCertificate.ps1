<#
.SYNOPSIS
    Creates a self-signed RSA certificate (.pfx + .cer) for the NavMailGraph Entra app registration.

.DESCRIPTION
    Generates a self-signed certificate that NavMailGraph uses to authenticate to Microsoft Entra ID
    with the OAuth 2.0 client-credentials flow (RS256 client assertion).

    Output:
      <OutputDirectory>\<FileName>.pfx  certificate + private key, protected by -PfxPassword.
                                         Install it on the NAV server (Install-NavMailGraphAddin.ps1 -PfxPath).
      <OutputDirectory>\<FileName>.cer  public certificate only (DER). Upload it to the app registration
                                         (New-NavMailGraphApp.ps1 -CertificatePath).

    Certificate properties: RSA (default 2048 bit), SHA-256 signature, KeyUsage DigitalSignature (critical),
    not a CA, subject key identifier.

    Lifetime: the Microsoft Graph "add a certificate" how-to states that a keyCredential endDateTime can be at
    most one year after startDateTime, so -ValidityMonths is limited to 12. The certificate ends one day before
    the full number of months has passed, so a 12-month certificate stays strictly inside one year
    (research-notes.md lists this one-year cap as "verify what Graph actually enforces").

    Implementation branches:
      * PowerShell 7+ (macOS, Linux, Windows): System.Security.Cryptography.X509Certificates.CertificateRequest.
        Nothing is written to any certificate store.
      * Windows PowerShell 5.1: CertificateRequest needs .NET Framework 4.7.2 and is not reliably usable from
        Windows PowerShell 5.1, so this branch uses New-SelfSignedCertificate (PKI module, Windows 10 /
        Windows Server 2016 or later). The certificate is created in Cert:\CurrentUser\My, exported, and then
        removed from the store again unless -KeepInStore is given.

    No password is written to disk. The .pfx file is the only file that contains the private key; on macOS and
    Linux it is created with file mode 600.

.PARAMETER Subject
    Subject distinguished name. Default: CN=NavMailGraph.

.PARAMETER ValidityMonths
    Validity in months, 1-12. Default: 12.

.PARAMETER KeyLength
    RSA key size in bits: 2048 (default), 3072 or 4096.

.PARAMETER OutputDirectory
    Directory for the .pfx and .cer files. Created if missing. Default: the current directory.

.PARAMETER FileName
    Base file name without extension. Default: NavMailGraph.

.PARAMETER PfxPassword
    Password for the .pfx file as SecureString. If omitted you are prompted (twice).

.PARAMETER Force
    Overwrite existing .pfx/.cer files.

.PARAMETER KeepInStore
    Windows PowerShell 5.1 branch only: keep the generated certificate in Cert:\CurrentUser\My.

.EXAMPLE
    ./New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs

    Creates ./certs/NavMailGraph.pfx and ./certs/NavMailGraph.cer valid for 12 months; prompts for the pfx password.

.EXAMPLE
    $pw = Read-Host -AsSecureString 'PFX password'
    ./New-NavMailGraphCertificate.ps1 -Subject 'CN=NAV2016 MailGraph Contoso' -ValidityMonths 6 -PfxPassword $pw -FileName contoso-nav

.OUTPUTS
    PSCustomObject with Subject, Thumbprint, NotBefore, NotAfter, KeyLength, PfxPath, CerPath, Method.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Subject = 'CN=NavMailGraph',

    [ValidateRange(1, 12)]
    [int] $ValidityMonths = 12,

    [ValidateSet(2048, 3072, 4096)]
    [int] $KeyLength = 2048,

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = (Get-Location).Path,

    [ValidatePattern('^[^\\/:*?"<>|]+$')]
    [string] $FileName = 'NavMailGraph',

    [System.Security.SecureString] $PfxPassword,

    [switch] $Force,

    [switch] $KeepInStore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][System.Security.SecureString] $Secure)
    # Kept in memory only, needed for X509Certificate2.Export(Pkcs12, string).
    return (New-Object System.Net.NetworkCredential('', $Secure)).Password
}

function Read-NewPassword {
    while ($true) {
        $p1 = Read-Host -AsSecureString -Prompt 'PFX password'
        $p2 = Read-Host -AsSecureString -Prompt 'Repeat PFX password'
        $t1 = ConvertTo-PlainText $p1
        $t2 = ConvertTo-PlainText $p2
        if ([string]::IsNullOrEmpty($t1)) { Write-Warning 'The password must not be empty.'; continue }
        if ($t1 -cne $t2) { Write-Warning 'The passwords do not match.'; continue }
        return $p1
    }
}

$isDesktop = $PSVersionTable.PSEdition -eq 'Desktop' -or $PSVersionTable.PSVersion.Major -lt 6
$isWindowsOs = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    if ($PSCmdlet.ShouldProcess($OutputDirectory, 'Create output directory')) {
        [void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
    }
}
$outDir = if (Test-Path -LiteralPath $OutputDirectory) { (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath } else { $OutputDirectory }
$pfxPath = Join-Path $outDir ($FileName + '.pfx')
$cerPath = Join-Path $outDir ($FileName + '.cer')

foreach ($p in @($pfxPath, $cerPath)) {
    if ((Test-Path -LiteralPath $p) -and -not $Force) {
        throw "File '$p' already exists. Use -Force to overwrite it."
    }
}

if (-not $PSCmdlet.ShouldProcess("$pfxPath, $cerPath", "Create self-signed certificate '$Subject' (RSA $KeyLength, $ValidityMonths months)")) {
    return
}

if (-not $PfxPassword) { $PfxPassword = Read-NewPassword }
$plainPassword = ConvertTo-PlainText $PfxPassword
if ([string]::IsNullOrEmpty($plainPassword)) { throw 'The PFX password must not be empty.' }

# Validity: start 5 minutes in the past (clock skew), end one day before the full month count.
$notBefore = [System.DateTimeOffset]::UtcNow.AddMinutes(-5)
$notAfter = $notBefore.AddMonths($ValidityMonths).AddDays(-1)

$cert = $null
$method = $null
try {
    if (-not $isDesktop) {
        # ---- PowerShell 7+: CertificateRequest (cross-platform, no certificate store involved) ----
        $method = 'CertificateRequest'
        $rsa = [System.Security.Cryptography.RSA]::Create($KeyLength)
        try {
            $dn = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Subject)
            $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
                $dn, $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $req.CertificateExtensions.Add(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
            $req.CertificateExtensions.Add(
                [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $false))
            $req.CertificateExtensions.Add(
                [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($req.PublicKey, $false))

            $cert = $req.CreateSelfSigned($notBefore, $notAfter)
        }
        finally {
            $rsa.Dispose()
        }

        $pfxBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $plainPassword)
        $cerBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

        [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
        if (-not $isWindowsOs) {
            # owner read/write only (File.SetUnixFileMode exists on .NET 7+)
            $mode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
            [System.IO.File]::SetUnixFileMode($pfxPath, $mode)
        }
        [System.IO.File]::WriteAllBytes($cerPath, $cerBytes)
        [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
    }
    else {
        # ---- Windows PowerShell 5.1: New-SelfSignedCertificate (PKI module) ----
        $method = 'New-SelfSignedCertificate'
        if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
            throw 'New-SelfSignedCertificate (PKI module) is not available. Run this script with PowerShell 7 instead.'
        }
        $params = @{
            Subject           = $Subject
            KeyAlgorithm      = 'RSA'
            KeyLength         = $KeyLength
            HashAlgorithm     = 'SHA256'
            KeyUsage          = 'DigitalSignature'
            KeyExportPolicy   = 'Exportable'
            NotBefore         = $notBefore.LocalDateTime
            NotAfter          = $notAfter.LocalDateTime
            CertStoreLocation = 'Cert:\CurrentUser\My'
        }
        $cert = New-SelfSignedCertificate @params
        try {
            [void](Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $PfxPassword -Force)
            [void](Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -Force)
        }
        finally {
            if (-not $KeepInStore) {
                Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -DeleteKey -ErrorAction SilentlyContinue
            }
        }
    }
}
finally {
    $plainPassword = $null
}

$result = [pscustomobject]@{
    Subject    = $cert.Subject
    Thumbprint = $cert.Thumbprint
    NotBefore  = $cert.NotBefore.ToUniversalTime()
    NotAfter   = $cert.NotAfter.ToUniversalTime()
    KeyLength  = $KeyLength
    PfxPath    = $pfxPath
    CerPath    = $cerPath
    Method     = $method
}
if ($method -eq 'CertificateRequest') { $cert.Dispose() }

Write-Host ''
Write-Host 'Certificate created' -ForegroundColor Green
Write-Host ("  Subject    : {0}" -f $result.Subject)
Write-Host ("  Thumbprint : {0}" -f $result.Thumbprint)
Write-Host ("  Valid (UTC): {0:yyyy-MM-dd HH:mm} .. {1:yyyy-MM-dd HH:mm}" -f $result.NotBefore, $result.NotAfter)
Write-Host ("  PFX        : {0}  (private key - keep it secret, do not commit it)" -f $pfxPath)
Write-Host ("  CER        : {0}  (public key - upload to the app registration)" -f $cerPath)
Write-Host ''
Write-Host 'Next steps' -ForegroundColor Cyan
Write-Host "  1. New-NavMailGraphApp.ps1 -CertificatePath '$cerPath'"
Write-Host "  2. On the NAV server: Install-NavMailGraphAddin.ps1 -PfxPath <copy of the .pfx> -ServerInstance <instance>"
Write-Host '  3. NAV, page 409 "SMTP Mail Setup", group "Microsoft Graph":'
Write-Host '       Graph Auth Method      = Certificate'
Write-Host ("       Graph Cert. Thumbprint = {0}" -f $result.Thumbprint)
Write-Host ("  Renew the certificate before {0:yyyy-MM-dd}." -f $result.NotAfter)

$result
