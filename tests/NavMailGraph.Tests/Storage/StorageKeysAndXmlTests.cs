using System;
using System.Linq;
using System.Text;
using NavMailGraph.Storage;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    public class StorageKeysAndXmlTests
    {
        [Theory]
        [InlineData("AZaz09-._~", true, "AZaz09-._~")]
        [InlineData("a b", true, "a%20b")]
        [InlineData("test$file.text", false, "test%24file.text")]
        [InlineData("a/b/c.pdf", false, "a/b/c.pdf")]
        [InlineData("a/b", true, "a%2Fb")]
        [InlineData("x+y=z&q?#%", true, "x%2By%3Dz%26q%3F%23%25")]
        [InlineData("Rechnung ä.pdf", false, "Rechnung%20%C3%A4.pdf")]
        [InlineData("€", true, "%E2%82%AC")]
        [InlineData("😀", true, "%F0%9F%98%80")]
        [InlineData("", true, "")]
        [InlineData("*'()!", true, "%2A%27%28%29%21")]
        public void UriEncode_rules(string input, bool encodeSlash, string expected)
        {
            Assert.Equal(expected, StorageKeys.UriEncode(input, encodeSlash));
        }

        [Fact]
        public void UriEncode_uses_uppercase_hex()
        {
            Assert.Equal("%1A%7F", StorageKeys.UriEncode("\u001A\u007F", true));
        }

        [Theory]
        [InlineData("invoices/2026/10/R-1001.pdf")]
        [InlineData("a..b/c.d/...")]
        [InlineData("Rechnung ä €.pdf")]
        [InlineData("a//b")]
        [InlineData("with$special+chars=&?#.txt")]
        public void Valid_keys(string key)
        {
            Assert.Equal("", StorageKeys.Validate(key));
        }

        [Theory]
        [InlineData(null, "empty")]
        [InlineData("", "empty")]
        [InlineData("/abs", "start with '/'")]
        [InlineData("a\\b", "backslash")]
        [InlineData("a/./b", "'.' or '..'")]
        [InlineData("a/../b", "'.' or '..'")]
        [InlineData("..", "'.' or '..'")]
        [InlineData("a/.", "'.' or '..'")]
        [InlineData("tab\there", "control")]
        [InlineData("nl\n", "control")]
        public void Invalid_keys(string? key, string reasonPart)
        {
            Assert.Contains(reasonPart, StorageKeys.Validate(key));
        }

        [Fact]
        public void Unpaired_surrogates_are_rejected()
        {
            Assert.Contains("surrogate", StorageKeys.Validate("bad" + (char)0xD800 + "x"));
            Assert.Contains("surrogate", StorageKeys.Validate("bad" + (char)0xDC00));
            Assert.Contains("surrogate", StorageKeys.Validate("bad" + (char)0xD800));
            Assert.Equal("", StorageKeys.Validate("ok" + (char)0xD83D + (char)0xDE00));
        }

        [Fact]
        public void Block_ids_are_base64_of_six_digit_index_and_equal_length()
        {
            Assert.Equal("MDAwMDAx", StorageKeys.BlockId(1));
            Assert.Equal("000042", Encoding.ASCII.GetString(Convert.FromBase64String(StorageKeys.BlockId(42))));
            Assert.Single(new[] { 1, 9, 10, 999, 50000, 999999 }.Select(i => StorageKeys.BlockId(i).Length).Distinct());
            Assert.Throws<ArgumentOutOfRangeException>(() => StorageKeys.BlockId(0));
        }

        [Fact]
        public void Part_math()
        {
            const int MiB = 1024 * 1024;
            Assert.Equal(1, StorageKeys.PartCount(0, 8 * MiB));
            Assert.Equal(1, StorageKeys.PartCount(8 * MiB, 8 * MiB));
            Assert.Equal(2, StorageKeys.PartCount(8 * MiB + 1, 8 * MiB));
            Assert.Equal(3, StorageKeys.PartCount(20 * MiB, 8 * MiB));

            // configured size wins while it fits into the part limit
            Assert.Equal(8 * MiB, StorageKeys.EffectivePartSize(20 * MiB, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
            Assert.Equal(8 * MiB, StorageKeys.EffectivePartSize(-1, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
            // S3 minimum 5 MiB
            Assert.Equal(5 * MiB, StorageKeys.EffectivePartSize(20 * MiB, 1 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
            // Azure has no minimum
            Assert.Equal(1 * MiB, StorageKeys.EffectivePartSize(20 * MiB, 1 * MiB, StorageKeys.AzureMaxBlocks, 1));
            // 100 GiB with 8 MiB parts would need 12,800 parts > 10,000 -> raised to whole MiB: ceil(100 GiB / 10,000) = 10,738,419 B -> 11 MiB
            long hundredGiB = 100L * 1024 * MiB;
            int raised = StorageKeys.EffectivePartSize(hundredGiB, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize);
            Assert.Equal(11 * MiB, raised);
            Assert.True(StorageKeys.PartCount(hundredGiB, raised) <= StorageKeys.S3MaxParts);
            // exactly at the limit stays
            Assert.Equal(8 * MiB, StorageKeys.EffectivePartSize(10000L * 8 * MiB, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
            Assert.Equal(9 * MiB, StorageKeys.EffectivePartSize(10000L * 8 * MiB + 1, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
            // beyond the in-memory buffer cap
            Assert.Equal(-1, StorageKeys.EffectivePartSize(10000L * 600 * MiB, 8 * MiB, StorageKeys.S3MaxParts, StorageKeys.S3MinPartSize));
        }

        [Fact]
        public void PathOf()
        {
            Assert.Equal("/devstoreaccount1/docs/a%20b", StorageKeys.PathOf("http://127.0.0.1:10000/devstoreaccount1/docs/a%20b?x=1"));
            Assert.Equal("/", StorageKeys.PathOf("https://host"));
            Assert.Equal("/a", StorageKeys.PathOf("https://host/a#f"));
        }

        [Fact]
        public void Parse_s3_error()
        {
            StorageErrorInfo? e = StorageXml.ParseError("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>NoSuchKey</Code><Message>The resource you requested does not exist</Message><Resource>/mybucket/myfoto.jpg</Resource><RequestId>4442587FB7D0A2F9</RequestId></Error>");
            Assert.NotNull(e);
            Assert.Equal("NoSuchKey", e!.Code);
            Assert.Equal("The resource you requested does not exist", e.Message);
            Assert.Equal("4442587FB7D0A2F9", e.RequestId);
        }

        [Fact]
        public void Parse_azure_error_with_bom_and_multiline_message()
        {
            StorageErrorInfo? e = StorageXml.ParseError("﻿<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>AuthenticationFailed</Code><Message>Server failed to authenticate the request.\nRequestId:abc\nTime:2026-10-12T08:00:00.0000000Z</Message><AuthenticationErrorDetail>The MAC signature found ...</AuthenticationErrorDetail></Error>");
            Assert.Equal("AuthenticationFailed", e!.Code);
            Assert.StartsWith("Server failed to authenticate the request.", e.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{\"error\":{}}")]
        [InlineData("<html>bad gateway</html>")]
        [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><Error><Code>&e;</Code></Error>")]
        public void Non_error_bodies_and_dtds_yield_null(string? body)
        {
            Assert.Null(StorageXml.ParseError(body));
        }

        [Fact]
        public void Parse_azure_list()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ServiceEndpoint=\"http://127.0.0.1:10000/devstoreaccount1\" ContainerName=\"docs\">"
                + "<Prefix>nav/</Prefix><MaxResults>2</MaxResults><Blobs>"
                + "<Blob><Name>nav/a.pdf</Name><Properties><Creation-Time>Mon, 12 Oct 2026 08:00:00 GMT</Creation-Time><Content-Length>1234</Content-Length><Content-Type>application/pdf</Content-Type><BlobType>BlockBlob</BlobType></Properties><Metadata /></Blob>"
                + "<Blob><Name>nav/b &amp; c.txt</Name><Properties><Content-Length>0</Content-Length></Properties></Blob>"
                + "<Blob><Name Encoded=\"true\">nav%2Fenc%C3%A4</Name><Properties><Content-Length>5</Content-Length></Properties></Blob>"
                + "</Blobs><NextMarker>2!72!MDAwMDE0IW5hdi9jLnR4dCEwMDAwMjghOTk5OS0xMi0zMVQyMzo1OTo1OS45OTk5OTk5WiE-</NextMarker></EnumerationResults>";
            ListPage page = StorageXml.ParseAzureList(xml);
            Assert.Equal(3, page.Items.Count);
            Assert.Equal("nav/a.pdf", page.Items[0].Key);
            Assert.Equal(1234, page.Items[0].Size);
            Assert.Equal("nav/b & c.txt", page.Items[1].Key);
            Assert.Equal(0, page.Items[1].Size);
            Assert.Equal("nav/encä", page.Items[2].Key);
            Assert.StartsWith("2!72!", page.NextMarker);

            Assert.Equal("", StorageXml.ParseAzureList("<EnumerationResults><Blobs /><NextMarker /></EnumerationResults>").NextMarker);
            Assert.Throws<FormatException>(() => StorageXml.ParseAzureList("<Error><Code>X</Code></Error>"));
        }

        [Fact]
        public void Parse_s3_list_v2_with_namespace()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">"
                + "<Name>bucket</Name><Prefix>nav/</Prefix><KeyCount>2</KeyCount><MaxKeys>2</MaxKeys><IsTruncated>true</IsTruncated>"
                + "<Contents><Key>nav/a.pdf</Key><LastModified>2026-10-12T08:00:00.000Z</LastModified><ETag>\"abc\"</ETag><Size>1234</Size><StorageClass>STANDARD</StorageClass></Contents>"
                + "<Contents><Key>nav/b.txt</Key><Size>7</Size></Contents>"
                + "<NextContinuationToken>1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=</NextContinuationToken></ListBucketResult>";
            ListPage page = StorageXml.ParseS3List(xml);
            Assert.Equal(new[] { "nav/a.pdf", "nav/b.txt" }, page.Items.Select(i => i.Key));
            Assert.Equal(new[] { 1234L, 7L }, page.Items.Select(i => i.Size));
            Assert.Equal("1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=", page.NextMarker);

            Assert.Equal("", StorageXml.ParseS3List("<ListBucketResult><IsTruncated>false</IsTruncated><NextContinuationToken>x</NextContinuationToken></ListBucketResult>").NextMarker);
            Assert.Throws<FormatException>(() => StorageXml.ParseS3List("<ListBucketResult><IsTruncated>true</IsTruncated></ListBucketResult>"));
        }

        [Fact]
        public void Parse_s3_list_keeps_leading_trailing_and_whitespace_only_keys_verbatim()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">\n"
                + "  <IsTruncated>\n    true\n  </IsTruncated>\n"
                + "  <Contents>\n    <Key> lead and trail </Key>\n    <Size>\n 3 </Size>\n  </Contents>\n"
                + "  <Contents><Key>   </Key><Size>1</Size></Contents>\n"
                + "  <Contents><Key>a\tb </Key><Size>2</Size></Contents>\n"
                + "  <NextContinuationToken>tok </NextContinuationToken>\n</ListBucketResult>";
            ListPage page = StorageXml.ParseS3List(xml);
            Assert.Equal(new[] { " lead and trail ", "   ", "a\tb " }, page.Items.Select(i => i.Key));
            Assert.Equal(new[] { 3L, 1L, 2L }, page.Items.Select(i => i.Size));
            Assert.Equal("tok ", page.NextMarker);
        }

        [Fact]
        public void Parse_azure_list_keeps_whitespace_in_names()
        {
            ListPage page = StorageXml.ParseAzureList("<EnumerationResults><Blobs><Blob><Name> x </Name><Properties><Content-Length> 4 </Content-Length></Properties></Blob></Blobs><NextMarker /></EnumerationResults>");
            Assert.Equal(" x ", page.Items.Single().Key);
            Assert.Equal(4, page.Items.Single().Size);
        }

        [Fact]
        public void Parse_multipart_responses()
        {
            Assert.Equal("VXBsb2FkSUQ", StorageXml.ParseUploadId("<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>b</Bucket><Key>k</Key><UploadId>VXBsb2FkSUQ</UploadId></InitiateMultipartUploadResult>"));
            Assert.Throws<FormatException>(() => StorageXml.ParseUploadId("<InitiateMultipartUploadResult><UploadId/></InitiateMultipartUploadResult>"));
            Assert.Equal("\"3858f62230ac3c915f300c664312c11f-9\"", StorageXml.ParseCompleteETag("<CompleteMultipartUploadResult><Location>x</Location><ETag>\"3858f62230ac3c915f300c664312c11f-9\"</ETag></CompleteMultipartUploadResult>"));
        }

        [Fact]
        public void Complete_multipart_xml_lists_parts_ascending_and_escapes_etags()
        {
            Assert.Equal(
                "<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Part><ETag>&quot;a&quot;</ETag><PartNumber>1</PartNumber></Part><Part><ETag>&quot;b&quot;</ETag><PartNumber>2</PartNumber></Part></CompleteMultipartUpload>",
                S3Backend.CompleteXml(new[] { "\"a\"", "\"b\"" }));
        }

        [Theory]
        [InlineData("AuthenticationFailed", 403, "clock")]
        [InlineData("RequestTimeTooSkewed", 403, "NAV server clock")]
        [InlineData("SignatureDoesNotMatch", 403, "secret access key")]
        [InlineData("AccessDenied", 403, "IAM")]
        [InlineData("AuthorizationPermissionMismatch", 403, "Storage Blob Data Contributor")]
        [InlineData("NoSuchBucket", 404, "does not exist")]
        [InlineData("ContainerNotFound", 404, "does not exist")]
        [InlineData("", 403, "IAM")]
        [InlineData("", 403, "secret access key")]
        [InlineData("PermanentRedirect", 301, "region")]
        public void Hints(string code, int status, string expectedPart)
        {
            Assert.Contains(expectedPart, StorageErrorHints.GetHint(code, status, ""));
        }

        [Fact]
        public void Authentication_message_wins_over_authorization_code()
        {
            Assert.Equal(StorageErrorHints.AzureAuthenticationHint, StorageErrorHints.GetHint("AuthorizationFailure", 403, "Server failed to authenticate the request. Make sure ..."));
            Assert.Equal(StorageErrorHints.AccessDeniedHint, StorageErrorHints.GetHint("AuthorizationFailure", 403, "This request is not authorized to perform this operation."));
        }

        [Fact]
        public void Api_version_hint()
        {
            Assert.Equal(StorageErrorHints.ApiVersionHint, StorageErrorHints.GetHint("InvalidHeaderValue", 400, "The value for one of the HTTP headers is not in the correct format. x-ms-version"));
            Assert.Equal("", StorageErrorHints.GetHint("SomethingElse", 500, ""));
        }

        [Fact]
        public void Redaction_masks_sas_and_sigv4_secrets()
        {
            string text = StorageHttp.Redact(
                "GET https://a.blob.core.windows.net/c/x?sv=2026-04-06&sig=ABC%2Bdef%3D&se=1 "
                + "https://h/k?X-Amz-Credential=AKID%2F2026&X-Amz-Signature=deadbeef&X-Amz-Security-Token=tok "
                + "Authorization: AWS4-HMAC-SHA256 Credential=AKID/x,SignedHeaders=host,Signature=0123abcd SharedKey acct:c2lnbmF0dXJl");
            Assert.DoesNotContain("ABC%2Bdef", text);
            Assert.DoesNotContain("deadbeef", text);
            Assert.DoesNotContain("tok ", text);
            Assert.DoesNotContain("0123abcd", text);
            Assert.DoesNotContain("c2lnbmF0dXJl", text);
            Assert.Contains("sv=2026-04-06", text);
        }
    }
}
