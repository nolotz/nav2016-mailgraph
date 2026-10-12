# NavMailGraph v2 – Design: reading/polling, large attachments, object storage

Status: implementation contract for v0.2. Extends `DESIGN.md` (whose rules still apply: zero NuGet dependencies in the
core, AssemblyVersion pinned at 1.0.0.0, C/AL-friendly public API, methods return `""` on success or an error text and
never throw for remote/IO problems, secrets never in logs). Facts and sources: `research-notes.md` (v1) and
`research-notes-v2.md` (v2). Where research-notes-v2 contradicts this draft, research wins – fix the draft.

## 1. Goals

1. **Read and poll mailboxes** from NAV (job queue), import mails + attachments into NAV tables, mark/move processed mails.
2. **Large attachments everywhere**: receive attachments of any size Graph allows (streamed `$value` download, never
   base64 in memory), send large attachments (existing draft/upload flow) or – optionally – upload them to object
   storage and send a time-limited download link instead.
3. **Object storage**: Azure Blob Storage (Shared Key, SAS URL, Entra ID app) and Amazon S3 / S3-compatible storage
   (SigV4 access keys, path-style for MinIO etc.) with streamed upload/download, block/multipart uploads for large files,
   list, delete, exists and pre-signed / SAS download links.
4. **Simple in NAV**: one setup for credentials (the Graph fields on Table 409 are reused), a mailbox card, a storage
   profile card, a job queue entry, an inbound mail list with "open attachment", and one integration event that
   partners subscribe to for processing (e.g. create purchase invoices from PDFs).

Non-goals: Graph change notifications/webhooks (need a public HTTPS endpoint, NAV 2016 on-prem has none – polling is the
right tool), calendar/contacts, IMAP/POP, client-side encryption.

## 2. .NET public API (additions; all in namespace NavMailGraph)

C/AL rules from DESIGN.md §4 apply: simple types only, no generics, no optional params, no async, no same-arity overloads,
index-based collections (`Count` + `GetX(int index)`), DateTime values exposed both as `System.DateTime` (UTC) and as ISO text.

### 2.1 Shared connection configuration

To avoid configuring credentials three times, add a public `GraphConnection` that holds tenant, client, credential, cloud,
token library, timeouts, retries and log path. `GraphMailSender` keeps its existing members (append-only) and gains
`UseConnection(GraphConnection connection)`; `GraphMailbox` and `ObjectStorageClient` (Entra mode) accept the same object.

```csharp
public sealed class GraphConnection
{
    public GraphConnection(string tenantId, string clientId);
    public void UseClientSecret(string clientSecret);
    public void UseCertificateThumbprint(string thumbprint);
    public void UseCertificateFile(string pfxPath, string pfxPassword);
    public void UseTokenProvider(ITokenProvider provider);
    public string TokenLibrary { get; set; }   // "BuiltIn" | "Msal"
    public string Cloud { get; set; }          // Global | USGov | USGovDoD | China
    public string GraphBaseUrl { get; set; }
    public string AuthorityHost { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MaxRetries { get; set; }
    public string LogFilePath { get; set; }
    public string TestConnection();            // "" or error
}
```

### 2.2 Reading mail: `GraphMailbox`, `GraphMailItem`, `GraphMailAttachmentInfo`

```csharp
public sealed class GraphMailbox
{
    public GraphMailbox(GraphConnection connection, string mailbox);   // UPN/SMTP or object id
    public string Folder { get; set; }              // default "inbox"; well-known name, folder id, or path "Inbox/NAV/Invoices"
    public bool UnreadOnly { get; set; }            // default true
    public System.DateTime ReceivedAfterUtc { get; set; }  // default DateTime.MinValue = no filter
    public int MaxMessages { get; set; }            // default 50 per Fetch, upper bound 1000
    public bool IncludeBody { get; set; }           // default true
    public bool BodyAsText { get; set; }            // default false: body as sent (HTML or text); true: Graph converts to text
    // polling
    public string Fetch();                          // loads up to MaxMessages matching messages (oldest first); "" or error
    public string FetchChanges(string deltaLink);   // delta query; "" deltaLink = initial sync; afterwards read NextDeltaLink
    public string NextDeltaLink { get; }            // persist in NAV, pass to the next FetchChanges
    public int Count { get; }
    public GraphMailItem GetMessage(int index);     // 0-based
    public int RemovedCount { get; }                // delta: ids removed since last sync
    public string GetRemovedId(int index);
    // actions (all return "" or error)
    public string MarkAsRead(string messageId);
    public string MarkAsUnread(string messageId);
    public string MoveToFolder(string messageId, string folder);   // creates the folder path if missing; sets LastMovedMessageId
    public string LastMovedMessageId { get; }
    public string DeleteMessage(string messageId);                  // moves to Deleted Items (Graph DELETE)
    public string SaveMimeToFile(string messageId, string filePath);// .eml
    // attachments (streamed via $value, any size)
    public string SaveAttachmentToFile(string messageId, string attachmentId, string filePath);
    public string SaveAttachmentToStream(string messageId, string attachmentId, System.IO.Stream target);
    public string CopyAttachmentToStorage(string messageId, string attachmentId, ObjectStorageClient storage, string objectKey);
    // diagnostics
    public string LastRequestId { get; }
    public string LastDiagnostics { get; }
}

public sealed class GraphMailItem
{
    public string Id { get; }                 // immutable id (Prefer: IdType="ImmutableId") – stable across moves
    public string InternetMessageId { get; }
    public string ConversationId { get; }
    public string Subject { get; }
    public string FromAddress { get; }
    public string FromName { get; }
    public string SenderAddress { get; }
    public string ToRecipients { get; }       // "a@x.de; b@y.de"
    public string CcRecipients { get; }
    public string ReplyTo { get; }
    public System.DateTime ReceivedUtc { get; }
    public string ReceivedText { get; }       // ISO 8601 as returned by Graph
    public System.DateTime SentUtc { get; }
    public bool IsRead { get; }
    public string Importance { get; }
    public bool HasAttachments { get; }
    public string Body { get; }
    public bool BodyIsHtml { get; }
    public string BodyPreview { get; }
    public string WebLink { get; }
    public int AttachmentCount { get; }       // metadata loaded with the message (no content)
    public GraphMailAttachmentInfo GetAttachment(int index);
}

public sealed class GraphMailAttachmentInfo
{
    public string Id { get; }
    public string Name { get; }
    public string ContentType { get; }
    public long Size { get; }                 // Graph size (approximate for item attachments)
    public bool IsInline { get; }
    public string ContentId { get; }
    public string Kind { get; }               // "file" | "item" | "reference"
    public string SuggestedFileName { get; }  // sanitized for Windows; item attachments get ".eml"
}
```

Graph rules (verify in research-notes-v2): list via `/users/{mb}/mailFolders/{folder}/messages` with `$select`,
`$filter=isRead eq false [and receivedDateTime ge ...]`, `$orderby=receivedDateTime asc` only if the filter/orderby combination
is allowed, `$top`, follow `@odata.nextLink` until MaxMessages; attachments metadata via `$expand=attachments($select=...)`
or a separate `GET .../attachments?$select=id,name,contentType,size,isInline` (never pull contentBytes in lists); content via
`GET .../attachments/{id}/$value` streamed with `HttpCompletionOption.ResponseHeadersRead`; `Prefer: IdType="ImmutableId"` on all calls.
Permissions: reading = Mail.Read; MarkAsRead/Move/Delete/create folder = Mail.ReadWrite (Exchange RBAC roles
`Application Mail.Read` / `Application Mail.ReadWrite`). Error hints mention the role.

### 2.3 Object storage: `ObjectStorageClient`

```csharp
public sealed class ObjectStorageClient
{
    public ObjectStorageClient();
    // exactly one of these configures the provider
    public void UseAzureBlobSharedKey(string accountName, string accountKey, string container);
    public void UseAzureBlobSasUrl(string containerSasUrl);              // https://acct.blob.core.windows.net/container?sv=...
    public void UseAzureBlobEntra(GraphConnection connection, string accountName, string container); // token scope https://storage.azure.com/.default
    public void UseS3(string serviceUrl, string region, string bucket, string accessKeyId, string secretAccessKey);
    // options
    public string BlobEndpointSuffix { get; set; }   // default "blob.core.windows.net"; Azurite: set ServiceUrl instead
    public string ServiceUrl { get; set; }           // Azure: override account URL (Azurite http://127.0.0.1:10000/devstoreaccount1); S3: endpoint
    public bool S3PathStyle { get; set; }            // default false for AWS, true recommended for MinIO/custom endpoints
    public string S3SessionToken { get; set; }
    public string KeyPrefix { get; set; }            // prepended to every object key, e.g. "nav/prod/"
    public long MultipartThresholdBytes { get; set; }// default 64 MiB → block / multipart upload above
    public int PartSizeBytes { get; set; }           // default 8 MiB (S3 min 5 MiB)
    public int TimeoutSeconds { get; set; }          // per request, default 300
    public int MaxRetries { get; set; }              // default 3 (429/500/503 – idempotent operations only)
    public string LogFilePath { get; set; }
    // operations: "" on success or error text
    public string UploadFile(string filePath, string objectKey, string contentType);    // contentType "" → by extension
    public string UploadStream(System.IO.Stream source, string objectKey, string contentType);
    public string UploadText(string text, string objectKey, string contentType);
    public string DownloadToFile(string objectKey, string filePath);
    public string DownloadToStream(string objectKey, System.IO.Stream target);
    public string Delete(string objectKey);          // missing object = success
    public bool Exists(string objectKey);            // false + LastError on failure
    public long GetSize(string objectKey);           // -1 when missing/failure
    public string List(string prefix);               // fills ListCount/GetListedKey/GetListedSize
    public int ListCount { get; }
    public string GetListedKey(int index);
    public long GetListedSize(int index);
    public string GetDownloadUrl(string objectKey, int validMinutes); // S3 presigned GET; Azure: service SAS (shared key) or the SAS URL; "" + LastError if impossible
    public string TestConnection();                  // list with max 1 result
    public string LastError { get; }
    public string LastDiagnostics { get; }
    public string Describe();                        // no secrets
}
```

Implementation notes: Azure Shared Key signing and service SAS exactly per research-notes-v2; `x-ms-version` constant in one place;
Put Blob for ≤ threshold, Put Block + Put Block List for larger (block ids equal length, base64); S3 SigV4 header auth with
`x-amz-content-sha256` (hash streamed from file for single PUT, or `UNSIGNED-PAYLOAD` over HTTPS – choose per research),
multipart above threshold with abort on failure, presigned GET via query auth; object keys URI-encoded per segment;
responses streamed (`ResponseHeadersRead`) to file/stream with a fixed 81920-byte buffer. Unit tests use published AWS SigV4
test vectors and Azure signing examples; integration tests run against **Azurite** and **MinIO** (docker) when env vars
`NMG_AZURITE=1` / `NMG_MINIO=1` are set (CI ubuntu job uses service containers).

### 2.4 Sending: large attachment as link (optional)

`GraphMailSender` additions:
```csharp
public void UseLinkStorage(ObjectStorageClient storage, long thresholdBytes, int validMinutes, string linkText);
// attachments with size >= thresholdBytes are uploaded under "mail-attachments/{yyyy}/{MM}/{guid}/{fileName}" and
// replaced by a link line appended to the body (HTML <a> or text). linkText "" → "Download {0} ({1})".
public void ClearLinkStorage();
```
`GraphMailMessage.AddAttachmentFromStorage(ObjectStorageClient storage, string objectKey, string fileName)` – downloads to a temp
file at Send time and attaches (useful for archived documents).

## 3. NAV 2016 objects (C/AL)

Credentials come from the Graph fields in Table 409 (one app registration). New objects (customer range, adjust to licence):

| Object | Name | Purpose |
|---|---|---|
| Table 50400 | Graph Mailbox Setup | one line per polled mailbox |
| Table 50401 | Graph Inbound Mail | imported mails (header + body BLOB) |
| Table 50402 | Graph Inbound Attachment | attachments: BLOB, object storage key, or server file |
| Table 50403 | Object Storage Profile | Azure Blob / S3 connection, secret in Service Password |
| Page 50400 | Graph Mailbox Setup (List, editable) | actions: Poll Now, Test Connection, Create Job Queue Entry |
| Page 50401 | Graph Inbound Mails (List) | actions: Open/Download attachment, Show Body, Mark Processed, Reprocess, Show in Outlook (web link) |
| Page 50402 | Graph Inbound Mail (Card) + Page 50403 Attachments (ListPart) | |
| Page 50404 | Object Storage Profiles (List, editable) | secret entry (masked), Test Connection, Upload test file |
| Codeunit 50401 | Graph Mailbox Polling | `OnRun` (TableNo 472 Job Queue Entry; Parameter String = mailbox code or blank for all), `PollMailbox(Code)`, events |
| Codeunit 50402 | Object Storage Mgt. | simple facade: UploadFile, UploadBlob(TempBlob), DownloadToBlob, DownloadToClient, Delete, Exists, GetDownloadUrl, Try* variants |
| Codeunit 50403 | Graph Inbound Mail Mgt. | open/download attachments, export .eml, body to text, helper for subscribers |

Mailbox setup fields: Code, Description, Mailbox, Folder (default Inbox), Processed Folder (e.g. "Inbox/Processed"; blank = keep),
After Import (Option: Mark as Read, Move to Processed Folder, Delete, Nothing), Unread Only, Max Mails per Run, Import Body,
Attachment Storage (Option: Database (BLOB), Object Storage, Server Folder), Storage Profile Code, Server Folder Path,
Max BLOB Size (MB) (larger → object storage if a profile exists, else error), Skip Inline Images, Enabled, Last Poll At,
Last Error (Text250), Mails Imported (FlowField count).

Polling algorithm per mailbox (codeunit 50401): Fetch → for each mail: skip if a Graph Inbound Mail with the same Message ID
exists (idempotent re-runs); insert header + body; for each attachment save per storage option (BLOB via TempBlob OutStream →
`SaveAttachmentToStream`; object storage via `CopyAttachmentToStorage` with key `<mailbox code>/<yyyy>/<MM>/<entry no>/<file>`;
server folder via `SaveAttachmentToFile`); COMMIT per mail; raise `OnAfterImportMail(VAR InboundMail)` (integration event)
inside a codeunit run so a failing subscriber marks only that mail Status=Error with the error text; then After Import action.
Errors per mail never stop the whole run; the run ends with Last Error set if any mail failed (job queue log shows it).

Events (NAV 2016 integration events): `OnBeforeImportMail(MailboxSetup, MailItem DotNet, VAR Skip)`,
`OnAfterImportMail(VAR InboundMail)`, `OnAfterImportAttachment(VAR InboundAttachment)`.

Table 409 additions for links: 50113 "Graph Link Storage Profile" (Code20, TableRelation Object Storage Profile),
50114 "Graph Link Threshold (MB)" (Integer, 0 = off), 50115 "Graph Link Validity (Days)" (Integer, default 7).

## 4. Docs, scripts, tests

* docs/reading-mail.md, docs/object-storage.md (Azure: storage account, container, RBAC role "Storage Blob Data Contributor" for
  Entra mode, SAS creation; S3: IAM policy minimal JSON, MinIO/Wasabi/R2 examples), update README(s), troubleshooting, kundeninfo
  (Mail.Read/ReadWrite scope for reading).
* deploy/Grant-NavMailGraphMailboxScope.ps1: `-IncludeRead` (Application Mail.Read) in addition to `-IncludeReadWrite`.
* CLI: `nmg inbox list|get|save-attachment|mark-read|move`, `nmg storage put|get|ls|rm|url|test`.
* CI: ubuntu job with Azurite + MinIO service containers runs integration tests.

## 5. Revisions after research (2026-10-12) – OVERRIDE sections 2–4 where they differ

### 5.1 Reading / polling
* **Default processing mode = move**: after a mail is imported, NAV moves it to `<Folder>/Processed` (or `<Folder>/Error` when
  the import or a subscriber failed). The source folder is then a visible work queue. Needs Mail.ReadWrite.
* Polling query (list mode): `GET /users/{mb}/mailFolders/{folderId}/messages?$filter=receivedDateTime ge {watermark}&$orderby=receivedDateTime asc&$top={n}&$select=...`
  – receivedDateTime must be the first `$filter` property when `$orderby` is used (else `InefficientFilter`). Do **not** rely on
  `isRead eq false` as the primary mechanism (users opening the mail in Outlook would hide it). `UnreadOnly` stays available
  but is appended after the receivedDateTime clause (`receivedDateTime ge X and isRead eq false`) and defaults to **false**.
* Watermark is **text** (the exact Graph `receivedDateTime` string). `GraphMailbox.ReceivedAfterUtc` (DateTime) is replaced by
  `public string ReceivedAfter { get; set; }` (ISO 8601; "" = no filter). NAV stores the newest imported `ReceivedText` and passes it
  back; the query is inclusive (`ge`) and NAV de-duplicates by Graph id and internetMessageId.
* Read-only mode (Mail.Read only): `FetchChanges(deltaLink)` per folder with `changeType=created`, `Prefer: odata.maxpagesize`,
  `$select`; store the full deltaLink (BLOB in NAV); on HTTP 410 / `syncStateNotFound` return error code `DeltaExpired` (property
  `DeltaExpired` bool) so NAV restarts with `ReceivedAfter` bounded. Entries with `@removed` go to RemovedCount/GetRemovedId.
* Every Graph call sends `Prefer: IdType="ImmutableId"`. Ids are case-sensitive (NAV stores them in Text fields, never Code).
* Folder paths ("Inbox/Processed") are resolved segment by segment (well-known name for the first segment if it matches the list
  in research-notes-v2, else displayName under msgfolderroot; children via `/childFolders?$filter=displayName eq '...'`, quotes
  doubled), created on demand when the method says so; resolved ids cached per mailbox+path in the GraphMailbox instance.
  New method `public string EnsureFolder(string folderPath)` returns "" or error and sets `LastFolderId`.
* Attachment metadata: `GET .../messages/{id}/attachments?$select=id,name,contentType,size,isInline,lastModifiedDateTime`;
  the kind comes from the returned `@odata.type` annotation (not selectable). Defensive guard: if a metadata response contains
  `contentBytes`, fail with a clear error. Follow `@odata.nextLink`. Message list does **not** `$expand` attachments; metadata is
  loaded per message lazily on first `AttachmentCount` / `GetAttachment` access (one call per message that has attachments;
  `hasAttachments` is false for inline-only, so also load when the HTML body contains `cid:`).
* Content: `GET .../attachments/{aid}/$value` streamed (`ResponseHeadersRead`, own CancellationTokenSource covering the whole body
  read, 81920-byte copy). `referenceAttachment` → 405 → error text "reference attachments (links) cannot be downloaded; the link
  target is ..." (Kind = "reference"). `itemAttachment` → MIME (.eml).
* At most 4 concurrent requests per mailbox (static SemaphoreSlim keyed by mailbox); honour Retry-After on 429/503.
* `GraphMailItem` timestamps: `ReceivedText`/`SentText` (authoritative ISO UTC, e.g. `2026-10-12T08:00:00Z`) plus
  `ReceivedUtc`/`SentUtc` DateTime (Kind=Utc, parsed with InvariantCulture + AdjustToUniversal) for convenience. C/AL converts with
  `EVALUATE(dt, text, 9)`.
* Body: HTML as returned by default; `BodyAsText = true` sends `Prefer: outlook.body-content-type="text"`.
* Permission hints (wording as implemented in `ErrorHints.cs`): 403 on read → "needs Mail.Read (RBAC role 'Application Mail.Read')", on PATCH/move/delete/create folder →
  "needs Mail.ReadWrite (RBAC role 'Application Mail.ReadWrite')".

### 5.2 Large attachments → storage
* NAV attachment storage rule per mailbox: size ≤ "Max BLOB Size (KB)" (default 4096) → NAV BLOB (DLL streams into the C/AL
  OutStream); larger → object storage profile when configured (DLL streams Graph `$value` straight into Put Block / UploadPart with one
  reused 8 MiB buffer – no temp file, no C/AL stream); else metadata only with Status "Skipped (too large)".
* `CopyAttachmentToStorage` returns "" or error and fills `LastStorageKey`, `LastStorageSize` (long), `LastStorageETag`.
* Object key layout (default): `{KeyPrefix}{mailbox code}/{yyyy}/{MM}/{dd}/{entry no}/{sanitized file name}`.

### 5.3 Object storage
* Azure: `x-ms-version` default **2026-04-06** (property `AzureApiVersion` to override); Put Blob up to `MultipartThresholdBytes`,
  else Put Block (fixed-length base64 ids of a zero-padded 6-digit index) + Put Block List (`<Latest>`); Get Blob streamed;
  HEAD for Exists/GetSize (404 → not found); List Blobs XML with NextMarker loop (parse with XmlDocument/XDocument);
  Shared Key StringToSign per research (Content-Length empty when 0); service SAS for `GetDownloadUrl` with the 2020-12-06 layout
  (sv = AzureApiVersion), sp=r, sr=b, spr=https; SAS-URL mode uses the given SAS for all operations and `GetDownloadUrl` returns a
  blob URL with the same SAS; Entra mode (`UseAzureBlobEntra`) uses the connection's token provider with scope
  `https://storage.azure.com/.default` (+ cloud-specific suffix) – `GetDownloadUrl` in Entra mode returns an error ("needs shared key
  or SAS"; user delegation SAS is a later feature).
* S3: SigV4 header auth signing `host;x-amz-content-sha256;x-amz-date` (+ content-type, range, x-amz-security-token when present);
  own `UriEncode` (UTF-8, uppercase hex, `/` kept in keys), reject keys with `.`/`..` segments or backslashes; real SHA-256 for
  small bodies and every multipart part, `UNSIGNED-PAYLOAD` only for single streamed PUTs over HTTPS; never STREAMING-*;
  multipart (min 5 MiB parts, ≤ 10,000 parts, ETags from UploadPart, Complete with ascending parts, check `<Error>` in a 200
  Complete response, Abort on failure); presigned GET (≤ 604800 s); `S3PathStyle` default **true** whenever `serviceUrl` is not an
  `amazonaws.com` host or the bucket contains a dot; region default "us-east-1"; error XML (Code/Message/RequestId), `RequestTimeTooSkewed`
  hint "check the NAV server clock". Sign `new Uri(url).PathAndQuery` and assert it equals the canonical path.
* Unit tests: AWS published SigV4 vectors verbatim (see research-notes-v2), Azure StringToSign construction.
  Integration: Azurite `mcr.microsoft.com/azure-storage/azurite:3.37.0`; S3 via a pullable S3-compatible server (minio images are not
  pullable here; use `chrislusf/seaweedfs` or `rustfs/rustfs`, verify credentials and SigV4/multipart/presign support).
  `tests/integration/docker-compose.yml` + `tests/integration/README.md`; tests run when `NMG_INTEGRATION=1`.

### 5.4 NAV specifics
* Events in TXT exports: `[Integration]` attribute (or `[Integration(TRUE)]` for IncludeSender) above `PROCEDURE ...@id()`; subscribers
  `[EventSubscriber(Codeunit,50401,OnAfterImportMail)]`. Do not use GlobalVarAccess.
* Job queue: polling codeunit `TableNo=472`, `Rec."Parameter String"` = mailbox code; errors via ERROR (job queue retries/logs to 474).
  Setup action "Schedule Polling" creates a recurring Job Queue Entry (Codeunit 453 "Job Queue - Enqueue" `EnqueueJobQueueEntry`, verify
  name in research-notes-v2).
* Secrets of storage profiles via Service Password (Table 1261) like Table 1260.
* BLOB: INSERT/MODIFY before CALCFIELDS; TempBlob (Table 99008535) has no WriteAsText in 2016 → CREATEOUTSTREAM + WRITETEXT.
* Web-client downloads: `FileManagement.BLOBExport(TempBlob, FileName, TRUE)`.
* .NET collections: use Count + GetX(index) (no FOREACH dependency).
* Mailbox setup: per-mailbox processing option `After Import`: Move to Processed Folder (default), Mark as Read, Delete, Nothing (read-only
  delta mode). Delta link stored in a BLOB field "Delta Link". Watermark "Last Received (Text)" Text50 + display DateTime.
