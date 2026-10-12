using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// AWS Signature Version 4 for S3 (header auth and presigned query auth). Every step is a separate pure function so the
    /// published AWS test vectors (docs/research-notes-v2.md) can check canonical request, string to sign and signature
    /// independently.
    /// </summary>
    internal static class SigV4
    {
        internal const string Algorithm = "AWS4-HMAC-SHA256";

        internal const string Service = "s3";

        internal const string Terminator = "aws4_request";

        /// <summary>Hex SHA-256 of an empty body.</summary>
        internal const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        internal const string UnsignedPayload = "UNSIGNED-PAYLOAD";

        /// <summary>Presigned URLs are valid for at most 7 days.</summary>
        internal const int MaxPresignSeconds = 604800;

        /// <summary>yyyyMMdd'T'HHmmss'Z'.</summary>
        internal static string AmzDate(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>yyyyMMdd.</summary>
        internal static string DateStamp(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Canonical query string: names and values UriEncoded individually (also '/'), sorted by encoded name (then value),
        /// a parameter without value rendered as "name=".
        /// </summary>
        internal static string CanonicalQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
        {
            var encoded = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string?> p in parameters)
            {
                encoded.Add(new KeyValuePair<string, string>(StorageKeys.UriEncode(p.Key, true), StorageKeys.UriEncode(p.Value ?? "", true)));
            }

            return string.Join("&", encoded
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value));
        }

        /// <summary>
        /// Query string as sent on the wire: same encoding and order as the canonical form, but a flag parameter (null value,
        /// e.g. "uploads") is written without '='.
        /// </summary>
        internal static string WireQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
        {
            return string.Join("&", parameters
                .Select(p => new KeyValuePair<string, string?>(StorageKeys.UriEncode(p.Key, true), p.Value == null ? null : StorageKeys.UriEncode(p.Value, true)))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value ?? "", StringComparer.Ordinal)
                .Select(p => p.Value == null ? p.Key : p.Key + "=" + p.Value));
        }

        /// <summary>
        /// Canonical headers block ("name:value\n" per header, lowercase names sorted, values trimmed with inner runs of
        /// spaces collapsed) and the matching SignedHeaders list.
        /// </summary>
        internal static string CanonicalHeaders(IEnumerable<KeyValuePair<string, string>> headers, out string signedHeaders)
        {
            var sorted = headers
                .Select(h => new KeyValuePair<string, string>(h.Key.Trim().ToLowerInvariant(), CollapseSpaces(h.Value ?? "")))
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .ToList();
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> h in sorted)
            {
                sb.Append(h.Key).Append(':').Append(h.Value).Append('\n');
            }

            signedHeaders = string.Join(";", sorted.Select(h => h.Key));
            return sb.ToString();
        }

        /// <summary>Method\nCanonicalURI\nCanonicalQuery\nCanonicalHeaders\nSignedHeaders\nHashedPayload.</summary>
        internal static string CanonicalRequest(string method, string canonicalUri, string canonicalQuery, string canonicalHeaders, string signedHeaders, string payloadHash)
        {
            return method + "\n" + canonicalUri + "\n" + canonicalQuery + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;
        }

        /// <summary>yyyyMMdd/region/s3/aws4_request.</summary>
        internal static string Scope(string dateStamp, string region)
        {
            return dateStamp + "/" + region + "/" + Service + "/" + Terminator;
        }

        internal static string StringToSign(string amzDate, string scope, string canonicalRequest)
        {
            return Algorithm + "\n" + amzDate + "\n" + scope + "\n" + Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));
        }

        internal static byte[] SigningKey(string secretAccessKey, string dateStamp, string region)
        {
            byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
            byte[] dateRegionKey = Hmac(dateKey, region);
            byte[] dateRegionServiceKey = Hmac(dateRegionKey, Service);
            return Hmac(dateRegionServiceKey, Terminator);
        }

        internal static string Signature(byte[] signingKey, string stringToSign)
        {
            return Hex(Hmac(signingKey, stringToSign));
        }

        internal static string AuthorizationHeader(string accessKeyId, string scope, string signedHeaders, string signature)
        {
            return Algorithm + " Credential=" + accessKeyId + "/" + scope + ",SignedHeaders=" + signedHeaders + ",Signature=" + signature;
        }

        /// <summary>
        /// Presigned GET/PUT URL. <paramref name="baseUrl"/> is scheme+authority (no trailing slash), <paramref name="canonicalUri"/>
        /// the already encoded path; only "host" is signed and the payload hash is UNSIGNED-PAYLOAD.
        /// </summary>
        internal static string PresignUrl(string method, string baseUrl, string host, string canonicalUri, string region, string accessKeyId, string secretAccessKey, string? sessionToken, DateTime utcNow, int expiresSeconds)
        {
            string amzDate = AmzDate(utcNow);
            string dateStamp = DateStamp(utcNow);
            string scope = Scope(dateStamp, region);
            var query = new List<KeyValuePair<string, string?>>
            {
                new KeyValuePair<string, string?>("X-Amz-Algorithm", Algorithm),
                new KeyValuePair<string, string?>("X-Amz-Credential", accessKeyId + "/" + scope),
                new KeyValuePair<string, string?>("X-Amz-Date", amzDate),
                new KeyValuePair<string, string?>("X-Amz-Expires", expiresSeconds.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("X-Amz-SignedHeaders", "host"),
            };
            if (!string.IsNullOrEmpty(sessionToken))
            {
                query.Add(new KeyValuePair<string, string?>("X-Amz-Security-Token", sessionToken));
            }

            string canonicalQuery = CanonicalQueryString(query);
            string signedHeaders;
            string canonicalHeaders = CanonicalHeaders(new[] { new KeyValuePair<string, string>("host", host) }, out signedHeaders);
            string canonicalRequest = CanonicalRequest(method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, UnsignedPayload);
            string signature = Signature(SigningKey(secretAccessKey, dateStamp, region), StringToSign(amzDate, scope, canonicalRequest));
            return baseUrl + canonicalUri + "?" + canonicalQuery + "&X-Amz-Signature=" + signature;
        }

        internal static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        internal static string Sha256Hex(byte[] data, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data, offset, count));
            }
        }

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static byte[] Hmac(byte[] key, string data)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            }
        }

        private static string CollapseSpaces(string value)
        {
            string trimmed = value.Trim();
            var sb = new StringBuilder(trimmed.Length);
            bool space = false;
            foreach (char c in trimmed)
            {
                if (c == ' ')
                {
                    if (!space)
                    {
                        sb.Append(c);
                    }

                    space = true;
                    continue;
                }

                space = false;
                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
