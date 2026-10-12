<#
.SYNOPSIS
    Creates the Microsoft Entra app registration and service principal used by NavMailGraph.

.DESCRIPTION
    Uses the Microsoft Graph PowerShell SDK (module Microsoft.Graph.Applications, tested with 2.40/2.41) to:

      1. Connect-MgGraph with the scope Application.ReadWrite.All
         (plus AppRoleAssignment.ReadWrite.All only when -GrantTenantWide is used).
      2. New-MgApplication (single tenant, SignInAudience AzureADMyOrg), optionally with the public
         certificate from -CertificatePath as keyCredential (AsymmetricX509Cert / Verify).
      3. New-MgServicePrincipal for the app (the "Enterprise application"; its object id is what
         Exchange Online needs).
      4. Optional -CreateSecret: Add-MgApplicationPassword with an end date of at most 12 months.
         The secret value is shown ONCE on the console and is never written to disk or returned.
      5. DEFAULT: NO Microsoft Graph application permission is added or consented. Mailbox access is
         granted afterwards by Grant-NavMailGraphMailboxScope.ps1 through Exchange Online RBAC for
         Applications, scoped to the sender mailbox(es). This is the recommended design.

         With -GrantTenantWide the app additionally gets the Graph application permission Mail.Send
         (b633e1c5-b582-4048-a93e-9f11b44c7e96) and, with -IncludeReadWrite, Mail.ReadWrite
         (e2a3a72e-5f79-4c64-b1b1-878b674786c9) in requiredResourceAccess, and both are granted
         (admin consent equivalent) with New-MgServicePrincipalAppRoleAssignment. This lets the app send
         as ANY mailbox in the tenant, and because Entra and Exchange grants are a union, it also makes
         any Exchange RBAC scoping ineffective.

    Required role of the signed-in admin: Application Administrator or Cloud Application Administrator
    (Privileged Role Administrator or Global Administrator for -GrantTenantWide).

.PARAMETER DisplayName
    Display name of the app registration. Default: NAV2016 MailGraph.

.PARAMETER TenantId
    Tenant id or verified domain passed to Connect-MgGraph. Optional; default is the home tenant of the
    signed-in account.

.PARAMETER CertificatePath
    Path to the public certificate (.cer, DER or PEM) created by New-NavMailGraphCertificate.ps1.
    A .pfx is rejected on purpose: the private key must not leave the NAV server / admin workstation.

.PARAMETER CreateSecret
    Also create a client secret. Certificates are recommended; use secrets only for tests.

.PARAMETER SecretValidityMonths
    Lifetime of the client secret in months (1-12). Default: 6.

.PARAMETER GrantTenantWide
    Add and grant the Graph application permission Mail.Send tenant-wide. NOT recommended; see DESCRIPTION.

.PARAMETER IncludeReadWrite
    With -GrantTenantWide: also add and grant Mail.ReadWrite (needed by the draft/upload flow for
    messages larger than about 3.5 MB). Without -GrantTenantWide this switch is ignored; use
    Grant-NavMailGraphMailboxScope.ps1 -IncludeReadWrite for the scoped design.

.PARAMETER AllowDuplicateName
    Create the app even if an app registration with the same display name already exists.

.PARAMETER SkipConnect
    Use the existing Microsoft Graph connection (Get-MgContext) instead of calling Connect-MgGraph.

.PARAMETER UseDeviceCode
    Sign in with the device code flow (Connect-MgGraph -UseDeviceCode), e.g. on a headless shell.

.EXAMPLE
    ./New-NavMailGraphApp.ps1 -CertificatePath ./certs/NavMailGraph.cer

    Scoped design (recommended): app + service principal + certificate, no Graph permission.
    Continue with Grant-NavMailGraphMailboxScope.ps1.

.EXAMPLE
    ./New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph TEST' -CreateSecret -SecretValidityMonths 3

    Test app with a short-lived client secret.

.EXAMPLE
    ./New-NavMailGraphApp.ps1 -CertificatePath ./certs/NavMailGraph.cer -GrantTenantWide -IncludeReadWrite

    Tenant-wide variant: the app may send as every mailbox in the tenant.

.OUTPUTS
    PSCustomObject with DisplayName, TenantId, ClientId (AppId), ApplicationObjectId,
    ServicePrincipalObjectId, Thumbprint, CertificateExpires, SecretExpires, GraphPermissions.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()]
    [string] $DisplayName = 'NAV2016 MailGraph',

    [string] $TenantId,

    [string] $CertificatePath,

    [switch] $CreateSecret,

    [ValidateRange(1, 12)]
    [int] $SecretValidityMonths = 6,

    [switch] $GrantTenantWide,

    [switch] $IncludeReadWrite,

    [switch] $AllowDuplicateName,

    [switch] $SkipConnect,

    [switch] $UseDeviceCode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Identifiers from the Microsoft Graph permissions reference (see docs/research-notes.md).
$GraphAppId = '00000003-0000-0000-c000-000000000000'
$MailSendRoleId = 'b633e1c5-b582-4048-a93e-9f11b44c7e96'
$MailReadWriteRoleId = 'e2a3a72e-5f79-4c64-b1b1-878b674786c9'

# NOTE: Microsoft.Graph 2.x cmdlets are functions in script modules and do not see this script's
# $ErrorActionPreference; every Graph call therefore passes -ErrorAction Stop explicitly.

# Error texts that indicate Entra ID replication lag (an object created seconds ago is not visible yet).
# Not an exhaustive or documented list; other errors (403, 400, ...) are thrown immediately.
$ReplicationErrorPattern = 'Request_ResourceNotFound|ResourceNotFound|does not exist|NoBackingApplicationObject|does not reference a valid application object'

function Invoke-WithRetry {
    # New applications / service principals need a few seconds to replicate before they can be referenced.
    # Only replication errors are retried, so non-idempotent calls (Add-MgApplicationPassword) are not repeated
    # after a failure that may have been applied server-side. The action must use -ErrorAction Stop.
    param([Parameter(Mandatory)][scriptblock] $Action, [string] $What = 'operation', [int] $Attempts = 6, [int] $DelaySeconds = 5)
    for ($i = 1; ; $i++) {
        try { return & $Action }
        catch {
            if ($i -ge $Attempts -or ([string]$_ + ' ' + $_.Exception.Message) -notmatch $ReplicationErrorPattern) { throw }
            Write-Verbose ("{0} failed (attempt {1}/{2}): {3}; retrying in {4}s" -f $What, $i, $Attempts, $_.Exception.Message, $DelaySeconds)
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

# ---------------------------------------------------------------- prerequisites
if ($IncludeReadWrite -and -not $GrantTenantWide) {
    Write-Warning '-IncludeReadWrite is only used together with -GrantTenantWide. For the scoped design run Grant-NavMailGraphMailboxScope.ps1 -IncludeReadWrite.'
}
if (-not $CertificatePath -and -not $CreateSecret) {
    Write-Warning 'Neither -CertificatePath nor -CreateSecret was given: the app gets no credential. Add one later (Entra admin center > App registrations > Certificates & secrets).'
}

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Applications)) {
    throw "Module 'Microsoft.Graph.Applications' not found. Install it with: Install-Module Microsoft.Graph.Applications -Scope CurrentUser   (or the full Microsoft.Graph module, 2.41.x tested)"
}
Import-Module Microsoft.Graph.Applications -ErrorAction Stop

$certInfo = $null
if ($CertificatePath) {
    $resolved = (Resolve-Path -LiteralPath $CertificatePath).ProviderPath
    if ([System.IO.Path]::GetExtension($resolved) -match '^\.(pfx|p12)$') {
        throw "'$resolved' looks like a PFX file. Pass the public .cer file; the private key stays on the NAV server."
    }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, [System.IO.File]::ReadAllBytes($resolved))
    if ($cert.HasPrivateKey) { throw "'$resolved' contains a private key. Pass the public .cer file only." }
    $lifetime = $cert.NotAfter - $cert.NotBefore
    if ($lifetime.TotalDays -gt 366) {
        Write-Warning ("The certificate is valid for {0:N0} days. Microsoft Graph documents a maximum of one year for keyCredentials; the upload may be rejected." -f $lifetime.TotalDays)
    }
    if ($cert.NotAfter -lt (Get-Date)) { throw "The certificate expired on $($cert.NotAfter)." }
    $certInfo = [pscustomobject]@{
        RawData    = $cert.RawData   # DER bytes of the public certificate
        Thumbprint = $cert.Thumbprint
        Subject    = $cert.Subject
        NotAfter   = $cert.NotAfter.ToUniversalTime()
    }
}

# ---------------------------------------------------------------- connect
$scopes = @('Application.ReadWrite.All')
if ($GrantTenantWide) { $scopes += 'AppRoleAssignment.ReadWrite.All' }

if (-not $SkipConnect) {
    $connect = @{ Scopes = $scopes; NoWelcome = $true; ErrorAction = 'Stop' }
    if ($TenantId) { $connect.TenantId = $TenantId }
    if ($UseDeviceCode) { $connect.UseDeviceCode = $true }
    Write-Host ("Connecting to Microsoft Graph (scopes: {0}) ..." -f ($scopes -join ', '))
    Connect-MgGraph @connect
}
$ctx = Get-MgContext -ErrorAction Stop
if (-not $ctx) { throw 'Not connected to Microsoft Graph. Run without -SkipConnect or call Connect-MgGraph first.' }
$missing = @($scopes | Where-Object { @($ctx.Scopes) -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Warning ("The current Graph session does not list the scope(s) {0}. The next calls may fail with 403." -f ($missing -join ', '))
}
$tenant = $ctx.TenantId
Write-Host ("Tenant {0}, signed in as {1}" -f $tenant, $ctx.Account)

# ---------------------------------------------------------------- app registration
$escapedName = $DisplayName.Replace("'", "''")
$existing = @(Get-MgApplication -Filter "displayName eq '$escapedName'" -All -ErrorAction Stop)
if ($existing.Count -gt 0 -and -not $AllowDuplicateName) {
    throw ("An app registration named '{0}' already exists (AppId {1}). Use a different -DisplayName, delete the old one, or pass -AllowDuplicateName." -f $DisplayName, (($existing | ForEach-Object AppId) -join ', '))
}

$appParams = @{
    DisplayName    = $DisplayName
    SignInAudience = 'AzureADMyOrg'
    Notes          = 'NavMailGraph: Dynamics NAV 2016 Codeunit 400 -> Microsoft Graph sendMail (client credentials). https://github.com/nolotz/nav2016-mailgraph'
}
if ($certInfo) {
    $appParams.KeyCredentials = @(@{
            Type        = 'AsymmetricX509Cert'
            Usage       = 'Verify'
            Key         = $certInfo.RawData
            DisplayName = "$($certInfo.Subject) $($certInfo.Thumbprint)"
        })
}
$grantedRoles = @()
if ($GrantTenantWide) {
    $resourceAccess = @(@{ Id = $MailSendRoleId; Type = 'Role' })
    if ($IncludeReadWrite) { $resourceAccess += @{ Id = $MailReadWriteRoleId; Type = 'Role' } }
    $appParams.RequiredResourceAccess = @(@{ ResourceAppId = $GraphAppId; ResourceAccess = $resourceAccess })
}

if (-not $PSCmdlet.ShouldProcess("tenant $tenant", "Create app registration '$DisplayName' and its service principal")) {
    return
}

$app = New-MgApplication @appParams -ErrorAction Stop
if (-not $app -or -not $app.AppId) { throw 'New-MgApplication returned no application object.' }
Write-Host ("Created app registration '{0}' AppId {1} (object id {2})" -f $app.DisplayName, $app.AppId, $app.Id) -ForegroundColor Green

$sp = Invoke-WithRetry -What 'New-MgServicePrincipal' -Action { New-MgServicePrincipal -AppId $app.AppId -ErrorAction Stop }
if (-not $sp -or -not $sp.Id) { throw 'New-MgServicePrincipal returned no service principal object.' }
Write-Host ("Created service principal (Enterprise application) object id {0}" -f $sp.Id) -ForegroundColor Green

# ---------------------------------------------------------------- client secret (optional)
$secretExpires = $null
if ($CreateSecret) {
    $end = (Get-Date).ToUniversalTime().AddMonths($SecretValidityMonths)
    if ($PSCmdlet.ShouldProcess($app.AppId, "Create client secret valid until $($end.ToString('yyyy-MM-dd'))")) {
        $secret = Invoke-WithRetry -What 'Add-MgApplicationPassword' -Action {
            Add-MgApplicationPassword -ApplicationId $app.Id -PasswordCredential @{
                DisplayName   = 'NavMailGraph ' + (Get-Date -Format 'yyyy-MM-dd')
                EndDateTime   = $end
            } -ErrorAction Stop
        }
        $secretExpires = $secret.EndDateTime
        Write-Host ''
        Write-Warning 'The client secret VALUE is shown only now and cannot be retrieved again. Paste it directly into NAV (page 409, Graph client secret field) or a password manager. Do not store it in files, tickets or chat.'
        Write-Host ("  Client secret value : {0}" -f $secret.SecretText) -ForegroundColor Yellow
        Write-Host ("  Secret id (NOT the value): {0}, expires {1:yyyy-MM-dd} UTC" -f $secret.KeyId, $secretExpires)
        Write-Host ''
        $secret = $null
    }
}

# ---------------------------------------------------------------- tenant-wide grant (optional, not recommended)
if ($GrantTenantWide) {
    Write-Warning 'TENANT-WIDE: the app will be able to send mail as ANY mailbox in this tenant. Entra grants and Exchange RBAC grants are combined (union), so this also defeats any Exchange RBAC mailbox scoping. Prefer the scoped design (Grant-NavMailGraphMailboxScope.ps1).'
    $graphSp = @(Get-MgServicePrincipal -Filter "appId eq '$GraphAppId'" -ErrorAction Stop) | Select-Object -First 1
    if (-not $graphSp) { throw 'Microsoft Graph service principal not found in the tenant.' }
    $roles = @(@{ Name = 'Mail.Send'; Id = $MailSendRoleId })
    if ($IncludeReadWrite) { $roles += @{ Name = 'Mail.ReadWrite'; Id = $MailReadWriteRoleId } }
    foreach ($role in $roles) {
        if ($PSCmdlet.ShouldProcess($sp.Id, "Grant Microsoft Graph application permission $($role.Name) (tenant-wide)")) {
            [void](Invoke-WithRetry -What "grant $($role.Name)" -Action {
                    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $graphSp.Id -AppRoleId $role.Id -ErrorAction Stop
                })
            $grantedRoles += $role.Name
            Write-Host ("Granted Microsoft Graph {0} (Application) tenant-wide" -f $role.Name) -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------- result
$result = [pscustomobject]@{
    DisplayName              = $app.DisplayName
    TenantId                 = $tenant
    ClientId                 = $app.AppId
    ApplicationObjectId      = $app.Id
    ServicePrincipalObjectId = $sp.Id
    Thumbprint               = if ($certInfo) { $certInfo.Thumbprint } else { $null }
    CertificateExpires       = if ($certInfo) { $certInfo.NotAfter } else { $null }
    SecretExpires            = $secretExpires
    GraphPermissions         = if ($grantedRoles.Count -gt 0) { ($grantedRoles -join ', ') + ' (tenant-wide)' } else { 'none (use Exchange RBAC)' }
}

$authMethod = if ($certInfo) { 'Certificate' } elseif ($CreateSecret) { 'Client Secret' } else { '<add a credential first>' }
Write-Host ''
Write-Host 'NAV page 409 "SMTP Mail Setup", group "Microsoft Graph"' -ForegroundColor Cyan
Write-Host  '  Mail Transport         = Microsoft Graph'
Write-Host ("  Graph Tenant ID        = {0}" -f $result.TenantId)
Write-Host ("  Graph Client ID        = {0}" -f $result.ClientId)
Write-Host ("  Graph Auth Method      = {0}" -f $authMethod)
if ($certInfo) {
    Write-Host ("  Graph Cert. Thumbprint = {0}   (pfx must be in LocalMachine\My on the NAV server)" -f $certInfo.Thumbprint)
}
if ($CreateSecret) {
    Write-Host  '  Graph client secret    = <the secret value shown above>'
}
Write-Host  '  Graph Sender Mailbox   = <the mailbox you scope in the next step, e.g. nav@contoso.com>'
Write-Host  '  Graph Large Messages   = Yes only if Mail.ReadWrite is granted (Exchange role "Application Mail.ReadWrite")'
Write-Host ''
if (-not $GrantTenantWide) {
    Write-Host 'Next step (Exchange Online RBAC, scoped to the sender mailbox):' -ForegroundColor Cyan
    Write-Host ("  ./Grant-NavMailGraphMailboxScope.ps1 -AppId {0} -ServicePrincipalObjectId {1} -DisplayName '{2}' -Mailbox nav@contoso.com" -f $result.ClientId, $result.ServicePrincipalObjectId, $result.DisplayName)
    Write-Host '  No admin consent for Graph permissions is needed in this design.'
}

$result
