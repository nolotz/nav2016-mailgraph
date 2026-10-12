#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Installs the NavMailGraph add-in on a Dynamics NAV 2016 server (and optionally the C/SIDE client),
    imports the certificate and checks .NET Framework / TLS 1.2 prerequisites.

.DESCRIPTION
    Windows only; written for Windows PowerShell 5.1 (also runs in PowerShell 7 on Windows). Run elevated.

    What it does, in this order:
      1. Locates the add-in payload (-SourcePath, default: ..\dist\Add-ins\NavMailGraph relative to this
         script, or ..\Add-ins\NavMailGraph / .\Add-ins\NavMailGraph for the extracted release zip) and verifies
         it against SHA256SUMS when that file is found next to the Add-ins folder.
      2. Checks .NET Framework 4.6.2 or later (registry Release >= 394802) and reports the TLS registry values
         SchUseStrongCrypto / SystemDefaultTlsVersions under
         HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319 and HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319.
      3. With -ServerInstance: stops the service MicrosoftDynamicsNavServer$<Instance> (NAV keeps loaded add-in
         DLLs locked until the service stops) and starts it again at the end (also after an error).
      4. Backs up an existing <Add-ins>\NavMailGraph folder to -BackupPath, which must be OUTSIDE the Add-ins
         tree (the NAV Server scans Add-ins subfolders, so an old copy there could be loaded instead), then copies
         the payload to <NavServicePath>\Add-ins\NavMailGraph. The Msal subfolder is copied only with -IncludeMsal.
      5. With -RoleTailoredClient: does the same for the RoleTailored Client Add-ins folder (needed on the
         machine where C/SIDE compiles the objects).
      6. With -FixTls: sets SchUseStrongCrypto = 1 and SystemDefaultTlsVersions = 1 (DWORD) in both registry
         paths. Processes read these values at start-up: restart the NAV Server service (or reboot).
      7. With -PfxPath: imports the certificate into Cert:\LocalMachine\My (Import-PfxCertificate, private key
         non-exportable) and grants Read on the private key file to the NAV service account (from the service
         configuration of -ServerInstance, or -ServiceAccount). Handles CNG keys
         (%ProgramData%\Microsoft\Crypto\Keys\<UniqueName>) and legacy CAPI keys
         (%ProgramData%\Microsoft\Crypto\RSA\MachineKeys\<UniqueKeyContainerName>).
      8. Prints a summary and the values to enter in NAV.

    Supports -WhatIf and -Confirm. Nothing secret is written to disk; the PFX password is only kept in memory.

.PARAMETER SourcePath
    Folder that contains NavMailGraph.dll (and optionally the Msal subfolder).

.PARAMETER NavServicePath
    NAV Server installation folder. Default: C:\Program Files\Microsoft Dynamics NAV\90\Service

.PARAMETER IncludeMsal
    Also deploy the Msal subfolder (NavMailGraph.Msal.dll + MSAL dependencies) for "Graph Token Library = MSAL".

.PARAMETER RoleTailoredClient
    Also deploy to the RoleTailored Client Add-ins folder (C/SIDE development machine).

.PARAMETER RoleTailoredClientPath
    Default: C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client

.PARAMETER PfxPath
    Certificate (with private key) created by New-NavMailGraphCertificate.ps1.

.PARAMETER PfxPassword
    Password of the PFX as SecureString; prompted when -PfxPath is given without it.

.PARAMETER ServerInstance
    NAV Server instance name, e.g. DynamicsNAV90. Used to stop/start the service and to find its service account.

.PARAMETER ServiceAccount
    Account that gets Read access to the private key, e.g. 'NT AUTHORITY\NETWORK SERVICE' or 'CONTOSO\svc-nav'.
    Default: the logon account of MicrosoftDynamicsNavServer$<ServerInstance>.

.PARAMETER NoRestart
    Do not stop/start the NAV Server service even if -ServerInstance is given (files may then be locked).

.PARAMETER FixTls
    Set the .NET Framework TLS registry values (SchUseStrongCrypto, SystemDefaultTlsVersions) to 1.

.PARAMETER BackupPath
    Backup root for replaced add-in folders. Default: %ProgramData%\NavMailGraph\Backup. Must not be inside an
    Add-ins folder.

.PARAMETER SkipHashCheck
    Do not verify the payload against SHA256SUMS.

.EXAMPLE
    .\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -WhatIf

    Shows what would happen.

.EXAMPLE
    .\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls

    Typical server installation: add-in, certificate with private-key ACL for the service account, TLS registry
    values, service restart.

.EXAMPLE
    .\Install-NavMailGraphAddin.ps1 -SourcePath D:\NavMailGraph-1.0.0\Add-ins\NavMailGraph -RoleTailoredClient -IncludeMsal

    Updates server and C/SIDE client add-in folders including the MSAL flavour (no service restart: stop the
    service instances yourself or add -ServerInstance).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SourcePath,

    [ValidateNotNullOrEmpty()]
    [string] $NavServicePath = 'C:\Program Files\Microsoft Dynamics NAV\90\Service',

    [switch] $IncludeMsal,

    [switch] $RoleTailoredClient,

    [ValidateNotNullOrEmpty()]
    [string] $RoleTailoredClientPath = 'C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client',

    [string] $PfxPath,

    [System.Security.SecureString] $PfxPassword,

    [string] $ServerInstance,

    [string] $ServiceAccount,

    [switch] $NoRestart,

    [switch] $FixTls,

    [string] $BackupPath,

    [switch] $SkipHashCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'Install-NavMailGraphAddin.ps1 runs on Windows only (NAV Server host).'
}

$AddinFolderName = 'NavMailGraph'
$MinNetRelease = 394802   # .NET Framework 4.6.2
$TlsKeys = @(
    'HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319'
)
$TlsValueNames = @('SchUseStrongCrypto', 'SystemDefaultTlsVersions')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$summary = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- helpers
function Get-FullPath {
    param([string] $Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-IsUnder {
    param([string] $Child, [string] $Parent)
    $c = (Get-FullPath $Child) + '\'
    $p = (Get-FullPath $Parent) + '\'
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-NetFrameworkVersionName {
    param([int] $Release)
    if ($Release -ge 533320) { return '4.8.1 or later' }
    if ($Release -ge 528040) { return '4.8' }
    if ($Release -ge 461808) { return '4.7.2' }
    if ($Release -ge 461308) { return '4.7.1' }
    if ($Release -ge 460798) { return '4.7' }
    if ($Release -ge 394802) { return '4.6.2' }
    return 'older than 4.6.2'
}

function Get-RegistryDword {
    param([string] $Key, [string] $Name)
    if (-not (Test-Path -LiteralPath $Key)) { return $null }
    return (Get-Item -LiteralPath $Key).GetValue($Name, $null)
}

function Resolve-AccountName {
    # Win32_Service.StartName -> NTAccount name usable for ACLs.
    param([string] $StartName)
    if ([string]::IsNullOrEmpty($StartName)) { return $null }
    if ($StartName -match '^(LocalSystem|NT AUTHORITY\\SYSTEM)$') { return 'NT AUTHORITY\SYSTEM' }
    if ($StartName -match '^NT AUTHORITY\\NetworkService$') { return 'NT AUTHORITY\NETWORK SERVICE' }
    if ($StartName -match '^NT AUTHORITY\\LocalService$') { return 'NT AUTHORITY\LOCAL SERVICE' }
    if ($StartName -match '^\.\\(.+)$') { return ($env:COMPUTERNAME + '\' + $Matches[1]) }
    return $StartName
}

function Test-PayloadHash {
    param([string] $Source, [bool] $WithMsal)
    # Layout: <root>\Add-ins\NavMailGraph\... and <root>\SHA256SUMS with lines "<sha256>  Add-ins/NavMailGraph/<file>"
    $root = Split-Path -Parent (Split-Path -Parent $Source)
    $sums = Join-Path $root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sums)) {
        Write-Warning "No SHA256SUMS found in '$root'; payload integrity not verified."
        return
    }
    $prefix = 'Add-ins/' + (Split-Path -Leaf $Source) + '/'
    $checked = 0
    foreach ($line in (Get-Content -LiteralPath $sums)) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { continue }
        $hash = $Matches[1]; $rel = $Matches[2].Trim()
        if (-not $rel.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $WithMsal -and $rel.Substring($prefix.Length).StartsWith('Msal/', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $file = Join-Path $root ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $file)) { throw "Payload incomplete: '$rel' is listed in SHA256SUMS but missing." }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($actual -ne $hash.ToUpperInvariant()) { throw "Checksum mismatch for '$rel'. Re-download the release." }
        $checked++
    }
    if ($checked -eq 0) { Write-Warning "SHA256SUMS contains no entries for '$prefix'; payload integrity not verified." }
    else { Write-Host ("Verified {0} file(s) against SHA256SUMS" -f $checked) }
}

function Install-AddinFolder {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Source, [string] $AddinsPath, [string] $Label, [bool] $WithMsal, [string] $BackupRoot)
    if (-not (Test-Path -LiteralPath $AddinsPath)) {
        throw "Add-ins folder '$AddinsPath' not found. Check -NavServicePath / -RoleTailoredClientPath."
    }
    $target = Join-Path $AddinsPath $AddinFolderName

    if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $BackupRoot ("{0}-{1}-{2}" -f $Label, $AddinFolderName, $stamp)
        if ($PSCmdlet.ShouldProcess($target, "Back up to '$backup' and remove")) {
            [void](New-Item -ItemType Directory -Path $backup -Force -Confirm:$false)
            Copy-Item -Path (Join-Path $target '*') -Destination $backup -Recurse -Force -Confirm:$false
            try {
                Remove-Item -LiteralPath $target -Recurse -Force -Confirm:$false
            }
            catch {
                throw "Could not replace '$target' (files in use?). Stop the NAV Server instance(s) using this folder (or pass -ServerInstance) and close C/SIDE / the Windows client. Backup is in '$backup'. Error: $($_.Exception.Message)"
            }
            $script:summary.Add("$Label backup: $backup")
        }
    }

    if ($PSCmdlet.ShouldProcess($target, "Copy add-in from '$Source'" + $(if ($WithMsal) { ' (including Msal)' } else { ' (without Msal)' }))) {
        [void](New-Item -ItemType Directory -Path $target -Force -Confirm:$false)
        Get-ChildItem -LiteralPath $Source -File | Copy-Item -Destination $target -Force -Confirm:$false
        foreach ($dir in (Get-ChildItem -LiteralPath $Source -Directory)) {
            if ($dir.Name -eq 'Msal' -and -not $WithMsal) { continue }
            Copy-Item -LiteralPath $dir.FullName -Destination $target -Recurse -Force -Confirm:$false
        }
        $count = @(Get-ChildItem -LiteralPath $target -File -Recurse).Count
        $script:summary.Add("$Label add-in: $target ($count files)")
        Write-Host ("Installed {0} file(s) to {1}" -f $count, $target) -ForegroundColor Green
    }
}

function Grant-PrivateKeyRead {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Thumbprint, [string] $Account)
    $cert = Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $Thumbprint)
    if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint in LocalMachine\My has no private key." }
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($null -eq $rsa) { throw "Certificate $Thumbprint has no accessible RSA private key." }
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $kind = 'CNG'
            $provider = $rsa.Key.Provider.Provider
            $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName)
        }
        elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $kind = 'CAPI'
            $provider = $rsa.CspKeyContainerInfo.ProviderName
            $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName)
        }
        else {
            throw "Unsupported private key type $($rsa.GetType().FullName)."
        }
    }
    finally {
        $rsa.Dispose()
    }
    Write-Host ("Private key: {0} ({1}), file {2}" -f $kind, $provider, $keyFile)
    if (-not (Test-Path -LiteralPath $keyFile)) {
        throw "Private key file '$keyFile' not found. Grant access manually: certlm.msc > Personal > Certificates > right-click > All Tasks > Manage Private Keys > add '$Account' (Read)."
    }
    $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    if ($PSCmdlet.ShouldProcess($keyFile, "Grant Read to $Account ($sid)")) {
        $out = & icacls.exe $keyFile /grant ('*' + $sid + ':(R)') 2>&1
        if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE): $out" }
        $script:summary.Add("Private key ($kind, $provider): Read granted to $Account")
    }
}

# ---------------------------------------------------------------- 1. payload
if (-not $SourcePath) {
    $candidates = @(
        (Join-Path $PSScriptRoot '..\dist\Add-ins\NavMailGraph'),
        (Join-Path $PSScriptRoot '..\Add-ins\NavMailGraph'),
        (Join-Path $PSScriptRoot 'Add-ins\NavMailGraph')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $c 'NavMailGraph.dll')) { $SourcePath = $c; break }
    }
    if (-not $SourcePath) {
        throw ("NavMailGraph.dll not found in: {0}. Build with ./build.sh, extract the release zip, or pass -SourcePath." -f ($candidates -join '; '))
    }
}
$SourcePath = Get-FullPath $SourcePath
$sourceDll = Join-Path $SourcePath 'NavMailGraph.dll'
if (-not (Test-Path -LiteralPath $sourceDll)) { throw "NavMailGraph.dll not found in '$SourcePath'." }
$hasMsal = Test-Path -LiteralPath (Join-Path $SourcePath 'Msal\NavMailGraph.Msal.dll')
if ($IncludeMsal -and -not $hasMsal) { throw "-IncludeMsal given but '$SourcePath\Msal\NavMailGraph.Msal.dll' does not exist." }
$dllVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($sourceDll)
Write-Host ("Payload: {0} (NavMailGraph.dll file version {1}, product version {2})" -f $SourcePath, $dllVersion.FileVersion, $dllVersion.ProductVersion)
if (-not $SkipHashCheck) { Test-PayloadHash -Source $SourcePath -WithMsal ([bool]$IncludeMsal) }

if (-not $BackupPath) { $BackupPath = Join-Path $env:ProgramData 'NavMailGraph\Backup' }
$serverAddins = Join-Path $NavServicePath 'Add-ins'
$rtcAddins = Join-Path $RoleTailoredClientPath 'Add-ins'
foreach ($a in @($serverAddins, $rtcAddins)) {
    if (Test-IsUnder -Child $BackupPath -Parent $a) {
        throw "-BackupPath '$BackupPath' is inside '$a'. NAV scans Add-ins subfolders; keep backups outside."
    }
}
if (-not (Test-Path -LiteralPath $NavServicePath)) { throw "NAV Server folder '$NavServicePath' not found (-NavServicePath)." }
if (-not (Test-Path -LiteralPath (Join-Path $NavServicePath 'Microsoft.Dynamics.Nav.Server.exe'))) {
    Write-Warning "Microsoft.Dynamics.Nav.Server.exe not found in '$NavServicePath'. Is this the NAV 2016 Service folder?"
}

# ---------------------------------------------------------------- 2. .NET Framework and TLS
$ndp = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$release = Get-RegistryDword -Key $ndp -Name 'Release'
if ($null -eq $release) { throw '.NET Framework 4.5 or later not found (registry Release value missing). Install .NET Framework 4.8.' }
$netName = Get-NetFrameworkVersionName -Release ([int]$release)
if ([int]$release -lt $MinNetRelease) {
    throw ".NET Framework $netName (Release $release) found; NavMailGraph needs 4.6.2 or later (Release >= $MinNetRelease). Install .NET Framework 4.8."
}
Write-Host (".NET Framework {0} (Release {1}) - OK" -f $netName, $release)
$summary.Add(".NET Framework: $netName (Release $release)")

$tlsMissing = @()
foreach ($k in $TlsKeys) {
    foreach ($n in $TlsValueNames) {
        $v = Get-RegistryDword -Key $k -Name $n
        $shown = if ($null -eq $v) { '<not set>' } else { [string]$v }
        Write-Host ("TLS: {0}\{1} = {2}" -f $k, $n, $shown)
        if ($v -ne 1) { $tlsMissing += [pscustomobject]@{ Key = $k; Name = $n } }
    }
}
if ($tlsMissing.Count -gt 0 -and -not $FixTls) {
    Write-Warning 'Not all TLS registry values are 1. Microsoft recommends SchUseStrongCrypto=1 and SystemDefaultTlsVersions=1 (both paths) so .NET uses TLS 1.2+ for login.microsoftonline.com / graph.microsoft.com. Re-run with -FixTls to set them.'
    $notes.Add('TLS registry values incomplete; consider -FixTls.')
}

# ---------------------------------------------------------------- service
$service = $null
$serviceName = $null
if ($ServerInstance) {
    $serviceName = 'MicrosoftDynamicsNavServer$' + $ServerInstance
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $serviceName.Replace("'", "\'"))
    if (-not $service) { throw "Service '$serviceName' not found. Check -ServerInstance." }
    Write-Host ("Service {0}: {1}, logon account {2}" -f $serviceName, $service.State, $service.StartName)
    if ($service.PathName -and $service.PathName.IndexOf((Get-FullPath $NavServicePath), [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Warning ("Service binary '{0}' is not under -NavServicePath '{1}'." -f $service.PathName, $NavServicePath)
    }
}
else {
    $navServices = @(Get-CimInstance -ClassName Win32_Service -Filter "Name LIKE 'MicrosoftDynamicsNavServer%'")
    if ($navServices.Count -gt 0) {
        Write-Host ("NAV Server instances on this machine: {0}" -f (($navServices | ForEach-Object { $_.Name.Replace('MicrosoftDynamicsNavServer$', '') + ' (' + $_.State + ')' }) -join ', '))
        $notes.Add('No -ServerInstance given: restart the NAV Server instance(s) yourself so the new add-in is loaded.')
    }
}

if ($PfxPath) {
    if (-not $ServiceAccount) {
        if ($service) { $ServiceAccount = Resolve-AccountName $service.StartName }
        else { throw '-PfxPath needs -ServerInstance (to find the service account) or -ServiceAccount.' }
    }
    $PfxPath = (Resolve-Path -LiteralPath $PfxPath).ProviderPath
    if (-not $PfxPassword) { $PfxPassword = Read-Host -AsSecureString -Prompt "Password for $PfxPath" }
}

# ---------------------------------------------------------------- 3.-7. changes (service stopped)
$stoppedByUs = $false
$thumbprint = $null
try {
    if ($service -and -not $NoRestart -and $service.State -eq 'Running') {
        if ($PSCmdlet.ShouldProcess($serviceName, 'Stop service')) {
            Write-Host "Stopping $serviceName ..."
            Stop-Service -Name $serviceName -Force -Confirm:$false
            $stoppedByUs = $true
            $summary.Add("Service $serviceName stopped")
        }
    }

    Install-AddinFolder -Source $SourcePath -AddinsPath $serverAddins -Label 'Service' -WithMsal ([bool]$IncludeMsal) -BackupRoot $BackupPath
    if ($RoleTailoredClient) {
        Install-AddinFolder -Source $SourcePath -AddinsPath $rtcAddins -Label 'RTC' -WithMsal ([bool]$IncludeMsal) -BackupRoot $BackupPath
    }

    if ($FixTls -and $tlsMissing.Count -gt 0) {
        foreach ($m in $tlsMissing) {
            if ($PSCmdlet.ShouldProcess("$($m.Key)\$($m.Name)", 'Set DWORD 1')) {
                if (-not (Test-Path -LiteralPath $m.Key)) { [void](New-Item -Path $m.Key -Force -Confirm:$false) }
                [void](New-ItemProperty -LiteralPath $m.Key -Name $m.Name -Value 1 -PropertyType DWord -Force -Confirm:$false)
                $summary.Add("TLS: $($m.Key)\$($m.Name) = 1")
            }
        }
        Write-Warning 'TLS registry values changed. They take effect when the process starts: restart the NAV Server service (done automatically with -ServerInstance) or reboot to be sure.'
        $notes.Add('TLS registry values changed: NAV Server service restart (or reboot) required.')
    }
    elseif ($FixTls) {
        Write-Host 'TLS registry values already set.'
    }

    if ($PfxPath) {
        if ($PSCmdlet.ShouldProcess('Cert:\LocalMachine\My', "Import-PfxCertificate '$PfxPath'")) {
            $imported = @(Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation 'Cert:\LocalMachine\My' -Password $PfxPassword)
            $leaf = $imported | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
            if (-not $leaf) { throw 'The PFX did not contain a certificate with a private key.' }
            $thumbprint = $leaf.Thumbprint
            Write-Host ("Imported certificate {0} ({1}), valid until {2:yyyy-MM-dd}" -f $thumbprint, $leaf.Subject, $leaf.NotAfter) -ForegroundColor Green
            $summary.Add("Certificate $thumbprint imported into LocalMachine\My")
            if ($leaf.NotAfter -lt (Get-Date).AddDays(30)) { Write-Warning ("The certificate expires on {0:yyyy-MM-dd}." -f $leaf.NotAfter) }
            Grant-PrivateKeyRead -Thumbprint $thumbprint -Account $ServiceAccount
        }
    }
}
finally {
    $PfxPassword = $null
    if ($stoppedByUs) {
        Write-Host "Starting $serviceName ..."
        Start-Service -Name $serviceName -Confirm:$false
        $summary.Add("Service $serviceName started")
    }
    elseif ($service -and -not $NoRestart -and $service.State -ne 'Running') {
        $notes.Add("Service $serviceName was not running before; it was left stopped.")
    }
}

# ---------------------------------------------------------------- 8. summary
Write-Host ''
Write-Host 'Summary' -ForegroundColor Cyan
if ($WhatIfPreference) { Write-Host '  (WhatIf: nothing was changed)' }
foreach ($s in $summary) { Write-Host "  - $s" }
foreach ($n in $notes) { Write-Host "  ! $n" -ForegroundColor Yellow }
Write-Host ''
Write-Host 'NAV' -ForegroundColor Cyan
Write-Host "  DotNet assembly : 'NavMailGraph, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null' (server side, RunOnClient = No)"
Write-Host '  Page 409 "SMTP Mail Setup", group "Microsoft Graph":'
if ($thumbprint) {
    Write-Host  '    Graph Auth Method      = Certificate'
    Write-Host ("    Graph Cert. Thumbprint = {0}" -f $thumbprint)
}
if ($IncludeMsal) {
    Write-Host  '    Graph Token Library    = MSAL (optional; Built-in needs no extra DLLs)'
    Write-Host  '  If MSAL fails with FileLoadException: merge the bindingRedirects from Add-ins\NavMailGraph\Msal\NavMailGraph.Msal.dll.config into Microsoft.Dynamics.Nav.Server.exe.config.'
}
Write-Host '  Then use "Test Graph Connection" and "Send Graph Test Email".'
if ($PfxPath) {
    Write-Host '  If token acquisition reports "Keyset does not exist" / access denied on the key, grant the service account Full Control'
    Write-Host '  via certlm.msc > Personal > Certificates > All Tasks > Manage Private Keys (Read is normally sufficient).'
}
