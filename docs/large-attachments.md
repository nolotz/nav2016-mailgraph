# Large attachments

How the add-in moves big files in both directions without loading them into NAV memory, and which limits apply.
Object storage setup: [object-storage.md](object-storage.md). Reading concepts: [reading-mail.md](reading-mail.md).

* [Sending](#sending)
  * [sendMail and the draft flow](#sendmail-and-the-draft-flow)
  * [Link instead of attachment](#link-instead-of-attachment)
  * [Attachments from object storage](#attachments-from-object-storage)
* [Receiving](#receiving)
  * [Streamed download](#streamed-download)
  * [Where the content goes in NAV](#where-the-content-goes-in-nav)
  * [Straight from Graph into storage](#straight-from-graph-into-storage)
* [Limits at a glance](#limits-at-a-glance)

## Sending

### sendMail and the draft flow

Unchanged from v0.1 ([README](../README.md#limits)):

| Message | Path | Permission |
|---------|------|------------|
| estimated request incl. base64 ≤ 3.5 MiB (attachments up to about 2.5 MB in total) | one `POST /users/{mailbox}/sendMail` | Mail.Send |
| larger | draft → attachments below 3 MB by POST, from 3 MB by **upload session** (chunks ≤ 4 MiB) → send draft | Mail.Send + Mail.ReadWrite |

* Upload sessions take up to **150 MB per attachment** (`MaxAttachmentBytes`). They do not exist in the US
  Government L4/L5 clouds; there large attachments fail with a clear validation error.
* The **Exchange Online message size** limit (35 MB by default, admins can set 1–150 MB) still applies to the whole
  message. An upload that Graph accepts can still bounce as a non-delivery report, and the recipient's mail server
  may have a lower limit. Messages that leave Microsoft's data centres grow by about a third through encoding.
* With *Graph Large Messages* = No (`UseDraftForLargeMessages = false`) a too-large message fails with a validation
  error instead.

### Link instead of attachment

For files that are too big to mail reliably, the sender can upload them to object storage and put a time-limited
download link into the body instead.

```csharp
var storage = new ObjectStorageClient();
storage.UseS3("", "eu-central-1", "contoso-nav-mail", accessKeyId, secret);   // or UseAzureBlobSharedKey
storage.KeyPrefix = "nav/prod/";
sender.UseLinkStorage(storage, 10L * 1024 * 1024, 7 * 1440, "");              // >= 10 MiB, valid 7 days
string error = sender.Send(message);
sender.ClearLinkStorage();                                                    // back to normal attachments
```

In NAV: Table 409 fields *Graph Link Storage Profile*, *Graph Link Threshold (MB)* (0 = off) and *Graph Link
Validity (Days)* (default 7) on Page 409 ([../nav/patches/TAB409.md](../nav/patches/TAB409.md)). Codeunit 50400 then
calls `UseLinkStorage` for every mail sent through Graph.

What happens at `Send`:

1. Every **non-inline** attachment of `thresholdBytes` or more is a link candidate (inline images always stay
   attachments).
2. For each candidate the **link is created first** (local signing, no network), then the file is uploaded to
   `mail-attachments/{yyyy}/{MM}/{guid}/{file name}` below the storage's `KeyPrefix`. A mode that cannot create links
   (Azure Entra ID, S3 with more than 7 days) therefore fails the Send before anything is uploaded or sent.
3. The link replaces the attachment in the body:
   * HTML body: `<p><a href="...">Download report.pdf (20.0 MB)</a></p>` (URL and text HTML-escaped), inserted
     before the last `</body>` or appended;
   * text body: a blank line, then one `text: url` line per file.
4. The remaining message goes out through sendMail or the draft flow as usual. Link files do not count against the
   Graph 150 MB attachment limit or the Exchange message size; the storage limits apply instead.

`linkText` is a .NET format string: `{0}` = file name, `{1}` = size (1024-based, e.g. `20.0 MB`), `{2}` = link expiry
as `yyyy-MM-dd HH:mm UTC`; `""` means `Download {0} ({1})`. Write `{{` and `}}` for literal braces; an invalid format
text is a validation error. NAV passes `""`.

Failure handling:

* An upload or link error fails the whole Send with the error text ("Attachment 'x' (n bytes) could not be sent as a
  download link (UseLinkStorage): ..."); **no mail is sent**.
* If a later upload or the send itself fails, the objects uploaded so far are deleted (best effort). When the send
  outcome is unknown (timeout or HTTP 504 on sendMail), they are kept, because the mail may have been delivered.
* The caller's `GraphMailMessage` is never changed (the send works on a copy), so a repeated `Send` behaves the same.
* Signed URLs are never written to `LastDiagnostics` or the log file.

Security: a link works for everyone who has it until it expires, also after the mail is forwarded. Do not use an
Azure **SAS URL** profile as link storage – its links carry the container SAS with all its permissions. See
[object-storage.md](object-storage.md#security-notes).

### Attachments from object storage

`GraphMailMessage.AddAttachmentFromStorage(storage, objectKey, fileName)` attaches a file that already sits in
storage (an archived invoice, a scan):

* `fileName` `""` = the last segment of the key. An empty key, or a key ending in `/` without a file name, is a
  validation error reported by `Send`.
* At `Send` the object is downloaded to `{temp}/nmg-{guid}.tmp` on the NAV server and sent like a normal file
  attachment. The temp file is deleted afterwards, also when sending fails.
* A download error fails the Send: "Attachment 'x' could not be downloaded from object storage (key k): ...".
* Combined with link storage, a large storage attachment is downloaded and uploaded again as a link object.
* Until `Send` its size is unknown (`TotalAttachmentBytes` counts it as 0).

## Receiving

### Streamed download

* Attachment **metadata** comes from `GET .../messages/{id}/attachments?$select=id,name,contentType,size,isInline,lastModifiedDateTime`.
  `contentBytes` (base64) is never requested; should a response contain it anyway, the add-in stops with a clear
  error instead of loading it into memory.
* **Content** comes from `GET .../attachments/{id}/$value` with `HttpCompletionOption.ResponseHeadersRead` and is
  copied in 81920-byte steps. File attachments arrive as raw bytes, attached mails (item attachments) as MIME and are
  saved as `.eml`.
* The timeout is an **idle timeout** per read, so a 100 MB download over a slow line does not fail as long as data
  keeps flowing; a stalled connection still fails.
* **Reference attachments** (links to OneDrive/SharePoint files) have no content: Graph answers HTTP 405. The add-in
  returns "reference attachments (links to cloud files) have no content in Microsoft Graph (HTTP 405) ..." and NAV
  stores them as metadata with *Status* = *Skipped (Link)*.
* `SaveMimeToFile` saves the whole original mail as `.eml`.
* `GraphMailAttachmentInfo.Size` is the size Graph reports (approximate for item attachments); the bytes actually
  written are in `GraphMailbox.LastDownloadSize`.

### Where the content goes in NAV

Per mailbox, *Attachment Storage* decides ([../nav/README.md](../nav/README.md#attachment-storage)):

| Setting | Up to *Max. BLOB Size (KB)* (default 4096, 0 = no limit) | Larger |
|---------|-------------------------------------------------------------|--------|
| Database (BLOB) | NAV BLOB: `SaveAttachmentToStream` writes into the BLOB's OutStream | object storage when a *Storage Profile Code* is set, else metadata only with *Status* = **Skipped (Too Large)** |
| Object Storage | always object storage (`CopyAttachmentToStorage`) | same |
| Server Folder | a file below *Server Folder Path* on the NAV server (`SaveAttachmentToFile`) | same |

Why a BLOB cap: NAV BLOB streams are memory streams on the NAV server, so a 100 MB attachment can occupy that
memory more than once while it is copied. The 4 MB default is an engineering choice, not a Microsoft limit; raise it
only after a load test on your server. *Skip Inline Images* (default Yes) leaves out logos and signature images.

An attachment marked *Skipped (Too Large)* is not in NAV, but the mail still contains it: open the mail in Outlook
(*Show in Outlook*) or use *Export as .eml*, which saves the original mail including all attachments (both need the mail to still exist in the mailbox).

### Straight from Graph into storage

`GraphMailbox.CopyAttachmentToStorage(messageId, attachmentId, storage, objectKey)` pipes the attachment from Graph
into Azure Blob or S3:

* The key and the storage configuration are checked **before** the first Graph request.
* The `$value` response is read through an idle-timeout stream directly into the storage upload: a single PUT when
  it fits into one part, otherwise Put Block / S3 multipart with one reused part buffer (default 8 MiB). No temp
  file, never the whole file in memory, nothing passes through C/AL. Without a Content-Length from Graph the upload
  runs part by part with unknown total length.
* While a storage part uploads, the Graph idle timer is kept alive; a Graph read that stalls still times out.
* The content type comes from the Graph response, otherwise from the key's extension.
* Success: `LastStorageKey` (without `KeyPrefix` – what `DownloadToFile` and `GetDownloadUrl` expect),
  `LastStorageSize` and `LastStorageETag` are set. NAV stores them in Table 50402 (*Object Key*, *Stored Size*,
  *Storage ETag*).
* Failure: the three properties are cleared and the error text says which side failed:
  * Graph: "Microsoft Graph download of attachment X failed after N bytes: ...";
  * storage: the storage error with its own hint and request id (for example `AuthenticationFailed`, `NoSuchBucket`).
    Graph permission hints are not added to storage errors. `storage.LastError` and `storage.LastDiagnostics`
    describe the storage side.
* A failed S3 multipart upload is aborted. If NAV's import transaction fails **after** a successful upload, the
  object stays in storage (the NAV rollback cannot undo it); deleting NAV entries never deletes stored objects.

## Limits at a glance

| Limit | Value |
|-------|-------|
| sendMail request | 4 MB (add-in switches at an estimated 3.5 MiB incl. base64) |
| Attachment by direct POST / upload session | < 3 MB / 3–150 MB per attachment |
| Exchange Online message size | 35 MB send, 36 MB receive by default; 1–150 MB configurable |
| Graph upload volume per app and mailbox | 150 MB per 5 minutes |
| Graph `$value` download | no documented size limit |
| NAV BLOB field | 2 GB (practical cap: *Max. BLOB Size (KB)*) |
| Azure single Put Blob / block / blocks per blob | 5,000 MiB / 4,000 MiB / 50,000 (service version 2019-12-12+) |
| S3 single PUT / parts | 5 GB / 5 MiB–5 GiB per part, at most 10,000 parts |
| Add-in single-PUT threshold / part size | 64 MiB / 8 MiB (configurable) |
| S3 presigned link validity | at most 7 days |

Sources: [research-notes.md](research-notes.md) and [research-notes-v2.md](research-notes-v2.md).
