# Object storage: Azure Blob Storage and Amazon S3 (v0.2)

`ObjectStorageClient` in `NavMailGraph.dll` stores files in **Azure Blob Storage** or in **Amazon S3 /
S3-compatible storage**, with no extra DLL. NAV uses it for three things:

1. **Inbound attachments** that are too large for a NAV BLOB: streamed straight from Graph into storage
   ([large-attachments.md](large-attachments.md#receiving)).
2. **Outbound large attachments as a download link** instead of an attachment
   ([large-attachments.md](large-attachments.md#link-instead-of-attachment)).
3. **Your own code**: archive documents, attach archived files to mails, hand out time-limited links
   (Codeunit 50402 *Object Storage Mgt.*, see [../nav/README.md](../nav/README.md#setup-walkthrough-object-storage)).

In NAV every connection is an *Object Storage Profile* (Table 50403, Pages 50404/50407); the secret is kept in
Table 1261 *Service Password*.

* [Choosing a mode](#choosing-a-mode)
* [Azure Blob Storage](#azure-blob-storage)
* [Amazon S3 and S3-compatible storage](#amazon-s3-and-s3-compatible-storage)
* [Key layout](#key-layout)
* [How transfers work](#how-transfers-work)
* [Security notes](#security-notes)
* [API reference](#api-reference)

## Choosing a mode

| Mode | Call | Secret in NAV | Download links (`GetDownloadUrl`, link mode) | Use when |
|------|------|---------------|------------------------------------------------|----------|
| Azure Shared Key | `UseAzureBlobSharedKey(account, key, container)` | storage account key | yes: per-blob read-only SAS with its own expiry | simplest Azure setup; links needed |
| Azure SAS URL | `UseAzureBlobSasUrl(containerSasUrl)` | container SAS URL | **the configured SAS itself** – see [security notes](#security-notes) | you may not hand out the account key; no link mode |
| Azure Entra ID | `UseAzureBlobEntra(connection, account, container)` | none (the Graph app's certificate/secret) | **no** | no storage secret at all; RBAC on the container |
| Amazon S3 / S3-compatible | `UseS3(serviceUrl, region, bucket, accessKeyId, secret)` | secret access key | yes: presigned GET, at most 7 days | AWS, Cloudflare R2, Wasabi, SeaweedFS, ... |

Call exactly one `Use...` method. Configuration errors (missing values, a key that is not base64, an unsupported
cloud) are not thrown: the next operation returns them as its error text.

## Azure Blob Storage

### Storage account and container

1. Create a storage account (Standard, StorageV2) in the Azure portal, region near the NAV server.
2. Create a **private** container (no anonymous access), for example `nav-mail`.
3. Optional but recommended: a lifecycle management rule that deletes `mail-attachments/` objects after the link
   validity, and soft delete for blobs.

The add-in sends `x-ms-version` **2026-04-06** by default (property `AzureApiVersion`); Microsoft lists it as fully
deployed in all public regions. If a service rejects it (error code `InvalidHeaderValue` naming `x-ms-version`),
set an older version such as `2025-11-05`. Entra mode needs 2017-11-09 or later.

### Shared Key

Enter the account name, the container and **key1 or key2** (Storage account → Security + networking → Access
keys). The key grants full access to the whole storage account, so prefer a storage account used only by NAV.

`GetDownloadUrl(key, minutes)` creates a service SAS for exactly one blob: read only (`sp=r`, `sr=b`), HTTPS only,
valid for `minutes`. It is signed locally; no request is sent.

Shared Key and every SAS stop working when *Allow storage account key access* is disabled on the account. Only
Entra ID tokens work then.

### SAS URL

Paste a **container** SAS URL: `https://<account>.blob.core.windows.net/<container>?sv=...&sig=...`. In the portal:
container → *Shared access tokens*, signing method *Account key*, allowed protocol *HTTPS only*. Permissions
needed by the add-in: **read, add, create, write, delete, list** (`sp=racwdl`) for the full feature set (upload,
download, delete, exists/size, list, test connection); read and list are enough for a read-only profile.

Every request uses this SAS; no account key is stored. The SAS has its own expiry (`se`) – note it in your
calendar, every operation fails with `AuthenticationFailed` after it.

### Entra ID (no storage secret)

The add-in requests a token with scope `https://storage.azure.com/.default` for the **same app registration** that
sends mail (the Graph fields of Table 409, certificate or secret) and sends it as `Authorization: Bearer`. Tokens
are cached process-wide per scope; a 401 refreshes the token once.

Grant the app the Azure RBAC role **Storage Blob Data Contributor** on the container (least privilege for upload,
download, delete; *Storage Blob Data Reader* is enough for read-only use). Portal: container → *Access Control
(IAM)* → *Add role assignment* → the role → *User, group, or service principal* → select the app. Azure CLI:

```bash
az role assignment create \
    --role "Storage Blob Data Contributor" \
    --assignee-object-id "<object id of the app's service principal (Enterprise application)>" \
    --assignee-principal-type "ServicePrincipal" \
    --scope "/subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.Storage/storageAccounts/<account>/blobServices/default/containers/<container>"
```

Microsoft says role assignments can take up to 10 minutes to take effect. Assigning roles needs
`Microsoft.Authorization/roleAssignments/write` (for example *Owner* or *User Access Administrator*) on that scope.

Limits of Entra mode:

* **No download links.** `GetDownloadUrl` returns `""` and `LastError` says "GetDownloadUrl is not available in
  Entra ID mode ... needs a shared key (UseAzureBlobSharedKey) or a SAS URL ...". So this mode cannot be the link
  storage for sending, and *Open Download Link* in NAV does not work. Opening an attachment in NAV (download through
  the NAV server) works. User delegation SAS is not implemented.
* **Clouds:** Global (`blob.core.windows.net`) and China (`blob.core.chinacloudapi.cn`; Microsoft's China docs use
  the same resource `https://storage.azure.com/`). For *US Government* and *US Government DoD* the connection gives
  the configuration error "supports the clouds Global and China only ... use UseAzureBlobSharedKey or
  UseAzureBlobSasUrl", because the storage token audience there could not be verified. Use Shared Key or SAS there
  (with `BlobEndpointSuffix = blob.core.usgovcloudapi.net`).
* A `GraphConnection` configured with `UseTokenProvider` passes that provider through unchanged and skips the cloud
  check; the provider must then return Azure Storage tokens.
* Token errors (`AADSTS...`) get the same hints as for Graph ([troubleshooting.md](troubleshooting.md#token-entra-id)).

### Other Azure endpoints

`BlobEndpointSuffix` (default `blob.core.windows.net`) builds `https://<account>.<suffix>`. `ServiceUrl` overrides the
whole account URL, for example `http://127.0.0.1:10000/devstoreaccount1` for the Azurite emulator.

## Amazon S3 and S3-compatible storage

`UseS3(serviceUrl, region, bucket, accessKeyId, secretAccessKey)`:

* `serviceUrl` `""` = AWS (`https://s3.<region>.amazonaws.com`); otherwise the provider's endpoint.
* `region` `""` = `us-east-1`. For AWS use the bucket's region; requests are signed with it (SigV4), a wrong region
  gives `AuthorizationHeaderMalformed` or a redirect.
* `S3PathStyle`: default **true** unless the endpoint is an `amazonaws.com` host and the bucket name has no dot. Path
  style is `https://endpoint/bucket/key`, virtual-hosted style `https://bucket.endpoint/key`. In NAV: *S3 Addressing*
  = Automatic / Path Style / Virtual Hosted Style.
* `S3SessionToken`: for temporary (STS) credentials.

### IAM policy for AWS

Create an IAM user (or role) only for NAV, an access key for it, and this policy (replace bucket and prefix):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ListNavPrefix",
      "Effect": "Allow",
      "Action": "s3:ListBucket",
      "Resource": "arn:aws:s3:::contoso-nav-mail",
      "Condition": { "StringLike": { "s3:prefix": ["nav/prod/*"] } }
    },
    {
      "Sid": "ObjectsUnderNavPrefix",
      "Effect": "Allow",
      "Action": [
        "s3:PutObject", "s3:GetObject", "s3:DeleteObject",
        "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts"
      ],
      "Resource": "arn:aws:s3:::contoso-nav-mail/nav/prod/*"
    }
  ]
}
```

* `s3:PutObject` covers single PUTs and the multipart calls (create, upload part, complete); `s3:AbortMultipartUpload`
  lets the add-in clean up after a failed multipart upload. The same policy (with `s3:ListMultipartUploadParts`) is
  in [deploy/s3-iam-policy.json](../deploy/s3-iam-policy.json).
* `HEAD` (used by `Exists` / `GetSize`) needs `s3:GetObject`. Without `s3:ListBucket` S3 answers **403 instead of 404**
  for a missing object, so `Exists` would report an error instead of `false`.
* The `s3:prefix` condition above can have the same effect: a `HEAD` request carries no `s3:prefix`, so the
  conditioned `s3:ListBucket` grant may not apply to it and `Exists` / `GetSize` on a missing key can still fail with
  **403**. If NAV relies on `Exists` (or "Test File" reports access denied for a missing object), remove the
  `Condition` block: listing the whole bucket is then allowed, reading or writing objects outside the prefix still
  is not.
* `TestConnection` lists at most one object below `KeyPrefix`, so the prefix condition must match `KeyPrefix`.
  Set *Key Prefix* = `nav/prod/` in the profile; without a prefix drop the `Condition` and use
  `arn:aws:s3:::contoso-nav-mail/*`.
* Add a lifecycle rule *AbortIncompleteMultipartUpload* (e.g. after 1 day). The add-in aborts failed uploads itself,
  but a killed NAV service cannot; AWS bills the parts until they are removed.
* Keep *Block Public Access* on. Presigned links work without public access.

### S3-compatible providers

| Provider | Service URL | Region | Notes |
|----------|-------------|--------|-------|
| Cloudflare R2 | `https://<account id>.r2.cloudflarestorage.com` | `auto` | path style (Automatic). R2 rejects some `x-amz-*` headers such as ACL or tagging; the add-in sends none of them. |
| Wasabi | `https://s3.<region>.wasabisys.com` (e.g. `s3.eu-central-2.wasabisys.com`) | the bucket's region | use the URL that matches the bucket's region |
| Hetzner Object Storage | `https://<location>.your-objectstorage.com` (`fsn1`, `nbg1`, `hel1`) | not verified (probably the location) | test with *Test Connection* / *Upload Test File* |
| SeaweedFS | your server, e.g. `http://127.0.0.1:8333` | `us-east-1` | tested in the integration tests (SigV4, multipart, presigned GET, paging) |
| MinIO | your server | `us-east-1` | the MinIO repository is no longer maintained and the `minio/minio` image was not pullable when this was written; not tested |

Only the providers marked as tested were run against the add-in. For the others the values come from the providers'
documentation ([research-notes-v2.md](research-notes-v2.md#amazon-s3--sigv4--s3-compatible)); run *Test Connection*
and *Upload Test File* (Page 50407), or `nmg storage test` / `put` / `url`
([development-macos.md](development-macos.md#reading-mail-and-object-storage-v02)), before going live.

## Key layout

Keys are relative to `KeyPrefix`, which the add-in puts in front of every key and removes again from listed keys and
from `GraphMailbox.LastStorageKey`. Do not change *Key Prefix* after files were stored: NAV keeps the relative keys.

| Use | Key (after `KeyPrefix`) |
|-----|-------------------------|
| Inbound attachments (Codeunit 50401) | `{mailbox code}/{yyyy}/{MM}/{dd}/{mail entry no.}/{file name}` |
| Outbound link mode | `mail-attachments/{yyyy}/{MM}/{guid}/{file name}` (UTC date; `/`, `\`, control characters and unpaired surrogates in the file name become `_`) |
| Your own code | anything that follows the rules below |

Key rules (checked before any request): not empty, no leading `/`, no backslash, no `.` or `..` segment, no control
characters, no unpaired UTF-16 surrogates. The reason is that the .NET Framework HTTP stack would change such paths
after they were signed. Spaces, umlauts and other characters are fine; the add-in percent-encodes them.

## How transfers work

* **Upload:** up to `MultipartThresholdBytes` (default 64 MiB) a single PUT; above that Azure Put Block + Put Block
  List or S3 multipart upload with parts of `PartSizeBytes` (default 8 MiB, raised automatically when the part count
  limit would be hit). One part buffer is reused, so memory stays at about one part. A failed S3 multipart upload is
  aborted; uncommitted Azure blocks are removed by Azure after a week.
* **Streams of unknown length** (the Graph `$value` pipe without Content-Length) are uploaded part by part; nothing is
  written to a temp file.
* **Download:** streamed in 81920-byte steps to a file or stream; `DownloadToFile` writes to a temporary file next to the target and replaces the target only on success, so a failed download leaves an existing file untouched.
* **Retries:** idempotent operations are retried on 408/429/5xx and transport errors (`MaxRetries`, default 3).
  `TimeoutSeconds` (default 300) applies per request; for downloads it is an idle timeout per read.
* **Integrity:** S3 bodies up to the part size and every multipart part are signed with their SHA-256; only larger
  single streamed PUTs over HTTPS use `UNSIGNED-PAYLOAD` (TLS protects them). Azure computes and stores an MD5 for
  single PUTs.

## Security notes

* **Secrets.** Account keys, SAS URLs and S3 secret keys are stored in Table 1261 *Service Password* (encrypted when
  the NAV database has an encryption key, like the Graph client secret) and are shown masked. The add-in never writes
  them, a SAS signature, a presigned URL or a bearer token into error texts, `LastDiagnostics`, `Describe()` or the log
  file.
* **Prefer least privilege.** Entra mode (no secret, RBAC on one container) or an S3 user limited to one prefix. An
  Azure account key opens the whole storage account.
* **Download links are bearer tokens.** Anyone who has the link can download the file until it expires – including
  whoever the mail is forwarded to. Choose the validity as short as practical: *Graph Link Validity (Days)* (default
  7) for mails, *Download Link Validity (Min.)* (default 60) for *Open Download Link*. S3 presigned URLs are valid for
  at most 7 days; a longer request fails before anything is uploaded. Revoking a single link early is not possible:
  delete the object (or, for Azure Shared Key, rotate the account key, which revokes all links and breaks the
  profile until the new key is entered).
* **SAS URL mode hands out the container SAS.** In this mode `GetDownloadUrl` returns the blob URL with the
  **configured container SAS**, i.e. with all its permissions (typically write, delete and list for the whole
  container) and its full validity. Do **not** use a SAS URL profile as link storage for outgoing mail and do not
  give its links to anyone outside the company. Use Shared Key or S3 for links.
* **Links are created before uploading.** If a link cannot be created (Entra mode, S3 with more than 7 days), Send
  fails before anything is uploaded or sent. Signed URLs are never logged.
* **Uploaded link targets stay in storage.** The add-in deletes them only when the mail was not sent. Use a lifecycle
  rule to remove `mail-attachments/` after the link validity.
* **Clock.** Shared Key and SigV4 requests must be within 15 minutes of the server time (`RequestTimeTooSkewed`,
  `AuthenticationFailed`). Keep the NAV server time-synchronised.
* **Transport.** Use HTTPS endpoints. `http://` is accepted only for local emulators.
* **Inbound content is untrusted.** Files from mail are stored as received; scan them like any mail attachment
  before opening.

## API reference

All methods return `""` or an error text (also in `LastError`) and never throw for remote, IO or validation
problems. One instance per NAV session or job; not thread-safe.

| Member | Purpose |
|--------|---------|
| `UseAzureBlobSharedKey`, `UseAzureBlobSasUrl`, `UseAzureBlobEntra`, `UseS3` | configure the provider (exactly one) |
| `BlobEndpointSuffix`, `ServiceUrl`, `AzureApiVersion` | Azure endpoint and version |
| `S3PathStyle`, `S3SessionToken` | S3 addressing and STS token |
| `KeyPrefix`, `MultipartThresholdBytes`, `PartSizeBytes`, `TimeoutSeconds`, `MaxRetries`, `LogFilePath` | options |
| `UploadFile(path, key, contentType)`, `UploadStream`, `UploadText` | upload (`contentType` `""` = by extension) |
| `DownloadToFile(key, path)`, `DownloadToStream(key, stream)` | download |
| `Delete(key)` | delete; a missing object counts as success |
| `Exists(key)` → bool, `GetSize(key)` → long | `false` / `-1` for "not found" with `LastError` `""`; on failure `LastError` is set |
| `List(prefix)`, `ListCount`, `GetListedKey(i)`, `GetListedSize(i)` | list all pages |
| `GetDownloadUrl(key, validMinutes)` | time-limited link or `""` + `LastError` |
| `TestConnection()` | list at most one object |
| `LastError`, `LastRequestId`, `LastDiagnostics`, `Describe()` | diagnostics without secrets |

Related: `GraphMailbox.CopyAttachmentToStorage`, `GraphMailSender.UseLinkStorage` / `ClearLinkStorage`,
`GraphMailMessage.AddAttachmentFromStorage` – see [large-attachments.md](large-attachments.md).
