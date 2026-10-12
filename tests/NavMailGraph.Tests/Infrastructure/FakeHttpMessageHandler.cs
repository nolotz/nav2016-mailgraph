using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Tests.Infrastructure
{
    /// <summary>One request as seen by <see cref="FakeHttpMessageHandler"/> (copied at send time, so buffers reused by the caller do not matter).</summary>
    internal sealed class RecordedRequest
    {
        internal RecordedRequest(HttpMethod method, Uri? uri, Dictionary<string, string> headers, string? authorization, byte[] body, long? contentLength, bool hasContent)
        {
            Method = method;
            Uri = uri;
            Headers = headers;
            Authorization = authorization;
            Body = body;
            ContentLength = contentLength;
            HasContent = hasContent;
        }

        internal HttpMethod Method { get; }

        internal Uri? Uri { get; }

        /// <summary>The URL exactly as the code built it (Uri.OriginalString).</summary>
        internal string Url
        {
            get { return Uri == null ? "" : Uri.OriginalString; }
        }

        /// <summary>Request and content headers, case-insensitive names, multiple values joined with ", ".</summary>
        internal Dictionary<string, string> Headers { get; }

        /// <summary>Authorization header value ("Bearer xyz") or null when absent.</summary>
        internal string? Authorization { get; }

        internal byte[] Body { get; }

        internal long? ContentLength { get; }

        internal bool HasContent { get; }

        internal string BodyText
        {
            get { return Encoding.UTF8.GetString(Body); }
        }

        internal string Header(string name)
        {
            string? value;
            return Headers.TryGetValue(name, out value) ? value : "";
        }

        internal bool HasHeader(string name)
        {
            return Headers.ContainsKey(name);
        }

        public override string ToString()
        {
            return Method + " " + Url;
        }
    }

    /// <summary>
    /// Scripted HTTP handler: records every request (method, URL, headers, body bytes) and answers from a queue of
    /// responders; when the queue is empty, <see cref="Fallback"/> answers (default: HTTP 599 so a missing script is obvious).
    /// </summary>
    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _sync = new object();

        private readonly Queue<Func<RecordedRequest, HttpResponseMessage>> _queue = new Queue<Func<RecordedRequest, HttpResponseMessage>>();

        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();

        internal Func<RecordedRequest, HttpResponseMessage>? Fallback { get; set; }

        internal IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToList();
                }
            }
        }

        internal int PendingResponses
        {
            get
            {
                lock (_sync)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>Queues a response.</summary>
        internal FakeHttpMessageHandler Enqueue(int status, string? body = null, string? requestId = null, TimeSpan? retryAfter = null, string contentType = "application/json")
        {
            return Enqueue(_ => Response(status, body, requestId, retryAfter, contentType));
        }

        internal FakeHttpMessageHandler Enqueue(Func<RecordedRequest, HttpResponseMessage> responder)
        {
            lock (_sync)
            {
                _queue.Enqueue(responder);
            }

            return this;
        }

        /// <summary>Queues a responder that throws (transport failure).</summary>
        internal FakeHttpMessageHandler EnqueueException(Exception exception)
        {
            return Enqueue(_ => throw exception);
        }

        internal static HttpResponseMessage Response(int status, string? body = null, string? requestId = null, TimeSpan? retryAfter = null, string contentType = "application/json")
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            if (body != null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, contentType);
            }
            else
            {
                response.Content = new ByteArrayContent(new byte[0]);
            }

            if (requestId != null)
            {
                response.Headers.TryAddWithoutValidation("request-id", requestId);
            }

            if (retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            byte[] body = new byte[0];
            long? contentLength = null;
            bool hasContent = request.Content != null;
            if (request.Content != null)
            {
                contentLength = request.Content.Headers.ContentLength;
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }

                body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }

            string? authorization = request.Headers.Authorization == null ? null : request.Headers.Authorization.ToString();
            var recorded = new RecordedRequest(request.Method, request.RequestUri, headers, authorization, body, contentLength, hasContent);

            Func<RecordedRequest, HttpResponseMessage>? responder = null;
            lock (_sync)
            {
                _requests.Add(recorded);
                if (_queue.Count > 0)
                {
                    responder = _queue.Dequeue();
                }
            }

            if (responder == null)
            {
                responder = Fallback ?? (_ => Response(599, "{\"error\":{\"code\":\"NoScriptedResponse\",\"message\":\"FakeHttpMessageHandler has no response queued for " + recorded + "\"}}"));
            }

            HttpResponseMessage response = responder(recorded);
            response.RequestMessage = request;
            return response;
        }
    }
}
