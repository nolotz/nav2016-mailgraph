using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using NavMailGraph.Internal;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class RetryAndErrorTests
    {
        [Fact]
        public void Retry_after_429_then_success()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(429, TestSupport.GraphError("ApplicationThrottled", "Too many requests", "r1"), "r1", TimeSpan.Zero)
                .Enqueue(202);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = TestSupport.NewSender(handler, null, delays);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.Zero }, delays.ToArray());
            Assert.NotEqual(handler.Requests[0].Header("client-request-id"), handler.Requests[1].Header("client-request-id"));
            Assert.Equal(handler.Requests[0].BodyText, handler.Requests[1].BodyText);
        }

        [Fact]
        public void Retry_after_seconds_value_is_passed_to_delay()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(503, null, null, TimeSpan.FromSeconds(7)).Enqueue(202);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = TestSupport.NewSender(handler, null, delays);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays.ToArray());
        }

        [Fact]
        public void Retries_exhausted_returns_error_with_exponential_backoff()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(503).Enqueue(509).Enqueue(503);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = TestSupport.NewSender(handler, null, delays);
            sender.MaxRetries = 2;
            string result = sender.Send(TestSupport.NewMessage());
            Assert.StartsWith("Microsoft Graph sendMail failed (HTTP 503", result);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays.ToArray());
        }

        [Fact]
        public void Huge_retry_after_is_capped()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(429, null, null, TimeSpan.FromSeconds(3600)).Enqueue(202);
            var delays = new List<TimeSpan>();
            GraphMailSender sender = TestSupport.NewSender(handler, null, delays);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(delays));
        }

        [Fact]
        public void MaxRetries_zero_does_not_retry_and_500_is_not_retried()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(429, null, null, TimeSpan.Zero);
            GraphMailSender sender = TestSupport.NewSender(handler);
            sender.MaxRetries = 0;
            Assert.Contains("HTTP 429", sender.Send(TestSupport.NewMessage()));
            Assert.Single(handler.Requests);

            var handler2 = new FakeHttpMessageHandler().Enqueue(500, "oops", null, null, "text/plain");
            GraphMailSender sender2 = TestSupport.NewSender(handler2);
            sender2.Send(TestSupport.NewMessage());
            Assert.Single(handler2.Requests);
        }

        [Fact]
        public void Http_401_forces_one_token_refresh()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(401, TestSupport.GraphError("InvalidAuthenticationToken", "Access token has expired.", "r")).Enqueue(202);
            var tokens = new FakeTokenProvider();
            GraphMailSender sender = TestSupport.NewSender(handler, tokens, null);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal("Bearer fake-token-1", handler.Requests[0].Authorization);
            Assert.Equal("Bearer fake-token-2", handler.Requests[1].Authorization);
            Assert.Contains(true, tokens.Calls);
            Assert.False(tokens.Calls[0]);
        }

        [Fact]
        public void Second_401_fails()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(401, TestSupport.GraphError("InvalidAuthenticationToken", "Access token is empty.", "r1"), "r1")
                .Enqueue(401, TestSupport.GraphError("InvalidAuthenticationToken", "Access token is empty.", "r2"), "r2");
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.StartsWith("Microsoft Graph sendMail failed (HTTP 401, InvalidAuthenticationToken): Access token is empty.", result);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("r2", sender.LastRequestId);
        }

        [Fact]
        public void Graph_403_error_text_has_status_code_message_request_id_and_hint()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "Access is denied. Check credentials and try again.", "7d4f-body"), "7d4f-header");
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.Equal(
                "Microsoft Graph sendMail failed (HTTP 403, ErrorAccessDenied): Access is denied. Check credentials and try again. "
                + "[request-id 7d4f-header, 2026-10-12T06:00:00Z, mailbox nav@contoso.com]" + ErrorHints.Prefix + ErrorHints.AccessDeniedHint,
                result);
            Assert.Equal("7d4f-header", sender.LastRequestId);
            Assert.Contains("FAILED: ", sender.LastDiagnostics);
        }

        [Fact]
        public void Request_id_falls_back_to_inner_error()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(404, TestSupport.GraphError("ResourceNotFound", "Resource could not be discovered.", "inner-rid"));
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.Contains("(HTTP 404, ResourceNotFound): Resource could not be discovered. [request-id inner-rid,", result);
            Assert.Contains(ErrorHints.MailboxNotFoundHint, result);
            Assert.Equal("inner-rid", sender.LastRequestId);
        }

        [Fact]
        public void Send_as_denied_gets_its_own_hint()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorSendAsDenied", "The user account which was used to submit this request does not have the right to send mail on behalf of the specified sending account.", "r"));
            GraphMailSender sender = TestSupport.NewSender(handler);
            Assert.Contains(ErrorHints.SendAsDeniedHint, sender.Send(TestSupport.NewMessage()));
        }

        [Fact]
        public void Non_json_500_is_reported_with_snippet()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(500, "<html><body>Proxy error</body></html>", null, null, "text/html");
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.StartsWith("Microsoft Graph sendMail failed (HTTP 500): ", result);
            Assert.Contains("Proxy error", result);
            Assert.Contains("mailbox nav@contoso.com]", result);
            Assert.DoesNotContain("Hint:", result);
        }

        [Fact]
        public void Payload_too_large_gets_hint()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(413, "", null, null, "text/plain");
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.Contains("HTTP 413", result);
            Assert.Contains(ErrorHints.PayloadTooLargeHint, result);
        }

        [Fact]
        public void Transport_error_is_reported_without_throwing()
        {
            var handler = new FakeHttpMessageHandler().EnqueueException(new HttpRequestException("No such host is known.", new IOException("dns\nsecond line")));
            GraphMailSender sender = TestSupport.NewSender(handler);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.StartsWith("Microsoft Graph sendMail failed: HttpRequestException: No such host is known. -> IOException: dns", result);
            Assert.DoesNotContain("second line", result);
        }

        [Fact]
        public void Token_provider_exception_is_wrapped_with_first_line_and_aadsts_hint()
        {
            var tokens = new FakeTokenProvider
            {
                ThrowOnAcquire = new InvalidOperationException("AADSTS700016: Application with identifier 'x' was not found in the directory 'y'.\r\nTrace ID: 123"),
            };
            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = TestSupport.NewSender(handler, tokens, null);
            string result = sender.Send(TestSupport.NewMessage());
            Assert.StartsWith("Token acquisition failed (InvalidOperationException): AADSTS700016: Application with identifier 'x' was not found in the directory 'y'. [provider FakeTokenProvider]", result);
            Assert.DoesNotContain("Trace ID", result);
            Assert.EndsWith(ErrorHints.Prefix + "The Client ID was not found in this tenant: check Graph Client ID and Graph Tenant ID.", result);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Errors_after_success_reset_last_request_id()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(403, TestSupport.GraphError("ErrorAccessDenied", "x", "r1"), "r1")
                .Enqueue(202, null, "r2");
            GraphMailSender sender = TestSupport.NewSender(handler);
            Assert.NotEqual("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal("r1", sender.LastRequestId);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.DoesNotContain("FAILED", sender.LastDiagnostics);
        }

        [Fact]
        public void Bearer_token_never_reaches_diagnostics_or_log_file()
        {
            using (var dir = new TempDirectory())
            {
                string log = Path.Combine(dir.Path, "nmg.log");
                var handler = new FakeHttpMessageHandler().Enqueue(202);
                GraphMailSender sender = TestSupport.NewSender(handler);
                sender.LogFilePath = log;
                Assert.Equal("", sender.Send(TestSupport.NewMessage()));
                Assert.DoesNotContain("fake-token-1", sender.LastDiagnostics);
                string file = File.ReadAllText(log);
                Assert.Contains("Send start", file);
                Assert.DoesNotContain("fake-token-1", file);
            }
        }

        [Theory]
        [InlineData("Authorization: Bearer abc.def-ghi", "Bearer [redacted]")]
        [InlineData("client_id=x&client_secret=s3cr3t&scope=y", "client_secret=[redacted]&scope=y")]
        [InlineData("client_assertion=eyJabc&x=1", "client_assertion=[redacted]&x=1")]
        [InlineData("https://outlook.office.com/api/x?authtoken=abc123", "authtoken=[redacted]")]
        [InlineData("?access_token=tok", "access_token=[redacted]")]
        [InlineData("{\"access_token\":\"tok\",\"x\":1}", "\"access_token\":\"[redacted]\"")]
        [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl end", "token [redacted-jwt] end")]
        public void Diagnostics_redaction(string input, string expectedFragment)
        {
            string redacted = Diagnostics.Redact(input);
            Assert.Contains(expectedFragment, redacted);
        }

        [Fact]
        public void SafeUrl_strips_query()
        {
            Assert.Equal("https://outlook.office.com/api/x", Diagnostics.SafeUrl("https://outlook.office.com/api/x?authtoken=abc"));
        }
    }
}
