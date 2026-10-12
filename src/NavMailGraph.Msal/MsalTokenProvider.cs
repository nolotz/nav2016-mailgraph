using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace NavMailGraph.Msal
{
    /// <summary>
    /// <see cref="ITokenProvider"/> on Microsoft.Identity.Client (MSAL.NET), client-credentials flow.
    /// Created by <c>NavMailGraph.Internal.MsalBridge</c> through reflection when <c>GraphMailSender.TokenLibrary</c> is "Msal";
    /// the bridge looks for the public constructor <see cref="MsalTokenProvider(TokenProviderSettings)"/>.
    /// </summary>
    /// <remarks>
    /// One <see cref="IConfidentialClientApplication"/> is kept per configuration (authority host, tenant, client id,
    /// credential fingerprint, x5c flag) in a static dictionary, because MSAL's in-memory application token cache lives on
    /// the application object ("keep it as a singleton", see docs/research-notes.md, MSAL.NET section). The dictionary key
    /// never contains the secret itself, only its SHA-256 hash or the certificate thumbprint. Instances are cheap and
    /// thread-safe; there is nothing to dispose.
    /// </remarks>
    public sealed class MsalTokenProvider : ITokenProvider
    {
        private const string ClientSecretKind = "ClientSecret";

        private const string CertificateKind = "Certificate";

        // MsalError.RequestTimeout / MsalError.ServiceNotAvailable (docs/research-notes.md, MsalError.cs).
        private const string RequestTimeoutCode = "request_timeout";

        private const string ServiceNotAvailableCode = "service_not_available";

        private static readonly ConcurrentDictionary<string, IConfidentialClientApplication> Applications =
            new ConcurrentDictionary<string, IConfidentialClientApplication>(StringComparer.Ordinal);

        private static readonly Regex AadstsPattern = new Regex(@"AADSTS(\d+)", RegexOptions.CultureInvariant);

        private static readonly Regex AadstsPrefix = new Regex(@"^\s*AADSTS(\d+)\s*:\s*", RegexOptions.CultureInvariant);

        private readonly TokenProviderSettings _settings;

        private readonly IConfidentialClientApplication _application;

        private readonly string[] _scopes;

        private readonly string _kind;

        /// <summary>
        /// Creates the provider and (once per configuration) the underlying MSAL confidential client application.
        /// </summary>
        /// <param name="settings">Tenant, client id, authority host, scope and exactly one credential (secret or certificate).</param>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        /// <exception cref="GraphMailException">The settings are incomplete or MSAL rejects the configuration (code "Validation").</exception>
        public MsalTokenProvider(TokenProviderSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            if (string.IsNullOrWhiteSpace(settings.TenantId))
            {
                throw Validation("Validation failed: Tenant ID is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.ClientId))
            {
                throw Validation("Validation failed: Client ID is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.AuthorityHost) || string.IsNullOrWhiteSpace(settings.Scope))
            {
                throw Validation("Validation failed: authority host and scope are required.");
            }

            if (settings.HasClientSecret == settings.HasCertificate)
            {
                throw Validation(settings.HasClientSecret
                    ? "Validation failed: configure either a client secret or a certificate, not both."
                    : "Validation failed: no credential configured (call UseClientSecret, UseCertificateThumbprint, UseCertificateFile or UseTokenProvider).");
            }

            _kind = settings.HasCertificate ? CertificateKind : ClientSecretKind;
            _scopes = new[] { settings.Scope.Trim() };

            string key = CacheKey(settings);
            try
            {
                // If two threads race, both may build an application; only one is stored and used. Building is cheap
                // and has no side effects (no network call), so this is harmless.
                _application = Applications.GetOrAdd(key, _ => BuildApplication(settings));
            }
            catch (MsalException ex)
            {
                throw new GraphMailException(
                    "Token library 'Msal': the MSAL client application could not be created (" + ex.ErrorCode + "): " + FirstLine(ex.Message),
                    GraphMailException.ValidationCode, 0, "", false, ex);
            }
            catch (ArgumentException ex)
            {
                throw new GraphMailException(
                    "Token library 'Msal': invalid configuration for the MSAL client application: " + FirstLine(ex.Message),
                    GraphMailException.ValidationCode, 0, "", false, ex);
            }
        }

        /// <summary>Version of the loaded Microsoft.Identity.Client assembly (for diagnostics).</summary>
        public static string MsalVersion
        {
            get
            {
                Version? version = typeof(ConfidentialClientApplicationBuilder).Assembly.GetName().Version;
                return version == null ? "unknown" : version.ToString();
            }
        }

        /// <summary>
        /// Returns an access token from MSAL's application token cache, or acquires one from Entra ID when none is cached,
        /// the cached one is expiring or <paramref name="forceRefresh"/> is set.
        /// </summary>
        /// <param name="forceRefresh">Ignore cached tokens (MSAL <c>WithForceRefresh(true)</c>); used once after an HTTP 401.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The access token and its expiry as reported by MSAL.</returns>
        /// <exception cref="GraphMailException">MSAL or Entra ID reported an error.</exception>
        public async Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            AuthenticationResult result;
            try
            {
                result = await _application
                    .AcquireTokenForClient(_scopes)
                    .WithForceRefresh(forceRefresh)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MsalServiceException ex)
            {
                throw MapServiceException(ex);
            }
            catch (MsalClientException ex)
            {
                throw MapClientException(ex);
            }
            catch (MsalException ex)
            {
                throw MapOtherException(ex);
            }

            if (result == null || string.IsNullOrEmpty(result.AccessToken))
            {
                throw new GraphMailException("Token acquisition failed via MSAL: no access token was returned.", "Token", 0, "", false);
            }

            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }

        /// <summary>
        /// One-line description for diagnostics, e.g. "Msal/ClientSecret tenant=... client=... authority=... scope=... msal=4.90.1.0".
        /// Never contains the secret.
        /// </summary>
        public string Describe()
        {
            string text = "Msal/" + _kind + " tenant=" + _settings.TenantId + " client=" + _settings.ClientId
                + " authority=" + _settings.AuthorityHost + " scope=" + _settings.Scope;
            if (_settings.Certificate != null)
            {
                text += " thumbprint=" + _settings.Certificate.Thumbprint + (_settings.SendX5C ? " x5c" : "");
            }

            return text + " msal=" + MsalVersion;
        }

        /// <summary>Removes all cached MSAL applications (and therefore their token caches). For tests.</summary>
        internal static void ResetForTests()
        {
            Applications.Clear();
        }

        /// <summary>Number of cached MSAL applications. For tests.</summary>
        internal static int CachedApplicationCount
        {
            get { return Applications.Count; }
        }

        /// <summary>Authority URI "{AuthorityHost}/{TenantId}" passed to MSAL.</summary>
        internal static string Authority(TokenProviderSettings settings)
        {
            return settings.AuthorityHost.Trim().TrimEnd('/') + "/" + Uri.EscapeDataString(settings.TenantId.Trim());
        }

        /// <summary>Static dictionary key. Contains the SHA-256 of the secret or the certificate thumbprint, never the secret.</summary>
        internal static string CacheKey(TokenProviderSettings settings)
        {
            return string.Join("|", new[]
            {
                settings.AuthorityHost.Trim().TrimEnd('/').ToLowerInvariant(),
                settings.TenantId.Trim().ToLowerInvariant(),
                settings.ClientId.Trim().ToLowerInvariant(),
                CredentialFingerprint(settings),
                settings.SendX5C ? "x5c" : "nox5c",
            });
        }

        /// <summary>SHA-256 of the secret, or the certificate thumbprint.</summary>
        internal static string CredentialFingerprint(TokenProviderSettings settings)
        {
            if (settings.Certificate != null)
            {
                return "cert:" + settings.Certificate.Thumbprint;
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(settings.ClientSecret ?? ""));
                return "secret:" + BitConverter.ToString(hash).Replace("-", "");
            }
        }

        /// <summary>Maps an Entra ID / service error to "Token acquisition failed via MSAL (&lt;error&gt;, AADSTS&lt;code&gt;): &lt;first line&gt; [correlation-id ...]".</summary>
        internal static GraphMailException MapServiceException(MsalServiceException ex)
        {
            int status = ex.StatusCode;
            string error = ex.ErrorCode ?? "";
            string correlationId = ex.CorrelationId ?? "";
            string description = FirstLine(ex.Message ?? "");

            string aadsts = "";
            Match match = AadstsPattern.Match(ex.Message ?? "");
            if (!match.Success)
            {
                match = AadstsPattern.Match(ex.ResponseBody ?? "");
            }

            if (match.Success)
            {
                aadsts = "AADSTS" + match.Groups[1].Value;
            }

            Match prefix = AadstsPrefix.Match(description);
            if (prefix.Success)
            {
                description = description.Substring(prefix.Length);
            }

            // Entra appends " Trace ID: ... Correlation ID: ... Timestamp: ..." to error_description (MSAL keeps it on the
            // same line); the correlation id is reported separately below.
            int trace = description.IndexOf(" Trace ID:", StringComparison.Ordinal);
            if (trace > 0)
            {
                description = description.Substring(0, trace).TrimEnd();
            }

            if (description.Length == 0)
            {
                description = status > 0 ? "HTTP " + status.ToString(CultureInfo.InvariantCulture) : "no details";
            }

            var head = new StringBuilder("Token acquisition failed via MSAL (");
            head.Append(error);
            if (error.Length > 0 && aadsts.Length > 0)
            {
                head.Append(", ");
            }

            head.Append(aadsts);
            if (error.Length == 0 && aadsts.Length == 0)
            {
                head.Append("HTTP ").Append(status.ToString(CultureInfo.InvariantCulture));
            }

            head.Append("): ").Append(description);
            if (correlationId.Length > 0)
            {
                head.Append(" [correlation-id ").Append(correlationId).Append(']');
            }

            string code = aadsts.Length > 0 ? aadsts : (error.Length > 0 ? error : "Token");
            bool transient = status == 429 || status >= 500
                || string.Equals(error, RequestTimeoutCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(error, ServiceNotAvailableCode, StringComparison.OrdinalIgnoreCase);
            return new GraphMailException(head.ToString(), code, status, correlationId, transient, ex);
        }

        /// <summary>Maps a local MSAL error (configuration, certificate, ...) to a non-transient <see cref="GraphMailException"/>.</summary>
        internal static GraphMailException MapClientException(MsalClientException ex)
        {
            string error = string.IsNullOrEmpty(ex.ErrorCode) ? "MsalClient" : ex.ErrorCode;
            string text = "Token acquisition failed via MSAL (client error " + error + "): " + FirstLine(ex.Message ?? "");
            string correlationId = ex.CorrelationId ?? "";
            if (correlationId.Length > 0)
            {
                text += " [correlation-id " + correlationId + "]";
            }

            return new GraphMailException(text, error, 0, correlationId, false, ex);
        }

        /// <summary>Maps any other <see cref="MsalException"/>.</summary>
        internal static GraphMailException MapOtherException(MsalException ex)
        {
            string error = string.IsNullOrEmpty(ex.ErrorCode) ? "Msal" : ex.ErrorCode;
            string text = "Token acquisition failed via MSAL (" + error + "): " + FirstLine(ex.Message ?? "");
            string correlationId = ex.CorrelationId ?? "";
            if (correlationId.Length > 0)
            {
                text += " [correlation-id " + correlationId + "]";
            }

            return new GraphMailException(text, error, 0, correlationId, false, ex);
        }

        /// <summary>First non-empty line of <paramref name="text"/>, trimmed.</summary>
        internal static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return "";
        }

        private static IConfidentialClientApplication BuildApplication(TokenProviderSettings settings)
        {
            ConfidentialClientApplicationBuilder builder = ConfidentialClientApplicationBuilder
                .Create(settings.ClientId.Trim())
                .WithAuthority(Authority(settings))
                .WithLegacyCacheCompatibility(false);

            builder = settings.Certificate != null
                ? builder.WithCertificate(settings.Certificate, settings.SendX5C)
                : builder.WithClientSecret(settings.ClientSecret);

            return builder.Build();
        }

        private static GraphMailException Validation(string text)
        {
            return new GraphMailException(text, GraphMailException.ValidationCode, 0, "", false);
        }
    }
}
