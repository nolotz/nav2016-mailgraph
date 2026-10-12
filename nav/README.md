# NAV 2016 objects and patches

This folder contains the Dynamics NAV 2016 (C/AL) side of nav2016-mailgraph:

* **Sending (v0.1, NMG1.00):** Codeunit 50400 and step-by-step patch instructions for three Microsoft objects.
  After the change, every caller of Codeunit 400 "SMTP Mail" (document mailing, reminders, report mailing,
  notification e-mails, customer code) sends through Microsoft Graph `sendMail` when *Mail Transport* =
  *Microsoft Graph* in the SMTP Mail Setup. No caller changes.
* **Reading and object storage (v0.2, NMG2.00):** a job queue codeunit polls Exchange Online mailboxes,
  imports e-mails and attachments into NAV tables, moves processed mails to a folder and raises an integration
  event for your processing (e.g. create purchase invoices). Large attachments go to Azure Blob Storage or
  Amazon S3 / S3-compatible storage; outgoing large attachments can be sent as a download link. See
  [Reading mail and object storage (v0.2)](#reading-mail-and-object-storage-v02).

> **Not compiled in CI. Verify in C/SIDE.** The objects were written from the NAV 2016 CU1 (build 43402)
> object facts in [../docs/research-notes.md](../docs/research-notes.md) and checked only statically (block
> balance, IDs, field names). There is no NAV 2016 compiler in CI. Import, compile and test them in a NAV 2016
> development database before anything goes to a customer.

## Contents

| Path | What |
|------|------|
| [objects/COD50400.TXT](objects/COD50400.TXT) | **Codeunit 50400 "Graph Mail"** – new, complete C/AL text export (ours, MIT) |
| [objects/](objects/) `TAB50400..50403`, `PAG50400..50408`, `COD50401..50404` | v0.2 objects for reading mail and object storage (ours, MIT), see the [object list](#object-list) |
| [patches/TAB409.md](patches/TAB409.md) | Table 409 "SMTP Mail Setup": fields 50100..50112 (v0.1) and 50113..50115 (v0.2, links) |
| [patches/PAG409.md](patches/PAG409.md) | Page 409 "SMTP Mail Setup": Microsoft Graph group, masked secret, test actions, v0.2 link fields |
| [patches/COD400.md](patches/COD400.md) | Codeunit 400 "SMTP Mail": globals + one delegation block per function |
| [tools/Find-SmtpCallers.ps1](tools/Find-SmtpCallers.ps1) | Scans exported .TXT objects for Codeunit 400 / Table 409 / "SMTP Server" usage |
| [tools/check_cal.py](tools/check_cal.py) | Static check of the TXT objects (no compiler): encoding, block balance, ids, names, fields, DotNet members, calls |

The Microsoft objects are **not** redistributed. The patch files only show signature lines to locate the
insertion points plus the lines to insert.

### Object list

| Object | No. | Change | Version List |
|--------|-----|--------|--------------|
| Codeunit "Graph Mail" | 50400 | new (v0.2: `CreateGraphConnection`, link storage) | NMG2.00 |
| Table "SMTP Mail Setup" | 409 | 13 fields (v0.1) + 3 fields 50113..50115 (v0.2) | append `,NMG1.00` / `,NMG2.00` |
| Page "SMTP Mail Setup" | 409 | group, 12 table fields + 2 variable fields, 2 actions, 1 function; v0.2: 3 fields | append `,NMG1.00` / `,NMG2.00` |
| Codeunit "SMTP Mail" | 400 | 2 globals, delegation in 13 functions + subscriber | append `,NMG1.00` |
| Table "Graph Mailbox Setup" | 50400 | new – one line per polled mailbox | NMG2.00 |
| Table "Graph Inbound Mail" | 50401 | new – imported e-mails (header, body BLOB) | NMG2.00 |
| Table "Graph Inbound Attachment" | 50402 | new – attachments (BLOB, object storage key or server file) | NMG2.00 |
| Table "Object Storage Profile" | 50403 | new – Azure Blob / S3 connection, secret in Service Password | NMG2.00 |
| Page "Graph Mailbox Setup" | 50400 | new – list of mailboxes (card 50405) | NMG2.00 |
| Page "Graph Inbound Mails" | 50401 | new – list of imported e-mails (card 50402, attachments FactBox 50403) | NMG2.00 |
| Page "Graph Inbound Mail" | 50402 | new – card with attachments subpage | NMG2.00 |
| Page "Graph Inbound Mail Attachments" | 50403 | new – ListPart | NMG2.00 |
| Page "Object Storage Profiles" | 50404 | new – list (card 50407) | NMG2.00 |
| Page "Graph Mailbox Card" | 50405 | new | NMG2.00 |
| Page "Graph Inbound Attachments" | 50406 | new – list of all attachments | NMG2.00 |
| Page "Object Storage Profile Card" | 50407 | new – with masked secret | NMG2.00 |
| Page "Graph Inbound Mail Body" | 50408 | new – plain-text view of the body | NMG2.00 |
| Codeunit "Graph Mailbox Polling" | 50401 | new – job queue codeunit (TableNo 472), events | NMG2.00 |
| Codeunit "Object Storage Mgt." | 50402 | new – storage facade, secrets | NMG2.00 |
| Codeunit "Graph Inbound Mail Mgt." | 50403 | new – open attachment, .eml, body, reprocess | NMG2.00 |
| Codeunit "Graph Mail Import Runner" | 50404 | new – internal (isolates one e-mail per transaction) | NMG2.00 |

Numbers 50100+ / 50400 are in the customer range; change them if your licence or existing customizations
require it. Codeunit 50400 refers to the Table 409 fields **by name**, so renumbering fields is safe.
Renumbering the codeunit means changing `Codeunit 50400` in the Page 409 and Codeunit 400 patches.

### What Codeunit 50400 does

* Same functions as Codeunit 400 (NAV 2016): `CreateMessage`, `AddRecipients`, `AddCC`, `AddBCC`,
  `AppendBody`, `AddAttachment(Attachment,FileName)` (EXISTS check, Codeunit 419
  `StripNotsupportChrInFileName`, `;` removed – like Codeunit 400), `AddAttachmentStream(InStream,Name)`,
  `TrySend : Boolean` (never raises an error; `SendResult = ''`), `Send` (raises the error),
  `GetLastSendMailErrorText`, `GetBody`.
* `IsEnabled` – Table 409 exists and *Mail Transport* = Microsoft Graph.
* `TestConnection() : Text` / `ShowConnectionTest` – acquire a token only.
* `SendTestMail(ToAddress)` – HTML test mail through Graph, also while *Mail Transport* is still SMTP.
* `SetClientSecret` / `GetClientSecret` / `HasClientSecret` / `DeleteClientSecret` – client secret in
  Table 1261 "Service Password" (the Password-Key pattern of Table 1260), encrypted when the database has an
  encryption key. Table 409 only holds the GUID key.
* Sender logic: with a fixed *Graph Sender Mailbox* the From address is always that mailbox (no Send As
  right needed) and the caller's From address goes to Reply-To when *Graph Reply-To Caller* is set and it
  differs. Without a fixed mailbox, the caller's From address is the mailbox used for `/users/{mailbox}/sendMail`.
* DotNet: `NavMailGraph.GraphMailMessage` and `NavMailGraph.GraphMailSender` from
  `'NavMailGraph, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'`, server-side (no RUNONCLIENT).
  `CANLOADTYPE` gives a clear error if the DLL is missing on the server.
* Uses add-in members `UseDraftForLargeMessages` and the cloud value `USGovDoD` (DESIGN.md section 11).
  The NavMailGraph.dll you copy to the C/SIDE machine must be a build that contains them, otherwise
  Codeunit 50400 does not compile.

## Installation

Order matters: C/SIDE needs the DLL to compile, and Codeunit 400 / Page 409 need Codeunit 50400 and the new
Table 409 fields.

1. **Back up** the database (or at least export Table 409, Page 409 and Codeunit 400 as `.fob`).
2. **DLL on the C/SIDE machine.** Copy `NavMailGraph.dll` (from `dist/Add-ins/NavMailGraph/` of the build or the
   release zip) to the Add-ins folder of the Windows client on the machine that runs the development
   environment, e.g.
   `C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client\Add-ins\NavMailGraph\NavMailGraph.dll`.
   C/SIDE (finsql.exe, 32-bit) resolves DotNet types from there when compiling. Restart C/SIDE if it was open.
3. **DLL on the NAV Server.** Copy it to
   `C:\Program Files\Microsoft Dynamics NAV\90\Service\Add-ins\NavMailGraph\` (the `deploy/Install-NavMailGraphAddin.ps1`
   script does this). Keep old DLL copies **outside** the Add-ins tree. The MSAL flavour (optional) goes into a
   `Msal` subfolder – see the deployment docs.
4. **Table 409**: apply [patches/TAB409.md](patches/TAB409.md), compile.
5. **Codeunit 50400**: `File → Import` → `objects/COD50400.TXT`, then compile it.
6. **Codeunit 400**: apply [patches/COD400.md](patches/COD400.md), compile.
7. **Page 409**: apply [patches/PAG409.md](patches/PAG409.md), compile.
8. Compile all objects with Version List containing `NMG` (filter in Object Designer) once more, and compile
   Codeunits 412 and 9520 to make sure nothing depending on Codeunit 400 broke.
9. **Restart the NAV Server instance** so it loads the add-in (a running NST keeps loaded DLLs until restart).

### Shipping as .fob

For customer databases, do steps 4–8 in a development copy of the customer database (same NAV 2016 CU
level, same customizations) and export the four objects as one `.fob`. Import the `.fob` at the customer with
the Import Worksheet: choose **Replace** for Table 409 / Page 409 / Codeunit 400 only after checking that the
customer's versions match your dev copy (Import Worksheet shows conflicts when the objects differ). Table 409
schema changes only add fields, so *Synchronize Schema* runs without data loss. Never ship a `.fob` built in a
database with a different CU level or other Codeunit 400 modifications. Run
[tools/Find-SmtpCallers.ps1](tools/Find-SmtpCallers.ps1) against an export of the customer database first.

### Text file encoding

All `objects/*.TXT` files (`COD50400.TXT` and the v0.2 objects) are stored in the **OEM code page 850 (CP850) with CRLF**, the format C/SIDE (NAV 2016)
reads and writes for text objects, so the German captions import with correct umlauts. This is settled from
Microsoft's own NAV 2016 exports: e.g. `Page_0000000409.txt` in github.com/mouadhbh/NAV (commit `1848f41`)
stores the `é` of "Paramétrage" as byte `0x82`, which is `é` in CP850/437 and not in Windows-1252. The only
non-ASCII characters in our files are `ä` (`0x84`), `ö` (`0x94`), `ü` (`0x81`), `Ü` (`0x9A`) and `ß` (`0xE1`), which have
the same bytes in CP437, so the files also import correctly on systems whose OEM code page is 437. On Windows with another OEM code page
(check with `chcp`), convert it to that code page first.

Editors default to UTF-8 or Windows-1252 and show the umlauts as wrong characters; `nav/.editorconfig` removes
the repository-wide `charset = utf-8` for `*.TXT` so the file is not re-saved as UTF-8. If you edited the file in
a Windows-1252 editor (or exported it from a tool that writes 1252), convert it back before importing, e.g. in
**Windows PowerShell 5.1**:

```powershell
$text = [IO.File]::ReadAllText("$PWD\COD50400.TXT", [Text.Encoding]::GetEncoding(1252))
[IO.File]::WriteAllText("$PWD\COD50400.oem.TXT", $text, [Text.Encoding]::GetEncoding(850))
```

ENU texts are pure ASCII and are not affected; wrong encoding only garbles the DEU umlauts.

The `Date=12.10.26` in `OBJECT-PROPERTIES` is in German `dd.mm.yy` format. If C/SIDE on a non-German locale
rejects it on import, change it to your local short date format.

## Configuration

Open **SMTP Mail Setup** (Page 409), group **Microsoft Graph**:

| Field | Value |
|-------|-------|
| Mail Transport | `SMTP` (default) until the tests below succeed, then `Microsoft Graph` |
| Graph Tenant ID / Graph Client ID | from the Entra app registration |
| Graph Auth Method | `Certificate` (recommended) or `Client Secret` |
| Client Secret | the secret **value** (not the secret ID); stored in Service Password |
| Graph Cert. Thumbprint | certificate in `LocalMachine\My`; NAV service account needs read access to the private key |
| Graph Sender Mailbox | e.g. `nav@contoso.com` (recommended) or blank to send as the caller's From address |
| Graph Reply-To Caller | Yes: replies go to the user/address NAV passed as sender |
| Graph Save to Sent Items | Yes/No |
| Graph Large Messages | Yes only if the app has **Mail.ReadWrite** (needed when attachments total more than ~2.5 MB; the sendMail request incl. base64 is limited to 3.5 MiB) |
| Graph Token Library / Graph Cloud | normally `Built-in` / `Global` |
| Graph Log File Path | optional, a path writable by the NAV service account, for troubleshooting |

**From address on the Web client.** Before Codeunit 400/50400 runs, Codeunit 9520 `QualifyFromAddress` looks
for a From address for a blank sender (User Setup *E-Mail*, User *Authentication Email*, Active Directory, SMTP
*User ID*). On the **Web client** it stops with "An email from address was not found. Contact an administrator."
when none is found, even if a *Graph Sender Mailbox* is set. The SMTP *User ID* only counts when SMTP
*Authentication* = `Basic` and it contains `@`, so a Graph-only setup with blank SMTP fields does not provide
one. Therefore either give every Web client user an e-mail address in **User Setup (E-Mail)** or the User card
(**Authentication Email**), or, as a fallback, set SMTP **Authentication = Basic** and SMTP **User ID** = the
*Graph Sender Mailbox* address. While *Mail Transport* = Microsoft Graph the SMTP fields are not used for sending,
so this only feeds `QualifyFromAddress`; enter a real SMTP password or reset these fields before you switch back
to SMTP. The Windows client continues with a blank From address instead of an error.

Set up data encryption for the database (Data Encryption Management) so the client secret is stored
encrypted; Codeunit 50400 warns when it is not.

## Testing

1. **Test Graph Connection** (Page 409 action) – acquires a token with the current settings. Works while
   *Mail Transport* is still SMTP. Error texts include the Entra/Graph error code and a hint.
2. **Send Graph Test Email** (Page 409 action) – enter *Test E-Mail Recipient* first. Sends through Graph,
   independent of *Mail Transport*.
3. Set *Mail Transport* = `Microsoft Graph`, then run the standard **Test Email Setup** action (Codeunit 412
   "SMTP Test Mail"). This goes through Codeunit 400 → Codeunit 50400 and proves the patch. The body still lists
   the SMTP fields – expected.
4. Send a real document from NAV (e.g. *Send by E-mail* on a posted sales invoice) with a PDF attachment, and one
   with an attachment > 4 MB if *Graph Large Messages* is enabled.
5. With *Graph Sender Mailbox* set, check in the received mail that From = mailbox and Reply-To = the user's
   address.

## Rollback

* **Immediate:** set *Mail Transport* back to `SMTP` in Page 409. Codeunit 400 then runs its original code
  path unchanged (the SMTP fields were never touched). No restart needed.
* **Full:** re-import the backed-up Table 409 / Page 409 / Codeunit 400 objects, delete Codeunit 50400, remove
  the DLL from the Add-ins folders and restart the NAV Server. Removing the Table 409 fields requires a schema
  sync that drops their data (Force); the Service Password entry for the secret can be deleted first with
  `DeleteClientSecret` (or by clearing the Client Secret field).
* **v0.2 (reading/storage) only:** set *Enabled* = No on the mailboxes and delete their Job Queue Entries (Codeunit
  50401). To remove completely, delete the objects 50400..50408 (tables, pages) and 50401..50404 (codeunits), then
  re-import the v0.1 `COD50400.TXT` (Version NMG1.00, from the v0.1 release) and remove the Table 409 fields
  50113..50115 and the Page 409 link fields. Delete the storage secrets first (clear the *Secret* on each storage
  profile card) so no Service Password entries remain. Objects in Azure/S3 are not touched.

## Find-SmtpCallers.ps1

Export all objects of the customer database as text (one file or one file per object) and run:

```powershell
pwsh ./tools/Find-SmtpCallers.ps1 -Path C:\Export\Objects -ExcludeKnown
pwsh ./tools/Find-SmtpCallers.ps1 -Path C:\Export\all.txt -CsvPath smtp-usage.csv
```

It lists objects that declare Codeunit 400, call `GetSMTPMessage`, read the field `"SMTP Server"`, reference
Table 409, or use the `Microsoft.Dynamics.Nav.SMTP` DotNet type themselves. Objects handled by the patches and
the standard users that need no change (Codeunit 412, 9520, Page 410) are marked *Known*. Everything else needs
a look: code that checks `"SMTP Server" <> ''` should use `Codeunit 400.IsEnabled` instead, and code that
calls `GetSMTPMessage` or the SMTP DotNet type bypasses Graph. Works on Windows PowerShell 5.1 and
PowerShell 7.

## Reading mail and object storage (v0.2)

### How polling works

Codeunit 50401 "Graph Mailbox Polling" runs from the job queue (`TableNo` 472; *Parameter String* = mailbox code,
blank = all enabled mailboxes) or from the action **Poll Now**. Per mailbox it reads up to *Max. Mails per Run*
messages of *Folder* (oldest first) and handles every e-mail on its own:

0. **Run lock and retries** – only one session polls a mailbox at a time (job queue entries, **Poll Now**, a second
   job queue, an entry for all mailboxes). The lock is kept in Table 50400 (*Poll Session ID*, *Poll Started At*); a
   lock whose session no longer exists (Table 2000000110 "Active Session") is taken over, and a second run that finds
   the lock ends without doing anything. Each run first finishes e-mails of earlier runs whose mailbox action failed
   or whose run stopped after the import (up to 5 attempts per e-mail, *Mailbox Action Attempts*; 25 e-mails per run).
1. **Duplicate check** – an e-mail already in Table 50401 (same mailbox and Graph id) is not imported again; a
   missing `OnAfterImportMail` (*Subscribers Run* = No) or a missing/failed mailbox action is completed. Another copy
   of an imported e-mail (same internetMessageId, other Graph id) only gets the mailbox action; the existing entry
   is not changed.
2. **Import** (own transaction, Codeunit 50404): header, body (BLOB, UTF-8), attachments, then `COMMIT`.
   Event `OnBeforeImportMail` can skip the e-mail; `OnAfterImportAttachment` runs per attachment. When the import
   fails, the objects / server files already written for it are deleted again (best effort).
3. **Processing** (own transaction, Codeunit 50404): event `OnAfterImportMail`. A failing subscriber rolls back
   only its own changes; the e-mail gets *Status* = Error and the error text, and the next e-mail continues.
4. **Mailbox action** (*After Import*): *Move to Processed Folder* (default: `<Folder>/Processed`), *Mark as Read*,
   *Delete* or *Nothing*. Failed e-mails go to the error folder (default `<Folder>/Error`) unless *After Import* =
   *Nothing*. Folders are created when missing.
5. **Watermark**: *Last Received (Text)* = the exact Graph `receivedDateTime` of the newest e-mail; the next run reads
   `receivedDateTime ge <watermark>` (inclusive, duplicates are skipped by step 1).

*Polling Mode* = *Delta Query* uses Graph delta (the delta link is kept in the BLOB *Delta Link*); combined with
*After Import* = *Nothing* this needs only **Mail.Read** (read-only mailbox). When Graph drops the sync state the
codeunit restarts the delta sync bounded by the watermark.

A NAV 2016 job queue entry that ends with an error gets *Status* = Error after *Maximum No. of Attempts to Run* and
stops recurring. So a mailbox that cannot be read (token, throttling, network, permissions, folder not found) does
**not** raise an error in the job queue: the error is saved in *Last Error*, *Consecutive Failures* counts the failed
polls in a row (reset by the next successful poll), and the next scheduled run tries again. Only setup errors (add-in
not loaded, a required field blank) end the job queue run with an error (with a blank *Parameter String*: only when
every mailbox failed that way). Failures of single e-mails never raise an error. **Poll Now** shows every error.
Monitor *Last Error*, *Consecutive Failures* and *Mails with Errors* on the mailbox card, or the list
**Graph Inbound Mails** filtered on *Status* = Error.

### Attachment storage

| *Attachment Storage* | Where the content goes |
|---|---|
| Database (BLOB) | Up to *Max. BLOB Size (KB)* (default 4096, 0 = no limit) into the BLOB of Table 50402. Larger attachments go to object storage when *Storage Profile Code* is set, otherwise only the metadata is stored (*Status* = Skipped (Too Large)). |
| Object Storage | Always to the *Storage Profile Code* (required). The add-in streams Graph → Azure/S3; nothing passes through NAV memory. Key: `{Key Prefix}{mailbox}/{yyyy}/{MM}/{dd}/{mail entry no.}/{file name}` |
| Server Folder | `{Server Folder Path}\{mailbox}\{yyyy}\{MM}\{mail entry no.}\{file name}` on the NAV Server (the service account needs write access) |

Inline images are skipped by default (*Skip Inline Images*). Cloud links (reference attachments) are stored as
metadata only (*Status* = Skipped (Link)). Users open any attachment with **Open Attachment** (Codeunit 50403
`OpenAttachment` – BLOB, storage or server file, Windows and Web client); **Open Download Link** opens a
time-limited S3 pre-signed URL / Azure SAS link (object storage only, not in Entra ID mode).

### Installation (v0.2)

Prerequisites: v0.1 installed (Codeunit 50400 and the Table 409 fields 50100..50112) and a NavMailGraph.dll build that
contains the v0.2 types (`GraphConnection`, `GraphMailbox`, `GraphMailItem`, `GraphMailAttachmentInfo`,
`ObjectStorageClient`, `GraphMailSender.UseLinkStorage`) in the Add-ins folders of the C/SIDE machine **and** the
NAV Server (same steps 2–3 as above; AssemblyVersion stays 1.0.0.0, so no object references change).

1. Back up the database.
2. **Table 409**: add fields 50113..50115 ([patches/TAB409.md](patches/TAB409.md#v02-nmg200-fields-5011350115--large-attachments-as-download-link)).
   Their TableRelation points to Table 50403, so import the new tables first (step 3) and compile Table 409 afterwards.
3. **Import** all new objects in one go (`File → Import`, select the files, or concatenate them into one TXT):
   `TAB50400..TAB50403`, `COD50401..COD50404`, `PAG50400..PAG50408` and the updated `COD50400.TXT` (replaces the
   v0.1 version). The objects reference each other (e.g. Codeunit 50400 → 50402 → Table 50403 → 50402), so compile
   them **together** afterwards: Object Designer → filter *Version List* = `*NMG2.00*` → select all → Compile (F11).
4. **Page 409**: add the three link fields ([patches/PAG409.md](patches/PAG409.md#v02-nmg200-link-fields-5011350115)), compile.
5. Compile Codeunit 400, 412, 9520 and Page 409 once more, then **restart the NAV Server instance** (new DLL).

The text files are CP850 + CRLF like `COD50400.TXT` (see [Text file encoding](#text-file-encoding)); this applies to
**all** `objects/*.TXT`. Before shipping, run the static check: `python3 nav/tools/check_cal.py` (no output errors).

### Setup walkthrough: reading a mailbox

1. **Permissions** (Exchange Online RBAC for Applications, see [../docs/exchange-rbac.md](../docs/exchange-rbac.md)):
   assign `Application Mail.ReadWrite` for the polled mailbox to the same app as for sending (needed for move,
   mark as read, delete and creating the Processed/Error folders). Read-only setups (*Polling Mode* = Delta Query and
   *After Import* = Nothing) need only `Application Mail.Read`. Role changes can take 30 minutes to 2 hours.
2. Credentials: the Graph fields of the **SMTP Mail Setup** (Page 409) are used – nothing to enter again. Test them
   there first (*Test Graph Connection*).
3. Open **Graph Mailbox Setup** (Page 50400) → New. Enter *Code* (e.g. `INVOICES`), *Mailbox*
   (`invoices@contoso.com`), *Folder* (`Inbox`, or a path like `Inbox/Invoices`), *After Import*, *Attachment
   Storage*, and for the first run *Import From Date* (otherwise the whole folder is read, *Max. Mails per Run* per run).
4. **Test Connection**: gets a token, resolves the folder and creates the Processed/Error folders.
5. **Poll Now** once, check **Inbound Mails**, then set *Enabled* and choose **Schedule Polling**: creates a recurring
   Job Queue Entry (Codeunit 50401, *Parameter String* = mailbox code, every *Polling Interval (Min.)* minutes, all
   weekdays, 3 attempts, 60 s rerun delay) through Codeunit 453 `EnqueueJobQueueEntry`. A job queue (NAS) must run
   for the company. Instead of one entry per mailbox you can create one entry with a blank *Parameter String* that
   polls all enabled mailboxes.
6. **Reset Sync State** clears the watermark and the delta link (already imported mails are skipped).

Working with imported mails (Page 50401/50402): **Show Body**, **Download Body**, **Export as .eml** (original MIME
from the mailbox), **Show in Outlook** (web link), **Mark as Processed**, **Reprocess**. *Reprocess* runs
`OnAfterImportMail` again and then the mailbox action (e.g. moves the mail from Error to Processed). For an e-mail
whose **import** failed (*Import Failed* = Yes, no attachments in NAV) it moves the mail back to the source folder,
lowers the watermark (delta mode: restarts the delta sync), deletes the NAV entry, and the next poll imports it again.

### Setup walkthrough: object storage

Open **Object Storage Profiles** (Page 50404) → New, fill the card (Page 50407), then **Test Connection** (list call)
and **Upload Test File** (upload, size check, delete – proves write/read/delete rights).

| Provider | Fields | Secret |
|---|---|---|
| Azure Blob Shared Key | *Account Name*, *Container / Bucket* (container) | storage account key |
| Azure Blob SAS URL | – | container SAS URL `https://<account>.blob.core.windows.net/<container>?sv=...` (needs r, w, d, l permissions) |
| Azure Blob Entra ID | *Account Name*, *Container / Bucket* | none – uses the Graph app of Table 409; assign it the role *Storage Blob Data Contributor* on the container. Download links are not possible in this mode |
| Amazon S3 | *Container / Bucket* (bucket), *Region* (default `us-east-1`), *Access Key ID*, *Service URL* (blank for AWS; `https://...` for MinIO, Wasabi, Cloudflare R2, SeaweedFS ...), *S3 Addressing* (Automatic = path style for non-AWS endpoints) | secret access key |

*Key Prefix* (e.g. `nav/prod/`) is put in front of every object key by the add-in; do not change it after attachments
were stored (the stored keys are relative to it). The secret is saved in Table 1261 "Service Password" (encrypted when
the database has an encryption key, like the Graph client secret); the card shows only `**********`. For Azurite use
*Service URL* `http://127.0.0.1:10000/devstoreaccount1`.

For your own code, Codeunit 50402 "Object Storage Mgt." offers `UploadFile`, `UploadBlob(VAR TempBlob)`,
`DownloadToBlob`, `DownloadToFile`, `DownloadToClient`, `Delete`, `Exists`, `GetDownloadUrl`, `TestConnection` and
`Try...` variants (`TryUploadFile`, `TryUploadBlob`, `TryDownloadToBlob`, `TryDelete`, `TryExists`,
`TryGetDownloadUrl`) that return a Boolean and leave the error in `GetLastError`:

```
IF NOT ObjectStorageMgt.TryUploadBlob('ARCHIVE',TempBlob,'invoices/2026/' + FileName,'application/pdf') THEN
  ERROR(ObjectStorageMgt.GetLastError);
```

**Large attachments as link when sending:** set *Graph Link Threshold (MB)* and *Graph Link Storage Profile* in the
SMTP Mail Setup (Table 409 fields 50113..50115). Attachments of at least that size are uploaded and replaced by a
download link valid for *Graph Link Validity (Days)* (S3: max. 7 days).

### Events and a subscriber example

| Event (Codeunit 50401) | When | Transaction |
|---|---|---|
| `OnBeforeImportMail(MailboxSetup : Record 50400;MailItem : DotNet NavMailGraph.GraphMailItem;VAR Skip : Boolean)` | before an e-mail is imported; `Skip := TRUE` ignores it | import transaction |
| `OnAfterImportAttachment(VAR InboundAttachment : Record 50402)` | after each attachment was stored | import transaction (an error fails the import) |
| `OnAfterImportMail(VAR InboundMail : Record 50401)` | after the e-mail and its attachments were committed | own transaction; an error sets *Status* = Error |

Rules for `OnAfterImportMail` subscribers: they run in the job queue (no UI – no `CONFIRM`, `MESSAGE`, `PAGE.RUN`);
raise an `ERROR` to mark the e-mail as failed (it is then moved to the error folder); change the **VAR record** to
set *Status*, *Processed At* or *Related Record ID* but do **not** `MODIFY` it yourself (the runner saves it). Get the
attachment content with Codeunit 50403 `GetAttachmentContent(InboundAttachment,TempBlob)` (TEMPORARY TempBlob) or
`SaveAttachmentToServerFile`, independent of where it is stored. Body text: `GetBodyPlainText(InboundMail)`.

Example (a separate codeunit of your own, e.g. 50410): create a purchase invoice stub for every e-mail with a PDF
in mailbox `INVOICES` whose sender address is a vendor's *E-Mail*.

```
OBJECT Codeunit 50410 Graph Mail Purch. Inv. Stub
{
  OBJECT-PROPERTIES
  {
    Date=12.10.26;
    Time=12:00:00;
    Version List=;
  }
  PROPERTIES
  {
  }
  CODE
  {
    VAR
      NoVendorErr@1000 : TextConst 'DEU=Kein Kreditor mit der E-Mail-Adresse %1.;ENU=No vendor with e-mail address %1.';
      NoPdfErr@1001 : TextConst 'DEU=Die E-Mail hat keinen PDF-Anhang.;ENU=The e-mail has no PDF attachment.';

    [EventSubscriber(Codeunit,50401,OnAfterImportMail)]
    PROCEDURE CreatePurchInvoiceStub@1(VAR InboundMail@1000 : Record 50401);
    VAR
      Vendor@1001 : Record 23;
      PurchaseHeader@1002 : Record 38;
      InboundAttachment@1003 : Record 50402;
      TempBlob@1004 : TEMPORARY Record 99008535;
      InboundMailMgt@1005 : Codeunit 50403;
    BEGIN
      IF InboundMail."Mailbox Code" <> 'INVOICES' THEN
        EXIT;
      IF InboundMail.Status <> InboundMail.Status::Imported THEN
        EXIT;

      Vendor.SETRANGE("E-Mail",InboundMail."From Address");
      IF NOT Vendor.FINDFIRST THEN
        ERROR(NoVendorErr,InboundMail."From Address");

      InboundAttachment.SETRANGE("Mail Entry No.",InboundMail."Entry No.");
      InboundAttachment.SETFILTER("File Name",'@*.pdf');
      InboundAttachment.SETRANGE(Status,InboundAttachment.Status::Stored);
      IF NOT InboundAttachment.FINDFIRST THEN
        ERROR(NoPdfErr);
      InboundMailMgt.GetAttachmentContent(InboundAttachment,TempBlob);
      // TempBlob.Blob now holds the PDF: e.g. store it as Incoming Document attachment or send it to OCR.

      PurchaseHeader.INIT;
      PurchaseHeader."Document Type" := PurchaseHeader."Document Type"::Invoice;
      PurchaseHeader.INSERT(TRUE);
      PurchaseHeader.VALIDATE("Buy-from Vendor No.",Vendor."No.");
      PurchaseHeader."Posting Description" := COPYSTR(InboundMail.Subject,1,MAXSTRLEN(PurchaseHeader."Posting Description"));
      PurchaseHeader.MODIFY(TRUE);

      // No MODIFY here: Codeunit 50404 saves InboundMail after all subscribers ran.
      InboundMail.Status := InboundMail.Status::Processed;
      InboundMail."Processed At" := CURRENTDATETIME;
      InboundMail."Related Record ID" := PurchaseHeader.RECORDID;
    END;

    BEGIN
    END.
  }
}
```

The example is not part of the delivered objects and not compiled; the Vendor/Purchase Header field names are the
standard NAV 2016 ones (Table 23 *E-Mail*, Table 38 *Buy-from Vendor No.*, *Posting Description*). Subscribers of one
event run in random order (NAV 2016); if you have several, filter on *Mailbox Code* in each.

## Known limitations / to verify in C/SIDE

* Not compiled in CI (see top).
* `FileManagement.StripNotsupportChrInFileName`, `MailManagement.CheckValidEmailAddresses`,
  `ServicePassword.SavePassword/GetPassword` are used with the signatures documented for NAV 2016 CU1; later
  CUs or customer modifications may differ.
* Page 409: the trigger that computes `CanSendTestMail` is not reproduced in the patch; find it in your object.
* Codeunit 400 subscriber `HandleSMTPRegisterServiceConnection`: the patch describes the condition to extend but
  does not quote Microsoft's statement.
* Date format in `OBJECT-PROPERTIES` (see "Text file encoding" above).

v0.2 objects – written against the DESIGN-v2 API and checked with `tools/check_cal.py` only; points that could not be
verified from a NAV 2016 export:

* `CREATEOUTSTREAM(OutStr,TEXTENCODING::UTF8)` / `CREATEINSTREAM(InStr,TEXTENCODING::UTF8)` for the body BLOB.
  The encoding parameter exists in NAV 2016 (Table 99008535 `ReadAsText` passes one), the `TEXTENCODING::UTF8`
  constant syntax was not found in an export. If it does not compile, drop the parameter (umlauts outside CP850 are
  then lost in the stored body).
* Page properties `MultiLine=Yes` and `ShowCaption=No` (body field on Pages 50402/50408) were not found in the
  reference exports.
* A DotNet parameter in an `[Integration]` publisher (`OnBeforeImportMail`, MailItem). If C/SIDE rejects it, remove
  the parameter (subscribers then only get the mailbox setup).
* The runner pattern `Runner.SetImportContext(...); IF Runner.RUN(InboundMail) THEN` relies on a codeunit variable
  keeping its globals for `RUN` (standard C/AL behaviour, e.g. `IF JobQueueSendNotification.RUN(Rec) THEN` in Table 472).
* `EVALUATE(DateTime,'2026-10-12T08:00:00Z',9)` for the Graph timestamps (only community sources); on failure the
  DateTime fields stay blank – the watermark uses the text value, so polling is not affected.
* Text filters on SQL Server are case-insensitive while Graph ids are case-sensitive: the duplicate check compares the
  ids exactly after the filter. Ids longer than 250 characters are cut (not seen with immutable ids).
* When the import of an e-mail fails, the objects / server files already written for it are deleted again (best
  effort: if that delete fails too, they stay under `{mailbox}/{date}/{entry no.}/`, an entry no. that no Table 50402
  record uses). Deleting NAV entries (also **Reprocess** of a failed import) does not delete stored objects or server
  files.
* *Database (BLOB)* keeps attachments in NAV Server memory while they are copied (BLOB streams are memory streams);
  keep *Max. BLOB Size (KB)* small and use object storage for large files.
* Job queue: only setup errors end the run with an error; after *Maximum No. of Attempts to Run* the NAV 2016 job
  queue sets the entry to Error and it stops recurring. Restart it (*Set Status to Ready* or **Schedule Polling**)
  after fixing the cause. **Schedule Polling** does not touch an entry that is *In Process*.
