using System;
using System.Collections.Generic;
using System.Linq;
using NavMailGraph.Reading;
using NavMailGraph.Tests.Infrastructure;
using Xunit;
using static NavMailGraph.Tests.Reading.ReadingTestSupport;

namespace NavMailGraph.Tests.Reading
{
    public class FetchTests
    {
        [Fact]
        public void Default_query_lists_inbox_oldest_first_with_select_top_and_immutable_ids()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.Fetch());

            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal("GET", request.Method.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/mailFolders/inbox/messages", PathOf(request));
            List<KeyValuePair<string, string>> query = QueryOf(request);
            Assert.Equal(new[] { "$orderby", "$top", "$select" }, query.Select(p => p.Key).ToArray());
            Assert.Equal("receivedDateTime asc", Param(request, "$orderby"));
            Assert.Equal("50", Param(request, "$top"));
            Assert.Equal(MailQueries.MessageSelect + ",body", Param(request, "$select"));
            Assert.DoesNotContain("contentBytes", request.Url);
            Assert.Contains("IdType=\"ImmutableId\"", request.Header("Prefer"));
            Assert.DoesNotContain("outlook.body-content-type", request.Header("Prefer"));
            Assert.Equal("Bearer fake-token-1", request.Authorization);
            Assert.Equal(0, mailbox.Count);
            Assert.False(mailbox.MoreAvailable);
        }

        [Fact]
        public void Watermark_comes_first_in_filter_then_isRead()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.ReceivedAfter = "2026-10-12T08:00:00Z";
            mailbox.UnreadOnly = true;
            mailbox.MaxMessages = 20;

            Assert.Equal("", mailbox.Fetch());

            RecordedRequest request = handler.Requests[0];
            Assert.Equal(new[] { "$filter", "$orderby", "$top", "$select" }, QueryOf(request).Select(p => p.Key).ToArray());
            Assert.Equal("receivedDateTime ge 2026-10-12T08:00:00Z and isRead eq false", Param(request, "$filter"));
            Assert.Equal("20", Param(request, "$top"));
            Assert.DoesNotContain(" ", request.Url);
        }

        [Fact]
        public void Watermark_only_has_no_isRead_clause()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.ReceivedAfter = "2026-10-12T08:00:00.123+02:00";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("receivedDateTime ge 2026-10-12T08:00:00.123+02:00", Param(handler.Requests[0], "$filter"));
            Assert.Contains("%2B02%3A00", handler.Requests[0].Url);
        }

        [Fact]
        public void UnreadOnly_without_watermark_still_starts_filter_with_receivedDateTime()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.UnreadOnly = true;

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false", Param(handler.Requests[0], "$filter"));
        }

        [Theory]
        [InlineData("12.10.2026 08:00")]
        [InlineData("2026-10-12T08:00:00Z' or 1 eq 1")]
        [InlineData("2026-10-12")]
        public void Invalid_watermark_is_a_validation_error_without_request(string watermark)
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.ReceivedAfter = watermark;

            string error = mailbox.Fetch();
            Assert.StartsWith("Validation failed: ReceivedAfter", error);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void BodyAsText_adds_prefer_and_IncludeBody_false_drops_body()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.BodyAsText = true;
            mailbox.IncludeBody = false;

            Assert.Equal("", mailbox.Fetch());
            RecordedRequest request = handler.Requests[0];
            Assert.Contains("outlook.body-content-type=\"text\"", request.Header("Prefer"));
            Assert.Contains("IdType=\"ImmutableId\"", request.Header("Prefer"));
            Assert.Equal(MailQueries.MessageSelect, Param(request, "$select"));
        }

        [Theory]
        [InlineData("2026-10-12T08:00:00Z")]
        [InlineData("2026-10-12T10:00:00+02:00")]
        public void Messages_sharing_the_watermark_timestamp_are_loaded_beyond_MaxMessages(string watermark)
        {
            string next1 = "https://graph.microsoft.com/v1.0/users/nav@contoso.com/mailFolders/inbox/messages?%24skip=2";
            string next2 = "https://graph.microsoft.com/v1.0/users/nav@contoso.com/mailFolders/inbox/messages?%24skip=4";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(next1, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:00:00Z")))
                .Enqueue(200, Page(next2, Message("m3", "2026-10-12T08:00:00Z"), Message("m4", "2026-10-12T08:00:01Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.MaxMessages = 2;
            mailbox.ReceivedAfter = watermark;

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(3, mailbox.Count);
            Assert.Equal("m3", mailbox.GetMessage(2).Id);
            Assert.True(mailbox.MoreAvailable);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("share the watermark timestamp", mailbox.LastDiagnostics);
        }

        [Fact]
        public void Cap_without_watermark_tie_stops_at_MaxMessages()
        {
            string next1 = "https://graph.microsoft.com/v1.0/users/nav@contoso.com/mailFolders/inbox/messages?%24skip=2";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(next1, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:00:05Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.MaxMessages = 2;
            mailbox.ReceivedAfter = "2026-10-12T08:00:00Z";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(2, mailbox.Count);
            Assert.True(mailbox.MoreAvailable);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Follows_nextLink_unchanged()
        {
            string next = "https://graph.microsoft.com/v1.0/users/nav@contoso.com/mailFolders/inbox/messages?%24orderby=receivedDateTime+asc&%24top=2&%24skip=2";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(next, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:01:00Z")))
                .Enqueue(200, Page(null, Message("m3", "2026-10-12T08:02:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.Fetch());

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(next, handler.Requests[1].Url);
            Assert.Contains("IdType=\"ImmutableId\"", handler.Requests[1].Header("Prefer"));
            Assert.Equal(3, mailbox.Count);
            Assert.Equal(new[] { "m1", "m2", "m3" }, Enumerable.Range(0, mailbox.Count).Select(i => mailbox.GetMessage(i).Id).ToArray());
            Assert.False(mailbox.MoreAvailable);
        }

        [Fact]
        public void MaxMessages_caps_result_and_reports_more()
        {
            string next = "https://graph.microsoft.com/v1.0/users/nav@contoso.com/mailFolders/inbox/messages?%24skip=2";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(next, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:01:00Z")))
                .Enqueue(200, Page(next + "0", Message("m3", "2026-10-12T08:02:00Z"), Message("m4", "2026-10-12T08:03:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.MaxMessages = 3;

            Assert.Equal("", mailbox.Fetch());

            Assert.Equal("3", Param(handler.Requests[0], "$top"));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(3, mailbox.Count);
            Assert.True(mailbox.MoreAvailable);
        }

        [Fact]
        public void MaxMessages_is_clamped()
        {
            Assert.Equal(50, MailQueries.EffectiveMaxMessages(0));
            Assert.Equal(1000, MailQueries.EffectiveMaxMessages(5000));
            Assert.Equal(50, MailQueries.PageSize(1000));
            Assert.Equal(7, MailQueries.PageSize(7));
        }

        [Fact]
        public void NextLink_to_foreign_host_is_rejected()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page("https://evil.example.com/steal", Message("m1", "2026-10-12T08:00:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.Fetch();
            Assert.Contains("not a Microsoft Graph URL", error);
            Assert.Single(handler.Requests);
            Assert.Equal(0, mailbox.Count);
        }

        [Fact]
        public void Maps_message_fields()
        {
            string json = "{\"id\":\"AAMkImmutable=\",\"internetMessageId\":\"<abc@x.de>\",\"conversationId\":\"conv\",\"subject\":\"Rechnung 4711\","
                + "\"from\":{\"emailAddress\":{\"name\":\"Lieferant GmbH\",\"address\":\"rechnung@lieferant.de\"}},"
                + "\"sender\":{\"emailAddress\":{\"name\":\"Sekretariat\",\"address\":\"sek@lieferant.de\"}},"
                + "\"toRecipients\":[{\"emailAddress\":{\"address\":\"a@x.de\"}},{\"emailAddress\":{\"name\":\"B\",\"address\":\"b@y.de\"}}],"
                + "\"ccRecipients\":[],\"replyTo\":[{\"emailAddress\":{\"address\":\"reply@lieferant.de\"}}],"
                + "\"receivedDateTime\":\"2026-10-12T08:00:00Z\",\"sentDateTime\":\"2026-10-12T07:59:58Z\",\"isRead\":false,\"importance\":\"high\","
                + "\"hasAttachments\":true,\"bodyPreview\":\"Hallo\",\"body\":{\"contentType\":\"html\",\"content\":\"<p>Hallo</p>\"},\"webLink\":\"https://outlook.office365.com/owa/?ItemID=x\"}";
            var handler = new FakeHttpMessageHandler().Enqueue(200, Page(null, json));
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.Fetch());
            GraphMailItem item = mailbox.GetMessage(0);
            Assert.Equal("AAMkImmutable=", item.Id);
            Assert.Equal("<abc@x.de>", item.InternetMessageId);
            Assert.Equal("conv", item.ConversationId);
            Assert.Equal("Rechnung 4711", item.Subject);
            Assert.Equal("rechnung@lieferant.de", item.FromAddress);
            Assert.Equal("Lieferant GmbH", item.FromName);
            Assert.Equal("sek@lieferant.de", item.SenderAddress);
            Assert.Equal("a@x.de; b@y.de", item.ToRecipients);
            Assert.Equal("", item.CcRecipients);
            Assert.Equal("reply@lieferant.de", item.ReplyTo);
            Assert.Equal("2026-10-12T08:00:00Z", item.ReceivedText);
            Assert.Equal(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc), item.ReceivedUtc);
            Assert.Equal(DateTimeKind.Utc, item.ReceivedUtc.Kind);
            Assert.Equal("2026-10-12T07:59:58Z", item.SentText);
            Assert.Equal(new DateTime(2026, 10, 12, 7, 59, 58, DateTimeKind.Utc), item.SentUtc);
            Assert.False(item.IsRead);
            Assert.Equal("high", item.Importance);
            Assert.True(item.HasAttachments);
            Assert.True(item.BodyIsHtml);
            Assert.Equal("<p>Hallo</p>", item.Body);
            Assert.Equal("Hallo", item.BodyPreview);
            Assert.Equal("https://outlook.office365.com/owa/?ItemID=x", item.WebLink);
            Assert.Throws<ArgumentOutOfRangeException>(() => mailbox.GetMessage(1));
        }

        [Fact]
        public void Throttled_get_is_retried_with_retry_after()
        {
            var delays = new List<TimeSpan>();
            var handler = new FakeHttpMessageHandler()
                .Enqueue(429, TestSupport.GraphError("TooManyRequests", "Throttled", "r1"), "r1", TimeSpan.FromSeconds(7))
                .Enqueue(503, null, "r2")
                .Enqueue(504, null, "r3")
                .Enqueue(200, Page(null, Message("m1", "2026-10-12T08:00:00Z")));
            GraphMailbox mailbox = NewMailbox(handler, delays);

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(4, handler.Requests.Count);
            Assert.Equal(TimeSpan.FromSeconds(7), delays[0]);
            Assert.Equal(3, delays.Count);
            Assert.Equal(1, mailbox.Count);
        }

        [Fact]
        public void Access_denied_names_Mail_Read_role()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied. Check credentials and try again.", "req-403"), "req-403");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.Fetch();
            Assert.StartsWith("Microsoft Graph list messages failed (HTTP 403, ErrorAccessDenied): Access is denied.", error);
            Assert.Contains("request-id req-403", error);
            Assert.Contains("mailbox nav@contoso.com", error);
            Assert.Contains("Hint: ", error);
            Assert.Contains("'Application Mail.Read'", error);
            Assert.Equal("req-403", mailbox.LastRequestId);
            Assert.DoesNotContain("fake-token", mailbox.LastDiagnostics);
        }

        [Fact]
        public void Mailbox_not_found_hint()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(404, TestSupport.GraphError("ResourceNotFound", "Resource could not be discovered.", "r"), "r");
            string error = NewMailbox(handler).Fetch();
            Assert.Contains("Hint: " + NavMailGraph.Internal.ErrorHints.ReadNotFoundHint, error);
        }

        [Fact]
        public void Missing_mailbox_is_validation_error()
        {
            var handler = new FakeHttpMessageHandler();
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            connection.UseTokenProvider(new FakeTokenProvider());
            var mailbox = new GraphMailbox(connection, " ");
            Assert.Equal("Validation failed: the mailbox is required.", mailbox.Fetch());
            Assert.Empty(handler.Requests);
        }
    }
}
