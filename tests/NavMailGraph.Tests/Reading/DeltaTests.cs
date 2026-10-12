using System.Linq;
using NavMailGraph.Internal;
using NavMailGraph.Reading;
using NavMailGraph.Tests.Infrastructure;
using Xunit;
using static NavMailGraph.Tests.Reading.ReadingTestSupport;

namespace NavMailGraph.Tests.Reading
{
    public class DeltaTests
    {
        private const string DeltaBase = TestSupport.MailboxUrl + "/mailFolders/inbox/messages/delta";

        [Fact]
        public void Initial_sync_follows_nextLink_until_deltaLink()
        {
            string next = DeltaBase + "?%24skiptoken=abc";
            string delta = DeltaBase + "?%24deltatoken=xyz";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, DeltaPage(next, null, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:01:00Z")))
                .Enqueue(200, DeltaPage(null, delta,
                    Message("m2", "2026-10-12T08:01:00Z"),
                    "{\"id\":\"gone\",\"@removed\":{\"reason\":\"deleted\"}}",
                    "{\"id\":\"m9\",\"isRead\":true}",
                    Message("m3", "2026-10-12T08:02:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.ReceivedAfter = "2026-10-01T00:00:00Z";

            Assert.Equal("", mailbox.FetchChanges(""));

            RecordedRequest first = handler.Requests[0];
            Assert.Equal(DeltaBase, PathOf(first));
            Assert.Equal(new[] { "changeType", "$select", "$filter" }, QueryOf(first).Select(p => p.Key).ToArray());
            Assert.Equal("created", Param(first, "changeType"));
            Assert.Equal(MailQueries.MessageSelect + ",body", Param(first, "$select"));
            Assert.Equal("receivedDateTime ge 2026-10-01T00:00:00Z", Param(first, "$filter"));
            Assert.Contains("odata.maxpagesize=50", first.Header("Prefer"));
            Assert.Contains("IdType=\"ImmutableId\"", first.Header("Prefer"));
            Assert.Equal(next, handler.Requests[1].Url);
            Assert.Contains("IdType=\"ImmutableId\"", handler.Requests[1].Header("Prefer"));

            Assert.Equal(new[] { "m1", "m2", "m3" }, Enumerable.Range(0, mailbox.Count).Select(i => mailbox.GetMessage(i).Id).ToArray());
            Assert.Equal(1, mailbox.RemovedCount);
            Assert.Equal("gone", mailbox.GetRemovedId(0));
            Assert.Equal(delta, mailbox.NextDeltaLink);
            Assert.False(mailbox.DeltaExpired);
            Assert.False(mailbox.MoreAvailable);
        }

        [Fact]
        public void Initial_sync_without_watermark_has_no_filter()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, DeltaPage(null, DeltaBase + "?%24deltatoken=1"));
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.Equal("", mailbox.FetchChanges(""));
            Assert.Equal(new[] { "changeType", "$select" }, QueryOf(handler.Requests[0]).Select(p => p.Key).ToArray());
        }

        [Fact]
        public void Next_sync_uses_stored_link_verbatim()
        {
            string stored = DeltaBase + "?%24deltatoken=LongOpaqueToken_-";
            string newer = DeltaBase + "?%24deltatoken=Newer";
            var handler = new FakeHttpMessageHandler().Enqueue(200, DeltaPage(null, newer, Message("m5", "2026-10-12T09:00:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.FetchChanges(stored));
            Assert.Equal(stored, handler.Requests[0].Url);
            Assert.Equal(1, mailbox.Count);
            Assert.Equal(newer, mailbox.NextDeltaLink);
        }

        [Fact]
        public void MaxMessages_stops_at_page_boundary_and_returns_nextLink_to_resume()
        {
            string next = DeltaBase + "?%24skiptoken=p2";
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, DeltaPage(next, null, Message("m1", "2026-10-12T08:00:00Z"), Message("m2", "2026-10-12T08:01:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.MaxMessages = 2;

            Assert.Equal("", mailbox.FetchChanges(""));
            Assert.Contains("odata.maxpagesize=2", handler.Requests[0].Header("Prefer"));
            Assert.Single(handler.Requests);
            Assert.Equal(2, mailbox.Count);
            Assert.True(mailbox.MoreAvailable);
            Assert.Equal(next, mailbox.NextDeltaLink);
        }

        [Fact]
        public void Gone_410_sets_DeltaExpired()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(410, TestSupport.GraphError("resyncRequired", "Resync required.", "r410"), "r410");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.FetchChanges(DeltaBase + "?%24deltatoken=old");
            Assert.StartsWith("DeltaExpired: Microsoft Graph delta failed (HTTP 410", error);
            Assert.Contains("Hint: " + ErrorHints.DeltaExpiredHint, error);
            Assert.True(mailbox.DeltaExpired);
            Assert.Equal("", mailbox.NextDeltaLink);
            Assert.Equal(0, mailbox.Count);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void SyncStateNotFound_sets_DeltaExpired()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, TestSupport.GraphError("syncStateNotFound", "The sync state was not found.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.FetchChanges(DeltaBase + "?%24deltatoken=old");
            Assert.StartsWith("DeltaExpired:", error);
            Assert.True(mailbox.DeltaExpired);
        }

        [Theory]
        [InlineData(400, "ErrorInvalidSyncStateData")]
        [InlineData(404, "ErrorItemNotFound")]
        [InlineData(400, "BadRequest")]
        public void Any_other_4xx_on_a_stored_link_sets_DeltaExpired(int status, string code)
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(status, TestSupport.GraphError(code, "Rejected.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.FetchChanges(DeltaBase + "?%24deltatoken=old");
            Assert.StartsWith("DeltaExpired:", error);
            Assert.True(mailbox.DeltaExpired);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Plain_4xx_on_the_initial_request_does_not_set_DeltaExpired()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, TestSupport.GraphError("BadRequest", "Invalid filter clause.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.FetchChanges("");
            Assert.False(mailbox.DeltaExpired);
            Assert.DoesNotContain("DeltaExpired:", error);
        }

        [Fact]
        public void Other_errors_do_not_set_DeltaExpired()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "r"), "r");
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.FetchChanges(DeltaBase + "?%24deltatoken=old");
            Assert.False(mailbox.DeltaExpired);
            Assert.Contains("Application Mail.Read", error);
        }

        [Fact]
        public void Foreign_delta_link_is_rejected_without_request()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(handler);
            string error = mailbox.FetchChanges("https://example.org/delta?token=1");
            Assert.StartsWith("Validation failed: the delta link is not a Microsoft Graph URL", error);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Response_without_links_is_an_error()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, DeltaPage(null, null, Message("m1", "2026-10-12T08:00:00Z")));
            GraphMailbox mailbox = NewMailbox(handler);
            string error = mailbox.FetchChanges("");
            Assert.Contains("neither @odata.nextLink nor @odata.deltaLink", error);
            Assert.Equal(0, mailbox.Count);
        }
    }
}
