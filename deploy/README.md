# deploy/ – setup and deployment scripts

PowerShell scripts that take you from "nothing" to "NAV 2016 sends mail through Microsoft Graph":

| # | Script | Runs on | Purpose |
|---|--------|---------|---------|
| 1 | `New-NavMailGraphCertificate.ps1` | admin workstation (PowerShell 7 on macOS/Windows/Linux, or Windows PowerShell 5.1) | self-signed RSA certificate → `.pfx` (private key) + `.cer` (public key) |
| 2 | `New-NavMailGraphApp.ps1` | admin workstation, Microsoft Graph PowerShell SDK | Entra app registration + service principal, uploads the `.cer`; by default **no** Graph permission |
| 3 | `Grant-NavMailGraphMailboxScope.ps1` | admin workstation, ExchangeOnlineManagement | Exchange Online RBAC for Applications: `Application Mail.Send` (+ `Mail.ReadWrite`) **only** for the sender mailbox(es) |
| 4 | `Install-NavMailGraphAddin.ps1` | NAV Server (Windows, elevated, Windows PowerShell 5.1) | copies the add-in, imports the `.pfx`, grants the service account access to the private key, checks .NET/TLS, restarts the instance |
| – | `Remove-NavMailGraphMailboxScope.ps1` | admin workstation | removes the role assignments and scope created by step 3 |

Every script has comment-based help (`Get-Help ./<script>.ps1 -Full`), supports `-WhatIf` where it changes
anything, never writes secrets to disk, and ends with the values to enter on NAV page 409 "SMTP Mail Setup".

## Prerequisites

Versions below are the ones current on 2026-10-12 (see `docs/research-notes.md`).

| Component | Version | Notes |
|-----------|---------|-------|
| PowerShell | 7.6+ on the admin workstation | ExchangeOnlineManagement **3.10.x requires PowerShell 7.6.0+** (.NET 10 dependency); on macOS 14 Sonoma or later as well. macOS 13 Ventura tops out at module 3.9.2 (PowerShell 7.4+). |
| Microsoft.Graph (or just `Microsoft.Graph.Applications` + `Microsoft.Graph.Authentication`) | 2.41.x (tested with 2.40.0) | `Install-Module Microsoft.Graph.Applications -Scope CurrentUser` |
| ExchangeOnlineManagement | 3.10.x (3.9.2 works on PowerShell 7.4) | `Install-Module ExchangeOnlineManagement -Scope CurrentUser`. Connect-IPPSSession is not available on macOS, but it is not needed. |
| Entra role | Application Administrator or Cloud Application Administrator | step 2; tenant-wide variant needs a role that can grant app roles (Privileged Role Administrator / Global Administrator) |
| Exchange role | member of "Organization Management" (or Entra "Exchange Administrator") | step 3 |
| NAV Server | Windows, .NET Framework **4.6.2+** (registry Release ≥ 394802), TLS 1.2 | step 4 checks both; `-FixTls` sets `SchUseStrongCrypto=1` and `SystemDefaultTlsVersions=1` under `HKLM:\SOFTWARE\[WOW6432Node\]Microsoft\.NETFramework\v4.0.30319` |
| Sender mailbox | Exchange Online mailbox; a shared mailbox (no license below 50 GB, sign-in blocked) is recommended | verify with a test send in your tenant (not explicitly documented by Microsoft for app-only sendMail) |

`New-NavMailGraphCertificate.ps1` needs no module. Under Windows PowerShell 5.1 it uses `New-SelfSignedCertificate`
(Windows 10 / Server 2016 or later) instead of `CertificateRequest`.

## Variant A – scoped (recommended)

The app has **no** Microsoft Graph application permission in Entra ID. Exchange Online grants `Application Mail.Send`
only for the mailboxes in a management scope; every other mailbox returns HTTP 403.

```powershell
# 1. certificate (admin workstation; prompts for the pfx password)
./New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs -Subject 'CN=NAV2016 MailGraph Contoso'

# 2. app registration + service principal, no Graph permission
$app = ./New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph' -CertificatePath ./certs/NavMailGraph.cer

# 3. Exchange RBAC: only nav@contoso.com; prove that another mailbox is out of scope
./Grant-NavMailGraphMailboxScope.ps1 -AppId $app.ClientId -ServicePrincipalObjectId $app.ServicePrincipalObjectId `
    -DisplayName $app.DisplayName -Mailbox nav@contoso.com -NegativeTestMailbox ceo@contoso.com
#    add -IncludeReadWrite if NAV must send attachments > ~2.5 MB in total ("Graph Large Messages" = Yes;
#    the sendMail request incl. base64 encoding is limited to 3.5 MiB, so a 3 MB PDF already needs it)

# 4. NAV Server (copy the .pfx and the release folder to the server; elevated Windows PowerShell 5.1)
.\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls -WhatIf
.\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls
#    C/SIDE development machine: add -RoleTailoredClient
```

Then delete the `.pfx` copies you no longer need (keep one in a password manager / vault for renewal).

**Exchange permission cache:** role assignment changes take **30 minutes to 2 hours** to become effective for real
Graph calls. `Test-ServicePrincipalAuthorization` (run by step 3) bypasses the cache, so it can already say
`InScope True` while NAV still gets 403. Wait before debugging.

**Do not combine** the scoped grant with a tenant-wide Entra `Mail.Send` grant: Entra and Exchange permissions are a
union, so the scope would have no effect. Step 3 checks this automatically when a Microsoft Graph PowerShell session
is connected (`Get-MgContext`) to the same tenant as the Exchange Online session (a different tenant stops the
script); otherwise, or if the check itself fails (e.g. the Graph session lacks `Application.Read.All`), it prints how to
check manually and returns `TenantWideGraphGrant = $null` (not verified).

Step 3 is idempotent but strict: an existing assignment named `<ScopeName> Mail.Send` / `<ScopeName> Mail.ReadWrite`
with another scope or **no** scope (organization-wide) stops the script. Running step 3 without `-IncludeReadWrite`
after an earlier run with it does **not** remove `Mail.ReadWrite`; the script warns and lists the assignment in its
output. Remove it with `Remove-ManagementRoleAssignment` (command shown in the warning).

A group of senders instead of single mailboxes:

```powershell
$dn = (Get-DistributionGroup 'NAV Senders').DistinguishedName    # or Get-UnifiedGroup / mail-enabled security group
./Grant-NavMailGraphMailboxScope.ps1 -AppId $app.ClientId -ServicePrincipalObjectId $app.ServicePrincipalObjectId `
    -GroupDistinguishedName $dn -TestMailbox nav@contoso.com -NegativeTestMailbox ceo@contoso.com -SkipConnect
```

Only **direct** members are in scope; Entra-only security groups do not work.

Undo step 3 (the scope name is part of the step 3 output, default `NavMailGraph <DisplayName>`):

```powershell
./Remove-NavMailGraphMailboxScope.ps1 -ScopeName 'NavMailGraph NAV2016 MailGraph' -WhatIf
```

## Variant B – tenant-wide (not recommended)

The app may send as **any** mailbox in the tenant. Use only if your security policy explicitly accepts that.

```powershell
./New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs
$app = ./New-NavMailGraphApp.ps1 -CertificatePath ./certs/NavMailGraph.cer -GrantTenantWide -IncludeReadWrite
# no step 3
.\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls
```

`-GrantTenantWide` adds `Mail.Send` (`b633e1c5-b582-4048-a93e-9f11b44c7e96`) and, with `-IncludeReadWrite`,
`Mail.ReadWrite` (`e2a3a72e-5f79-4c64-b1b1-878b674786c9`) to the app and grants them with
`New-MgServicePrincipalAppRoleAssignment` (no separate admin-consent click needed). Switching to variant A later:
remove these grants in Entra (Enterprise applications → app → Permissions) and run step 3.

## Client secret instead of a certificate (tests only)

```powershell
./New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph TEST' -CreateSecret -SecretValidityMonths 3
```

The secret value is printed **once** and never stored. Enter it in NAV (page 409, masked field); NAV keeps it encrypted
in table 1261 "Service Password". Microsoft recommends certificates over secrets and secret lifetimes below 12 months.

## What goes into NAV (page 409 "SMTP Mail Setup", group "Microsoft Graph")

| NAV field | Value | Printed by |
|-----------|-------|------------|
| Mail Transport | Microsoft Graph | – |
| Graph Tenant ID | `TenantId` | step 2 |
| Graph Client ID | `ClientId` (application id) | step 2 |
| Graph Auth Method | Certificate (or Client Secret) | step 2 |
| Graph Cert. Thumbprint | `Thumbprint` (certificate in `LocalMachine\My` on the NAV server) | steps 1, 2, 4 |
| Graph client secret (masked) | secret value (secret variant only) | step 2 |
| Graph Sender Mailbox | one of the scoped mailboxes, e.g. `nav@contoso.com` | step 3 |
| Graph Large Messages | Yes only if `Application Mail.ReadWrite` (variant A, `-IncludeReadWrite`) or Graph `Mail.ReadWrite` (variant B) is granted | steps 2/3 |
| Graph Token Library | Built-in (default); MSAL only if installed with `-IncludeMsal` | step 4 |

Then use the page actions "Test Graph Connection" and "Send Graph Test Email".

## Notes and limits

* **Certificate lifetime:** Microsoft Graph documents a maximum of one year for an app keyCredential; step 1 therefore
  limits `-ValidityMonths` to 12 (and ends one day early). Put the renewal date in your calendar. To renew: run step 1
  again, add the new `.cer` to the app (Entra admin center → App registrations → Certificates & secrets → Upload, or
  `Update-MgApplication` – note that a PATCH of keyCredentials *replaces* all existing ones), install the new `.pfx`
  with step 4, change the thumbprint in NAV, then remove the old certificate.
* **Private key ACL:** step 4 grants **Read** on the key file to the NAV service account. Microsoft's Business Central
  guide grants Full Control; Read is normally sufficient but not confirmed by Microsoft. If token acquisition fails
  with "Keyset does not exist", grant more via `certlm.msc` → Manage Private Keys.
* **Add-ins folder:** the NAV Server scans subfolders of `Add-ins`, so step 4 keeps backups in
  `%ProgramData%\NavMailGraph\Backup`, never under `Add-ins`. A replaced DLL is only picked up after the instance
  restarts. If several instances share the Service folder, stop all of them before running step 4.
* **MSAL flavour:** if `NavMailGraph.Msal` fails with `FileLoadException`, merge the binding redirects from
  `Add-ins\NavMailGraph\Msal\NavMailGraph.Msal.dll.config` into `Microsoft.Dynamics.Nav.Server.exe.config`.
* **Unverified details** (marked as open questions in `docs/research-notes.md`): the exact property names on some
  Exchange Online objects (`Get-ServicePrincipal`, `Get-ManagementRoleAssignment`, `Get-ManagementScope` output) were not
  checked against a live tenant; the scripts read them defensively and only use them for idempotency checks and
  warnings. Please report differences.
* Exchange RBAC background, cmdlet sources and limits: `docs/research-notes.md`, section "Exchange Online RBAC for
  Applications and Entra app registration"; design decisions: `docs/DESIGN.md` section 11.
