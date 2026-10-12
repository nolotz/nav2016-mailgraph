# Troubleshooting

## How errors reach you

* `GraphMailSender.Send` / `TestConnection` never throw for Graph, auth, I/O or validation problems. They return
  one error text. Codeunit 50400 shows it (Page 409 actions, `Send`) or returns it through `TrySend` /
  `GetLastSendMailErrorText` (Codeunit 9520 pattern), exactly like the SMTP path.
* The text contains the HTTP status, the Graph or Entra error code, the request ID / correlation ID and, when
  known, a `Hint:`. Examples:

  ```
  Microsoft Graph sendMail failed (HTTP 403, ErrorAccessDenied): Access is denied. ... [request-id 7d4f..., ...] Hint: ...
  Token acquisition failed (invalid_client, AADSTS7000215): Invalid client secret provided. ... [correlation-id ...] Hint: ...
  Validation failed: at least one recipient is required.
  ```

* Give the request ID / correlation ID to Microsoft support; it identifies the call in their logs.
* The same rules apply to the v0.2 classes: every `GraphMailbox` and `ObjectStorageClient` method returns `""` or
  one error text (`ObjectStorageClient` also keeps it in `LastError`). Storage errors carry the storage request id
  (`x-ms-request-id` / `x-amz-request-id`) and a storage hint, never a Graph hint. Examples:

  ```
  Microsoft Graph list messages failed (HTTP 403, ErrorAccessDenied): ... Hint: Reading mail and attachments needs the Mail.Read application permission ...
  S3 upload failed (HTTP 403, SignatureDoesNotMatch): ... [request-id ..., bucket contoso-nav-mail, key ...] Hint: The S3 server rejected the signature ...
  ```

* In NAV, mailbox-level errors of the polling codeunit go to *Last Error* on the mailbox card and the job queue log;
  errors of single mails to the *Error Text* of the mail in *Graph Inbound Mails* (*Status* = Error).

Reproduce outside NAV with the CLI on any machine: `nmg token` / `nmg send` / `nmg inbox` / `nmg storage` print
the same text plus the diagnostic log ([development-macos.md](development-macos.md#end-to-end-test-with-nmg)).

## Symptoms

### Token (Entra ID)

| Symptom / code | Cause | Fix |
|----------------|-------|-----|
| `AADSTS7000215` invalid client secret | Wrong secret, often the **Secret ID** instead of the **Value** | Enter the secret Value in the masked *Client Secret* field |
| `AADSTS7000222` client secret expired | Secret past its end date | New secret in the app registration, enter it in NAV, delete the old one |
| `AADSTS700016` application not found | Wrong *Graph Client ID*, or app in another tenant | Check Client ID and Tenant ID on the app's Overview page |
| `AADSTS90002` tenant not found | Typo in *Graph Tenant ID*, or wrong *Graph Cloud* | Use the tenant GUID or a verified domain; check the cloud |
| `AADSTS50059` / `AADSTS9001023` | Tenant missing or `common` / `organizations` used | Set the tenant GUID or verified domain; client credentials need a specific tenant |
| `AADSTS700027` client assertion failed signature validation | Certificate not uploaded to the app, or a different certificate in NAV | Upload the `.cer` that belongs to the thumbprint; check *Graph Cert. Thumbprint* |
| `AADSTS7000218` request must contain client_assertion or client_secret | No credential configured | Set *Graph Auth Method* and the secret or thumbprint |
| `Certificate with thumbprint ... not found in LocalMachine\My or CurrentUser\My` | Certificate not imported, or imported into the wrong store / user | `Import-PfxCertificate ... -CertStoreLocation Cert:\LocalMachine\My` ([deployment.md](deployment.md#certificate)) |
| "Keyset does not exist", access denied on the private key, `CryptographicException` when signing | NAV service account cannot read the private key | `certlm.msc` → Manage Private Keys → add the service account (Read; Full Control if Read is not enough) |
| "Invalid algorithm specified" | Key in a legacy CAPI provider that cannot sign SHA-256 | The add-in re-opens the key with the RSA/AES provider (type 24) automatically; if it persists, re-import the PFX (the install script handles CNG and CAPI keys) |
| Certificate warning "expired on ..." in the log | Certificate outside its validity period | Renew ([azure-setup.md](azure-setup.md#renewal)) |
| `Token acquisition failed: no response ... within 100 s` / transport error | Firewall, proxy, DNS | Allow `login.microsoftonline.com`; configure the proxy ([deployment.md](deployment.md#proxy)) |

### Sending (Microsoft Graph)

| Symptom / code | Cause | Fix |
|----------------|-------|-----|
| HTTP 403 `ErrorAccessDenied` | Mailbox outside the RBAC scope, no `Application Mail.Send` assignment, or the 30 min – 2 h permission cache after a change | `Test-ServicePrincipalAuthorization -Identity <sp> -Resource <mailbox>` must show `InScope True`; then wait for the cache ([exchange-rbac.md](exchange-rbac.md#permission-cache-30-minutes-to-2-hours)) |
| HTTP 403 in the draft flow (create draft, attachment, upload session) | `Application Mail.ReadWrite` missing; attachments > ~2.5 MB in total (request incl. base64 > 3.5 MiB) | Assign `Application Mail.ReadWrite` (`Grant-NavMailGraphMailboxScope.ps1 -IncludeReadWrite`), or set *Graph Large Messages* = No and keep messages small |
| HTTP 403 `ErrorSendAsDenied` | The From address differs from the mailbox and the mailbox has no Send As right for it | Set *Graph Sender Mailbox* (From becomes the mailbox, the user's address goes to Reply-To), or grant Send As |
| HTTP 404 `MailboxNotEnabledForRESTAPI` / `ResourceNotFound` | Mailbox address does not exist, has no Exchange Online mailbox (e.g. on-premises or unlicensed user), or typo | Check *Graph Sender Mailbox*; without it, the From address NAV passes must be an Exchange Online mailbox |
| HTTP 413 Request Entity Too Large / Payload Too Large | Request above Graph's 4 MB limit | Keep the defaults (`SendMailPayloadLimitBytes` 3.5 MiB, `DirectAttachmentLimitBytes` 3 MiB); enable *Graph Large Messages* |
| `Validation failed: the message is too large for a single sendMail request` | *Graph Large Messages* = No and attachments > ~2.5 MB in total (estimated request incl. base64 > 3.5 MiB) | Enable *Graph Large Messages* (needs Mail.ReadWrite) or send smaller attachments |
| `Attachment ... is too large` | Attachment above 150 MB | Not possible via Graph; reduce the file |
| Large attachment fails in US Government clouds | Upload sessions are not available in US Gov L4/L5 | Keep attachments below ~2.5 MB in total there |
| `ErrorAttachmentSizeShouldNotBeLessThanMinimumSize` | Upload session for a file below 3 MB | Do not set `DirectAttachmentLimitBytes` below 3 MB |
| HTTP 429 `TooManyRequests`, 503, 509 | Throttling or a temporary outage | The add-in retries up to 3 times honouring `Retry-After` (max 120 s per pause). Persistent 429: too many mails from one mailbox (30/min, Graph limits per app and mailbox); spread the load or use several mailboxes |
| `... The outcome is unknown: the message may already have been sent ...` (HTTP 504 or timeout on sendMail / send draft) | Graph or the gateway did not answer in time; the mail may or may not have gone out | Check the Sent Items folder of the sender mailbox before sending again. With Save to Sent Items off, ask the recipient. After a failed draft send the draft stays in Drafts |
| HTTP 401 | Token rejected | The add-in refreshes the token once automatically; persistent 401: wrong cloud (*Graph Cloud*) or wrong Graph URL |
| Send returns success, but the mail never arrives | `202 Accepted` only means accepted. Delivery errors arrive later as a **non-delivery report (NDR)** in the sender mailbox | Check the Inbox of the sender mailbox; check message trace in the Exchange admin center. Typical: invalid recipient, message above the 35 MB tenant limit, recipient rate limit (10,000/day), 100 external recipients/day limit for `*.onmicrosoft.com` sender domains (`550 5.7.236`) |
| No copy in Sent Items | *Graph Save to Sent Items* = No | Set Yes. (Large messages always land in Sent Items.) |

### Reading mail (Graph)

Concepts: [reading-mail.md](reading-mail.md).

| Symptom / code | Cause | Fix |
|----------------|-------|-----|
| HTTP 403 `ErrorAccessDenied` on list, delta, get, attachment or `.eml` | No `Application Mail.Read` / `Application Mail.ReadWrite` for this mailbox, mailbox outside the scope, or the 30 min – 2 h cache | `Grant-NavMailGraphMailboxScope.ps1 -Mailbox <mailbox> -IncludeRead` (read-only) or `-IncludeReadWrite`; check with `Test-ServicePrincipalAuthorization`. Mail.ReadBasic is not enough |
| HTTP 403 on mark as read, move, delete or creating the Processed/Error folder | Only Mail.Read granted | Grant `Application Mail.ReadWrite`, or switch the mailbox to read-only (*Polling Mode* = Delta Query, *After Import* = Nothing) |
| HTTP 404 `ResourceNotFound` / "mailbox, folder, message or attachment was not found" | Wrong mailbox address or folder path, or the mail was moved or deleted in the meantime | Check *Mailbox* and *Folder* (paths use `/`, first segment a well-known name like `Inbox` or a top-level folder); ids are case-sensitive |
| `MailboxNotEnabledForRESTAPI` | Mailbox does not exist in Exchange Online or has no licence/mailbox | Use an Exchange Online (shared) mailbox |
| HTTP 400 `InefficientFilter` ("The restriction or sort order is too complex for this operation") | A `$filter` that does not start with `receivedDateTime` while sorting by it | The add-in builds the right query; this only appears with modified query code. Keep `receivedDateTime` first |
| `Validation failed: ReceivedAfter '...' is not an ISO 8601 UTC date-time` | Watermark text changed by hand or converted through a DateTime | Store `ReceivedText` unchanged, e.g. `2026-10-12T08:00:00Z`; *Reset Sync State* clears it |
| Error text starting with `DeltaExpired:` (HTTP 410, `syncStateNotFound`, `syncStateInvalid`, `resyncRequired`) | Graph evicted the delta sync state (no fixed lifetime) | Nothing to do in NAV: Codeunit 50401 restarts the sync bounded by the watermark, the duplicate check skips known mails. Own code: call `FetchChanges("")` with `ReceivedAfter` set |
| "reference attachments (links to cloud files) have no content in Microsoft Graph (HTTP 405)" | The attachment is a OneDrive/SharePoint link, not a file | Expected; NAV stores it as *Skipped (Link)*. Open the mail in Outlook to follow the link |
| "Microsoft Graph list attachments returned attachment content (contentBytes) although only metadata was selected" | Graph ignored the `$select` | Report it (request id in the text); the add-in refuses to load base64 content into memory |
| "no free request slot for mailbox ... (at most 4 concurrent requests per mailbox)" | Several NAV sessions or job queue entries work on the same mailbox at once | Poll each mailbox from one job queue entry; lower parallel use |
| HTTP 429 / 503 on reading | Throttling (10,000 requests per 10 minutes, 4 concurrent per app and mailbox) | Retried automatically; if persistent, poll less often or lower *Max. Mails per Run* |
| Mails are imported twice | Should not happen: duplicates are skipped by Graph id and internetMessageId | Check that nobody deleted the NAV entries; an immutable id changes only when a mail goes to an archive mailbox or is re-imported |
| Mails are never imported although they are in the folder | *Unread Only* = Yes and the mail was opened in Outlook, the watermark is newer than the mail (a mail moved back into the folder can carry an older received date), or an `OnBeforeImportMail` subscriber skips it | Switch off *Unread Only*; *Reset Sync State* (known mails are skipped); check subscribers |
| Job queue entry stops recurring | Mailbox-level error raised *Maximum No. of Attempts to Run* times; the NAV 2016 job queue then sets *Status* = Error | Fix the cause (see *Last Error*), then *Set Status to Ready* |

### Object storage

Setup: [object-storage.md](object-storage.md). The NAV profile card has **Test Connection** (list) and **Upload
Test File** (upload, size, delete).

| Symptom / code | Cause | Fix |
|----------------|-------|-----|
| Azure HTTP 403 `AuthenticationFailed` (Azurite: `AuthorizationFailure` "Server failed to authenticate the request") | Wrong account name or key, key not copied completely, NAV server clock off by more than 15 minutes, or an expired SAS (`se`) | Copy key1/key2 again; check the server time; create a new SAS |
| Azure HTTP 401 `NoAuthenticationInformation` in Entra mode | No token was sent (no `Authorization` header) | Check the app credential on Page 409 and the token error before it in the diagnostics |
| Azure HTTP 401 `InvalidAuthenticationInfo` in Entra mode | A token was sent but rejected: wrong audience or tenant (e.g. a custom `UseTokenProvider` provider that returns Graph tokens instead of tokens for `https://storage.azure.com`), or expired; the error detail usually names the failed check (audience, issuer) | A `UseTokenProvider` provider must return Azure Storage tokens for the storage account's tenant |
| Azure HTTP 403 `AuthorizationPermissionMismatch` / `AuthorizationFailure` in Entra mode | The app has no data role on the container | Assign *Storage Blob Data Contributor* on the container ([object-storage.md](object-storage.md#entra-id-no-storage-secret)); up to 10 minutes delay |
| `AADSTS...` in a storage error | Token request for `https://storage.azure.com/.default` failed | Same causes as for Graph, see [Token (Entra ID)](#token-entra-id) |
| "Entra ID mode supports the clouds Global and China only" | Connection cloud is US Government / DoD | Use Shared Key or SAS URL with `BlobEndpointSuffix` `blob.core.usgovcloudapi.net` |
| "GetDownloadUrl is not available in Entra ID mode" | Download links need a key | Use a Shared Key or S3 profile for links (*Graph Link Storage Profile*, *Open Download Link*) |
| Azure `InvalidHeaderValue` naming `x-ms-version` | The service (e.g. an older emulator or a sovereign cloud) does not know 2026-04-06 | Set *Azure API Version* (`AzureApiVersion`), e.g. `2025-11-05` |
| Azure `ContainerNotFound`, S3 `NoSuchBucket` | Container/bucket name wrong or not created | Create it or fix the name (S3: also region and service URL) |
| Azure `BlobNotFound`, S3 `NoSuchKey` (HTTP 404) on download | The object does not exist: wrong key, changed *Key Prefix*, deleted by a lifecycle rule or by hand | Keys in NAV are relative to *Key Prefix* – do not change the prefix after storing; check the lifecycle rules. `Exists` returns false and `GetSize` -1 for a missing object without an error |
| S3 HTTP 403 `SignatureDoesNotMatch` | Wrong secret access key, wrong region, or path-style vs. virtual-hosted mismatch at a non-AWS endpoint | Re-enter the secret; set the bucket's region; *S3 Addressing* = Path Style for custom endpoints |
| S3 `InvalidAccessKeyId` | Access key id unknown at this endpoint | Check the key id and the service URL |
| S3 `ExpiredToken` / `InvalidToken` | STS session token expired or missing | Renew the temporary credentials (`S3SessionToken`) |
| S3 HTTP 403 `RequestTimeTooSkewed` | NAV server clock differs by more than 15 minutes | Synchronise the server clock (Windows Time service / NTP) |
| S3 HTTP 403 `AccessDenied` | IAM policy misses an action or the prefix does not match | See the [IAM policy](object-storage.md#iam-policy-for-aws): `s3:ListBucket` on the bucket, `s3:PutObject`/`GetObject`/`DeleteObject`/`AbortMultipartUpload`/`ListMultipartUploadParts` on the prefix |
| `Exists` / `GetSize` fails with HTTP 403 instead of returning false / -1 | S3 answers 403 for a missing object unless `s3:ListBucket` applies to the request; a `s3:ListBucket` grant limited by an `s3:prefix` condition may not apply to `HEAD` | Grant `s3:ListBucket` on the bucket **without** the `s3:prefix` `Condition` if NAV relies on `Exists` ([IAM policy](object-storage.md#iam-policy-for-aws)) |
| S3 `AuthorizationHeaderMalformed`, `PermanentRedirect`, HTTP 301/307 | Bucket is in another region | Set the bucket's region and its regional service URL |
| "the object key must not contain '.' or '..' path segments" / "... a backslash" / "control characters" | Key rules of the add-in (the .NET HTTP stack would change such paths after signing) | Use `/` as separator and no `.`/`..` segments |
| "this .NET runtime would send the object key in a different form" | The NAV server process uses legacy URI parsing for this key | Use a key without the reported characters |
| "S3 presigned URLs are valid for at most 7 days (10080 minutes)" | Link validity too long | *Graph Link Validity (Days)* ≤ 7 for S3 |
| `RequestBodyTooLarge` / `EntityTooLarge` / HTTP 413 | Single request above the service limit | Lower *Multipart Threshold (MB)* / *Part Size (MB)* |
| HTTP 500 at SeaweedFS "No writable volumes" (tests) | Emulator out of volume slots | See [../tests/integration/README.md](../tests/integration/README.md#troubleshooting) |

### Link storage and attachments from storage

| Symptom | Cause | Fix |
|---------|-------|-----|
| "Attachment 'x' (n bytes) could not be sent as a download link (UseLinkStorage): ..." | Upload or link creation failed; **no mail was sent** | Fix the storage error in the text; uploaded objects of this attempt were deleted |
| "... GetDownloadUrl is not available in Entra ID mode ..." when sending | Link storage profile is in Entra mode | Use a Shared Key or S3 profile as *Graph Link Storage Profile* |
| "Validation failed: the link text '...' is not a valid format text" | Braces in `linkText` | Use `{0}`, `{1}`, `{2}` only; write `{{` / `}}` for literal braces |
| "Attachment 'x' could not be downloaded from object storage (key k): ..." | `AddAttachmentFromStorage` key missing or no read permission | Check the key (relative to *Key Prefix*) and the profile; the temp file was removed |
| Recipient: the link no longer works (HTTP 403 from Azure or S3) | Link older than its validity | Send again or create a new link; raise *Graph Link Validity (Days)* (S3 max. 7) |
| Link objects pile up in storage | The add-in deletes them only when the mail was not sent | Lifecycle rule on `mail-attachments/` ([object-storage.md](object-storage.md#security-notes)) |

### TLS and network

| Symptom | Cause | Fix |
|---------|-------|-----|
| "The underlying connection was closed", "Could not create SSL/TLS secure channel", "Authentication failed because the remote party has closed the transport stream" | TLS 1.2 not negotiated or disabled in Schannel; or a proxy breaks TLS | Set `SchUseStrongCrypto` / `SystemDefaultTlsVersions` = 1 (`Install-NavMailGraphAddin.ps1 -FixTls`), restart the instance; check Schannel settings; check the proxy ([deployment.md](deployment.md#tls-12)) |
| HTTP 407 Proxy Authentication Required | Proxy needs the service account's credentials | `<defaultProxy enabled="true" useDefaultCredentials="true">` in `Microsoft.Dynamics.Nav.Server.exe.config` ([deployment.md](deployment.md#proxy)) |
| Timeouts | Firewall blocks `graph.microsoft.com`, `login.microsoftonline.com` or `outlook.office.com` (upload sessions) | Open outbound HTTPS to these hosts |
| Storage timeouts / connection errors | Firewall blocks `<account>.blob.core.windows.net` or the S3 endpoint | Open outbound HTTPS to the storage endpoint; for Entra mode also the token endpoint |

### Add-in loading (NAV / C/SIDE)

| Symptom | Cause | Fix |
|---------|-------|-----|
| NAV: "The add-in NavMailGraph.dll (version 1.0.0.0) cannot be loaded on the server ..." | DLL not in the service tier's `Add-ins` folder, or instance not restarted | Copy to `...\90\Service\Add-ins\NavMailGraph\`, restart the instance |
| C/SIDE compile: cannot find assembly `NavMailGraph, Version=1.0.0.0...` / DotNet type not found | DLL missing in the RoleTailored Client Add-ins folder on the development machine | Copy to `C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client\Add-ins\NavMailGraph\`, restart C/SIDE |
| C/SIDE compile: unknown member `UseDraftForLargeMessages` or similar | Older DLL on the C/SIDE machine | Copy the DLL that matches the Codeunit 50400 version |
| Old behaviour after an update | Old DLL copy somewhere below `Add-ins`, or instance not restarted | Remove all copies below `Add-ins` except the current one; restart all instances |
| `FileLoadException`: "Could not load file or assembly 'System.Memory' (or another System.* / Microsoft.IdentityModel.*), ... manifest definition does not match" | MSAL flavour without binding redirects | Merge redirects into `Microsoft.Dynamics.Nav.Server.exe.config` ([deployment.md](deployment.md#msal-flavour)), or switch *Graph Token Library* back to `Built-in` |
| "Token library 'Msal': ..." errors | `Msal\` subfolder missing or from a different build than `NavMailGraph.dll` | Deploy the matching `Msal` folder (`-IncludeMsal`) or use `Built-in` |

### NAV setup

| Symptom | Cause | Fix |
|---------|-------|-----|
| "A sender address is required ..." | No *Graph Sender Mailbox* and the caller passed no From address | Set *Graph Sender Mailbox* |
| "No client secret is stored for Microsoft Graph" | *Graph Auth Method* = Client Secret but no secret saved | Enter the secret on Page 409 |
| "The client secret was stored, but it is not encrypted ..." | No encryption key in the database | Set up data encryption (Data Encryption Management) and enter the secret again |
| Mail still goes through SMTP | *Mail Transport* is `SMTP`, or Codeunit 400 not patched | Set *Mail Transport* = Microsoft Graph; check the Codeunit 400 patch ([../nav/patches/COD400.md](../nav/patches/COD400.md)) |
| "The SMTP message object is not available ..." | Custom code calls `GetSMTPMessage` | Review that code ([../nav/README.md](../nav/README.md#find-smtpcallersps1)) |
| Graph switches are No after the upgrade | `InitValue` only applies to new records | Set them on Page 409 ([../nav/patches/TAB409.md](../nav/patches/TAB409.md#existing-record)) |

## Log file and diagnostics

Every `Send` and `TestConnection` writes a step-by-step log of that operation: settings (cloud, Graph URL,
authority), token provider, certificate subject/thumbprint, message summary, payload estimate and chosen path,
each HTTP request with its `client-request-id`, status, duration, retries and the error body.

* **Redacted:** bearer tokens, client secrets, client assertions, passwords and JWT-like strings are masked;
  upload URLs are logged without their query string (it contains an auth token).
* **Not redacted:** subject (first 80 characters), sender/recipient addresses and attachment names. The body is
  not logged (only its length). Treat the log as personal data.

### Enable the log file in NAV

1. Page 409 → *Graph Log File Path* (Importance = Additional, so it may be hidden under "Show more fields"), e.g. `D:\NavMailGraph\logs\navmailgraph.log`.
2. The **NAV service account** needs write access to that folder. The path is on the **NAV Server**, not on the
   client machine.
3. The file is append-only; every line has a UTC time stamp and an operation tag. I/O errors are ignored (a wrong
   path never breaks sending). There is no rotation: clear the field again after troubleshooting and delete the
   file.

### Logs of the v0.2 classes

* `GraphConnection.LogFilePath` (NAV: *Graph Log File Path*, shared by sending and reading) and
  `ObjectStorageClient.LogFilePath` (NAV: *Log File Path* on the storage profile) write the same kind of redacted log.
* The storage log never contains account keys, SAS signatures, presigned URLs or bearer tokens; object keys and
  file names are logged.
* `GraphMailbox.LastDiagnostics` / `ObjectStorageClient.LastDiagnostics` return the log of the last operation;
  after `CopyAttachmentToStorage` the storage side is in the storage client's `LastError` / `LastDiagnostics`.

### LastDiagnostics

`GraphMailSender.LastDiagnostics` returns the same redacted log of the last `Send` / `TestConnection` as a string;
`LastRequestId` returns the Graph request-id or Entra correlation-id of the last failed call. Codeunit 50400
currently shows only the error text, not `LastDiagnostics`. Ways to get it:

* set *Graph Log File Path* (above) – same content, no code change;
* run the same scenario with `nmg` (it prints the diagnostic log on errors, always with `--verbose`);
* in your own C/AL code, read `GraphSender.LastDiagnostics` after `Send` and show it with `MESSAGE` or store it.

## Checks in Microsoft 365

* **Entra admin center → Sign-in logs → Service principal sign-ins**: token requests of the app, with result and
  error code.
* **Exchange admin center → Mail flow → Message trace**: whether a message was sent, delivered or rejected.
* **Microsoft Graph activity logs** (must be enabled via diagnostic settings): every Graph call with AppId,
  RequestUri, status and the `client-request-id` that also appears in the NavMailGraph log.
* `Test-ServicePrincipalAuthorization -Identity <sp> -Resource <mailbox>` for the Exchange scope
  ([exchange-rbac.md](exchange-rbac.md#5-test)).
