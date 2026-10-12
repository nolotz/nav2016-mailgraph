using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using NavMailGraph.Reading;
using NavMailGraph.Storage;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Storage;
using Xunit;
using static NavMailGraph.Tests.Integration.IntegrationTestSupport;

namespace NavMailGraph.Tests.Integration
{
    public class CopyAttachmentToStorageTests
    {
        private static GraphMailbox NewMailbox(FakeHttpMessageHandler graph)
        {
            return new GraphMailbox(NewConnection(graph), TestSupport.Mailbox);
        }

        [Fact]
        public void Small_attachment_is_one_put_with_graph_content_type()
        {
            byte[] pdf = TestSupport.Pattern(3000);
            var graph = new FakeHttpMessageHandler().Enqueue(r => Value(pdf, "application/pdf", true));
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"etag-small\""));
            ObjectStorageClient storage = NewS3(s3);
            storage.KeyPrefix = "nav/prod/";
            GraphMailbox mailbox = NewMailbox(graph);

            Assert.Equal("", mailbox.CopyAttachmentToStorage("m1", "a1", storage, "INBOX/2026/10/1/Rechnung.bin"));

            RecordedRequest download = graph.Requests.Single();
            Assert.Equal(HttpMethod.Get, download.Method);
            Assert.Equal(AttachmentValueUrl, PathOf(download));
            Assert.Contains("IdType=\"ImmutableId\"", download.Header("Prefer"));
            RecordedRequest put = s3.Requests.Single();
            Assert.Equal(HttpMethod.Put, put.Method);
            Assert.Equal("https://s3.example.test/nav-bucket/nav/prod/INBOX/2026/10/1/Rechnung.bin", put.Uri!.AbsoluteUri);
            Assert.Equal(pdf, put.Body);
            Assert.Equal("application/pdf", put.Header("Content-Type"));
            Assert.Equal(SigV4.Sha256Hex(pdf), put.Header("x-amz-content-sha256"));

            Assert.Equal("INBOX/2026/10/1/Rechnung.bin", mailbox.LastStorageKey);
            Assert.Equal(3000L, mailbox.LastStorageSize);
            Assert.Equal("\"etag-small\"", mailbox.LastStorageETag);
            Assert.Equal(3000L, mailbox.LastDownloadSize);
            Assert.Equal("", storage.LastError);
            Assert.Contains("uploaded 3000 bytes", storage.LastDiagnostics);
        }

        [Fact]
        public void Large_attachment_of_unknown_length_is_streamed_in_blocks()
        {
            byte[] data = TestSupport.Pattern(5 * MiB + 1234, 3);
            var graph = new FakeHttpMessageHandler().Enqueue(r => Value(data, "", false));
            var azure = new FakeHttpMessageHandler();
            azure.Fallback = r => StorageTestSupport.Empty(201, "ETag: \"0xBLOCKS\"");
            ObjectStorageClient storage = NewAzure(azure);
            storage.MultipartThresholdBytes = 2 * MiB;
            storage.PartSizeBytes = 2 * MiB;
            GraphMailbox mailbox = NewMailbox(graph);

            Assert.Equal("", mailbox.CopyAttachmentToStorage("m1", "a1", storage, "big/scan.pdf"));

            var requests = azure.Requests;
            Assert.Equal(4, requests.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Contains("comp=block&blockid=", requests[i].Url);
            }

            Assert.Equal(data, requests.Take(3).SelectMany(r => r.Body).ToArray());
            Assert.EndsWith("comp=blocklist", requests[3].Url);
            Assert.Equal("application/pdf", requests[3].Header("x-ms-blob-content-type"));
            Assert.Equal(data.LongLength, mailbox.LastStorageSize);
            Assert.Equal("\"0xBLOCKS\"", mailbox.LastStorageETag);
            Assert.Equal("big/scan.pdf", mailbox.LastStorageKey);
        }

        [Fact]
        public void Reference_attachment_gives_a_clear_error_without_storage_traffic()
        {
            var graph = new FakeHttpMessageHandler().Enqueue(405, TestSupport.GraphError("ErrorInvalidRequest", "not supported", "r-405"));
            var s3 = new FakeHttpMessageHandler();
            ObjectStorageClient storage = NewS3(s3);
            GraphMailbox mailbox = NewMailbox(graph);

            string error = mailbox.CopyAttachmentToStorage("m1", "a1", storage, "x.bin");
            Assert.Contains("reference attachments", error);
            Assert.Empty(s3.Requests);
            Assert.Equal("", mailbox.LastStorageKey);
            Assert.Equal(0L, mailbox.LastStorageSize);
        }

        [Fact]
        public void Storage_failure_names_the_storage_and_gets_no_mail_permission_hint()
        {
            byte[] data = TestSupport.Pattern(100);
            var graph = new FakeHttpMessageHandler().Enqueue(r => Value(data, "application/pdf", true));
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Xml(403, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>AccessDenied</Code><Message>Access Denied</Message><RequestId>S3REQ1</RequestId></Error>", "x-amz-request-id: S3REQ1"));
            ObjectStorageClient storage = NewS3(s3);
            GraphMailbox mailbox = NewMailbox(graph);

            string error = mailbox.CopyAttachmentToStorage("m1", "a1", storage, "x.pdf");
            Assert.StartsWith("Copy of attachment a1 to object storage failed after 100 bytes read from Graph: S3 upload failed (HTTP 403, AccessDenied)", error);
            Assert.DoesNotContain("Mail.Read", error);
            Assert.Equal("S3REQ1", mailbox.LastRequestId);
            Assert.Contains("AccessDenied", storage.LastError);
            Assert.Equal("S3REQ1", storage.LastRequestId);
            Assert.Equal("", mailbox.LastStorageKey);
            Assert.DoesNotContain(StorageTestSupport.S3Secret, error + mailbox.LastDiagnostics + storage.LastDiagnostics);
        }

        [Fact]
        public void Broken_graph_download_is_reported_as_a_graph_error()
        {
            var graph = new FakeHttpMessageHandler().Enqueue(r => BrokenValue(70000));
            var azure = new FakeHttpMessageHandler();
            azure.Fallback = r => StorageTestSupport.Empty(201);
            ObjectStorageClient storage = NewAzure(azure);
            storage.MultipartThresholdBytes = 64 * 1024;
            storage.PartSizeBytes = 64 * 1024;
            GraphMailbox mailbox = NewMailbox(graph);

            string error = mailbox.CopyAttachmentToStorage("m1", "a1", storage, "x.pdf");
            Assert.StartsWith("Microsoft Graph download of attachment a1 failed after 70000 bytes", error);
            Assert.Contains("Connection reset", error);
            Assert.DoesNotContain(azure.Requests, r => r.Url.Contains("blocklist"));
            Assert.Equal("", mailbox.LastStorageKey);
            Assert.Contains("Microsoft Graph download", storage.LastError);
        }

        [Fact]
        public void Unconfigured_storage_and_bad_arguments_fail_before_any_request()
        {
            var graph = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(graph);
            var storage = new ObjectStorageClient();

            Assert.Contains("Object storage is not configured", mailbox.CopyAttachmentToStorage("m1", "a1", storage, "x.pdf"));
            Assert.Contains("object key is required", mailbox.CopyAttachmentToStorage("m1", "a1", storage, " "));
            Assert.Contains("attachment id is required", mailbox.CopyAttachmentToStorage("m1", "", storage, "x.pdf"));
            ObjectStorageClient s3 = NewS3(new FakeHttpMessageHandler());
            Assert.Contains("'.' or '..' path segments", mailbox.CopyAttachmentToStorage("m1", "a1", s3, "../x.pdf"));
            Assert.Empty(graph.Requests);
            Assert.Throws<ArgumentNullException>(() => mailbox.CopyAttachmentToStorage("m1", "a1", null!, "x.pdf"));
        }

        [Fact]
        public void Idle_timer_is_kept_alive_while_not_reading()
        {
            int resets = 0;
            using (var inner = new System.IO.MemoryStream(new byte[10]))
            using (var pipe = new IdleTimeoutReadStream(inner, () => Interlocked.Increment(ref resets), TimeSpan.FromMilliseconds(20), "attachment x"))
            {
                var buffer = new byte[4];
                Assert.Equal(4, pipe.Read(buffer, 0, 4));
                int afterRead = Volatile.Read(ref resets);
                Assert.True(afterRead >= 2);
                SpinWait.SpinUntil(() => Volatile.Read(ref resets) > afterRead + 2, TimeSpan.FromSeconds(5));
                Assert.True(Volatile.Read(ref resets) > afterRead + 2, "keep-alive did not reset the idle timer");
                Assert.False(pipe.CanSeek);
                Assert.Throws<NotSupportedException>(() => pipe.Length);
                Assert.Equal(4L, pipe.BytesRead);
            }
        }
    }
}
