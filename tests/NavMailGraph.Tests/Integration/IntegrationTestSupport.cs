using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Storage;

namespace NavMailGraph.Tests.Integration
{
    internal static class IntegrationTestSupport
    {
        internal const int MiB = 1024 * 1024;

        internal const string AttachmentValueUrl = TestSupport.MailboxUrl + "/messages/m1/attachments/a1/$value";

        /// <summary>Connection over a fake handler with a fake token provider and instant retries.</summary>
        internal static GraphConnection NewConnection(FakeHttpMessageHandler handler, FakeTokenProvider? tokens = null)
        {
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            connection.UseTokenProvider(tokens ?? new FakeTokenProvider());
            connection.RetryDelayOverride = (delay, ct) => Task.FromResult(0);
            return connection;
        }

        /// <summary>Graph $value response; <paramref name="withLength"/> false = no Content-Length (chunked / unknown length).</summary>
        internal static HttpResponseMessage Value(byte[] content, string contentType, bool withLength)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (withLength)
            {
                response.Content = new ByteArrayContent(content);
            }
            else
            {
                response.Content = new StreamContent(new NonSeekableStream(content));
                response.Content.Headers.ContentLength = null;
            }

            if (contentType.Length > 0)
            {
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            response.Headers.TryAddWithoutValidation("request-id", "dl-1");
            return response;
        }

        /// <summary>A stream that delivers <paramref name="good"/> bytes and then fails like a broken connection.</summary>
        internal static HttpResponseMessage BrokenValue(int good)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingStream(TestSupport.Pattern(good))),
            };
            response.Content.Headers.ContentLength = null;
            return response;
        }

        /// <summary>Path part of the URL (no query), unescaped.</summary>
        internal static string PathOf(RecordedRequest request)
        {
            string url = request.Url;
            int q = url.IndexOf('?');
            return Uri.UnescapeDataString(q >= 0 ? url.Substring(0, q) : url);
        }

        internal static ObjectStorageClient NewS3(FakeHttpMessageHandler handler)
        {
            return StorageTestSupport.NewS3Client(handler);
        }

        internal static ObjectStorageClient NewAzure(FakeHttpMessageHandler handler)
        {
            return StorageTestSupport.NewAzureClient(handler);
        }
    }

    /// <summary>Delivers its data, then throws an IOException (connection reset).</summary>
    internal sealed class FailingStream : Stream
    {
        private readonly MemoryStream _inner;

        internal FailingStream(byte[] data)
        {
            _inner = new MemoryStream(data, false);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, Math.Min(count, 4096));
            if (read == 0)
            {
                throw new IOException("Unable to read data from the transport connection: Connection reset by peer.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
