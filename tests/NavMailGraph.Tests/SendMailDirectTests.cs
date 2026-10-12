using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class SendMailDirectTests
    {
        private static JsonElement SendAndCapture(GraphMailMessage message, Action<GraphMailSender>? configure, out RecordedRequest request)
        {
            var handler = new FakeHttpMessageHandler().Enqueue(202, null, "req-1");
            GraphMailSender sender = TestSupport.NewSender(handler);
            if (configure != null)
            {
                configure(sender);
            }

            string result = sender.Send(message);
            Assert.Equal("", result);
            request = Assert.Single(handler.Requests);
            return TestSupport.Json(request);
        }

        [Fact]
        public void Posts_sendMail_with_expected_json_shape()
        {
            byte[] pdf = TestSupport.Pattern(1234);
            var message = TestSupport.NewMessage();
            message.Subject = "Rechnung 1001 – äöü";
            message.Body = "<p>Hallo</p>";
            message.HtmlFormatted = true;
            message.AddRecipients("Erika <erika@example.com>");
            message.AddCC("cc@example.com");
            message.AddBCC("bcc@example.com");
            message.AddReplyTo("Support <support@contoso.com>");
            message.AddAttachmentBytes(pdf, "Rechnung.pdf");

            RecordedRequest request;
            JsonElement json = SendAndCapture(message, null, out request);

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/sendMail", request.Url);
            Assert.StartsWith("application/json", request.Header("Content-Type"));

            Assert.True(json.GetProperty("saveToSentItems").GetBoolean());
            JsonElement msg = json.GetProperty("message");
            Assert.Equal("Rechnung 1001 – äöü", msg.GetProperty("subject").GetString());
            Assert.Equal("HTML", msg.GetProperty("body").GetProperty("contentType").GetString());
            Assert.Equal("<p>Hallo</p>", msg.GetProperty("body").GetProperty("content").GetString());
            Assert.Equal(new[] { "max@example.com", "erika@example.com" }, TestSupport.Addresses(msg.GetProperty("toRecipients")));
            Assert.Equal("Erika", msg.GetProperty("toRecipients")[1].GetProperty("emailAddress").GetProperty("name").GetString());
            Assert.False(TestSupport.Has(msg.GetProperty("toRecipients")[0].GetProperty("emailAddress"), "name"));
            Assert.Equal(new[] { "cc@example.com" }, TestSupport.Addresses(msg.GetProperty("ccRecipients")));
            Assert.Equal(new[] { "bcc@example.com" }, TestSupport.Addresses(msg.GetProperty("bccRecipients")));
            Assert.Equal(new[] { "support@contoso.com" }, TestSupport.Addresses(msg.GetProperty("replyTo")));
            Assert.False(TestSupport.Has(msg, "from"));
            Assert.False(TestSupport.Has(msg, "importance"));

            JsonElement attachment = Assert.Single(msg.GetProperty("attachments").EnumerateArray());
            Assert.Equal("#microsoft.graph.fileAttachment", attachment.GetProperty("@odata.type").GetString());
            Assert.Equal("Rechnung.pdf", attachment.GetProperty("name").GetString());
            Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
            Assert.Equal(pdf, Convert.FromBase64String(attachment.GetProperty("contentBytes").GetString()!));
            Assert.False(TestSupport.Has(attachment, "isInline"));
        }

        [Fact]
        public void Odata_type_key_is_literal_in_raw_json()
        {
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[] { 1, 2, 3 }, "a.bin");
            RecordedRequest request;
            SendAndCapture(message, null, out request);
            Assert.Contains("\"@odata.type\":", request.BodyText);
        }

        [Fact]
        public void Text_body_importance_headers_and_save_to_sent_items_false()
        {
            var message = TestSupport.NewMessage();
            message.Importance = "HIGH";
            message.AddHeader("X-Nav-Document", "SI-1001");
            RecordedRequest request;
            JsonElement json = SendAndCapture(message, s => s.SaveToSentItems = false, out request);
            Assert.False(json.GetProperty("saveToSentItems").GetBoolean());
            JsonElement msg = json.GetProperty("message");
            Assert.Equal("Text", msg.GetProperty("body").GetProperty("contentType").GetString());
            Assert.Equal("high", msg.GetProperty("importance").GetString());
            JsonElement header = Assert.Single(msg.GetProperty("internetMessageHeaders").EnumerateArray());
            Assert.Equal("X-Nav-Document", header.GetProperty("name").GetString());
            Assert.Equal("SI-1001", header.GetProperty("value").GetString());
            Assert.False(TestSupport.Has(msg, "attachments"));
            Assert.False(TestSupport.Has(msg, "ccRecipients"));
        }

        [Fact]
        public void Inline_attachment_carries_isInline_and_contentId()
        {
            using (var dir = new TempDirectory())
            {
                var message = TestSupport.NewMessage();
                message.HtmlFormatted = true;
                message.Body = "<img src=\"cid:logo\">";
                message.AddInlineAttachment(dir.File("logo.png", new byte[] { 1, 2 }), "", "logo");
                RecordedRequest request;
                JsonElement attachment = SendAndCapture(message, null, out request).GetProperty("message").GetProperty("attachments")[0];
                Assert.True(attachment.GetProperty("isInline").GetBoolean());
                Assert.Equal("logo", attachment.GetProperty("contentId").GetString());
                Assert.Equal("image/png", attachment.GetProperty("contentType").GetString());
            }
        }

        [Fact]
        public void From_is_absent_and_no_reply_to_added_when_from_equals_mailbox_case_insensitively()
        {
            var message = TestSupport.NewMessage();
            message.FromAddress = "NAV@Contoso.com";
            RecordedRequest request;
            JsonElement msg = SendAndCapture(message, s => s.SenderMailbox = "nav@contoso.com", out request).GetProperty("message");
            Assert.False(TestSupport.Has(msg, "from"));
            Assert.False(TestSupport.Has(msg, "replyTo"));
            Assert.Equal(TestSupport.MailboxUrl + "/sendMail", request.Url);
        }

        // docs/DESIGN.md section 11 overrides section 5: Graph "from" is never set (the mail is always sent as the mailbox);
        // a different FromAddress goes into Reply-To instead.
        [Fact]
        public void From_differing_from_fixed_mailbox_goes_to_reply_to_not_from()
        {
            var message = TestSupport.NewMessage();
            message.FromName = "Max Vertrieb";
            message.FromAddress = "vertrieb@contoso.com";
            RecordedRequest request;
            JsonElement msg = SendAndCapture(message, s => s.SenderMailbox = TestSupport.Mailbox, out request).GetProperty("message");
            Assert.False(TestSupport.Has(msg, "from"));
            JsonElement replyTo = Assert.Single(msg.GetProperty("replyTo").EnumerateArray());
            Assert.Equal("vertrieb@contoso.com", replyTo.GetProperty("emailAddress").GetProperty("address").GetString());
            Assert.Equal("Max Vertrieb", replyTo.GetProperty("emailAddress").GetProperty("name").GetString());
            Assert.Equal(TestSupport.MailboxUrl + "/sendMail", request.Url);
        }

        [Fact]
        public void From_already_in_reply_to_is_not_duplicated()
        {
            var message = TestSupport.NewMessage();
            message.FromAddress = "vertrieb@contoso.com";
            message.AddReplyTo("VERTRIEB@contoso.com");
            RecordedRequest request;
            JsonElement msg = SendAndCapture(message, s => s.SenderMailbox = TestSupport.Mailbox, out request).GetProperty("message");
            Assert.Single(msg.GetProperty("replyTo").EnumerateArray());
        }

        [Fact]
        public void ReplyToFromAddress_false_leaves_reply_to_alone()
        {
            var message = TestSupport.NewMessage();
            message.FromAddress = "vertrieb@contoso.com";
            RecordedRequest request;
            JsonElement msg = SendAndCapture(message, s =>
            {
                s.SenderMailbox = TestSupport.Mailbox;
                s.ReplyToFromAddress = false;
            }, out request).GetProperty("message");
            Assert.False(TestSupport.Has(msg, "replyTo"));
            Assert.False(TestSupport.Has(msg, "from"));
        }

        [Fact]
        public void Defaults_of_new_properties()
        {
            var sender = new GraphMailSender("t", "c");
            Assert.True(sender.ReplyToFromAddress);
            Assert.True(sender.UseDraftForLargeMessages);
            Assert.True(sender.SaveToSentItems);
            Assert.False(sender.SendX5C);
            Assert.Equal(100, sender.TimeoutSeconds);
            Assert.Equal(3, sender.MaxRetries);
            Assert.Equal(3670016, sender.SendMailPayloadLimitBytes);
            Assert.Equal(3145728, sender.DirectAttachmentLimitBytes);
            Assert.Equal(3932160, sender.UploadChunkBytes);
            Assert.Equal(157286400, sender.MaxAttachmentBytes);
            Assert.Equal("BuiltIn", sender.TokenLibrary);
            Assert.Equal("Global", sender.Cloud);
            Assert.Equal("", sender.LastMessageId);
            Assert.Equal("", sender.LastRequestId);
            Assert.False(string.IsNullOrEmpty(GraphMailSender.Version));
        }

        [Fact]
        public void Standard_headers_on_graph_calls()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(202);
            var tokens = new FakeTokenProvider();
            GraphMailSender sender = TestSupport.NewSender(handler, tokens, null);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer fake-token-1", request.Authorization);
            Guid id;
            Assert.True(Guid.TryParse(request.Header("client-request-id"), out id), "client-request-id must be a GUID: " + request.Header("client-request-id"));
            Assert.StartsWith("NavMailGraph/", request.Header("User-Agent"));
            Assert.Contains("application/json", request.Header("Accept"));
            Assert.Equal("", sender.LastMessageId);
        }

        [Fact]
        public void Message_with_two_mailboxes_sends_through_sender_mailbox_url()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.SenderMailbox = "shared#1@contoso.com";
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(TestSupport.GraphBase + "/users/shared%231@contoso.com/sendMail", handler.Requests[0].Url);
        }

        [Fact]
        public void Graph_base_url_override_is_used()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.GraphBaseUrl = "https://graph.microsoft.com/beta/";
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal("https://graph.microsoft.com/beta/users/nav@contoso.com/sendMail", handler.Requests[0].Url);
        }
    }
}
