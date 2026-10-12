using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using NavMailGraph.Storage;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    /// <summary>
    /// AWS SigV4 test vectors published in the S3 developer guide (sig-v4-header-based-auth, sigv4-query-string-auth), copied
    /// verbatim from docs/research-notes-v2.md. Canonical request, its hash and the signature are checked separately.
    /// </summary>
    public class SigV4Tests
    {
        internal const string AccessKey = "AKIAIOSFODNN7EXAMPLE";

        internal const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

        internal const string Region = "us-east-1";

        internal const string AmzDate = "20130524T000000Z";

        internal const string Host = "examplebucket.s3.amazonaws.com";

        internal static readonly DateTime VectorTime = new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

        private const string Scope = "20130524/us-east-1/s3/aws4_request";

        [Fact]
        public void Signing_key_and_scope()
        {
            Assert.Equal(Scope, SigV4.Scope("20130524", Region));
            Assert.Equal(AmzDate, SigV4.AmzDate(VectorTime));
            Assert.Equal("20130524", SigV4.DateStamp(VectorTime));
            Assert.Equal(SigV4.EmptyPayloadHash, SigV4.Sha256Hex(new byte[0]));
        }

        [Fact]
        public void Vector_get_object_with_range()
        {
            string signedHeaders;
            string headers = SigV4.CanonicalHeaders(new[]
            {
                H("Host", Host),
                H("Range", "bytes=0-9"),
                H("x-amz-content-sha256", SigV4.EmptyPayloadHash),
                H("x-amz-date", AmzDate),
            }, out signedHeaders);
            string canonical = SigV4.CanonicalRequest("GET", "/" + StorageKeys.UriEncode("test.txt", false), "", headers, signedHeaders, SigV4.EmptyPayloadHash);

            Assert.Equal(
                "GET\n/test.txt\n\nhost:examplebucket.s3.amazonaws.com\nrange:bytes=0-9\nx-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\nx-amz-date:20130524T000000Z\n\nhost;range;x-amz-content-sha256;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                canonical);
            Assert.Equal("host;range;x-amz-content-sha256;x-amz-date", signedHeaders);
            Assert.Equal("7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972", SigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical)));

            string stringToSign = SigV4.StringToSign(AmzDate, Scope, canonical);
            Assert.Equal("AWS4-HMAC-SHA256\n20130524T000000Z\n20130524/us-east-1/s3/aws4_request\n7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972", stringToSign);

            string signature = SigV4.Signature(SigV4.SigningKey(Secret, "20130524", Region), stringToSign);
            Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);
            Assert.Equal(
                "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
                SigV4.AuthorizationHeader(AccessKey, Scope, signedHeaders, signature));
        }

        [Fact]
        public void Vector_put_object_dollar_key()
        {
            const string bodyHash = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";
            Assert.Equal(bodyHash, SigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("Welcome to Amazon S3.")));

            string signedHeaders;
            string headers = SigV4.CanonicalHeaders(new[]
            {
                H("Date", "Fri, 24 May 2013 00:00:00 GMT"),
                H("Host", Host),
                H("x-amz-content-sha256", bodyHash),
                H("x-amz-date", AmzDate),
                H("x-amz-storage-class", "REDUCED_REDUNDANCY"),
            }, out signedHeaders);
            string canonicalUri = "/" + StorageKeys.UriEncode("test$file.text", false);
            Assert.Equal("/test%24file.text", canonicalUri);
            string canonical = SigV4.CanonicalRequest("PUT", canonicalUri, "", headers, signedHeaders, bodyHash);

            Assert.Equal(
                "PUT\n/test%24file.text\n\ndate:Fri, 24 May 2013 00:00:00 GMT\nhost:examplebucket.s3.amazonaws.com\nx-amz-content-sha256:" + bodyHash
                + "\nx-amz-date:20130524T000000Z\nx-amz-storage-class:REDUCED_REDUNDANCY\n\ndate;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class\n" + bodyHash,
                canonical);
            Assert.Equal("9e0e90d9c76de8fa5b200d8c849cd5b8dc7a3be3951ddb7f6a76b4158342019d", SigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical)));
            Assert.Equal("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
                SigV4.Signature(SigV4.SigningKey(Secret, "20130524", Region), SigV4.StringToSign(AmzDate, Scope, canonical)));
        }

        [Fact]
        public void Vector_get_bucket_lifecycle()
        {
            string canonicalQuery = SigV4.CanonicalQueryString(new[] { Q("lifecycle", null) });
            Assert.Equal("lifecycle=", canonicalQuery);
            Assert.Equal("lifecycle", SigV4.WireQueryString(new[] { Q("lifecycle", null) }));
            AssertSimpleGet("/", canonicalQuery,
                "9766c798316ff2757b517bc739a67f6213b4ab36dd5da2f94eaebf79c77395ca",
                "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
        }

        [Fact]
        public void Vector_list_objects()
        {
            // given out of order on purpose: the canonical form sorts by name
            string canonicalQuery = SigV4.CanonicalQueryString(new[] { Q("prefix", "J"), Q("max-keys", "2") });
            Assert.Equal("max-keys=2&prefix=J", canonicalQuery);
            AssertSimpleGet("/", canonicalQuery,
                "df57d21db20da04d7fa30298dd4488ba3a2b47ca3a489c74750e0f1e7df1b9b7",
                "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
        }

        [Fact]
        public void Vector_presigned_get()
        {
            string expectedCanonical =
                "GET\n/test.txt\nX-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host\nhost:examplebucket.s3.amazonaws.com\n\nhost\nUNSIGNED-PAYLOAD";
            Assert.Equal("3bfa292879f6447bbcda7001decf97f4a54dc650c8942174ae0a9121cf58ad04", SigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(expectedCanonical)));

            string url = SigV4.PresignUrl("GET", "https://" + Host, Host, "/test.txt", Region, AccessKey, Secret, null, VectorTime, 86400);
            Assert.Equal(
                "https://examplebucket.s3.amazonaws.com/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
                url);
        }

        [Fact]
        public void Presigned_url_with_session_token_signs_the_token()
        {
            string url = SigV4.PresignUrl("GET", "https://" + Host, Host, "/test.txt", Region, AccessKey, Secret, "tok/en+=", VectorTime, 60);
            Assert.Contains("&X-Amz-Security-Token=tok%2Fen%2B%3D&X-Amz-SignedHeaders=host&X-Amz-Signature=", url);
        }

        [Fact]
        public void Backend_build_reproduces_the_lifecycle_and_list_vectors()
        {
            S3Backend backend = StorageTestSupport.NewS3Backend("https://s3.amazonaws.com", "examplebucket", false, VectorTime);
            Assert.Equal("https://examplebucket.s3.amazonaws.com", backend.Origin);

            using (HttpRequestMessage request = backend.Build(HttpMethod.Get, "/", "", new[] { Q("lifecycle", "") }, null, SigV4.EmptyPayloadHash))
            {
                Assert.EndsWith("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", Header(request, "Authorization"));
                Assert.Equal(AmzDate, Header(request, "x-amz-date"));
                Assert.Equal(SigV4.EmptyPayloadHash, Header(request, "x-amz-content-sha256"));
            }

            using (HttpRequestMessage request = backend.Build(HttpMethod.Get, "/", "", new[] { Q("max-keys", "2"), Q("prefix", "J") }, null, SigV4.EmptyPayloadHash))
            {
                Assert.Equal("https://examplebucket.s3.amazonaws.com/?max-keys=2&prefix=J", request.RequestUri!.AbsoluteUri);
                Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", Header(request, "Authorization"));
            }
        }

        [Fact]
        public void Backend_presign_reproduces_the_vector()
        {
            S3Backend backend = StorageTestSupport.NewS3Backend("https://s3.amazonaws.com", "examplebucket", false, VectorTime);
            Assert.EndsWith("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", backend.CreateDownloadUrl("test.txt", 86400));
        }

        [Fact]
        public void Canonical_headers_trim_and_collapse_spaces()
        {
            string signed;
            string block = SigV4.CanonicalHeaders(new[] { H("X-Amz-Meta-A", "  a   b  "), H("Content-Type", "text/plain") }, out signed);
            Assert.Equal("content-type:text/plain\nx-amz-meta-a:a b\n", block);
            Assert.Equal("content-type;x-amz-meta-a", signed);
        }

        [Fact]
        public void Canonical_query_encodes_names_and_values_and_sorts_after_encoding()
        {
            string q = SigV4.CanonicalQueryString(new[] { Q("prefix", "a b/ä"), Q("continuation-token", "x+y="), Q("list-type", "2") });
            Assert.Equal("continuation-token=x%2By%3D&list-type=2&prefix=a%20b%2F%C3%A4", q);
        }

        private static void AssertSimpleGet(string canonicalUri, string canonicalQuery, string expectedHash, string expectedSignature)
        {
            string signedHeaders;
            string headers = SigV4.CanonicalHeaders(new[]
            {
                H("host", Host),
                H("x-amz-content-sha256", SigV4.EmptyPayloadHash),
                H("x-amz-date", AmzDate),
            }, out signedHeaders);
            Assert.Equal("host;x-amz-content-sha256;x-amz-date", signedHeaders);
            string canonical = SigV4.CanonicalRequest("GET", canonicalUri, canonicalQuery, headers, signedHeaders, SigV4.EmptyPayloadHash);
            Assert.Equal(expectedHash, SigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical)));
            Assert.Equal(expectedSignature, SigV4.Signature(SigV4.SigningKey(Secret, "20130524", Region), SigV4.StringToSign(AmzDate, Scope, canonical)));
        }

        private static string Header(HttpRequestMessage request, string name)
        {
            IEnumerable<string>? values;
            return request.Headers.TryGetValues(name, out values) && values != null ? string.Join(",", values) : "";
        }

        private static KeyValuePair<string, string> H(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }

        private static KeyValuePair<string, string?> Q(string name, string? value)
        {
            return new KeyValuePair<string, string?>(name, value);
        }
    }
}
