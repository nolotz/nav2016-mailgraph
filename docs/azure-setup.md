# Entra app registration

NavMailGraph signs in to Microsoft Entra ID as an **application** (OAuth 2.0 client credentials) and calls
Microsoft Graph with an app-only token. This page creates that app registration.

Two design variants:

| Variant | Graph permission in Entra | Mailbox access granted by | Effect |
|---------|---------------------------|---------------------------|--------|
| **A – scoped (recommended)** | none | Exchange Online RBAC for Applications ([exchange-rbac.md](exchange-rbac.md)) | App can send only as the scoped mailbox(es) |
| B – tenant-wide | `Mail.Send` (+ `Mail.ReadWrite`) with admin consent | Entra ID | App can send as **every** mailbox in the tenant |

This page covers the app registration itself. For variant A you then continue with
[exchange-rbac.md](exchange-rbac.md). Do not combine A and B: the grants add up and the scope has no effect.

## What you need

* Entra role **Application Administrator** or **Cloud Application Administrator**
  (variant B: a role that can grant admin consent, e.g. Privileged Role Administrator or Global Administrator).
* A certificate (recommended) or a client secret, see [below](#certificate-or-client-secret).

## Certificate or client secret

| | Certificate | Client secret |
|---|---|---|
| Microsoft's guidance | Preferred over secrets ("Don't use password credentials, also known as secrets") | "should not be used in production environments" |
| Maximum lifetime | 1 year per the Graph how-to for app key credentials (`New-NavMailGraphCertificate.ps1` caps at 12 months) | 24 months (portal); Microsoft recommends **less than 12 months**. `New-NavMailGraphApp.ps1` allows 1–12 months, default 6. |
| Where the secret part lives | Private key created on the admin workstation as a password-protected `.pfx`, then imported non-exportable into `LocalMachine\My` on the NAV server | Secret value in NAV Table 1261 "Service Password" (encrypted if the database has an encryption key) |
| Leak risk | Once imported, the key cannot be exported from the server's store; delete all other `.pfx` copies except one in a secure vault (for renewal). Entra only holds the public `.cer` | Anyone who reads the value can request tokens |
| NAV field | *Graph Cert. Thumbprint* | *Client Secret* (masked page field) |

Use a certificate in production. Use a secret only for quick tests and with a short lifetime.

Tenant admins can enforce lifetimes or block secrets with Entra **app management policies**
(`passwordLifetime`, `passwordAddition`, `asymmetricKeyLifetime`). These policies only block creating or
updating credentials; they never stop token issuance for existing apps.

## Script variant (recommended)

Runs on the admin workstation with PowerShell 7.6+ (macOS, Linux, Windows). Scripts and parameters:
[../deploy/README.md](../deploy/README.md).

```powershell
Install-Module Microsoft.Graph.Applications -Scope CurrentUser

# 1. self-signed certificate: ./certs/NavMailGraph.pfx (private key) + ./certs/NavMailGraph.cer (public)
./deploy/New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs -Subject 'CN=NAV2016 MailGraph Contoso'

# 2. app registration + service principal + certificate, NO Graph permission (variant A)
$app = ./deploy/New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph' -CertificatePath ./certs/NavMailGraph.cer
$app | Format-List
```

`New-NavMailGraphCertificate.ps1` options: `-Subject`, `-ValidityMonths` (1–12, default 12), `-KeyLength`
(2048/3072/4096), `-OutputDirectory`, `-FileName`, `-PfxPassword`, `-Force`, `-KeepInStore`. It prompts for the
PFX password when `-PfxPassword` is not given and writes no password to disk.

`New-NavMailGraphApp.ps1` connects with `Connect-MgGraph -Scopes Application.ReadWrite.All`, creates a
single-tenant app (`AzureADMyOrg`), uploads the `.cer` as key credential and creates the service principal
(Enterprise application). It returns an object with `DisplayName`, `TenantId`, `ClientId`,
`ApplicationObjectId`, `ServicePrincipalObjectId`, `Thumbprint`, `CertificateExpires`, `SecretExpires` and
`GraphPermissions`. Further switches: `-TenantId`, `-SkipConnect`, `-UseDeviceCode`, `-AllowDuplicateName`.

Client secret for a test app:

```powershell
./deploy/New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph TEST' -CreateSecret -SecretValidityMonths 3
```

The secret value is printed **once** and is not stored or returned. Copy it straight into NAV.

Variant B (not recommended):

```powershell
$app = ./deploy/New-NavMailGraphApp.ps1 -CertificatePath ./certs/NavMailGraph.cer -GrantTenantWide -IncludeReadWrite
```

`-GrantTenantWide` adds the Graph application permission `Mail.Send`
(`b633e1c5-b582-4048-a93e-9f11b44c7e96`), with `-IncludeReadWrite` also `Mail.ReadWrite`
(`e2a3a72e-5f79-4c64-b1b1-878b674786c9`), and grants them with `New-MgServicePrincipalAppRoleAssignment`
(no separate consent click). It connects with the additional scope `AppRoleAssignment.ReadWrite.All`.

## Portal variant

Microsoft Entra admin center (<https://entra.microsoft.com>). Menu names can change; the steps below describe
the current layout.

1. **Identity → Applications → App registrations → New registration.**
   * Name: e.g. `NAV2016 MailGraph`.
   * Supported account types: **Accounts in this organizational directory only** (single tenant).
   * Redirect URI: leave empty.
   * **Register**.
2. On the app's **Overview** note:
   * **Directory (tenant) ID** → NAV *Graph Tenant ID*.
   * **Application (client) ID** → NAV *Graph Client ID*.
3. **Certificates & secrets → Certificates → Upload certificate** → select the `.cer` file
   (from `New-NavMailGraphCertificate.ps1`, or from your PKI). Note the **thumbprint** shown → NAV
   *Graph Cert. Thumbprint*.
   Upload only the public `.cer`, never the `.pfx`.
4. Secret instead (tests only): **Certificates & secrets → Client secrets → New client secret**, choose an
   expiry below 12 months, then copy the **Value** column immediately. The **Secret ID** is not the secret;
   using it gives `AADSTS7000215`.
5. **API permissions**:
   * Variant A: add **no** Microsoft Graph application permission. If the portal added the delegated
     permission `User.Read` by default, it is not used by NavMailGraph and can be removed.
   * Variant B: **Add a permission → Microsoft Graph → Application permissions → Mail.Send** (and
     **Mail.ReadWrite** for large messages), then **Grant admin consent for <tenant>**.
6. **Enterprise applications** → search the app → **Overview** → note the **Object ID**. This is the
   *service principal* object ID that Exchange Online needs (`New-ServicePrincipal -ObjectId`,
   `New-ManagementRoleAssignment -App`). It is **not** the object ID shown under App registrations.

Then continue with [exchange-rbac.md](exchange-rbac.md) (variant A).

### Azure CLI (alternative)

From the research notes, for automation without the Graph PowerShell SDK:

```bash
az ad app create --display-name "NAV2016 MailGraph" --sign-in-audience AzureADMyOrg
az ad sp create --id <appId>
az ad app credential reset --id <appId> --cert @cert.pem --append
```

Without `--append`, `az ad app credential reset` removes all existing credentials. For variant A skip
`az ad app permission add` / `admin-consent`.

## Optional hardening

* Enterprise application → **Properties → Assignment required = Yes**. This does not restrict which
  mailboxes the app can reach (that is Exchange RBAC's job). It only prevents other clients from getting
  role-less tokens for this app.
* Keep the app single-tenant.
* Give the app an owner and a description that names the NAV system and the contact person.

## Values for NAV

Page 409 "SMTP Mail Setup", group *Microsoft Graph* (fields are described in [../nav/README.md](../nav/README.md#configuration)):

| NAV field | Value | From |
|-----------|-------|------|
| Mail Transport | `SMTP` until the tests pass, then `Microsoft Graph` | – |
| Graph Tenant ID | Directory (tenant) ID (GUID) or a verified domain such as `contoso.onmicrosoft.com`. Never `common`. | Overview / `TenantId` |
| Graph Client ID | Application (client) ID | Overview / `ClientId` |
| Graph Auth Method | `Certificate` (recommended) or `Client Secret` | – |
| Graph Cert. Thumbprint | Thumbprint of the certificate in `LocalMachine\My` on the NAV server | `Thumbprint`, or output of `Install-NavMailGraphAddin.ps1` |
| Client Secret (masked) | Secret **Value** (secret variant only) | portal or `-CreateSecret` output |
| Graph Sender Mailbox | The scoped mailbox, e.g. `nav@contoso.com` | [exchange-rbac.md](exchange-rbac.md) |
| Graph Reply-To Caller | Yes: replies go to the address NAV passed as sender | – |
| Graph Save to Sent Items | Yes/No (the large-message flow always saves) | – |
| Graph Large Messages | Yes only if `Mail.ReadWrite` is granted (scoped or tenant-wide) | – |
| Graph Token Library | `Built-in` (default); `MSAL` only if the MSAL files are deployed | [deployment.md](deployment.md#msal-flavour) |
| Graph Cloud | `Global`, or `US Government` / `US Government DoD` / `China` for national clouds | – |
| Graph Log File Path | Optional, see [troubleshooting.md](troubleshooting.md#log-file-and-diagnostics) | – |

The field *Graph Secret Key* is internal (GUID of the Table 1261 entry) and is not shown on the page.

## Renewal

Certificate (yearly):

1. Create a new certificate (`New-NavMailGraphCertificate.ps1`).
2. Add the new `.cer` to the app **in addition** to the old one: portal → Certificates & secrets → Upload.
   With `Update-MgApplication` note that a PATCH of `keyCredentials` **replaces** all existing certificates;
   include the old one.
3. Install the new `.pfx` on the NAV server (`Install-NavMailGraphAddin.ps1 -PfxPath ...`).
4. Change *Graph Cert. Thumbprint* in NAV, run **Test Graph Connection**.
5. Remove the old certificate from the app and from `LocalMachine\My`.

Client secret: create a new secret, enter it in NAV, test, delete the old secret. Expired secrets fail with
`AADSTS7000222`.

Put the expiry date in a calendar. NavMailGraph logs a warning when the certificate is outside its validity
period; Entra rejects expired certificates.

## Sources

Facts and links: [research-notes.md](research-notes.md), section "Exchange Online RBAC for Applications and Entra
app registration".
