using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Storage;
using Xunit;
using static NavMailGraph.Tests.Integration.IntegrationTestSupport;

namespace NavMailGraph.Tests.Integration
{
    public class StorageAttachmentTests
    {
        private static GraphMailSender NewSender(FakeHttpMessageHandler graph, TempDirectory temp)
        {
            GraphMailSender sender = TestSupport.NewSender(graph);
            sender.TempDirectoryOverride = temp.Path;
            return sender;
        }

        [Fact]
        public void Attachment_is_downloaded_to_a_temp_file_sent_and_the_temp_file_removed()
        {
            using (var temp = new TempDirectory())
            {
                byte[] pdf = TestSupport.Pattern(4321);
                var graph = new FakeHttpMessageHandler().Enqueue(202);
                var s3 = new FakeHttpMessageHandler();
                s3.Enqueue(r => StorageTestSupport.Bytes(200, pdf));
                ObjectStorageClient storage = NewS3(s3);
                storage.KeyPrefix = "archive/";
                GraphMailMessage message = TestSupport.NewMessage();
                message.AddAttachmentFromStorage(storage, "2026/10/Rechnung 1001.pdf", "");
                Assert.Equal(1, message.AttachmentCount);
                Assert.Equal(0L, message.TotalAttachmentBytes);
                Assert.Contains("object storage key 2026/10/Rechnung 1001.pdf", message.Describe());

                GraphMailSender sender = NewSender(graph, temp);
                Assert.Equal("", sender.Send(message));
                Assert.Contains("4321 bytes downloaded from object storage key 2026/10/Rechnung 1001.pdf", sender.LastDiagnostics);

                RecordedRequest get = s3.Requests.Single();
                Assert.Equal(HttpMethod.Get, get.Method);
                Assert.Equal("https://s3.example.test/nav-bucket/archive/2026/10/Rechnung%201001.pdf", get.Uri!.AbsoluteUri);
                JsonElement attachment = TestSupport.Json(graph.Requests.Single()).GetProperty("message").GetProperty("attachments")[0];
                Assert.Equal("Rechnung 1001.pdf", attachment.GetProperty("name").GetString());
                Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
                Assert.Equal(Convert.ToBase64String(pdf), attachment.GetProperty("contentBytes").GetString());
                Assert.Empty(Directory.GetFiles(temp.Path));
            }
        }

        [Fact]
        public void Download_failure_fails_the_send_and_leaves_no_temp_file()
        {
            using (var temp = new TempDirectory())
            {
                var graph = new FakeHttpMessageHandler();
                var s3 = new FakeHttpMessageHandler();
                s3.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message></Error>"));
                ObjectStorageClient storage = NewS3(s3);
                GraphMailMessage message = TestSupport.NewMessage();
                message.AddAttachmentFromStorage(storage, "missing.pdf", "Beleg.pdf");

                string error = NewSender(graph, temp).Send(message);
                Assert.StartsWith("Attachment 'Beleg.pdf' could not be downloaded from object storage (key missing.pdf): ", error);
                Assert.Contains("NoSuchKey", error);
                Assert.DoesNotContain("Mail.Send", error);
                Assert.Empty(graph.Requests);
                Assert.Empty(Directory.GetFiles(temp.Path));
                Assert.Contains("NoSuchKey", storage.LastError);
            }
        }

        [Fact]
        public void Temp_file_is_removed_when_graph_fails()
        {
            using (var temp = new TempDirectory())
            {
                var graph = new FakeHttpMessageHandler().Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "r-403"));
                var azure = new FakeHttpMessageHandler();
                azure.Enqueue(r => StorageTestSupport.Bytes(200, TestSupport.Pattern(10)));
                GraphMailMessage message = TestSupport.NewMessage();
                message.AddAttachmentFromStorage(NewAzure(azure), "a/b.txt", "");

                string error = NewSender(graph, temp).Send(message);
                Assert.Contains("ErrorAccessDenied", error);
                Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs/a/b.txt", azure.Requests.Single().Uri!.AbsoluteUri);
                Assert.Empty(Directory.GetFiles(temp.Path));
            }
        }

        [Fact]
        public void Builder_validation_and_name_derivation()
        {
            ObjectStorageClient storage = NewS3(new FakeHttpMessageHandler());
            var message = TestSupport.NewMessage();
            message.AddAttachmentFromStorage(storage, " ", "x.pdf");
            message.AddAttachmentFromStorage(storage, "folder/", "");
            Assert.Equal(0, message.AttachmentCount);
            Assert.Throws<ArgumentNullException>(() => message.AddAttachmentFromStorage(null!, "a", "b"));

            var graph = new FakeHttpMessageHandler();
            using (var temp = new TempDirectory())
            {
                string error = NewSender(graph, temp).Send(message);
                Assert.Contains("Attachment object key is empty for 'x.pdf'", error);
                Assert.Empty(graph.Requests);
            }

            var ok = TestSupport.NewMessage();
            ok.AddAttachmentFromStorage(storage, "a/b/c/Scan_01.PDF", "");
            Assert.Equal("Scan_01.PDF", ok.AttachmentList[0].Name);
            Assert.Equal("application/pdf", ok.AttachmentList[0].ContentType);
        }

        [Fact]
        public void Large_storage_attachment_can_go_out_as_a_link()
        {
            using (var temp = new TempDirectory())
            {
                byte[] data = TestSupport.Pattern(3000);
                var graph = new FakeHttpMessageHandler().Enqueue(202);
                var s3 = new FakeHttpMessageHandler();
                s3.Enqueue(r => StorageTestSupport.Bytes(200, data));
                s3.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e\""));
                ObjectStorageClient storage = NewS3(s3);
                GraphMailSender sender = NewSender(graph, temp);
                sender.UseLinkStorage(storage, 1000, 60, "");
                GraphMailMessage message = TestSupport.NewMessage();
                message.AddAttachmentFromStorage(storage, "archiv/plan.dwg", "");

                Assert.Equal("", sender.Send(message));
                Assert.Equal(HttpMethod.Get, s3.Requests[0].Method);
                Assert.Equal(HttpMethod.Put, s3.Requests[1].Method);
                Assert.Equal(data, s3.Requests[1].Body);
                string body = TestSupport.Json(graph.Requests.Single()).GetProperty("message").GetProperty("body").GetProperty("content").GetString()!;
                Assert.Contains("Download plan.dwg (2.9 KB): https://s3.example.test/nav-bucket/mail-attachments/", body);
                Assert.Empty(Directory.GetFiles(temp.Path));
            }
        }
    }
}
