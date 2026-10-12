using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Internal;
using NavMailGraph.Storage;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    /// <summary>[Fact] that is skipped unless NMG_INTEGRATION=1 (see tests/integration/README.md).</summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public IntegrationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("NMG_INTEGRATION") != "1")
            {
                Skip = "integration test: start tests/integration/docker-compose.yml and set NMG_INTEGRATION=1";
            }
        }
    }

    /// <summary>Endpoints and test credentials of tests/integration/docker-compose.yml (overridable through environment variables).</summary>
    internal static class IntegrationEnv
    {
        internal static readonly string AzuriteUrl = Env("NMG_AZURITE_URL", "http://127.0.0.1:10000/devstoreaccount1");

        internal const string AzuriteAccount = "devstoreaccount1";

        internal static readonly string S3Url = Env("NMG_S3_URL", "http://127.0.0.1:8333");

        internal static readonly string S3AccessKey = Env("NMG_S3_ACCESS_KEY", "nmgtestaccesskey");

        internal static readonly string S3SecretKey = Env("NMG_S3_SECRET_KEY", "nmgtestsecretkey0123456789");

        internal static readonly string S3Region = Env("NMG_S3_REGION", "us-east-1");

        /// <summary>One container/bucket per test run (lowercase, 3-63 chars).</summary>
        internal static readonly string RunName = "nmg-it-" + DateTime.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

        private static readonly SemaphoreSlim Sync = new SemaphoreSlim(1, 1);

        private static bool _azureReady;

        private static bool _s3Ready;

        internal static async Task<ObjectStorageClient> AzureAsync()
        {
            await EnsureAzureContainerAsync().ConfigureAwait(false);
            var client = new ObjectStorageClient();
            client.UseAzureBlobSharedKey(AzuriteAccount, StorageTestSupport.DevStoreKey, RunName);
            client.ServiceUrl = AzuriteUrl;
            client.MaxRetries = 1;
            client.TimeoutSeconds = 60;
            return client;
        }

        internal static async Task<ObjectStorageClient> S3Async()
        {
            await EnsureS3BucketAsync().ConfigureAwait(false);
            var client = new ObjectStorageClient();
            client.UseS3(S3Url, S3Region, RunName, S3AccessKey, S3SecretKey);
            client.MaxRetries = 1;
            client.TimeoutSeconds = 60;
            return client;
        }

        /// <summary>Container SAS URL (sr=c, sp=racwdl, 1 h) signed with the Azurite account key, for the SAS-URL mode tests.</summary>
        internal static string AzureContainerSasUrl()
        {
            string se = AzureSigning.SasTime(DateTime.UtcNow.AddHours(1));
            const string sv = ObjectStorageClient.DefaultAzureApiVersion;
            string stringToSign = AzureSigning.ServiceSasStringToSign("racwdl", "", se, AzureSigning.SasCanonicalizedResource(AzuriteAccount, RunName, ""), "", "",
                "https,http", sv, "c", "", "", "", "", "", "", "");
            string sig = AzureSigning.Sign(Convert.FromBase64String(StorageTestSupport.DevStoreKey), stringToSign);
            return AzuriteUrl + "/" + RunName + "?sv=" + sv + "&sp=racwdl&se=" + StorageKeys.UriEncode(se, true) + "&sr=c&spr=https%2Chttp&sig=" + StorageKeys.UriEncode(sig, true);
        }

        private static async Task EnsureAzureContainerAsync()
        {
            await Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_azureReady)
                {
                    return;
                }

                var client = new ObjectStorageClient();
                client.UseAzureBlobSharedKey(AzuriteAccount, StorageTestSupport.DevStoreKey, RunName);
                client.ServiceUrl = AzuriteUrl;
                var backend = (AzureBlobBackend)client.CreateBackend();
                using (HttpRequestMessage request = await backend.BuildAsync(HttpMethod.Put, backend.ContainerUrl, "", new[] { new KeyValuePair<string, string>("restype", "container") },
                    () => new ByteArrayContent(new byte[0]), new KeyValuePair<string, string>[0], false, CancellationToken.None).ConfigureAwait(false))
                using (HttpResponseMessage response = await HttpClientProvider.Client.SendAsync(request).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status != 201 && status != 409)
                    {
                        throw new InvalidOperationException("Create container failed: HTTP " + status + " " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }

                _azureReady = true;
            }
            finally
            {
                Sync.Release();
            }
        }

        private static async Task EnsureS3BucketAsync()
        {
            await Sync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_s3Ready)
                {
                    return;
                }

                var client = new ObjectStorageClient();
                client.UseS3(S3Url, S3Region, RunName, S3AccessKey, S3SecretKey);
                var backend = (S3Backend)client.CreateBackend();
                using (HttpRequestMessage request = backend.Build(HttpMethod.Put, backend.BucketPath, "", new KeyValuePair<string, string?>[0], () => new ByteArrayContent(new byte[0]), SigV4.EmptyPayloadHash))
                using (HttpResponseMessage response = await HttpClientProvider.Client.SendAsync(request).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status != 200 && status != 409)
                    {
                        throw new InvalidOperationException("Create bucket failed: HTTP " + status + " " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }

                _s3Ready = true;
            }
            finally
            {
                Sync.Release();
            }
        }

        private static string Env(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value!;
        }
    }

    /// <summary>
    /// End-to-end tests against Azurite (Azure Blob, Shared Key and SAS URL) and SeaweedFS (S3, SigV4). Shared scenarios run
    /// for both providers; each test uses its own key prefix.
    /// </summary>
    [Trait("Category", "Integration")]
    public class StorageIntegrationTests
    {
        private const int MiB = 1024 * 1024;

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { "azure" };
            yield return new object[] { "azure-sas" };
            yield return new object[] { "s3" };
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Small_upload_download_roundtrip(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            string key = Prefix("small") + "Grüße & Rechnung (1) $+=.txt";
            const string text = "Hallo NAV – Grüße aus dem Speicher.\r\nZeile 2";
            AssertOk(client, client.UploadText(text, key, ""));

            using (var target = new MemoryStream())
            {
                AssertOk(client, client.DownloadToStream(key, target));
                Assert.Equal(text, new UTF8Encoding(false).GetString(target.ToArray()));
            }

            Assert.True(client.Exists(key), client.LastError);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), client.GetSize(key));
            AssertOk(client, client.Delete(key));
            Assert.False(client.Exists(key));
            Assert.Equal("", client.LastError);
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Large_file_uses_blocks_or_multipart_and_downloads_to_file_with_same_hash(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            client.MultipartThresholdBytes = 5 * MiB;
            client.PartSizeBytes = 5 * MiB;
            string dir = Path.Combine(Path.GetTempPath(), "nmg-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string source = Path.Combine(dir, "source.bin");
                byte[] data = RandomBytes(20 * MiB + 4321);
                File.WriteAllBytes(source, data);
                string key = Prefix("large") + "archive 20MiB.bin";

                AssertOk(client, client.UploadFile(source, key, ""));
                Assert.Contains("in 5 parts", client.LastDiagnostics);
                Assert.Equal(data.Length, client.GetSize(key));

                string target = Path.Combine(dir, "target.bin");
                AssertOk(client, client.DownloadToFile(key, target));
                Assert.Equal(Sha256(data), Sha256(File.ReadAllBytes(target)));
                AssertOk(client, client.Delete(key));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Non_seekable_stream_of_unknown_length_is_uploaded_part_by_part(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            client.PartSizeBytes = 5 * MiB;
            byte[] data = RandomBytes(11 * MiB + 17);
            string key = Prefix("pipe") + "piped.bin";

            StorageUploadResult result = await client.UploadFromStreamAsync(new NonSeekableStream(data), -1, key, "application/pdf", CancellationToken.None);
            Assert.Equal(3, result.Parts);
            Assert.Equal(data.Length, result.Size);

            using (var target = new MemoryStream())
            {
                AssertOk(client, client.DownloadToStream(key, target));
                Assert.Equal(Sha256(data), Sha256(target.ToArray()));
            }

            AssertOk(client, client.Delete(key));
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Exists_size_list_with_paging_and_delete(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            string prefix = Prefix("list");
            client.KeyPrefix = prefix;
            client.ListPageSize = 2;
            string[] keys = { "a/1.txt", "a/2.txt", "a/3 drei.txt", "b/4.txt", "a/5.txt" };
            for (int i = 0; i < keys.Length; i++)
            {
                AssertOk(client, client.UploadText(new string('x', i + 1), keys[i], ""));
            }

            AssertOk(client, client.List("a/"));
            Assert.Equal(4, client.ListCount);
            var listed = Enumerable.Range(0, client.ListCount).ToDictionary(client.GetListedKey, client.GetListedSize);
            Assert.Equal(new[] { "a/1.txt", "a/2.txt", "a/3 drei.txt", "a/5.txt" }, listed.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(3, listed["a/3 drei.txt"]);
            Assert.Equal(5, listed["a/5.txt"]);

            AssertOk(client, client.List(""));
            Assert.Equal(5, client.ListCount);

            Assert.True(client.Exists("b/4.txt"));
            Assert.Equal(4, client.GetSize("b/4.txt"));
            Assert.Equal(-1, client.GetSize("b/missing.txt"));
            Assert.Equal("", client.LastError);

            foreach (string key in keys)
            {
                AssertOk(client, client.Delete(key));
            }

            AssertOk(client, client.Delete("a/1.txt")); // missing object = success
            AssertOk(client, client.List(""));
            Assert.Equal(0, client.ListCount);
            AssertOk(client, client.TestConnection());
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Download_url_works_with_a_plain_http_client(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            string key = Prefix("url") + "Beleg 4711 ä.pdf";
            byte[] data = RandomBytes(100000);
            using (var source = new MemoryStream(data))
            {
                AssertOk(client, client.UploadStream(source, key, ""));
            }

            string url = client.GetDownloadUrl(key, 10);
            Assert.True(url.Length > 0, client.LastError);
            using (var http = new HttpClient())
            using (HttpResponseMessage response = await http.GetAsync(url))
            {
                byte[] body = await response.Content.ReadAsByteArrayAsync();
                Assert.True(response.IsSuccessStatusCode, "HTTP " + (int)response.StatusCode + ": " + Encoding.UTF8.GetString(body));
                Assert.Equal(Sha256(data), Sha256(body));
                Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
            }

            AssertOk(client, client.Delete(key));
        }

        [IntegrationFact]
        public async Task Azure_wrong_key_gives_a_readable_error()
        {
            await IntegrationEnv.AzureAsync();
            var client = new ObjectStorageClient();
            client.UseAzureBlobSharedKey(IntegrationEnv.AzuriteAccount, Convert.ToBase64String(Encoding.ASCII.GetBytes("definitely-the-wrong-account-key-0123456789")), IntegrationEnv.RunName);
            client.ServiceUrl = IntegrationEnv.AzuriteUrl;
            string error = client.TestConnection();
            // Azure documents AuthenticationFailed; Azurite 3.37.0 answers AuthorizationFailure with the same message.
            Assert.True(error.StartsWith("Azure Blob list failed (HTTP 403, AuthenticationFailed): Server failed to authenticate the request.", StringComparison.Ordinal)
                || error.StartsWith("Azure Blob list failed (HTTP 403, AuthorizationFailure): Server failed to authenticate the request.", StringComparison.Ordinal), error);
            Assert.Contains("Hint: Azure rejected the signature", error);
            Assert.Contains("request-id ", error);
            Assert.False(client.Exists("x"));
            Assert.Contains("HTTP 403", client.LastError);
        }

        [IntegrationFact]
        public async Task S3_wrong_secret_gives_a_readable_error()
        {
            await IntegrationEnv.S3Async();
            var client = new ObjectStorageClient();
            client.UseS3(IntegrationEnv.S3Url, IntegrationEnv.S3Region, IntegrationEnv.RunName, IntegrationEnv.S3AccessKey, "wrong-secret");
            string error = client.UploadText("x", "wrong.txt", "");
            Assert.StartsWith("S3 upload failed (HTTP 403, SignatureDoesNotMatch): ", error);
            Assert.Contains("Hint: The S3 server rejected the signature", error);
            Assert.DoesNotContain("wrong-secret", error + client.LastDiagnostics);

            client.UseS3(IntegrationEnv.S3Url, IntegrationEnv.S3Region, IntegrationEnv.RunName, "UNKNOWNKEY", "wrong-secret");
            Assert.Contains("HTTP 403", client.TestConnection());
        }

        [IntegrationFact]
        public async Task Missing_container_and_bucket_give_a_hint()
        {
            ObjectStorageClient azure = await IntegrationEnv.AzureAsync();
            azure.UseAzureBlobSharedKey(IntegrationEnv.AzuriteAccount, StorageTestSupport.DevStoreKey, "nmg-missing-container");
            string error = azure.TestConnection();
            Assert.Contains("ContainerNotFound", error);
            Assert.Contains("does not exist", error);

            ObjectStorageClient s3 = await IntegrationEnv.S3Async();
            s3.UseS3(IntegrationEnv.S3Url, IntegrationEnv.S3Region, "nmg-missing-bucket", IntegrationEnv.S3AccessKey, IntegrationEnv.S3SecretKey);
            error = s3.TestConnection();
            Assert.Contains("NoSuchBucket", error);
            Assert.Contains("does not exist", error);

            // A HEAD 404 has no body: Exists/GetSize must still tell a missing bucket apart from a missing object.
            Assert.False(s3.Exists("a"));
            Assert.Contains("NoSuchBucket", s3.LastError);
            Assert.Equal(-1, s3.GetSize("a"));
            Assert.Contains("NoSuchBucket", s3.LastError);
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Keys_with_leading_and_trailing_spaces_list_verbatim(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            string prefix = Prefix("space");
            client.KeyPrefix = prefix;
            string[] keys = { " lead and trail ", "trail ", "dir/ x " };
            foreach (string key in keys)
            {
                AssertOk(client, client.UploadText("v", key, ""));
            }

            AssertOk(client, client.List(""));
            string[] listed = Enumerable.Range(0, client.ListCount).Select(client.GetListedKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), listed);
            foreach (string key in listed)
            {
                Assert.True(client.Exists(key), "'" + key + "': " + client.LastError);
                AssertOk(client, client.Delete(key));
            }
        }

        [IntegrationTheory]
        [MemberData(nameof(Providers))]
        public async Task Failed_download_to_file_keeps_an_existing_file(string provider)
        {
            ObjectStorageClient client = await NewClientAsync(provider);
            string dir = Path.Combine(Path.GetTempPath(), "nmg-int-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "existing.pdf");
                File.WriteAllText(file, "keep me");
                string error = client.DownloadToFile(Prefix("missing") + "nope.pdf", file);
                Assert.Contains("404", error);
                Assert.Equal("keep me", File.ReadAllText(file));
                Assert.Single(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static async Task<ObjectStorageClient> NewClientAsync(string provider)
        {
            switch (provider)
            {
                case "azure":
                    return await IntegrationEnv.AzureAsync();
                case "azure-sas":
                    await IntegrationEnv.AzureAsync();
                    var sas = new ObjectStorageClient();
                    sas.UseAzureBlobSasUrl(IntegrationEnv.AzureContainerSasUrl());
                    sas.MaxRetries = 1;
                    return sas;
                default:
                    return await IntegrationEnv.S3Async();
            }
        }

        private static string Prefix(string test)
        {
            return test + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/";
        }

        private static void AssertOk(ObjectStorageClient client, string result)
        {
            Assert.True(result.Length == 0, result + "\n--- diagnostics ---\n" + client.LastDiagnostics);
        }

        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            new Random(length).NextBytes(bytes);
            return bytes;
        }

        private static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(data));
            }
        }
    }

    /// <summary>[Theory] that is skipped unless NMG_INTEGRATION=1.</summary>
    public sealed class IntegrationTheoryAttribute : TheoryAttribute
    {
        public IntegrationTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("NMG_INTEGRATION") != "1")
            {
                Skip = "integration test: start tests/integration/docker-compose.yml and set NMG_INTEGRATION=1";
            }
        }
    }
}
