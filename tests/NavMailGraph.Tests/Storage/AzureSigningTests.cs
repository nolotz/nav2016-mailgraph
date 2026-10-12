using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NavMailGraph.Storage;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    /// <summary>Azure Shared Key StringToSign and service SAS construction (rules quoted in docs/research-notes-v2.md).</summary>
    public class AzureSigningTests
    {
        [Fact]
        public void Canonicalized_resource_matches_the_documented_example()
        {
            // /myaccount/mycontainer\ncomp:list\ninclude:metadata,snapshots,uncommittedblobs\nrestype:container
            string resource = AzureSigning.CanonicalizedResource("myaccount", "/mycontainer", new[]
            {
                P("restype", "container"),
                P("include", "uncommittedblobs"),
                P("comp", "list"),
                P("include", "metadata"),
                P("Include", "snapshots"),
            });
            Assert.Equal("/myaccount/mycontainer\ncomp:list\ninclude:metadata,snapshots,uncommittedblobs\nrestype:container", resource);
        }

        [Fact]
        public void Canonicalized_resource_for_the_emulator_contains_the_account_twice()
        {
            Assert.Equal("/devstoreaccount1/devstoreaccount1/docs/a%20b.txt",
                AzureSigning.CanonicalizedResource("devstoreaccount1", "/devstoreaccount1/docs/a%20b.txt", new KeyValuePair<string, string>[0]));
        }

        [Fact]
        public void Canonicalized_headers_lowercase_sorted_whitespace_collapsed_empty_values_kept()
        {
            string headers = AzureSigning.CanonicalizedHeaders(new[]
            {
                P("x-ms-version", "2026-04-06"),
                P("X-MS-Date", "Mon, 12 Oct 2026 08:00:00 GMT"),
                P("x-ms-meta-Note", "  a \t  b  "),
                P("x-ms-meta-empty", ""),
                P("Content-Type", "text/plain"),
                P("x-ms-blob-type", "BlockBlob"),
            });
            Assert.Equal(
                "x-ms-blob-type:BlockBlob\nx-ms-date:Mon, 12 Oct 2026 08:00:00 GMT\nx-ms-meta-empty:\nx-ms-meta-note:a b\nx-ms-version:2026-04-06\n",
                headers);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData(0L, "")]
        [InlineData(12L, "12")]
        public void Content_length_line_is_empty_for_zero(long? length, string expectedLine)
        {
            string s = AzureSigning.StringToSign("put", "", "", length, "", "", "", "", "", "", "", "", "x-ms-date:d\n", "/a/c/b");
            string[] lines = s.Split('\n');
            Assert.Equal("PUT", lines[0]);
            Assert.Equal(expectedLine, lines[3]);
            Assert.Equal("PUT\n\n\n" + expectedLine + "\n\n\n\n\n\n\n\n\nx-ms-date:d\n/a/c/b", s);
        }

        [Fact]
        public async Task Shared_key_string_to_sign_from_a_real_request()
        {
            var info = new AzureRequestInfo("devstoreaccount1", new[] { P("comp", "block"), P("blockid", "MDAwMDAx") });
            using (var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:10000/devstoreaccount1/docs/a%20b%24.txt?comp=block&blockid=MDAwMDAx"))
            {
                var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
                content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                request.Content = content;
                request.Headers.TryAddWithoutValidation("x-ms-version", "2026-04-06");
                request.Headers.TryAddWithoutValidation("x-ms-date", "Mon, 12 Oct 2026 08:00:00 GMT");
                request.Headers.TryAddWithoutValidation("User-Agent", "test");

                string expected = "PUT\n\n\n3\n\napplication/pdf\n\n\n\n\n\n\n"
                    + "x-ms-date:Mon, 12 Oct 2026 08:00:00 GMT\nx-ms-version:2026-04-06\n"
                    + "/devstoreaccount1/devstoreaccount1/docs/a%20b%24.txt\nblockid:MDAwMDAx\ncomp:block";
                Assert.Equal(expected, AzureSharedKeyAuth.BuildStringToSign(request, info));

                byte[] key = Convert.FromBase64String(StorageTestSupport.DevStoreKey);
                var auth = new AzureSharedKeyAuth(key);
                string signature;
                using (var hmac = new HMACSHA256(key))
                {
                    signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(expected)));
                }

                Assert.Equal(signature, auth.Sign(request, info));
                await auth.ApplyAsync(request, info, false, default);
                Assert.Equal("SharedKey devstoreaccount1:" + signature, string.Join(",", request.Headers.GetValues("Authorization")));
            }
        }

        [Fact]
        public void Get_request_without_body_has_empty_length_and_type_lines()
        {
            var info = new AzureRequestInfo("acct", new[] { P("restype", "container"), P("comp", "list"), P("maxresults", "1") });
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://acct.blob.core.windows.net/docs?restype=container&comp=list&maxresults=1"))
            {
                request.Headers.TryAddWithoutValidation("x-ms-date", "D");
                request.Headers.TryAddWithoutValidation("x-ms-version", "V");
                Assert.Equal("GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:D\nx-ms-version:V\n/acct/docs\ncomp:list\nmaxresults:1\nrestype:container",
                    AzureSharedKeyAuth.BuildStringToSign(request, info));
            }
        }

        [Fact]
        public void Service_sas_string_to_sign_has_16_fields_and_no_trailing_newline()
        {
            string s = AzureSigning.ServiceSasStringToSign("r", "", "2026-10-12T09:00:00Z", "/blob/acct/docs/a b.pdf", "", "", "https", "2026-04-06", "b", "", "", "", "", "", "", "");
            Assert.Equal("r\n\n2026-10-12T09:00:00Z\n/blob/acct/docs/a b.pdf\n\n\nhttps\n2026-04-06\nb\n\n\n\n\n\n\n", s);
            Assert.Equal(16, s.Split('\n').Length);
            Assert.Equal("/blob/acct/docs/a b.pdf", AzureSigning.SasCanonicalizedResource("acct", "docs", "a b.pdf"));
            Assert.Equal("/blob/acct/docs", AzureSigning.SasCanonicalizedResource("acct", "docs", ""));
        }

        [Fact]
        public void Blob_read_sas_query()
        {
            byte[] key = Convert.FromBase64String(StorageTestSupport.DevStoreKey);
            var expiry = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
            string query = AzureSigning.BlobReadSasQuery(key, "acct", "docs", "a b.pdf", expiry, "2026-04-06", true);

            string expectedSig = AzureSigning.Sign(key, "r\n\n2026-10-12T09:00:00Z\n/blob/acct/docs/a b.pdf\n\n\nhttps\n2026-04-06\nb\n\n\n\n\n\n\n");
            Assert.Equal("sv=2026-04-06&spr=https&se=2026-10-12T09%3A00%3A00Z&sr=b&sp=r&sig=" + StorageKeys.UriEncode(expectedSig, true), query);
            Assert.Contains("spr=https%2Chttp", AzureSigning.BlobReadSasQuery(key, "acct", "docs", "x", expiry, "2026-04-06", false));
        }

        [Fact]
        public void Block_list_xml()
        {
            Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList><Latest>MDAwMDAx</Latest><Latest>MDAwMDAy</Latest></BlockList>",
                AzureBlobBackend.BlockListXml(new[] { StorageKeys.BlockId(1), StorageKeys.BlockId(2) }));
        }

        [Fact]
        public void Sas_auth_describe_shows_expiry_and_permissions_but_no_signature()
        {
            var auth = new AzureSasAuth("sv=2026-04-06&sp=rcwdl&se=2026-12-31T00%3A00%3A00Z&sr=c&sig=SECRETSIG%3D");
            Assert.Equal("SAS sp=rcwdl se=2026-12-31T00:00:00Z", auth.Describe());
            string error;
            Assert.Equal("https://a.blob.core.windows.net/c/x?sv=2026-04-06&sp=rcwdl&se=2026-12-31T00%3A00%3A00Z&sr=c&sig=SECRETSIG%3D",
                auth.CreateReadUrl("https://a.blob.core.windows.net/c/x", "a", "c", "x", DateTime.UtcNow, "v", true, out error));
        }

        private static KeyValuePair<string, string> P(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }
    }
}
