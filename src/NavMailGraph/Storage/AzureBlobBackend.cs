using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// Azure Blob Storage REST (block blobs): Put Blob, Put Block + Put Block List, Get Blob, Get Blob Properties (HEAD),
    /// Delete Blob, List Blobs. Authorization through an <see cref="IStorageAuth"/> (Shared Key, SAS URL; Entra later).
    /// </summary>
    internal sealed class AzureBlobBackend : StorageBackend
    {
        private readonly IStorageAuth _auth;

        /// <summary>
        /// Creates the backend. <paramref name="containerUrl"/> is the encoded container URL without query, e.g.
        /// https://acct.blob.core.windows.net/container or http://127.0.0.1:10000/devstoreaccount1/container.
        /// </summary>
        internal AzureBlobBackend(StorageHttp http, StorageSettings settings, Func<DateTime> clock, IStorageAuth auth, string accountName, string containerName, string containerUrl, string apiVersion)
            : base(http, settings, clock)
        {
            _auth = auth;
            AccountName = accountName;
            ContainerName = containerName;
            ContainerUrl = containerUrl.TrimEnd('/');
            ApiVersion = apiVersion;
        }

        internal string AccountName { get; }

        internal string ContainerName { get; }

        internal string ContainerUrl { get; }

        internal string ApiVersion { get; }

        internal override int MaxParts
        {
            get { return StorageKeys.AzureMaxBlocks; }
        }

        internal override int MinPartSize
        {
            get { return 1; }
        }

        internal override string Describe()
        {
            return "AzureBlob/" + _auth.Describe() + " account=" + AccountName + " container=" + ContainerName + " url=" + ContainerUrl + " x-ms-version=" + ApiVersion;
        }

        /// <summary>Encoded blob URL without query.</summary>
        internal string BlobUrl(string key)
        {
            return ContainerUrl + "/" + StorageKeys.UriEncode(key, false);
        }

        internal override async Task<string> PutAsync(string key, string contentType, UploadBody body, CancellationToken cancellationToken)
        {
            MediaTypeHeaderValue type = ParseContentType(key, contentType);
            var call = new StorageCall("upload", key, force => BuildAsync(HttpMethod.Put, BlobUrl(key), key, NoQuery, () =>
            {
                HttpContent content = body.CreateContent();
                content.Headers.ContentType = type;
                return content;
            }, new[] { Header("x-ms-blob-type", "BlockBlob") }, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                return GraphClient.HeaderValue(response.Headers, "ETag");
            }
        }

        internal override Task<MultipartState> BeginMultipartAsync(string key, string contentType, CancellationToken cancellationToken)
        {
            ParseContentType(key, contentType);
            Http.Log.Log("block upload: " + key);
            return Task.FromResult(new MultipartState(key, contentType, ""));
        }

        internal override async Task UploadPartAsync(MultipartState state, int partNumber, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            string blockId = StorageKeys.BlockId(partNumber);
            var query = new List<KeyValuePair<string, string>> { Pair("comp", "block"), Pair("blockid", blockId) };
            var call = new StorageCall("put block " + partNumber, state.Key, force => BuildAsync(HttpMethod.Put, BlobUrl(state.Key), state.Key, query, () =>
            {
                var content = new ByteArrayContent(buffer, 0, count);
                content.Headers.ContentLength = count;
                return content;
            }, NoHeaders, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
            };
            using (await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
            }

            state.Parts.Add(blockId);
        }

        internal override async Task<string> CompleteMultipartAsync(MultipartState state, CancellationToken cancellationToken)
        {
            byte[] xml = Encoding.UTF8.GetBytes(BlockListXml(state.Parts));
            var query = new List<KeyValuePair<string, string>> { Pair("comp", "blocklist") };
            var call = new StorageCall("put block list", state.Key, force => BuildAsync(HttpMethod.Put, BlobUrl(state.Key), state.Key, query, () =>
            {
                var content = new ByteArrayContent(xml);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                content.Headers.ContentLength = xml.Length;
                return content;
            }, new[] { Header("x-ms-blob-content-type", state.ContentType) }, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                return GraphClient.HeaderValue(response.Headers, "ETag");
            }
        }

        /// <summary>Uncommitted blocks are garbage-collected by the service (one week); nothing to abort.</summary>
        internal override Task AbortMultipartAsync(MultipartState state)
        {
            Http.Log.Log("block upload failed; uncommitted blocks are discarded by the service");
            return Task.FromResult(0);
        }

        internal override async Task<long> DownloadAsync(string key, Stream target, CancellationToken cancellationToken)
        {
            var call = new StorageCall("download", key, force => BuildAsync(HttpMethod.Get, BlobUrl(key), key, NoQuery, null, NoHeaders, force, cancellationToken))
            {
                Idempotent = true,
                StreamResponse = true,
                CanRefreshAuth = _auth.CanRefresh,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                return await Http.CopyBodyAsync("download", key, response, target, cancellationToken).ConfigureAwait(false);
            }
        }

        internal override async Task<long> GetSizeAsync(string key, CancellationToken cancellationToken)
        {
            var call = new StorageCall("get properties", key, force => BuildAsync(HttpMethod.Head, BlobUrl(key), key, NoQuery, null, NoHeaders, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
                AcceptStatus = IsBlobNotFound,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 404)
                {
                    return -1;
                }

                long? length = response.Content == null ? null : response.Content.Headers.ContentLength;
                return length ?? 0;
            }
        }

        internal override async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            var call = new StorageCall("delete", key, force => BuildAsync(HttpMethod.Delete, BlobUrl(key), key, NoQuery, null,
                new[] { Header("x-ms-delete-snapshots", "include") }, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
                AcceptStatus = IsBlobNotFound,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 404)
                {
                    Http.Log.Log("blob did not exist (treated as deleted)");
                }
            }
        }

        internal override async Task<ListPage> ListPageAsync(string prefix, string marker, int maxResults, CancellationToken cancellationToken)
        {
            var query = new List<KeyValuePair<string, string>> { Pair("restype", "container"), Pair("comp", "list") };
            if (prefix.Length > 0)
            {
                query.Add(Pair("prefix", prefix));
            }

            if (marker.Length > 0)
            {
                query.Add(Pair("marker", marker));
            }

            query.Add(Pair("maxresults", maxResults.ToString(CultureInfo.InvariantCulture)));
            var call = new StorageCall("list", "", force => BuildAsync(HttpMethod.Get, ContainerUrl, "", query, null, NoHeaders, force, cancellationToken))
            {
                Idempotent = true,
                CanRefreshAuth = _auth.CanRefresh,
            };
            using (HttpResponseMessage response = await Http.SendAsync(call, cancellationToken).ConfigureAwait(false))
            {
                string body = await StorageHttp.ReadBodyAsync(response).ConfigureAwait(false);
                try
                {
                    return StorageXml.ParseAzureList(body);
                }
                catch (FormatException ex)
                {
                    throw Http.Validation("list", "", ex.Message);
                }
            }
        }

        internal override string CreateDownloadUrl(string key, int validSeconds)
        {
            string error;
            bool httpsOnly = ContainerUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            string url = _auth.CreateReadUrl(BlobUrl(key), AccountName, ContainerName, key, Clock().AddSeconds(validSeconds), ApiVersion, httpsOnly, out error);
            if (url.Length == 0)
            {
                throw Http.Validation("download URL", key, error);
            }

            return url;
        }

        /// <summary>&lt;?xml ...?&gt;&lt;BlockList&gt;&lt;Latest&gt;id&lt;/Latest&gt;...&lt;/BlockList&gt;.</summary>
        internal static string BlockListXml(IEnumerable<string> blockIds)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
            foreach (string id in blockIds)
            {
                sb.Append("<Latest>").Append(StorageKeys.XmlEscape(id)).Append("</Latest>");
            }

            return sb.Append("</BlockList>").ToString();
        }

        /// <summary>Builds the URL (encoded query + SAS) and the request, adds x-ms-date/x-ms-version and authorizes it.</summary>
        internal async Task<HttpRequestMessage> BuildAsync(HttpMethod method, string urlWithoutQuery, string key, IList<KeyValuePair<string, string>> query,
            Func<HttpContent>? content, IEnumerable<KeyValuePair<string, string>> headers, bool forceRefresh, CancellationToken cancellationToken)
        {
            string queryText = string.Join("&", query.Select(p => StorageKeys.UriEncode(p.Key, true) + "=" + StorageKeys.UriEncode(p.Value, true)));
            string auth = _auth.AuthQuery;
            if (auth.Length > 0)
            {
                queryText = queryText.Length > 0 ? queryText + "&" + auth : auth;
            }

            string url = urlWithoutQuery + (queryText.Length > 0 ? "?" + queryText : "");
            var uri = new Uri(url, UriKind.Absolute);
            if (!string.Equals(StorageKeys.PathOf(urlWithoutQuery), uri.AbsolutePath, StringComparison.Ordinal))
            {
                throw Http.Validation("request", key, StorageKeys.PathChangedText(uri.AbsolutePath));
            }

            var request = new HttpRequestMessage(method, uri);
            if (content != null)
            {
                request.Content = content();
            }

            request.Headers.TryAddWithoutValidation("x-ms-date", Clock().ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
            foreach (KeyValuePair<string, string> header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            await _auth.ApplyAsync(request, new AzureRequestInfo(AccountName, query), forceRefresh, cancellationToken).ConfigureAwait(false);
            return request;
        }

        private static readonly IList<KeyValuePair<string, string>> NoQuery = new KeyValuePair<string, string>[0];

        private static readonly KeyValuePair<string, string>[] NoHeaders = new KeyValuePair<string, string>[0];

        /// <summary>404 for a missing blob is an answer, not an error; a missing container is an error.</summary>
        private static bool IsBlobNotFound(int status, string errorCode)
        {
            return status == 404 && !string.Equals(errorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase);
        }

        private static KeyValuePair<string, string> Pair(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }

        private static KeyValuePair<string, string> Header(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }
    }
}
