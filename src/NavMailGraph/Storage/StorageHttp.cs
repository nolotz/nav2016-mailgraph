using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Internal;

namespace NavMailGraph.Storage
{
    /// <summary>One logical storage request (re-created and re-signed for every attempt).</summary>
    internal sealed class StorageCall
    {
        internal StorageCall(string operation, string key, Func<bool, Task<HttpRequestMessage>> createRequest)
        {
            Operation = operation;
            Key = key;
            CreateRequest = createRequest;
        }

        /// <summary>Short operation name for texts ("upload", "download", "put block", ...).</summary>
        internal string Operation { get; }

        /// <summary>Object key for error texts ("" for container/bucket operations).</summary>
        internal string Key { get; }

        /// <summary>Creates and signs the request; the argument is forceRefresh (bearer token after a 401).</summary>
        internal Func<bool, Task<HttpRequestMessage>> CreateRequest { get; }

        /// <summary>Retried on 408/429/500/502/503/504 and transport errors. Only for idempotent operations.</summary>
        internal bool Idempotent { get; set; }

        /// <summary>True: the response is returned after the headers (body not buffered) for streaming downloads.</summary>
        internal bool StreamResponse { get; set; }

        /// <summary>Additional statuses returned to the caller instead of being turned into an error (e.g. 404 for HEAD).</summary>
        internal Func<int, string, bool>? AcceptStatus { get; set; }

        /// <summary>True when a 401 may be retried once with a refreshed token.</summary>
        internal bool CanRefreshAuth { get; set; }
    }

    /// <summary>
    /// Shared HTTP engine of the storage backends: per-attempt timeout, retries with Retry-After (idempotent operations only),
    /// redacted logging and error texts with provider code/message/request id. Reuses <see cref="GraphClient"/>'s helpers.
    /// </summary>
    internal sealed class StorageHttp
    {
        internal const int CopyBufferSize = 81920;

        internal static readonly int[] RetryableStatusCodes = { 408, 429, 500, 502, 503, 504 };

        private static readonly Regex SecretQueryPattern = new Regex(
            @"((?:sig|X-Amz-Signature|X-Amz-Security-Token|X-Amz-Credential)=)[^&\s""'<]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SignaturePattern = new Regex(
            @"((?:Signature=|SharedKey\s+[^:\s]+:))[A-Za-z0-9+/=%]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpClient _http;

        private readonly Diagnostics _log;

        private readonly int _timeoutSeconds;

        private readonly int _maxRetries;

        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        internal StorageHttp(HttpClient http, Diagnostics log, string provider, string target, int timeoutSeconds, int maxRetries, Func<TimeSpan, CancellationToken, Task> delay)
        {
            _http = http;
            _log = log;
            Provider = provider;
            Target = target;
            _timeoutSeconds = timeoutSeconds;
            _maxRetries = maxRetries;
            _delay = delay;
        }

        /// <summary>"Azure Blob" or "S3".</summary>
        internal string Provider { get; }

        /// <summary>"container x" / "bucket y" for error texts.</summary>
        internal string Target { get; }

        internal int TimeoutSeconds
        {
            get { return _timeoutSeconds; }
        }

        internal int MaxRetries
        {
            get { return _maxRetries; }
        }

        internal Diagnostics Log
        {
            get { return _log; }
        }

        /// <summary>Request id of the last response that carried one.</summary>
        internal string LastRequestId { get; private set; } = "";

        /// <summary>
        /// Sends <paramref name="call"/> and returns the response (2xx or accepted status); the caller disposes it.
        /// Everything else becomes a <see cref="GraphMailException"/> with a complete user-facing text.
        /// </summary>
        internal async Task<HttpResponseMessage> SendAsync(StorageCall call, CancellationToken cancellationToken)
        {
            int retries = 0;
            bool refreshed = false;
            bool forceRefresh = false;
            while (true)
            {
                HttpResponseMessage response;
                using (HttpRequestMessage request = await call.CreateRequest(forceRefresh).ConfigureAwait(false))
                {
                    forceRefresh = false;
                    request.Headers.ExpectContinue = false;
                    request.Headers.TryAddWithoutValidation("User-Agent", GraphMailSender.UserAgent);
                    long sent = request.Content == null ? 0 : (request.Content.Headers.ContentLength ?? -1);
                    _log.Log(request.Method.Method + " " + SafeUrl(request.RequestUri) + " (" + sent + " bytes)");
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        response = await SendOnceAsync(call, request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GraphMailException transport) when (transport.IsTransient && call.Idempotent && retries < _maxRetries && !cancellationToken.IsCancellationRequested)
                    {
                        TimeSpan pause = TimeSpan.FromSeconds(GraphClient.BaseRetryDelay.TotalSeconds * Math.Pow(2, retries));
                        retries++;
                        _log.Log("transport error, retry " + retries + "/" + _maxRetries + " in " + pause.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                        await _delay(pause, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    int status = (int)response.StatusCode;
                    string requestId = RequestIdOf(response);
                    if (requestId.Length > 0)
                    {
                        LastRequestId = requestId;
                    }

                    _log.Log("HTTP " + status + " " + (response.ReasonPhrase ?? "") + " in " + stopwatch.ElapsedMilliseconds + " ms"
                        + (requestId.Length > 0 ? ", request-id " + requestId : ""));

                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    if (call.AcceptStatus != null)
                    {
                        string errorCode = GraphClient.HeaderValue(response.Headers, "x-ms-error-code");
                        if (errorCode.Length == 0 && !call.StreamResponse)
                        {
                            StorageErrorInfo? parsed = StorageXml.ParseError(await ReadBodyAsync(response).ConfigureAwait(false));
                            errorCode = parsed == null ? "" : parsed.Code;
                        }

                        if (call.AcceptStatus(status, errorCode))
                        {
                            return response;
                        }
                    }

                    using (response)
                    {
                        string body = await ReadBodyAsync(response).ConfigureAwait(false);
                        if (status == 401 && call.CanRefreshAuth && !refreshed)
                        {
                            refreshed = true;
                            forceRefresh = true;
                            _log.Log("HTTP 401: refreshing the token once and retrying");
                            continue;
                        }

                        if (call.Idempotent && Array.IndexOf(RetryableStatusCodes, status) >= 0 && retries < _maxRetries)
                        {
                            TimeSpan pause = GraphClient.RetryDelay(response, retries);
                            retries++;
                            _log.Log("HTTP " + status + ": retry " + retries + "/" + _maxRetries + " in " + pause.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                            await _delay(pause, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        GraphMailException error = BuildError(call.Operation, call.Key, status, response, body);
                        _log.Log("ERROR " + error.Message);
                        throw error;
                    }
                }
            }
        }

        /// <summary>
        /// "{Provider} {operation} failed (HTTP {status}, {code}): {message} [request-id ..., {date}, {target}, key ...]".
        /// Code and message come from the &lt;Error&gt; body, else x-ms-error-code / the reason phrase.
        /// </summary>
        internal GraphMailException BuildError(string operation, string key, int status, HttpResponseMessage response, string body)
        {
            StorageErrorInfo? info = StorageXml.ParseError(body);
            string code = info != null && info.Code.Length > 0 ? info.Code : GraphClient.HeaderValue(response.Headers, "x-ms-error-code");
            string message = info == null ? "" : GraphClient.FirstLine(info.Message);
            string requestId = RequestIdOf(response);
            if (requestId.Length == 0 && info != null)
            {
                requestId = info.RequestId;
            }

            if (message.Length == 0)
            {
                message = status == 404 && string.IsNullOrEmpty(code) ? "not found" : (response.ReasonPhrase ?? "");
                if (message.Length == 0)
                {
                    message = "no details in response";
                }
            }

            DateTimeOffset stamp = response.Headers.Date ?? DateTimeOffset.UtcNow;
            var text = new StringBuilder();
            text.Append(Provider).Append(' ').Append(operation).Append(" failed (HTTP ").Append(status);
            if (code.Length > 0)
            {
                text.Append(", ").Append(code);
            }

            text.Append("): ").Append(message).Append(" [");
            if (requestId.Length > 0)
            {
                text.Append("request-id ").Append(requestId).Append(", ");
            }

            text.Append(stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(", ").Append(Target);
            if (key.Length > 0)
            {
                text.Append(", key ").Append(key);
            }

            text.Append(']');
            string full = Redact(Internal.ErrorHints.Append(text.ToString(), StorageErrorHints.GetHint(code, status, message)));
            return new GraphMailException(full, code.Length > 0 ? code : "HTTP" + status, status, requestId, status == 429 || status >= 500 || IsTransientErrorCode(code));
        }

        /// <summary>
        /// Provider error codes that mean "try again" even when they arrive with HTTP 200 (S3 CompleteMultipartUpload can
        /// fail after the 200 headers were sent).
        /// </summary>
        internal static bool IsTransientErrorCode(string? code)
        {
            return string.Equals(code, "InternalError", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "SlowDown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "ServiceUnavailable", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Exponential back-off pause before retry number <paramref name="retries"/> + 1 (for retry loops outside <see cref="SendAsync"/>).</summary>
        internal Task PauseBeforeRetryAsync(int retries, CancellationToken cancellationToken)
        {
            TimeSpan pause = TimeSpan.FromSeconds(GraphClient.BaseRetryDelay.TotalSeconds * Math.Pow(2, retries));
            _log.Log("retry " + (retries + 1) + "/" + _maxRetries + " in " + pause.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
            return _delay(pause, cancellationToken);
        }

        /// <summary>
        /// Reads a streamed (<see cref="StorageCall.StreamResponse"/>) body as UTF-8 text with the idle timeout of
        /// <see cref="CopyBodyAsync"/>: slow responses that keep sending data (S3 CompleteMultipartUpload whitespace) do not time out.
        /// </summary>
        internal async Task<string> ReadStreamedBodyAsync(string operation, string key, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                await CopyBodyAsync(operation, key, response, buffer, cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        /// <summary>Validation error (no HTTP involved).</summary>
        internal GraphMailException Validation(string operation, string key, string reason)
        {
            return GraphMailException.Validation(Redact(Provider + " " + operation + " failed: " + reason + " [" + Target + (key.Length > 0 ? ", key " + key : "") + "]"));
        }

        /// <summary>Transport error (connection lost, DNS, TLS, timeout).</summary>
        internal GraphMailException Transport(string operation, string key, string reason, Exception? inner, bool transient)
        {
            return new GraphMailException(
                Redact(Provider + " " + operation + " failed: " + reason + " [" + Target + (key.Length > 0 ? ", key " + key : "") + "]"),
                GraphMailException.TransportCode, 0, "", transient, inner);
        }

        /// <summary>
        /// Copies a streamed response body to <paramref name="target"/> with an 81920-byte buffer. <see cref="TimeoutSeconds"/>
        /// applies as an idle timeout per read (the response is disposed when it fires, which also unblocks .NET Framework reads).
        /// Verifies Content-Length when present. Returns the number of bytes copied.
        /// </summary>
        internal async Task<long> CopyBodyAsync(string operation, string key, HttpResponseMessage response, Stream target, CancellationToken cancellationToken)
        {
            long? expected = response.Content == null ? null : response.Content.Headers.ContentLength;
            long total = 0;
            if (response.Content == null)
            {
                return 0;
            }

            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (idle.Token.Register(() => DisposeQuietly(response)))
            {
                Stream body;
                try
                {
                    body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException)
                {
                    throw Transport(operation, key, "the response body could not be read: " + GraphClient.DescribeTransportError(ex), ex, true);
                }

                using (body)
                {
                    byte[] buffer = new byte[CopyBufferSize];
                    while (true)
                    {
                        int read;
                        idle.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
                        try
                        {
                            read = await body.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            throw Transport(operation, key, "no data received for " + _timeoutSeconds + " s after " + total + " bytes (timeout)", ex, true);
                        }
                        catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException)
                        {
                            throw Transport(operation, key, "the connection was lost after " + total + " bytes: " + GraphClient.DescribeTransportError(ex), ex, true);
                        }

                        idle.CancelAfter(Timeout.InfiniteTimeSpan);
                        if (read <= 0)
                        {
                            break;
                        }

                        try
                        {
                            await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ObjectDisposedException)
                        {
                            throw GraphMailException.Validation(Redact(Provider + " " + operation + " failed: the target could not be written after " + total + " bytes (" + ex.Message + ")"), ex);
                        }

                        total += read;
                    }
                }
            }

            if (expected.HasValue && expected.Value != total)
            {
                throw Transport(operation, key, "the download ended after " + total + " of " + expected.Value + " bytes", null, true);
            }

            return total;
        }

        /// <summary>Reads a (buffered) response body as UTF-8 text, "" when there is none.</summary>
        internal static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            if (response.Content == null)
            {
                return "";
            }

            try
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException)
            {
                return "";
            }
        }

        /// <summary>x-ms-request-id (Azure) or x-amz-request-id (S3).</summary>
        internal static string RequestIdOf(HttpResponseMessage response)
        {
            string id = GraphClient.HeaderValue(response.Headers, "x-ms-request-id");
            return id.Length > 0 ? id : GraphClient.HeaderValue(response.Headers, "x-amz-request-id");
        }

        /// <summary>Masks SAS signatures, SigV4 signatures/credentials/session tokens and Shared Key signatures, then applies <see cref="Diagnostics.Redact"/>.</summary>
        internal static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            string result = SecretQueryPattern.Replace(text!, "$1[redacted]");
            result = SignaturePattern.Replace(result, "$1[redacted]");
            return Diagnostics.Redact(result);
        }

        /// <summary>URL without query (SAS tokens and presign parameters live there).</summary>
        internal static string SafeUrl(Uri? uri)
        {
            return uri == null ? "" : Diagnostics.SafeUrl(uri.ToString());
        }

        private async Task<HttpResponseMessage> SendOnceAsync(StorageCall call, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                TimeSpan limit = TimeSpan.FromSeconds(_timeoutSeconds);
                timeout.CancelAfter(limit);
                if (request.Content != null && !(request.Content is ProgressContent))
                {
                    // The timeout counts from the last progress: every chunk of the request body that is sent restarts it, so slow
                    // uplinks do not fail large uploads as long as data keeps flowing (the content headers are already signed and are copied).
                    request.Content = new ProgressContent(request.Content, () => RestartQuietly(timeout, limit));
                }

                try
                {
                    HttpCompletionOption option = call.StreamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                    return await _http.SendAsync(request, option, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _log.Log("timeout after " + _timeoutSeconds + " s");
                    throw Transport(call.Operation, call.Key, "no response within " + _timeoutSeconds + " s (timeout)", ex, true);
                }
                catch (HttpRequestException ex)
                {
                    string reason = GraphClient.DescribeTransportError(ex);
                    _log.Log("transport error: " + Redact(reason));
                    throw Transport(call.Operation, call.Key, reason, ex, true);
                }
                catch (IOException ex)
                {
                    // Reading a request body stream (file) can fail inside SendAsync.
                    throw Transport(call.Operation, call.Key, GraphClient.DescribeTransportError(ex), ex, false);
                }
            }
        }

        private static void RestartQuietly(CancellationTokenSource timeout, TimeSpan limit)
        {
            try
            {
                if (!timeout.IsCancellationRequested)
                {
                    timeout.CancelAfter(limit);
                }
            }
            catch (ObjectDisposedException)
            {
                // the attempt is already over
            }
        }

        private static void DisposeQuietly(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // best effort: only used to abort a stalled read
            }
        }
    }

    /// <summary>
    /// Wraps a request body and reports progress for every chunk written to the connection (at most
    /// <see cref="ProgressStream.ChunkSize"/> bytes per write, so one large buffer write still reports progress). Copies the headers.
    /// </summary>
    internal sealed class ProgressContent : HttpContent
    {
        private readonly HttpContent _inner;

        private readonly Action _progress;

        internal ProgressContent(HttpContent inner, Action progress)
        {
            _inner = inner;
            _progress = progress;
            long? length = inner.Headers.ContentLength;
            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>> header in inner.Headers)
            {
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (length.HasValue)
            {
                Headers.ContentLength = length;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _inner.CopyToAsync(new ProgressStream(stream, _progress));
        }

        protected override bool TryComputeLength(out long length)
        {
            long? known = _inner.Headers.ContentLength;
            length = known ?? 0;
            return known.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Write-only pass-through stream that splits writes into chunks and calls a progress callback before each one.</summary>
    internal sealed class ProgressStream : Stream
    {
        internal const int ChunkSize = 65536;

        private readonly Stream _inner;

        private readonly Action _progress;

        internal ProgressStream(Stream inner, Action progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int chunk = Math.Min(count, ChunkSize);
                _progress();
                _inner.Write(buffer, offset, chunk);
                offset += chunk;
                count -= chunk;
            }

            _progress();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                int chunk = Math.Min(count, ChunkSize);
                _progress();
                await _inner.WriteAsync(buffer, offset, chunk, cancellationToken).ConfigureAwait(false);
                offset += chunk;
                count -= chunk;
            }

            _progress();
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            // the connection stream belongs to the handler
        }
    }
}
