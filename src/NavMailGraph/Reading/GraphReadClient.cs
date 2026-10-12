using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Internal;

namespace NavMailGraph.Reading
{
    /// <summary>
    /// HTTP layer of <see cref="GraphMailbox"/>: standard headers (shared with <see cref="GraphClient"/>), the
    /// Prefer: IdType="ImmutableId" header on every call, token handling (401 → one forced refresh), retries with
    /// Retry-After (429/503/509 for every call, 504 additionally for GETs), at most
    /// <see cref="MaxConcurrentRequestsPerMailbox"/> concurrent requests per mailbox (process-wide), and streamed
    /// downloads (<see cref="HttpCompletionOption.ResponseHeadersRead"/>, idle timeout, sequential writes only).
    /// Error texts come from <see cref="GraphClient.BuildGraphError"/> so they match the sending side.
    /// </summary>
    internal sealed class GraphReadClient
    {
        /// <summary>Outlook allows four concurrent requests per app and mailbox (docs/research-notes-v2.md, throttling limits).</summary>
        internal const int MaxConcurrentRequestsPerMailbox = 4;

        /// <summary>Copy buffer for streamed downloads (below the large-object-heap threshold).</summary>
        internal const int CopyBufferSize = 81920;

        /// <summary>Sent on every Graph call: ids stay stable across folder moves (docs/research-notes-v2.md).</summary>
        internal const string ImmutableIdPreference = "IdType=\"ImmutableId\"";

        /// <summary>Asks Graph to return body as text (GraphMailbox.BodyAsText).</summary>
        internal const string TextBodyPreference = "outlook.body-content-type=\"text\"";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly HttpClient _http;

        private readonly ITokenProvider _tokenProvider;

        private readonly Diagnostics _log;

        private readonly string _baseUrl;

        private readonly string _mailbox;

        private readonly int _timeoutSeconds;

        private readonly int _maxRetries;

        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        private readonly GraphClient _errorFormatter;

        private readonly SemaphoreSlim _gate;

        internal GraphReadClient(HttpClient http, ITokenProvider tokenProvider, Diagnostics log, string graphBaseUrl, string mailbox, int timeoutSeconds, int maxRetries, Func<TimeSpan, CancellationToken, Task>? delay)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _baseUrl = (graphBaseUrl ?? "").TrimEnd('/');
            _mailbox = mailbox ?? "";
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : GraphMailSender.DefaultTimeoutSeconds;
            _maxRetries = maxRetries < 0 ? 0 : maxRetries;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _errorFormatter = new GraphClient(_http, _tokenProvider, _log, _baseUrl, _mailbox, _timeoutSeconds, _maxRetries, _delay);
            _gate = GateFor(GateKey(_baseUrl, _mailbox));
        }

        /// <summary>Graph request-id of the most recent response, "" if none.</summary>
        internal string LastRequestId { get; private set; } = "";

        /// <summary>{base}/users/{mailbox}.</summary>
        internal string MailboxUrl
        {
            get { return _baseUrl + "/users" + GraphClient.UserSegment(_mailbox); }
        }

        internal string MessageUrl(string messageId)
        {
            return MailboxUrl + "/messages/" + GraphClient.EscapePathSegment(messageId);
        }

        /// <summary>Process-wide gate key: Graph host + mailbox (case-insensitive).</summary>
        internal static string GateKey(string graphBaseUrl, string mailbox)
        {
            Uri? uri;
            string host = Uri.TryCreate(graphBaseUrl ?? "", UriKind.Absolute, out uri) && uri != null ? uri.Host : (graphBaseUrl ?? "");
            return host.ToLowerInvariant() + "|" + (mailbox ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>The process-wide semaphore limiting concurrent requests for one mailbox.</summary>
        internal static SemaphoreSlim GateFor(string key)
        {
            return Gates.GetOrAdd(key, _ => new SemaphoreSlim(MaxConcurrentRequestsPerMailbox, MaxConcurrentRequestsPerMailbox));
        }

        /// <summary>
        /// Rejects absolute URLs (nextLink / deltaLink) that do not point to the configured Graph host, so the bearer token
        /// is never sent elsewhere. Returns the URL unchanged.
        /// </summary>
        internal string EnsureGraphUrl(string url, string what)
        {
            Uri? target;
            Uri? graph;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out target) || target == null
                || !Uri.TryCreate(_baseUrl, UriKind.Absolute, out graph) || graph == null
                || !string.Equals(target.Scheme, graph.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(target.Authority, graph.Authority, StringComparison.OrdinalIgnoreCase))
            {
                throw GraphMailException.Validation("Validation failed: the " + what + " is not a Microsoft Graph URL of " + _baseUrl + " (got '" + Diagnostics.SafeUrl(url ?? "") + "').");
            }

            return url!;
        }

        /// <summary>GET returning a JSON body (idempotent: retried on 429/503/509/504).</summary>
        internal Task<GraphResponse> GetJsonAsync(string operation, string url, IList<string>? extraPreferences, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                operation,
                () => NewRequest(HttpMethod.Get, url, null, extraPreferences),
                true,
                "application/json",
                ReadBodyAsync,
                cancellationToken);
        }

        /// <summary>POST/PATCH/DELETE with an optional JSON body (not idempotent: retried only on 429/503/509).</summary>
        internal Task<GraphResponse> SendJsonAsync(string operation, HttpMethod method, string url, byte[]? payload, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                operation,
                () => NewRequest(method, url, payload, null),
                false,
                "application/json",
                ReadBodyAsync,
                cancellationToken);
        }

        /// <summary>
        /// Streamed GET (e.g. .../$value): <paramref name="consumer"/> receives the response stream once the headers of a 2xx
        /// response arrived. The request timeout becomes an idle timeout: the consumer calls the reset action after every chunk.
        /// Retries happen only before the body is read. Returns what the consumer returns.
        /// </summary>
        internal Task<long> DownloadAsync(string operation, string url, Func<Stream, HttpResponseMessage, Action, CancellationToken, Task<long>> consumer, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                operation,
                () => NewRequest(HttpMethod.Get, url, null, null),
                true,
                "*/*",
                async (response, reset, token) =>
                {
                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        return await consumer(source, response, reset, token).ConfigureAwait(false);
                    }
                },
                cancellationToken);
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="target"/> sequentially in <see cref="CopyBufferSize"/> chunks
        /// (Write only: no Seek, Length or Position on the target, which may be a NAV OutStream wrapper), resetting the idle
        /// timeout after every chunk. Write failures become an "IO" error; read failures surface to the caller.
        /// </summary>
        internal static async Task<long> CopyAsync(Stream source, Stream target, Action resetTimeout, string what, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[CopyBufferSize];
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                try
                {
                    target.Write(buffer, 0, read);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    throw new GraphMailException(
                        "Writing " + what + " to the target failed after " + total + " bytes: " + ex.GetType().Name + ": " + GraphClient.FirstLine(ex.Message),
                        IoErrorCode, 0, "", false, ex);
                }

                total += read;
                resetTimeout();
            }

            try
            {
                target.Flush();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new GraphMailException(
                    "Writing " + what + " to the target failed after " + total + " bytes: " + ex.GetType().Name + ": " + GraphClient.FirstLine(ex.Message),
                    IoErrorCode, 0, "", false, ex);
            }

            return total;
        }

        /// <summary>Error code of local file/stream write failures.</summary>
        internal const string IoErrorCode = "IO";

        private static async Task<GraphResponse> ReadBodyAsync(HttpResponseMessage response, Action reset, CancellationToken token)
        {
            string body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new GraphResponse((int)response.StatusCode, body, GraphClient.HeaderValue(response.Headers, GraphClient.RequestIdHeader));
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string url, byte[]? payload, IList<string>? extraPreferences)
        {
            var request = new HttpRequestMessage(method, url);
            if (payload != null)
            {
                var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                request.Content = content;
            }

            request.Headers.TryAddWithoutValidation("Prefer", ImmutableIdPreference);
            if (extraPreferences != null)
            {
                foreach (string preference in extraPreferences)
                {
                    request.Headers.TryAddWithoutValidation("Prefer", preference);
                }
            }

            return request;
        }

        /// <summary>
        /// One logical request with retries. Every attempt holds one slot of the mailbox gate while it is on the wire
        /// (including the body read); retry pauses happen outside the gate.
        /// </summary>
        private async Task<T> ExecuteAsync<T>(string operation, Func<HttpRequestMessage> requestFactory, bool idempotent, string accept,
            Func<HttpResponseMessage, Action, CancellationToken, Task<T>> onSuccess, CancellationToken cancellationToken)
        {
            GraphClient.RequestKind kind = idempotent ? GraphClient.RequestKind.Idempotent : GraphClient.RequestKind.NonIdempotent;
            int retries = 0;
            bool tokenRefreshed = false;
            bool forceRefresh = false;
            while (true)
            {
                TimeSpan? retryDelay = null;
                AccessToken token = await GraphClient.AcquireTokenAsync(_tokenProvider, forceRefresh, cancellationToken).ConfigureAwait(false);
                forceRefresh = false;

                if (!await _gate.WaitAsync(TimeSpan.FromSeconds(_timeoutSeconds), cancellationToken).ConfigureAwait(false))
                {
                    throw new GraphMailException(
                        "Microsoft Graph " + operation + " failed: no free request slot for mailbox " + _mailbox + " within " + _timeoutSeconds + " s (at most "
                        + MaxConcurrentRequestsPerMailbox + " concurrent requests per mailbox). [mailbox " + _mailbox + "]",
                        GraphMailException.TransportCode, 0, "", true);
                }

                try
                {
                    using (HttpRequestMessage request = requestFactory())
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                        request.Headers.TryAddWithoutValidation(GraphClient.ClientRequestIdHeader, Guid.NewGuid().ToString("D"));
                        request.Headers.TryAddWithoutValidation("User-Agent", GraphMailSender.UserAgent);
                        request.Headers.ExpectContinue = false;
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                        long sent = request.Content == null ? 0 : (request.Content.Headers.ContentLength ?? 0);
                        string url = request.RequestUri == null ? "" : Diagnostics.SafeUrl(request.RequestUri.ToString());
                        _log.Log(request.Method.Method + " " + url + " (" + sent + " bytes) " + GraphClient.ClientRequestIdHeader + "="
                            + GraphClient.HeaderValue(request.Headers, GraphClient.ClientRequestIdHeader));

                        TimeSpan limit = TimeSpan.FromSeconds(_timeoutSeconds);
                        timeout.CancelAfter(limit);
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        HttpResponseMessage sentResponse;
                        try
                        {
                            sentResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw Timeout(operation, ex);
                        }
                        catch (HttpRequestException ex)
                        {
                            throw Transport(operation, ex);
                        }

                        using (HttpResponseMessage response = sentResponse)
                        using (timeout.Token.Register(() => response.Dispose()))
                        {
                            int status = (int)response.StatusCode;
                            string requestId = GraphClient.HeaderValue(response.Headers, GraphClient.RequestIdHeader);
                            if (requestId.Length > 0)
                            {
                                LastRequestId = requestId;
                            }

                            if (response.IsSuccessStatusCode)
                            {
                                _log.Log("HTTP " + status + " " + (response.ReasonPhrase ?? "") + " (headers in " + stopwatch.ElapsedMilliseconds + " ms)"
                                    + (requestId.Length > 0 ? ", " + GraphClient.RequestIdHeader + "=" + requestId : ""));
                                try
                                {
                                    return await onSuccess(response, () => timeout.CancelAfter(limit), timeout.Token).ConfigureAwait(false);
                                }
                                catch (GraphMailException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                                {
                                    throw Timeout(operation, ex);
                                }
                                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException)
                                {
                                    throw Transport(operation, ex);
                                }
                            }

                            string body;
                            try
                            {
                                body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                            {
                                body = "";
                                _log.Log("error body could not be read: " + ex.GetType().Name);
                            }

                            _log.Log("HTTP " + status + " " + (response.ReasonPhrase ?? "") + " in " + stopwatch.ElapsedMilliseconds + " ms, " + body.Length + " chars"
                                + (requestId.Length > 0 ? ", " + GraphClient.RequestIdHeader + "=" + requestId : ""));

                            if (status == 401 && !tokenRefreshed)
                            {
                                tokenRefreshed = true;
                                forceRefresh = true;
                                _log.Log("HTTP 401: refreshing the token once and retrying");
                                continue;
                            }

                            if (GraphClient.IsRetryable(status, kind) && retries < _maxRetries)
                            {
                                retryDelay = GraphClient.RetryDelay(response, retries);
                                retries++;
                                _log.Log("HTTP " + status + ": retry " + retries + "/" + _maxRetries + " in "
                                    + retryDelay.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                            }
                            else
                            {
                                GraphMailException error = _errorFormatter.BuildGraphError(operation, status, response, body, requestId);
                                if (error.RequestId.Length > 0)
                                {
                                    LastRequestId = error.RequestId;
                                }

                                _log.Log("ERROR " + error.Message + (body.Length > 0 ? " | body: " + GraphClient.Truncate(body, 500) : ""));
                                throw error;
                            }
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (retryDelay.HasValue)
                {
                    await _delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private GraphMailException Timeout(string operation, Exception ex)
        {
            _log.Log("timeout: no data within " + _timeoutSeconds + " s");
            return new GraphMailException(
                "Microsoft Graph " + operation + " failed: no response within " + _timeoutSeconds + " s (timeout). [mailbox " + _mailbox + "]",
                GraphMailException.TransportCode, 0, "", true, ex) { IsTimeout = true };
        }

        private GraphMailException Transport(string operation, Exception ex)
        {
            string detail = GraphClient.DescribeTransportError(ex);
            _log.Log("transport error: " + detail);
            return new GraphMailException(
                "Microsoft Graph " + operation + " failed: " + detail + " [mailbox " + _mailbox + "]",
                GraphMailException.TransportCode, 0, "", true, ex);
        }
    }
}
