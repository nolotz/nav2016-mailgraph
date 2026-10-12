using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

// ------------------------------------------------------------------------------------------------------------------
// Test hooks (internal, visible to NavMailGraph.Tests; never part of the C/AL surface):
//   GraphConnection.CreateForTests(tenantId, clientId, HttpMessageHandler handler)  - Graph + token traffic through `handler`
//   connection.RetryDelayOverride                                                    - replaces Task.Delay for retry pauses
// ------------------------------------------------------------------------------------------------------------------

namespace NavMailGraph
{
    /// <summary>
    /// Shared Microsoft Graph connection settings (tenant, client, credential, cloud, token library, timeouts, retries, log
    /// path) for <see cref="GraphMailbox"/> and <see cref="GraphMailSender.UseConnection"/>, so credentials are configured
    /// once (docs/DESIGN-v2.md section 2.1). The settings and the token-provider logic are the ones of
    /// <see cref="GraphMailSender"/> (one implementation); token caches are static and process-wide.
    /// </summary>
    public sealed class GraphConnection
    {
        private readonly GraphMailSender _settings;

        /// <summary>Creates a connection for an Entra app registration.</summary>
        /// <param name="tenantId">Tenant id (GUID) or verified domain name.</param>
        /// <param name="clientId">Application (client) id.</param>
        public GraphConnection(string tenantId, string clientId)
            : this(new GraphMailSender(tenantId, clientId))
        {
        }

        private GraphConnection(GraphMailSender settings)
        {
            _settings = settings;
        }

        /// <summary>Test hook: a connection whose HTTP traffic (Graph and token endpoint) goes through <paramref name="handler"/>.</summary>
        internal static GraphConnection CreateForTests(string tenantId, string clientId, HttpMessageHandler handler)
        {
            return new GraphConnection(GraphMailSender.CreateForTests(tenantId, clientId, handler));
        }

        /// <summary>The settings holder (a sender instance that is never used for sending). Internal.</summary>
        internal GraphMailSender Settings
        {
            get { return _settings; }
        }

        /// <summary>Test hook: replaces Task.Delay for retry pauses (null restores Task.Delay).</summary>
        internal Func<TimeSpan, CancellationToken, Task>? RetryDelayOverride
        {
            get { return _settings.RetryDelayOverride; }
            set { _settings.RetryDelayOverride = value; }
        }

        /// <summary>Authenticates with a client secret.</summary>
        public void UseClientSecret(string clientSecret)
        {
            _settings.UseClientSecret(clientSecret);
        }

        /// <summary>Authenticates with a certificate from LocalMachine\My (first) or CurrentUser\My.</summary>
        public void UseCertificateThumbprint(string thumbprint)
        {
            _settings.UseCertificateThumbprint(thumbprint);
        }

        /// <summary>Authenticates with a certificate from a PFX file.</summary>
        public void UseCertificateFile(string pfxPath, string pfxPassword)
        {
            _settings.UseCertificateFile(pfxPath, pfxPassword);
        }

        /// <summary>Uses a caller-supplied token provider (tests, CLI, custom auth). Overrides TokenLibrary and the credentials.</summary>
        public void UseTokenProvider(ITokenProvider provider)
        {
            _settings.UseTokenProvider(provider);
        }

        /// <summary>"BuiltIn" (default) or "Msal". Case-insensitive.</summary>
        public string TokenLibrary
        {
            get { return _settings.TokenLibrary; }
            set { _settings.TokenLibrary = value; }
        }

        /// <summary>"Global" (default), "USGov", "USGovDoD" or "China"; case-insensitive.</summary>
        public string Cloud
        {
            get { return _settings.Cloud; }
            set { _settings.Cloud = value; }
        }

        /// <summary>Explicit Graph base URL incl. API version (e.g. "https://graph.microsoft.com/v1.0"); overrides Cloud.</summary>
        public string GraphBaseUrl
        {
            get { return _settings.GraphBaseUrl; }
            set { _settings.GraphBaseUrl = value; }
        }

        /// <summary>Explicit authority host (e.g. "https://login.microsoftonline.com"); overrides Cloud.</summary>
        public string AuthorityHost
        {
            get { return _settings.AuthorityHost; }
            set { _settings.AuthorityHost = value; }
        }

        /// <summary>Include the x5c claim in the client assertion (certificate auth). Default false.</summary>
        public bool SendX5C
        {
            get { return _settings.SendX5C; }
            set { _settings.SendX5C = value; }
        }

        /// <summary>Timeout per HTTP request in seconds (default 100). For streamed downloads it is the maximum time without receiving data.</summary>
        public int TimeoutSeconds
        {
            get { return _settings.TimeoutSeconds; }
            set { _settings.TimeoutSeconds = value; }
        }

        /// <summary>Retries per request on HTTP 429/503/509 (GET requests also on 504); default 3, 0 disables retries.</summary>
        public int MaxRetries
        {
            get { return _settings.MaxRetries; }
            set { _settings.MaxRetries = value; }
        }

        /// <summary>Optional append-only diagnostic log file (secrets redacted). "" disables it.</summary>
        public string LogFilePath
        {
            get { return _settings.LogFilePath; }
            set { _settings.LogFilePath = value; }
        }

        /// <summary>Acquires a fresh token (bypassing the cache). "" on success, else the error text.</summary>
        public string TestConnection()
        {
            return _settings.TestConnection();
        }

        /// <summary>Request/correlation id of the last failed TestConnection, "" if none.</summary>
        public string LastRequestId
        {
            get { return _settings.LastRequestId; }
        }

        /// <summary>Redacted diagnostic log of the last TestConnection (and of token requests made for mailboxes).</summary>
        public string LastDiagnostics
        {
            get { return _settings.LastDiagnostics; }
        }
    }
}
