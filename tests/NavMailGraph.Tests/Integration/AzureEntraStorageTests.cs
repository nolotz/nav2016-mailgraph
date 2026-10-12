using System;
using System.Linq;
using System.Net.Http;
using NavMailGraph.Storage;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Storage;
using Xunit;

namespace NavMailGraph.Tests.Integration
{
    public class AzureEntraStorageTests
    {
        private static ObjectStorageClient NewEntraClient(FakeHttpMessageHandler storageHandler, GraphConnection connection)
        {
            ObjectStorageClient client = StorageTestSupport.NewClient(storageHandler, null);
            client.UseAzureBlobEntra(connection, "navarchive", "docs");
            return client;
        }

        [Fact]
        public void Requests_carry_the_bearer_token_and_api_version_without_shared_key()
        {
            var storage = new FakeHttpMessageHandler();
            storage.Enqueue(r => StorageTestSupport.Empty(201, "ETag: \"0x1\""));
            var tokens = new FakeTokenProvider();
            ObjectStorageClient client = NewEntraClient(storage, IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler(), tokens));

            Assert.Equal("", client.UploadText("Hallo", "a/b.txt", ""));

            RecordedRequest put = storage.Requests.Single();
            Assert.Equal(HttpMethod.Put, put.Method);
            Assert.Equal("https://navarchive.blob.core.windows.net/docs/a/b.txt", put.Uri!.AbsoluteUri);
            Assert.Equal("Bearer fake-token-1", put.Authorization);
            Assert.Equal(ObjectStorageClient.DefaultAzureApiVersion, put.Header("x-ms-version"));
            Assert.Equal("BlockBlob", put.Header("x-ms-blob-type"));
            Assert.DoesNotContain("SharedKey", put.Header("Authorization"));
            Assert.Equal(new[] { false }, tokens.Calls);
            Assert.DoesNotContain("fake-token-1", client.LastDiagnostics);
            Assert.Contains("Entra", client.Describe());
            Assert.Contains("scope=https://storage.azure.com/.default", client.Describe());
        }

        [Fact]
        public void A_401_refreshes_the_token_once()
        {
            var storage = new FakeHttpMessageHandler();
            storage.Enqueue(r => StorageTestSupport.Xml(401, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>InvalidAuthenticationInfo</Code><Message>Lifetime validation failed.</Message></Error>"));
            storage.Enqueue(r => StorageTestSupport.Empty(200, "Content-Length: 5"));
            var tokens = new FakeTokenProvider();
            ObjectStorageClient client = NewEntraClient(storage, IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler(), tokens));

            Assert.True(client.Exists("x.txt"), client.LastError);
            Assert.Equal(new[] { false, true }, tokens.Calls);
            Assert.Equal("Bearer fake-token-1", storage.Requests[0].Authorization);
            Assert.Equal("Bearer fake-token-2", storage.Requests[1].Authorization);
        }

        [Fact]
        public void GetDownloadUrl_is_not_available_in_entra_mode()
        {
            var storage = new FakeHttpMessageHandler();
            ObjectStorageClient client = NewEntraClient(storage, IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler()));

            Assert.Equal("", client.GetDownloadUrl("x.pdf", 60));
            Assert.Contains("not available in Entra ID mode", client.LastError);
            Assert.Contains("UseAzureBlobSharedKey", client.LastError);
            Assert.Empty(storage.Requests);
        }

        [Fact]
        public void Built_in_provider_requests_a_storage_token_and_reuses_it()
        {
            GraphMailSender.ResetTokenCacheForTests();
            try
            {
                var graph = new FakeHttpMessageHandler();
                graph.Enqueue(200, "{\"token_type\":\"Bearer\",\"expires_in\":3599,\"ext_expires_in\":3599,\"access_token\":\"storage-token-abc\"}");
                GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, graph);
                connection.UseClientSecret("very-secret-value");
                var storage = new FakeHttpMessageHandler();
                storage.Fallback = r => StorageTestSupport.Empty(201, "ETag: \"0x1\"");
                ObjectStorageClient client = NewEntraClient(storage, connection);

                Assert.Equal("", client.UploadText("1", "one.txt", ""));
                Assert.Equal("", client.UploadText("2", "two.txt", ""));

                RecordedRequest token = graph.Requests.Single();
                Assert.Equal("https://login.microsoftonline.com/" + TestSupport.Tenant + "/oauth2/v2.0/token", token.Url);
                var form = TestSupport.Form(token);
                Assert.Equal("https://storage.azure.com/.default", form["scope"]);
                Assert.Equal("client_credentials", form["grant_type"]);
                Assert.All(storage.Requests, r => Assert.Equal("Bearer storage-token-abc", r.Authorization));
                Assert.DoesNotContain("very-secret-value", client.Describe());
                Assert.DoesNotContain("very-secret-value", client.LastDiagnostics);
                Assert.Contains("tenant=" + TestSupport.Tenant, client.Describe());
            }
            finally
            {
                GraphMailSender.ResetTokenCacheForTests();
            }
        }

        [Fact]
        public void Token_errors_are_returned_with_a_hint_and_without_secrets()
        {
            GraphMailSender.ResetTokenCacheForTests();
            try
            {
                var graph = new FakeHttpMessageHandler();
                graph.Enqueue(401, "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000215: Invalid client secret provided. Trace ID: t Correlation ID: c Timestamp: 2026-10-12 08:00:00Z\",\"error_codes\":[7000215],\"correlation_id\":\"corr-1\"}");
                GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, graph);
                connection.UseClientSecret("very-secret-value");
                var storage = new FakeHttpMessageHandler();
                ObjectStorageClient client = NewEntraClient(storage, connection);

                string error = client.UploadText("1", "one.txt", "");
                Assert.Contains("AADSTS7000215", error);
                Assert.Contains("Hint:", error);
                Assert.DoesNotContain("very-secret-value", error);
                Assert.Empty(storage.Requests);
            }
            finally
            {
                GraphMailSender.ResetTokenCacheForTests();
            }
        }

        [Fact]
        public void Missing_credential_is_reported_by_the_first_operation()
        {
            GraphConnection connection = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, new FakeHttpMessageHandler());
            var storage = new FakeHttpMessageHandler();
            ObjectStorageClient client = NewEntraClient(storage, connection);

            Assert.Contains("no credential configured", client.UploadText("1", "one.txt", ""));
            Assert.Empty(storage.Requests);
        }

        [Fact]
        public void China_cloud_uses_the_china_blob_suffix_and_usgov_is_rejected()
        {
            var storage = new FakeHttpMessageHandler();
            storage.Enqueue(r => StorageTestSupport.Empty(201));
            GraphConnection china = IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler());
            china.Cloud = "China";
            china.UseClientSecret("s");
            china.UseTokenProvider(new FakeTokenProvider());
            ObjectStorageClient client = StorageTestSupport.NewClient(storage, null);
            client.UseAzureBlobEntra(china, "navarchive", "docs");
            Assert.Equal("", client.UploadText("x", "x.txt", ""));
            Assert.Equal("https://navarchive.blob.core.chinacloudapi.cn/docs/x.txt", storage.Requests.Single().Uri!.AbsoluteUri);

            GraphConnection usGov = GraphConnection.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, new FakeHttpMessageHandler());
            usGov.Cloud = "USGov";
            usGov.UseClientSecret("s");
            var storage2 = new FakeHttpMessageHandler();
            ObjectStorageClient gov = StorageTestSupport.NewClient(storage2, null);
            gov.UseAzureBlobEntra(usGov, "navarchive", "docs");
            string error = gov.UploadText("x", "x.txt", "");
            Assert.Contains("Global and China only", error);
            Assert.Contains("UseAzureBlobSharedKey", error);
            Assert.Empty(storage2.Requests);
            Assert.Contains("invalid configuration", gov.Describe());
        }

        [Fact]
        public void Old_api_versions_are_rejected_and_account_settings_are_required()
        {
            var storage = new FakeHttpMessageHandler();
            ObjectStorageClient client = NewEntraClient(storage, IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler()));
            client.AzureApiVersion = "2017-04-17";
            Assert.Contains("2017-11-09 or later", client.UploadText("x", "x.txt", ""));
            Assert.Empty(storage.Requests);

            ObjectStorageClient incomplete = StorageTestSupport.NewClient(storage, null);
            incomplete.UseAzureBlobEntra(IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler()), "", "docs");
            Assert.Contains("account name and container are required", incomplete.TestConnection());
            Assert.Throws<ArgumentNullException>(() => incomplete.UseAzureBlobEntra(null!, "a", "b"));
        }

        [Fact]
        public void Service_url_override_works_for_entra_mode()
        {
            var storage = new FakeHttpMessageHandler();
            storage.Enqueue(r => StorageTestSupport.Empty(202));
            ObjectStorageClient client = StorageTestSupport.NewClient(storage, null);
            client.ServiceUrl = "https://127.0.0.1:10000/devstoreaccount1";
            client.UseAzureBlobEntra(IntegrationTestSupport.NewConnection(new FakeHttpMessageHandler()), "devstoreaccount1", "docs");
            Assert.Equal("", client.Delete("old.txt"));
            Assert.Equal("https://127.0.0.1:10000/devstoreaccount1/docs/old.txt", storage.Requests.Single().Uri!.AbsoluteUri);
            Assert.Equal(AzureEntraAuth.StorageScope, "https://storage.azure.com/.default");
        }
    }
}
