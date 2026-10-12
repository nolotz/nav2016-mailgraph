using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NavMailGraph.Tests.Infrastructure;
using NavMailGraph.Tests.Integration;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    /// <summary>
    /// End-to-end pipe: a fake Graph $value response (in-process handler, 20 MiB) is streamed by
    /// GraphMailbox.CopyAttachmentToStorage into real Azurite / SeaweedFS (tests/integration/docker-compose.yml).
    /// </summary>
    [Trait("Category", "Integration")]
    public class GraphToStorageIntegrationTests
    {
        private const int MiB = 1024 * 1024;

        public static IEnumerable<object[]> Cases()
        {
            yield return new object[] { "azure", false };
            yield return new object[] { "azure", true };
            yield return new object[] { "s3", false };
            yield return new object[] { "s3", true };
        }

        [IntegrationTheory]
        [MemberData(nameof(Cases))]
        public async Task Graph_value_is_piped_into_storage_without_temp_file(string provider, bool withContentLength)
        {
            ObjectStorageClient storage = provider == "s3" ? await IntegrationEnv.S3Async() : await IntegrationEnv.AzureAsync();
            storage.MultipartThresholdBytes = 8 * MiB;
            storage.PartSizeBytes = 5 * MiB;
            storage.KeyPrefix = "graph-pipe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/";

            byte[] data = new byte[20 * MiB + 777];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(data);
            }

            var graph = new FakeHttpMessageHandler().Enqueue(r => IntegrationTestSupport.Value(data, "application/pdf", withContentLength));
            var mailbox = new GraphMailbox(IntegrationTestSupport.NewConnection(graph), TestSupport.Mailbox);
            string key = "INBOX/2026/10/12/1/Scan 20MiB.pdf";

            string error = mailbox.CopyAttachmentToStorage("m1", "a1", storage, key);
            Assert.True(error.Length == 0, error + "\n" + mailbox.LastDiagnostics + "\n" + storage.LastDiagnostics);
            Assert.Equal(key, mailbox.LastStorageKey);
            Assert.Equal(data.LongLength, mailbox.LastStorageSize);
            Assert.Contains("in 5 parts", storage.LastDiagnostics);

            Assert.Equal(data.LongLength, storage.GetSize(key));
            using (var target = new MemoryStream())
            {
                Assert.Equal("", storage.DownloadToStream(key, target));
                Assert.Equal(Sha256(data), Sha256(target.ToArray()));
            }

            Assert.Equal("", storage.Delete(key));
        }

        private static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(data));
            }
        }
    }
}
