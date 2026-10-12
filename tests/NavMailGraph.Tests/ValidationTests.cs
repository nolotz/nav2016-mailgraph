using System;
using System.Collections.Generic;
using System.IO;
using NavMailGraph.Graph;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class ValidationTests
    {
        private static string SendExpectingNoHttp(GraphMailSender sender, FakeHttpMessageHandler handler, GraphMailMessage message)
        {
            string result = sender.Send(message);
            Assert.Empty(handler.Requests);
            return result;
        }

        [Fact]
        public void Null_message_throws_ArgumentNullException()
        {
            var sender = TestSupport.NewSender(new FakeHttpMessageHandler());
            Assert.Throws<ArgumentNullException>(() => sender.Send(null!));
        }

        [Fact]
        public void No_recipients()
        {
            var handler = new FakeHttpMessageHandler();
            var message = new GraphMailMessage { FromAddress = TestSupport.Mailbox, Subject = "x" };
            message.AddReplyTo("r@example.com");
            Assert.Equal("Validation failed: at least one recipient is required.", SendExpectingNoHttp(TestSupport.NewSender(handler), handler, message));
        }

        [Fact]
        public void No_mailbox()
        {
            var handler = new FakeHttpMessageHandler();
            var message = TestSupport.NewMessage();
            message.FromAddress = "";
            Assert.StartsWith("Validation failed: sender mailbox is required", SendExpectingNoHttp(TestSupport.NewSender(handler), handler, message));
        }

        [Fact]
        public void Missing_attachment_file()
        {
            var handler = new FakeHttpMessageHandler();
            string path = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".pdf");
            var message = TestSupport.NewMessage();
            message.AddAttachment(path, "Rechnung.pdf");
            Assert.Equal("Attachment 'Rechnung.pdf' not found: " + path, SendExpectingNoHttp(TestSupport.NewSender(handler), handler, message));
        }

        [Fact]
        public void Oversize_attachment()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.MaxAttachmentBytes = 10;
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[11], "big.bin");
            Assert.Equal("Attachment 'big.bin' is too large: 11 bytes (limit 10 bytes).", SendExpectingNoHttp(sender, handler, message));
        }

        [Fact]
        public void Several_problems_report_first_plus_count_and_all_in_diagnostics()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddRecipients("bad-one; bad-two");
            message.AddHeader("Subject", "x");
            string result = SendExpectingNoHttp(sender, handler, message);
            Assert.StartsWith("Validation failed: Invalid recipient 'bad-one'", result);
            Assert.Contains("(+2 more problem(s), see LastDiagnostics)", result);
            Assert.Contains("bad-two", sender.LastDiagnostics);
            Assert.Contains("Custom header 'Subject'", sender.LastDiagnostics);
        }

        [Fact]
        public void Invalid_importance()
        {
            var handler = new FakeHttpMessageHandler();
            var message = TestSupport.NewMessage();
            message.Importance = "urgent";
            Assert.StartsWith("Validation failed: Importance must be", SendExpectingNoHttp(TestSupport.NewSender(handler), handler, message));
        }

        [Theory]
        [InlineData("common", true)]
        [InlineData("Organizations", true)]
        [InlineData(" consumers ", true)]
        [InlineData("contoso.onmicrosoft.com", false)]
        [InlineData("1b2c3d4e-0000-1111-2222-333344445555", false)]
        [InlineData("", false)]
        public void IsMultiTenantAlias(string tenant, bool expected)
        {
            Assert.Equal(expected, GraphMailSender.IsMultiTenantAlias(tenant));
        }

        [Fact]
        public void Common_tenant_is_rejected_before_any_call()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = GraphMailSender.CreateForTests("common", TestSupport.ClientId, handler);
            sender.UseClientSecret("secret");
            Assert.StartsWith("Validation failed: Tenant ID 'common' cannot be used with client credentials", sender.Send(TestSupport.NewMessage()));
            Assert.StartsWith("Validation failed: Tenant ID 'common'", sender.TestConnection());
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Missing_configuration_errors()
        {
            var handler = new FakeHttpMessageHandler();
            Assert.StartsWith("Validation failed: no credential configured", GraphMailSender.CreateForTests("t", "c", handler).TestConnection());
            Assert.Equal("Validation failed: Tenant ID is required.", GraphMailSender.CreateForTests("", "c", handler).TestConnection());
            Assert.Equal("Validation failed: Client ID is required.", GraphMailSender.CreateForTests("t", " ", handler).TestConnection());
            GraphMailSender emptySecret = GraphMailSender.CreateForTests("t", "c", handler);
            emptySecret.UseClientSecret("");
            Assert.Equal("Validation failed: the client secret is empty.", emptySecret.TestConnection());
            GraphMailSender unknownLib = GraphMailSender.CreateForTests("t", "c", handler);
            unknownLib.UseClientSecret("s");
            unknownLib.TokenLibrary = "Adal";
            Assert.StartsWith("Validation failed: unknown TokenLibrary 'Adal'", unknownLib.TestConnection());
            GraphMailSender unknownCloud = GraphMailSender.CreateForTests("t", "c", handler);
            unknownCloud.UseClientSecret("s");
            unknownCloud.Cloud = "Mars";
            Assert.StartsWith("Validation failed: unknown Cloud 'Mars'", unknownCloud.TestConnection());
            GraphMailSender missingPfx = GraphMailSender.CreateForTests("t", "c", handler);
            missingPfx.UseCertificateFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".pfx"), "pw");
            Assert.StartsWith("Certificate file not found: ", missingPfx.TestConnection());
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Unreadable_attachment_during_send_is_reported_as_cannot_be_read()
        {
            string path = Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N") + ".pdf");
            var attachment = new MailAttachment("x.pdf", "application/pdf", path, null, false, "");
            var ex = Assert.Throws<GraphMailException>(() => GraphMailSender.BuildFileAttachment(attachment));
            Assert.StartsWith("Attachment 'x.pdf' cannot be read: " + path + " (", ex.Message);
            Assert.Equal(GraphMailException.ValidationCode, ex.ErrorCode);
        }

        [Fact]
        public void EstimatePayloadBytes_formula()
        {
            var message = new GraphMailMessage { Body = "abc", Subject = "é" };
            // 3 + 2 + 2048 + (ceil(3/3)*4 + 512) + (ceil(4/3)*4 + 512) + (0 + 512)
            Assert.Equal(3 + 2 + 2048 + 516 + 520 + 512, GraphMailSender.EstimatePayloadBytes(message, new List<long> { 3, 4, 0 }));
        }

        [Fact]
        public void ValidateSizes_decisions()
        {
            var small = new MailAttachment("s.pdf", "application/pdf", null, new byte[1], false, "");
            var big = new MailAttachment("b.pdf", "application/pdf", null, new byte[1], false, "");
            var attachments = new List<MailAttachment> { small, big };
            var sizes = new List<long> { 100, 3145728 };

            Assert.True(GraphMailSender.ValidateSizes(attachments, sizes, 1000, 1000, false, 3145728, false, "USGov"));
            Assert.False(GraphMailSender.ValidateSizes(attachments, sizes, 1001, 1000, true, 3145728, true, "Global"));
            var noDraft = Assert.Throws<GraphMailException>(() => GraphMailSender.ValidateSizes(attachments, sizes, 1001, 1000, false, 3145728, true, "Global"));
            Assert.Contains("UseDraftForLargeMessages is off", noDraft.Message);
            var usGov = Assert.Throws<GraphMailException>(() => GraphMailSender.ValidateSizes(attachments, sizes, 1001, 1000, true, 3145728, false, "USGov"));
            Assert.Contains("attachment 'b.pdf'", usGov.Message);
            Assert.False(GraphMailSender.ValidateSizes(attachments, new List<long> { 100, 3145727 }, 1001, 1000, true, 3145728, false, "USGov"));
        }

        [Fact]
        public void FromDiffersFromMailbox_rules()
        {
            var message = new GraphMailMessage();
            Assert.False(GraphMailSender.FromDiffersFromMailbox(message, "a@x.de"));
            message.FromAddress = "A@X.de";
            Assert.False(GraphMailSender.FromDiffersFromMailbox(message, "a@x.de"));
            message.FromAddress = "b@x.de";
            Assert.True(GraphMailSender.FromDiffersFromMailbox(message, "a@x.de"));
        }

        [Fact]
        public void BuildGraphMessage_never_sets_from()
        {
            var message = TestSupport.NewMessage();
            message.FromAddress = "other@contoso.com";
            GraphMessage built = GraphMailSender.BuildGraphMessage(message, TestSupport.Mailbox, true, null);
            Assert.Null(built.From);
            Assert.Null(built.Attachments);
            Assert.Equal("other@contoso.com", Assert.Single(built.ReplyTo!).EmailAddress!.Address);
            Assert.Null(GraphMailSender.BuildGraphMessage(message, TestSupport.Mailbox, false, null).ReplyTo);
        }
    }
}
