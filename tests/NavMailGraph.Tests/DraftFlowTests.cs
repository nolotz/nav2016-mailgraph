using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using NavMailGraph.Graph;
using NavMailGraph.Internal;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class DraftFlowTests
    {
        internal const string DraftId = "AAMkADraft==";

        internal const string UploadUrl = "https://outlook.office.com/api/v2.0/Users('abc')/Messages('AAMkADraft==')/AttachmentSessions('s1')?authtoken=eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

        internal static string DraftUrl
        {
            get { return TestSupport.MailboxUrl + "/messages/" + DraftId; }
        }

        internal static string CreatedDraft()
        {
            return "{\"id\":\"" + DraftId + "\",\"isDraft\":true}";
        }

        internal static string Session()
        {
            return "{\"uploadUrl\":\"" + UploadUrl + "\",\"expirationDateTime\":\"2026-10-12T08:00:00Z\",\"nextExpectedRanges\":[\"0-\"]}";
        }

        internal static string Intermediate(long next)
        {
            return "{\"expirationDateTime\":\"2026-10-12T08:00:00Z\",\"nextExpectedRanges\":[\"" + next + "\"]}";
        }

        [Fact]
        public void Two_2MB_attachments_use_draft_flow_with_direct_attachment_posts()
        {
            byte[] a = TestSupport.Pattern(2 * 1024 * 1024, 1);
            byte[] b = TestSupport.Pattern(2 * 1024 * 1024, 2);
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft())
                .Enqueue(201, "{}")
                .Enqueue(201, "{}")
                .Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(a, "a.pdf");
            message.AddAttachmentBytes(b, "b.xlsx");

            Assert.Equal("", sender.Send(message));
            Assert.Equal(DraftId, sender.LastMessageId);

            IReadOnlyList<RecordedRequest> requests = handler.Requests;
            Assert.Equal(4, requests.Count);

            Assert.Equal(HttpMethod.Post, requests[0].Method);
            Assert.Equal(TestSupport.MailboxUrl + "/messages", requests[0].Url);
            JsonElement draft = TestSupport.Json(requests[0]);
            Assert.Equal("Rechnung 1001", draft.GetProperty("subject").GetString());
            Assert.False(TestSupport.Has(draft, "attachments"));
            Assert.False(TestSupport.Has(draft, "from"));

            Assert.Equal(DraftUrl + "/attachments", requests[1].Url);
            JsonElement first = TestSupport.Json(requests[1]);
            Assert.Equal("#microsoft.graph.fileAttachment", first.GetProperty("@odata.type").GetString());
            Assert.Equal("a.pdf", first.GetProperty("name").GetString());
            Assert.Equal(a, Convert.FromBase64String(first.GetProperty("contentBytes").GetString()!));
            JsonElement second = TestSupport.Json(requests[2]);
            Assert.Equal("b.xlsx", second.GetProperty("name").GetString());
            Assert.Equal(b, Convert.FromBase64String(second.GetProperty("contentBytes").GetString()!));

            RecordedRequest send = requests[3];
            Assert.Equal(HttpMethod.Post, send.Method);
            Assert.Equal(DraftUrl + "/send", send.Url);
            Assert.True(send.HasContent);
            Assert.Equal(0L, send.ContentLength);
            Assert.Empty(send.Body);
            Assert.Equal("Bearer fake-token-1", send.Authorization);
            Assert.All(requests, r => Assert.StartsWith("Bearer ", r.Authorization));
        }

        [Fact]
        public void EmptyPost_has_explicit_content_length_zero()
        {
            using (HttpRequestMessage request = GraphClient.EmptyPost("https://graph.microsoft.com/v1.0/users/x@y/messages/1/send"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.NotNull(request.Content);
                Assert.Equal(0L, request.Content!.Headers.ContentLength);
            }
        }

        [Fact]
        public void Upload_session_for_5MB_attachment()
        {
            const int total = 5 * 1024 * 1024;
            const int chunk = 3932160;
            byte[] content = TestSupport.Pattern(total, 3);
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft())
                .Enqueue(201, Session())
                .Enqueue(200, Intermediate(chunk))
                .Enqueue(201)
                .Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            Assert.Equal(chunk, sender.UploadChunkBytes);
            using (var dir = new TempDirectory())
            {
                var message = TestSupport.NewMessage();
                message.AddAttachment(dir.File("tmp1.pdf", content), "Rechnung.pdf");
                Assert.Equal("", sender.Send(message));
            }

            IReadOnlyList<RecordedRequest> requests = handler.Requests;
            Assert.Equal(5, requests.Count);
            Assert.Equal(DraftUrl + "/attachments/createUploadSession", requests[1].Url);
            JsonElement item = TestSupport.Json(requests[1]).GetProperty("AttachmentItem");
            Assert.Equal("file", item.GetProperty("attachmentType").GetString());
            Assert.Equal("Rechnung.pdf", item.GetProperty("name").GetString());
            Assert.Equal(total, item.GetProperty("size").GetInt64());
            Assert.Equal("application/pdf", item.GetProperty("contentType").GetString());

            AssertChunks(requests.Skip(2).Take(2).ToList(), content, chunk);
            Assert.Equal(DraftUrl + "/send", requests[4].Url);
            Assert.Equal(DraftId, sender.LastMessageId);

            // The pre-authenticated upload URL's authtoken must not reach the diagnostics.
            Assert.DoesNotContain("c2lnbmF0dXJl", sender.LastDiagnostics);
        }

        [Theory]
        [InlineData(1048576, 1048576)]
        [InlineData(8388608, 4194304)] // capped at 4 MiB
        [InlineData(0, 3932160)] // non-positive -> default
        public void Upload_ranges_follow_effective_UploadChunkBytes(int configured, int effective)
        {
            const int total = 5 * 1024 * 1024;
            byte[] content = TestSupport.Pattern(total, 4);
            int chunks = (total + effective - 1) / effective;
            var handler = new FakeHttpMessageHandler().Enqueue(201, CreatedDraft()).Enqueue(201, Session());
            for (int i = 1; i < chunks; i++)
            {
                handler.Enqueue(200, Intermediate((long)i * effective));
            }

            handler.Enqueue(201).Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.UploadChunkBytes = configured;
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(content, "big.zip");
            Assert.Equal("", sender.Send(message));

            List<RecordedRequest> puts = handler.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
            Assert.Equal(chunks, puts.Count);
            AssertChunks(puts, content, effective);
        }

        private static void AssertChunks(List<RecordedRequest> puts, byte[] content, int chunk)
        {
            var reassembled = new MemoryStream();
            long offset = 0;
            foreach (RecordedRequest put in puts)
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal(UploadUrl, put.Url);
                Assert.Null(put.Authorization);
                Assert.False(put.HasHeader("Authorization"));
                Assert.Equal("application/octet-stream", put.Header("Content-Type"));
                long end = Math.Min(offset + chunk, content.Length) - 1;
                Assert.Equal("bytes " + offset + "-" + end + "/" + content.Length, put.Header("Content-Range"));
                Assert.Equal(end - offset + 1, put.ContentLength);
                Assert.Equal(end - offset + 1, put.Body.Length);
                reassembled.Write(put.Body, 0, put.Body.Length);
                offset = end + 1;
            }

            Assert.Equal(content.Length, offset);
            Assert.Equal(content, reassembled.ToArray());
        }

        [Fact]
        public void UseDraftForLargeMessages_false_fails_validation_without_http()
        {
            var handler = new FakeHttpMessageHandler();
            var tokens = new FakeTokenProvider();
            GraphMailSender sender = TestSupport.NewSender(handler, tokens, null);
            sender.UseDraftForLargeMessages = false;
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            string result = sender.Send(message);
            Assert.StartsWith("Validation failed: the message is too large for a single sendMail request", result);
            Assert.Contains("Mail.ReadWrite", result);
            Assert.Empty(handler.Requests);
            Assert.Empty(tokens.Calls);
        }

        [Theory]
        [InlineData("USGov", "")]
        [InlineData("USGovDoD", "")]
        [InlineData("Global", "https://graph.microsoft.us/v1.0")]
        [InlineData("Global", "https://dod-graph.microsoft.us/v1.0")]
        public void Us_government_blocks_upload_sessions_before_any_call(string cloud, string graphBaseUrl)
        {
            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.Cloud = cloud;
            sender.GraphBaseUrl = graphBaseUrl;
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            string result = sender.Send(message);
            Assert.StartsWith("Validation failed: attachment 'big.pdf'", result);
            Assert.Contains("US Government", result);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Us_government_still_allows_draft_flow_with_small_attachments()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(201, CreatedDraft()).Enqueue(201, "{}").Enqueue(201, "{}").Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.Cloud = "usgov";
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "a.pdf");
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "b.pdf");
            Assert.Equal("", sender.Send(message));
            Assert.All(handler.Requests, r => Assert.StartsWith("https://graph.microsoft.us/v1.0/users/nav@contoso.com/", r.Url));
        }

        [Fact]
        public void China_cloud_uses_china_graph_and_allows_upload_sessions()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft()).Enqueue(201, Session()).Enqueue(200, Intermediate(3932160)).Enqueue(201).Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.Cloud = "China";
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            Assert.Equal("", sender.Send(message));
            Assert.StartsWith("https://microsoftgraph.chinacloudapi.cn/v1.0/users/nav@contoso.com/messages", handler.Requests[0].Url);
        }

        [Fact]
        public void Failure_after_draft_creation_deletes_the_draft()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft())
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied. Check credentials and try again.", "rid-att"), "rid-att")
                .Enqueue(204);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "a.pdf");
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "b.pdf");
            string result = sender.Send(message);

            Assert.StartsWith("Microsoft Graph add attachment 'a.pdf' failed (HTTP 403, ErrorAccessDenied)", result);
            Assert.Contains(ErrorHints.Prefix + ErrorHints.MailReadWriteHint, result);
            Assert.Equal("rid-att", sender.LastRequestId);
            Assert.Equal("", sender.LastMessageId);
            RecordedRequest delete = handler.Requests[2];
            Assert.Equal(HttpMethod.Delete, delete.Method);
            Assert.Equal(DraftUrl, delete.Url);
            Assert.Equal("Bearer fake-token-1", delete.Authorization);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public void Failure_of_draft_creation_has_mail_readwrite_hint_and_no_delete()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "rid-1"), "rid-1");
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            string result = sender.Send(message);
            Assert.StartsWith("Microsoft Graph create draft failed (HTTP 403, ErrorAccessDenied)", result);
            Assert.Contains(ErrorHints.MailReadWriteHint, result);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Failure_of_send_draft_gets_access_denied_hint_and_delete()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft()).Enqueue(201, "{}").Enqueue(201, "{}")
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied.", "rid-send"), "rid-send")
                .Enqueue(204);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "a.pdf");
            message.AddAttachmentBytes(new byte[2 * 1024 * 1024], "b.pdf");
            string result = sender.Send(message);
            Assert.StartsWith("Microsoft Graph send draft failed (HTTP 403, ErrorAccessDenied)", result);
            Assert.Contains(ErrorHints.AccessDeniedHint, result);
            Assert.DoesNotContain(ErrorHints.MailReadWriteHint, result);
            Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        }

        [Fact]
        public void Upload_session_error_on_premature_201_deletes_draft()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft()).Enqueue(201, Session())
                .Enqueue(201)
                .Enqueue(204);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            string result = sender.Send(message);
            Assert.Contains("upload chunk 1/2 of 'big.pdf' failed: unexpected HTTP 201", result);
            Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);
            Assert.Equal(DraftUrl, handler.Requests[3].Url);
        }

        [Fact]
        public void Upload_session_error_on_final_200_and_on_range_mismatch()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft()).Enqueue(201, Session())
                .Enqueue(200, Intermediate(3932160))
                .Enqueue(200, Intermediate(5000000))
                .Enqueue(204);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            string result = sender.Send(message);
            Assert.Contains("upload chunk 2/2 of 'big.pdf' failed: unexpected HTTP 200 for the final range", result);
            Assert.Contains("5000000", result);

            var handler2 = new FakeHttpMessageHandler()
                .Enqueue(201, CreatedDraft()).Enqueue(201, Session())
                .Enqueue(200, Intermediate(1000))
                .Enqueue(204);
            GraphMailSender sender2 = TestSupport.NewSender(handler2);
            string result2 = sender2.Send(message);
            Assert.Contains("expects the next range to start at byte 1000 but 3932160 bytes were sent", result2);
            Assert.Equal(HttpMethod.Delete, handler2.Requests[3].Method);
        }

        [Fact]
        public void Missing_upload_url_or_draft_id_is_reported()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(201, "{}");
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(new byte[5 * 1024 * 1024], "big.pdf");
            Assert.Contains("the response contained no message id", sender.Send(message));

            var handler2 = new FakeHttpMessageHandler().Enqueue(201, CreatedDraft()).Enqueue(201, "{}").Enqueue(204);
            GraphMailSender sender2 = TestSupport.NewSender(handler2);
            Assert.Contains("the response contained no uploadUrl", sender2.Send(message));
            Assert.Equal(HttpMethod.Delete, handler2.Requests[2].Method);
        }

        [Fact]
        public void CheckChunkResponse_and_NextExpectedRange()
        {
            var client = new GraphClient(new HttpClient(new FakeHttpMessageHandler()), new FakeTokenProvider(), new Diagnostics(), TestSupport.GraphBase, TestSupport.Mailbox, 100, 0);
            client.CheckChunkResponse("op", new GraphResponse(200, Intermediate(10), ""), false, 10);
            client.CheckChunkResponse("op", new GraphResponse(200, "", ""), false, 10); // no nextExpectedRanges -> not checked
            client.CheckChunkResponse("op", new GraphResponse(201, "", ""), true, 20);

            var early = Assert.Throws<GraphMailException>(() => client.CheckChunkResponse("op", new GraphResponse(201, "", "r"), false, 10));
            Assert.Equal("UnexpectedStatus", early.ErrorCode);
            var late = Assert.Throws<GraphMailException>(() => client.CheckChunkResponse("op", new GraphResponse(200, Intermediate(15), "r"), true, 20));
            Assert.Equal("UnexpectedStatus", late.ErrorCode);
            var range = Assert.Throws<GraphMailException>(() => client.CheckChunkResponse("op", new GraphResponse(200, Intermediate(5), "r"), false, 10));
            Assert.Equal("UnexpectedRange", range.ErrorCode);

            Assert.Equal("2097152", GraphClient.NextExpectedRange("{\"ExpirationDateTime\":\"x\",\"NextExpectedRanges\":[\"2097152\"]}"));
            Assert.Equal("0-", GraphClient.NextExpectedRange(Session()));
            Assert.Equal("", GraphClient.NextExpectedRange(""));
            Assert.Equal("", GraphClient.NextExpectedRange(null));
        }
    }
}
