<#
.SYNOPSIS
    Removes the Exchange Online role assignments and management scope created by Grant-NavMailGraphMailboxScope.ps1.

.DESCRIPTION
    Looks up, by name:
      * the role assignments "<ScopeName> Mail.Send" and "<ScopeName> Mail.ReadWrite" (same naming rule as
        the grant script, scope part shortened to keep the name within 64 characters), and
      * any other 'Application Mail.Send' / 'Application Mail.ReadWrite' assignment whose CustomResourceScope is
        <ScopeName> (a scope cannot be removed while assignments still reference it),
    removes them with Remove-ManagementRoleAssignment and then removes the management scope with
    Remove-ManagementScope.

    With -RemoveServicePrincipal -AppId the Exchange service principal pointer (New-ServicePrincipal) is removed
    too (Remove-ServicePrincipal). This does not touch the Entra app registration; deleting the service principal
    in Entra removes the Exchange pointer and its assignments automatically but leaves management scopes behind.

    Every Exchange cmdlet is called with -ErrorAction Stop (ExchangeOnlineManagement 3.x cmdlets are script-module
    functions and ignore this script's $ErrorActionPreference), and each object is re-read after removal: it is
    reported as removed only when it is really gone, otherwise the script stops.

    Changes are subject to the Exchange permission cache (30 minutes to 2 hours) before real Graph calls fail.

.PARAMETER ScopeName
    Name of the management scope that Grant-NavMailGraphMailboxScope.ps1 created.

.PARAMETER RemoveServicePrincipal
    Also remove the Exchange service principal pointer for -AppId.

.PARAMETER AppId
    Application (client) id; required with -RemoveServicePrincipal.

.PARAMETER UserPrincipalName
    Admin account for Connect-ExchangeOnline (optional).

.PARAMETER SkipConnect
    Use the existing Exchange Online session.

.EXAMPLE
    ./Remove-NavMailGraphMailboxScope.ps1 -ScopeName 'NavMailGraph NAV2016 MailGraph' -WhatIf

    Shows what would be removed.

.EXAMPLE
    ./Remove-NavMailGraphMailboxScope.ps1 -ScopeName 'NAV2016 senders' -RemoveServicePrincipal -AppId 11111111-2222-3333-4444-555555555555 -Confirm:$false
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateLength(1, 64)]
    [string] $ScopeName,

    [switch] $RemoveServicePrincipal,

    [guid] $AppId,

    [string] $UserPrincipalName,

    [switch] $SkipConnect
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Prop {
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Get-AssignmentName {
    # Same rule as in Grant-NavMailGraphMailboxScope.ps1 - keep both in sync.
    param([string] $Scope, [string] $Suffix)
    $tail = ' ' + $Suffix
    $max = 64 - $tail.Length
    $head = if ($Scope.Length -gt $max) { $Scope.Substring(0, $max).TrimEnd() } else { $Scope }
    return $head + $tail
}

if ($RemoveServicePrincipal -and -not $AppId) { throw '-RemoveServicePrincipal requires -AppId.' }

if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    throw "Module 'ExchangeOnlineManagement' not found. Install it with: Install-Module ExchangeOnlineManagement -Scope CurrentUser"
}
Import-Module ExchangeOnlineManagement -ErrorAction Stop

if (-not $SkipConnect) {
    $connect = @{ ShowBanner = $false; ErrorAction = 'Stop' }
    if ($UserPrincipalName) { $connect.UserPrincipalName = $UserPrincipalName }
    Write-Host 'Connecting to Exchange Online ...'
    Connect-ExchangeOnline @connect
}
if (-not (Get-Command Remove-ManagementRoleAssignment -ErrorAction SilentlyContinue)) {
    throw 'Exchange Online cmdlets are not available. Connect with Connect-ExchangeOnline or run without -SkipConnect.'
}

$removed = @()
$roles = @('Application Mail.Send', 'Application Mail.ReadWrite')
foreach ($role in $roles) {
    $name = Get-AssignmentName -Scope $ScopeName -Suffix ($role -replace '^Application ', '')
    $hits = @(Get-ManagementRoleAssignment -Role $role -ErrorAction Stop | Where-Object {
            [string](Get-Prop $_ 'Name') -eq $name -or [string](Get-Prop $_ 'CustomResourceScope') -eq $ScopeName
        })
    if ($hits.Count -eq 0) { Write-Host ("No '{0}' assignment for scope '{1}' found" -f $role, $ScopeName) }
    foreach ($a in $hits) {
        $aName = [string](Get-Prop $a 'Name')
        $assignee = [string](Get-Prop $a 'RoleAssigneeName')
        if (-not $assignee) { $assignee = [string](Get-Prop $a 'RoleAssignee') }
        if ($PSCmdlet.ShouldProcess("$aName ($role, assignee $assignee)", 'Remove-ManagementRoleAssignment')) {
            Remove-ManagementRoleAssignment -Identity $aName -Confirm:$false -ErrorAction Stop
            $left = @(Get-ManagementRoleAssignment -Role $role -ErrorAction Stop | Where-Object { [string](Get-Prop $_ 'Name') -eq $aName })
            if ($left.Count -gt 0) { throw "Remove-ManagementRoleAssignment returned without error, but '$aName' still exists. The app keeps this access." }
            Write-Host ("Removed role assignment '{0}'" -f $aName) -ForegroundColor Green
            $removed += "RoleAssignment:$aName"
        }
    }
}

function Get-NamedScope {
    @(Get-ManagementScope -ErrorAction Stop | Where-Object { [string](Get-Prop $_ 'Name') -eq $ScopeName }) | Select-Object -First 1
}
$scope = Get-NamedScope
if (-not $scope) {
    Write-Host ("Management scope '{0}' not found" -f $ScopeName)
}
elseif ($PSCmdlet.ShouldProcess("$ScopeName (filter: $(Get-Prop $scope 'RecipientFilter'))", 'Remove-ManagementScope')) {
    Remove-ManagementScope -Identity $ScopeName -Confirm:$false -ErrorAction Stop
    if (Get-NamedScope) { throw "Remove-ManagementScope returned without error, but scope '$ScopeName' still exists." }
    Write-Host ("Removed management scope '{0}'" -f $ScopeName) -ForegroundColor Green
    $removed += "ManagementScope:$ScopeName"
}

if ($RemoveServicePrincipal) {
    function Get-ExoServicePrincipal {
        @(Get-ServicePrincipal -ErrorAction Stop | Where-Object { [string](Get-Prop $_ 'AppId') -eq $AppId.ToString() }) | Select-Object -First 1
    }
    $sp = Get-ExoServicePrincipal
    if (-not $sp) {
        Write-Host ("No Exchange service principal for AppId {0} found" -f $AppId)
    }
    else {
        $spIds = @(@([string](Get-Prop $sp 'Identity'), [string](Get-Prop $sp 'DisplayName'), $AppId.ToString()) | Where-Object { $_ })
        $remaining = @(Get-ManagementRoleAssignment -ErrorAction Stop | Where-Object {
                $spIds -contains [string](Get-Prop $_ 'RoleAssignee') -or $spIds -contains [string](Get-Prop $_ 'RoleAssigneeName')
            })
        if ($remaining.Count -gt 0) {
            Write-Warning ("The service principal still has {0} other role assignment(s): {1}. Removing it removes those too." -f $remaining.Count, (($remaining | ForEach-Object { Get-Prop $_ 'Name' }) -join ', '))
        }
        $spIdentity = [string](Get-Prop $sp 'Identity')
        if (-not $spIdentity) { $spIdentity = $AppId.ToString() }
        if ($PSCmdlet.ShouldProcess("$spIdentity (AppId $AppId)", 'Remove-ServicePrincipal')) {
            Remove-ServicePrincipal -Identity $spIdentity -Confirm:$false -ErrorAction Stop
            if (Get-ExoServicePrincipal) { throw "Remove-ServicePrincipal returned without error, but the Exchange service principal for AppId $AppId still exists." }
            Write-Host ("Removed Exchange service principal for AppId {0}" -f $AppId) -ForegroundColor Green
            $removed += "ServicePrincipal:$AppId"
        }
    }
}

Write-Host ''
if ($removed.Count -gt 0) {
    Write-Host 'Note: Exchange caches app permissions for 30 minutes to 2 hours; the app may keep working during that window.' -ForegroundColor Cyan
    Write-Host 'If the app should not send mail anymore at all, also delete its credentials or the app registration in Entra ID.'
}
$removed
