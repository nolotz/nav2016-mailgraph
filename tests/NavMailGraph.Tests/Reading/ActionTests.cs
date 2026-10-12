using System.Collections.Generic;
using System;
using NavMailGraph.Internal;
using NavMailGraph.Tests.Infrastructure;
using Xunit;
using static NavMailGraph.Tests.Reading.ReadingTestSupport;

namespace NavMailGraph.Tests.Reading
{
    public class ActionTests
    {
        private const string MessageId = "AAMkAGI2-immutable_id=";

        [Fact]
        public void MarkAsRead_and_MarkAsUnread_patch_isRead()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, "{\"id\":\"x\"}").Enqueue(200, "{\"id\":\"x\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.MarkAsRead(MessageId));
            Assert.Equal("", mailbox.MarkAsUnread(MessageId));

            Assert.Equal("PATCH", handler.Requests[0].Method.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/" + MessageId, handler.Requests[0].Url);
            Assert.Equal("{\"isRead\":true}", handler.Requests[0].BodyText);
            Assert.StartsWith("application/json", handler.Requests[0].Header("Content-Type"));
            Assert.Equal("{\"isRead\":false}", handler.Requests[1].BodyText);
            Assert.Contains("IdType=\"ImmutableId\"", handler.Requests[0].Header("Prefer"));
        }

        [Fact]
        public void MarkAsRead_403_names_Mail_ReadWrite_role()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "r403"), "r403");
            string error = NewMailbox(handler).MarkAsRead(MessageId);
            Assert.StartsWith("Microsoft Graph mark as read failed (HTTP 403, ErrorAccessDenied)", error);
            Assert.Contains("'Application Mail.ReadWrite'", error);
        }

        [Fact]
        public void Move_to_new_path_creates_folder_then_posts_destinationId()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders())
                .Enqueue(201, "{\"id\":\"proc-id\",\"displayName\":\"Processed\"}")
                .Enqueue(201, "{\"id\":\"" + MessageId + "\",\"subject\":\"x\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.MoveToFolder(MessageId, "Inbox/Processed"));

            RecordedRequest move = handler.Requests[2];
            Assert.Equal("POST", move.Method.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/" + MessageId + "/move", move.Url);
            Assert.Equal("{\"destinationId\":\"proc-id\"}", move.BodyText);
            Assert.Equal(MessageId, mailbox.LastMovedMessageId);
        }

        [Fact]
        public void Move_to_well_known_folder_needs_no_lookup()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(201, "{\"id\":\"new-id\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.MoveToFolder(MessageId, "DeletedItems"));
            Assert.Single(handler.Requests);
            Assert.Equal("{\"destinationId\":\"deleteditems\"}", handler.Requests[0].BodyText);
            Assert.Equal("new-id", mailbox.LastMovedMessageId);
        }

        [Fact]
        public void Move_with_stale_cached_folder_resolves_again_once()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders("old-id", "Processed"))
                .Enqueue(201, "{\"id\":\"m\"}")
                .Enqueue(404, TestSupport.GraphError("ErrorItemNotFound", "The specified object was not found in the store.", "r"), "r")
                .Enqueue(200, Folders("new-id", "Processed"))
                .Enqueue(201, "{\"id\":\"m\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.MoveToFolder("m", "Inbox/Processed"));
            Assert.Equal("", mailbox.MoveToFolder("m", "Inbox/Processed"));
            Assert.Equal(5, handler.Requests.Count);
            Assert.Equal("{\"destinationId\":\"new-id\"}", handler.Requests[4].BodyText);
        }

        [Fact]
        public void Move_is_not_retried_on_504_but_on_429()
        {
            var delays = new List<TimeSpan>();
            var handler = new FakeHttpMessageHandler()
                .Enqueue(429, null, "a", TimeSpan.FromSeconds(3))
                .Enqueue(504, null, "b");
            GraphMailbox mailbox = NewMailbox(handler, delays);

            string error = mailbox.MoveToFolder(MessageId, "inbox");
            Assert.Contains("HTTP 504", error);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays.ToArray());
        }

        [Fact]
        public void Delete_sends_DELETE()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(204);
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.DeleteMessage(MessageId));
            Assert.Equal("DELETE", handler.Requests[0].Method.Method);
            Assert.Equal(TestSupport.MailboxUrl + "/messages/" + MessageId, handler.Requests[0].Url);
        }

        [Fact]
        public void Empty_ids_are_validation_errors()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("Validation failed: the message id is required.", mailbox.MarkAsRead(""));
            Assert.Equal("Validation failed: the message id is required.", mailbox.DeleteMessage(null!));
            Assert.Equal("Validation failed: the target folder is required.", mailbox.MoveToFolder("x", " "));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Token_401_is_refreshed_once()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(401, TestSupport.GraphError("InvalidAuthenticationToken", "expired", "r")).Enqueue(204);
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.DeleteMessage(MessageId));
            Assert.Equal("Bearer fake-token-1", handler.Requests[0].Authorization);
            Assert.Equal("Bearer fake-token-2", handler.Requests[1].Authorization);
        }
    }
}
