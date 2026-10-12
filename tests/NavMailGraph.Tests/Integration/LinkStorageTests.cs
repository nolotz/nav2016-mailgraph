using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Storage;
using Xunit;
using static NavMailGraph.Tests.Integration.IntegrationTestSupport;

namespace NavMailGraph.Tests.Integration
{
    public class LinkStorageTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

        private static GraphMailSender NewSender(FakeHttpMessageHandler graph, ObjectStorageClient storage, long threshold, int minutes, string text)
        {
            GraphMailSender sender = TestSupport.NewSender(graph);
            sender.LinkClockOverride = () => Now;
            sender.UseLinkStorage(storage, threshold, minutes, text);
            return sender;
        }

        private static JsonElement SentMessage(FakeHttpMessageHandler graph)
        {
            RecordedRequest send = graph.Requests.Single(r => r.Url.EndsWith("/sendMail", StringComparison.Ordinal));
            return TestSupport.Json(send).GetProperty("message");
        }

        [Fact]
        public void Large_attachment_becomes_a_link_line_in_a_text_body()
        {
            var graph = new FakeHttpMessageHandler().Enqueue(202);
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e1\""));
            ObjectStorageClient storage = NewS3(s3);
            storage.KeyPrefix = "nav/";
            GraphMailSender sender = NewSender(graph, storage, 2048, 7 * 24 * 60, "");
            GraphMailMessage message = TestSupport.NewMessage();
            message.Body = "Sehr geehrte Damen und Herren,";
            byte[] big = TestSupport.Pattern(3000);
            message.AddAttachmentBytes(big, "Katalog 2026.pdf");
            message.AddAttachmentBytes(TestSupport.Pattern(100), "small.txt");

            Assert.Equal("", sender.Send(message));

            RecordedRequest put = s3.Requests.Single();
            Assert.Equal(HttpMethod.Put, put.Method);
            Match key = Regex.Match(Uri.UnescapeDataString(put.Uri!.AbsolutePath), "^/nav-bucket/nav/mail-attachments/2026/10/([0-9a-f]{32})/Katalog 2026.pdf$");
            Assert.True(key.Success, put.Uri.AbsolutePath);
            Assert.Equal(big, put.Body);
            Assert.Equal("application/pdf", put.Header("Content-Type"));

            JsonElement msg = SentMessage(graph);
            Assert.Equal("Text", msg.GetProperty("body").GetProperty("contentType").GetString());
            string body = msg.GetProperty("body").GetProperty("content").GetString()!;
            Assert.StartsWith("Sehr geehrte Damen und Herren,\r\n\r\nDownload Katalog 2026.pdf (2.9 KB): https://s3.example.test/nav-bucket/nav/mail-attachments/2026/10/" + key.Groups[1].Value + "/Katalog%202026.pdf?", body);
            Assert.Contains("X-Amz-Expires=604800", body);
            Assert.Contains("X-Amz-Signature=", body);
            JsonElement attachment = msg.GetProperty("attachments").EnumerateArray().Single();
            Assert.Equal("small.txt", attachment.GetProperty("name").GetString());

            // the caller's message is unchanged, the diagnostics hold no signed URL
            Assert.Equal("Sehr geehrte Damen und Herren,", message.Body);
            Assert.Equal(2, message.AttachmentCount);
            Assert.DoesNotContain("X-Amz-Signature", sender.LastDiagnostics);
            Assert.Contains("replaced by a download link", sender.LastDiagnostics);
        }

        [Fact]
        public void Html_body_gets_an_escaped_anchor_before_the_closing_body_tag()
        {
            var graph = new FakeHttpMessageHandler().Enqueue(202);
            var azure = new FakeHttpMessageHandler();
            azure.Enqueue(r => StorageTestSupport.Empty(201, "ETag: \"0x1\""));
            ObjectStorageClient storage = NewAzure(azure);
            GraphMailSender sender = NewSender(graph, storage, 1000, 60, "<{0}> {1} bis {2}");
            GraphMailMessage message = TestSupport.NewMessage();
            message.HtmlFormatted = true;
            message.Body = "<html><body><p>Hallo</p></body></html>";
            message.AddAttachmentBytes(TestSupport.Pattern(5 * 1024 * 1024), "R&D.zip");

            Assert.Equal("", sender.Send(message));

            string body = SentMessage(graph).GetProperty("body").GetProperty("content").GetString()!;
            Match link = Regex.Match(body, "^<html><body><p>Hallo</p><p><a href=\"(http://127\\.0\\.0\\.1:10000/devstoreaccount1/docs/mail-attachments/2026/10/[0-9a-f]{32}/R%26D\\.zip\\?[^\"]+)\">&lt;R&amp;D\\.zip&gt; 5\\.0 MB bis 2026-10-12 09:00 UTC</a></p></body></html>$");
            Assert.True(link.Success, body);
            string href = link.Groups[1].Value.Replace("&amp;", "&");
            Assert.Contains("sp=r", href);
            Assert.Contains("sr=b", href);
            Assert.Contains("sig=", href);
            Assert.False(SentMessage(graph).TryGetProperty("attachments", out _));
        }

        [Fact]
        public void Mode_without_links_fails_the_send_before_uploading()
        {
            var graph = new FakeHttpMessageHandler();
            var azure = new FakeHttpMessageHandler();
            ObjectStorageClient storage = StorageTestSupport.NewClient(azure, null);
            storage.UseAzureBlobEntra(NewConnection(new FakeHttpMessageHandler()), "acct", "docs");
            GraphMailSender sender = NewSender(graph, storage, 10, 60, "");
            GraphMailMessage message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");

            string error = sender.Send(message);
            Assert.StartsWith("Attachment 'a.pdf' (100 bytes) could not be sent as a download link (UseLinkStorage): ", error);
            Assert.Contains("not available in Entra ID mode", error);
            Assert.Empty(azure.Requests);
            Assert.Empty(graph.Requests);
            Assert.Contains("not available in Entra ID mode", storage.LastError);
        }

        [Fact]
        public void Upload_failure_fails_the_send()
        {
            var graph = new FakeHttpMessageHandler();
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchBucket</Code><Message>The specified bucket does not exist</Message></Error>"));
            GraphMailSender sender = NewSender(graph, NewS3(s3), 10, 60, "");
            GraphMailMessage message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");

            string error = sender.Send(message);
            Assert.Contains("could not be sent as a download link", error);
            Assert.Contains("NoSuchBucket", error);
            Assert.Empty(graph.Requests);
        }

        [Fact]
        public void Failed_send_removes_the_uploaded_objects()
        {
            var graph = new FakeHttpMessageHandler().Enqueue(400, TestSupport.GraphError("ErrorInvalidRecipients", "bad", "r1"));
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e1\""));
            s3.Enqueue(r => StorageTestSupport.Empty(204));
            GraphMailSender sender = NewSender(graph, NewS3(s3), 10, 60, "");
            GraphMailMessage message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");

            Assert.Contains("ErrorInvalidRecipients", sender.Send(message));
            Assert.Equal(2, s3.Requests.Count);
            Assert.Equal(HttpMethod.Delete, s3.Requests[1].Method);
            Assert.Equal(s3.Requests[0].Uri!.AbsolutePath, s3.Requests[1].Uri!.AbsolutePath);
        }

        [Fact]
        public void Second_upload_failure_removes_the_first_upload()
        {
            var graph = new FakeHttpMessageHandler();
            var s3 = new FakeHttpMessageHandler();
            s3.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e1\""));
            s3.Enqueue(r => StorageTestSupport.Xml(403, "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>"));
            s3.Enqueue(r => StorageTestSupport.Empty(204));
            GraphMailSender sender = NewSender(graph, NewS3(s3), 10, 60, "");
            GraphMailMessage message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");
            message.AddAttachmentBytes(TestSupport.Pattern(100), "b.pdf");

            string error = sender.Send(message);
            Assert.StartsWith("Attachment 'b.pdf' (100 bytes) could not be sent as a download link", error);
            Assert.Equal(3, s3.Requests.Count);
            Assert.Equal(HttpMethod.Delete, s3.Requests[2].Method);
            Assert.Equal(s3.Requests[0].Uri!.AbsolutePath, s3.Requests[2].Uri!.AbsolutePath);
            Assert.Empty(graph.Requests);
        }

        [Fact]
        public void Inline_and_small_attachments_stay_and_clear_turns_links_off()
        {
            using (var temp = new TempDirectory())
            {
                var graph = new FakeHttpMessageHandler().Enqueue(202).Enqueue(202);
                var s3 = new FakeHttpMessageHandler();
                GraphMailSender sender = NewSender(graph, NewS3(s3), 10, 60, "");
                GraphMailMessage message = TestSupport.NewMessage();
                message.HtmlFormatted = true;
                message.Body = "<img src=\"cid:logo\">";
                message.AddInlineAttachment(temp.File("logo.png", TestSupport.Pattern(500)), "logo.png", "logo");
                message.AddAttachmentBytes(TestSupport.Pattern(5), "tiny.txt");
                Assert.Equal("", sender.Send(message));
                Assert.Empty(s3.Requests);
                Assert.Equal(2, SentMessage(graph).GetProperty("attachments").GetArrayLength());

                sender.ClearLinkStorage();
                GraphMailMessage second = TestSupport.NewMessage();
                second.AddAttachmentBytes(TestSupport.Pattern(5000), "big.bin");
                Assert.Equal("", sender.Send(second));
                Assert.Empty(s3.Requests);
            }
        }

        [Fact]
        public void Invalid_link_settings_are_validation_errors()
        {
            var graph = new FakeHttpMessageHandler();
            var s3 = new FakeHttpMessageHandler();
            GraphMailMessage message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");

            Assert.Contains("not a valid format text", NewSender(graph, NewS3(s3), 10, 60, "Download {3}").Send(message));
            Assert.Contains("threshold must be greater than 0", NewSender(graph, NewS3(s3), 0, 60, "").Send(message));
            Assert.Contains("validity must be greater than 0", NewSender(graph, NewS3(s3), 10, 0, "").Send(message));
            Assert.Empty(s3.Requests);
            Assert.Empty(graph.Requests);
            Assert.Throws<ArgumentNullException>(() => TestSupport.NewSender(graph).UseLinkStorage(null!, 1, 1, ""));

            GraphMailMessage noRecipient = new GraphMailMessage { FromAddress = TestSupport.Mailbox, Subject = "x" };
            noRecipient.AddAttachmentBytes(TestSupport.Pattern(100), "a.pdf");
            Assert.Contains("at least one recipient", NewSender(graph, NewS3(s3), 10, 60, "").Send(noRecipient));
            Assert.Empty(s3.Requests);
        }

        [Fact]
        public void Helpers_format_sizes_keys_and_links()
        {
            Assert.Equal("512 bytes", GraphMailSender.FormatSize(512));
            Assert.Equal("1.5 KB", GraphMailSender.FormatSize(1536));
            Assert.Equal("20.0 MB", GraphMailSender.FormatSize(20L * 1024 * 1024));
            Assert.Equal("1.5 GB", GraphMailSender.FormatSize(1536L * 1024 * 1024));

            Assert.Equal("a_b_c.pdf", GraphMailSender.SafeKeySegment("a/b\\c.pdf"));
            Assert.Equal("_", GraphMailSender.SafeKeySegment(".."));
            Assert.Equal("_", GraphMailSender.SafeKeySegment(" "));
            Assert.Equal("x_y", GraphMailSender.SafeKeySegment("x\ty"));
            Assert.Equal("😀.txt", GraphMailSender.SafeKeySegment("😀.txt"));
            Assert.Equal("_.txt", GraphMailSender.SafeKeySegment("\ud800.txt"));

            var links = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("A", "https://x/a?b=1&c=2") };
            Assert.Equal("A: https://x/a?b=1&c=2", GraphMailSender.AppendLinks("", false, links));
            Assert.Equal("Hi\r\n\r\nA: https://x/a?b=1&c=2", GraphMailSender.AppendLinks("Hi", false, links));
            Assert.Equal("Hi\r\n\r\nA: https://x/a?b=1&c=2", GraphMailSender.AppendLinks("Hi\r\n", false, links));
            Assert.Equal("<p>x</p><p><a href=\"https://x/a?b=1&amp;c=2\">A</a></p>", GraphMailSender.AppendLinks("<p>x</p>", true, links));
            Assert.Equal("<BODY>x<p><a href=\"https://x/a?b=1&amp;c=2\">A</a></p></BODY>", GraphMailSender.AppendLinks("<BODY>x</BODY>", true, links));
        }
    }
}
