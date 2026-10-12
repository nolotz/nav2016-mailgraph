using System.IO;
using NavMailGraph.Reading;
using NavMailGraph.Tests.Infrastructure;
using Xunit;
using static NavMailGraph.Tests.Reading.ReadingTestSupport;

namespace NavMailGraph.Tests.Reading
{
    public class AttachmentTests
    {
        private const string AttachmentsList =
            "{\"value\":["
            + "{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"@odata.mediaContentType\":\"application/pdf\",\"id\":\"att1\",\"name\":\"Rechnung 4711.pdf\",\"contentType\":\"application/pdf\",\"size\":417351,\"isInline\":false,\"lastModifiedDateTime\":\"2026-10-12T08:00:00Z\"},"
            + "{\"@odata.type\":\"#microsoft.graph.itemAttachment\",\"id\":\"att2\",\"name\":\"Re: Angebot\",\"contentType\":null,\"size\":32005,\"isInline\":false},"
            + "{\"@odata.type\":\"#microsoft.graph.referenceAttachment\",\"id\":\"att3\",\"name\":\"Plan.xlsx\",\"contentType\":\"application/vnd.ms-excel\",\"size\":0,\"isInline\":false}"
            + "],\"@odata.nextLink\":\"" + TestSupport.MailboxUrl + "/messages/m1/attachments?%24skip=3\"}";

        private const string AttachmentsPage2 =
            "{\"value\":[{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"att4\",\"name\":\"logo.png\",\"contentType\":\"image/png\",\"size\":1234,\"isInline\":true}]}";

        [Fact]
        public void Metadata_is_loaded_lazily_with_select_and_kinds()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null, Message("m1", "2026-10-12T08:00:00Z", hasAttachments: true)))
                .Enqueue(200, AttachmentsList)
                .Enqueue(200, AttachmentsPage2);
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.Fetch());
            GraphMailItem item = mailbox.GetMessage(0);
            Assert.Single(handler.Requests);

            Assert.Equal(4, item.AttachmentCount);
            Assert.Equal(3, handler.Requests.Count);
            RecordedRequest metadata = handler.Requests[1];
            Assert.Equal(TestSupport.MailboxUrl + "/messages/m1/attachments", PathOf(metadata));
            Assert.Equal("id,name,contentType,size,isInline,lastModifiedDateTime", Param(metadata, "$select"));
            Assert.Contains("IdType=\"ImmutableId\"", metadata.Header("Prefer"));
            Assert.Equal(TestSupport.MailboxUrl + "/messages/m1/attachments?%24skip=3", handler.Requests[2].Url);

            GraphMailAttachmentInfo pdf = item.GetAttachment(0);
            Assert.Equal("att1", pdf.Id);
            Assert.Equal("Rechnung 4711.pdf", pdf.Name);
            Assert.Equal("application/pdf", pdf.ContentType);
            Assert.Equal(417351L, pdf.Size);
            Assert.False(pdf.IsInline);
            Assert.Equal("file", pdf.Kind);
            Assert.Equal("Rechnung 4711.pdf", pdf.SuggestedFileName);
            Assert.Equal("2026-10-12T08:00:00Z", pdf.LastModifiedText);

            GraphMailAttachmentInfo mail = item.GetAttachment(1);
            Assert.Equal("item", mail.Kind);
            Assert.Equal("", mail.ContentType);
            Assert.Equal("Re_ Angebot.eml", mail.SuggestedFileName);

            Assert.Equal("reference", item.GetAttachment(2).Kind);
            Assert.True(item.GetAttachment(3).IsInline);

            // loaded once
            Assert.Equal(4, item.AttachmentCount);
            Assert.Equal("", item.LoadAttachments());
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public void No_call_without_attachments_but_cid_body_triggers_load()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null,
                    Message("plain", "2026-10-12T08:00:00Z"),
                    Message("inline", "2026-10-12T08:01:00Z", body: "<img src=\\\"cid:logo@01\\\">")))
                .Enqueue(200, AttachmentsPage2);
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.Fetch());

            Assert.Equal(0, mailbox.GetMessage(0).AttachmentCount);
            Assert.Single(handler.Requests);
            Assert.Equal(1, mailbox.GetMessage(1).AttachmentCount);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/inline/attachments", PathOf(handler.Requests[1]));
        }

        [Fact]
        public void Without_body_metadata_is_always_loaded_to_find_inline_only_attachments()
        {
            string noBody = "{\"id\":\"inline\",\"receivedDateTime\":\"2026-10-12T08:00:00Z\",\"hasAttachments\":false}";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null, noBody))
                .Enqueue(200, AttachmentsPage2);
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.IncludeBody = false;
            Assert.Equal("", mailbox.Fetch());

            GraphMailItem item = mailbox.GetMessage(0);
            Assert.Equal("", item.LoadAttachments());
            Assert.Equal(1, item.AttachmentCount);
            Assert.True(item.GetAttachment(0).IsInline);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/inline/attachments", PathOf(handler.Requests[1]));
        }

        [Fact]
        public void Body_converted_to_text_cannot_show_cid_so_metadata_is_loaded()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null, Message("t1", "2026-10-12T08:00:00Z", body: "Hallo")))
                .Enqueue(200, "{\"value\":[]}");
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.BodyAsText = true;
            Assert.Equal("", mailbox.Fetch());

            Assert.Equal(0, mailbox.GetMessage(0).AttachmentCount);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void Failed_metadata_load_is_reported_by_LoadAttachments_and_negative_count()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null, Message("m1", "2026-10-12T08:00:00Z", hasAttachments: true)))
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.Fetch());
            GraphMailItem item = mailbox.GetMessage(0);

            // C/AL contract: call LoadAttachments first and stop on a non-empty result.
            string error = item.LoadAttachments();
            Assert.NotEqual("", error);
            Assert.Equal(error, item.AttachmentError);
            Assert.True(item.AttachmentCount < 0);
        }

        [Fact]
        public void ContentBytes_in_metadata_response_is_rejected()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null, Message("m1", "2026-10-12T08:00:00Z", hasAttachments: true)))
                .Enqueue(200, "{\"value\":[{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"a\",\"name\":\"x.pdf\",\"contentBytes\":\"JVBERi0xLjQK\"}]}");
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.Fetch());
            GraphMailItem item = mailbox.GetMessage(0);

            Assert.Equal(-1, item.AttachmentCount);
            Assert.Contains("contentBytes", item.AttachmentError);
            Assert.StartsWith("Microsoft Graph list attachments returned attachment content (contentBytes)", item.AttachmentError);
            Assert.DoesNotContain("JVBERi0xLjQK", item.AttachmentError);
            Assert.DoesNotContain("JVBERi0xLjQK", mailbox.LastDiagnostics);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => item.GetAttachment(0));
        }

        [Fact]
        public void Value_is_streamed_to_a_write_only_non_seekable_stream()
        {
            byte[] content = TestSupport.Pattern(300000);
            var handler = new FakeHttpMessageHandler().Enqueue(_ => Binary(content));
            GraphMailbox mailbox = NewMailbox(handler);
            var target = new WriteOnlyStream();

            Assert.Equal("", mailbox.SaveAttachmentToStream("m1", "att/1=", target));

            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal("GET", request.Method.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/m1/attachments/att%2F1=/$value", request.Url);
            Assert.Contains("IdType=\"ImmutableId\"", request.Header("Prefer"));
            Assert.Equal(content, target.Content);
            Assert.True(target.WriteCalls >= 4);
            Assert.Equal(1, target.FlushCount);
            Assert.Equal(300000L, mailbox.LastDownloadSize);
        }

        [Fact]
        public void Value_download_retries_throttling_before_body()
        {
            byte[] content = TestSupport.Pattern(1000);
            var handler = new FakeHttpMessageHandler()
                .Enqueue(503, null, "r1", System.TimeSpan.FromSeconds(1))
                .Enqueue(_ => Binary(content));
            GraphMailbox mailbox = NewMailbox(handler);
            var target = new WriteOnlyStream();

            Assert.Equal("", mailbox.SaveAttachmentToStream("m1", "a1", target));
            Assert.Equal(content, target.Content);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void Write_failure_on_target_is_reported()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(_ => Binary(TestSupport.Pattern(10)));
            GraphMailbox mailbox = NewMailbox(handler);
            var target = new FailingStream();

            string error = mailbox.SaveAttachmentToStream("m1", "a1", target);
            Assert.StartsWith("Writing attachment a1 to the target failed after 0 bytes: IOException: disk full", error);
        }

        [Fact]
        public void Null_or_readonly_target_is_validation_error()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("Validation failed: the target stream is required.", mailbox.SaveAttachmentToStream("m", "a", null!));
            Assert.Equal("Validation failed: the target stream is not writable.", mailbox.SaveAttachmentToStream("m", "a", new MemoryStream(new byte[1], false)));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Save_to_file_replaces_existing_file_and_leaves_no_partial()
        {
            byte[] content = TestSupport.Pattern(200000, 3);
            var handler = new FakeHttpMessageHandler().Enqueue(_ => Binary(content));
            GraphMailbox mailbox = NewMailbox(handler);
            using (var dir = new TempDirectory())
            {
                string path = Path.Combine(dir.Path, "sub", "Rechnung.pdf");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "old");

                Assert.Equal("", mailbox.SaveAttachmentToFile("m1", "a1", path));
                Assert.Equal(content, File.ReadAllBytes(path));
                Assert.False(File.Exists(path + ".nmgpart"));
                Assert.Equal(200000L, mailbox.LastDownloadSize);
            }
        }

        [Fact]
        public void Failed_file_download_keeps_no_partial_file()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(404, TestSupport.GraphError("ErrorItemNotFound", "Not found.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);
            using (var dir = new TempDirectory())
            {
                string path = Path.Combine(dir.Path, "x.pdf");
                string error = mailbox.SaveAttachmentToFile("m1", "a1", path);
                Assert.Contains("download attachment failed (HTTP 404", error);
                Assert.Contains("Hint: ", error);
                Assert.False(File.Exists(path));
                Assert.False(File.Exists(path + ".nmgpart"));
            }
        }

        [Fact]
        public void Mime_is_saved_from_message_value()
        {
            byte[] mime = System.Text.Encoding.ASCII.GetBytes("From: a@b.de\r\nSubject: x\r\n\r\nHallo\r\n");
            var handler = new FakeHttpMessageHandler().Enqueue(_ => Binary(mime, "message/rfc822"));
            GraphMailbox mailbox = NewMailbox(handler);
            using (var dir = new TempDirectory())
            {
                string path = Path.Combine(dir.Path, "mail.eml");
                Assert.Equal("", mailbox.SaveMimeToFile("m1", path));
                Assert.Equal(TestSupport.MailboxUrl + "/messages/m1/$value", handler.Requests[0].Url);
                Assert.Equal(mime, File.ReadAllBytes(path));
            }
        }

        [Fact]
        public void Reference_attachment_405_has_clear_message()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(405, TestSupport.GraphError("ErrorInvalidRequest", "Method not allowed.", "r405"), "r405");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.SaveAttachmentToStream("m1", "att3", new WriteOnlyStream());
            Assert.StartsWith("Attachment att3 cannot be downloaded: reference attachments (links to cloud files) have no content in Microsoft Graph (HTTP 405)", error);
            Assert.Contains("request-id r405", error);
            Assert.Single(handler.Requests);
        }

        [Theory]
        [InlineData("Rechnung 4711.pdf", "file", "Rechnung 4711.pdf")]
        [InlineData("a<b>c:d\"e/f\\g|h?i*.pdf", "file", "a_b_c_d_e_f_g_h_i_.pdf")]
        [InlineData("CON.txt", "file", "_CON.txt")]
        [InlineData("trailing. ", "file", "trailing")]
        [InlineData("", "file", "attachment")]
        [InlineData("???", "file", "attachment")]
        [InlineData("Weitergeleitet", "item", "Weitergeleitet.eml")]
        [InlineData("mail.eml", "item", "mail.eml")]
        public void Suggested_file_names(string name, string kind, string expected)
        {
            Assert.Equal(expected, MailQueries.SuggestFileName(name, kind));
        }

        [Fact]
        public void Long_names_keep_extension()
        {
            string result = MailQueries.SuggestFileName(new string('x', 300) + ".pdf", "file");
            Assert.Equal(200, result.Length);
            Assert.EndsWith(".pdf", result);
        }

        [Theory]
        [InlineData("#microsoft.graph.fileAttachment", "file")]
        [InlineData("#microsoft.graph.itemAttachment", "item")]
        [InlineData("#microsoft.graph.referenceAttachment", "reference")]
        [InlineData(null, "file")]
        public void Kinds(string? odataType, string expected)
        {
            Assert.Equal(expected, MailQueries.KindFromODataType(odataType));
        }

        private sealed class FailingStream : Stream
        {
            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new System.NotSupportedException();

            public override long Position { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();

            public override void SetLength(long value) => throw new System.NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
        }
    }
}
