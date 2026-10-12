# NAV 2016 objects and patches

This folder contains the Dynamics NAV 2016 (C/AL) side of nav2016-mailgraph: one new codeunit and
step-by-step patch instructions for three Microsoft objects. After the change, every caller of Codeunit 400
"SMTP Mail" (document mailing, reminders, report mailing, notification e-mails, customer code) sends through
Microsoft Graph `sendMail` when *Mail Transport* = *Microsoft Graph* in the SMTP Mail Setup. No caller
changes.

> **Not compiled in CI. Verify in C/SIDE.** The objects were written from the NAV 2016 CU1 (build 43402)
> object facts in [../docs/research-notes.md](../docs/research-notes.md) and checked only statically (block
> balance, IDs, field names). There is no NAV 2016 compiler in CI. Import, compile and test them in a NAV 2016
> development database before anything goes to a customer.

## Contents

| Path | What |
|------|------|
| [objects/COD50400.TXT](objects/COD50400.TXT) | **Codeunit 50400 "Graph Mail"** – new, complete C/AL text export (ours, MIT) |
| [patches/TAB409.md](patches/TAB409.md) | Table 409 "SMTP Mail Setup": fields 50100..50112 (fields only) |
| [patches/PAG409.md](patches/PAG409.md) | Page 409 "SMTP Mail Setup": Microsoft Graph group, masked secret, test actions |
| [patches/COD400.md](patches/COD400.md) | Codeunit 400 "SMTP Mail": globals + one delegation block per function |
| [tools/Find-SmtpCallers.ps1](tools/Find-SmtpCallers.ps1) | Scans exported .TXT objects for Codeunit 400 / Table 409 / "SMTP Server" usage |

The Microsoft objects are **not** redistributed. The patch files only show signature lines to locate the
insertion points plus the lines to insert.

### Object list

| Object | No. | Change | Version List |
|--------|-----|--------|--------------|
| Codeunit "Graph Mail" | 50400 | new | NMG1.00 |
| Table "SMTP Mail Setup" | 409 | 13 fields | append `,NMG1.00` |
| Page "SMTP Mail Setup" | 409 | group, 12 table fields + 2 variable fields, 2 actions, 1 function | append `,NMG1.00` |
| Codeunit "SMTP Mail" | 400 | 2 globals, delegation in 13 functions + subscriber | append `,NMG1.00` |

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

`objects/COD50400.TXT` is stored in the **OEM code page 850 (CP850) with CRLF**, the format C/SIDE (NAV 2016)
reads and writes for text objects, so the German captions import with correct umlauts. This is settled from
Microsoft's own NAV 2016 exports: e.g. `Page_0000000409.txt` in github.com/mouadhbh/NAV (commit `1848f41`)
stores the `é` of "Paramétrage" as byte `0x82`, which is `é` in CP850/437 and not in Windows-1252. The only
non-ASCII characters in our file are `ä` (`0x84`) and `ü` (`0x81`), which have the same bytes in CP437, so the
file also imports correctly on systems whose OEM code page is 437. On Windows with another OEM code page
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

## Known limitations / to verify in C/SIDE

* Not compiled in CI (see top).
* `FileManagement.StripNotsupportChrInFileName`, `MailManagement.CheckValidEmailAddresses`,
  `ServicePassword.SavePassword/GetPassword` are used with the signatures documented for NAV 2016 CU1; later
  CUs or customer modifications may differ.
* Page 409: the trigger that computes `CanSendTestMail` is not reproduced in the patch; find it in your object.
* Codeunit 400 subscriber `HandleSMTPRegisterServiceConnection`: the patch describes the condition to extend but
  does not quote Microsoft's statement.
* Date format in `OBJECT-PROPERTIES` (see "Text file encoding" above).
