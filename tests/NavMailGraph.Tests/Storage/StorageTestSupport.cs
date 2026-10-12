using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Internal;
using NavMailGraph.Storage;
using NavMailGraph.Tests.Infrastructure;

namespace NavMailGraph.Tests.Storage
{
    internal static class StorageTestSupport
    {
        /// <summary>Azurite's well-known development account key (public, documented by Microsoft).</summary>
        internal const string DevStoreKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

        internal const string S3Secret = "s3cr3t-Example-Value-1234567890";

        internal static readonly DateTime FixedTime = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

        internal static S3Backend NewS3Backend(string serviceUrl, string bucket, bool pathStyle, DateTime now)
        {
            var http = new StorageHttp(HttpClientProvider.Create(new FakeHttpMessageHandler()), new Diagnostics(), "S3", "bucket " + bucket, 30, 0, NoDelay);
            var settings = new StorageSettings { MultipartThresholdBytes = 64L << 20, PartSizeBytes = 8 << 20, TimeoutSeconds = 30, MaxRetries = 0 };
            return new S3Backend(http, settings, () => now, serviceUrl, SigV4Tests.Region, bucket, pathStyle, SigV4Tests.AccessKey, SigV4Tests.Secret, "");
        }

        /// <summary>Client over a fake handler with a fixed clock and instant retries (pauses are recorded).</summary>
        internal static ObjectStorageClient NewClient(FakeHttpMessageHandler handler, List<TimeSpan>? delays)
        {
            return new ObjectStorageClient(HttpClientProvider.Create(handler), () => FixedTime, (pause, ct) =>
            {
                if (delays != null)
                {
                    delays.Add(pause);
                }

                return Task.FromResult(0);
            });
        }

        internal static ObjectStorageClient NewS3Client(FakeHttpMessageHandler handler)
        {
            ObjectStorageClient client = NewClient(handler, null);
            client.UseS3("https://s3.example.test", "eu-central-1", "nav-bucket", "AKIDEXAMPLE", S3Secret);
            return client;
        }

        internal static ObjectStorageClient NewAzureClient(FakeHttpMessageHandler handler)
        {
            ObjectStorageClient client = NewClient(handler, null);
            client.UseAzureBlobSharedKey("devstoreaccount1", DevStoreKey, "docs");
            client.ServiceUrl = "http://127.0.0.1:10000/devstoreaccount1";
            return client;
        }

        internal static Task NoDelay(TimeSpan pause, CancellationToken ct)
        {
            return Task.FromResult(0);
        }

        /// <summary>Response with XML body and optional headers.</summary>
        internal static HttpResponseMessage Xml(int status, string body, params string[] headers)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            AddHeaders(response, headers);
            return response;
        }

        /// <summary>Empty response with headers ("Name: value").</summary>
        internal static HttpResponseMessage Empty(int status, params string[] headers)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(new byte[0]) };
            AddHeaders(response, headers);
            return response;
        }

        internal static HttpResponseMessage Bytes(int status, byte[] body)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentLength = body.Length;
            return response;
        }

        internal static byte[] Pattern(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)((i * 31 + (i >> 8)) & 0xFF);
            }

            return bytes;
        }

        private static void AddHeaders(HttpResponseMessage response, string[] headers)
        {
            foreach (string header in headers)
            {
                int colon = header.IndexOf(':');
                string name = header.Substring(0, colon).Trim();
                string value = header.Substring(colon + 1).Trim();
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }
    }

    /// <summary>Forward-only stream without length (like a Graph $value response body).</summary>
    internal sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        internal NonSeekableStream(byte[] data)
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

        /// <summary>Returns at most 1000 bytes per read to exercise the "read until the part is full" loop.</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, Math.Min(count, 1000));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
