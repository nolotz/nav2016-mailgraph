# Exchange Online RBAC for Applications

This page limits the NavMailGraph app to the mailbox(es) NAV sends from. It is the recommended setup
(variant A in [azure-setup.md](azure-setup.md)).

## Why not grant Mail.Send in Entra ID

An application permission such as Graph `Mail.Send`, consented in Entra ID, applies to **every mailbox in the
tenant**. Microsoft's own description of the permission is "Send mail as any user".

Exchange Online **RBAC for Applications** grants the same rights per mailbox scope. Microsoft states that it
"replaces Application Access Policies" and that "these permission grants are independent of the unscoped grants
in Microsoft Entra ID".

The two systems are combined as a **union**:

> the assigned permissions are a union operation on the permissions from Microsoft Entra ID and the permissions
> assigned in Exchange Online RBAC. Each authority can act independently.

Consequence:

| Entra ID grant | Exchange RBAC grant (scoped) | Effective access |
|----------------|------------------------------|------------------|
| none | Mail.Send for `nav@contoso.com` | send as `nav@contoso.com` only |
| Mail.Send (admin consent) | Mail.Send for `nav@contoso.com` | send as **every** mailbox (scope has no effect) |
| Mail.Send (admin consent) | none | send as every mailbox |

So: register the app with **no** Graph mail permission in Entra and grant the rights **only** in Exchange. If
the app already has an Entra grant, remove it (Enterprise applications → app → Permissions), otherwise the
scope is useless.

## Roles used by NavMailGraph

| Exchange application role | Needed for | What it allows inside the scope |
|---------------------------|------------|---------------------------------|
| `Application Mail.Send` | always (`sendMail`, sending a draft) | send mail as the mailbox; save to Sent Items |
| `Application Mail.ReadWrite` | only messages whose attachments total more than about 2.5 MB (the `sendMail` request incl. base64 encoding is limited to 3.5 MiB; NAV *Graph Large Messages* = Yes) | create/read/change/delete messages in the mailbox: draft, attachments, upload session, draft cleanup. **No** sending. |

`Mail.ReadWrite` gives read access to the mail in the scoped mailbox. That is the reason it is optional. If NAV
never sends attachments totalling more than about 2.5 MB, do not assign it and set *Graph Large Messages* = No. NAV then reports
a clear error for a too-large message instead of trying the draft flow. Note that the cut-over is lower than
it looks: base64 adds about 33 %, so a typical scanned PDF of 3 MB already needs `Mail.ReadWrite`.

There is also a combined role `Application Mail Full Access` (Mail.ReadWrite + Mail.Send). NavMailGraph does
not need it; the two separate roles make the intent visible.

## Prerequisites

* Member of the Exchange role group **Organization Management**, or Entra role **Exchange Administrator**.
* PowerShell 7.6+ with **ExchangeOnlineManagement 3.10.x** (3.10 requires PowerShell 7.6; on PowerShell 7.4 use
  3.9.2). Works on macOS, Linux and Windows.
* The app registration and its service principal (Enterprise application) from [azure-setup.md](azure-setup.md):
  * **Application (client) ID** (`AppId`),
  * **service principal object ID** (Enterprise applications → app → Object ID). Not the object ID from the App
    registrations page.
* The sender mailbox. A shared mailbox is recommended: no licence needed below 50 GB, sign-in blocked. Microsoft
  does not explicitly document app-only `sendMail` from an unlicensed shared mailbox; field reports say it works.
  Test it in your tenant.
* No licence requirement for RBAC for Applications is mentioned by Microsoft.

## With the script

```powershell
Install-Module ExchangeOnlineManagement -Scope CurrentUser

./deploy/Grant-NavMailGraphMailboxScope.ps1 -AppId <client id> -ServicePrincipalObjectId <sp object id> `
    -DisplayName 'NAV2016 MailGraph' -Mailbox nav@contoso.com -NegativeTestMailbox ceo@contoso.com
```

Add `-IncludeReadWrite` for `Application Mail.ReadWrite`. Several mailboxes: `-Mailbox nav@contoso.com,
invoices@contoso.com`. Other parameters: `-ScopeName` (default `NavMailGraph <DisplayName>`), `-GroupDistinguishedName`,
`-TestMailbox`, `-UserPrincipalName`, `-SkipConnect`, `-WhatIf`.

The script is idempotent. It runs the manual steps below, tests the scope with
`Test-ServicePrincipalAuthorization` (expects `InScope = True` for the sender and `False` for
`-NegativeTestMailbox`) and warns when the app also has a tenant-wide Entra grant (checked automatically when a
Microsoft Graph PowerShell session is connected). Role assignment names are `<ScopeName> Mail.Send` and
`<ScopeName> Mail.ReadWrite`.

Re-run behaviour worth knowing:

* An existing assignment with the same name but a different or empty scope stops the script. Remove it first.
* A `Mail.ReadWrite` assignment from an earlier run with `-IncludeReadWrite` is reported, not removed. Use
  `Remove-NavMailGraphMailboxScope.ps1` or `Remove-ManagementRoleAssignment` if large messages are no longer needed.
* The Microsoft Graph session used for the tenant-wide grant check must be signed in to the same tenant as Exchange
  Online, otherwise the script stops. `TenantWideGraphGrant = $null` in the output means the check was not run.

## Manual steps

### 1. Connect

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@contoso.com
```

On macOS/Linux, `-CertificateThumbprint` (unattended sign-in) is Windows-only; use interactive sign-in or
`-Certificate` / `-CertificateFilePath` with `-AppID -Organization`.

### 2. Register the service principal in Exchange

```powershell
New-ServicePrincipal -AppId <client id> -ObjectId <sp object id> -DisplayName 'NAV2016 MailGraph'
```

This creates a pointer to the Entra service principal. Microsoft warns: "Don't use the IDs from the App
registrations page." `-ObjectId` is the Enterprise application object ID.

### 3a. Scope: a single mailbox

Filter on a property that identifies exactly one recipient. `ExternalDirectoryObjectId` (the Entra object ID of
the mailbox user) is the most robust choice. Microsoft says not to use `PrimarySmtpAddress` in recipient filters;
use `EmailAddresses` instead.

```powershell
$mbx = Get-Recipient nav@contoso.com
New-ManagementScope -Name 'NavMailGraph NAV2016 MailGraph' `
    -RecipientRestrictionFilter "ExternalDirectoryObjectId -eq '$($mbx.ExternalDirectoryObjectId)'"

# check: must return exactly the sender mailbox
Get-Recipient -RecipientPreviewFilter "ExternalDirectoryObjectId -eq '$($mbx.ExternalDirectoryObjectId)'"
```

Several mailboxes: combine conditions with `-or`. Scope names have at most 64 characters.

### 3b. Scope: a group of mailboxes

```powershell
$dn = (Get-DistributionGroup 'NAV Senders').DistinguishedName   # or Get-UnifiedGroup / mail-enabled security group
New-ManagementScope -Name 'NavMailGraph NAV senders' -RecipientRestrictionFilter "MemberOfGroup -eq '$dn'"
```

Rules from Microsoft:

* Only **direct** members are in scope; nested group members are out of scope.
* Supported: Microsoft 365 groups, mail-enabled security groups, distribution lists.
  Entra-only security groups do **not** work (`MemberOfGroup` takes the distinguished name of a group Exchange
  knows).
* Alternatively `New-ManagementRoleAssignment -RecipientGroupScope <group>` (also direct members only).

A group is convenient (add a mailbox without touching RBAC) but anyone who can change the group membership can
widen the app's reach. A filter on a fixed mailbox is stricter.

Exclusive management scopes do not restrict app access. Use normal scopes.

### 4. Assign the roles

```powershell
New-ManagementRoleAssignment -Name 'NavMailGraph NAV2016 MailGraph Mail.Send' -App <sp object id> `
    -Role 'Application Mail.Send' -CustomResourceScope 'NavMailGraph NAV2016 MailGraph'

# only for attachments > ~2.5 MB in total (sendMail request incl. base64 is limited to 3.5 MiB)
New-ManagementRoleAssignment -Name 'NavMailGraph NAV2016 MailGraph Mail.ReadWrite' -App <sp object id> `
    -Role 'Application Mail.ReadWrite' -CustomResourceScope 'NavMailGraph NAV2016 MailGraph'
```

The cmdlet reference says `-App` takes the service principal ObjectId; the RBAC article also mentions AppId or
display name. The scripts use the ObjectId.

### 5. Test

```powershell
Test-ServicePrincipalAuthorization -Identity <sp object id> -Resource nav@contoso.com   # expect InScope = True
Test-ServicePrincipalAuthorization -Identity <sp object id> -Resource ceo@contoso.com   # expect InScope = False
```

The output has `RoleName`, `GrantedPermissions`, `AllowedResourceScope`, `ScopeType` and `InScope`. Two caveats
from Microsoft:

* The test **bypasses the permission cache**, so it shows the new state immediately while real Graph calls may
  still use the old one (next section).
* It **excludes permissions granted in Entra ID**. `InScope = False` does not prove that the app cannot access
  the mailbox if it also has a tenant-wide Entra grant. Check Entra separately
  (Enterprise applications → app → Permissions must show no Mail.* application permission).

Then send a real test: NAV Page 409 **Send Graph Test Email**. A mailbox outside the scope must fail with
HTTP 403.

## Permission cache: 30 minutes to 2 hours

Microsoft: "Changes to app permissions are subject to cache maintenance that varies between 30 minutes and 2
hours depending on the app's recent usage." An idle app's cache resets after 30 minutes; an active app keeps it
up to 2 hours. Field reports range from "immediately" to 30 minutes.

Practical effect: after creating or changing an assignment, a 403 from Graph within that window is expected. Do
not debug it until the window has passed. The same delay applies when you **remove** access.

## Change, list, remove

```powershell
# list
Get-ManagementRoleAssignment -Role 'Application Mail.Send' | Format-Table Name, RoleAssignee*, CustomResourceScope
Get-ManagementScope 'NavMailGraph NAV2016 MailGraph' | Format-List Name, RecipientFilter

# change the scope of an assignment
Set-ManagementRoleAssignment -Identity 'NavMailGraph NAV2016 MailGraph Mail.Send' -CustomResourceScope 'Other scope'

# remove (assignments first, then the scope)
Remove-ManagementRoleAssignment -Identity 'NavMailGraph NAV2016 MailGraph Mail.Send'
Remove-ManagementRoleAssignment -Identity 'NavMailGraph NAV2016 MailGraph Mail.ReadWrite'
Remove-ManagementScope -Identity 'NavMailGraph NAV2016 MailGraph'
```

`Get-ManagementRoleAssignment` has no `-App` parameter; filter by `-Role` or `-RoleAssignee`. The exact output
property names were not verified against a live tenant.

Or with the script:

```powershell
./deploy/Remove-NavMailGraphMailboxScope.ps1 -ScopeName 'NavMailGraph NAV2016 MailGraph' -WhatIf
./deploy/Remove-NavMailGraphMailboxScope.ps1 -ScopeName 'NavMailGraph NAV2016 MailGraph' -RemoveServicePrincipal -AppId <client id>
```

Deleting the service principal (Enterprise application) in Entra removes the Exchange pointer and its role
assignments automatically, but **leaves the management scopes** behind. Remove them with `Remove-ManagementScope`.

Fastest emergency stop, independent of the cache: disable or delete the app's credentials in Entra
(Certificates & secrets) or disable the Enterprise application (Properties → Enabled for users to sign in = No).
New tokens are then refused. Already issued access tokens stay valid until they expire.

## Limits

* Up to 10,000 applications per organization.
* Applications cannot be members of role groups.
* Application roles cannot be copied or derived.
* RBAC for Applications covers Microsoft Graph and EWS.

## Background: Application Access Policies (legacy)

Before RBAC for Applications, mailbox access of Entra-consented apps was restricted with Application Access
Policies:

```powershell
New-ApplicationAccessPolicy -AppId <client id> -PolicyScopeGroupId nav-senders@contoso.com `
    -AccessRight RestrictAccess -Description 'NAV2016 MailGraph'
Test-ApplicationAccessPolicy -Identity nav@contoso.com -AppId <client id>
```

* `PolicyScopeGroupId` must be a mailbox user, mail user or **mail-enabled security group**; shared mailboxes
  must be put into such a group.
* Up to about 300 policies per tenant; changes take up to 30 minutes.
* Microsoft: "Don't create new App Access Policies as these policies will eventually require migration to Role
  Based Access Control for Applications."
* An Entra grant can only be constrained by an Application Access Policy, not by RBAC for Applications.

NavMailGraph does not use them. If your tenant already has one for this app, migrate: assign the scoped Exchange
roles, remove the Entra consent, then remove the policy.

## Sources

[research-notes.md](research-notes.md), section "Exchange Online RBAC for Applications and Entra app
registration". Main Microsoft article: <https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac>.
