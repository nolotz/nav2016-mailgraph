using System.Linq;
using NavMailGraph.Internal;
using NavMailGraph.Reading;
using NavMailGraph.Tests.Infrastructure;
using Xunit;
using static NavMailGraph.Tests.Reading.ReadingTestSupport;

namespace NavMailGraph.Tests.Reading
{
    public class FolderTests
    {
        [Fact]
        public void Path_is_resolved_segment_by_segment_and_cached()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders("nav-id", "NAV"))
                .Enqueue(200, Folders("inv-id", "Invoices"))
                .Enqueue(200, Page(null))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "Inbox/NAV/Invoices";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("", mailbox.Fetch());

            Assert.Equal(4, handler.Requests.Count);
            Assert.Equal(FoldersUrl + "/inbox/childFolders", PathOf(handler.Requests[0]));
            Assert.Equal("displayName eq 'NAV'", Param(handler.Requests[0], "$filter"));
            Assert.Equal("id,displayName", Param(handler.Requests[0], "$select"));
            Assert.Equal(FoldersUrl + "/nav-id/childFolders", PathOf(handler.Requests[1]));
            Assert.Equal("displayName eq 'Invoices'", Param(handler.Requests[1], "$filter"));
            Assert.Equal(FoldersUrl + "/inv-id/messages", PathOf(handler.Requests[2]));
            Assert.Equal(FoldersUrl + "/inv-id/messages", PathOf(handler.Requests[3]));
            Assert.All(handler.Requests, r => Assert.Contains("IdType=\"ImmutableId\"", r.Header("Prefer")));
        }

        [Fact]
        public void First_segment_that_is_not_well_known_is_looked_up_below_the_root()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders("arch-id", "Archiv 2026"))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "Archiv 2026";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(FoldersUrl, PathOf(handler.Requests[0]));
            Assert.Equal("displayName eq 'Archiv 2026'", Param(handler.Requests[0], "$filter"));
            Assert.Equal(FoldersUrl + "/arch-id/messages", PathOf(handler.Requests[1]));
        }

        [Fact]
        public void Quotes_in_folder_names_are_doubled()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders("q-id", "O'Brien & Co"))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "inbox/O'Brien & Co";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("displayName eq 'O''Brien & Co'", Param(handler.Requests[0], "$filter"));
            Assert.Contains("%26", handler.Requests[0].Url);
            Assert.Equal("$filter=displayName%20eq%20%27O%27%27Brien%20%26%20Co%27&$select=id,displayName", MailQueries.FolderLookupQuery("O'Brien & Co"));
        }

        private const string FolderId = "AAMkAGI2THVSAAA=AAMkAGI2THVSAAA=AAMkAGI2THVSAAA=AAMkAGI2THVSAAA=";

        [Fact]
        public void Well_known_name_is_used_directly_and_folder_id_is_verified_once()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Page(null))
                .Enqueue(200, "{\"id\":\"" + FolderId + "\",\"displayName\":\"Rechnungen\"}")
                .Enqueue(200, Page(null))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "JunkEmail";
            Assert.Equal("", mailbox.Fetch());
            mailbox.Folder = FolderId;
            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(4, handler.Requests.Count);
            Assert.Equal(FoldersUrl + "/junkemail/messages", PathOf(handler.Requests[0]));
            Assert.Equal(FoldersUrl + "/" + FolderId, PathOf(handler.Requests[1]));
            Assert.Equal("id,displayName", Param(handler.Requests[1], "$select"));
            Assert.Equal(FoldersUrl + "/" + FolderId + "/messages", PathOf(handler.Requests[2]));
            Assert.Equal(FoldersUrl + "/" + FolderId + "/messages", PathOf(handler.Requests[3]));
        }

        [Fact]
        public void Folder_id_with_child_path_uses_the_id_as_parent()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, "{\"id\":\"" + FolderId + "\",\"displayName\":\"Rechnungen\"}")
                .Enqueue(200, Folders())
                .Enqueue(201, "{\"id\":\"proc-id\",\"displayName\":\"Processed\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.EnsureFolder(FolderId + "/Processed"));
            Assert.Equal("proc-id", mailbox.LastFolderId);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(FoldersUrl + "/" + FolderId, PathOf(handler.Requests[0]));
            Assert.Equal(FoldersUrl + "/" + FolderId + "/childFolders", PathOf(handler.Requests[1]));
            Assert.Equal("displayName eq 'Processed'", Param(handler.Requests[1], "$filter"));
            Assert.Equal("POST", handler.Requests[2].Method.Method);
            Assert.Equal(FoldersUrl + "/" + FolderId + "/childFolders", PathOf(handler.Requests[2]));
        }

        [Fact]
        public void Id_like_folder_name_falls_back_to_display_name_lookup()
        {
            const string name = "Eingangsrechnungen_Lieferanten_Deutschland";
            Assert.True(MailQueries.LooksLikeFolderId(name));
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, TestSupport.GraphError("ErrorInvalidIdMalformed", "Id is malformed.", "r"), "r")
                .Enqueue(200, Folders("name-id", name))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = name;

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(FoldersUrl + "/" + name, PathOf(handler.Requests[0]));
            Assert.Equal(FoldersUrl, PathOf(handler.Requests[1]));
            Assert.Equal("displayName eq '" + name + "'", Param(handler.Requests[1], "$filter"));
            Assert.Equal(FoldersUrl + "/name-id/messages", PathOf(handler.Requests[2]));
        }

        [Fact]
        public void Unknown_id_like_first_segment_is_never_created_as_root_folder()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(404, TestSupport.GraphError("ErrorItemNotFound", "The specified object was not found in the store.", "r"), "r")
                .Enqueue(200, Folders());
            GraphMailbox mailbox = NewMailbox(handler);

            string error = mailbox.EnsureFolder(FolderId + "/Processed");
            Assert.StartsWith("Mail folder '" + FolderId + "' of path '" + FolderId + "/Processed' was not found", error);
            Assert.Contains("it is not created", error);
            Assert.Equal(2, handler.Requests.Count);
            Assert.DoesNotContain(handler.Requests, r => r.Method.Method == "POST");
        }

        [Fact]
        public void Missing_source_folder_is_not_created_and_reports_not_found()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, Folders());
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "Inbox/Missing";

            string error = mailbox.Fetch();
            Assert.StartsWith("Mail folder 'Missing' of path 'Inbox/Missing' was not found in mailbox nav@contoso.com.", error);
            Assert.Contains("Hint: " + ErrorHints.ReadNotFoundHint, error);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void EnsureFolder_creates_missing_segments()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders())
                .Enqueue(201, "{\"id\":\"proc-id\",\"displayName\":\"Processed\"}")
                .Enqueue(200, Folders())
                .Enqueue(201, "{\"id\":\"y-id\",\"displayName\":\"2026\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.EnsureFolder("Inbox/Processed/2026"));
            Assert.Equal("y-id", mailbox.LastFolderId);

            RecordedRequest create = handler.Requests[1];
            Assert.Equal("POST", create.Method.Method);
            Assert.Equal(FoldersUrl + "/inbox/childFolders", create.Url);
            Assert.Equal("{\"displayName\":\"Processed\",\"isHidden\":false}", create.BodyText);
            Assert.Equal(FoldersUrl + "/proc-id/childFolders", handler.Requests[3].Url);
            Assert.Equal(3, mailbox.CachedFolderCount); // inbox, inbox/processed, inbox/processed/2026

            // cached: no further requests
            Assert.Equal("", mailbox.EnsureFolder("inbox/processed/2026"));
            Assert.Equal(4, handler.Requests.Count);
        }

        [Fact]
        public void Root_level_folder_is_created_under_mailFolders()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders())
                .Enqueue(201, "{\"id\":\"root-child\"}");
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.EnsureFolder("NAV Import"));
            Assert.Equal(FoldersUrl, handler.Requests[1].Url);
            Assert.Equal("root-child", mailbox.LastFolderId);
        }

        [Fact]
        public void Filter_rejected_falls_back_to_listing()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, TestSupport.GraphError("BadRequest", "Invalid filter clause", "r"), "r")
                .Enqueue(200, "{\"value\":[{\"id\":\"a\",\"displayName\":\"Other\"}],\"@odata.nextLink\":\"" + FoldersUrl + "/inbox/childFolders?%24skip=1\"}")
                .Enqueue(200, Folders("b", "processed"))
                .Enqueue(200, Page(null));
            GraphMailbox mailbox = NewMailbox(handler);
            mailbox.Folder = "Inbox/Processed";

            Assert.Equal("", mailbox.Fetch());
            Assert.Equal("100", Param(handler.Requests[1], "$top"));
            Assert.Equal(FoldersUrl + "/b/messages", PathOf(handler.Requests[3]));
        }

        [Fact]
        public void Create_conflict_looks_up_again()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders())
                .Enqueue(409, TestSupport.GraphError("ErrorFolderExists", "A folder with the specified name already exists.", "r"), "r")
                .Enqueue(200, Folders("exists-id", "Processed"));
            GraphMailbox mailbox = NewMailbox(handler);

            Assert.Equal("", mailbox.EnsureFolder("Inbox/Processed"));
            Assert.Equal("exists-id", mailbox.LastFolderId);
        }

        [Fact]
        public void Empty_segment_is_validation_error()
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailbox mailbox = NewMailbox(handler);
            Assert.StartsWith("Validation failed: folder path", mailbox.EnsureFolder("Inbox//X"));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Create_folder_403_names_Mail_ReadWrite_role()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, Folders())
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "r"), "r");
            string error = NewMailbox(handler).EnsureFolder("Inbox/Processed");
            Assert.Contains("create folder 'Processed' failed (HTTP 403", error);
            Assert.Contains("Hint: " + ErrorHints.MailReadWriteForMailboxHint, error);
        }

        [Fact]
        public void Split_and_well_known_helpers()
        {
            Assert.Equal(new[] { "Inbox", "A", "B" }, MailQueries.SplitFolderPath(" /Inbox/ A /B/ ").ToArray());
            Assert.Empty(MailQueries.SplitFolderPath(""));
            Assert.Equal("inbox", MailQueries.WellKnownName("INBOX"));
            Assert.Equal("sentitems", MailQueries.WellKnownName("SentItems"));
            Assert.Null(MailQueries.WellKnownName("Posteingang"));
            Assert.False(MailQueries.LooksLikeFolderId("Processed"));
        }
    }
}
