# nav2016-mailgraph

Send e-mail from **Microsoft Dynamics NAV 2016** through **Microsoft Graph** instead of SMTP.

[Deutsche Fassung](README.de.md)

A small .NET add-in (`NavMailGraph.dll`) plus one new C/AL codeunit replace the SMTP transport behind
Codeunit 400 "SMTP Mail". Every existing caller (document mailing, reminders, report mailing, notification
e-mails, your own code) keeps working unchanged. Mail goes out through `POST /users/{mailbox}/sendMail` with
application permissions (OAuth 2.0 client credentials).

Version 0.2 adds the other direction and large files: a job queue codeunit **reads mailboxes** and imports
e-mails and attachments into NAV tables, an integration event hands them to your code (e.g. "create a purchase
invoice from every PDF"), and large attachments go to **Azure Blob Storage or Amazon S3** – in both directions,
including "send a download link instead of a 50 MB attachment".

## Why

* NAV 2016 Codeunit 400 only speaks SMTP with user name and password. Microsoft has announced the end of basic
  authentication for SMTP AUTH in Exchange Online (check Microsoft's current timeline for your tenant).
* Graph with application permissions needs no user password and no SMTP relay.
* With Exchange Online **RBAC for Applications** the app can be limited to the one sender mailbox NAV uses.
  It cannot read or send from any other mailbox.

## Architecture

```mermaid
flowchart LR
    subgraph NAV["NAV 2016 Server (Windows, .NET Framework 4.6.2+)"]
        callers["Callers<br/>CU 260 / 9520 / 412 / reports / custom code"]
        cu400["Codeunit 400 'SMTP Mail'<br/>(patched: delegates when<br/>Mail Transport = Microsoft Graph)"]
        cu50400["Codeunit 50400 'Graph Mail'"]
        dll["NavMailGraph.dll<br/>(Add-ins folder, no NuGet deps)"]
        t409[("Table 409 'SMTP Mail Setup'<br/>+ Graph fields<br/>Table 1261 Service Password")]
        callers --> cu400 --> cu50400 --> dll
        cu50400 -. reads .-> t409
    end
    dll -- "client credentials<br/>(certificate or secret)" --> entra["Microsoft Entra ID<br/>login.microsoftonline.com"]
    dll -- "sendMail / draft + upload session" --> graph["Microsoft Graph<br/>graph.microsoft.com"]
    graph --> exo["Exchange Online<br/>sender mailbox<br/>(RBAC for Applications scope)"]
    dll -. "0.2: read mail (job queue)" .-> graph
    dll -. "0.2: large attachments, links" .-> storage[("Azure Blob Storage<br/>or Amazon S3")]
```

The same picture as text:

```
 NAV callers ──> Codeunit 400 ──(Mail Transport = Microsoft Graph)──> Codeunit 50400 ──> NavMailGraph.dll
                     │                                                                        │
                     └──(Mail Transport = SMTP: original code, unchanged)                     │
                                                                                              ▼
                                    Entra ID token (client credentials) ──> Microsoft Graph ──> Exchange Online mailbox
```

## Features

* Drop-in for NAV 2016 Codeunit 400: all signatures unchanged, rollback by switching *Mail Transport* back to
  `SMTP`.
* Core add-in with **zero NuGet dependencies**: one DLL in the Add-ins folder, no binding redirects.
* Authentication with a **certificate** (recommended, `LocalMachine\My` by thumbprint) or a client secret
  (stored encrypted in NAV Table 1261 "Service Password" when the database has an encryption key).
* Optional MSAL token provider (`NavMailGraph.Msal.dll`) for people who want Microsoft's auth library.
* Messages whose attachments total up to about 2.5 MB go out in one `sendMail` call (needs **Mail.Send** only).
  The limit applies to the whole request incl. base64 encoding (3.5 MiB), so a 3 MB PDF is already "large".
* Larger messages use draft + attachment upload sessions (up to 150 MB per attachment; needs **Mail.ReadWrite**
  in addition). Can be switched off.
* Fixed sender mailbox: From is always that mailbox, the NAV user's address goes into Reply-To. No Send As
  right needed.
* Retries on HTTP 429/503/509 with `Retry-After`. A sendMail or draft send that times out or gets HTTP 504 is never repeated; the error says the outcome is unknown, so no duplicate mails. Token cache per process.
* Error texts with the Graph/Entra error code, request id and a configuration hint. Optional log file with
  secrets redacted.
* Clouds: Global, US Government (L4), US Government DoD (L5), China (21Vianet).
* PowerShell scripts for the certificate, the Entra app registration, the Exchange RBAC scope and the server
  installation.

New in 0.2 ([docs/reading-mail.md](docs/reading-mail.md), [docs/large-attachments.md](docs/large-attachments.md),
[docs/object-storage.md](docs/object-storage.md)):

* **Reading mailboxes** from the NAV job queue: one setup line per mailbox, folder paths like `Inbox/Invoices`,
  immutable Graph ids, import into NAV tables (header, body, attachments, original `.eml` on demand).
* After import the mail is **moved to `<Folder>/Processed`** (default; failures to `<Folder>/Error`), or marked as
  read, deleted, or left alone. Polling by a received-date **watermark** with de-duplication, or a **read-only delta
  mode** that needs only Mail.Read.
* **Integration events** `OnBeforeImportMail`, `OnAfterImportAttachment`, `OnAfterImportMail` for partner code; a
  failing subscriber marks only that mail as Error.
* **Large inbound attachments** are streamed (`$value`, never base64 in memory) into a NAV BLOB up to a size limit,
  or **straight from Graph into object storage** without a temp file.
* **Object storage** in the core DLL, still without NuGet dependencies: Azure Blob (Shared Key, SAS URL or Entra ID
  token of the same app) and Amazon S3 / S3-compatible storage (SigV4, path-style for R2, Wasabi, SeaweedFS ...),
  block/multipart uploads, list, delete, time-limited download links.
* **Link instead of attachment** when sending: attachments above a threshold are uploaded and replaced by a download
  link in the body; archived files can be attached straight from storage.
* Builds and unit-tests on macOS; the .NET Framework build is tested on Windows in CI.

## Requirements

| Component | Requirement |
|-----------|-------------|
| Dynamics NAV | NAV 2016 (C/AL, C/SIDE). Objects written against CU1 (build 43402). NAV 2017+ untested. |
| NAV Server | Windows Server with **.NET Framework 4.6.2 or later** and **TLS 1.2** (enabled by default on Windows Server 2012 R2 and later) |
| Microsoft 365 | Exchange Online with a mailbox for NAV (a shared mailbox is recommended) |
| Entra ID | Rights to create an app registration (Application Administrator or Cloud Application Administrator) |
| Exchange | Member of "Organization Management" (or Entra role "Exchange Administrator") for the RBAC scope |
| Admin workstation | PowerShell 7.6+, modules Microsoft.Graph.Applications and ExchangeOnlineManagement 3.10.x |
| Build machine | .NET SDK 8 (macOS, Linux or Windows). A newer SDK works only if the .NET 8 runtime is installed too: tests and `nmg` target net8.0 and do not roll forward to a newer major runtime. |

NAV 2016 itself documents .NET Framework 4.5.2 as its minimum. The add-in targets 4.6.2, so the server needs
4.6.2 or later (4.8 recommended).

## Quick start

1. **Build** (macOS shown; Linux/Windows work the same).

   ```bash
   brew install --cask dotnet-sdk@8   # tests and nmg need the .NET 8 runtime; plain dotnet-sdk installs only the latest
   ./build.sh
   ```

   Result: `dist/Add-ins/NavMailGraph/NavMailGraph.dll` (+ optional `Msal/` subfolder), `dist/nav/` and
   `dist/SHA256SUMS`. Or download the release zip. Details: [docs/development-macos.md](docs/development-macos.md).

2. **Entra app registration** with a certificate and **no** Graph permission.

   ```powershell
   ./deploy/New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs
   $app = ./deploy/New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph' -CertificatePath ./certs/NavMailGraph.cer
   ```

   Details and the portal variant: [docs/azure-setup.md](docs/azure-setup.md).

3. **Exchange scope**: allow the app to send only as the NAV mailbox.

   ```powershell
   ./deploy/Grant-NavMailGraphMailboxScope.ps1 -AppId $app.ClientId -ServicePrincipalObjectId $app.ServicePrincipalObjectId `
       -DisplayName $app.DisplayName -Mailbox nav@contoso.com -NegativeTestMailbox ceo@contoso.com
   # add -IncludeReadWrite for attachments > ~2.5 MB in total (sendMail request incl. base64 is limited to 3.5 MiB)
   ```

   Wait 30 minutes to 2 hours before the first real send (Exchange permission cache).
   Details: [docs/exchange-rbac.md](docs/exchange-rbac.md).

4. **Install the add-in** on the NAV Server (elevated Windows PowerShell 5.1, in the `deploy\` folder of the
   extracted release zip or of the repository after `./build.sh`).

   ```powershell
   .\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls
   ```

   Copies the DLL, imports the certificate, grants the NAV service account read access to the private key, sets
   the TLS registry values and restarts the instance. Details: [docs/deployment.md](docs/deployment.md).

5. **NAV objects**: copy the DLL to the C/SIDE machine, apply the Table 409 patch, import Codeunit 50400,
   apply the Codeunit 400 and Page 409 patches, compile. Follow [nav/README.md](nav/README.md) step by step.

6. **Test** on Page 409 "SMTP Mail Setup", group *Microsoft Graph*: fill in the fields, run
   **Test Graph Connection**, then **Send Graph Test Email**. When both work, set *Mail Transport* =
   `Microsoft Graph` and run the standard **Test Email Setup**. If something fails:
   [docs/troubleshooting.md](docs/troubleshooting.md).

7. **Optional (0.2): read a mailbox.** Grant the app the mailbox (`Grant-NavMailGraphMailboxScope.ps1 -Mailbox
   invoices@contoso.com -IncludeReadWrite`, or `-IncludeRead` for read-only), import the v0.2 objects
   ([nav/README.md](nav/README.md#installation-v02)), create a line on **Graph Mailbox Setup**, run **Test
   Connection** and **Poll Now**, then **Schedule Polling**. Concepts: [docs/reading-mail.md](docs/reading-mail.md).

8. **Optional (0.2): object storage.** Create an **Object Storage Profile** (Azure Blob or S3), run **Test
   Connection** and **Upload Test File**, then use it for large inbound attachments or as *Graph Link Storage
   Profile* for sending. Setup and security: [docs/object-storage.md](docs/object-storage.md).

## Permissions

| Permission | When | What it allows |
|------------|------|----------------|
| **Mail.Send** (application) | always | Send mail as the mailbox and save a copy to Sent Items. No reading. |
| **Mail.ReadWrite** (application) | sending: only for messages whose attachments total more than about 2.5 MB (the `sendMail` request incl. base64 encoding is limited to 3.5 MiB; a typical 3 MB scanned PDF already needs it; *Graph Large Messages* = Yes). Reading (0.2): for the default mode that moves processed mail, and for mark as read / delete | Create a draft, add attachments, upload sessions, delete the draft. Read, mark, move and delete mail and create folders in the mailbox. Does **not** include sending. |
| **Mail.Read** (application) | reading (0.2) in read-only mode (*After Import* = Nothing); not needed when Mail.ReadWrite is granted | Read messages, bodies and attachments of the mailbox. No changes, no sending. Mail.ReadBasic is not enough (no body, no attachments). |
| **Storage Blob Data Contributor** (Azure RBAC role) | only for object storage in Azure *Entra ID* mode (0.2), on the container | Read, write and delete blobs in that container. Shared Key, SAS URL and S3 profiles use their own keys instead. |

Recommended: grant the mail permissions **only** through Exchange Online RBAC for Applications (roles
`Application Mail.Send`, `Application Mail.ReadWrite`, `Application Mail.Read`), scoped to the NAV mailboxes, and
**not** as Graph application permissions with admin consent in Entra ID. Entra and Exchange grants add up; a
tenant-wide Entra grant makes any Exchange scope ineffective – for Mail.Read that means **every** mailbox can be
read. Explained in [docs/exchange-rbac.md](docs/exchange-rbac.md) and [docs/reading-mail.md](docs/reading-mail.md#permissions).
A German one-pager for customers: [docs/de/kundeninfo-berechtigungen.md](docs/de/kundeninfo-berechtigungen.md).

## Limits

| Limit | Value | Source / effect |
|-------|-------|-----------------|
| Graph request size (`sendMail`) | 4 MB | The add-in switches to the draft flow above an estimated 3.5 MiB request (base64 adds ~33 %). |
| Direct attachment POST | 3 MB | Larger attachments use an upload session. |
| Upload session | 150 MB per attachment | Add-in default `MaxAttachmentBytes` = 150 MiB. Not available in US Government L4/L5. |
| Exchange Online message size | 35 MB (default) | Admins can change it (1–150 MB). A larger upload succeeds but the message bounces (NDR). |
| Sending rate | 30 messages per minute per mailbox | Excess is throttled and delayed. |
| Recipient rate | 10,000 recipients per day per mailbox | Rolling 24 hours. |
| Recipients per message | 500 (default) | To + Cc + Bcc; admins can set 1–1000. |
| Graph upload volume | 150 MB per 5 minutes per app and mailbox | Relevant for many large attachments. |
| Graph requests (reading) | 10,000 per 10 minutes and 4 concurrent per app and mailbox | The add-in runs at most 4 requests per mailbox at a time and retries 429/503 with `Retry-After`. |
| S3 presigned link | 7 days | Longer link validity fails before anything is uploaded. Azure Shared Key links have no such cap. |

The add-in does not enforce the Exchange limits; tenants can change them. Exchange Online is not meant for bulk
mail. A `202 Accepted` from Graph means "accepted", not "delivered": rejected recipients show up later as
non-delivery reports in the sender mailbox.

## Documentation

| Document | Content |
|----------|---------|
| [docs/azure-setup.md](docs/azure-setup.md) | Entra app registration (portal and script), certificate vs. secret, values for NAV |
| [docs/exchange-rbac.md](docs/exchange-rbac.md) | RBAC for Applications: scoping, testing, removal |
| [docs/deployment.md](docs/deployment.md) | Windows server: install script, manual steps, certificate, TLS, proxy, MSAL, updates |
| [nav/README.md](nav/README.md) | NAV objects: import, patches, configuration, tests, rollback |
| [docs/development-macos.md](docs/development-macos.md) | Build, tests, `nmg` CLI, release process |
| [docs/reading-mail.md](docs/reading-mail.md) | Reading mailboxes: processing modes, watermark, delta, permissions, job queue, events, limits |
| [docs/object-storage.md](docs/object-storage.md) | Azure Blob (Shared Key, SAS, Entra ID) and S3: setup, IAM policy, providers, key layout, security |
| [docs/large-attachments.md](docs/large-attachments.md) | Large attachments when sending (draft, upload session, link) and receiving (streaming, BLOB limit, storage) |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Error codes, causes and fixes; log file |
| [docs/de/kundeninfo-berechtigungen.md](docs/de/kundeninfo-berechtigungen.md) | German customer information on permissions |
| [deploy/README.md](deploy/README.md) | PowerShell scripts in detail |
| [docs/DESIGN.md](docs/DESIGN.md), [docs/DESIGN-v2.md](docs/DESIGN-v2.md) | Design and API contract (v0.1, v0.2) |
| [docs/research-notes.md](docs/research-notes.md), [docs/research-notes-v2.md](docs/research-notes-v2.md) | Verified facts with Microsoft, AWS and NAV sources |
| [CHANGELOG.md](CHANGELOG.md) | Changes per version |

## Status and disclaimer

* Version 0.2.0 in development (0.1.0 = sending only), pre-release. Use a test tenant and a NAV test database first.
* The C/AL objects (Codeunits 50400–50404, Tables/Pages 504xx and the patches) are **not compiled in CI**; there
  is no NAV 2016 compiler there. They were checked statically against NAV 2016 CU1 object exports
  (`nav/tools/check_cal.py`). Import and compile them in C/SIDE and test before production.
* Object storage was tested against the Azurite and SeaweedFS emulators; Graph reading against a fake Graph server
  in the unit tests. Test both against your real tenant, storage account or bucket before going live.
* The Microsoft objects (Codeunit 400, Table 409, Page 409) are not redistributed. The repository only contains
  patch instructions.
* This project is **not affiliated with, endorsed or supported by Microsoft**. Microsoft, Microsoft 365,
  Exchange Online, Microsoft Entra and Dynamics NAV are trademarks of the Microsoft group of companies.
* Security issues: see [SECURITY.md](SECURITY.md). Contributions: see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
