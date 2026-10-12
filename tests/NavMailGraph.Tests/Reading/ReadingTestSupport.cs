using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NavMailGraph.Tests.Infrastructure;

namespace NavMailGraph.Tests.Reading
{
    internal static class ReadingTestSupport
    {
        internal const string FoldersUrl = TestSupport.MailboxUrl + "/mailFolders";

        /// <summary>Mailbox over a fake handler with a fake token provider and instant, recorded retry pauses.</summary>
        internal static GraphMailbox NewMailbox(FakeHttpMessageHandler handler, List<TimeSpan>? delays = null)
        {
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            connection.UseTokenProvider(new FakeTokenProvider());
            connection.RetryDelayOverride = (delay, ct) =>
            {
                if (delays != null)
                {
                    delays.Add(delay);
                }

                return Task.FromResult(0);
            };
            return new GraphMailbox(connection, TestSupport.Mailbox);
        }

        /// <summary>Path part of the request URL (no query), unescaped.</summary>
        internal static string PathOf(RecordedRequest request)
        {
            string url = request.Url;
            int q = url.IndexOf('?');
            return Uri.UnescapeDataString(q >= 0 ? url.Substring(0, q) : url);
        }

        /// <summary>Query parameters in order, unescaped.</summary>
        internal static List<KeyValuePair<string, string>> QueryOf(RecordedRequest request)
        {
            var result = new List<KeyValuePair<string, string>>();
            string url = request.Url;
            int q = url.IndexOf('?');
            if (q < 0)
            {
                return result;
            }

            foreach (string pair in url.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? "" : pair.Substring(eq + 1);
                result.Add(new KeyValuePair<string, string>(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
            }

            return result;
        }

        internal static string Param(RecordedRequest request, string name)
        {
            foreach (KeyValuePair<string, string> pair in QueryOf(request))
            {
                if (pair.Key == name)
                {
                    return pair.Value;
                }
            }

            return "";
        }

        internal static string Message(string id, string received, bool hasAttachments = false, string body = "<p>Hallo</p>", string extra = "")
        {
            return "{\"id\":\"" + id + "\",\"internetMessageId\":\"<" + id + "@example.com>\",\"subject\":\"Subject " + id + "\",\"receivedDateTime\":\"" + received
                + "\",\"hasAttachments\":" + (hasAttachments ? "true" : "false") + ",\"body\":{\"contentType\":\"html\",\"content\":\"" + body + "\"}" + extra + "}";
        }

        internal static string Page(string? nextLink, params string[] items)
        {
            return "{\"@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#users('nav%40contoso.com')/mailFolders('inbox')/messages\",\"value\":["
                + string.Join(",", items) + "]" + (nextLink != null ? ",\"@odata.nextLink\":\"" + nextLink + "\"" : "") + "}";
        }

        internal static string DeltaPage(string? nextLink, string? deltaLink, params string[] items)
        {
            return "{\"value\":[" + string.Join(",", items) + "]"
                + (nextLink != null ? ",\"@odata.nextLink\":\"" + nextLink + "\"" : "")
                + (deltaLink != null ? ",\"@odata.deltaLink\":\"" + deltaLink + "\"" : "") + "}";
        }

        internal static string Folders(params string[] idAndNames)
        {
            var items = new List<string>();
            for (int i = 0; i + 1 < idAndNames.Length; i += 2)
            {
                items.Add("{\"id\":\"" + idAndNames[i] + "\",\"displayName\":\"" + idAndNames[i + 1] + "\"}");
            }

            return "{\"value\":[" + string.Join(",", items) + "]}";
        }

        internal static HttpResponseMessage Binary(byte[] content, string contentType = "application/pdf")
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(content);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            response.Headers.TryAddWithoutValidation("request-id", "dl-1");
            return response;
        }
    }

    /// <summary>A forward-only, write-only stream like a NAV OutStream wrapper: Seek/Length/Position/Read throw.</summary>
    internal sealed class WriteOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();

        internal int FlushCount { get; private set; }

        internal int WriteCalls { get; private set; }

        internal byte[] Content
        {
            get { return _inner.ToArray(); }
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
            get { throw new NotSupportedException("Length"); }
        }

        public override long Position
        {
            get { throw new NotSupportedException("Position get"); }
            set { throw new NotSupportedException("Position set"); }
        }

        public override void Flush()
        {
            FlushCount++;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Read");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("Seek");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("SetLength");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            _inner.Write(buffer, offset, count);
        }
    }
}
