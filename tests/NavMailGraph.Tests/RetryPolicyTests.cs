using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    /// <summary>Per-operation retry policy (504 / timeouts on non-idempotent POSTs), token retries, attachment POST size.</summary>
    public class RetryPolicyTests
    {
        public RetryPolicyTests()
        {
            GraphMailSender.ResetTokenCacheForTests();
        }

        private static string TokenJson(string token)
        {
            return "{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"" + token + "\"}";
        }

        [Fact]
        public void SendMail_504_is_not_retried_and_reports_unknown_outcome()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(504, TestSupport.GraphError("UnknownError", "Gateway Timeout", "r504")).Enqueue(202);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = TestSupport.NewSender(handler, null, delays);
            string result = sender.Send(TestSupport.NewMessage());

            Assert.Single(handler.Requests);
            Assert.Empty(delays);
            Assert.StartsWith("Microsoft Graph sendMail failed (HTTP 504, UnknownError): Gateway Timeout", result);
            Assert.Contains("The outcome is unknown: the message may already have been sent. Check the Sent Items folder of mailbox " + TestSupport.Mailbox, result);
            Assert.Equal("r504", sender.LastRequestId);
        }

        [Fact]
        public void SendMail_timeout_reports_unknown_outcome()
        {
            var handler = new FakeHttpMessageHandler().EnqueueException(new TaskCanceledException("timeout"));
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());

            Assert.Single(handler.Requests);
            Assert.StartsWith("Microsoft Graph sendMail failed: no response within", result);
            Assert.Contains("The outcome is unknown", result);
        }

        [Fact]
        public void SendMail_transport_error_is_not_reported_as_unknown_outcome()
        {
            var handler = new FakeHttpMessageHandler().EnqueueException(new HttpRequestException("No such host is known."));
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.DoesNotContain("outcome is unknown", result);
        }

        [Theory]
        [InlineData(429)]
        [InlineData(503)]
        [InlineData(509)]
        public void SendMail_throttling_statuses_are_retried(int status)
        {
            var handler = new FakeHttpMessageHandler().Enqueue(status, null, null, TimeSpan.Zero).Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void Create_draft_504_is_not_retried()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(504).Enqueue(201, DraftFlowTests.CreatedDraft());
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(4 * 1024 * 1024), "big.pdf");
            string result = sender.Send(message);

            Assert.Single(handler.Requests);
            Assert.StartsWith("Microsoft Graph create draft failed (HTTP 504", result);
            Assert.DoesNotContain("outcome is unknown", result);
        }

        [Fact]
        public void Send_draft_504_is_not_retried_and_keeps_the_draft()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, DraftFlowTests.CreatedDraft())
                .Enqueue(201, "{}")
                .Enqueue(201, "{}")
                .Enqueue(504)
                .Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(2 * 1024 * 1024, 1), "a.pdf");
            message.AddAttachmentBytes(TestSupport.Pattern(2 * 1024 * 1024, 2), "b.pdf");
            string result = sender.Send(message);

            Assert.Equal(4, handler.Requests.Count);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
            Assert.StartsWith("Microsoft Graph send draft failed (HTTP 504", result);
            Assert.Contains("The outcome is unknown", result);
            Assert.Contains("kept: the outcome of the send is unknown", sender.LastDiagnostics);
            Assert.Equal("", sender.LastMessageId);
        }

        [Fact]
        public void Upload_range_504_is_retried()
        {
            int size = 5 * 1024 * 1024;
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, DraftFlowTests.CreatedDraft())
                .Enqueue(201, DraftFlowTests.Session())
                .Enqueue(504)
                .Enqueue(200, DraftFlowTests.Intermediate(GraphMailSender.DefaultUploadChunkBytes))
                .Enqueue(201, "{}")
                .Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(size), "big.pdf");

            Assert.Equal("", sender.Send(message));
            List<RecordedRequest> puts = handler.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
            Assert.Equal(3, puts.Count);
            Assert.Equal(puts[0].Header("Content-Range"), puts[1].Header("Content-Range"));
        }

        [Fact]
        public void IsRetryable_by_kind()
        {
            Assert.True(GraphClient.IsRetryable(504, GraphClient.RequestKind.Idempotent));
            Assert.False(GraphClient.IsRetryable(504, GraphClient.RequestKind.NonIdempotent));
            Assert.False(GraphClient.IsRetryable(504, GraphClient.RequestKind.Delivering));
            foreach (GraphClient.RequestKind kind in new[] { GraphClient.RequestKind.Idempotent, GraphClient.RequestKind.NonIdempotent, GraphClient.RequestKind.Delivering })
            {
                Assert.True(GraphClient.IsRetryable(429, kind));
                Assert.True(GraphClient.IsRetryable(503, kind));
                Assert.True(GraphClient.IsRetryable(509, kind));
                Assert.False(GraphClient.IsRetryable(500, kind));
                Assert.False(GraphClient.IsRetryable(413, kind));
            }
        }

        [Theory]
        [InlineData(503)]
        [InlineData(429)]
        [InlineData(500)]
        public void Builtin_token_request_is_retried_on_transient_status(int status)
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(status, "{\"error\":\"temporarily_unavailable\"}", null, TimeSpan.FromSeconds(3))
                .Enqueue(200, TokenJson("at-1"))
                .Enqueue(202);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = GraphMailSender.CreateForTests(Guid.NewGuid().ToString("D"), TestSupport.ClientId, handler);
            sender.UseClientSecret("s3cret");
            sender.RetryDelayOverride = (d, ct) =>
            {
                delays.Add(d);
                return Task.FromResult(0);
            };

            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays.ToArray());
            Assert.Equal("Bearer at-1", handler.Requests[2].Authorization);
        }

        [Fact]
        public void Builtin_token_request_is_retried_on_transport_error_and_respects_MaxRetries()
        {
            var handler = new FakeHttpMessageHandler()
                .EnqueueException(new HttpRequestException("connection reset"))
                .EnqueueException(new HttpRequestException("connection reset"));
            GraphMailSender sender = GraphMailSender.CreateForTests(Guid.NewGuid().ToString("D"), TestSupport.ClientId, handler);
            sender.UseClientSecret("s3cret");
            sender.MaxRetries = 1;
            sender.RetryDelayOverride = (d, ct) => Task.FromResult(0);

            string result = sender.TestConnection();
            Assert.StartsWith("Token acquisition failed: HttpRequestException: connection reset", result);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void Builtin_token_request_is_not_retried_on_400()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS90002: Tenant 'x' not found.\"}")
                .Enqueue(200, TokenJson("at-1"));
            GraphMailSender sender = GraphMailSender.CreateForTests(Guid.NewGuid().ToString("D"), TestSupport.ClientId, handler);
            sender.UseClientSecret("s3cret");
            sender.RetryDelayOverride = (d, ct) => Task.FromResult(0);

            Assert.StartsWith("Token acquisition failed (invalid_request, AADSTS90002)", sender.TestConnection());
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Serialized_json_has_no_escaped_solidus()
        {
            byte[] content = TestSupport.Pattern(300000);
            var attachment = new MailAttachment("a/b.pdf", "application/pdf", null, content, false, "");
            byte[] payload = GraphJson.SerializeToUtf8Bytes(GraphMailSender.BuildFileAttachment(attachment));
            string text = Encoding.UTF8.GetString(payload);

            Assert.DoesNotContain("\\/", text);
            Assert.Contains("a/b.pdf", text);
            Assert.Contains("application/pdf", text);
            Assert.Equal(content, Convert.FromBase64String(TestSupport.ParseJson(text).GetProperty("contentBytes").GetString()!));
        }

        [Theory]
        [InlineData("{\"a\":\"x\\/y\"}", "{\"a\":\"x/y\"}")]
        [InlineData("{\"a\":\"x\\\\\\/y\"}", "{\"a\":\"x\\\\/y\"}")]
        [InlineData("{\"a\":\"x\\\\\"}", "{\"a\":\"x\\\\\"}")]
        [InlineData("{\"a\":\"\\u002f\\n\"}", "{\"a\":\"\\u002f\\n\"}")]
        public void UnescapeSolidus_keeps_json_equivalent(string input, string expected)
        {
            string output = Encoding.UTF8.GetString(GraphJson.UnescapeSolidus(Encoding.UTF8.GetBytes(input)));
            Assert.Equal(expected, output);
            Assert.Equal(TestSupport.ParseJson(input).GetProperty("a").GetString(), TestSupport.ParseJson(output).GetProperty("a").GetString());
        }

        [Fact]
        public void Largest_direct_attachment_post_body_stays_close_to_base64_size()
        {
            // 3 MiB - 1: the largest attachment POSTed directly by default. Without the '\/' escapes the body is the
            // base64 text (4,194,304 bytes) plus the small JSON envelope, not ~4.26 MB as before.
            long size = GraphMailSender.DefaultDirectAttachmentLimitBytes - 1;
            var attachment = new MailAttachment("scan.pdf", "application/pdf", null, TestSupport.Pattern((int)size), false, "");
            byte[] payload = GraphJson.SerializeToUtf8Bytes(GraphMailSender.BuildFileAttachment(attachment));
            long base64 = ((size + 2) / 3) * 4;
            Assert.InRange(payload.Length, base64, base64 + 512);
        }

        [Fact]
        public void Direct_attachment_413_falls_back_to_upload_session()
        {
            int size = 3100000; // below DirectAttachmentLimitBytes (3 MiB), above the 3,000,000-byte upload-session lower bound
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, DraftFlowTests.CreatedDraft())
                .Enqueue(413, TestSupport.GraphError("RequestEntityTooLarge", "Payload too large", "r413"))
                .Enqueue(201, DraftFlowTests.Session())
                .Enqueue(201, "{}")
                .Enqueue(202);
            GraphMailSender sender = TestSupport.NewSender(handler);
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(size), "scan.pdf");

            Assert.Equal("", sender.Send(message));
            IReadOnlyList<RecordedRequest> requests = handler.Requests;
            Assert.Equal(5, requests.Count);
            Assert.Equal(DraftFlowTests.DraftUrl + "/attachments", requests[1].Url);
            Assert.Equal(DraftFlowTests.DraftUrl + "/attachments/createUploadSession", requests[2].Url);
            Assert.Equal(HttpMethod.Put, requests[3].Method);
            Assert.Equal("bytes 0-" + (size - 1) + "/" + size, requests[3].Header("Content-Range"));
            Assert.Equal(DraftFlowTests.DraftUrl + "/send", requests[4].Url);
        }

        [Fact]
        public void Direct_attachment_413_below_upload_session_minimum_is_reported()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(201, DraftFlowTests.CreatedDraft())
                .Enqueue(413, TestSupport.GraphError("RequestEntityTooLarge", "Payload too large", "r413"))
                .Enqueue(204);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.SendMailPayloadLimitBytes = 1000;
            var message = TestSupport.NewMessage();
            message.AddAttachmentBytes(TestSupport.Pattern(2000000), "scan.pdf");

            string result = sender.Send(message);
            Assert.StartsWith("Microsoft Graph add attachment 'scan.pdf' failed (HTTP 413", result);
            Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        }
    }
}
