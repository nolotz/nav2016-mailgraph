using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using NavMailGraph.Auth;
using NavMailGraph.Internal;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class BuiltInTokenProviderTests
    {
        private const string Secret = "Qx8Q~SuperSecretValue_123";

        public BuiltInTokenProviderTests()
        {
            GraphMailSender.ResetTokenCacheForTests();
        }

        private static string TokenJson(string token, int expiresIn = 3599)
        {
            return "{\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + ",\"ext_expires_in\":" + expiresIn + ",\"access_token\":\"" + token + "\"}";
        }

        private static GraphMailSender NewSecretSender(FakeHttpMessageHandler handler, string tenant)
        {
            GraphMailSender sender = GraphMailSender.CreateForTests(tenant, TestSupport.ClientId, handler);
            sender.UseClientSecret(Secret);
            sender.RetryDelayOverride = (d, ct) => System.Threading.Tasks.Task.FromResult(0);
            return sender;
        }

        private static List<RecordedRequest> TokenRequests(FakeHttpMessageHandler handler)
        {
            return handler.Requests.Where(r => r.Url.IndexOf("/oauth2/v2.0/token", StringComparison.Ordinal) >= 0).ToList();
        }

        [Fact]
        public void Client_secret_form_post_and_bearer_on_graph()
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler().Enqueue(200, TokenJson("at-1")).Enqueue(202);
            GraphMailSender sender = NewSecretSender(handler, tenant);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));

            RecordedRequest token = handler.Requests[0];
            Assert.Equal(HttpMethod.Post, token.Method);
            Assert.Equal("https://login.microsoftonline.com/" + tenant + "/oauth2/v2.0/token", token.Url);
            Assert.StartsWith("application/x-www-form-urlencoded", token.Header("Content-Type"));
            Assert.Null(token.Authorization);
            Dictionary<string, string> form = TestSupport.Form(token);
            Assert.Equal(TestSupport.ClientId, form["client_id"]);
            Assert.Equal(Secret, form["client_secret"]);
            Assert.Equal("https://graph.microsoft.com/.default", form["scope"]);
            Assert.Equal("client_credentials", form["grant_type"]);
            Assert.False(form.ContainsKey("client_assertion"));
            Assert.StartsWith("NavMailGraph/", token.Header("User-Agent"));

            Assert.Equal("Bearer at-1", handler.Requests[1].Authorization);
            Assert.DoesNotContain(Secret, sender.LastDiagnostics);
            Assert.Contains("BuiltIn/ClientSecret", sender.LastDiagnostics);
        }

        [Fact]
        public void Token_is_cached_across_sends_and_senders_and_forceRefresh_bypasses_cache()
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, TokenJson("at-1")).Enqueue(202).Enqueue(202).Enqueue(202)
                .Enqueue(200, TokenJson("at-2"));
            GraphMailSender sender = NewSecretSender(handler, tenant);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            GraphMailSender other = NewSecretSender(handler, tenant);
            Assert.Equal("", other.Send(TestSupport.NewMessage()));
            Assert.Single(TokenRequests(handler));
            Assert.Equal("Bearer at-1", handler.Requests[3].Authorization);

            Assert.Equal("", sender.TestConnection()); // forceRefresh
            Assert.Equal(2, TokenRequests(handler).Count);
        }

        [Fact]
        public void Different_secret_does_not_reuse_cached_token()
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler().Enqueue(200, TokenJson("at-1")).Enqueue(200, TokenJson("at-2"));
            GraphMailSender sender = NewSecretSender(handler, tenant);
            Assert.Equal("", sender.TestConnection());
            sender.UseClientSecret("another-secret");
            Assert.Equal("", sender.TestConnection());
            Assert.Equal(2, TokenRequests(handler).Count);
        }

        [Fact]
        public void Short_lived_token_is_refreshed()
        {
            // A token with less than 5 minutes left is never reused (TokenCache.RefreshSkew), so each acquisition hits the endpoint.
            string tenant = Guid.NewGuid().ToString("D");
            int issued = 0;
            var handler = new FakeHttpMessageHandler
            {
                Fallback = r => r.Url.IndexOf("/oauth2/v2.0/token", StringComparison.Ordinal) >= 0
                    ? FakeHttpMessageHandler.Response(200, TokenJson("at-short-" + (++issued), 120))
                    : FakeHttpMessageHandler.Response(202),
            };
            GraphMailSender sender = NewSecretSender(handler, tenant);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            int afterFirst = TokenRequests(handler).Count;
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.True(TokenRequests(handler).Count > afterFirst);
            Assert.Equal("Bearer at-short-" + issued, handler.Requests.Last().Authorization);
        }

        [Fact]
        public void Graph_401_triggers_forced_token_refresh_through_endpoint()
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler()
                .Enqueue(200, TokenJson("at-1")).Enqueue(401, TestSupport.GraphError("InvalidAuthenticationToken", "expired", "r"))
                .Enqueue(200, TokenJson("at-2")).Enqueue(202);
            GraphMailSender sender = NewSecretSender(handler, tenant);
            Assert.Equal("", sender.Send(TestSupport.NewMessage()));
            Assert.Equal(2, TokenRequests(handler).Count);
            Assert.Equal("Bearer at-2", handler.Requests[3].Authorization);
        }

        [Fact]
        public void Aadsts7000215_error_text_with_hint_and_no_secret()
        {
            string tenant = Guid.NewGuid().ToString("D");
            string body = "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000215: Invalid client secret provided. Ensure the secret being sent in the request is the client secret value, not the client secret ID, for a secret added to app '" + TestSupport.ClientId + "'.\\r\\nTrace ID: t-1\\r\\nCorrelation ID: corr-1\\r\\nTimestamp: 2026-10-12 06:00:00Z\",\"error_codes\":[7000215],\"timestamp\":\"2026-10-12 06:00:00Z\",\"trace_id\":\"t-1\",\"correlation_id\":\"corr-1\",\"error_uri\":\"https://login.microsoftonline.com/error?code=7000215\"}";
            var handler = new FakeHttpMessageHandler().Enqueue(401, body);
            GraphMailSender sender = NewSecretSender(handler, tenant);
            string result = sender.Send(TestSupport.NewMessage());

            Assert.Equal(
                "Token acquisition failed (invalid_client, AADSTS7000215): Invalid client secret provided. Ensure the secret being sent in the request is the client secret value, not the client secret ID, for a secret added to app '"
                + TestSupport.ClientId + "'. [correlation-id corr-1]" + ErrorHints.Prefix + "Wrong client secret: enter the secret's Value, not its Secret ID.",
                result);
            Assert.Equal("corr-1", sender.LastRequestId);
            Assert.DoesNotContain(Secret, result);
            Assert.DoesNotContain(Secret, sender.LastDiagnostics);
            Assert.Single(handler.Requests);

            var handler2 = new FakeHttpMessageHandler().Enqueue(401, body);
            GraphMailSender sender2 = NewSecretSender(handler2, tenant);
            string test = sender2.TestConnection();
            Assert.Equal(result, test);
            Assert.DoesNotContain(Secret, sender2.LastDiagnostics);
        }

        [Theory]
        [InlineData(400, "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS90002: Tenant 'x' not found.\"}", "Token acquisition failed (invalid_request, AADSTS90002): Tenant 'x' not found.")]
        [InlineData(502, "<html>bad gateway</html>", "Token acquisition failed (HTTP 502): <html>bad gateway</html>")]
        [InlineData(500, "", "Token acquisition failed (HTTP 500): Internal Server Error")]
        public void BuildError_formats(int status, string body, string expectedStart)
        {
            string reason = status == 500 ? "Internal Server Error" : "";
            GraphMailException ex = BuiltInTokenProvider.BuildError(status, reason, body);
            Assert.StartsWith(expectedStart, ex.Message);
            Assert.Equal(status >= 500, ex.IsTransient);
        }

        [Fact]
        public void Missing_access_token_in_success_response()
        {
            var handler = new FakeHttpMessageHandler().Enqueue(200, "{\"token_type\":\"Bearer\"}");
            GraphMailSender sender = NewSecretSender(handler, Guid.NewGuid().ToString("D"));
            Assert.StartsWith("Token acquisition failed (HTTP 200): the response contained no access_token.", sender.TestConnection());
        }

        [Theory]
        [InlineData("USGov", "https://login.microsoftonline.us/", "https://graph.microsoft.us/.default")]
        [InlineData("USGovDoD", "https://login.microsoftonline.us/", "https://dod-graph.microsoft.us/.default")]
        [InlineData("China", "https://login.chinacloudapi.cn/", "https://microsoftgraph.chinacloudapi.cn/.default")]
        [InlineData("Global", "https://login.microsoftonline.com/", "https://graph.microsoft.com/.default")]
        public void Cloud_selects_authority_and_scope(string cloud, string authority, string scope)
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler().Enqueue(200, TokenJson("at"));
            GraphMailSender sender = NewSecretSender(handler, tenant);
            sender.Cloud = cloud;
            Assert.Equal("", sender.TestConnection());
            RecordedRequest token = Assert.Single(handler.Requests);
            Assert.Equal(authority + tenant + "/oauth2/v2.0/token", token.Url);
            Assert.Equal(scope, TestSupport.Form(token)["scope"]);
        }

        [Fact]
        public void Explicit_authority_and_graph_base_url_override_cloud()
        {
            string tenant = Guid.NewGuid().ToString("D");
            var handler = new FakeHttpMessageHandler().Enqueue(200, TokenJson("at"));
            GraphMailSender sender = NewSecretSender(handler, tenant);
            sender.AuthorityHost = "https://login.example.test/";
            sender.GraphBaseUrl = "https://graph.example.test/v1.0";
            Assert.Equal("", sender.TestConnection());
            Assert.Equal("https://login.example.test/" + tenant + "/oauth2/v2.0/token", handler.Requests[0].Url);
            Assert.Equal("https://graph.example.test/.default", TestSupport.Form(handler.Requests[0])["scope"]);
        }

        [Fact]
        public void Describe_and_access_token_hide_secrets()
        {
            var settings = new TokenProviderSettings
            {
                TenantId = "t",
                ClientId = "c",
                AuthorityHost = "https://login.microsoftonline.com",
                Scope = "https://graph.microsoft.com/.default",
                ClientSecret = Secret,
            };
            var provider = new BuiltInTokenProvider(settings, new HttpClient(new FakeHttpMessageHandler()), null, 5);
            Assert.DoesNotContain(Secret, provider.Describe());
            Assert.DoesNotContain(Secret, provider.CacheKey);
            Assert.Equal(5, provider.TimeoutSeconds);
            Assert.Equal("https://login.microsoftonline.com/t/oauth2/v2.0/token", provider.TokenEndpoint);

            var token = new AccessToken("very-secret-token", new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero));
            Assert.Equal("AccessToken(expires 2026-10-12T06:00:00Z)", token.ToString());
            Assert.Throws<ArgumentNullException>(() => new AccessToken(null!, DateTimeOffset.UtcNow));
        }

        [Fact]
        public void GraphMailException_properties()
        {
            var ex = new GraphMailException("m", "ErrorAccessDenied", 403, "rid", false);
            Assert.Equal("m", ex.Message);
            Assert.Equal("ErrorAccessDenied", ex.ErrorCode);
            Assert.Equal(403, ex.StatusCode);
            Assert.Equal("rid", ex.RequestId);
            Assert.False(ex.IsTransient);
            var simple = new GraphMailException("x");
            Assert.Equal(0, simple.StatusCode);
            Assert.Equal("", simple.RequestId);
            var inner = new InvalidOperationException("i");
            Assert.Same(inner, new GraphMailException("x", null!, 0, null!, true, inner).InnerException);
        }
    }
}
