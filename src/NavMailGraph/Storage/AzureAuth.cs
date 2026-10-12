using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Storage
{
    /// <summary>What an <see cref="IStorageAuth"/> needs to authorize one Azure Blob request.</summary>
    internal sealed class AzureRequestInfo
    {
        internal AzureRequestInfo(string accountName, IList<KeyValuePair<string, string>> decodedQuery)
        {
            AccountName = accountName;
            DecodedQuery = decodedQuery;
        }

        /// <summary>Storage account name (first part of CanonicalizedResource).</summary>
        internal string AccountName { get; }

        /// <summary>Query parameters of the operation (decoded; without SAS parameters).</summary>
        internal IList<KeyValuePair<string, string>> DecodedQuery { get; }
    }

    /// <summary>
    /// Authorization mode of the Azure Blob backend. Implementations: <see cref="AzureSharedKeyAuth"/>, <see cref="AzureSasAuth"/>;
    /// a bearer-token mode (Entra ID, scope https://storage.azure.com/.default) is added in the integration phase by
    /// implementing this interface over an <see cref="ITokenProvider"/> (CanRefresh = true, AuthQuery = "").
    /// </summary>
    internal interface IStorageAuth
    {
        /// <summary>"SharedKey", "SAS", "Entra", ... (for Describe and error texts).</summary>
        string Mode { get; }

        /// <summary>Token appended to every request URL (SAS mode), otherwise "". Never logged.</summary>
        string AuthQuery { get; }

        /// <summary>True when a 401 may be retried once after <see cref="ApplyAsync"/> with forceRefresh = true (bearer tokens).</summary>
        bool CanRefresh { get; }

        /// <summary>Adds the authorization header(s). Called for every attempt after all other headers and the content are set.</summary>
        Task ApplyAsync(HttpRequestMessage request, AzureRequestInfo info, bool forceRefresh, CancellationToken cancellationToken);

        /// <summary>
        /// A read-only download URL for the blob, or "" with <paramref name="error"/> set when this mode cannot create one.
        /// <paramref name="blobUrlWithoutQuery"/> is the encoded blob URL; <paramref name="containerName"/>/<paramref name="blobName"/> are decoded.
        /// </summary>
        string CreateReadUrl(string blobUrlWithoutQuery, string accountName, string containerName, string blobName, DateTime expiresUtc, string apiVersion, bool httpsOnly, out string error);

        /// <summary>No secrets.</summary>
        string Describe();
    }

    /// <summary>Azure Storage Shared Key signing (Blob service 2009-09-19+ layout, Content-Length empty when 0) and service SAS (sv 2020-12-06 layout).</summary>
    internal static class AzureSigning
    {
        /// <summary>
        /// VERB\nContent-Encoding\nContent-Language\nContent-Length\nContent-MD5\nContent-Type\nDate\nIf-Modified-Since\nIf-Match\nIf-None-Match\nIf-Unmodified-Since\nRange\n
        /// + CanonicalizedHeaders + CanonicalizedResource. A Content-Length of 0 (or none) is written as an empty line.
        /// </summary>
        internal static string StringToSign(string verb, string contentEncoding, string contentLanguage, long? contentLength, string contentMd5, string contentType,
            string date, string ifModifiedSince, string ifMatch, string ifNoneMatch, string ifUnmodifiedSince, string range, string canonicalizedHeaders, string canonicalizedResource)
        {
            string length = contentLength.HasValue && contentLength.Value > 0 ? contentLength.Value.ToString(CultureInfo.InvariantCulture) : "";
            return verb.ToUpperInvariant() + "\n"
                + contentEncoding + "\n"
                + contentLanguage + "\n"
                + length + "\n"
                + contentMd5 + "\n"
                + contentType + "\n"
                + date + "\n"
                + ifModifiedSince + "\n"
                + ifMatch + "\n"
                + ifNoneMatch + "\n"
                + ifUnmodifiedSince + "\n"
                + range + "\n"
                + canonicalizedHeaders
                + canonicalizedResource;
        }

        /// <summary>
        /// All x-ms-* headers: lowercase names, sorted ascending, linear whitespace in values collapsed to one space, trimmed,
        /// each "name:value\n" (empty values kept as "name:\n").
        /// </summary>
        internal static string CanonicalizedHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> h in headers
                .Select(h => new KeyValuePair<string, string>(h.Key.Trim().ToLowerInvariant(), CollapseWhitespace(h.Value ?? "")))
                .Where(h => h.Key.StartsWith("x-ms-", StringComparison.Ordinal))
                .OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                sb.Append(h.Key).Append(':').Append(h.Value).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// "/" + account + encoded URI path, then for each query parameter (name lowercased, sorted ascending, values decoded,
        /// several values sorted and comma-joined) "\nname:value".
        /// </summary>
        internal static string CanonicalizedResource(string accountName, string encodedPath, IEnumerable<KeyValuePair<string, string>> decodedQuery)
        {
            var sb = new StringBuilder();
            sb.Append('/').Append(accountName).Append(string.IsNullOrEmpty(encodedPath) ? "/" : encodedPath);
            foreach (IGrouping<string, string> group in decodedQuery
                .GroupBy(p => p.Key.ToLowerInvariant(), p => p.Value ?? "")
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append('\n').Append(group.Key).Append(':').Append(string.Join(",", group.OrderBy(v => v, StringComparer.Ordinal)));
            }

            return sb.ToString();
        }

        /// <summary>Base64(HMAC-SHA256(UTF8(stringToSign), key)).</summary>
        internal static string Sign(byte[] key, string stringToSign)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            }
        }

        /// <summary>
        /// sv of generated account-key service SAS links. Pinned (independent of x-ms-version): the 16-field StringToSign below is
        /// the newest documented service SAS layout and is only valid for this sv and later.
        /// </summary>
        internal const string ServiceSasVersion = "2020-12-06";

        /// <summary>
        /// Service SAS StringToSign, Blob, sv 2020-12-06 and later (16 lines, no trailing newline): sp, st, se,
        /// canonicalizedResource, si, sip, spr, sv, sr, snapshot time, ses, rscc, rscd, rsce, rscl, rsct.
        /// </summary>
        internal static string ServiceSasStringToSign(string permissions, string start, string expiry, string canonicalizedResource, string identifier, string ip,
            string protocol, string version, string resource, string snapshotTime, string encryptionScope, string rscc, string rscd, string rsce, string rscl, string rsct)
        {
            return string.Join("\n", new[]
            {
                permissions, start, expiry, canonicalizedResource, identifier, ip, protocol, version, resource, snapshotTime, encryptionScope, rscc, rscd, rsce, rscl, rsct,
            });
        }

        /// <summary>"/blob/{account}/{container}/{blob}" (decoded, no trailing slash).</summary>
        internal static string SasCanonicalizedResource(string accountName, string containerName, string blobName)
        {
            return "/blob/" + accountName + "/" + containerName + (string.IsNullOrEmpty(blobName) ? "" : "/" + blobName);
        }

        /// <summary>ISO 8601 UTC without fractions, e.g. 2026-10-12T08:00:00Z.</summary>
        internal static string SasTime(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>Read-only blob service SAS query (without '?'): sv, se, sr=b, sp=r, spr, sig.</summary>
        internal static string BlobReadSasQuery(byte[] accountKey, string accountName, string containerName, string blobName, DateTime expiresUtc, string version, bool httpsOnly)
        {
            string se = SasTime(expiresUtc);
            string spr = httpsOnly ? "https" : "https,http";
            string stringToSign = ServiceSasStringToSign("r", "", se, SasCanonicalizedResource(accountName, containerName, blobName), "", "", spr, version, "b", "", "", "", "", "", "", "");
            string sig = Sign(accountKey, stringToSign);
            return "sv=" + StorageKeys.UriEncode(version, true)
                + "&spr=" + StorageKeys.UriEncode(spr, true)
                + "&se=" + StorageKeys.UriEncode(se, true)
                + "&sr=b&sp=r&sig=" + StorageKeys.UriEncode(sig, true);
        }

        private static string CollapseWhitespace(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool pending = false;
            foreach (char c in value.Trim())
            {
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    pending = true;
                    continue;
                }

                if (pending)
                {
                    sb.Append(' ');
                    pending = false;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }

    /// <summary>Shared Key authorization: "Authorization: SharedKey account:signature" over the request as it will be sent.</summary>
    internal sealed class AzureSharedKeyAuth : IStorageAuth
    {
        private readonly byte[] _key;

        internal AzureSharedKeyAuth(byte[] accountKey)
        {
            _key = accountKey;
        }

        public string Mode
        {
            get { return "SharedKey"; }
        }

        public string AuthQuery
        {
            get { return ""; }
        }

        public bool CanRefresh
        {
            get { return false; }
        }

        public Task ApplyAsync(HttpRequestMessage request, AzureRequestInfo info, bool forceRefresh, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "SharedKey " + info.AccountName + ":" + Sign(request, info));
            return Task.FromResult(0);
        }

        /// <summary>The signature for <paramref name="request"/> (exposed for tests).</summary>
        internal string Sign(HttpRequestMessage request, AzureRequestInfo info)
        {
            return AzureSigning.Sign(_key, BuildStringToSign(request, info));
        }

        /// <summary>StringToSign from the actual request headers/content (what the service recomputes).</summary>
        internal static string BuildStringToSign(HttpRequestMessage request, AzureRequestInfo info)
        {
            var msHeaders = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                if (header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
                {
                    msHeaders.Add(new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)));
                }
            }

            HttpContent? content = request.Content;
            long? length = content == null ? null : content.Headers.ContentLength;
            string contentType = content == null || content.Headers.ContentType == null ? "" : content.Headers.ContentType.ToString();
            string encodedPath = request.RequestUri == null ? "/" : request.RequestUri.AbsolutePath;
            return AzureSigning.StringToSign(
                request.Method.Method, "", "", length, "", contentType, "", "", "", "", "", "",
                AzureSigning.CanonicalizedHeaders(msHeaders),
                AzureSigning.CanonicalizedResource(info.AccountName, encodedPath, info.DecodedQuery));
        }

        /// <summary>
        /// Read SAS with sv <see cref="AzureSigning.ServiceSasVersion"/> (not <paramref name="apiVersion"/>: the StringToSign layout
        /// depends on sv). A configured x-ms-version older than that means the service may not accept the SAS: error.
        /// </summary>
        public string CreateReadUrl(string blobUrlWithoutQuery, string accountName, string containerName, string blobName, DateTime expiresUtc, string apiVersion, bool httpsOnly, out string error)
        {
            if (!string.IsNullOrEmpty(apiVersion) && string.CompareOrdinal(apiVersion, AzureSigning.ServiceSasVersion) < 0)
            {
                error = "download links need AzureApiVersion " + AzureSigning.ServiceSasVersion + " or later (the SAS is signed with sv "
                    + AzureSigning.ServiceSasVersion + "; configured: " + apiVersion + ")";
                return "";
            }

            error = "";
            return blobUrlWithoutQuery + "?" + AzureSigning.BlobReadSasQuery(_key, accountName, containerName, blobName, expiresUtc, AzureSigning.ServiceSasVersion, httpsOnly);
        }

        public string Describe()
        {
            return "SharedKey";
        }
    }

    /// <summary>SAS URL mode: the given SAS token is appended to every request; no Authorization header.</summary>
    internal sealed class AzureSasAuth : IStorageAuth
    {
        private readonly string _sas;

        internal AzureSasAuth(string sasQuery)
        {
            _sas = sasQuery;
        }

        public string Mode
        {
            get { return "SAS"; }
        }

        public string AuthQuery
        {
            get { return _sas; }
        }

        public bool CanRefresh
        {
            get { return false; }
        }

        public Task ApplyAsync(HttpRequestMessage request, AzureRequestInfo info, bool forceRefresh, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public string CreateReadUrl(string blobUrlWithoutQuery, string accountName, string containerName, string blobName, DateTime expiresUtc, string apiVersion, bool httpsOnly, out string error)
        {
            error = "";
            return blobUrlWithoutQuery + "?" + _sas;
        }

        /// <summary>Expiry (se) and permissions (sp) of the SAS for Describe; never the signature.</summary>
        public string Describe()
        {
            string se = "";
            string sp = "";
            foreach (string part in _sas.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string name = part.Substring(0, eq);
                string value = Uri.UnescapeDataString(part.Substring(eq + 1));
                if (name == "se")
                {
                    se = value;
                }
                else if (name == "sp")
                {
                    sp = value;
                }
            }

            return "SAS" + (sp.Length > 0 ? " sp=" + sp : "") + (se.Length > 0 ? " se=" + se : "");
        }
    }
}
