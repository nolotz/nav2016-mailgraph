using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Auth;
using NavMailGraph.Graph;
using NavMailGraph.Internal;

// ------------------------------------------------------------------------------------------------------------------
// Test hooks (internal, visible to NavMailGraph.Tests through InternalsVisibleTo; never part of the C/AL surface):
//
//   GraphMailSender.CreateForTests(tenantId, clientId, HttpMessageHandler handler)
//       Sender whose Graph calls AND built-in token requests go through `handler` (a fake handler) instead of the
//       process-wide HttpClient. The internal ctor GraphMailSender(tenantId, clientId, HttpClient) does the same for a
//       caller-built HttpClient.
//   sender.UseTokenProvider(ITokenProvider)   (public)
//       Skips Entra entirely; combine with CreateForTests to test the Graph flow with a fake token.
//   sender.RetryDelayOverride = (delay, ct) => Task.CompletedTask-like
//       Replaces Task.Delay for Graph and token-endpoint retry pauses so retry tests run instantly (record `delay` to assert
//       Retry-After handling).
//   GraphMailSender.ResetTokenCacheForTests()
//       Clears the static, process-wide token cache (NavMailGraph.Auth.TokenCache) between tests.
//   Internal static helpers: EstimatePayloadBytes, BuildGraphMessage, BuildFileAttachment, BuildUploadSessionRequest,
//   NormalizeImportance, ValidateSizes; NavMailGraph.Internal.ErrorHints.GetHint; NavMailGraph.Graph.GraphClient.UserSegment.
// ------------------------------------------------------------------------------------------------------------------

namespace NavMailGraph
{
    /// <summary>
    /// Configures authentication and options and sends messages through Microsoft Graph (application permissions,
    /// client-credentials flow). One instance per NAV session is fine: the token cache and the HttpClient are static,
    /// process-wide and thread-safe. All operations are synchronous and never throw for auth/Graph/IO/validation
    /// problems; they return the complete error text instead (see docs/DESIGN.md section 4).
    /// </summary>
    public sealed partial class GraphMailSender
    {
        internal const int DefaultTimeoutSeconds = 100;

        internal const int DefaultMaxRetries = 3;

        /// <summary>3.5 MiB: the sendMail request (JSON incl. base64 attachments) must stay below Graph's 4 MiB request limit.</summary>
        internal const long DefaultSendMailPayloadLimitBytes = 3670016;

        /// <summary>3 MiB: Graph accepts fileAttachment POSTs up to 3 MB; larger content needs an upload session.</summary>
        internal const long DefaultDirectAttachmentLimitBytes = 3145728;

        /// <summary>3,932,160 bytes (12 x 320 KiB): below the documented "up to 4 MB" per PUT. Outlook upload sessions have no 320 KiB alignment rule.</summary>
        internal const int DefaultUploadChunkBytes = 3932160;

        /// <summary>150 MiB, the Graph/Exchange Online maximum for a single attachment.</summary>
        internal const long DefaultMaxAttachmentBytes = 157286400;

        /// <summary>4 MiB: Microsoft recommends upload ranges of "up to 4 MB each time".</summary>
        internal const int MaxUploadChunkBytes = 4194304;

        /// <summary>
        /// 3,000,000 bytes: lower bound of the documented upload-session range ("between 3 MB and 150 MB"; whether "MB" means
        /// 10^6 or 2^20 bytes is not documented). A direct attachment POST of at least this size that Graph rejects with
        /// HTTP 413 is retried once through an upload session.
        /// </summary>
        internal const long UploadSessionMinimumBytes = 3000000;

        internal const string BuiltInLibraryName = "BuiltIn";

        internal const string MsalLibraryName = "Msal";

        internal const string RepositoryUrl = "https://github.com/nolotz/nav2016-mailgraph";

        /// <summary>Fixed JSON overhead reserved in the sendMail size estimate (envelope, recipients, headers).</summary>
        internal const long PayloadEstimateOverheadBytes = 2048;

        /// <summary>Per-attachment JSON overhead reserved in the sendMail size estimate.</summary>
        internal const long PayloadEstimateAttachmentOverheadBytes = 512;

        private static readonly string InformationalVersion = ReadInformationalVersion();

        /// <summary>User-Agent sent on every HTTP request.</summary>
        internal static readonly string UserAgent = "NavMailGraph/" + InformationalVersion + " (+" + RepositoryUrl + ")";

        private enum CredentialKind
        {
            None,
            ClientSecret,
            CertificateThumbprint,
            CertificateFile,
            Provider,
        }

        private string _tenantId;

        private string _clientId;

        private HttpClient? _httpClient;

        private readonly Diagnostics _diag = new Diagnostics();

        private CredentialKind _credentialKind = CredentialKind.None;

        private string _clientSecret = "";

        private string _thumbprint = "";

        private string _pfxPath = "";

        private string _pfxPassword = "";

        private ITokenProvider? _explicitProvider;

        private ITokenProvider? _builtProvider;

        private X509Certificate2? _certificate;

        private string _tokenLibrary = BuiltInLibraryName;

        private string _cloud = CloudEndpoints.GlobalName;

        private string _graphBaseUrl = "";

        private string _authorityHost = "";

        private string _senderMailbox = "";

        private bool _sendX5C;

        private int _timeoutSeconds = DefaultTimeoutSeconds;

        private string _logFilePath = "";

        private Func<TimeSpan, CancellationToken, Task>? _retryDelayOverride;

        private int _maxRetries = DefaultMaxRetries;

        private ObjectStorageClient? _linkStorage;

        private long _linkThresholdBytes;

        private int _linkValidMinutes;

        private string _linkText = "";

        /// <summary>Creates a sender for an Entra app registration.</summary>
        /// <param name="tenantId">Tenant id (GUID) or verified domain name.</param>
        /// <param name="clientId">Application (client) id.</param>
        public GraphMailSender(string tenantId, string clientId)
            : this(tenantId, clientId, null)
        {
        }

        /// <summary>Test hook: uses <paramref name="httpClient"/> (e.g. built over a fake handler) instead of the static client.</summary>
        internal GraphMailSender(string tenantId, string clientId, HttpClient? httpClient)
        {
            // Process-wide TLS 1.2 / ServicePointManager setup (net462) must happen before ANY outbound request, including
            // MSAL's own token requests, which never touch HttpClientProvider.Client.
            HttpClientProvider.EnsureInitialized();
            _tenantId = (tenantId ?? "").Trim();
            _clientId = (clientId ?? "").Trim();
            _httpClient = httpClient;
        }

        /// <summary>Test hook: a sender whose HTTP traffic (Graph and token endpoint) goes through <paramref name="handler"/>.</summary>
        internal static GraphMailSender CreateForTests(string tenantId, string clientId, HttpMessageHandler handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return new GraphMailSender(tenantId, clientId, HttpClientProvider.Create(handler));
        }

        /// <summary>Test hook: clears the static, process-wide token cache.</summary>
        internal static void ResetTokenCacheForTests()
        {
            TokenCache.Clear();
        }

        /// <summary>Test hook: replaces Task.Delay for retry pauses (null restores Task.Delay).</summary>
        internal Func<TimeSpan, CancellationToken, Task>? RetryDelayOverride
        {
            get { return _retryDelayOverride; }
            set
            {
                _retryDelayOverride = value;
                ResetProvider();
            }
        }

        /// <summary>Authenticates with a client secret.</summary>
        public void UseClientSecret(string clientSecret)
        {
            _credentialKind = CredentialKind.ClientSecret;
            _clientSecret = clientSecret ?? "";
            _explicitProvider = null;
            ResetCredentialState();
        }

        /// <summary>Authenticates with a certificate from LocalMachine\My (first) or CurrentUser\My. Whitespace and non-hex characters in the thumbprint are ignored.</summary>
        public void UseCertificateThumbprint(string thumbprint)
        {
            _credentialKind = CredentialKind.CertificateThumbprint;
            _thumbprint = thumbprint ?? "";
            _explicitProvider = null;
            ResetCredentialState();
        }

        /// <summary>Authenticates with a certificate from a PFX file.</summary>
        public void UseCertificateFile(string pfxPath, string pfxPassword)
        {
            _credentialKind = CredentialKind.CertificateFile;
            _pfxPath = pfxPath ?? "";
            _pfxPassword = pfxPassword ?? "";
            _explicitProvider = null;
            ResetCredentialState();
        }

        /// <summary>Uses a caller-supplied token provider (tests, CLI, custom auth). Overrides TokenLibrary and the credentials.</summary>
        public void UseTokenProvider(ITokenProvider provider)
        {
            _explicitProvider = provider ?? throw new ArgumentNullException(nameof(provider));
            _credentialKind = CredentialKind.Provider;
            ResetCredentialState();
        }

        /// <summary>
        /// Copies the connection settings of <paramref name="connection"/> (tenant, client, credential, token library, cloud,
        /// Graph base URL, authority host, SendX5C, timeout, retries, log path) into this sender. Sender-specific settings
        /// (SenderMailbox, SaveToSentItems, size limits, ...) are kept. The copy is a snapshot: call UseConnection again after
        /// changing the connection.
        /// </summary>
        public void UseConnection(GraphConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            GraphMailSender source = connection.Settings;
            _tenantId = source._tenantId;
            _clientId = source._clientId;
            if (source._httpClient != null)
            {
                _httpClient = source._httpClient;
            }

            _credentialKind = source._credentialKind;
            _clientSecret = source._clientSecret;
            _thumbprint = source._thumbprint;
            _pfxPath = source._pfxPath;
            _pfxPassword = source._pfxPassword;
            _explicitProvider = source._explicitProvider;
            _tokenLibrary = source._tokenLibrary;
            _cloud = source._cloud;
            _graphBaseUrl = source._graphBaseUrl;
            _authorityHost = source._authorityHost;
            _sendX5C = source._sendX5C;
            _timeoutSeconds = source._timeoutSeconds;
            _maxRetries = source._maxRetries;
            _logFilePath = source._logFilePath;
            if (source._retryDelayOverride != null)
            {
                _retryDelayOverride = source._retryDelayOverride;
            }

            ResetCredentialState();
        }

        /// <summary>
        /// Sends large attachments as download links instead (docs/DESIGN-v2.md section 2.4): at Send, every non-inline attachment
        /// of <paramref name="thresholdBytes"/> or more is uploaded to <paramref name="storage"/> under
        /// "mail-attachments/{yyyy}/{MM}/{guid}/{fileName}" (after the storage's KeyPrefix) and replaced by a link line appended to
        /// the body (an HTML &lt;a&gt; paragraph for HTML bodies, inserted before &lt;/body&gt; when present; a plain "text: url" line
        /// for text bodies). The link is created with GetDownloadUrl semantics and is valid for <paramref name="validMinutes"/>
        /// (S3 at most 7 days; in Azure SAS-URL mode the SAS's own expiry applies; not available in Azure Entra mode).
        /// <paramref name="linkText"/> is a .NET format string: {0} = file name, {1} = size (e.g. "20.0 MB"), {2} = link expiry
        /// ("yyyy-MM-dd HH:mm UTC"); "" = "Download {0} ({1})". If a link cannot be created or the upload fails, Send fails with the
        /// error text and nothing is sent. The storage client is used with its settings at send time. Throws
        /// <see cref="ArgumentNullException"/> only for a null storage; invalid numbers are reported by Send.
        /// </summary>
        public void UseLinkStorage(ObjectStorageClient storage, long thresholdBytes, int validMinutes, string linkText)
        {
            _linkStorage = storage ?? throw new ArgumentNullException(nameof(storage));
            _linkThresholdBytes = thresholdBytes;
            _linkValidMinutes = validMinutes;
            _linkText = linkText ?? "";
        }

        /// <summary>Turns link storage off again: all attachments are sent as attachments.</summary>
        public void ClearLinkStorage()
        {
            _linkStorage = null;
            _linkThresholdBytes = 0;
            _linkValidMinutes = 0;
            _linkText = "";
        }

        /// <summary>"BuiltIn" (default) or "Msal" (loads NavMailGraph.Msal.dll by reflection at the first Send). Case-insensitive.</summary>
        public string TokenLibrary
        {
            get { return _tokenLibrary; }
            set
            {
                _tokenLibrary = (value ?? "").Trim();
                ResetProvider();
            }
        }

        /// <summary>
        /// "Global" (default), "USGov" (US Government L4 / GCC High), "USGovDoD" (US Government L5) or "China" (21Vianet);
        /// case-insensitive. Selects authority host and Graph base URL. Upload sessions (attachments of
        /// DirectAttachmentLimitBytes or more in the draft flow) are not available in USGov/USGovDoD.
        /// </summary>
        public string Cloud
        {
            get { return _cloud; }
            set
            {
                _cloud = (value ?? "").Trim();
                ResetProvider();
            }
        }

        /// <summary>Explicit Graph base URL incl. API version (e.g. "https://graph.microsoft.com/v1.0"); overrides Cloud. The token scope follows this host.</summary>
        public string GraphBaseUrl
        {
            get { return _graphBaseUrl; }
            set
            {
                _graphBaseUrl = (value ?? "").Trim();
                ResetProvider();
            }
        }

        /// <summary>Explicit authority host (e.g. "https://login.microsoftonline.com"); overrides Cloud.</summary>
        public string AuthorityHost
        {
            get { return _authorityHost; }
            set
            {
                _authorityHost = (value ?? "").Trim();
                ResetProvider();
            }
        }

        /// <summary>
        /// Mailbox used in /users/{mailbox}/...; "" uses the message's FromAddress. The mail is always sent as this mailbox
        /// (the Graph "from" property is never set, so no Send As right is needed); see <see cref="ReplyToFromAddress"/>.
        /// </summary>
        public string SenderMailbox
        {
            get { return _senderMailbox; }
            set { _senderMailbox = (value ?? "").Trim(); }
        }

        /// <summary>
        /// When the message's FromAddress differs from the mailbox (fixed <see cref="SenderMailbox"/>), add FromName/FromAddress
        /// to Reply-To so answers reach the original sender (default true; skipped when the address is already in Reply-To).
        /// </summary>
        public bool ReplyToFromAddress { get; set; } = true;

        /// <summary>
        /// Use the draft flow (create draft, add attachments / upload sessions, send) for messages whose estimated sendMail
        /// request exceeds <see cref="SendMailPayloadLimitBytes"/> (default true). The draft flow needs Mail.ReadWrite in
        /// addition to Mail.Send. When false, such messages fail with a validation error instead.
        /// </summary>
        public bool UseDraftForLargeMessages { get; set; } = true;

        /// <summary>Save sent mail to the mailbox's Sent Items (default true). Only the direct sendMail path honours false; the draft path always saves.</summary>
        public bool SaveToSentItems { get; set; } = true;

        /// <summary>Include the x5c claim (public certificate) in the client assertion for certificate auto-rotation / SNI. Default false.</summary>
        public bool SendX5C
        {
            get { return _sendX5C; }
            set
            {
                _sendX5C = value;
                ResetProvider();
            }
        }

        /// <summary>Timeout per HTTP request in seconds (default 100; values below 1 use the default).</summary>
        public int TimeoutSeconds
        {
            get { return _timeoutSeconds; }
            set
            {
                _timeoutSeconds = value;
                ResetProvider();
            }
        }

        /// <summary>
        /// Retries per request (default 3; 0 disables retries): Graph calls on HTTP 429/503/509 (upload ranges also on 504;
        /// sendMail and send are never repeated after a 504 or a timeout), built-in token requests on 429/5xx and transport errors.
        /// </summary>
        public int MaxRetries
        {
            get { return _maxRetries; }
            set
            {
                _maxRetries = value;
                ResetProvider();
            }
        }

        /// <summary>Largest estimated sendMail request that is sent directly (default 3,670,016 = 3.5 MiB). Above it the draft flow is used.</summary>
        public long SendMailPayloadLimitBytes { get; set; } = DefaultSendMailPayloadLimitBytes;

        /// <summary>In the draft flow, attachments below this size are POSTed directly, larger ones go through an upload session (default 3,145,728 = 3 MiB).</summary>
        public long DirectAttachmentLimitBytes { get; set; } = DefaultDirectAttachmentLimitBytes;

        /// <summary>Range size for upload sessions (default 3,932,160 bytes; values above 4 MiB are capped at 4 MiB).</summary>
        public int UploadChunkBytes { get; set; } = DefaultUploadChunkBytes;

        /// <summary>Largest accepted attachment (default 157,286,400 = 150 MiB, the Graph maximum).</summary>
        public long MaxAttachmentBytes { get; set; } = DefaultMaxAttachmentBytes;

        /// <summary>Optional append-only diagnostic log file (secrets redacted). "" disables it. IO errors are ignored.</summary>
        public string LogFilePath
        {
            get { return _logFilePath; }
            set { _logFilePath = (value ?? "").Trim(); }
        }

        /// <summary>
        /// Sends the message. Returns "" on success, otherwise the complete error text. Never throws for Graph, auth,
        /// IO or validation errors; throws <see cref="ArgumentNullException"/> only for a null message.
        /// </summary>
        public string Send(GraphMailMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            BeginOperation("Send");
            LastMessageId = "";
            try
            {
                AsyncHelper.RunSync(() => SendWithStorageAsync(message, CancellationToken.None));
                _diag.Log("Send completed" + (LastMessageId.Length > 0 ? " (message id " + LastMessageId + ")" : ""));
                return "";
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        /// <summary>Acquires a fresh token (bypassing the cache) without sending mail. "" on success, else the error text.</summary>
        public string TestConnection()
        {
            BeginOperation("TestConnection");
            try
            {
                AsyncHelper.RunSync(() => TestConnectionCoreAsync(CancellationToken.None));
                return "";
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        /// <summary>Graph message id of the last draft-based send; "" after a direct sendMail or a failure.</summary>
        public string LastMessageId { get; private set; } = "";

        /// <summary>Graph request-id (or Entra correlation id) of the last failed call, for Microsoft support. "" if none.</summary>
        public string LastRequestId { get; private set; } = "";

        /// <summary>Redacted diagnostic log of the last Send/TestConnection (what NAV shows on error).</summary>
        public string LastDiagnostics
        {
            get { return _diag.Text; }
        }

        /// <summary>Informational version of the assembly (the release number, not the pinned AssemblyVersion).</summary>
        public static string Version
        {
            get { return InformationalVersion; }
        }

        // ----------------------------------------------------------------------------------------------------------
        // Internals (visible to NavMailGraph.Tests)
        // ----------------------------------------------------------------------------------------------------------

        internal HttpClient HttpClient
        {
            get { return _httpClient ?? HttpClientProvider.Client; }
        }

        internal int EffectiveTimeoutSeconds
        {
            get { return _timeoutSeconds > 0 ? _timeoutSeconds : DefaultTimeoutSeconds; }
        }

        internal int EffectiveMaxRetries
        {
            get { return MaxRetries < 0 ? 0 : MaxRetries; }
        }

        internal long EffectiveSendMailPayloadLimitBytes
        {
            get { return SendMailPayloadLimitBytes > 0 ? SendMailPayloadLimitBytes : DefaultSendMailPayloadLimitBytes; }
        }

        internal long EffectiveDirectAttachmentLimitBytes
        {
            get { return DirectAttachmentLimitBytes > 0 ? DirectAttachmentLimitBytes : DefaultDirectAttachmentLimitBytes; }
        }

        internal int EffectiveUploadChunkBytes
        {
            get
            {
                if (UploadChunkBytes <= 0)
                {
                    return DefaultUploadChunkBytes;
                }

                return UploadChunkBytes > MaxUploadChunkBytes ? MaxUploadChunkBytes : UploadChunkBytes;
            }
        }

        internal long EffectiveMaxAttachmentBytes
        {
            get { return MaxAttachmentBytes > 0 ? MaxAttachmentBytes : DefaultMaxAttachmentBytes; }
        }

        internal CloudEndpoints ResolveEndpoints()
        {
            return CloudEndpoints.Resolve(_cloud);
        }

        internal string EffectiveGraphBaseUrl(CloudEndpoints endpoints)
        {
            return _graphBaseUrl.Length > 0 ? _graphBaseUrl.TrimEnd('/') : endpoints.GraphBaseUrl;
        }

        internal string EffectiveAuthorityHost(CloudEndpoints endpoints)
        {
            return _authorityHost.Length > 0 ? _authorityHost.TrimEnd('/') : endpoints.AuthorityHost;
        }

        /// <summary>The cloud's ".default" scope, or one derived from an explicit GraphBaseUrl so the token matches the resource.</summary>
        internal string EffectiveScope(CloudEndpoints endpoints)
        {
            return _graphBaseUrl.Length > 0 ? CloudEndpoints.ScopeFromGraphBaseUrl(EffectiveGraphBaseUrl(endpoints)) : endpoints.Scope;
        }

        /// <summary>Resolves the mailbox: SenderMailbox, else the message's FromAddress.</summary>
        internal string ResolveMailbox(GraphMailMessage message)
        {
            string mailbox = _senderMailbox.Length > 0 ? _senderMailbox : message.FromAddress;
            if (mailbox.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: sender mailbox is required (set SenderMailbox or the message's FromAddress).");
            }

            return mailbox;
        }

        /// <summary>Returns the explicit provider, the cached built provider, or builds one from the settings.</summary>
        internal ITokenProvider ResolveTokenProvider(CloudEndpoints endpoints)
        {
            if (_explicitProvider != null)
            {
                return _explicitProvider;
            }

            if (_builtProvider != null)
            {
                return _builtProvider;
            }

            ITokenProvider provider = BuildProvider(endpoints, EffectiveScope(endpoints));
            _builtProvider = provider;
            return provider;
        }

        /// <summary>
        /// A token provider for another resource of the same app registration (e.g. Azure Storage, scope
        /// https://storage.azure.com/.default). The built-in/MSAL token caches are keyed by scope, so tokens are reused.
        /// An explicit provider (<see cref="UseTokenProvider"/>) is returned unchanged: it must issue tokens for that resource itself.
        /// </summary>
        internal ITokenProvider CreateTokenProviderForScope(CloudEndpoints endpoints, string scope)
        {
            if (_explicitProvider != null)
            {
                return _explicitProvider;
            }

            return BuildProvider(endpoints, scope);
        }

        /// <summary>True when the credential is a caller-supplied <see cref="ITokenProvider"/>.</summary>
        internal bool HasExplicitTokenProvider
        {
            get { return _explicitProvider != null; }
        }

        internal string TenantId
        {
            get { return _tenantId; }
        }

        internal string ClientId
        {
            get { return _clientId; }
        }

        private ITokenProvider BuildProvider(CloudEndpoints endpoints, string scope)
        {
            if (_tenantId.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: Tenant ID is required.");
            }

            if (_clientId.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: Client ID is required.");
            }

            if (IsMultiTenantAlias(_tenantId))
            {
                // https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/web-apps-apis/client-credential-flows
                throw GraphMailException.Validation("Validation failed: Tenant ID '" + _tenantId + "' cannot be used with client credentials; use the tenant GUID or a verified domain (e.g. contoso.onmicrosoft.com).");
            }

            if (_credentialKind == CredentialKind.None)
            {
                throw GraphMailException.Validation("Validation failed: no credential configured (call UseClientSecret, UseCertificateThumbprint, UseCertificateFile or UseTokenProvider).");
            }

            if (_tokenLibrary.Length == 0 || string.Equals(_tokenLibrary, BuiltInLibraryName, StringComparison.OrdinalIgnoreCase))
            {
                return new BuiltInTokenProvider(BuildTokenProviderSettings(endpoints, scope), HttpClient, _diag, EffectiveTimeoutSeconds, EffectiveMaxRetries, _retryDelayOverride);
            }

            if (string.Equals(_tokenLibrary, MsalLibraryName, StringComparison.OrdinalIgnoreCase))
            {
                return MsalBridge.Create(BuildTokenProviderSettings(endpoints, scope));
            }

            throw GraphMailException.Validation("Validation failed: unknown TokenLibrary '" + _tokenLibrary + "' (expected BuiltIn or Msal).");
        }

        /// <summary>Builds the settings handed to the built-in or MSAL provider (loads the certificate when needed).</summary>
        internal TokenProviderSettings BuildTokenProviderSettings(CloudEndpoints endpoints)
        {
            return BuildTokenProviderSettings(endpoints, EffectiveScope(endpoints));
        }

        /// <summary>Same as <see cref="BuildTokenProviderSettings(CloudEndpoints)"/> for an explicit scope.</summary>
        internal TokenProviderSettings BuildTokenProviderSettings(CloudEndpoints endpoints, string scope)
        {
            var settings = new TokenProviderSettings
            {
                TenantId = _tenantId,
                ClientId = _clientId,
                AuthorityHost = EffectiveAuthorityHost(endpoints),
                Scope = scope,
                SendX5C = _sendX5C,
            };

            switch (_credentialKind)
            {
                case CredentialKind.ClientSecret:
                    if (_clientSecret.Length == 0)
                    {
                        throw GraphMailException.Validation("Validation failed: the client secret is empty.");
                    }

                    settings.ClientSecret = _clientSecret;
                    break;
                case CredentialKind.CertificateThumbprint:
                case CredentialKind.CertificateFile:
                    settings.Certificate = LoadCertificate();
                    break;
                default:
                    throw GraphMailException.Validation("Validation failed: no credential configured (call UseClientSecret, UseCertificateThumbprint, UseCertificateFile or UseTokenProvider).");
            }

            return settings;
        }

        /// <summary>sendMail size estimate: utf8(body) + utf8(subject) + sum(ceil(size/3)*4 + 512) + 2048.</summary>
        internal static long EstimatePayloadBytes(GraphMailMessage message, IList<long> attachmentSizes)
        {
            long estimate = Encoding.UTF8.GetByteCount(message.Body) + Encoding.UTF8.GetByteCount(message.Subject) + PayloadEstimateOverheadBytes;
            foreach (long size in attachmentSizes)
            {
                estimate += ((size + 2) / 3) * 4 + PayloadEstimateAttachmentOverheadBytes;
            }

            return estimate;
        }

        /// <summary>True when FromAddress is set and is not the mailbox the mail is sent through.</summary>
        internal static bool FromDiffersFromMailbox(GraphMailMessage message, string mailbox)
        {
            return message.FromAddress.Length > 0 && !string.Equals(message.FromAddress, mailbox, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps the builder to the Graph message resource. Attachments are embedded only when <paramref name="embeddedAttachments"/> is given.
        /// The "from" property is never set: the mail is sent as <paramref name="mailbox"/> (docs/DESIGN.md section 11). When
        /// FromAddress differs from the mailbox and <paramref name="replyToFromAddress"/> is true, it is added to Reply-To.
        /// </summary>
        internal static GraphMessage BuildGraphMessage(GraphMailMessage message, string mailbox, bool replyToFromAddress, IList<MailAttachment>? embeddedAttachments)
        {
            var replyTo = new List<MailRecipient>(message.ReplyToList);
            if (replyToFromAddress && FromDiffersFromMailbox(message, mailbox))
            {
                bool present = false;
                foreach (MailRecipient existing in replyTo)
                {
                    if (string.Equals(existing.Address, message.FromAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    replyTo.Add(new MailRecipient(message.FromAddress, message.FromName));
                }
            }

            var result = new GraphMessage
            {
                Subject = message.Subject,
                Body = new GraphItemBody { ContentType = message.HtmlFormatted ? "HTML" : "Text", Content = message.Body },
                ToRecipients = ToGraphRecipients(message.ToList),
                CcRecipients = ToGraphRecipients(message.CcList),
                BccRecipients = ToGraphRecipients(message.BccList),
                ReplyTo = ToGraphRecipients(replyTo),
            };

            string importance = NormalizeImportance(message.Importance);
            if (importance.Length > 0)
            {
                result.Importance = importance;
            }

            if (message.HeaderList.Count > 0)
            {
                result.InternetMessageHeaders = new List<GraphInternetMessageHeader>();
                foreach (KeyValuePair<string, string> header in message.HeaderList)
                {
                    result.InternetMessageHeaders.Add(new GraphInternetMessageHeader { Name = header.Key, Value = header.Value });
                }
            }

            if (embeddedAttachments != null && embeddedAttachments.Count > 0)
            {
                result.Attachments = new List<GraphFileAttachment>();
                foreach (MailAttachment attachment in embeddedAttachments)
                {
                    result.Attachments.Add(BuildFileAttachment(attachment));
                }
            }

            return result;
        }

        /// <summary>Reads the content and builds a base64 fileAttachment.</summary>
        internal static GraphFileAttachment BuildFileAttachment(MailAttachment attachment)
        {
            byte[] content;
            try
            {
                content = attachment.ReadAllBytes();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw GraphMailException.Validation("Attachment '" + attachment.Name + "' cannot be read: " + attachment.FilePath + " (" + ex.Message + ")", ex);
            }

            return new GraphFileAttachment
            {
                ODataType = GraphFileAttachment.ODataTypeValue,
                Name = attachment.Name,
                ContentType = attachment.ContentType,
                ContentBytes = Convert.ToBase64String(content),
                IsInline = attachment.IsInline ? true : (bool?)null,
                ContentId = attachment.IsInline && attachment.ContentId.Length > 0 ? attachment.ContentId : null,
            };
        }

        internal static UploadSessionRequest BuildUploadSessionRequest(MailAttachment attachment, long size)
        {
            // https://learn.microsoft.com/en-us/graph/api/resources/attachmentitem
            return new UploadSessionRequest
            {
                AttachmentItem = new AttachmentItem
                {
                    AttachmentType = "file",
                    Name = attachment.Name,
                    Size = size,
                    ContentType = attachment.ContentType,
                    IsInline = attachment.IsInline ? true : (bool?)null,
                    ContentId = attachment.IsInline && attachment.ContentId.Length > 0 ? attachment.ContentId : null,
                },
            };
        }

        /// <summary>"" stays ""; "low"/"normal"/"high" are lower-cased; anything else is a validation error.</summary>
        internal static string NormalizeImportance(string importance)
        {
            string value = (importance ?? "").Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "low" || value == "normal" || value == "high")
            {
                return value;
            }

            throw GraphMailException.Validation("Validation failed: Importance must be '', 'low', 'normal' or 'high' (got '" + importance + "').");
        }

        // ----------------------------------------------------------------------------------------------------------
        // Send algorithm (docs/DESIGN.md section 5)
        // ----------------------------------------------------------------------------------------------------------

        private async Task SendCoreAsync(GraphMailMessage message, CancellationToken cancellationToken)
        {
            // 1. Validate everything that needs no network (configuration, message, attachments, size plan).
            string mailbox = ResolveMailbox(message);
            CloudEndpoints endpoints = ResolveEndpoints();
            string graphBaseUrl = EffectiveGraphBaseUrl(endpoints);
            LogSettings(endpoints, mailbox);
            _diag.Log("message: " + message.Describe());
            ValidateMessage(message);
            List<MailAttachment> attachments = new List<MailAttachment>(message.AttachmentList);
            List<long> sizes = ValidateAttachments(attachments);

            long estimate = EstimatePayloadBytes(message, sizes);
            long limit = EffectiveSendMailPayloadLimitBytes;
            bool uploadSessionsAvailable = endpoints.SupportsUploadSessions && !CloudEndpoints.IsUsGovernmentGraphHost(graphBaseUrl);
            bool direct = ValidateSizes(attachments, sizes, estimate, limit, UseDraftForLargeMessages, EffectiveDirectAttachmentLimitBytes, uploadSessionsAvailable, endpoints.Name);
            _diag.Log("payload estimate " + estimate + " bytes (limit " + limit + "): " + (direct ? "direct sendMail" : "draft + attachments + send"));
            if (FromDiffersFromMailbox(message, mailbox))
            {
                _diag.Log("from address " + message.FromAddress + " differs from mailbox " + mailbox + ": sent as the mailbox"
                    + (ReplyToFromAddress ? ", from address added to Reply-To" : ""));
            }

            // 2. Token (surfaces auth problems before any payload is built).
            ITokenProvider provider = ResolveTokenProvider(endpoints);
            _diag.Log("token provider: " + provider.Describe());
            var client = new GraphClient(HttpClient, provider, _diag, graphBaseUrl, mailbox, EffectiveTimeoutSeconds, EffectiveMaxRetries, _retryDelayOverride);
            try
            {
                await GraphClient.AcquireTokenAsync(provider, false, cancellationToken).ConfigureAwait(false);

                // 3. One sendMail call, or the draft flow.
                if (direct)
                {
                    GraphMessage graphMessage = BuildGraphMessage(message, mailbox, ReplyToFromAddress, attachments);
                    await client.SendMailAsync(new SendMailRequest { Message = graphMessage, SaveToSentItems = SaveToSentItems }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!SaveToSentItems)
                {
                    _diag.Log("note: the draft flow always saves to Sent Items (POST /messages/{id}/send has no saveToSentItems option)");
                }

                string messageId = await client.CreateDraftAsync(BuildGraphMessage(message, mailbox, ReplyToFromAddress, null), cancellationToken).ConfigureAwait(false);
                _diag.Log("draft created: " + messageId);
                try
                {
                    long directLimit = EffectiveDirectAttachmentLimitBytes;
                    for (int i = 0; i < attachments.Count; i++)
                    {
                        MailAttachment attachment = attachments[i];
                        long size = sizes[i];
                        string label = "attachment " + (i + 1) + "/" + attachments.Count + " " + attachment.Describe();
                        if (size < directLimit)
                        {
                            _diag.Log(label + ": direct POST");
                            try
                            {
                                await client.AddAttachmentAsync(messageId, BuildFileAttachment(attachment), cancellationToken).ConfigureAwait(false);
                            }
                            catch (GraphMailException tooLarge) when (tooLarge.StatusCode == 413 && uploadSessionsAvailable && size >= UploadSessionMinimumBytes)
                            {
                                // The base64 JSON of a file just under 3 MiB is about 4 MiB; whether Graph's "4 MB" request
                                // limit is 4,000,000 or 4,194,304 bytes is not documented. A 413 means the POST was rejected
                                // (nothing was added), so the upload session is a safe second attempt.
                                _diag.Log(label + ": direct POST rejected with HTTP 413, retrying through an upload session");
                                await UploadAttachmentAsync(client, messageId, attachment, size, label, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await UploadAttachmentAsync(client, messageId, attachment, size, label, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    await client.SendDraftAsync(messageId, cancellationToken).ConfigureAwait(false);
                    LastMessageId = messageId;
                }
                catch (Exception ex)
                {
                    GraphMailException? graphError = ex as GraphMailException;
                    if (graphError != null && graphError.OutcomeUnknown)
                    {
                        // The send may have been processed: keep the draft so the administrator can see in Drafts / Sent
                        // Items whether the message went out (deleting it could also hit an already sent item).
                        _diag.Log("draft " + messageId + " kept: the outcome of the send is unknown");
                        throw;
                    }

                    // Best effort: do not leave half-built drafts behind in the mailbox. The original error is rethrown.
                    await client.TryDeleteDraftAsync(messageId).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                if (client.LastRequestId.Length > 0)
                {
                    LastRequestId = client.LastRequestId;
                }
            }
        }

        private async Task UploadAttachmentAsync(GraphClient client, string messageId, MailAttachment attachment, long size, string label, CancellationToken cancellationToken)
        {
            int chunk = EffectiveUploadChunkBytes;
            _diag.Log(label + ": upload session, " + chunk + "-byte ranges");
            string uploadUrl = await client.CreateUploadSessionAsync(messageId, BuildUploadSessionRequest(attachment, size), cancellationToken).ConfigureAwait(false);
            await client.UploadChunksAsync(uploadUrl, attachment, size, chunk, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Decides between direct sendMail (returns true) and the draft flow (returns false) and rejects plans that cannot
        /// work: draft flow disabled, or an attachment that would need an upload session in a cloud without upload sessions.
        /// </summary>
        internal static bool ValidateSizes(IList<MailAttachment> attachments, IList<long> sizes, long estimate, long sendMailLimit, bool useDraftForLargeMessages,
            long directAttachmentLimit, bool uploadSessionsAvailable, string cloudName)
        {
            if (estimate <= sendMailLimit)
            {
                return true;
            }

            if (!useDraftForLargeMessages)
            {
                throw GraphMailException.Validation(
                    "Validation failed: the message is too large for a single sendMail request (estimated " + estimate + " bytes, limit " + sendMailLimit
                    + " bytes) and UseDraftForLargeMessages is off. Larger messages are sent with the draft flow, which requires the Mail.ReadWrite "
                    + "application permission (Exchange RBAC role 'Application Mail.ReadWrite') in addition to Mail.Send. Enable it or send smaller attachments.");
            }

            if (!uploadSessionsAvailable)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    if (sizes[i] >= directAttachmentLimit)
                    {
                        throw GraphMailException.Validation(
                            "Validation failed: attachment '" + attachments[i].Name + "' (" + sizes[i] + " bytes) would need an upload session (attachments of "
                            + directAttachmentLimit + " bytes or more), but Microsoft Graph does not offer attachment upload sessions in the US Government "
                            + "L4/L5 clouds (Cloud=" + cloudName + "). Keep each attachment below " + directAttachmentLimit + " bytes.");
                    }
                }
            }

            return false;
        }

        /// <summary>True for "common", "organizations" and "consumers", which the client-credentials flow does not support.</summary>
        internal static bool IsMultiTenantAlias(string tenantId)
        {
            string value = (tenantId ?? "").Trim();
            return string.Equals(value, "common", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "organizations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "consumers", StringComparison.OrdinalIgnoreCase);
        }

        private async Task TestConnectionCoreAsync(CancellationToken cancellationToken)
        {
            CloudEndpoints endpoints = ResolveEndpoints();
            LogSettings(endpoints, _senderMailbox.Length > 0 ? _senderMailbox : "(from message)");
            ITokenProvider provider = ResolveTokenProvider(endpoints);
            _diag.Log("token provider: " + provider.Describe());
            AccessToken token = await GraphClient.AcquireTokenAsync(provider, true, cancellationToken).ConfigureAwait(false);
            _diag.Log("TestConnection succeeded: " + token);
        }

        private void ValidateMessage(GraphMailMessage message)
        {
            if (message.ValidationErrors.Count > 0)
            {
                string text = "Validation failed: " + message.ValidationErrors[0];
                if (message.ValidationErrors.Count > 1)
                {
                    text += " (+" + (message.ValidationErrors.Count - 1) + " more problem(s), see LastDiagnostics)";
                }

                foreach (string problem in message.ValidationErrors)
                {
                    _diag.Log("validation: " + problem);
                }

                throw GraphMailException.Validation(text);
            }

            if (message.RecipientCount == 0)
            {
                throw GraphMailException.Validation("Validation failed: at least one recipient is required.");
            }

            NormalizeImportance(message.Importance);
        }

        private List<long> ValidateAttachments(List<MailAttachment> attachments)
        {
            var sizes = new List<long>(attachments.Count);
            long max = EffectiveMaxAttachmentBytes;
            foreach (MailAttachment attachment in attachments)
            {
                if (!attachment.Exists())
                {
                    throw GraphMailException.Validation("Attachment '" + attachment.Name + "' not found: " + (attachment.FilePath ?? "(memory)"));
                }

                long size;
                try
                {
                    size = attachment.GetSize();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw GraphMailException.Validation("Attachment '" + attachment.Name + "' cannot be read: " + attachment.FilePath + " (" + ex.Message + ")", ex);
                }

                if (size > max)
                {
                    throw GraphMailException.Validation("Attachment '" + attachment.Name + "' is too large: " + size + " bytes (limit " + max + " bytes).");
                }

                sizes.Add(size);
            }

            return sizes;
        }

        private X509Certificate2 LoadCertificate()
        {
            if (_certificate != null)
            {
                return _certificate;
            }

            X509Certificate2 certificate = _credentialKind == CredentialKind.CertificateThumbprint
                ? CertificateLoader.LoadFromStore(_thumbprint)
                : CertificateLoader.LoadFromFile(_pfxPath, _pfxPassword);

            _diag.Log("certificate: " + certificate.Subject + " thumbprint=" + certificate.Thumbprint
                + " valid until " + certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (UTC)");
            string? warning = CertificateLoader.ValidityWarning(certificate, DateTime.UtcNow);
            if (warning != null)
            {
                _diag.Log("WARNING: " + warning);
            }

            _certificate = certificate;
            return certificate;
        }

        private void LogSettings(CloudEndpoints endpoints, string mailbox)
        {
            _diag.Log("settings: cloud=" + endpoints.Name + " graph=" + EffectiveGraphBaseUrl(endpoints) + " authority=" + EffectiveAuthorityHost(endpoints)
                + " tenant=" + _tenantId + " client=" + _clientId + " credential=" + _credentialKind + " tokenLibrary=" + (_tokenLibrary.Length > 0 ? _tokenLibrary : BuiltInLibraryName)
                + " mailbox=" + mailbox + " saveToSentItems=" + SaveToSentItems + " useDraftForLargeMessages=" + UseDraftForLargeMessages
                + " replyToFromAddress=" + ReplyToFromAddress + " timeout=" + EffectiveTimeoutSeconds + "s retries=" + EffectiveMaxRetries
                + " sendMailLimit=" + EffectiveSendMailPayloadLimitBytes + " directAttachmentLimit=" + EffectiveDirectAttachmentLimitBytes
                + " uploadChunk=" + EffectiveUploadChunkBytes + " maxAttachment=" + EffectiveMaxAttachmentBytes);
        }

        private void BeginOperation(string name)
        {
            _diag.LogFilePath = _logFilePath;
            _diag.BeginOperation(name);
            LastRequestId = "";
        }

        /// <summary>Converts any exception into the returned error text (redacted) and records it in the diagnostics.</summary>
        private string Fail(Exception exception)
        {
            GraphMailException? known = exception as GraphMailException;
            string text;
            if (known != null)
            {
                text = ErrorHints.Append(known.Message, ErrorHints.GetHint(known.ErrorCode, known.StatusCode, known.RequiresMailReadWrite, known.Message));
                if (known.RequestId.Length > 0)
                {
                    LastRequestId = known.RequestId;
                }
            }
            else if (exception is OperationCanceledException)
            {
                text = "Operation canceled: " + exception.Message;
            }
            else
            {
                text = "Unexpected error (" + exception.GetType().Name + "): " + GraphClient.FirstLine(exception.Message);
            }

            text = Diagnostics.Redact(text);
            _diag.Log("FAILED: " + text);
            if (known == null)
            {
                _diag.Log(exception.ToString());
            }

            return text;
        }

        private static List<GraphRecipient>? ToGraphRecipients(IList<MailRecipient> recipients)
        {
            if (recipients.Count == 0)
            {
                return null;
            }

            var result = new List<GraphRecipient>(recipients.Count);
            foreach (MailRecipient recipient in recipients)
            {
                result.Add(new GraphRecipient
                {
                    EmailAddress = new GraphEmailAddress
                    {
                        Address = recipient.Address,
                        Name = recipient.Name.Length > 0 ? recipient.Name : null,
                    },
                });
            }

            return result;
        }

        private void ResetCredentialState()
        {
            _certificate = null;
            ResetProvider();
        }

        private void ResetProvider()
        {
            _builtProvider = null;
        }

        private static string ReadInformationalVersion()
        {
            try
            {
                Assembly assembly = typeof(GraphMailSender).Assembly;
                AssemblyInformationalVersionAttribute? attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attribute != null && !string.IsNullOrEmpty(attribute.InformationalVersion))
                {
                    return attribute.InformationalVersion;
                }

                Version? version = assembly.GetName().Version;
                return version == null ? "0.0.0" : version.ToString();
            }
            catch (Exception)
            {
                return "0.0.0";
            }
        }
    }
}
