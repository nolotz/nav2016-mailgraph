using System;
using System.Globalization;
using System.Text;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// Object key validation, SigV4-style URI encoding (also used for Azure blob paths), block ids and part-size math.
    /// Pure functions so they can be unit-tested without HTTP. Rules: docs/DESIGN-v2.md section 5.3 and
    /// docs/research-notes-v2.md ("Amazon S3 / SigV4", "Follow-up (s3)", "Azure Blob Storage REST").
    /// </summary>
    internal static class StorageKeys
    {
        internal const int MiB = 1024 * 1024;

        /// <summary>S3 minimum part size (all parts except the last): 5 MiB.</summary>
        internal const int S3MinPartSize = 5 * MiB;

        /// <summary>S3: at most 10,000 parts per multipart upload.</summary>
        internal const int S3MaxParts = 10000;

        /// <summary>Azure: at most 50,000 committed blocks per block blob.</summary>
        internal const int AzureMaxBlocks = 50000;

        /// <summary>
        /// Upper bound for a single PUT used by this library (S3: 5 GB single PUT; Azure 2019-12-12+: 5,000 MiB Put Blob).
        /// A larger MultipartThresholdBytes is clamped to this value.
        /// </summary>
        internal const long MaxSinglePutBytes = 5000L * MiB;

        /// <summary>Upper bound for one in-memory part buffer (keeps the NAV service tier's memory use predictable).</summary>
        internal const int MaxPartBufferBytes = 512 * MiB;

        private const string HexUpper = "0123456789ABCDEF";

        /// <summary>
        /// Returns "" when <paramref name="key"/> can be sent unchanged through System.Uri/HttpClient on .NET Framework and
        /// modern .NET, otherwise a reason. Rejected: empty keys, a leading '/', backslashes (System.Uri converts them to '/'),
        /// '.' or '..' path segments (System.Uri compacts them, even percent-encoded), control characters (not representable
        /// in the XML list responses) and unpaired UTF-16 surrogates.
        /// </summary>
        internal static string Validate(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "the object key is empty";
            }

            string value = key!;
            if (value[0] == '/')
            {
                return "the object key must not start with '/'";
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    return "the object key must not contain a backslash ('\\'); use '/' as separator";
                }

                if (c < 0x20 || c == 0x7F)
                {
                    return "the object key must not contain control characters";
                }

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    {
                        return "the object key contains an invalid Unicode character (unpaired surrogate)";
                    }

                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return "the object key contains an invalid Unicode character (unpaired surrogate)";
                }
            }

            foreach (string segment in value.Split('/'))
            {
                if (segment == "." || segment == "..")
                {
                    return "the object key must not contain '.' or '..' path segments (they are removed by the HTTP stack)";
                }
            }

            return "";
        }

        /// <summary>
        /// SigV4 UriEncode: A-Z a-z 0-9 '-' '.' '_' '~' stay as they are, every other UTF-8 byte becomes %XX with uppercase hex
        /// (space = %20, never '+'); '/' is kept when <paramref name="encodeSlash"/> is false (object key paths).
        /// </summary>
        internal static string UriEncode(string value, bool encodeSlash)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~')
                {
                    sb.Append(c);
                }
                else if (c == '/' && !encodeSlash)
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('%').Append(HexUpper[b >> 4]).Append(HexUpper[b & 0x0F]);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Azure block id for the 1-based <paramref name="partNumber"/>: Base64 of the zero-padded 6-digit index
        /// (all ids of one blob have the same length, 8 characters).
        /// </summary>
        internal static string BlockId(int partNumber)
        {
            if (partNumber < 1 || partNumber > 999999)
            {
                throw new ArgumentOutOfRangeException(nameof(partNumber));
            }

            return Convert.ToBase64String(Encoding.ASCII.GetBytes(partNumber.ToString("D6", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Part size actually used: the configured size, raised (in whole MiB) when a known <paramref name="totalLength"/>
        /// would otherwise need more than <paramref name="maxParts"/> parts, and never below <paramref name="minPartSize"/>.
        /// Returns -1 when the required part size exceeds <see cref="MaxPartBufferBytes"/>.
        /// </summary>
        internal static int EffectivePartSize(long totalLength, int configuredPartSize, int maxParts, int minPartSize)
        {
            long size = Math.Max(configuredPartSize, minPartSize);
            if (size <= 0)
            {
                size = 8 * MiB;
            }

            if (totalLength > 0 && PartCount(totalLength, size) > maxParts)
            {
                long needed = (totalLength + maxParts - 1) / maxParts;
                size = (needed + MiB - 1) / MiB * MiB;
            }

            return size > MaxPartBufferBytes ? -1 : (int)size;
        }

        /// <summary>Number of parts for <paramref name="totalLength"/> bytes (0 bytes = 1 part).</summary>
        internal static long PartCount(long totalLength, long partSize)
        {
            if (partSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(partSize));
            }

            return totalLength <= 0 ? 1 : (totalLength + partSize - 1) / partSize;
        }

        /// <summary>The path part of an absolute URL string exactly as written ("/" when there is none), without query.</summary>
        internal static string PathOf(string url)
        {
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            int start = url.IndexOf('/', scheme < 0 ? 0 : scheme + 3);
            if (start < 0)
            {
                return "/";
            }

            int end = url.IndexOfAny(new[] { '?', '#' }, start);
            return end < 0 ? url.Substring(start) : url.Substring(start, end - start);
        }

        /// <summary>Error text when System.Uri would send a different path than the one that was built/signed.</summary>
        internal static string PathChangedText(string sentPath)
        {
            return "this .NET runtime would send the object key in a different form (" + sentPath + ") than the one signed; use a key without such characters";
        }

        /// <summary>Joins <paramref name="prefix"/> and <paramref name="key"/> (KeyPrefix is used exactly as configured).</summary>
        internal static string Combine(string? prefix, string? key)
        {
            return (prefix ?? "") + (key ?? "");
        }

        /// <summary>Escapes text for an XML element body.</summary>
        internal static string XmlEscape(string value)
        {
            var sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }
    }
}
