<#
.SYNOPSIS
    Grants the NavMailGraph app "Application Mail.Send" (and optionally Mail.Read / Mail.ReadWrite) in Exchange
    Online, restricted to specific mailboxes (RBAC for Applications).

.DESCRIPTION
    Scoped design (recommended): the app has NO Microsoft Graph application permission in Entra ID; Exchange
    Online grants the application roles only for the mailboxes in a management scope.

    Steps (ExchangeOnlineManagement 3.x; all steps are idempotent):
      1. Connect-ExchangeOnline (unless -SkipConnect).
      2. New-ServicePrincipal -AppId -ObjectId -DisplayName: registers a pointer to the Entra service principal
         in Exchange. Skipped when Get-ServicePrincipal already lists the AppId.
      3. New-ManagementScope -Name -RecipientRestrictionFilter:
           -Mailbox: each mailbox is resolved with Get-Recipient and matched by ExternalDirectoryObjectId
                     (EmailAddresses as fallback); Get-Recipient -RecipientPreviewFilter must return exactly the
                     requested mailboxes.
           -GroupDistinguishedName: "MemberOfGroup -eq '<DN>'" (direct members only; Microsoft 365 groups,
                     mail-enabled security groups and distribution lists; Entra-only security groups do not work).
         An existing scope with the same name and filter is reused; a different filter stops the script.
      4. New-ManagementRoleAssignment -App <service principal object id> -Role 'Application Mail.Send'
         -CustomResourceScope <scope>, plus 'Application Mail.Read' with -IncludeRead (reading mail: list, delta,
         get message, attachments) and 'Application Mail.ReadWrite' with -IncludeReadWrite (needed for the
         draft / upload-session flow used for messages larger than about 3.5 MB, and for reading mail with
         post-processing: mark as read, move, delete, create folder). Existing assignments with the
         same name are reused only if their CustomResourceScope is <ScopeName>; a same-named assignment with
         another or no scope (organization-wide) stops the script. Without -IncludeRead / -IncludeReadWrite an
         existing "<ScopeName> Mail.Read" / "<ScopeName> Mail.ReadWrite" assignment from an earlier run is
         reported (warning) and listed in the output; it is not removed (use Remove-NavMailGraphMailboxScope.ps1
         or Remove-ManagementRoleAssignment).
         Every created object is read back before it is reported as created.
      5. Test-ServicePrincipalAuthorization -Identity <sp> -Resource <mailbox>: expects InScope = True for the
         sender mailbox(es) and InScope = False for -NegativeTestMailbox.
      6. Warns if the app ALSO has a tenant-wide Graph Mail.Send / Mail.Read / Mail.ReadWrite grant in Entra ID (that grant
         and the Exchange grant are combined, so the scoping would have no effect). Checked automatically when a
         Microsoft Graph PowerShell session is connected (Get-MgContext) to the same tenant as the Exchange
         Online session (Get-ConnectionInformation TenantID), otherwise a manual check is printed. If the check
         itself fails (e.g. missing Application.Read.All), the result is "could not verify", never "Good".

    All Exchange Online and Microsoft Graph cmdlets are called with -ErrorAction Stop: both modules expose
    them as script-module functions, which do not inherit this script's $ErrorActionPreference.

    Role required: member of the Exchange "Organization Management" role group (or Entra "Exchange Administrator").

    Permission changes take 30 minutes to 2 hours to become effective for real Graph calls (Exchange permission
    cache). Test-ServicePrincipalAuthorization bypasses that cache. Do not debug HTTP 403 inside that window.

    Assignment names: "<ScopeName> Mail.Send", "<ScopeName> Mail.Read" and "<ScopeName> Mail.ReadWrite" (the
    scope part is shortened so the name stays within 64 characters). Remove-NavMailGraphMailboxScope.ps1 uses the same rule.

.PARAMETER AppId
    Application (client) id of the app registration.

.PARAMETER ServicePrincipalObjectId
    Object id of the Enterprise application (service principal), NOT the object id shown on the App
    registrations page. Optional when a Microsoft Graph PowerShell session is connected (resolved with
    Get-MgServicePrincipal). New-NavMailGraphApp.ps1 prints it as ServicePrincipalObjectId.

.PARAMETER DisplayName
    Display name for the Exchange service principal pointer. Default: NAV2016 MailGraph.

.PARAMETER Mailbox
    One or more sender mailboxes (SMTP address, UPN or other identity accepted by Get-Recipient).
    Shared mailboxes are fine.

.PARAMETER GroupDistinguishedName
    Distinguished name of a mail-enabled group whose DIRECT members are in scope, e.g. from
    (Get-DistributionGroup nav-senders).DistinguishedName or (Get-UnifiedGroup ...).DistinguishedName.

.PARAMETER ScopeName
    Name of the management scope (max 64 characters). Default: "NavMailGraph <DisplayName>" (shortened).

.PARAMETER IncludeRead
    Also assign 'Application Mail.Read': read messages and attachments in the scoped mailboxes (NAV mail import
    with "After Import" = Nothing; read-only). Marking messages as read, moving them to a Processed/Error
    folder, deleting them or creating folders needs Mail.ReadWrite (-IncludeReadWrite) instead; Mail.ReadWrite
    includes reading, so -IncludeRead is then not needed.

.PARAMETER IncludeReadWrite
    Also assign 'Application Mail.ReadWrite' (large attachments / draft flow, NAV field "Graph Large Messages";
    reading mail with mark as read / move / delete).

.PARAMETER TestMailbox
    Group mode: mailbox used for the positive authorization test. Default: the first group member found.

.PARAMETER NegativeTestMailbox
    A mailbox OUTSIDE the scope; the authorization test must return InScope = False for it.

.PARAMETER UserPrincipalName
    Admin account for Connect-ExchangeOnline (optional).

.PARAMETER SkipConnect
    Use the existing Exchange Online session.

.EXAMPLE
    ./Grant-NavMailGraphMailboxScope.ps1 -AppId 11111111-2222-3333-4444-555555555555 `
        -ServicePrincipalObjectId 66666666-7777-8888-9999-000000000000 -Mailbox nav@contoso.com `
        -NegativeTestMailbox ceo@contoso.com

    Allows the app to send only as nav@contoso.com and proves that ceo@contoso.com is out of scope.

.EXAMPLE
    ./Grant-NavMailGraphMailboxScope.ps1 -AppId 1111... -ServicePrincipalObjectId 6666... `
        -Mailbox nav@contoso.com, invoices@contoso.com -IncludeReadWrite -ScopeName 'NAV2016 senders'

.EXAMPLE
    ./Grant-NavMailGraphMailboxScope.ps1 -AppId 1111... -ServicePrincipalObjectId 6666... `
        -Mailbox invoices-in@contoso.com -IncludeRead -NegativeTestMailbox ceo@contoso.com

    Send and read-only import for invoices-in@contoso.com. Use -IncludeReadWrite instead of -IncludeRead if the
    import should mark, move or delete the processed messages.

.EXAMPLE
    $dn = (Get-DistributionGroup 'NAV Senders').DistinguishedName
    ./Grant-NavMailGraphMailboxScope.ps1 -AppId 1111... -ServicePrincipalObjectId 6666... -GroupDistinguishedName $dn -SkipConnect

.OUTPUTS
    PSCustomObject with AppId, ServicePrincipalObjectId, ScopeName, RecipientFilter, RoleAssignments,
    TenantWideGraphGrant ($true / $false / $null = not verified), TestResults.
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Mailbox')]
param(
    [Parameter(Mandatory)]
    [guid] $AppId,

    [guid] $ServicePrincipalObjectId,

    [ValidateNotNullOrEmpty()]
    [string] $DisplayName = 'NAV2016 MailGraph',

    [Parameter(Mandatory, ParameterSetName = 'Mailbox')]
    [ValidateNotNullOrEmpty()]
    [string[]] $Mailbox,

    [Parameter(Mandatory, ParameterSetName = 'Group')]
    [ValidateNotNullOrEmpty()]
    [string] $GroupDistinguishedName,

    [ValidateLength(1, 64)]
    [string] $ScopeName,

    [switch] $IncludeRead,

    [switch] $IncludeReadWrite,

    [Parameter(ParameterSetName = 'Group')]
    [string] $TestMailbox,

    [string] $NegativeTestMailbox,

    [string] $UserPrincipalName,

    [switch] $SkipConnect
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MailSendRoleId = 'b633e1c5-b582-4048-a93e-9f11b44c7e96'
$MailReadWriteRoleId = 'e2a3a72e-5f79-4c64-b1b1-878b674786c9'
$MailReadRoleId = '810c84a8-4a9e-49e6-bf7d-12d183f40d01'   # Graph application permission Mail.Read

# ---------------------------------------------------------------- helpers
function Get-Prop {
    # Safe property read under StrictMode (Exchange REST objects differ slightly between module versions).
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Get-AssignmentName {
    # Same rule as in Remove-NavMailGraphMailboxScope.ps1 - keep both in sync.
    param([string] $Scope, [string] $Suffix)
    $tail = ' ' + $Suffix
    $max = 64 - $tail.Length
    $head = if ($Scope.Length -gt $max) { $Scope.Substring(0, $max).TrimEnd() } else { $Scope }
    return $head + $tail
}

function ConvertTo-OPathLiteral {
    param([string] $Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-NormalizedFilter {
    param([string] $Filter)
    if (-not $Filter) { return '' }
    return ($Filter -replace '[\s\(\)]', '').ToLowerInvariant()
}

function Test-InScope {
    # Runs Test-ServicePrincipalAuthorization and returns one row per role of interest.
    param([string] $Identity, [string] $Resource, [string[]] $Roles, [bool] $Expected)
    $rows = @(Test-ServicePrincipalAuthorization -Identity $Identity -Resource $Resource -ErrorAction Stop)
    foreach ($role in $Roles) {
        $row = $rows | Where-Object { (Get-Prop $_ 'RoleName') -eq $role } | Select-Object -First 1
        $inScope = if ($row) { [string](Get-Prop $row 'InScope') } else { '<no assignment>' }
        $ok = if ($Expected) { $inScope -eq 'True' } else { $inScope -ne 'True' }
        [pscustomobject]@{
            Mailbox  = $Resource
            Role     = $role
            InScope  = $inScope
            Expected = $Expected
            Result   = if ($ok) { 'OK' } else { 'UNEXPECTED' }
        }
    }
}

# ---------------------------------------------------------------- prerequisites
if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    throw "Module 'ExchangeOnlineManagement' not found. Install it with: Install-Module ExchangeOnlineManagement -Scope CurrentUser (3.x; 3.10.x needs PowerShell 7.6+)"
}
Import-Module ExchangeOnlineManagement -ErrorAction Stop

if (-not $ScopeName) {
    $ScopeName = 'NavMailGraph ' + $DisplayName
    if ($ScopeName.Length -gt 64) { $ScopeName = $ScopeName.Substring(0, 64).TrimEnd() }
}

# NOTE: Microsoft.Graph 2.x and ExchangeOnlineManagement 3.x cmdlets are functions in script modules; they
# do not see this script's $ErrorActionPreference, so every call below passes -ErrorAction Stop explicitly.
$mgCtx = $null
if (Get-Command Get-MgContext -ErrorAction SilentlyContinue) {
    $mgCtx = Get-MgContext -ErrorAction Stop
}
$graphConnected = [bool]$mgCtx
if (-not $graphConnected -and -not $ServicePrincipalObjectId) {
    throw '-ServicePrincipalObjectId is required (Entra admin center > Enterprise applications > your app > Object ID), unless a Microsoft Graph PowerShell session is connected.'
}

$roles = @('Application Mail.Send')
if ($IncludeRead) { $roles += 'Application Mail.Read' }
if ($IncludeReadWrite) { $roles += 'Application Mail.ReadWrite' }

# ---------------------------------------------------------------- connect
if (-not $SkipConnect) {
    $connect = @{ ShowBanner = $false; ErrorAction = 'Stop' }
    if ($UserPrincipalName) { $connect.UserPrincipalName = $UserPrincipalName }
    Write-Host 'Connecting to Exchange Online ...'
    Connect-ExchangeOnline @connect
}
if (-not (Get-Command New-ManagementRoleAssignment -ErrorAction SilentlyContinue)) {
    throw 'Exchange Online cmdlets are not available. Connect with Connect-ExchangeOnline (Organization Management role required) or run without -SkipConnect.'
}

# ---------------------------------------------------------------- Graph session must target the same tenant
if ($graphConnected) {
    $graphTenant = [string]$mgCtx.TenantId
    $exoTenants = @(Get-ConnectionInformation -ErrorAction Stop |
            Where-Object { [string](Get-Prop $_ 'State') -eq 'Connected' } |
            ForEach-Object { [string](Get-Prop $_ 'TenantID') } |
            Where-Object { $_ } | Select-Object -Unique)
    if ($exoTenants.Count -eq 0) {
        Write-Warning 'Could not read the tenant id of the Exchange Online session (Get-ConnectionInformation). The Microsoft Graph session is NOT used; the Entra ID check must be done manually.'
        $graphConnected = $false
    }
    elseif (@($exoTenants | Where-Object { $_ -ne $graphTenant }).Count -gt 0) {
        throw ("The Microsoft Graph session is connected to tenant {0}, but Exchange Online to tenant {1}. Run Connect-MgGraph -TenantId <Exchange tenant id> -Scopes Application.Read.All, or Disconnect-MgGraph and pass -ServicePrincipalObjectId." -f $graphTenant, ($exoTenants -join ', '))
    }
    else {
        Write-Host ("Microsoft Graph and Exchange Online sessions use the same tenant {0}" -f $graphTenant)
    }
}

if ($graphConnected -and (Get-Command Get-MgServicePrincipal -ErrorAction SilentlyContinue)) {
    try {
        $mgSp = @(Get-MgServicePrincipal -Filter "appId eq '$AppId'" -ErrorAction Stop)
    }
    catch {
        throw ("Could not look up the service principal of AppId {0} in Microsoft Graph: {1}`nThe Graph session needs Application.Read.All (Connect-MgGraph -Scopes Application.Read.All), or run Disconnect-MgGraph and pass -ServicePrincipalObjectId." -f $AppId, $_.Exception.Message)
    }
    if ($mgSp.Count -eq 0) { throw "No service principal (Enterprise application) with AppId $AppId found in Entra ID. Run New-NavMailGraphApp.ps1 first." }
    $mgSp = $mgSp[0]
    if (-not $ServicePrincipalObjectId) {
        $ServicePrincipalObjectId = [guid]$mgSp.Id
        Write-Host ("Resolved service principal object id {0} via Microsoft Graph" -f $ServicePrincipalObjectId)
    }
    elseif ([guid]$mgSp.Id -ne $ServicePrincipalObjectId) {
        throw ("-ServicePrincipalObjectId {0} does not match the Enterprise application object id {1} of AppId {2}. Do not use the object id from the App registrations page." -f $ServicePrincipalObjectId, $mgSp.Id, $AppId)
    }
}
if (-not $ServicePrincipalObjectId) {
    throw '-ServicePrincipalObjectId is required (Entra admin center > Enterprise applications > your app > Object ID), unless a Microsoft Graph PowerShell session is connected.'
}
$spId = $ServicePrincipalObjectId.ToString()

# ---------------------------------------------------------------- 1. service principal pointer
function Get-ExoServicePrincipal {
    @(Get-ServicePrincipal -ErrorAction Stop | Where-Object { [string](Get-Prop $_ 'AppId') -eq $AppId.ToString() }) | Select-Object -First 1
}
$exoSp = Get-ExoServicePrincipal
if ($exoSp) {
    Write-Host ("Exchange service principal for AppId {0} already exists ('{1}')" -f $AppId, (Get-Prop $exoSp 'DisplayName'))
    $exoSpObjectId = [string](Get-Prop $exoSp 'ObjectId')
    if ($exoSpObjectId -and $exoSpObjectId -ne $spId) {
        Write-Warning ("The existing Exchange service principal points to object id {0}, not {1}. Check that you use the Enterprise application object id." -f $exoSpObjectId, $spId)
    }
}
elseif ($PSCmdlet.ShouldProcess("AppId $AppId", "New-ServicePrincipal -ObjectId $spId -DisplayName '$DisplayName'")) {
    [void](New-ServicePrincipal -AppId $AppId.ToString() -ObjectId $spId -DisplayName $DisplayName -ErrorAction Stop)
    $exoSp = Get-ExoServicePrincipal
    if (-not $exoSp) { throw "New-ServicePrincipal returned without error, but Get-ServicePrincipal does not list AppId $AppId." }
    Write-Host ("Created Exchange service principal '{0}'" -f $DisplayName) -ForegroundColor Green
}

# ---------------------------------------------------------------- 2. recipient filter
$expected = @()
if ($PSCmdlet.ParameterSetName -eq 'Mailbox') {
    $clauses = @()
    foreach ($m in $Mailbox) {
        $r = @(Get-Recipient -Identity $m -ErrorAction Stop)
        if ($r.Count -ne 1) { throw "Mailbox '$m' resolved to $($r.Count) recipients; use a unique identity (SMTP address)." }
        $r = $r[0]
        $type = [string](Get-Prop $r 'RecipientTypeDetails')
        if ([string](Get-Prop $r 'RecipientType') -ne 'UserMailbox') {
            Write-Warning ("'{0}' is a {1}, not a mailbox; Graph sendMail needs an Exchange Online mailbox." -f $m, $type)
        }
        $primary = [string](Get-Prop $r 'PrimarySmtpAddress')
        $extId = [string](Get-Prop $r 'ExternalDirectoryObjectId')
        if ($extId) {
            $clauses += "ExternalDirectoryObjectId -eq $(ConvertTo-OPathLiteral $extId)"
        }
        else {
            # Microsoft: use EmailAddresses, not PrimarySmtpAddress, in recipient filters.
            $clauses += "EmailAddresses -eq $(ConvertTo-OPathLiteral ('SMTP:' + $primary))"
        }
        $expected += [pscustomobject]@{ Input = $m; PrimarySmtpAddress = $primary; Type = $type; ExternalDirectoryObjectId = $extId }
        Write-Host ("Resolved {0} -> {1} ({2}, ExternalDirectoryObjectId {3})" -f $m, $primary, $type, $extId)
    }
    $filter = if ($clauses.Count -eq 1) { $clauses[0] } else { ($clauses | ForEach-Object { "($_)" }) -join ' -or ' }
}
else {
    $g = @(Get-Recipient -Identity $GroupDistinguishedName -ErrorAction Stop)
    if ($g.Count -ne 1) { throw "Group '$GroupDistinguishedName' resolved to $($g.Count) recipients." }
    $dn = [string](Get-Prop $g[0] 'DistinguishedName')
    if (-not $dn) { $dn = $GroupDistinguishedName }
    Write-Host ("Group: {0} ({1})" -f (Get-Prop $g[0] 'DisplayName'), (Get-Prop $g[0] 'RecipientTypeDetails'))
    Write-Warning 'Only DIRECT members of the group are in scope (nested groups are not expanded).'
    $filter = "MemberOfGroup -eq $(ConvertTo-OPathLiteral $dn)"
}
Write-Host ("Recipient filter: {0}" -f $filter)

# verify what the filter matches
$preview = @(Get-Recipient -RecipientPreviewFilter $filter -ResultSize Unlimited -ErrorAction Stop)
Write-Host ("The filter matches {0} recipient(s):" -f $preview.Count)
$preview | Select-Object -First 25 | ForEach-Object { Write-Host ("  {0} <{1}> {2}" -f (Get-Prop $_ 'DisplayName'), (Get-Prop $_ 'PrimarySmtpAddress'), (Get-Prop $_ 'RecipientTypeDetails')) }
if ($preview.Count -gt 25) { Write-Host '  ...' }
if ($PSCmdlet.ParameterSetName -eq 'Mailbox') {
    $previewAddresses = @($preview | ForEach-Object { [string](Get-Prop $_ 'PrimarySmtpAddress') })
    foreach ($e in $expected) {
        $hits = @($previewAddresses | Where-Object { $_ -eq $e.PrimarySmtpAddress })
        if ($hits.Count -ne 1) { throw "The filter matches '$($e.PrimarySmtpAddress)' $($hits.Count) time(s), expected exactly once. Scope NOT created." }
    }
    if ($preview.Count -ne $expected.Count) {
        throw "The filter matches $($preview.Count) recipients but $($expected.Count) mailbox(es) were requested. Scope NOT created."
    }
}
elseif ($preview.Count -eq 0) {
    Write-Warning 'The group currently has no members that match; the app cannot send as anyone until members are added.'
}

# ---------------------------------------------------------------- 3. management scope
function Get-NamedScope {
    @(Get-ManagementScope -ErrorAction Stop | Where-Object { [string](Get-Prop $_ 'Name') -eq $ScopeName }) | Select-Object -First 1
}
$scope = Get-NamedScope
if ($scope) {
    $existingFilter = [string](Get-Prop $scope 'RecipientFilter')
    if ((Get-NormalizedFilter $existingFilter) -ne (Get-NormalizedFilter $filter)) {
        throw ("Management scope '{0}' already exists with a different filter:`n  existing: {1}`n  wanted:   {2}`nUse another -ScopeName, or remove the old scope with Remove-NavMailGraphMailboxScope.ps1 -ScopeName '{0}'." -f $ScopeName, $existingFilter, $filter)
    }
    Write-Host ("Management scope '{0}' already exists with the same filter" -f $ScopeName)
}
elseif ($PSCmdlet.ShouldProcess($ScopeName, "New-ManagementScope -RecipientRestrictionFilter `"$filter`"")) {
    [void](New-ManagementScope -Name $ScopeName -RecipientRestrictionFilter $filter -ErrorAction Stop)
    if (-not (Get-NamedScope)) { throw "New-ManagementScope returned without error, but Get-ManagementScope does not list '$ScopeName'." }
    Write-Host ("Created management scope '{0}'" -f $ScopeName) -ForegroundColor Green
}

# ---------------------------------------------------------------- 4. role assignments
function Get-NamedAssignment {
    param([string] $Role, [string] $Name)
    $all = @(Get-ManagementRoleAssignment -Role $Role -ErrorAction Stop)
    [pscustomobject]@{
        All  = $all
        Mine = $all | Where-Object { [string](Get-Prop $_ 'Name') -eq $Name } | Select-Object -First 1
    }
}

$assignmentNames = @()
$testRoles = @($roles)
$readWriteLeftOver = $false
$readLeftOver = $false
$ids = @($spId, $AppId.ToString(), $DisplayName)
if ($exoSp) { $ids += [string](Get-Prop $exoSp 'Identity'); $ids += [string](Get-Prop $exoSp 'Name') }
$ids = @($ids | Where-Object { $_ })

# Mail.Read and Mail.ReadWrite are always inspected: without -IncludeRead / -IncludeReadWrite they are only reported
# (left over from an earlier run).
foreach ($role in @('Application Mail.Send', 'Application Mail.Read', 'Application Mail.ReadWrite')) {
    $wanted = $roles -contains $role
    $name = Get-AssignmentName -Scope $ScopeName -Suffix ($role -replace '^Application ', '')
    $found = Get-NamedAssignment -Role $role -Name $name
    $mine = $found.Mine

    # Warn about other assignments of the same role to this app (e.g. created manually), especially unscoped ones.
    $others = @($found.All | Where-Object {
            [string](Get-Prop $_ 'Name') -ne $name -and (
                $ids -contains [string](Get-Prop $_ 'RoleAssignee') -or $ids -contains [string](Get-Prop $_ 'RoleAssigneeName'))
        })
    foreach ($o in $others) {
        $oScope = [string](Get-Prop $o 'CustomResourceScope')
        if (-not $oScope) { $oScope = '<none: ORGANIZATION-WIDE>' }
        Write-Warning ("This app also has the role assignment '{0}' ({1}) with scope {2}. Assignments are combined; remove it if it is broader than intended." -f (Get-Prop $o 'Name'), $role, $oScope)
    }

    if ($mine) {
        # An empty CustomResourceScope means the assignment applies to the whole organization.
        $mineScope = [string](Get-Prop $mine 'CustomResourceScope')
        if ($mineScope -ne $ScopeName) {
            $shown = if ($mineScope) { "scope '$mineScope'" } else { 'NO scope (ORGANIZATION-WIDE)' }
            throw ("Role assignment '{0}' ({1}) already exists with {2} instead of '{3}'. Remove it (Remove-ManagementRoleAssignment -Identity '{0}', or Remove-NavMailGraphMailboxScope.ps1 -ScopeName '{3}') or use another -ScopeName." -f $name, $role, $shown, $ScopeName)
        }
        $assignmentNames += $name
        if ($wanted) {
            Write-Host ("Role assignment '{0}' already exists" -f $name)
        }
        else {
            if ($role -eq 'Application Mail.ReadWrite') { $readWriteLeftOver = $true; $switchName = '-IncludeReadWrite' }
            else { $readLeftOver = $true; $switchName = '-IncludeRead' }
            $testRoles += $role
            Write-Warning ("Role assignment '{0}' ({1}) from an earlier run still exists although {2} was not given; the app keeps {3} on the scoped mailboxes. Remove it with: Remove-ManagementRoleAssignment -Identity '{0}' -Confirm:`$false" -f $name, $role, $switchName, ($role -replace '^Application ', ''))
        }
    }
    elseif (-not $wanted) {
        continue
    }
    elseif ($PSCmdlet.ShouldProcess($name, "New-ManagementRoleAssignment -App $spId -Role '$role' -CustomResourceScope '$ScopeName'")) {
        [void](New-ManagementRoleAssignment -Name $name -App $spId -Role $role -CustomResourceScope $ScopeName -ErrorAction Stop)
        $check = (Get-NamedAssignment -Role $role -Name $name).Mine
        if (-not $check) { throw "New-ManagementRoleAssignment returned without error, but Get-ManagementRoleAssignment does not list '$name'." }
        if ([string](Get-Prop $check 'CustomResourceScope') -ne $ScopeName) {
            throw ("Role assignment '{0}' was created but reports CustomResourceScope '{1}' instead of '{2}'. Remove it and investigate." -f $name, (Get-Prop $check 'CustomResourceScope'), $ScopeName)
        }
        $assignmentNames += $name
        Write-Host ("Created role assignment '{0}' ({1})" -f $name, $role) -ForegroundColor Green
    }
    else {
        $assignmentNames += $name   # WhatIf: would be created
    }
}
if (-not $IncludeReadWrite -and -not $readWriteLeftOver) {
    Write-Host "Info: without 'Application Mail.ReadWrite' set NAV 'Graph Large Messages' = No; messages above ~3.5 MB are then rejected with a clear error."
    if ($IncludeRead -or $readLeftOver) {
        Write-Host "Info: 'Application Mail.Read' allows reading only. A mail import that marks messages as read, moves them (Processed/Error folder), deletes them or creates folders needs 'Application Mail.ReadWrite' (-IncludeReadWrite)."
    }
}
elseif ($IncludeRead -and $IncludeReadWrite) {
    Write-Host "Info: 'Application Mail.ReadWrite' already includes reading; 'Application Mail.Read' is not needed in addition (it does no harm)."
}

# ---------------------------------------------------------------- 5. authorization tests
$tests = @()
if ($WhatIfPreference) {
    Write-Host 'WhatIf: authorization tests skipped.'
}
else {
    $positive = @()
    if ($PSCmdlet.ParameterSetName -eq 'Mailbox') { $positive = @($expected | ForEach-Object PrimarySmtpAddress) }
    elseif ($TestMailbox) { $positive = @($TestMailbox) }
    elseif ($preview.Count -gt 0) { $positive = @([string](Get-Prop $preview[0] 'PrimarySmtpAddress')) }

    foreach ($p in $positive) { $tests += Test-InScope -Identity $spId -Resource $p -Roles $testRoles -Expected $true }
    if ($NegativeTestMailbox) { $tests += Test-InScope -Identity $spId -Resource $NegativeTestMailbox -Roles $testRoles -Expected $false }

    if ($tests.Count -gt 0) {
        Write-Host ''
        Write-Host 'Test-ServicePrincipalAuthorization (bypasses the permission cache):' -ForegroundColor Cyan
        $tests | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
        if (@($tests | Where-Object Result -ne 'OK').Count -gt 0) {
            Write-Warning 'At least one authorization test did not return the expected result. Check the scope filter and the role assignments above.'
        }
    }
    if (-not $NegativeTestMailbox) {
        Write-Host 'Tip: pass -NegativeTestMailbox <a mailbox outside the scope> to prove the restriction (expect InScope False).'
    }
}

# ---------------------------------------------------------------- 6. tenant-wide Entra grant check
Write-Host ''
$tenantWide = $null   # $null = not verified
$checked = $false
if ($graphConnected -and (Get-Command Get-MgServicePrincipalAppRoleAssignment -ErrorAction SilentlyContinue)) {
    try {
        $grants = @(Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $spId -All -ErrorAction Stop |
                Where-Object { @($MailSendRoleId, $MailReadRoleId, $MailReadWriteRoleId) -contains [string]$_.AppRoleId })
        $checked = $true
    }
    catch {
        Write-Warning ("Entra ID check: could NOT verify whether the app has a tenant-wide Graph Mail.Send / Mail.Read / Mail.ReadWrite grant: {0}" -f $_.Exception.Message)
    }
    if ($checked) {
        $tenantWide = $grants.Count -gt 0
        if ($tenantWide) {
            Write-Warning ("The app ALSO has tenant-wide Microsoft Graph application permission(s) in Entra ID ({0}). Entra and Exchange grants are combined, so the mailbox scope has NO effect. Remove the Graph Mail.* application permission/consent from the app (Entra admin center > Enterprise applications > app > Permissions) or with Remove-MgServicePrincipalAppRoleAssignment." -f (($grants | ForEach-Object { switch ([string]$_.AppRoleId) { $MailSendRoleId { 'Mail.Send' } $MailReadRoleId { 'Mail.Read' } default { 'Mail.ReadWrite' } } }) -join ', '))
        }
        else {
            Write-Host 'Entra ID check: no tenant-wide Graph Mail.Send / Mail.Read / Mail.ReadWrite grant on the service principal. Good.' -ForegroundColor Green
        }
    }
}
if (-not $checked) {
    Write-Host 'Manual check required (tenant-wide grant NOT verified): Entra admin center > Enterprise applications >' -ForegroundColor Yellow
    Write-Host ("  '{0}' > Permissions must NOT list Microsoft Graph 'Mail.Send', 'Mail.Read' or 'Mail.ReadWrite' (Application) with admin consent." -f $DisplayName) -ForegroundColor Yellow
    Write-Host ("  Or: Connect-MgGraph -TenantId <tenant> -Scopes Application.Read.All; Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId {0}" -f $spId) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Note: Exchange caches app permissions for 30 minutes to 2 hours. Real sendMail calls can return 403 during that window even though the test above says InScope True.' -ForegroundColor Cyan
Write-Host 'Then in NAV (page 409): Graph Sender Mailbox = one of the scoped mailboxes; use "Send Graph Test Email".'

[pscustomobject]@{
    AppId                    = $AppId.ToString()
    ServicePrincipalObjectId = $spId
    ScopeName                = $ScopeName
    RecipientFilter          = $filter
    RoleAssignments          = $assignmentNames
    TenantWideGraphGrant     = $tenantWide
    TestResults              = $tests
}
