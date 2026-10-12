using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using NavMailGraph.Storage;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests.Storage
{
    public class ObjectStorageClientTests
    {
        private const int MiB = 1024 * 1024;

        [Fact]
        public void Not_configured_returns_error_text()
        {
            var client = StorageTestSupport.NewClient(new FakeHttpMessageHandler(), null);
            string error = client.UploadText("x", "a.txt", "");
            Assert.Contains("not configured", error);
            Assert.Equal(error, client.LastError);
            Assert.Equal("ObjectStorageClient (not configured)", client.Describe());
            Assert.False(client.Exists("a.txt"));
            Assert.Equal(-1, client.GetSize("a.txt"));
            Assert.Equal("", client.GetDownloadUrl("a.txt", 5));
        }

        [Fact]
        public void Invalid_configuration_is_reported_by_operations_and_describe()
        {
            var client = StorageTestSupport.NewClient(new FakeHttpMessageHandler(), null);
            client.UseAzureBlobSharedKey("acct", "not base64!!", "docs");
            Assert.Contains("not valid base64", client.TestConnection());
            Assert.Contains("invalid configuration", client.Describe());

            client.UseAzureBlobSasUrl("https://acct.blob.core.windows.net/docs");
            Assert.Contains("SAS URL", client.TestConnection());

            client.UseS3("ftp://x", "", "b", "AK", "SK");
            Assert.Contains("service URL", client.TestConnection());

            client.UseS3("", "", "", "AK", "SK");
            Assert.Contains("bucket", client.TestConnection());
        }

        [Fact]
        public void Describe_contains_no_secrets()
        {
            var handler = new FakeHttpMessageHandler();
            ObjectStorageClient s3 = StorageTestSupport.NewS3Client(handler);
            s3.S3SessionToken = "SESSIONTOKEN-SECRET";
            string d = s3.Describe();
            Assert.Contains("bucket=nav-bucket", d);
            Assert.Contains("region=eu-central-1", d);
            Assert.Contains("pathStyle=true", d);
            Assert.DoesNotContain(StorageTestSupport.S3Secret, d);
            Assert.DoesNotContain("SESSIONTOKEN-SECRET", d);
            Assert.DoesNotContain("AKIDEXAMPLE", d);

            ObjectStorageClient azure = StorageTestSupport.NewAzureClient(handler);
            Assert.DoesNotContain(StorageTestSupport.DevStoreKey, azure.Describe());
            Assert.Contains("AzureBlob/SharedKey account=devstoreaccount1 container=docs", azure.Describe());

            var sas = StorageTestSupport.NewClient(handler, null);
            sas.UseAzureBlobSasUrl("https://acct.blob.core.windows.net/docs?sv=2026-04-06&sp=rcwl&se=2026-12-31T00%3A00%3A00Z&sr=c&sig=TOPSECRET%3D");
            Assert.DoesNotContain("TOPSECRET", sas.Describe());
            Assert.Contains("SAS sp=rcwl", sas.Describe());
        }

        [Fact]
        public void S3_path_style_defaults()
        {
            var client = StorageTestSupport.NewClient(new FakeHttpMessageHandler(), null);
            client.UseS3("", "eu-central-1", "bucket", "AK", "SK");
            Assert.False(client.S3PathStyle);
            client.UseS3("", "eu-central-1", "my.bucket", "AK", "SK");
            Assert.True(client.S3PathStyle);
            client.UseS3("https://s3.eu-central-1.amazonaws.com", "eu-central-1", "bucket", "AK", "SK");
            Assert.False(client.S3PathStyle);
            client.UseS3("https://fsn1.your-objectstorage.com", "fsn1", "bucket", "AK", "SK");
            Assert.True(client.S3PathStyle);
            client.S3PathStyle = false;
            Assert.False(client.S3PathStyle);
        }

        [Fact]
        public void S3_small_upload_is_one_signed_put_with_real_hash()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"etag1\"", "x-amz-request-id: R1"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.KeyPrefix = "nav/prod/";

            Assert.Equal("", client.UploadText("Hallo Welt", "Rechnung ä 1.txt", ""));

            RecordedRequest put = handler.Requests.Single();
            Assert.Equal(HttpMethod.Put, put.Method);
            Assert.Equal("https://s3.example.test/nav-bucket/nav/prod/Rechnung%20%C3%A4%201.txt", put.Uri!.AbsoluteUri);
            Assert.Equal("Hallo Welt", put.BodyText);
            Assert.Equal(SigV4.Sha256Hex(Encoding.UTF8.GetBytes("Hallo Welt")), put.Header("x-amz-content-sha256"));
            Assert.Equal("20261012T080000Z", put.Header("x-amz-date"));
            Assert.Equal("text/plain; charset=utf-8", put.Header("Content-Type"));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-central-1/s3/aws4_request,SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date,Signature=", put.Header("Authorization"));
            Assert.False(put.HasHeader("Expect"));
            Assert.DoesNotContain(StorageTestSupport.S3Secret, client.LastDiagnostics);
        }

        [Fact]
        public void S3_session_token_is_sent_and_signed()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(200));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.S3SessionToken = "TOKEN123";
            Assert.Equal("", client.Delete("a.txt"));
            RecordedRequest request = handler.Requests.Single();
            Assert.Equal("TOKEN123", request.Header("x-amz-security-token"));
            Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token,", request.Header("Authorization"));
        }

        [Fact]
        public async Task S3_multipart_upload_from_non_seekable_stream_of_unknown_length()
        {
            byte[] data = StorageTestSupport.Pattern(12 * MiB + 123);
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><Bucket>nav-bucket</Bucket><Key>k</Key><UploadId>UP-1</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p3\""));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<CompleteMultipartUploadResult><ETag>\"final-3\"</ETag></CompleteMultipartUploadResult>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.PartSizeBytes = 6 * MiB;

            StorageUploadResult result = await client.UploadFromStreamAsync(new NonSeekableStream(data), -1, "big/file.bin", "", CancellationToken.None);

            Assert.Equal(data.Length, result.Size);
            Assert.Equal(3, result.Parts);
            Assert.Equal("big/file.bin", result.Key);
            var requests = handler.Requests;
            Assert.Equal(5, requests.Count);
            Assert.Equal("POST", requests[0].Method.Method);
            Assert.EndsWith("/nav-bucket/big/file.bin?uploads", requests[0].Uri!.AbsoluteUri);
            Assert.Equal("application/octet-stream", requests[0].Header("Content-Type"));
            Assert.Equal("PUT", requests[1].Method.Method);
            Assert.EndsWith("?partNumber=1&uploadId=UP-1", requests[1].Uri!.AbsoluteUri);
            Assert.Equal(6 * MiB, requests[1].Body.Length);
            Assert.Equal(SigV4.Sha256Hex(requests[1].Body), requests[1].Header("x-amz-content-sha256"));
            Assert.EndsWith("?partNumber=2&uploadId=UP-1", requests[2].Uri!.AbsoluteUri);
            Assert.EndsWith("?partNumber=3&uploadId=UP-1", requests[3].Uri!.AbsoluteUri);
            Assert.Equal(123, requests[3].Body.Length);
        }

        [Fact]
        public async Task Non_seekable_stream_smaller_than_one_part_is_a_single_put()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(201, "ETag: \"0x1\""));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            byte[] data = StorageTestSupport.Pattern(5000);
            StorageUploadResult result = await client.UploadFromStreamAsync(new NonSeekableStream(data), -1, "small.bin", "", CancellationToken.None);
            Assert.Equal(0, result.Parts);
            Assert.Equal(5000, result.Size);
            Assert.Equal("\"0x1\"", result.ETag);
            Assert.Equal(data, handler.Requests.Single().Body);
            Assert.Equal("BlockBlob", handler.Requests.Single().Header("x-ms-blob-type"));
        }

        [Fact]
        public async Task S3_multipart_sequence_and_complete_body()
        {
            byte[] data = StorageTestSupport.Pattern(12 * MiB + 123);
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>UP-1</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p3\""));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<CompleteMultipartUploadResult><ETag>\"final-3\"</ETag></CompleteMultipartUploadResult>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.PartSizeBytes = 6 * MiB;

            StorageUploadResult result = await client.UploadFromStreamAsync(new NonSeekableStream(data), data.Length, "big/file.bin", "", CancellationToken.None);

            Assert.Equal(data.Length, result.Size);
            Assert.Equal(3, result.Parts);
            Assert.Equal("\"final-3\"", result.ETag);
            var requests = handler.Requests;
            Assert.Equal(5, requests.Count);
            Assert.Equal(new[] { 6 * MiB, 6 * MiB, 123 }, requests.Skip(1).Take(3).Select(r => r.Body.Length));
            Assert.Equal(data, requests.Skip(1).Take(3).SelectMany(r => r.Body).ToArray());
            RecordedRequest complete = requests[4];
            Assert.Equal("POST", complete.Method.Method);
            Assert.EndsWith("?uploadId=UP-1", complete.Uri!.AbsoluteUri);
            Assert.Equal(S3Backend.CompleteXml(new[] { "\"p1\"", "\"p2\"", "\"p3\"" }), complete.BodyText);
            Assert.Equal("application/xml", complete.Header("Content-Type"));
        }

        [Fact]
        public void S3_multipart_part_failure_aborts_the_upload()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>UP-9</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Xml(403, "<Error><Code>AccessDenied</Code><Message>Access Denied</Message><RequestId>RID-7</RequestId></Error>"));
            handler.Enqueue(r => StorageTestSupport.Empty(204));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                string error = client.UploadStream(stream, "x.bin", "");
                Assert.StartsWith("S3 upload part 1 failed (HTTP 403, AccessDenied): Access Denied [request-id RID-7, ", error);
                Assert.Contains("bucket nav-bucket, key x.bin]", error);
                Assert.Contains("Hint: ", error);
                Assert.Contains("IAM", error);
                Assert.Equal("RID-7", client.LastRequestId);
            }

            RecordedRequest abort = handler.Requests.Last();
            Assert.Equal("DELETE", abort.Method.Method);
            Assert.EndsWith("/nav-bucket/x.bin?uploadId=UP-9", abort.Uri!.AbsoluteUri);
        }

        [Fact]
        public void S3_complete_with_200_error_body_fails_and_aborts()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>U</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            for (int i = 0; i < 4; i++)
            {
                handler.Enqueue(r => StorageTestSupport.Xml(200, "<Error><Code>InternalError</Code><Message>We encountered an internal error. Please try again.</Message><RequestId>R</RequestId></Error>"));
            }

            handler.Enqueue(r => StorageTestSupport.Empty(204));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                string error = client.UploadStream(stream, "x.bin", "");
                Assert.StartsWith("S3 complete multipart upload failed (HTTP 200, InternalError): We encountered an internal error.", error);
            }

            Assert.Equal(4, handler.Requests.Count(r => r.Method.Method == "POST" && r.Uri!.Query == "?uploadId=U"));
            Assert.Equal("DELETE", handler.Requests.Last().Method.Method);
        }

        [Fact]
        public void S3_complete_200_error_and_500_are_retried_with_the_same_body()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>U</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "  \n<Error><Code>InternalError</Code><Message>Please try again.</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Xml(500, "<Error><Code>InternalError</Code><Message>x</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<CompleteMultipartUploadResult><ETag>\"final-2\"</ETag></CompleteMultipartUploadResult>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.Equal("", client.UploadStream(stream, "x.bin", ""));
            }

            List<RecordedRequest> completes = handler.Requests.Where(r => r.Method.Method == "POST" && r.Uri!.Query == "?uploadId=U").ToList();
            Assert.Equal(3, completes.Count);
            Assert.All(completes, c => Assert.Equal(S3Backend.CompleteXml(new[] { "\"p1\"", "\"p2\"" }), c.BodyText));
            Assert.DoesNotContain(handler.Requests, r => r.Method.Method == "DELETE");
        }

        [Fact]
        public void S3_complete_no_such_upload_after_lost_response_checks_the_object()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>U</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            handler.EnqueueException(new HttpRequestException("connection reset"));
            handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchUpload</Code><Message>The specified upload does not exist.</Message></Error>"));
            handler.Enqueue(r =>
            {
                HttpResponseMessage head = StorageTestSupport.Empty(200, "ETag: \"done-2\"");
                head.Content.Headers.ContentLength = 6 * MiB;
                return head;
            });
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.Equal("", client.UploadStream(stream, "x.bin", ""));
            }

            Assert.Equal("HEAD", handler.Requests.Last().Method.Method);
            Assert.DoesNotContain(handler.Requests, r => r.Method.Method == "DELETE");
        }

        [Fact]
        public void S3_complete_no_such_upload_with_wrong_size_object_is_an_error()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>U</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            handler.Enqueue(r => StorageTestSupport.Xml(503, "<Error><Code>SlowDown</Code><Message>Reduce</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchUpload</Code><Message>The specified upload does not exist.</Message></Error>"));
            handler.Enqueue(r =>
            {
                HttpResponseMessage head = StorageTestSupport.Empty(200, "ETag: \"old\"");
                head.Content.Headers.ContentLength = 17;
                return head;
            });
            handler.Enqueue(r => StorageTestSupport.Empty(204));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.StartsWith("S3 complete multipart upload failed (HTTP 404, NoSuchUpload)", client.UploadStream(stream, "x.bin", ""));
            }

            Assert.Equal("DELETE", handler.Requests.Last().Method.Method);
        }

        [Fact]
        public void S3_complete_transport_failure_after_retries_does_not_abort()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<InitiateMultipartUploadResult><UploadId>U</UploadId></InitiateMultipartUploadResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p1\""));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"p2\""));
            for (int i = 0; i < 4; i++)
            {
                handler.EnqueueException(new HttpRequestException("connection reset"));
            }

            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;

            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.StartsWith("S3 complete multipart upload failed: ", client.UploadStream(stream, "x.bin", ""));
            }

            Assert.Equal(3 + 4, handler.Requests.Count);
            Assert.DoesNotContain(handler.Requests, r => r.Method.Method == "DELETE");
            Assert.Contains("not aborted", client.LastDiagnostics);
        }

        [Fact]
        public void S3_exists_on_missing_bucket_is_an_error_and_missing_object_is_not()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(404));
            handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchBucket</Code><Message>The specified bucket does not exist</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Empty(404));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>"));
            handler.Enqueue(r => StorageTestSupport.Empty(404));
            handler.Enqueue(r => StorageTestSupport.Xml(403, "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);

            Assert.False(client.Exists("a"));
            Assert.Contains("NoSuchBucket", client.LastError);
            Assert.Equal(-1, client.GetSize("a"));
            Assert.Equal("", client.LastError);
            Assert.False(client.Exists("a"));
            Assert.Equal("", client.LastError);

            RecordedRequest list = handler.Requests[1];
            Assert.Equal("GET", list.Method.Method);
            Assert.Contains("list-type=2", list.Uri!.Query);
            Assert.Contains("max-keys=1", list.Uri!.Query);
            Assert.Contains("prefix=a", list.Uri!.Query);
        }

        [Fact]
        public async Task Upload_body_timeout_restarts_on_progress_and_headers_are_kept()
        {
            int calls = 0;
            var inner = new ByteArrayContent(new byte[3 * ProgressStream.ChunkSize + 5]);
            inner.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            inner.Headers.ContentLength = 3 * ProgressStream.ChunkSize + 5;
            inner.Headers.TryAddWithoutValidation("Content-MD5", "AAAA");
            using (var content = new ProgressContent(inner, () => calls++))
            {
                Assert.Equal("application/pdf", content.Headers.ContentType!.ToString());
                Assert.Equal(3 * ProgressStream.ChunkSize + 5, content.Headers.ContentLength);
                Assert.Equal("AAAA", string.Join(",", content.Headers.GetValues("Content-MD5")));
                byte[] bytes = await content.ReadAsByteArrayAsync();
                Assert.Equal(3 * ProgressStream.ChunkSize + 5, bytes.Length);
            }

            Assert.True(calls >= 4, "progress reported per chunk: " + calls);
        }

        [Fact]
        public void Seekable_stream_above_part_size_over_https_uses_unsigned_payload_single_put()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e\""));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.PartSizeBytes = 5 * MiB;
            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.Equal("", client.UploadStream(stream, "x.pdf", ""));
            }

            RecordedRequest put = handler.Requests.Single();
            Assert.Equal(SigV4.UnsignedPayload, put.Header("x-amz-content-sha256"));
            Assert.Equal("application/pdf", put.Header("Content-Type"));
            Assert.Equal(6 * MiB, put.ContentLength);
        }

        [Fact]
        public void Seekable_stream_over_http_is_hashed_and_replayed_on_retry()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(503, "<Error><Code>SlowDown</Code><Message>Reduce your request rate.</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Empty(200, "ETag: \"e\""));
            var delays = new List<TimeSpan>();
            ObjectStorageClient client = StorageTestSupport.NewClient(handler, delays);
            client.UseS3("http://127.0.0.1:8333", "", "b", "AK", "SK");
            client.PartSizeBytes = 5 * MiB;
            byte[] data = StorageTestSupport.Pattern(6 * MiB);
            using (var stream = new MemoryStream(data))
            {
                stream.Position = 0;
                Assert.Equal("", client.UploadStream(stream, "x.bin", ""));
            }

            Assert.Equal(2, handler.Requests.Count);
            Assert.Single(delays);
            foreach (RecordedRequest put in handler.Requests)
            {
                Assert.Equal(SigV4.Sha256Hex(data), put.Header("x-amz-content-sha256"));
                Assert.Equal(data, put.Body);
            }
        }

        [Fact]
        public void Non_idempotent_create_multipart_is_not_retried()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(503, "<Error><Code>SlowDown</Code><Message>Reduce</Message></Error>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            client.MultipartThresholdBytes = 1;
            client.PartSizeBytes = 5 * MiB;
            using (var stream = new MemoryStream(StorageTestSupport.Pattern(6 * MiB)))
            {
                Assert.StartsWith("S3 create multipart upload failed (HTTP 503, SlowDown)", client.UploadStream(stream, "x.bin", ""));
            }

            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Get_is_retried_on_500_and_transport_errors_up_to_max_retries()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(500, "<Error><Code>InternalError</Code><Message>x</Message></Error>"));
            handler.EnqueueException(new HttpRequestException("connection reset"));
            handler.Enqueue(r => StorageTestSupport.Bytes(200, Encoding.UTF8.GetBytes("payload")));
            var delays = new List<TimeSpan>();
            ObjectStorageClient client = StorageTestSupport.NewClient(handler, delays);
            client.UseS3("https://s3.example.test", "", "b", "AK", "SK");
            using (var target = new MemoryStream())
            {
                Assert.Equal("", client.DownloadToStream("a.txt", target));
                Assert.Equal("payload", Encoding.UTF8.GetString(target.ToArray()));
            }

            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(2, delays.Count);

            handler.Fallback = r => StorageTestSupport.Xml(503, "<Error><Code>SlowDown</Code><Message>busy</Message></Error>");
            client.MaxRetries = 2;
            using (var target = new MemoryStream())
            {
                Assert.StartsWith("S3 download failed (HTTP 503, SlowDown): busy", client.DownloadToStream("a.txt", target));
            }

            Assert.Equal(3 + 3, handler.Requests.Count);
        }

        [Fact]
        public void Download_to_file_keeps_an_existing_file_on_error_and_replaces_it_on_success()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nmg-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "existing.pdf");
                File.WriteAllText(file, "old content");
                var handler = new FakeHttpMessageHandler();
                handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message></Error>"));
                handler.Enqueue(r => StorageTestSupport.Bytes(200, Encoding.UTF8.GetBytes("new")));
                ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);

                Assert.StartsWith("S3 download failed (HTTP 404, NoSuchKey)", client.DownloadToFile("missing", file));
                Assert.Equal("old content", File.ReadAllText(file));
                Assert.Single(Directory.GetFiles(dir));

                Assert.Equal("", client.DownloadToFile("x", file));
                Assert.Equal("new", File.ReadAllText(file));
                Assert.Single(Directory.GetFiles(dir));

                Assert.Contains("no file path", client.DownloadToFile("x", ""));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Download_to_file_streams_and_removes_partial_file_on_error()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nmg-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] data = StorageTestSupport.Pattern(300000);
                var handler = new FakeHttpMessageHandler();
                handler.Enqueue(r => StorageTestSupport.Bytes(200, data));
                handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>BlobNotFound</Code><Message>The specified blob does not exist.\nRequestId:r</Message></Error>", "x-ms-error-code: BlobNotFound", "x-ms-request-id: AZ-1"));
                ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);

                string file = Path.Combine(dir, "a.bin");
                Assert.Equal("", client.DownloadToFile("a.bin", file));
                Assert.Equal(data, File.ReadAllBytes(file));

                string missing = Path.Combine(dir, "missing.bin");
                string error = client.DownloadToFile("missing.bin", missing);
                Assert.StartsWith("Azure Blob download failed (HTTP 404, BlobNotFound): The specified blob does not exist. [request-id AZ-1, ", error);
                Assert.Contains("container docs, key missing.bin]", error);
                Assert.False(File.Exists(missing));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Truncated_download_is_an_error()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r =>
            {
                HttpResponseMessage response = StorageTestSupport.Bytes(200, new byte[10]);
                response.Content.Headers.ContentLength = 20;
                return response;
            });
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            using (var target = new MemoryStream())
            {
                Assert.Contains("ended after 10 of 20 bytes", client.DownloadToStream("a", target));
            }
        }

        [Fact]
        public void Azure_single_put_blob_headers_and_signature()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(201, "ETag: \"0x8D\""));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            Assert.Equal("", client.UploadText("", "empty.txt", "text/plain"));

            RecordedRequest put = handler.Requests.Single();
            Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs/empty.txt", put.Uri!.AbsoluteUri);
            Assert.Equal("BlockBlob", put.Header("x-ms-blob-type"));
            Assert.Equal("2026-04-06", put.Header("x-ms-version"));
            Assert.Equal("Mon, 12 Oct 2026 08:00:00 GMT", put.Header("x-ms-date"));
            Assert.Equal(0, put.ContentLength);

            string stringToSign = "PUT\n\n\n\n\ntext/plain\n\n\n\n\n\n\nx-ms-blob-type:BlockBlob\nx-ms-date:Mon, 12 Oct 2026 08:00:00 GMT\nx-ms-version:2026-04-06\n/devstoreaccount1/devstoreaccount1/docs/empty.txt";
            Assert.Equal("SharedKey devstoreaccount1:" + AzureSigning.Sign(Convert.FromBase64String(StorageTestSupport.DevStoreKey), stringToSign), put.Header("Authorization"));
        }

        [Fact]
        public void Azure_block_upload_sequence()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Fallback = r => StorageTestSupport.Empty(201, "ETag: \"0xFINAL\"");
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            client.MultipartThresholdBytes = 1 * MiB;
            client.PartSizeBytes = 1 * MiB;
            byte[] data = StorageTestSupport.Pattern(2 * MiB + 5);
            using (var stream = new MemoryStream(data))
            {
                Assert.Equal("", client.UploadStream(stream, "folder/report.pdf", ""));
            }

            var requests = handler.Requests;
            Assert.Equal(4, requests.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs/folder/report.pdf?comp=block&blockid=" + StorageKeys.UriEncode(StorageKeys.BlockId(i + 1), true), requests[i].Uri!.AbsoluteUri);
                Assert.False(requests[i].HasHeader("x-ms-blob-type"));
            }

            Assert.Equal(data, requests.Take(3).SelectMany(r => r.Body).ToArray());
            RecordedRequest commit = requests[3];
            Assert.EndsWith("/docs/folder/report.pdf?comp=blocklist", commit.Uri!.AbsoluteUri);
            Assert.Equal("application/pdf", commit.Header("x-ms-blob-content-type"));
            Assert.Equal(AzureBlobBackend.BlockListXml(new[] { StorageKeys.BlockId(1), StorageKeys.BlockId(2), StorageKeys.BlockId(3) }), commit.BodyText);
        }

        [Fact]
        public void Azure_exists_size_delete_semantics()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Bytes(200, new byte[0]).WithLength(4711));
            handler.Enqueue(r => StorageTestSupport.Empty(404, "x-ms-error-code: BlobNotFound"));
            handler.Enqueue(r => StorageTestSupport.Empty(404, "x-ms-error-code: ContainerNotFound", "x-ms-request-id: RQ"));
            handler.Enqueue(r => StorageTestSupport.Empty(404, "x-ms-error-code: BlobNotFound"));
            handler.Enqueue(r => StorageTestSupport.Empty(202));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);

            Assert.Equal(4711, client.GetSize("a"));
            Assert.Equal("HEAD", handler.Requests[0].Method.Method);
            Assert.False(client.Exists("b"));
            Assert.Equal("", client.LastError);
            Assert.False(client.Exists("c"));
            Assert.StartsWith("Azure Blob get properties failed (HTTP 404, ContainerNotFound): Not Found", client.LastError);
            Assert.Contains("does not exist", client.LastError);
            Assert.Equal("", client.Delete("d"));
            Assert.Equal("include", handler.Requests[3].Header("x-ms-delete-snapshots"));
            Assert.Equal("", client.Delete("e"));
        }

        [Fact]
        public void S3_delete_of_missing_object_is_success_but_missing_bucket_is_an_error()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchKey</Code><Message>x</Message></Error>"));
            handler.Enqueue(r => StorageTestSupport.Xml(404, "<Error><Code>NoSuchBucket</Code><Message>The specified bucket does not exist</Message></Error>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            Assert.Equal("", client.Delete("a"));
            Assert.Contains("NoSuchBucket", client.Delete("a"));
        }

        [Fact]
        public void Azure_list_follows_next_marker_and_strips_key_prefix()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<EnumerationResults><Blobs><Blob><Name>p/a.txt</Name><Properties><Content-Length>1</Content-Length></Properties></Blob></Blobs><NextMarker>M2</NextMarker></EnumerationResults>"));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<EnumerationResults><Blobs><Blob><Name>p/b.txt</Name><Properties><Content-Length>2</Content-Length></Properties></Blob></Blobs><NextMarker /></EnumerationResults>"));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            client.KeyPrefix = "p/";

            Assert.Equal("", client.List(""));
            Assert.Equal(2, client.ListCount);
            Assert.Equal("a.txt", client.GetListedKey(0));
            Assert.Equal(2, client.GetListedSize(1));
            Assert.Equal("", client.GetListedKey(2));
            Assert.Equal(-1, client.GetListedSize(-1));
            Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs?restype=container&comp=list&prefix=p%2F&maxresults=1000", handler.Requests[0].Uri!.AbsoluteUri);
            Assert.Contains("&marker=M2&", handler.Requests[1].Uri!.AbsoluteUri);
        }

        [Fact]
        public void S3_list_uses_list_type_2_and_continuation_token()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<ListBucketResult><IsTruncated>true</IsTruncated><Contents><Key>a</Key><Size>1</Size></Contents><NextContinuationToken>T/1=</NextContinuationToken></ListBucketResult>"));
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<ListBucketResult><IsTruncated>false</IsTruncated><Contents><Key>b</Key><Size>2</Size></Contents></ListBucketResult>"));
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            Assert.Equal("", client.List("x y"));
            Assert.Equal(2, client.ListCount);
            Assert.Equal("https://s3.example.test/nav-bucket?list-type=2&max-keys=1000&prefix=x%20y", handler.Requests[0].Uri!.AbsoluteUri);
            Assert.Equal("https://s3.example.test/nav-bucket?continuation-token=T%2F1%3D&list-type=2&max-keys=1000&prefix=x%20y", handler.Requests[1].Uri!.AbsoluteUri);
        }

        [Fact]
        public void Wrong_key_error_is_readable_and_contains_no_secret()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(403,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>AuthenticationFailed</Code><Message>Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature.\nRequestId:6a3c\nTime:2026-10-12T08:00:00.0000000Z</Message><AuthenticationErrorDetail>The MAC signature found in the HTTP request 'abc=' is not the same as any computed signature.</AuthenticationErrorDetail></Error>",
                "x-ms-request-id: 6a3c", "x-ms-error-code: AuthenticationFailed"));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            string error = client.TestConnection();
            Assert.StartsWith("Azure Blob list failed (HTTP 403, AuthenticationFailed): Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature. [request-id 6a3c, ", error);
            Assert.Contains("Hint: Azure rejected the signature", error);
            Assert.DoesNotContain("MAC signature", error);
            Assert.DoesNotContain(StorageTestSupport.DevStoreKey, client.LastDiagnostics);
        }

        [Fact]
        public void Invalid_keys_are_rejected_without_a_request()
        {
            var handler = new FakeHttpMessageHandler();
            ObjectStorageClient client = StorageTestSupport.NewS3Client(handler);
            Assert.Contains("'.' or '..'", client.UploadText("x", "a/../b.txt", ""));
            Assert.Contains("backslash", client.Delete("a\\b"));
            Assert.Contains("empty", client.Delete(""));
            Assert.False(client.Exists("./x"));
            Assert.Contains("'.' or '..'", client.LastError);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Download_url_s3_presign_and_limits()
        {
            ObjectStorageClient client = StorageTestSupport.NewS3Client(new FakeHttpMessageHandler());
            string url = client.GetDownloadUrl("docs/R 1.pdf", 60);
            Assert.StartsWith("https://s3.example.test/nav-bucket/docs/R%201.pdf?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIDEXAMPLE%2F20261012%2Feu-central-1%2Fs3%2Faws4_request&X-Amz-Date=20261012T080000Z&X-Amz-Expires=3600&X-Amz-SignedHeaders=host&X-Amz-Signature=", url);
            Assert.DoesNotContain("X-Amz-Signature", client.LastDiagnostics);

            Assert.Equal("", client.GetDownloadUrl("a", 7 * 24 * 60 + 1));
            Assert.Contains("at most 7 days", client.LastError);
            Assert.Equal("", client.GetDownloadUrl("a", 0));
            Assert.Contains("validMinutes", client.LastError);
        }

        [Fact]
        public void Download_url_azure_shared_key_and_sas_modes()
        {
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(new FakeHttpMessageHandler());
            string url = client.GetDownloadUrl("a b.pdf", 30);
            byte[] key = Convert.FromBase64String(StorageTestSupport.DevStoreKey);
            Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs/a%20b.pdf?"
                + AzureSigning.BlobReadSasQuery(key, "devstoreaccount1", "docs", "a b.pdf", StorageTestSupport.FixedTime.AddMinutes(30), "2020-12-06", false), url);
            Assert.Contains("sv=2020-12-06&", url);

            client.AzureApiVersion = "2025-11-05";
            Assert.Contains("sv=2020-12-06&", client.GetDownloadUrl("a b.pdf", 30));
            client.AzureApiVersion = "2019-12-12";
            Assert.Equal("", client.GetDownloadUrl("a b.pdf", 30));
            Assert.Contains("AzureApiVersion 2020-12-06 or later", client.LastError);

            var sas = StorageTestSupport.NewClient(new FakeHttpMessageHandler(), null);
            sas.UseAzureBlobSasUrl("https://acct.blob.core.windows.net/docs?sv=2026-04-06&sp=rl&sr=c&sig=S%3D");
            Assert.Equal("https://acct.blob.core.windows.net/docs/x.pdf?sv=2026-04-06&sp=rl&sr=c&sig=S%3D", sas.GetDownloadUrl("x.pdf", 10));
        }

        [Fact]
        public void Sas_mode_appends_token_and_sends_no_authorization()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Xml(200, "<EnumerationResults><Blobs /></EnumerationResults>"));
            var client = StorageTestSupport.NewClient(handler, null);
            client.UseAzureBlobSasUrl("http://127.0.0.1:10000/devstoreaccount1/docs?sv=2026-04-06&sp=rl&sr=c&sig=S%3D");
            Assert.Contains("account=devstoreaccount1 container=docs", client.Describe());
            Assert.Equal("", client.TestConnection());
            RecordedRequest request = handler.Requests.Single();
            Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/docs?restype=container&comp=list&maxresults=1&sv=2026-04-06&sp=rl&sr=c&sig=S%3D", request.Uri!.AbsoluteUri);
            Assert.Null(request.Authorization);
            Assert.False(request.HasHeader("Authorization"));
            Assert.DoesNotContain("sig=", client.LastDiagnostics);
        }

        [Fact]
        public void Upload_file_missing_and_content_type_by_extension()
        {
            var handler = new FakeHttpMessageHandler();
            handler.Enqueue(r => StorageTestSupport.Empty(201));
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(handler);
            Assert.Contains("file not found", client.UploadFile("/nonexistent/x.pdf", "x.pdf", ""));

            string file = Path.Combine(Path.GetTempPath(), "nmg-" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            try
            {
                Assert.Equal("", client.UploadFile(file, "x", ""));
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", handler.Requests.Single().Header("Content-Type"));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task Known_length_mismatch_is_detected()
        {
            ObjectStorageClient client = StorageTestSupport.NewAzureClient(new FakeHttpMessageHandler());
            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => client.UploadFromStreamAsync(new NonSeekableStream(new byte[10]), 11, "a", "", CancellationToken.None));
            Assert.Contains("delivered 10 bytes but 11 were expected", ex.Message);
        }

        [Fact]
        public void Invalid_content_type_is_a_validation_error()
        {
            ObjectStorageClient client = StorageTestSupport.NewS3Client(new FakeHttpMessageHandler());
            Assert.Contains("invalid content type", client.UploadText("x", "a.txt", "not a type"));
        }
    }

    internal static class ResponseExtensions
    {
        internal static HttpResponseMessage WithLength(this HttpResponseMessage response, long length)
        {
            response.Content.Headers.ContentLength = length;
            return response;
        }
    }
}
