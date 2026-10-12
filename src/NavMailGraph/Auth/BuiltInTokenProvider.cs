using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Internal;

namespace NavMailGraph.Auth
{
    /// <summary>
    /// OAuth 2.0 client-credentials flow against the Microsoft identity platform v2.0 endpoint using only the in-box
    /// HttpClient: client secret (form post) or certificate (signed JWT client assertion).
    /// https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
    /// </summary>
    internal sealed class BuiltInTokenProvider : ITokenProvider
    {
        internal const string ClientSecretKind = "ClientSecret";

        internal const string CertificateKind = "Certificate";

        /// <summary>Used when the token response carries no usable expires_in.</summary>
        internal const int DefaultLifetimeSeconds = 3599;

        private static readonly Regex AadstsPrefix = new Regex(@"^\s*AADSTS(\d+)\s*:\s*", RegexOptions.CultureInvariant);

        private readonly TokenProviderSettings _settings;

        private readonly HttpClient _http;

        private readonly Diagnostics? _log;

        private readonly string _kind;

        private readonly string _tokenEndpoint;

        private readonly string _cacheKey;

        private readonly int _timeoutSeconds;

        private readonly int _maxRetries;

        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        internal BuiltInTokenProvider(TokenProviderSettings settings, HttpClient http, Diagnostics? log)
            : this(settings, http, log, GraphMailSender.DefaultTimeoutSeconds)
        {
        }

        internal BuiltInTokenProvider(TokenProviderSettings settings, HttpClient http, Diagnostics? log, int timeoutSeconds)
            : this(settings, http, log, timeoutSeconds, GraphMailSender.DefaultMaxRetries, null)
        {
        }

        /// <summary>
        /// Creates the provider. <paramref name="maxRetries"/> bounds the retries of one token request on HTTP 429/500/502/503/504
        /// and on transport errors/timeouts; <paramref name="delay"/> is a test hook replacing Task.Delay (null uses Task.Delay).
        /// </summary>
        internal BuiltInTokenProvider(TokenProviderSettings settings, HttpClient http, Diagnostics? log, int timeoutSeconds, int maxRetries, Func<TimeSpan, CancellationToken, Task>? delay)
        {
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : GraphMailSender.DefaultTimeoutSeconds;
            _maxRetries = maxRetries < 0 ? 0 : maxRetries;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log;

            if (string.IsNullOrWhiteSpace(settings.TenantId))
            {
                throw GraphMailException.Validation("Validation failed: Tenant ID is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.ClientId))
            {
                throw GraphMailException.Validation("Validation failed: Client ID is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.AuthorityHost) || string.IsNullOrWhiteSpace(settings.Scope))
            {
                throw GraphMailException.Validation("Validation failed: authority host and scope are required.");
            }

            if (settings.HasClientSecret == settings.HasCertificate)
            {
                throw GraphMailException.Validation(settings.HasClientSecret
                    ? "Validation failed: configure either a client secret or a certificate, not both."
                    : "Validation failed: no credential configured (call UseClientSecret, UseCertificateThumbprint, UseCertificateFile or UseTokenProvider).");
            }

            _kind = settings.HasCertificate ? CertificateKind : ClientSecretKind;
            _tokenEndpoint = settings.TokenEndpoint;
            _cacheKey = string.Join("|", new[]
            {
                settings.AuthorityHost.TrimEnd('/').ToLowerInvariant(),
                settings.TenantId.Trim().ToLowerInvariant(),
                settings.ClientId.Trim().ToLowerInvariant(),
                CredentialFingerprint(settings),
                settings.Scope.Trim(),
            });
        }

        /// <summary>Per-request timeout for the token endpoint in seconds.</summary>
        internal int TimeoutSeconds
        {
            get { return _timeoutSeconds; }
        }

        internal string CacheKey
        {
            get { return _cacheKey; }
        }

        internal string TokenEndpoint
        {
            get { return _tokenEndpoint; }
        }

        /// <inheritdoc/>
        public Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            return TokenCache.GetOrAcquireAsync(_cacheKey, forceRefresh, AcquireAsync, cancellationToken);
        }

        /// <inheritdoc/>
        public string Describe()
        {
            string text = "BuiltIn/" + _kind + " tenant=" + _settings.TenantId + " client=" + _settings.ClientId + " authority=" + _settings.AuthorityHost + " scope=" + _settings.Scope;
            if (_settings.Certificate != null)
            {
                text += " thumbprint=" + _settings.Certificate.Thumbprint + (_settings.SendX5C ? " x5c" : "");
            }

            return text;
        }

        /// <summary>SHA-256 of the secret, or the certificate thumbprint. Part of the cache key so a rotated credential never reuses an old token.</summary>
        internal static string CredentialFingerprint(TokenProviderSettings settings)
        {
            if (settings.Certificate != null)
            {
                return "cert:" + settings.Certificate.Thumbprint;
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(settings.ClientSecret));
                return "secret:" + BitConverter.ToString(hash).Replace("-", "");
            }
        }

        /// <summary>Token request statuses that are retried: throttling and server-side failures. 400/401 (invalid_client etc.) never are.</summary>
        internal static readonly int[] RetryableStatusCodes = { 429, 500, 502, 503, 504 };

        /// <summary>
        /// Requests a token, retrying up to MaxRetries times on <see cref="RetryableStatusCodes"/> and on transport errors or
        /// timeouts (a token request has no side effects). Retry-After is honoured (capped like Graph retries), otherwise
        /// 2 s * 2^attempt. MSAL retries 5xx once internally; apps are expected to add their own policy
        /// (https://learn.microsoft.com/en-us/entra/msal/dotnet/advanced/exceptions/msal-error-handling).
        /// </summary>
        private async Task<AccessToken> AcquireAsync(CancellationToken cancellationToken)
        {
            int retries = 0;
            while (true)
            {
                TimeSpan? retryAfter = null;
                try
                {
                    return await AcquireOnceAsync(cancellationToken, hint => retryAfter = hint).ConfigureAwait(false);
                }
                catch (GraphMailException ex) when (retries < _maxRetries && IsRetryableTokenError(ex))
                {
                    TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(GraphClient.BaseRetryDelay.TotalSeconds * Math.Pow(2, retries));
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }

                    TimeSpan max = TimeSpan.FromSeconds(GraphClient.MaxRetryDelaySeconds);
                    if (delay > max)
                    {
                        delay = max;
                    }

                    retries++;
                    Log("token request: retry " + retries + "/" + _maxRetries + " in " + delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                    await _delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>True for a transport error/timeout or a retryable HTTP status from the token endpoint.</summary>
        internal static bool IsRetryableTokenError(GraphMailException error)
        {
            if (string.Equals(error.ErrorCode, GraphMailException.TransportCode, StringComparison.Ordinal))
            {
                return true;
            }

            return Array.IndexOf(RetryableStatusCodes, error.StatusCode) >= 0;
        }

        /// <summary>One token request. The form (and, for certificates, the client assertion with a fresh jti/iat) is rebuilt per call.</summary>
        private async Task<AccessToken> AcquireOnceAsync(CancellationToken cancellationToken, Action<TimeSpan?> reportRetryAfter)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("client_id", _settings.ClientId.Trim()),
                new KeyValuePair<string, string>("scope", _settings.Scope.Trim()),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
            };

            if (_settings.Certificate != null)
            {
                // The assertion audience is the token endpoint URL; Entra accepts both the v2.0 and the v1.0 form.
                string assertion = ClientAssertionBuilder.Build(_settings.Certificate, _settings.ClientId.Trim(), _tokenEndpoint, _settings.SendX5C, DateTimeOffset.UtcNow);
                form.Add(new KeyValuePair<string, string>("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
                form.Add(new KeyValuePair<string, string>("client_assertion", assertion));
            }
            else
            {
                form.Add(new KeyValuePair<string, string>("client_secret", _settings.ClientSecret));
            }

            Log("POST " + _tokenEndpoint + " (" + _kind + ")");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Lifetime is counted from before the request so network latency can never make a token look fresher than it is.
            DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
            int status;
            string reason;
            string body;
            using (var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint))
            {
                request.Content = new FormUrlEncodedContent(form);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation(GraphClient.ClientRequestIdHeader, Guid.NewGuid().ToString("D"));
                request.Headers.TryAddWithoutValidation("User-Agent", GraphMailSender.UserAgent);
                request.Headers.ExpectContinue = false;

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
                    HttpResponseMessage response;
                    try
                    {
                        response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new GraphMailException("Token acquisition failed: no response from " + _tokenEndpoint + " within " + TimeoutSeconds + " s.", GraphMailException.TransportCode, 0, "", true, ex);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new GraphMailException("Token acquisition failed: " + GraphClient.DescribeTransportError(ex) + " (" + _tokenEndpoint + ")", GraphMailException.TransportCode, 0, "", true, ex);
                    }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            reportRetryAfter(GraphClient.ParseRetryAfter(response));
                        }

                        status = (int)response.StatusCode;
                        reason = response.ReasonPhrase ?? "";
                        body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }

            if (status >= 200 && status < 300)
            {
                TokenResponse? token = GraphJson.Deserialize<TokenResponse>(body);
                if (token == null || string.IsNullOrEmpty(token.AccessToken))
                {
                    throw new GraphMailException("Token acquisition failed (HTTP " + status + "): the response contained no access_token.", "Token", status, "", false);
                }

                int lifetime;
                if (!int.TryParse(token.ExpiresIn ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out lifetime) || lifetime <= 0)
                {
                    lifetime = DefaultLifetimeSeconds;
                }

                Log("token acquired in " + stopwatch.ElapsedMilliseconds + " ms, expires in " + lifetime + " s");
                return new AccessToken(token.AccessToken!, requestedAt.AddSeconds(lifetime));
            }

            GraphMailException error = BuildError(status, reason, body);
            Log("token request failed in " + stopwatch.ElapsedMilliseconds + " ms: " + error.Message);
            throw error;
        }

        /// <summary>Formats "Token acquisition failed (&lt;error&gt;, AADSTS&lt;code&gt;): &lt;first line&gt; [correlation-id ...]".</summary>
        internal static GraphMailException BuildError(int status, string reasonPhrase, string body)
        {
            TokenErrorResponse? parsed = GraphJson.Deserialize<TokenErrorResponse>(body);
            string error = parsed?.Error ?? "";
            string description = GraphClient.FirstLine(parsed?.ErrorDescription ?? "");
            string correlationId = parsed?.CorrelationId ?? "";
            string aadsts = "";
            if (parsed?.ErrorCodes != null && parsed.ErrorCodes.Length > 0)
            {
                aadsts = "AADSTS" + parsed.ErrorCodes[0].ToString(CultureInfo.InvariantCulture);
            }

            Match prefix = AadstsPrefix.Match(description);
            if (prefix.Success)
            {
                if (aadsts.Length == 0)
                {
                    aadsts = "AADSTS" + prefix.Groups[1].Value;
                }

                description = description.Substring(prefix.Length);
            }

            if (description.Length == 0)
            {
                description = GraphClient.FirstLine(body);
                if (description.Length > 200)
                {
                    description = description.Substring(0, 200) + "...";
                }
            }

            if (description.Length == 0)
            {
                description = reasonPhrase.Length > 0 ? reasonPhrase : "no details in response";
            }

            var head = new StringBuilder("Token acquisition failed (");
            if (error.Length > 0 || aadsts.Length > 0)
            {
                head.Append(error);
                if (error.Length > 0 && aadsts.Length > 0)
                {
                    head.Append(", ");
                }

                head.Append(aadsts);
            }
            else
            {
                head.Append("HTTP ").Append(status);
            }

            head.Append("): ").Append(description);
            if (correlationId.Length > 0)
            {
                head.Append(" [correlation-id ").Append(correlationId).Append(']');
            }

            string code = aadsts.Length > 0 ? aadsts : (error.Length > 0 ? error : "Token");
            return new GraphMailException(head.ToString(), code, status, correlationId, status == 429 || status >= 500);
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log.Log(message);
            }
        }
    }
}
