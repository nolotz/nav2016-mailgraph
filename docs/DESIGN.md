# NavMailGraph – Design

Status: living document. This is the implementation contract for the library, the NAV objects, the
deployment scripts and the docs. Facts marked **(verify)** are being confirmed against primary sources
and get updated in `docs/research-notes.md`.

## 1. Goal

Replace SMTP in Dynamics NAV 2016 (C/AL, Codeunit 400 "SMTP Mail") with Microsoft Graph `sendMail`
using application permissions (client credentials), **without changing any caller** of Codeunit 400
(document mailing, reminders, report mailing, ...). Development and unit/integration testing happen on
macOS; the add-in runs on the Windows NAV service tier.

Non-goals: delegated (user) auth, reading mail, Graph SDK, NAV 2017+ (works there too, but untested),
Business Central (has native Graph mail).

## 2. Repository layout

```
nav2016-mailgraph/
├── README.md / README.de.md         overview (EN primary, DE translation)
├── docs/                            design, Azure/Exchange setup, NAV integration, deployment, dev on mac, troubleshooting
│   └── de/                          German customer-facing texts (permission explanation)
├── src/NavMailGraph/                core add-in assembly: net462;netstandard2.0, ZERO NuGet dependencies
├── src/NavMailGraph.Msal/           optional token provider on Microsoft.Identity.Client (MSAL)
├── src/NavMailGraph.Cli/            `nmg` console tool (net8.0): real end-to-end test from macOS/Windows
├── tests/NavMailGraph.Tests/        xunit; runs on net8.0 everywhere and additionally on net462 on Windows
├── nav/                             NAV 2016 C/AL objects (.TXT) and patch instructions for Microsoft objects
├── deploy/                          PowerShell: Entra app registration, Exchange RBAC scoping, NAV add-in install
├── .github/workflows/               CI (ubuntu/macos/windows) + release packaging
└── build.sh                         mac one-shot: build, test, create dist/Add-ins
```

## 3. Why two assemblies (core without MSAL, MSAL optional)

The original plan said "MSAL + HttpClient, no Graph SDK, because the SDK pulls in many dependencies".
A probe build on macOS (2026-10-12, Microsoft.Identity.Client 4.90.1, net462) shows MSAL itself copies
**14 dependency DLLs** next to the add-in (System.Text.Json, System.Memory, System.Buffers,
System.Formats.Asn1, Microsoft.Bcl.AsyncInterfaces, System.Threading.Tasks.Extensions, System.ValueTuple,
System.Runtime.CompilerServices.Unsafe, System.Numerics.Vectors, System.Text.Encodings.Web,
System.Diagnostics.DiagnosticSource, Microsoft.IdentityModel.Abstractions, ...) and the SDK generates
**binding redirects** for `System.Memory` (→4.0.1.2) and `System.Runtime.CompilerServices.Unsafe`
(→6.0.0.0). A class library cannot apply binding redirects itself; they would have to be merged into
`Microsoft.Dynamics.Nav.Server.exe.config` on every NAV server, and the set changes with MSAL updates.

Therefore:

* `NavMailGraph.dll` (core) implements the OAuth 2.0 client-credentials flow itself with the in-box
  `HttpClient` and in-box crypto (client secret, or certificate via a self-signed RS256 JWT client
  assertion). One file to deploy, no redirects. **This is the default.**
* `NavMailGraph.Msal.dll` (+ MSAL and its closure) implements the same `ITokenProvider` with MSAL for
  people who want MSAL (Microsoft-supported auth stack, regional endpoints, future managed identity).
  Selected at runtime by the setup option "Token Library = MSAL"; the core loads it by reflection so the
  C/AL objects compile without it.

## 4. Core public API (C/AL-facing, keep it C/AL friendly)

Rules: only `string`, `bool`, `int`, `long`, `byte[]`, `System.IO.Stream` and our own classes in public
signatures; no generics, no `async` in public methods, no overloads with the same arity, no optional
parameters (C/AL cannot use them), no `out`/`ref`. Everything synchronous from the outside
(sync-over-async with `ConfigureAwait(false)` everywhere inside; NAV server has no SynchronizationContext).

```csharp
namespace NavMailGraph
{
    /// Message builder. Mirrors what Codeunit 400 collects before Send.
    public sealed class GraphMailMessage
    {
        public GraphMailMessage();
        public string FromName { get; set; }          // display name for "from"
        public string FromAddress { get; set; }       // SMTP address NAV wants as sender
        public string Subject { get; set; }
        public string Body { get; set; }
        public bool HtmlFormatted { get; set; }       // body contentType "HTML" else "Text"
        public string Importance { get; set; }        // "" | "low" | "normal" | "high"
        public void AddRecipients(string recipients); // "a@x.de; Name <b@y.de>, c@z.de" -> To
        public void AddCC(string recipients);
        public void AddBCC(string recipients);
        public void AddReplyTo(string recipients);
        public void AppendBody(string text);
        public void AddAttachment(string filePath, string fileName);  // fileName "" -> Path.GetFileName(filePath); file is read at Send time (streamed when large)
        public void AddAttachmentBytes(byte[] content, string fileName);
        public void AddAttachmentStream(System.IO.Stream content, string fileName); // buffered into memory
        public void AddInlineAttachment(string filePath, string fileName, string contentId); // isInline=true, cid: for HTML bodies
        public void AddHeader(string name, string value); // internetMessageHeaders; name must start with "x-" or "X-" (Graph rule) (verify)
        public int RecipientCount { get; }            // To+CC+BCC
        public int AttachmentCount { get; }
        public long TotalAttachmentBytes { get; }
        public string GetBody();
        public string GetSubject();
        public string Describe();                     // one-line summary for logs/errors, no body, no secrets
    }

    /// Configures auth + options and sends messages. One instance per NAV session is fine; token caches are static (process-wide, thread-safe).
    public sealed class GraphMailSender
    {
        public GraphMailSender(string tenantId, string clientId);
        // credentials (exactly one)
        public void UseClientSecret(string clientSecret);
        public void UseCertificateThumbprint(string thumbprint);   // LocalMachine\My first, then CurrentUser\My; thumbprint whitespace/non-hex stripped
        public void UseCertificateFile(string pfxPath, string pfxPassword);
        public void UseTokenProvider(ITokenProvider provider);    // programmatic (tests, CLI)
        // optional settings
        public string TokenLibrary { get; set; }      // "BuiltIn" (default) | "Msal" (loads NavMailGraph.Msal by reflection at first Send)
        public string Cloud { get; set; }             // "Global" (default) | "USGov" | "China"  -> authority host + graph base url
        public string GraphBaseUrl { get; set; }      // override, default from Cloud, e.g. "https://graph.microsoft.com/v1.0"
        public string AuthorityHost { get; set; }     // override, default from Cloud, e.g. "https://login.microsoftonline.com"
        public string SenderMailbox { get; set; }     // mailbox used in /users/{SenderMailbox}/...; "" -> message.FromAddress
        public bool SaveToSentItems { get; set; }     // default true
        public bool SendX5C { get; set; }             // include x5c in client assertion (cert auto-rotation / SNI), default false
        public int TimeoutSeconds { get; set; }       // per HTTP request, default 100
        public int MaxRetries { get; set; }           // on 429/5xx, default 3
        public long SendMailPayloadLimitBytes { get; set; }  // default 3,670,016 (3.5 MiB; Graph limit 4 MiB)
        public long DirectAttachmentLimitBytes { get; set; } // default 3,145,728 (3 MiB): below -> POST attachments, else upload session
        public int UploadChunkBytes { get; set; }     // default 3,932,160 (12 x 320 KiB, < 4 MiB)
        public long MaxAttachmentBytes { get; set; }  // default 157,286,400 (150 MiB, Graph maximum)
        public string LogFilePath { get; set; }       // optional append-only diagnostic log (secrets redacted)
        // operations
        public string Send(GraphMailMessage message); // "" on success, otherwise a complete error text (never throws for Graph/auth/IO errors; throws ArgumentNullException only for null message)
        public string TestConnection();               // acquires a token only; "" on success else error text
        public string LastMessageId { get; }          // Graph message id of the last draft-based send ("" for direct sendMail)
        public string LastRequestId { get; }          // Graph request-id of the last failed call, for support
        public string LastDiagnostics { get; }        // redacted log of the last Send/TestConnection (what NAV shows on error)
        public static string Version { get; }         // informational version of the assembly
    }

    public interface ITokenProvider
    {
        System.Threading.Tasks.Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, System.Threading.CancellationToken cancellationToken);
        string Describe(); // e.g. "BuiltIn/ClientSecret tenant=... client=..." (no secrets)
    }

    public sealed class AccessToken
    {
        public AccessToken(string token, System.DateTimeOffset expiresOn);
        public string Token { get; }
        public System.DateTimeOffset ExpiresOn { get; }
    }

    public sealed class GraphMailException : System.Exception   // used internally; Send() converts it into the returned string
    {
        public string ErrorCode { get; }      // Graph "error.code" or AADSTS code or "Transport"/"Validation"
        public int StatusCode { get; }        // HTTP status, 0 if none
        public string RequestId { get; }
        public bool IsTransient { get; }
    }
}
```

Return-string contract (what C/AL shows via `ERROR`):

```
Microsoft Graph sendMail failed (HTTP 403, ErrorAccessDenied): Access is denied. Check credentials and try again. [request-id 7d4f..., 2026-10-12T06:00:00Z, mailbox nav@contoso.com]
Token acquisition failed (invalid_client, AADSTS7000215): Invalid client secret provided. ... [correlation-id ...]
Validation failed: at least one recipient is required.
Attachment 'Rechnung.pdf' not found: C:\...\tmp.pdf
```

## 5. Core internals

```
src/NavMailGraph/
├── GraphMailMessage.cs, GraphMailSender.cs, ITokenProvider.cs, AccessToken.cs, GraphMailException.cs
├── MailAttachment.cs            (name, contentType, size, source: file path | bytes; OpenRead(); ReadAllBytes())
├── MailRecipient.cs             (address, name) + EmailAddressParser (split ; and , ; "Name <addr>"; trims; rejects without '@')
├── CloudEndpoints.cs            Global/USGov/China -> authority host, graph base url, default scope "https://graph.microsoft.com/.default" (China: https://microsoftgraph.chinacloudapi.cn, USGov: https://graph.microsoft.us) (verify)
├── Auth/BuiltInTokenProvider.cs client credentials over HttpClient: secret -> form post; certificate -> JWT client assertion (RS256, x5t, optional x5c)
├── Auth/ClientAssertionBuilder.cs base64url, JWT header/payload, RSA sign (fallback: re-open CSP key with provider type 24 when SignData throws "Invalid algorithm specified")
├── Auth/CertificateLoader.cs    thumbprint -> X509Store search; pfx file; validation (has private key, not expired -> warning only)
├── Auth/TokenCache.cs           static ConcurrentDictionary<cacheKey, AccessToken>; refresh when < 5 min left; SemaphoreSlim per key
├── Graph/GraphClient.cs         SendMailAsync, CreateDraftAsync, AddAttachmentAsync, CreateUploadSessionAsync, UploadChunksAsync, SendDraftAsync; retry/backoff; 401 -> force token refresh once
├── Graph/GraphModels.cs         [DataContract] POCOs (message, recipients, body, fileAttachment, sendMail envelope, upload session, error)
├── Graph/GraphJson.cs           DataContractJsonSerializer helpers (UTF-8, no BOM)
├── Graph/ContentTypes.cs        extension -> MIME map (pdf, xml, csv, txt, htm/html, doc/docx, xls/xlsx, ppt/pptx, png, jpg/jpeg, gif, zip, json, rtf, msg, eml), default application/octet-stream
├── Internal/HttpClientProvider.cs  one static HttpClient (Timeout = Infinite, per-request CTS), default system proxy; net462: ServicePointManager.SecurityProtocol |= Tls12 in static ctor
├── Internal/AsyncHelper.cs      RunSync(Func<Task<T>>)
├── Internal/Diagnostics.cs      in-memory log of the current operation + optional file log; redacts tokens/secrets
└── Internal/MsalBridge.cs       Type.GetType("NavMailGraph.Msal.MsalTokenProvider, NavMailGraph.Msal") + Activator; helpful error if missing
```

### Send algorithm

```
Send(msg):
  validate: sender mailbox (SenderMailbox or FromAddress) non-empty; >=1 recipient; each attachment exists & <= MaxAttachmentBytes
  token = provider.GetAccessToken()
  estimate = utf8(body)+utf8(subject) + Σ over attachments (ceil(size/3)*4 + 512) + 2048
  if estimate <= SendMailPayloadLimitBytes:
        POST {graph}/users/{mailbox}/sendMail  {message:{...attachments inline as fileAttachment...}, saveToSentItems}
        expect 202
  else:
        id = POST {graph}/users/{mailbox}/messages  (message without attachments) -> 201, read "id"
        for each attachment:
            if size < DirectAttachmentLimitBytes:
                POST {graph}/users/{mailbox}/messages/{id}/attachments  fileAttachment (base64) -> 201
            else:
                session = POST .../messages/{id}/attachments/createUploadSession {AttachmentItem:{attachmentType:"file", name, size, contentType? (verify), isInline?, contentId?}} -> 201, uploadUrl
                stream file in chunks of UploadChunkBytes (last chunk shorter):
                    PUT uploadUrl, headers Content-Type: application/octet-stream, Content-Length, Content-Range: bytes {start}-{end}/{total}; NO Authorization header (verify)
                    expect 200 (nextExpectedRanges) ... last 201 Created (verify)
        POST {graph}/users/{mailbox}/messages/{id}/send -> 202
        LastMessageId = id
  on failure in the draft flow: best effort DELETE .../messages/{id} (ignore errors), then report
```

Mailbox path: `/users/{Uri.EscapeDataString(mailbox)}/...`. Headers on every Graph call: `Authorization: Bearer`, `Accept: application/json`,
`client-request-id: <new guid>`, `User-Agent: NavMailGraph/<version> (+https://github.com/nolotz/nav2016-mailgraph)`.
`from` is only set when `FromAddress` differs from the mailbox (Graph then enforces SendAs; the error ErrorSendAsDenied is surfaced verbatim) (verify).
Retry (as implemented, see section 11): on 429/503/509 honour; sendMail/send are never retried after 504 or a timeout (outcome unknown). Originally: on 429/503/504 honour `Retry-After` (seconds) else backoff 2s·2^n, max `MaxRetries`. On 401 once: force token refresh and retry.
Upload chunks are not retried across sessions; a failed chunk fails the send (keeps the code simple; NAV job queue retries whole sends).

### Built-in token provider

* Secret: `POST {authority}/{tenant}/oauth2/v2.0/token` form `client_id, client_secret, scope, grant_type=client_credentials`.
* Certificate: form `client_id, scope, grant_type=client_credentials, client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer, client_assertion=<JWT>`
  JWT header `{"alg":"RS256","typ":"JWT","x5t":"<base64url(SHA-1 thumbprint bytes)>"}` (+ `"x5c":["<base64 DER>"]` when SendX5C);
  payload `{"aud":"<token endpoint>","iss":"<clientId>","sub":"<clientId>","jti":"<guid>","nbf":now-60,"exp":now+600,"iat":now}`; signature RSA-SHA256 PKCS#1 v1.5.
* Response `{access_token, expires_in, token_type}`; error `{error, error_description, error_codes[], correlation_id, trace_id}` → message
  `Token acquisition failed (<error>, AADSTS<code>): <description first line> [correlation-id ...]`.
* Cache key = authority|tenant|clientId|credentialFingerprint(SHA-256 of secret or cert thumbprint)|scope. Static, thread-safe. Refresh at < 5 min remaining or `forceRefresh`.

### MSAL token provider (`NavMailGraph.Msal`)

`MsalTokenProvider(string tenantId, string clientId)` + `UseClientSecret / UseCertificate(X509Certificate2, bool sendX5C) / AuthorityHost / GraphScope`;
builds `IConfidentialClientApplication` once per config (static cache) with `WithAuthority(new Uri($"{authorityHost}/{tenantId}"))`,
`AcquireTokenForClient(scopes).WithForceRefresh(force)`; maps `MsalServiceException` (ErrorCode, CorrelationId, ResponseBody first line) into `GraphMailException`.
Activated from core through `MsalBridge` with the same configuration object (tenant, client, credential, authority host, scope).

## 6. NAV 2016 integration (C/AL)

Numbers 50100+ are placeholders in the customer range; adjust to the licence. Captions ENU + DEU.

### Table 409 "SMTP Mail Setup" – new fields

| No.   | Name                        | Type / Length                     | Notes |
|-------|-----------------------------|-----------------------------------|-------|
| 50100 | Mail Transport              | Option `SMTP,Graph`               | Caption "SMTP,Microsoft Graph" |
| 50101 | Graph Tenant ID             | Text 250                          | GUID or `contoso.onmicrosoft.com` |
| 50102 | Graph Client ID             | Text 250                          | application (client) id |
| 50103 | Graph Auth Method           | Option `Secret,Certificate`       | |
| 50104 | Graph Client Secret Key     | GUID                              | key into Table 1261 "Service Password" (same mechanism as field "Password Key") (verify) |
| 50105 | Graph Cert. Thumbprint      | Text 100                          | |
| 50106 | Graph Sender Mailbox        | Text 250                          | optional fixed mailbox; empty → the From address passed by the caller |
| 50107 | Graph Save to Sent Items    | Boolean, InitValue Yes            | |
| 50108 | Graph Token Library         | Option `BuiltIn,MSAL`             | Caption "Built-in,MSAL" |
| 50109 | Graph Cloud                 | Option `Global,USGov,China`       | Caption "Global,US Government,China" |

Functions added to Table 409: `SetGraphClientSecret(Secret : Text)`, `GetGraphClientSecret() : Text`, `HasGraphClientSecret() : Boolean`, `IsGraphEnabled() : Boolean`.

### Codeunit 50400 "Graph Mail" (new, ours)

Same function names/signatures as Codeunit 400 so the patch in Codeunit 400 is a one-line delegation per function:
`CreateMessage(FromName, FromAddress, Recipients, Subject, Body, HtmlFormatted)`, `Send`, `TrySend() : Boolean` ([TryFunction]), `AddRecipients`, `AddCC`, `AddBCC`,
`AppendBody`, `AddAttachment(AttachmentPath, FileName)`, `GetBody`, `GetSubject`, plus `IsEnabled`, `TestConnection() : Text`, `SendTestMail(ToAddress)`.
DotNet variables: `'NavMailGraph, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.NavMailGraph.GraphMailMessage` and `...GraphMailSender`, RunOnClient=No.

### Codeunit 400 patch (minimal, documented, not redistributed)

```
VAR GraphMail@50100 : Codeunit 50400;  UseGraph@50101 : Boolean;
CreateMessage: first lines   UseGraph := GraphMail.IsEnabled; IF UseGraph THEN BEGIN GraphMail.CreateMessage(...); EXIT; END;
Send / AddRecipients / AddCC / AddBCC / AppendBody / AddAttachment / GetBody / GetSubject / TrySend: IF UseGraph THEN BEGIN GraphMail.X(...); EXIT; END;
IsEnabled (if present): EXIT(GraphMail.IsEnabled OR <original condition>)
```
Plus a note on other objects that test `"SMTP Server"` directly (Codeunit 9520 "Mail Management" etc.) (verify list).

### Page 409 patch

Group "Microsoft Graph" with the new fields, a masked `GraphClientSecret` page variable (OnValidate → `SetGraphClientSecret`), a `TestRecipient` variable,
actions "Test Graph Connection" and "Send Graph Test Email".

## 7. Deployment (Windows)

```
C:\Program Files\Microsoft Dynamics NAV\90\Service\Add-ins\NavMailGraph\NavMailGraph.dll          (always)
C:\Program Files\Microsoft Dynamics NAV\90\Service\Add-ins\NavMailGraph\Msal\NavMailGraph.Msal.dll (+ MSAL closure; optional)
C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client\Add-ins\NavMailGraph\...      (C/SIDE design time on the dev box) (verify)
```
Restart the NAV Server instance after copying. Certificates: import pfx into `LocalMachine\My`, grant the NAV service account read access to the private key.
If the MSAL flavour throws `FileLoadException`, merge the generated `NavMailGraph.Msal.dll.config` binding redirects into `Microsoft.Dynamics.Nav.Server.exe.config`.
Windows requirement: .NET Framework 4.6.2 or later; TLS 1.2 enabled (Windows Server 2012 R2: registry `SchUseStrongCrypto`/`SystemDefaultTlsVersions`) (verify).

## 8. Build & test on macOS

`brew install --cask dotnet-sdk@8` (the .NET 8 runtime is required for the net8.0 tests/CLI; a newer SDK alone is not enough) → `./build.sh` runs `dotnet build`, `dotnet test -f net8.0` and `dotnet publish -f net462` into
`dist/Add-ins/NavMailGraph/` (+ `dist/Add-ins/NavMailGraph/Msal/`) with a `SHA256SUMS` file. `nmg` (CLI) sends a real mail from macOS against a test tenant
(secret via env var or pfx file) so the Graph flow (incl. >3 MB attachments) is proven before anything touches NAV.
GitHub Actions: ubuntu + macos (build, tests net8.0) and windows (build, tests net8.0 **and net462**, publishes the add-in artifact). Release workflow on tag `v*` zips `dist/Add-ins` + `nav/`.

## 9. Versioning

`AssemblyVersion` is pinned at `1.0.0.0` (C/AL binds to it); `VersionPrefix` in `Directory.Build.props` is the release number (FileVersion/InformationalVersion, zip name, `GraphMailSender.Version`).
Public API is append-only within 1.x.

## 10. Security notes

* Secrets never appear in logs, exceptions, `Describe()` or `LastDiagnostics` (redaction of `client_secret=`, `Bearer `, `client_assertion=`).
* Certificates are preferred over secrets; secret storage in NAV is encrypted (Service Password / ENCRYPT).
* TLS 1.2 enforced on net462; `ServerCertificateCustomValidationCallback` is never relaxed.
* Mail.Send (Application) is tenant-wide by default → documented Exchange RBAC for Applications scoping (`docs/exchange-rbac.md`) + customer text in `docs/de/`.
* No telemetry.

## 11. Corrections from the research pass (2026-10-12) – these override earlier sections

Sources and the full fact list: `docs/research-notes.md`.

### Graph / permissions
* `sendMail` and `POST /messages/{id}/send` need **Mail.Send** only. The draft flow (create draft, add attachment,
  createUploadSession, delete draft) additionally needs **Mail.ReadWrite** (application). Mail.ReadWrite does not include sending.
  → Core: new property `bool UseDraftForLargeMessages` (default true). When false and the sendMail payload estimate exceeds
  `SendMailPayloadLimitBytes`, Send returns a validation error that says the message is too large for sendMail and that the
  draft flow requires Mail.ReadWrite. A 403 during the draft flow gets the hint "draft/large-attachment flow requires Mail.ReadWrite
  (Exchange RBAC role 'Application Mail.ReadWrite')".
* `POST .../send` must carry `Content-Length: 0` (send empty `ByteArrayContent`).
* Upload-session chunks: no 320 KiB alignment rule for Outlook; chunks ≤ 4 MiB; final PUT returns **201**, intermediate PUTs **200**.
  The uploadUrl is on outlook.office.com and pre-authenticated – never send Authorization there (already implemented).
* createUploadSession is **not available in US Government L4/L5** clouds → on `USGov`/`USGovDoD` an attachment ≥ DirectAttachmentLimitBytes
  fails with a clear validation error (no silent attempt).
* Message limit defaults to 35 MB in Exchange Online; recipients ≤ 500 per message by default; 30 messages/minute and 10,000
  recipients/day per mailbox. Document; do not enforce in code (tenants can change limits).
* Error hints (appended to the returned error text as ` Hint: ...`) for: Graph `ErrorAccessDenied`/403 (mailbox outside the RBAC
  scope or permission missing), `ErrorSendAsDenied` (From differs from mailbox; use Graph Sender Mailbox or grant Send As),
  `MailboxNotEnabledForRESTAPI`/`ResourceNotFound`/404 (mailbox does not exist or has no Exchange Online license/mailbox),
  413 (payload too large), AADSTS7000215 (wrong secret – use the secret *value*, not its ID), AADSTS7000222 (secret expired),
  AADSTS700016 (client id not found in tenant), AADSTS90002 (tenant not found), AADSTS700027 (certificate not registered on the app
  or wrong certificate), AADSTS50059/AADSTS9001023 (tenant id missing/`common` used).
* Clouds: `Global` (login.microsoftonline.com / graph.microsoft.com), `USGov` (login.microsoftonline.us / graph.microsoft.us),
  `USGovDoD` (login.microsoftonline.us / dod-graph.microsoft.us), `China` (**login.chinacloudapi.cn** / microsoftgraph.chinacloudapi.cn).

### Exchange RBAC for Applications (scoping)
* Recommended: **no** Graph application permission consented in Entra; grant `Application Mail.Send` (and `Application Mail.ReadWrite`
  if large attachments are needed) via `New-ManagementRoleAssignment -App <SP object id> -CustomResourceScope <scope>`.
  A tenant-wide Entra grant plus a scoped Exchange grant = union = **no** effective scoping. Cache delay 30 min – 2 h.
* Legacy Application Access Policies: documented only as background ("don't create new ones").

### NAV 2016 facts (CU1 build 43402 exports)
* Codeunit 400 globals: `Mail@1000 : DotNet "'Microsoft.Dynamics.Nav.SMTP, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'.Microsoft.Dynamics.Nav.SMTP.SmtpMessage"`,
  `SMTPMailSetup@1003 : Record 409`, `SendResult@1004 : Text`. Text002 = attachment does not exist, Text003 = 'The SMTP mail system returned the following error: "%1".'
* Functions: `CreateMessage@1(SenderName;SenderAddress;Recipients;Subject;Body : Text;HtmlFormatted : Boolean)` (does `TESTFIELD("SMTP Server")`),
  `TrySend@10() : Boolean` (plain function, `EXIT(SendResult = '')`), `Send@3()` (`IF NOT TrySend THEN ERROR(Text003,SendResult)`),
  `AddRecipients@4/AddCC@5/AddBCC@6(Recipients : Text)`, `AppendBody@7(BodyPart : Text)`, `AddAttachment@2(Attachment : Text;FileName : Text)`
  (EXIT if blank, ERROR(Text002) if NOT EXISTS, strips chars via CU 419), `AddAttachmentStream@15(AttachmentStream : InStream;AttachmentName : Text)`,
  `CheckValidEmailAddresses@8`, `GetLastSendMailErrorText@9() : Text`, `GetSMTPMessage@35(VAR SMTPMessage : DotNet)`, `IsEnabled@11() : Boolean`
  (`"SMTP Server" <> ''`), `ApplyOffice365Smtp@12`, `GetBody@13() : Text`, subscriber `HandleSMTPRegisterServiceConnection@14`. **No GetSubject.**
* Codeunit 9520 `SendViaSMTP` uses CreateMessage, AddAttachment, AddCC/AddBCC, `TrySend` and `GetLastSendMailErrorText`; `IsSMTPEnabled` = CU400.IsEnabled.
  Codeunit 412 "SMTP Test Mail" and Page 409 action "Test Email Setup" (enabled by `"SMTP Server" <> ''`) are the smoke test.
* Table 409 in NAV 2016 has fields 1–7 only (Password is plain Text250). **No "Password Key", no SetPassword/GetPassword.**
  Table 1261 "Service Password" (Key GUID, Value BLOB, `SavePassword(Text)`, `GetPassword() : Text`, encryption via CU 1266 when a key exists)
  and the Password-Key pattern of Table 1260 exist in NAV 2016 → reuse them.
* `[TryFunction]` exists in NAV 2016 (attribute line above PROCEDURE in TXT exports). DotNet vars: `Name@id : DotNet "'Asm, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.Ns.Type";`
  (server-side default, `RUNONCLIENT` suffix only for client-side). Unsigned assemblies are fine. NAV binds by AssemblyVersion.
* Attachments arrive as server temp files (FileManagement.ServerTempFileName); callers may delete them after TrySend → read them during Send (we do).
* NAV 2016 Service folder already ships Newtonsoft.Json 6.0.0.0 and ADAL 2.x → never depend on them.
* C/SIDE (finsql.exe) is 32-bit → AnyCPU (default). Copy the DLL to `RoleTailored Client\Add-ins` on the dev machine to compile.

### NAV integration – revised
Keep **all** Codeunit 400 signatures. Our Codeunit 50400 "Graph Mail" mirrors them (CreateMessage, TrySend, Send, AddRecipients, AddCC, AddBCC,
AppendBody, AddAttachment(Attachment,FileName), AddAttachmentStream(InStream,Name), GetLastSendMailErrorText, GetBody) plus IsEnabled,
TestConnection, SendTestMail and the secret helpers `SetClientSecret(VAR SMTPMailSetup;Secret)`, `GetClientSecret(SMTPMailSetup) : Text`,
`HasClientSecret(SMTPMailSetup) : Boolean`, `DeleteClientSecret(VAR SMTPMailSetup)` (Service Password pattern of Table 1260, so the Table 409 patch is fields only).
Codeunit 400 patch: global `GraphMail@50100 : Codeunit 50400` and `UseGraph@50101 : Boolean`; CreateMessage checks `GraphMail.IsEnabled` **before**
`TESTFIELD("SMTP Server")`; every function delegates when UseGraph; `GetSMTPMessage` errors in Graph mode; `IsEnabled` = original OR GraphMail.IsEnabled;
the Service Connection subscriber reports Enabled when Graph is enabled.
Page 409 patch: group "Microsoft Graph", masked secret variable (OnValidate → GraphMail.SetClientSecret), actions "Test Graph Connection" and
"Send Graph Test Email"; `CanSendTestMail` also true when Graph is enabled.

Table 409 fields (revised):

| No.   | Name                          | Type                               | Notes |
|-------|-------------------------------|------------------------------------|-------|
| 50100 | Mail Transport                | Option `SMTP,Microsoft Graph`      | Graph active when = Microsoft Graph |
| 50101 | Graph Tenant ID               | Text 250                           | |
| 50102 | Graph Client ID               | Text 250                           | |
| 50103 | Graph Auth Method             | Option `Client Secret,Certificate` | |
| 50104 | Graph Secret Key              | GUID, TableRelation "Service Password".Key | managed by CU 50400 |
| 50105 | Graph Cert. Thumbprint        | Text 100                           | LocalMachine\My of the NAV service account |
| 50106 | Graph Sender Mailbox          | Text 250                           | blank → send through the caller's From address mailbox |
| 50107 | Graph Save to Sent Items      | Boolean (InitValue Yes)            | |
| 50108 | Graph Reply-To Caller         | Boolean (InitValue Yes)            | with fixed Sender Mailbox: put the caller's From address into Reply-To when it differs |
| 50109 | Graph Token Library           | Option `Built-in,MSAL`             | |
| 50110 | Graph Cloud                   | Option `Global,US Government,US Government DoD,China` | |
| 50111 | Graph Large Messages          | Boolean (InitValue Yes)            | draft/upload flow when the sendMail estimate (incl. base64) > 3.5 MiB, i.e. attachments > ~2.5 MB (needs Mail.ReadWrite) |
| 50112 | Graph Log File Path           | Text 250                           | optional diagnostic log on the NAV server |

With a fixed Sender Mailbox the From address is always the mailbox (no Send As needed); the caller's address goes into Reply-To.

### Build
* net462 targets: explicit `<Reference Include="System.Net.Http" />` (done). Tools TFM stays net8.0 for now (local SDK 8/9); CI installs SDK 8 and 10.
  .NET 8 reaches end of support 2026-11-10 → move tools/tests to net10.0 then (tracked in CHANGELOG).
* DataContractJsonSerializer `@odata.type` key on .NET Framework must be verified by a net462 test on Windows CI.
* Strong naming: not used (PublicKeyToken=null is supported by NAV).
