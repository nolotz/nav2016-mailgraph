using System;
using System.IO;
using NavMailGraph.Graph;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class MessageBuilderTests
    {
        [Fact]
        public void AppendBody_and_Body_setter()
        {
            var message = new GraphMailMessage();
            message.AppendBody("Zeile 1");
            message.AppendBody("\r\nZeile 2");
            Assert.Equal("Zeile 1\r\nZeile 2", message.GetBody());
            message.Body = "neu";
            Assert.Equal("neu", message.Body);
            message.Subject = "Betreff";
            Assert.Equal("Betreff", message.GetSubject());
        }

        [Fact]
        public void Null_strings_become_empty()
        {
            var message = new GraphMailMessage { FromName = null!, FromAddress = null!, Subject = null!, Body = null!, Importance = null! };
            Assert.Equal("", message.FromName);
            Assert.Equal("", message.FromAddress);
            Assert.Equal("", message.Subject);
            Assert.Equal("", message.Body);
            Assert.Equal("", message.Importance);
        }

        [Fact]
        public void AddAttachment_uses_file_name_from_path_when_name_is_empty()
        {
            using (var dir = new TempDirectory())
            {
                string path = dir.File("tmp123.pdf", new byte[] { 1, 2, 3 });
                var message = new GraphMailMessage();
                message.AddAttachment(path, "");
                message.AddAttachment(path, "Rechnung.pdf");
                Assert.Equal(2, message.AttachmentCount);
                Assert.Equal("tmp123.pdf", message.AttachmentList[0].Name);
                Assert.Equal("Rechnung.pdf", message.AttachmentList[1].Name);
                Assert.Equal("application/pdf", message.AttachmentList[1].ContentType);
                Assert.Equal(6, message.TotalAttachmentBytes);
            }
        }

        [Fact]
        public void AddAttachment_with_empty_path_records_validation_error()
        {
            var message = new GraphMailMessage();
            message.AddAttachment("", "x.pdf");
            Assert.Equal(0, message.AttachmentCount);
            Assert.Single(message.ValidationErrors);
        }

        [Fact]
        public void File_attachment_is_read_at_send_time_and_missing_file_counts_zero()
        {
            var message = new GraphMailMessage();
            message.AddAttachment(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".pdf"), "");
            Assert.Equal(1, message.AttachmentCount);
            Assert.Equal(0, message.TotalAttachmentBytes);
        }

        [Fact]
        public void AddAttachmentBytes_requires_name_and_content()
        {
            var message = new GraphMailMessage();
            Assert.Throws<ArgumentNullException>(() => message.AddAttachmentBytes(null!, "x.txt"));
            message.AddAttachmentBytes(new byte[] { 1 }, "  ");
            Assert.Equal(0, message.AttachmentCount);
            Assert.Single(message.ValidationErrors);
            message.AddAttachmentBytes(new byte[] { 1, 2 }, "a.txt");
            Assert.Equal(1, message.AttachmentCount);
            Assert.Equal(2, message.TotalAttachmentBytes);
        }

        [Fact]
        public void AddAttachmentStream_copies_immediately()
        {
            byte[] content = TestSupport.Pattern(1000);
            var source = new MemoryStream(content);
            var message = new GraphMailMessage();
            message.AddAttachmentStream(source, "data.csv");
            source.Dispose();
            Assert.Equal(1000, message.TotalAttachmentBytes);
            Assert.Equal(content, message.AttachmentList[0].ReadAllBytes());
            Assert.Equal("text/csv", message.AttachmentList[0].ContentType);
            Assert.Throws<ArgumentNullException>(() => message.AddAttachmentStream(null!, "x"));
        }

        [Fact]
        public void AddInlineAttachment_sets_inline_and_content_id()
        {
            using (var dir = new TempDirectory())
            {
                string path = dir.File("logo.png", new byte[] { 9 });
                var message = new GraphMailMessage();
                message.AddInlineAttachment(path, "", " logo1 ");
                MailAttachment attachment = message.AttachmentList[0];
                Assert.True(attachment.IsInline);
                Assert.Equal("logo1", attachment.ContentId);
                Assert.Equal("image/png", attachment.ContentType);
            }
        }

        [Theory]
        [InlineData("X-Nav-Document", true)]
        [InlineData("x-custom", true)]
        [InlineData("Subject", false)]
        [InlineData("", false)]
        public void AddHeader_requires_x_prefix(string name, bool accepted)
        {
            var message = new GraphMailMessage();
            message.AddHeader(name, "v");
            Assert.Equal(accepted ? 1 : 0, message.HeaderList.Count);
            Assert.Equal(accepted ? 0 : 1, message.ValidationErrors.Count);
        }

        [Fact]
        public void Describe_contains_summary_but_no_body()
        {
            var message = TestSupport.NewMessage();
            message.Body = "GEHEIMER-INHALT";
            message.AddAttachmentBytes(new byte[10], "a.pdf");
            message.AddCC("cc@example.com");
            string text = message.Describe();
            Assert.Contains("subject=\"Rechnung 1001\"", text);
            Assert.Contains("max@example.com", text);
            Assert.Contains("cc@example.com", text);
            Assert.Contains("a.pdf", text);
            Assert.Contains("attachments=1 (10 bytes)", text);
            Assert.DoesNotContain("GEHEIMER-INHALT", text);
        }

        [Theory]
        [InlineData("Rechnung.pdf", "application/pdf")]
        [InlineData("RECHNUNG.PDF", "application/pdf")]
        [InlineData("a.xml", "application/xml")]
        [InlineData("a.csv", "text/csv")]
        [InlineData("a.txt", "text/plain")]
        [InlineData("a.htm", "text/html")]
        [InlineData("a.html", "text/html")]
        [InlineData("a.doc", "application/msword")]
        [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
        [InlineData("a.xls", "application/vnd.ms-excel")]
        [InlineData("a.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
        [InlineData("a.ppt", "application/vnd.ms-powerpoint")]
        [InlineData("a.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
        [InlineData("a.png", "image/png")]
        [InlineData("a.jpg", "image/jpeg")]
        [InlineData("a.jpeg", "image/jpeg")]
        [InlineData("a.gif", "image/gif")]
        [InlineData("a.zip", "application/zip")]
        [InlineData("a.json", "application/json")]
        [InlineData("a.rtf", "application/rtf")]
        [InlineData("a.msg", "application/vnd.ms-outlook")]
        [InlineData("a.eml", "message/rfc822")]
        [InlineData("a.unknownext", "application/octet-stream")]
        [InlineData("noextension", "application/octet-stream")]
        [InlineData("", "application/octet-stream")]
        public void Content_type_by_extension(string fileName, string expected)
        {
            Assert.Equal(expected, ContentTypes.FromFileName(fileName));
        }
    }
}
