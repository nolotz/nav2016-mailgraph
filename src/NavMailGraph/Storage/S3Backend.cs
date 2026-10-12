using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// Amazon S3 and S3-compatible storage with SigV4 header auth: PutObject, multipart upload (Create/UploadPart/Complete/Abort),
    /// GetObject, HeadObject, DeleteObject, ListObjectsV2 and presigned GET. Path-style or virtual-hosted addressing.
    /// </summary>
    internal sealed class S3Backend : StorageBackend
    {
        private readonly string _accessKeyId;

        private readonly string _secretAccessKey;

        private readonly string _sessionToken;

        /// <summary>Creates the backend; the service URL is the endpoint without bucket, e.g. https://s3.eu-central-1.amazonaws.com or http://127.0.0.1:8333.</summary>
        internal S3Backend(StorageHttp http, StorageSettings settings, Func<DateTime> clock, string serviceUrl, string region, string bucket, bool pathStyle,
            string accessKeyId, string secretAccessKey, string sessionToken)
            : base(http, settings, clock)
        {
            Region = region;
            Bucket = bucket;
            PathStyle = pathStyle;
            _accessKeyId = accessKeyId;
            _secretAccessKey = secretAccessKey;
            _sessionToken = sessionToken ?? "";

            var endpoint = new Uri(serviceUrl.TrimEnd('/'), UriKind.Absolute);
            string basePath = StorageKeys.PathOf(serviceUrl.TrimEnd('/'));
            if (basePath == "/")
            {
                basePath = "";
            }

            if (pathStyle)
            {
                Origin = endpoint.Scheme + "://" + endpoint.Authority;
                BucketPath = basePath + "/" + StorageKeys.UriEncode(bucket, true);
            }
            else
            {
                Origin = endpoint.Scheme + "://" + bucket + "." + endpoint.Authority;
                BucketPath = basePath;
            }

            IsHttps = string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        internal string Region { get; }

        internal string Bucket { get; }

        internal bool PathStyle { get; }

        internal bool IsHttps { get; }

        /// <summary>scheme://host[:port] the requests go to.</summary>
        internal string Origin { get; }

        /// <summary>Encoded path of the bucket ("" for virtual-hosted style without base path).</summary>
        internal string BucketPath { get; }

        internal override int MaxParts
        {
            get { return StorageKeys.S3MaxParts; }
        }

        internal override int MinPartSize
        {
            get { return StorageKeys.S3MinPartSize; }
        }

        internal override string Describe()
        {
            return "S3 endpoint=" + Origin + " bucket=" + Bucket + " region=" + Region + " pathStyle=" + (PathStyle ? "true" : "false")
                + " accessKeyId=" + MaskAccessKey(_accessKeyId) + (_sessionToken.Length > 0 ? " sessionToken=set" : "");
        }

        /// <summary>Encoded object path (canonical URI).</summary>
        internal string ObjectPath(string key)
        {
            return BucketPath + "/" + StorageKeys.UriEncode(key, false);
        }

        internal override async Task<string> PutAsync(string key, string contentType, UploadBody body, CancellationToken cancellationToken)
        {
            MediaTypeHeaderValue type = ParseContentType(key, contentType);
            string payloadHash = PayloadHash(body);
            var call = new StorageCall("upload", key, force => Task.FromResult(Build(HttpMethod.Put, ObjectPath(key), key, NoQuery, () =>
            {
                HttpContent content = body.CreateContent();
                content.Headers.ContentType = type;
                return content;
            }, payloadHash)))
            {
                Idempotent = true,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                return GraphClient.HeaderValue(response.Headers, "ETag");
            }
        }

        internal override async Task<MultipartState> BeginMultipartAsync(string key, string contentType, CancellationToken cancellationToken)
        {
            MediaTypeHeaderValue type = ParseContentType(key, contentType);
            var query = new[] { Flag("uploads") };
            var call = new StorageCall("create multipart upload", key, force => Task.FromResult(Build(HttpMethod.Post, ObjectPath(key), key, query, () =>
            {
                var content = new ByteArrayContent(new byte[0]);
                content.Headers.ContentType = type;
                content.Headers.ContentLength = 0;
                return content;
            }, SigV4.EmptyPayloadHash)));
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                string body = await StorageHttp.ReadBodyAsync(response).ConfigureAwait(false);
                string uploadId;
                try
                {
                    uploadId = StorageXml.ParseUploadId(body);
                }
                catch (FormatException ex)
                {
                    throw Http.Validation("create multipart upload", key, ex.Message);
                }

                Http.Log.Log("multipart upload started");
                return new MultipartState(key, contentType, uploadId);
            }
        }

        internal override async Task UploadPartAsync(MultipartState state, int partNumber, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            string hash = SigV4.Sha256Hex(buffer, 0, count);
            var query = new[] { Pair("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)), Pair("uploadId", state.UploadId) };
            var call = new StorageCall("upload part " + partNumber, state.Key, force => Task.FromResult(Build(HttpMethod.Put, ObjectPath(state.Key), state.Key, query, () =>
            {
                var content = new ByteArrayContent(buffer, 0, count);
                content.Headers.ContentLength = count;
                return content;
            }, hash)))
            {
                Idempotent = true,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                string eTag = GraphClient.HeaderValue(response.Headers, "ETag");
                if (eTag.Length == 0)
                {
                    throw Http.Validation("upload part " + partNumber, state.Key, "the server returned no ETag for the part");
                }

                state.Parts.Add(eTag);
            }
        }

        /// <summary>
        /// CompleteMultipartUpload with retries: 5xx and transport errors (StorageHttp), a 200 response whose body is an
        /// &lt;Error&gt; with a transient code (InternalError/SlowDown/ServiceUnavailable) and a body that stalls or breaks while
        /// S3 assembles the object. The body is read with the idle timeout (S3 sends whitespace while it works), not with a total
        /// deadline. A 404 NoSuchUpload after an earlier attempt means that attempt may have succeeded: the object is checked with HEAD.
        /// </summary>
        internal override async Task<string> CompleteMultipartAsync(MultipartState state, CancellationToken cancellationToken)
        {
            const string operation = "complete multipart upload";
            byte[] xml = Encoding.UTF8.GetBytes(CompleteXml(state.Parts));
            string hash = SigV4.Sha256Hex(xml);
            var query = new[] { Pair("uploadId", state.UploadId) };
            int attempts = 0;
            var call = new StorageCall(operation, state.Key, force =>
            {
                attempts++;
                return Task.FromResult(Build(HttpMethod.Post, ObjectPath(state.Key), state.Key, query, () =>
                {
                    var content = new ByteArrayContent(xml);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                    content.Headers.ContentLength = xml.Length;
                    return content;
                }, hash));
            })
            {
                Idempotent = true,
                StreamResponse = true,
                AcceptStatus = (status, code) => status == 404 && attempts > 1,
            };

            int retries = 0;
            while (true)
            {
                using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status == 404)
                    {
                        string notFound = await StorageHttp.ReadBodyAsync(response).ConfigureAwait(false);
                        return await ConfirmCompletedAsync(state, response, notFound, cancellationToken).ConfigureAwait(false);
                    }

                    string body;
                    try
                    {
                        // CompleteMultipartUpload can fail after the 200 headers were sent: the body is then an <Error> document.
                        body = await Http.ReadStreamedBodyAsync(operation, state.Key, response, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GraphMailException ex) when (ex.ErrorCode == GraphMailException.TransportCode && retries < Http.MaxRetries && !cancellationToken.IsCancellationRequested)
                    {
                        Http.Log.Log(operation + ": " + ex.Message);
                        await Http.PauseBeforeRetryAsync(retries++, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    StorageErrorInfo? error = StorageXml.ParseError(body);
                    if (error != null)
                    {
                        if (StorageHttp.IsTransientErrorCode(error.Code) && retries < Http.MaxRetries)
                        {
                            Http.Log.Log(operation + ": HTTP " + status + " with error " + error.Code);
                            await Http.PauseBeforeRetryAsync(retries++, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        GraphMailException failure = Http.BuildError(operation, state.Key, status, response, body);
                        Http.Log.Log("ERROR " + failure.Message);
                        throw failure;
                    }

                    string eTag = StorageXml.ParseCompleteETag(body);
                    return eTag.Length > 0 ? eTag : GraphClient.HeaderValue(response.Headers, "ETag");
                }
            }
        }

        /// <summary>
        /// 404 on a repeated CompleteMultipartUpload: with NoSuchUpload an earlier attempt may have completed the upload, so the
        /// object counts as uploaded when HEAD finds it with the expected size. Otherwise the 404 is reported.
        /// </summary>
        private async Task<string> ConfirmCompletedAsync(MultipartState state, HttpResponseMessage response, string body, CancellationToken cancellationToken)
        {
            StorageErrorInfo? info = StorageXml.ParseError(body);
            string code = info == null ? "" : info.Code;
            if (code.Length == 0 || string.Equals(code, "NoSuchUpload", StringComparison.OrdinalIgnoreCase))
            {
                Http.Log.Log("complete multipart upload: upload id no longer exists after a retry; checking whether an earlier attempt completed it");
                ObjectHead head = await HeadAsync(state.Key, "head", cancellationToken).ConfigureAwait(false);
                if (head.Exists && (state.TotalBytes < 0 || head.Size == state.TotalBytes))
                {
                    Http.Log.Log("the object exists with the expected size: the earlier attempt completed the upload");
                    return head.ETag;
                }
            }

            GraphMailException failure = Http.BuildError("complete multipart upload", state.Key, 404, response, body);
            Http.Log.Log("ERROR " + failure.Message);
            throw failure;
        }

        internal override async Task AbortMultipartAsync(MultipartState state)
        {
            try
            {
                var query = new[] { Pair("uploadId", state.UploadId) };
                var call = new StorageCall("abort multipart upload", state.Key, force => Task.FromResult(Build(HttpMethod.Delete, ObjectPath(state.Key), state.Key, query, null, SigV4.EmptyPayloadHash)))
                {
                    Idempotent = true,
                    AcceptStatus = (status, code) => status == 404,
                };
                using (await Http.SendAsync(call, CancellationToken.None).ConfigureAwait(false))
                {
                }

                Http.Log.Log("multipart upload aborted");
            }
            catch (Exception ex)
            {
                Http.Log.Log("aborting the multipart upload failed (the bucket's lifecycle rule must clean it up): " + ex.Message);
            }
        }

        internal override async Task<long> DownloadAsync(string key, Stream target, CancellationToken cancellationToken)
        {
            var call = new StorageCall("download", key, force => Task.FromResult(Build(HttpMethod.Get, ObjectPath(key), key, NoQuery, null, SigV4.EmptyPayloadHash)))
            {
                Idempotent = true,
                StreamResponse = true,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                return await Http.CopyBodyAsync("download", key, response, target, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// HEAD object; -1 when it does not exist. A HEAD 404 has no body, so a missing bucket looks like a missing object:
        /// a 404 is therefore cross-checked with a one-key ListObjectsV2, and NoSuchBucket becomes an error.
        /// </summary>
        internal override async Task<long> GetSizeAsync(string key, CancellationToken cancellationToken)
        {
            ObjectHead head = await HeadAsync(key, "head", cancellationToken).ConfigureAwait(false);
            if (head.Exists)
            {
                return head.Size;
            }

            try
            {
                await ListPageAsync(key, "", 1, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphMailException ex) when (!IsNoSuchBucket(ex.ErrorCode))
            {
                // Only a missing bucket is reported; other list problems (e.g. no s3:ListBucket permission) keep "not found".
                Http.Log.Log("bucket check after HEAD 404 inconclusive: " + ex.Message);
            }

            return -1;
        }

        private async Task<ObjectHead> HeadAsync(string key, string operation, CancellationToken cancellationToken)
        {
            var call = new StorageCall(operation, key, force => Task.FromResult(Build(HttpMethod.Head, ObjectPath(key), key, NoQuery, null, SigV4.EmptyPayloadHash)))
            {
                Idempotent = true,
                AcceptStatus = (status, code) => status == 404,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 404)
                {
                    return new ObjectHead(false, -1, "");
                }

                long? length = response.Content == null ? null : response.Content.Headers.ContentLength;
                return new ObjectHead(true, length ?? 0, GraphClient.HeaderValue(response.Headers, "ETag"));
            }
        }

        internal override async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            var call = new StorageCall("delete", key, force => Task.FromResult(Build(HttpMethod.Delete, ObjectPath(key), key, NoQuery, null, SigV4.EmptyPayloadHash)))
            {
                Idempotent = true,
                AcceptStatus = (status, code) => status == 404 && !IsNoSuchBucket(code),
            };
            using (await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
            }
        }

        internal override async Task<ListPage> ListPageAsync(string prefix, string marker, int maxResults, CancellationToken cancellationToken)
        {
            var query = new List<KeyValuePair<string, string?>> { Pair("list-type", "2"), Pair("max-keys", maxResults.ToString(CultureInfo.InvariantCulture)) };
            if (prefix.Length > 0)
            {
                query.Add(Pair("prefix", prefix));
            }

            if (marker.Length > 0)
            {
                query.Add(Pair("continuation-token", marker));
            }

            string path = BucketPath.Length == 0 ? "/" : BucketPath;
            var call = new StorageCall("list", "", force => Task.FromResult(Build(HttpMethod.Get, path, "", query, null, SigV4.EmptyPayloadHash)))
            {
                Idempotent = true,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                string body = await StorageHttp.ReadBodyAsync(response).ConfigureAwait(false);
                try
                {
                    return StorageXml.ParseS3List(body);
                }
                catch (FormatException ex)
                {
                    throw Http.Validation("list", "", ex.Message);
                }
            }
        }

        internal override string CreateDownloadUrl(string key, int validSeconds)
        {
            if (validSeconds > SigV4.MaxPresignSeconds)
            {
                throw Http.Validation("download URL", key, "S3 presigned URLs are valid for at most 7 days (10080 minutes)");
            }

            int seconds = Math.Max(1, validSeconds);
            string path = ObjectPath(key);
            CheckPath(path, key);
            var origin = new Uri(Origin, UriKind.Absolute);
            return SigV4.PresignUrl("GET", Origin, origin.Authority, path, Region, _accessKeyId, _secretAccessKey, _sessionToken.Length > 0 ? _sessionToken : null, Clock(), seconds);
        }

        /// <summary>&lt;CompleteMultipartUpload xmlns=...&gt;&lt;Part&gt;&lt;ETag/&gt;&lt;PartNumber/&gt;&lt;/Part&gt;...</summary>
        internal static string CompleteXml(IList<string> eTags)
        {
            var sb = new StringBuilder("<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
            for (int i = 0; i < eTags.Count; i++)
            {
                sb.Append("<Part><ETag>").Append(StorageKeys.XmlEscape(eTags[i])).Append("</ETag><PartNumber>")
                    .Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("</PartNumber></Part>");
            }

            return sb.Append("</CompleteMultipartUpload>").ToString();
        }

        /// <summary>
        /// Builds and signs one request. The canonical URI is checked against what System.Uri will actually send.
        /// Signed headers: host, x-amz-content-sha256, x-amz-date, plus content-type and x-amz-security-token when present.
        /// </summary>
        internal HttpRequestMessage Build(HttpMethod method, string canonicalUri, string key, IList<KeyValuePair<string, string?>> query, Func<HttpContent>? content, string payloadHash)
        {
            string wireQuery = SigV4.WireQueryString(query);
            var uri = new Uri(Origin + canonicalUri + (wireQuery.Length > 0 ? "?" + wireQuery : ""), UriKind.Absolute);
            if (!string.Equals(uri.AbsolutePath, canonicalUri, StringComparison.Ordinal))
            {
                throw Http.Validation("request", key, StorageKeys.PathChangedText(uri.AbsolutePath));
            }

            var request = new HttpRequestMessage(method, uri);
            if (content != null)
            {
                request.Content = content();
            }

            DateTime now = Clock();
            string amzDate = SigV4.AmzDate(now);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            var signed = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("host", uri.Authority),
                new KeyValuePair<string, string>("x-amz-content-sha256", payloadHash),
                new KeyValuePair<string, string>("x-amz-date", amzDate),
            };
            if (_sessionToken.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("x-amz-security-token", _sessionToken);
                signed.Add(new KeyValuePair<string, string>("x-amz-security-token", _sessionToken));
            }

            if (request.Content != null && request.Content.Headers.ContentType != null)
            {
                signed.Add(new KeyValuePair<string, string>("content-type", request.Content.Headers.ContentType.ToString()));
            }

            string signedHeaders;
            string canonicalHeaders = SigV4.CanonicalHeaders(signed, out signedHeaders);
            string canonicalRequest = SigV4.CanonicalRequest(method.Method, canonicalUri, SigV4.CanonicalQueryString(query), canonicalHeaders, signedHeaders, payloadHash);
            string dateStamp = SigV4.DateStamp(now);
            string scope = SigV4.Scope(dateStamp, Region);
            string signature = SigV4.Signature(SigV4.SigningKey(_secretAccessKey, dateStamp, Region), SigV4.StringToSign(amzDate, scope, canonicalRequest));
            request.Headers.TryAddWithoutValidation("Authorization", SigV4.AuthorizationHeader(_accessKeyId, scope, signedHeaders, signature));
            return request;
        }

        /// <summary>
        /// Payload hash of a single PUT: the real SHA-256 for buffers, for streams over plain HTTP and for streams up to one
        /// part size (one extra read pass); UNSIGNED-PAYLOAD only for larger streamed PUTs over HTTPS.
        /// </summary>
        private string PayloadHash(UploadBody body)
        {
            if (body.Buffer != null)
            {
                return SigV4.Sha256Hex(body.Buffer, 0, body.Count);
            }

            if (IsHttps && body.Length > Settings.PartSizeBytes)
            {
                return SigV4.UnsignedPayload;
            }

            Stream stream = body.Stream!;
            stream.Seek(body.Start, SeekOrigin.Begin);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[StorageHttp.CopyBufferSize];
                long remaining = body.Length;
                while (remaining > 0)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        throw Http.Validation("upload", "", "the source ended before the expected length");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    remaining -= read;
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                stream.Seek(body.Start, SeekOrigin.Begin);
                return SigV4.Hex(sha.Hash!);
            }
        }

        private void CheckPath(string canonicalUri, string key)
        {
            var uri = new Uri(Origin + canonicalUri, UriKind.Absolute);
            if (!string.Equals(uri.AbsolutePath, canonicalUri, StringComparison.Ordinal))
            {
                throw Http.Validation("download URL", key, StorageKeys.PathChangedText(uri.AbsolutePath));
            }
        }

        private static bool IsNoSuchBucket(string code)
        {
            return string.Equals(code, "NoSuchBucket", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>First 4 characters + "…" (access key ids are not secret, but there is no need to show them fully).</summary>
        internal static string MaskAccessKey(string accessKeyId)
        {
            if (string.IsNullOrEmpty(accessKeyId))
            {
                return "(none)";
            }

            return accessKeyId.Length <= 4 ? "****" : accessKeyId.Substring(0, 4) + "****";
        }

        private static readonly KeyValuePair<string, string?>[] NoQuery = new KeyValuePair<string, string?>[0];

        private static KeyValuePair<string, string?> Pair(string name, string value)
        {
            return new KeyValuePair<string, string?>(name, value);
        }

        private static KeyValuePair<string, string?> Flag(string name)
        {
            return new KeyValuePair<string, string?>(name, null);
        }

        private sealed class ObjectHead
        {
            internal ObjectHead(bool exists, long size, string eTag)
            {
                Exists = exists;
                Size = size;
                ETag = eTag;
            }

            internal bool Exists { get; }

            internal long Size { get; }

            internal string ETag { get; }
        }
    }
}
