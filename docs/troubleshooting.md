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

Reproduce outside NAV with the CLI on any machine: `nmg token` / `nmg send` print the same text plus the
diagnostic log ([development-macos.md](development-macos.md#end-to-end-test-with-nmg)).

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

### TLS and network

| Symptom | Cause | Fix |
|---------|-------|-----|
| "The underlying connection was closed", "Could not create SSL/TLS secure channel", "Authentication failed because the remote party has closed the transport stream" | TLS 1.2 not negotiated or disabled in Schannel; or a proxy breaks TLS | Set `SchUseStrongCrypto` / `SystemDefaultTlsVersions` = 1 (`Install-NavMailGraphAddin.ps1 -FixTls`), restart the instance; check Schannel settings; check the proxy ([deployment.md](deployment.md#tls-12)) |
| HTTP 407 Proxy Authentication Required | Proxy needs the service account's credentials | `<defaultProxy enabled="true" useDefaultCredentials="true">` in `Microsoft.Dynamics.Nav.Server.exe.config` ([deployment.md](deployment.md#proxy)) |
| Timeouts | Firewall blocks `graph.microsoft.com`, `login.microsoftonline.com` or `outlook.office.com` (upload sessions) | Open outbound HTTPS to these hosts |

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
