using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Internal;

namespace NavMailGraph.Graph
{
    /// <summary>A successful (2xx) Graph response.</summary>
    internal sealed class GraphResponse
    {
        internal GraphResponse(int statusCode, string body, string requestId)
        {
            StatusCode = statusCode;
            Body = body;
            RequestId = requestId;
        }

        internal int StatusCode { get; }

        internal string Body { get; }

        internal string RequestId { get; }
    }

    /// <summary>
    /// Thin Microsoft Graph mail client for one mailbox: sendMail, draft + attachments + send, upload sessions.
    /// Adds the standard headers, acquires tokens from the <see cref="ITokenProvider"/>, retries throttling/outages
    /// and turns error responses into <see cref="GraphMailException"/>s with the documented text format.
    /// </summary>
    internal sealed class GraphClient
    {
        internal const string ClientRequestIdHeader = "client-request-id";

        internal const string RequestIdHeader = "request-id";

        /// <summary>
        /// Graph documents that upload-session PUTs must NOT carry an Authorization header (the uploadUrl is
        /// pre-authenticated, on outlook.office.com) and the Graph token must never be sent to another host.
        /// https://learn.microsoft.com/en-us/graph/outlook-large-attachments
        /// </summary>
        internal const bool SendAuthorizationOnUploadChunks = false;

        /// <summary>
        /// Statuses retried with backoff for every request: throttling and "service busy" rejections (429, 503, 509), which
        /// mean the request was not processed. https://learn.microsoft.com/en-us/graph/throttling ,
        /// https://learn.microsoft.com/en-us/graph/best-practices-concept
        /// </summary>
        internal static readonly int[] RetryableStatusCodes = { 429, 503, 509 };

        /// <summary>
        /// Statuses retried only for idempotent requests (upload-range PUTs, which are checked against nextExpectedRanges).
        /// A 504 comes from a gateway that stopped waiting; the backend may already have processed the request, so
        /// non-idempotent POSTs (sendMail, create draft, add attachment, createUploadSession, send) are never retried on it.
        /// </summary>
        internal static readonly int[] IdempotentOnlyRetryableStatusCodes = { 504 };

        /// <summary>Appended when a mail-delivering call (sendMail, send draft) fails in a way that leaves its outcome open (HTTP 504, timeout).</summary>
        internal const string OutcomeUnknownText =
            " The outcome is unknown: the message may already have been sent. Check the Sent Items folder of mailbox {0} before sending it again.";

        /// <summary>Retry/outcome semantics of one Graph call.</summary>
        internal enum RequestKind
        {
            /// <summary>Safe to repeat (upload-range PUT): retried on 429/503/509 and 504.</summary>
            Idempotent,

            /// <summary>Creates something (draft, attachment, upload session): retried only on 429/503/509.</summary>
            NonIdempotent,

            /// <summary>Delivers mail (sendMail, send draft): retried only on 429/503/509; 504 and timeouts report an unknown outcome.</summary>
            Delivering,
        }

        /// <summary>Upper bound for one retry pause, even when Retry-After asks for more (NAV sessions must not hang for minutes).</summary>
        internal const int MaxRetryDelaySeconds = 120;

        /// <summary>Backoff without Retry-After: 2 s * 2^attempt.</summary>
        internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);

        private static readonly Regex NextExpectedRangesPattern = new Regex(
            @"""nextExpectedRanges""\s*:\s*\[\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpClient _http;

        private readonly ITokenProvider _tokenProvider;

        private readonly Diagnostics _log;

        private readonly string _baseUrl;

        private readonly string _mailbox;

        private readonly int _timeoutSeconds;

        private readonly int _maxRetries;

        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        internal GraphClient(HttpClient http, ITokenProvider tokenProvider, Diagnostics log, string graphBaseUrl, string mailbox, int timeoutSeconds, int maxRetries)
            : this(http, tokenProvider, log, graphBaseUrl, mailbox, timeoutSeconds, maxRetries, null)
        {
        }

        /// <summary>Creates a client; <c>delay</c> is a test hook replacing Task.Delay for retry pauses (null uses Task.Delay).</summary>
        internal GraphClient(HttpClient http, ITokenProvider tokenProvider, Diagnostics log, string graphBaseUrl, string mailbox, int timeoutSeconds, int maxRetries, Func<TimeSpan, CancellationToken, Task>? delay)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _baseUrl = (graphBaseUrl ?? "").TrimEnd('/');
            _mailbox = mailbox ?? "";
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : GraphMailSender.DefaultTimeoutSeconds;
            _maxRetries = maxRetries < 0 ? 0 : maxRetries;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        /// <summary>Graph request-id of the most recent response (success or failure), "" if none.</summary>
        internal string LastRequestId { get; private set; } = "";

        /// <summary>{base}/users/{mailbox}; see <see cref="UserSegment"/> for the encoding.</summary>
        internal string MailboxUrl
        {
            get { return _baseUrl + "/users" + UserSegment(_mailbox); }
        }

        internal string MessageUrl(string messageId)
        {
            return MailboxUrl + "/messages/" + EscapePathSegment(messageId);
        }

        /// <summary>
        /// Path part after "/users" for a mailbox (UPN, SMTP address or object id). Percent-encodes the value but keeps '@'
        /// literal (all Graph examples do; whether %40 is accepted is not documented) and encodes '#' as %23. A value that
        /// starts with '$' uses the documented key form /users('$x@y.com') because /users/$x@y.com fails with 400.
        /// https://learn.microsoft.com/en-us/graph/api/user-get
        /// </summary>
        internal static string UserSegment(string mailbox)
        {
            string value = mailbox ?? "";
            if (value.StartsWith("$", StringComparison.Ordinal))
            {
                // OData string key: quotes doubled, '$' and quotes kept literal as in the documented example.
                return "('" + EscapePathSegment(value.Replace("'", "''")).Replace("%24", "$").Replace("%27", "'") + "')";
            }

            return "/" + EscapePathSegment(value);
        }

        /// <summary>RFC 3986 percent-encoding of one path segment, leaving '@' and '=' literal (both are valid pchar).</summary>
        internal static string EscapePathSegment(string value)
        {
            return Uri.EscapeDataString(value ?? "").Replace("%40", "@").Replace("%3D", "=");
        }

        /// <summary>Acquires a token, converting foreign exceptions (e.g. from MSAL) into <see cref="GraphMailException"/>.</summary>
        internal static async Task<AccessToken> AcquireTokenAsync(ITokenProvider provider, bool forceRefresh, CancellationToken cancellationToken)
        {
            try
            {
                return await provider.GetAccessTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphMailException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GraphMailException(
                    "Token acquisition failed (" + ex.GetType().Name + "): " + FirstLine(ex.Message) + " [provider " + provider.Describe() + "]",
                    "Token", 0, "", false, ex);
            }
        }

        /// <summary>POST /users/{mailbox}/sendMail. Graph answers 202 Accepted.</summary>
        internal async Task SendMailAsync(SendMailRequest request, CancellationToken cancellationToken)
        {
            byte[] payload = GraphJson.SerializeToUtf8Bytes(request);
            await ExecuteAsync("sendMail", () => JsonRequest(HttpMethod.Post, MailboxUrl + "/sendMail", payload), RequestKind.Delivering, true, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>POST /users/{mailbox}/messages (201 Created). Returns the new message id.</summary>
        internal async Task<string> CreateDraftAsync(GraphMessage message, CancellationToken cancellationToken)
        {
            byte[] payload = GraphJson.SerializeToUtf8Bytes(message);
            GraphResponse response = await ExecuteAsync("create draft", () => JsonRequest(HttpMethod.Post, MailboxUrl + "/messages", payload), RequestKind.NonIdempotent, true, true, cancellationToken).ConfigureAwait(false);
            GraphMessageResponse? created = GraphJson.Deserialize<GraphMessageResponse>(response.Body);
            if (created == null || string.IsNullOrEmpty(created.Id))
            {
                throw new GraphMailException(
                    "Microsoft Graph create draft failed (HTTP " + response.StatusCode + "): the response contained no message id." + Suffix(response.RequestId),
                    "NoMessageId", response.StatusCode, response.RequestId, false);
            }

            return created.Id!;
        }

        /// <summary>POST /users/{mailbox}/messages/{id}/attachments with a base64 fileAttachment (201 Created). For content below 3 MiB.</summary>
        internal Task AddAttachmentAsync(string messageId, GraphFileAttachment attachment, CancellationToken cancellationToken)
        {
            byte[] payload = GraphJson.SerializeToUtf8Bytes(attachment);
            string name = attachment.Name ?? "";
            return ExecuteAsync("add attachment '" + name + "'", () => JsonRequest(HttpMethod.Post, MessageUrl(messageId) + "/attachments", payload), RequestKind.NonIdempotent, true, true, cancellationToken);
        }

        /// <summary>POST .../attachments/createUploadSession (201 Created). Returns the uploadUrl.</summary>
        internal async Task<string> CreateUploadSessionAsync(string messageId, UploadSessionRequest request, CancellationToken cancellationToken)
        {
            string name = request.AttachmentItem == null ? "" : (request.AttachmentItem.Name ?? "");
            byte[] payload = GraphJson.SerializeToUtf8Bytes(request);
            GraphResponse response = await ExecuteAsync(
                "create upload session for '" + name + "'",
                () => JsonRequest(HttpMethod.Post, MessageUrl(messageId) + "/attachments/createUploadSession", payload),
                RequestKind.NonIdempotent,
                true,
                true,
                cancellationToken).ConfigureAwait(false);
            UploadSessionResponse? session = GraphJson.Deserialize<UploadSessionResponse>(response.Body);
            if (session == null || string.IsNullOrEmpty(session.UploadUrl))
            {
                throw new GraphMailException(
                    "Microsoft Graph create upload session for '" + name + "' failed (HTTP " + response.StatusCode + "): the response contained no uploadUrl." + Suffix(response.RequestId),
                    "NoUploadUrl", response.StatusCode, response.RequestId, false);
            }

            _log.Log("upload session created, expires " + (session.ExpirationDateTime ?? "?"));
            return session.UploadUrl!;
        }

        /// <summary>
        /// Streams the attachment to the upload session in ranges of <paramref name="chunkSize"/> bytes (the last one shorter).
        /// Each PUT carries Content-Type: application/octet-stream, Content-Length and Content-Range: bytes {start}-{end}/{total}
        /// and no Authorization header. Intermediate ranges must answer 200 OK (body with nextExpectedRanges), the final
        /// range must answer 201 Created. A 201 before the last range or a 200 for the last range fails the send.
        /// https://learn.microsoft.com/en-us/graph/outlook-large-attachments
        /// </summary>
        internal async Task UploadChunksAsync(string uploadUrl, MailAttachment attachment, long totalSize, int chunkSize, CancellationToken cancellationToken)
        {
            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            if (totalSize <= 0)
            {
                throw GraphMailException.Validation("Attachment '" + attachment.Name + "' is empty; an upload session needs at least one byte.");
            }

            byte[] buffer = new byte[chunkSize];
            int chunkCount = (int)((totalSize + chunkSize - 1) / chunkSize);
            int chunkIndex = 0;
            long offset = 0;
            using (Stream stream = OpenAttachment(attachment))
            {
                while (offset < totalSize)
                {
                    int want = (int)Math.Min(chunkSize, totalSize - offset);
                    int read = ReadAttachment(attachment, stream, buffer, want);
                    if (read < want)
                    {
                        throw GraphMailException.Validation(
                            "Attachment '" + attachment.Name + "' changed while sending: expected " + totalSize + " bytes but the source ended after " + (offset + read) + " bytes.");
                    }

                    chunkIndex++;
                    long start = offset;
                    long end = offset + read - 1;
                    bool last = end == totalSize - 1;
                    int count = read;
                    string operation = "upload chunk " + chunkIndex + "/" + chunkCount + " of '" + attachment.Name + "'";
                    GraphResponse response = await ExecuteAsync(
                        operation,
                        () => ChunkRequest(uploadUrl, buffer, count, start, end, totalSize),
                        RequestKind.Idempotent,
                        SendAuthorizationOnUploadChunks,
                        false,
                        cancellationToken).ConfigureAwait(false);

                    CheckChunkResponse(operation, response, last, end + 1);
                    offset += read;
                }
            }
        }

        /// <summary>
        /// Validates the status of one upload PUT: intermediate 200 (and, when present, nextExpectedRanges must start at
        /// <paramref name="nextOffset"/>), final 201.
        /// </summary>
        internal void CheckChunkResponse(string operation, GraphResponse response, bool last, long nextOffset)
        {
            int expected = last ? 201 : 200;
            if (response.StatusCode != expected)
            {
                string detail = last
                    ? (response.StatusCode == 200 ? " for the final range: the server still expects more data (next expected range " + NextExpectedRange(response.Body) + ")." : " for the final range (expected 201 Created).")
                    : (response.StatusCode == 201 ? ": the server reported the upload complete before the last range." : " (expected 200 OK).");
                throw new GraphMailException(
                    "Microsoft Graph " + operation + " failed: unexpected HTTP " + response.StatusCode + detail + Suffix(response.RequestId),
                    "UnexpectedStatus", response.StatusCode, response.RequestId, false);
            }

            if (!last)
            {
                string next = NextExpectedRange(response.Body);
                long nextStart;
                int dash = next.IndexOf('-');
                string startText = dash >= 0 ? next.Substring(0, dash) : next;
                if (startText.Length > 0
                    && long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out nextStart)
                    && nextStart != nextOffset)
                {
                    throw new GraphMailException(
                        "Microsoft Graph " + operation + " failed: the server expects the next range to start at byte " + nextStart + " but " + nextOffset + " bytes were sent." + Suffix(response.RequestId),
                        "UnexpectedRange", response.StatusCode, response.RequestId, false);
                }
            }
        }

        /// <summary>
        /// First value of nextExpectedRanges from an upload PUT response ("" when absent). Matched case-insensitively with a
        /// regex because the documented intermediate response mixes casing (ExpirationDateTime / nextExpectedRanges).
        /// </summary>
        internal static string NextExpectedRange(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return "";
            }

            Match match = NextExpectedRangesPattern.Match(body);
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>POST /users/{mailbox}/messages/{id}/send (202 Accepted).</summary>
        internal Task SendDraftAsync(string messageId, CancellationToken cancellationToken)
        {
            return ExecuteAsync("send draft", () => EmptyPost(MessageUrl(messageId) + "/send"), RequestKind.Delivering, true, false, cancellationToken);
        }

        /// <summary>Best-effort DELETE of a draft after a failed send; never throws.</summary>
        internal async Task TryDeleteDraftAsync(string messageId)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Delete, MessageUrl(messageId)))
                {
                    ApplyStandardHeaders(request);
                    AccessToken token = await _tokenProvider.GetAccessTokenAsync(false, CancellationToken.None).ConfigureAwait(false);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                    using (HttpResponseMessage response = await SendWithTimeoutAsync("delete draft", request, CancellationToken.None).ConfigureAwait(false))
                    {
                        _log.Log("draft cleanup: DELETE -> HTTP " + (int)response.StatusCode + " request-id=" + HeaderValue(response.Headers, RequestIdHeader));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Log("draft cleanup failed (ignored): " + ex.GetType().Name + ": " + FirstLine(ex.Message));
            }
        }

        /// <summary>
        /// Sends one logical request with retries. <paramref name="requestFactory"/> is called once per attempt because an
        /// <see cref="HttpRequestMessage"/> cannot be re-sent. Retries: 429/503/509 (plus 504 for <see cref="RequestKind.Idempotent"/>)
        /// up to MaxRetries honouring Retry-After; 401 once with a forced token refresh. Anything else non-2xx becomes a
        /// <see cref="GraphMailException"/>; for <see cref="RequestKind.Delivering"/> a 504 or a timeout is reported as an
        /// unknown outcome (not transient), because repeating the send could deliver the mail twice.
        /// </summary>
        private async Task<GraphResponse> ExecuteAsync(string operation, Func<HttpRequestMessage> requestFactory, RequestKind kind, bool authenticate, bool requiresMailReadWrite, CancellationToken cancellationToken)
        {
            int retries = 0;
            bool tokenRefreshed = false;
            bool forceRefresh = false;
            while (true)
            {
                using (HttpRequestMessage request = requestFactory())
                {
                    ApplyStandardHeaders(request);
                    if (authenticate)
                    {
                        AccessToken token = await AcquireTokenAsync(_tokenProvider, forceRefresh, cancellationToken).ConfigureAwait(false);
                        forceRefresh = false;
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                    }

                    long sent = request.Content == null ? 0 : (request.Content.Headers.ContentLength ?? 0);
                    string url = request.RequestUri == null ? "" : Diagnostics.SafeUrl(request.RequestUri.ToString());
                    _log.Log(request.Method.Method + " " + url + " (" + sent + " bytes) " + ClientRequestIdHeader + "=" + HeaderValue(request.Headers, ClientRequestIdHeader));

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    HttpResponseMessage sentResponse;
                    try
                    {
                        sentResponse = await SendWithTimeoutAsync(operation, request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GraphMailException timeout) when (kind == RequestKind.Delivering && timeout.IsTimeout)
                    {
                        throw OutcomeUnknown(timeout, timeout.StatusCode, timeout.RequestId);
                    }

                    using (HttpResponseMessage response = sentResponse)
                    {
                        int status = (int)response.StatusCode;
                        string body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        string requestId = HeaderValue(response.Headers, RequestIdHeader);
                        if (requestId.Length > 0)
                        {
                            LastRequestId = requestId;
                        }

                        _log.Log("HTTP " + status + " " + (response.ReasonPhrase ?? "") + " in " + stopwatch.ElapsedMilliseconds + " ms, " + body.Length + " chars"
                            + (requestId.Length > 0 ? ", " + RequestIdHeader + "=" + requestId : ""));

                        if (response.IsSuccessStatusCode)
                        {
                            return new GraphResponse(status, body, requestId);
                        }

                        if (status == 401 && authenticate && !tokenRefreshed)
                        {
                            tokenRefreshed = true;
                            forceRefresh = true;
                            _log.Log("HTTP 401: refreshing the token once and retrying");
                            continue;
                        }

                        if (IsRetryable(status, kind) && retries < _maxRetries)
                        {
                            TimeSpan delay = RetryDelay(response, retries);
                            retries++;
                            _log.Log("HTTP " + status + ": retry " + retries + "/" + _maxRetries + " in " + delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                            await _delay(delay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        GraphMailException error = BuildGraphError(operation, status, response, body, requestId);
                        if (kind == RequestKind.Delivering && status == 504)
                        {
                            error = OutcomeUnknown(error, status, error.RequestId);
                        }

                        error.RequiresMailReadWrite = requiresMailReadWrite;
                        if (error.RequestId.Length > 0)
                        {
                            LastRequestId = error.RequestId;
                        }

                        _log.Log("ERROR " + error.Message + (body.Length > 0 ? " | body: " + Truncate(body, 500) : ""));
                        throw error;
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> SendWithTimeoutAsync(string operation, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
                try
                {
                    // ResponseContentRead buffers the body inside SendAsync so the timeout covers the whole exchange.
                    return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _log.Log("timeout after " + _timeoutSeconds + " s");
                    throw new GraphMailException(
                        "Microsoft Graph " + operation + " failed: no response within " + _timeoutSeconds + " s (timeout). [mailbox " + _mailbox + "]",
                        GraphMailException.TransportCode, 0, "", true, ex) { IsTimeout = true };
                }
                catch (HttpRequestException ex)
                {
                    _log.Log("transport error: " + DescribeTransportError(ex));
                    throw new GraphMailException(
                        "Microsoft Graph " + operation + " failed: " + DescribeTransportError(ex) + " [mailbox " + _mailbox + "]",
                        GraphMailException.TransportCode, 0, "", true, ex);
                }
            }
        }

        /// <summary>
        /// Formats "Microsoft Graph {operation} failed (HTTP {status}, {code}): {message} [request-id ..., {date}, mailbox ...]"
        /// from the Graph error JSON; tolerates missing parts and non-JSON bodies.
        /// </summary>
        internal GraphMailException BuildGraphError(string operation, int status, HttpResponseMessage response, string body, string headerRequestId)
        {
            GraphErrorResponse? parsed = GraphJson.Deserialize<GraphErrorResponse>(body);
            GraphError? error = parsed == null ? null : parsed.Error;
            string code = (error == null ? null : error.Code) ?? "";
            string message = CollapseWhitespace((error == null ? null : error.Message) ?? "");
            string requestId = headerRequestId;
            string date = "";
            if (error != null && error.InnerError != null)
            {
                if (requestId.Length == 0)
                {
                    requestId = error.InnerError.RequestId ?? "";
                }

                date = error.InnerError.Date ?? "";
            }

            if (date.Length > 0 && date.IndexOf('+') < 0 && !date.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                date += "Z"; // Graph reports UTC without a designator
            }

            if (date.Length == 0)
            {
                DateTimeOffset stamp = response.Headers.Date ?? DateTimeOffset.UtcNow;
                date = stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            if (message.Length == 0)
            {
                message = response.ReasonPhrase ?? "";
                string snippet = Truncate(FirstLine(body), 200);
                if (snippet.Length > 0)
                {
                    message = message.Length > 0 ? message + ": " + snippet : snippet;
                }

                if (message.Length == 0)
                {
                    message = "no details in response";
                }
            }

            var text = new StringBuilder();
            text.Append("Microsoft Graph ").Append(operation).Append(" failed (HTTP ").Append(status);
            if (code.Length > 0)
            {
                text.Append(", ").Append(code);
            }

            text.Append("): ").Append(message).Append(" [");
            if (requestId.Length > 0)
            {
                text.Append("request-id ").Append(requestId).Append(", ");
            }

            text.Append(date).Append(", mailbox ").Append(_mailbox).Append(']');
            return new GraphMailException(text.ToString(), code.Length > 0 ? code : "HTTP" + status, status, requestId, status == 429 || status >= 500);
        }

        /// <summary>True when <paramref name="status"/> may be retried for a request of the given kind.</summary>
        internal static bool IsRetryable(int status, RequestKind kind)
        {
            if (Array.IndexOf(RetryableStatusCodes, status) >= 0)
            {
                return true;
            }

            return kind == RequestKind.Idempotent && Array.IndexOf(IdempotentOnlyRetryableStatusCodes, status) >= 0;
        }

        /// <summary>
        /// Rewrites the error of a mail-delivering call whose outcome is open (504 / timeout): adds the "check Sent Items"
        /// text, clears IsTransient (an automatic resend could duplicate the mail) and sets <see cref="GraphMailException.OutcomeUnknown"/>.
        /// </summary>
        private GraphMailException OutcomeUnknown(GraphMailException original, int status, string requestId)
        {
            string text = original.Message + string.Format(CultureInfo.InvariantCulture, OutcomeUnknownText, _mailbox);
            _log.Log("outcome unknown: the message may already have been sent; not retried");
            return new GraphMailException(text, original.ErrorCode, status, requestId, false, original.InnerException ?? original)
            {
                OutcomeUnknown = true,
                IsTimeout = original.IsTimeout,
            };
        }

        /// <summary>Retry-After (delta seconds or HTTP-date) when present, else 2 s * 2^attempt; capped at <see cref="MaxRetryDelaySeconds"/>.</summary>
        internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            TimeSpan? fromHeader = ParseRetryAfter(response);
            TimeSpan delay = fromHeader ?? TimeSpan.FromSeconds(BaseRetryDelay.TotalSeconds * Math.Pow(2, attempt));
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            TimeSpan max = TimeSpan.FromSeconds(MaxRetryDelaySeconds);
            return delay > max ? max : delay;
        }

        internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? typed = response.Headers.RetryAfter;
            if (typed != null)
            {
                if (typed.Delta.HasValue)
                {
                    return typed.Delta.Value;
                }

                if (typed.Date.HasValue)
                {
                    return typed.Date.Value - DateTimeOffset.UtcNow;
                }
            }

            string raw = HeaderValue(response.Headers, "Retry-After").Trim();
            if (raw.Length == 0)
            {
                return null;
            }

            int seconds;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            DateTimeOffset date;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
            {
                return date - DateTimeOffset.UtcNow;
            }

            return null;
        }

        /// <summary>Headers on every Graph call: Accept, client-request-id (new GUID per attempt), User-Agent; no Expect: 100-continue.</summary>
        private static void ApplyStandardHeaders(HttpRequestMessage request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation(ClientRequestIdHeader, Guid.NewGuid().ToString("D"));
            request.Headers.TryAddWithoutValidation("User-Agent", GraphMailSender.UserAgent);
            request.Headers.ExpectContinue = false;
        }

        private static HttpRequestMessage JsonRequest(HttpMethod method, string url, byte[] payload)
        {
            var request = new HttpRequestMessage(method, url);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Content = content;
            return request;
        }

        /// <summary>
        /// POST without body. POST .../send documents "Content-Length: 0" as required; an explicit empty content with an
        /// explicit length guarantees the header on both .NET Framework and modern .NET.
        /// https://learn.microsoft.com/en-us/graph/api/message-send
        /// </summary>
        internal static HttpRequestMessage EmptyPost(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentLength = 0;
            request.Content = content;
            return request;
        }

        private static HttpRequestMessage ChunkRequest(string uploadUrl, byte[] buffer, int count, long start, long end, long total)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            var content = new ByteArrayContent(buffer, 0, count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
            request.Content = content;
            return request;
        }

        private static Stream OpenAttachment(MailAttachment attachment)
        {
            try
            {
                return attachment.OpenRead();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw GraphMailException.Validation("Attachment '" + attachment.Name + "' cannot be read: " + attachment.FilePath + " (" + ex.Message + ")", ex);
            }
        }

        private static int ReadAttachment(MailAttachment attachment, Stream stream, byte[] buffer, int count)
        {
            try
            {
                return ReadFully(stream, buffer, count);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw GraphMailException.Validation("Attachment '" + attachment.Name + "' cannot be read: " + attachment.FilePath + " (" + ex.Message + ")", ex);
            }
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        internal static string HeaderValue(HttpHeaders headers, string name)
        {
            IEnumerable<string>? values;
            if (headers.TryGetValues(name, out values) && values != null)
            {
                foreach (string value in values)
                {
                    return value ?? "";
                }
            }

            return "";
        }

        /// <summary>"ExceptionType: first line -> InnerType: first line" (max 4 levels), no stack traces.</summary>
        internal static string DescribeTransportError(Exception exception)
        {
            var sb = new StringBuilder();
            Exception? current = exception;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                sb.Append(current.GetType().Name).Append(": ").Append(FirstLine(current.Message));
                current = current.InnerException;
                depth++;
            }

            return sb.ToString();
        }

        internal static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            int cut = text!.IndexOfAny(new[] { '\r', '\n' });
            return (cut >= 0 ? text.Substring(0, cut) : text).Trim();
        }

        internal static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars) + "...";
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private string Suffix(string requestId)
        {
            return " [" + (requestId.Length > 0 ? "request-id " + requestId + ", " : "") + "mailbox " + _mailbox + "]";
        }
    }
}
