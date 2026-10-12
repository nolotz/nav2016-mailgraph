using NavMailGraph.Internal;
using NavMailGraph.Reading;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests.Reading
{
    public class ConnectionAndHintTests
    {
        [Fact]
        public void Sender_UseConnection_copies_credentials_and_cloud()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(202);
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            var tokens = new FakeTokenProvider();
            connection.UseTokenProvider(tokens);
            connection.Cloud = "USGov";
            connection.TimeoutSeconds = 42;
            connection.MaxRetries = 1;

            var sender = new GraphMailSender("", "");
            sender.UseConnection(connection);
            Assert.Equal("USGov", sender.Cloud);
            Assert.Equal(42, sender.TimeoutSeconds);
            Assert.Equal(1, sender.MaxRetries);

            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal("https://graph.microsoft.us/v1.0/users/nav@contoso.com/sendMail", request.Url);
            Assert.Equal("Bearer fake-token-1", request.Authorization);
        }

        [Fact]
        public void Sender_without_UseConnection_is_unchanged()
        {
            var sender = new GraphMailSender("t", "c");
            Assert.Equal("Global", sender.Cloud);
            Assert.Equal(100, sender.TimeoutSeconds);
        }

        [Fact]
        public void Connection_TestConnection_uses_provider()
        {
            var handler = new FakeHttpMessageHandler();
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            var tokens = new FakeTokenProvider();
            connection.UseTokenProvider(tokens);
            Assert.Equal("", connection.TestConnection());
            Assert.Equal(new[] { true }, tokens.Calls);
        }

        [Fact]
        public void Connection_without_credential_reports_validation_error()
        {
            var connection = new GraphConnection("contoso.onmicrosoft.com", "client");
            Assert.StartsWith("Validation failed: no credential configured", connection.TestConnection());
            var mailbox = new GraphMailbox(connection, "nav@contoso.com");
            Assert.StartsWith("Validation failed: no credential configured", mailbox.Fetch());
        }

        [Fact]
        public void Secret_never_appears_in_mailbox_diagnostics()
        {
            var handler = new FakeHttpMessageHandler()
                .Enqueue(400, "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000215: Invalid client secret provided.\"}");
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            connection.UseClientSecret("SuperSecret~Value123");
            connection.MaxRetries = 0;
            var mailbox = new GraphMailbox(connection, TestSupport.Mailbox);

            string error = mailbox.Fetch();
            Assert.Contains("AADSTS7000215", error);
            Assert.Contains("Hint: ", error);
            Assert.DoesNotContain("SuperSecret", error);
            Assert.DoesNotContain("SuperSecret", mailbox.LastDiagnostics);
            Assert.DoesNotContain("SuperSecret", connection.LastDiagnostics);
        }

        [Fact]
        public void Read_hints()
        {
            Assert.Equal(ErrorHints.MailReadHint, ErrorHints.GetReadHint("ErrorAccessDenied", 403, false, ""));
            Assert.Equal(ErrorHints.MailReadWriteForMailboxHint, ErrorHints.GetReadHint("ErrorAccessDenied", 403, true, ""));
            Assert.Equal(ErrorHints.MailReadWriteForMailboxHint, ErrorHints.GetReadHint("", 403, true, ""));
            Assert.Contains("Application Mail.Read'", ErrorHints.MailReadHint);
            Assert.Contains("Application Mail.ReadWrite'", ErrorHints.MailReadWriteForMailboxHint);
            Assert.Equal(ErrorHints.ReadNotFoundHint, ErrorHints.GetReadHint("ErrorItemNotFound", 404, false, ""));
            Assert.Equal(ErrorHints.ReadMailboxNotFoundHint, ErrorHints.GetReadHint("MailboxNotEnabledForRESTAPI", 404, false, ""));
            Assert.Equal(ErrorHints.InefficientFilterHint, ErrorHints.GetReadHint("InefficientFilter", 400, false, ""));
            Assert.Equal(ErrorHints.DeltaExpiredHint, ErrorHints.GetReadHint("syncStateNotFound", 400, false, ""));
            Assert.Equal(ErrorHints.DeltaExpiredHint, ErrorHints.GetReadHint("", 410, false, ""));
            Assert.Equal("", ErrorHints.GetReadHint(GraphMailException.ValidationCode, 0, false, ""));
            Assert.Equal("", ErrorHints.GetReadHint("IO", 0, false, ""));
            Assert.Equal(ErrorHints.GetHint("AADSTS7000222", 401, false, ""), ErrorHints.GetReadHint("AADSTS7000222", 401, false, ""));
        }

        [Fact]
        public void Concurrency_gate_is_shared_per_mailbox_with_four_slots()
        {
            string key = GraphReadClient.GateKey("https://graph.microsoft.com/v1.0", "Gate@Contoso.com");
            Assert.Equal("graph.microsoft.com|gate@contoso.com", key);
            Assert.Same(GraphReadClient.GateFor(key), GraphReadClient.GateFor(GraphReadClient.GateKey("https://GRAPH.microsoft.com/v1.0", "gate@contoso.com")));
            Assert.Equal(4, GraphReadClient.GateFor(key).CurrentCount);
        }

        [Fact]
        public void Gate_is_released_after_each_request()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(500, "x").Enqueue(200, ReadingTestSupport.Page(null));
            GraphMailbox mailbox = ReadingTestSupport.NewMailbox(handler);
            Assert.NotEqual("", mailbox.Fetch());
            Assert.Equal("", mailbox.Fetch());
            Assert.Equal(4, GraphReadClient.GateFor(GraphReadClient.GateKey(TestSupport.GraphBase, TestSupport.Mailbox)).CurrentCount);
        }
    }
}
