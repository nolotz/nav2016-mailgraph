# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). `AssemblyVersion` stays `1.0.0.0` for every release
(C/AL binds to it); the public .NET API is append-only.

## [0.2.0] - Unreleased

Reading mailboxes, large attachments in both directions and object storage. See
[docs/reading-mail.md](docs/reading-mail.md), [docs/large-attachments.md](docs/large-attachments.md) and
[docs/object-storage.md](docs/object-storage.md).

### Added
- `GraphConnection`: tenant, app, credential, cloud, token library, timeouts, retries and log path configured once
  and shared by sending (`GraphMailSender.UseConnection`), reading and Azure Entra ID storage.
- `GraphMailbox`, `GraphMailItem`, `GraphMailAttachmentInfo`: read a mailbox folder (folder paths like
  `Inbox/Invoices`, well-known names), `Fetch` with a text watermark (`ReceivedAfter`, inclusive, oldest first),
  `FetchChanges` delta query with `NextDeltaLink`, `DeltaExpired` and removed ids, `MarkAsRead` / `MarkAsUnread`,
  `MoveToFolder` (creates missing folders), `DeleteMessage`, `EnsureFolder`, `SaveMimeToFile` (.eml),
  `SaveAttachmentToFile`, `SaveAttachmentToStream`. Immutable ids on every call, at most 4 concurrent requests per
  mailbox, `Retry-After` handling, attachment metadata without `contentBytes`, streamed `$value` downloads with an
  idle timeout, clear errors for reference attachments (HTTP 405).
- `GraphMailbox.CopyAttachmentToStorage` with `LastStorageKey`, `LastStorageSize` and `LastStorageETag`: streams an
  attachment from Graph straight into Azure Blob or S3 (no temp file, one reused part buffer).
- `ObjectStorageClient`: Azure Blob Storage with Shared Key, container SAS URL or Entra ID token
  (`UseAzureBlobEntra`, scope `https://storage.azure.com/.default`, clouds Global and China) and Amazon S3 /
  S3-compatible storage (SigV4, path-style, STS session token). Streamed uploads (single PUT up to 64 MiB, Put Block /
  multipart above), downloads, delete, exists, size, list with paging, `GetDownloadUrl` (Azure service SAS, S3
  presigned GET up to 7 days), `TestConnection`, redacted `LastDiagnostics` and log file.
- `GraphMailSender.UseLinkStorage` / `ClearLinkStorage`: attachments above a threshold are uploaded to
  `mail-attachments/{yyyy}/{MM}/{guid}/{file name}` and replaced by a download link in the HTML or text body.
  Uploads are removed again when the mail is not sent.
- `GraphMailMessage.AddAttachmentFromStorage`: attaches an object from storage (downloaded to a temp file at send
  time and deleted afterwards).
- Error hints for reading (Mail.Read / Mail.ReadWrite with the Exchange RBAC role, `InefficientFilter`, expired delta
  links) and for storage (`AuthenticationFailed`, `SignatureDoesNotMatch`, `RequestTimeTooSkewed`, `AccessDenied`,
  missing container/bucket, region, `x-ms-version`).
- NAV 2016 objects (Version List `NMG2.00`, not compiled in CI): Tables 50400–50403 (mailbox setup, inbound mail,
  inbound attachment, object storage profile), Pages 50400–50408, Codeunit 50401 *Graph Mailbox Polling* (job queue,
  events `OnBeforeImportMail`, `OnAfterImportAttachment`, `OnAfterImportMail`), Codeunit 50402 *Object Storage Mgt.*,
  Codeunit 50403 *Graph Inbound Mail Mgt.*, Codeunit 50404 *Graph Mail Import Runner*; Table/Page 409 fields
  50113–50115 for link storage; `nav/tools/check_cal.py` static check.
- `Grant-NavMailGraphMailboxScope.ps1 -IncludeRead` (Exchange role `Application Mail.Read`); the remove script also
  removes the Mail.Read assignment.
- Storage integration tests against Azurite 3.37.0 and SeaweedFS 4.48 (`tests/integration/`, `NMG_INTEGRATION=1`),
  also run in CI (job *Storage integration*).
- `nmg inbox` (list, get, save-attachment, save-eml, mark-read/unread, move, delta) and `nmg storage` (test, put,
  get, ls, rm, url, size); `nmg send --attach-storage` and `--link-storage`.
- Documentation: reading mail, object storage, large attachments; troubleshooting, README, Exchange RBAC and the
  German customer information extended for reading and storage.

### Changed
- `GraphMailSender` is now a `partial` class (no API change); the token-provider construction was refactored so the
  same app can request tokens for other scopes (Azure Storage). The process-wide token cache is keyed by scope.
- Codeunit 50400 *Graph Mail* builds a `GraphConnection` and applies link storage when *Graph Link Threshold (MB)* is
  set.
- The release zip also contains `deploy/` (PowerShell scripts, `deploy/README.md`, `s3-iam-policy.json`), covered by
  `SHA256SUMS`; run `Install-NavMailGraphAddin.ps1` from the extracted `deploy\` folder.

### Known gaps
- Azure Entra ID storage mode is only unit-tested (no emulator setup with OAuth).
- User delegation SAS (download links in Entra ID mode) is not implemented.

## [0.1.0] - not tagged

### Added
- Core add-in `NavMailGraph.dll` (net462, netstandard2.0): Microsoft Graph `sendMail` with application
  permissions, built-in client-credentials flow (client secret or certificate), large attachments via
  draft + upload session, retries, redacted diagnostics.
- Optional `NavMailGraph.Msal.dll` token provider based on Microsoft.Identity.Client.
- `nmg` CLI for end-to-end tests from macOS/Windows.
- NAV 2016 objects: Codeunit 50400 "Graph Mail", Table/Page 409 patches, Codeunit 400 patch.
- PowerShell: Entra app registration, Exchange Online RBAC scoping, NAV add-in installation.
- Documentation in English with a German README and customer-facing permission text.
