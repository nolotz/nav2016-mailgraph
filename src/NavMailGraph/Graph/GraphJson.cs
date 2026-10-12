using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NavMailGraph.Graph
{
    /// <summary>
    /// JSON helpers on top of the in-box <see cref="DataContractJsonSerializer"/> (no NuGet dependency).
    /// Output is UTF-8 without BOM. The serializer orders members alphabetically (harmless) and always escapes '/' as '\/';
    /// <see cref="SerializeToUtf8Bytes{T}"/> removes that escape again because it inflates base64 attachment content
    /// (which contains many '/') by about 1.6 % and pushed near-3 MB attachment POSTs over Graph's 4 MB request limit.
    /// </summary>
    internal static class GraphJson
    {
        private static readonly ConcurrentDictionary<Type, DataContractJsonSerializer> Serializers = new ConcurrentDictionary<Type, DataContractJsonSerializer>();

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static DataContractJsonSerializer For(Type type)
        {
            return Serializers.GetOrAdd(type, t => new DataContractJsonSerializer(t));
        }

        /// <summary>Serializes to UTF-8 bytes (no BOM).</summary>
        internal static byte[] SerializeToUtf8Bytes<T>(T value) where T : class
        {
            using (var stream = new MemoryStream())
            {
                For(typeof(T)).WriteObject(stream, value);
                return UnescapeSolidus(stream.ToArray());
            }
        }

        /// <summary>
        /// Replaces every JSON escape sequence "\/" with "/" (an equivalent JSON text). Walks escape sequences pairwise so an
        /// escaped backslash followed by a slash ("\\\/") is handled correctly. Safe on UTF-8 bytes: '\' and '/' never occur
        /// inside multi-byte sequences.
        /// </summary>
        internal static byte[] UnescapeSolidus(byte[] json)
        {
            int count = 0;
            for (int i = 0; i < json.Length - 1; i++)
            {
                if (json[i] == (byte)'\\')
                {
                    if (json[i + 1] == (byte)'/')
                    {
                        count++;
                    }

                    i++; // skip the escaped character (for \uXXXX the hex digits are copied as plain bytes)
                }
            }

            if (count == 0)
            {
                return json;
            }

            var result = new byte[json.Length - count];
            int w = 0;
            for (int i = 0; i < json.Length; i++)
            {
                byte b = json[i];
                if (b == (byte)'\\' && i + 1 < json.Length)
                {
                    byte next = json[i + 1];
                    if (next != (byte)'/')
                    {
                        result[w++] = b;
                    }

                    result[w++] = next;
                    i++;
                    continue;
                }

                result[w++] = b;
            }

            return result;
        }

        /// <summary>Serializes to a JSON string.</summary>
        internal static string Serialize<T>(T value) where T : class
        {
            return Utf8NoBom.GetString(SerializeToUtf8Bytes(value));
        }

        /// <summary>Deserializes; returns null for empty, non-JSON or structurally incompatible input (never throws).</summary>
        internal static T? Deserialize<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using (var stream = new MemoryStream(Utf8NoBom.GetBytes(json)))
                {
                    return For(typeof(T)).ReadObject(stream) as T;
                }
            }
            catch (Exception)
            {
                // Tolerate HTML error pages from proxies, truncated bodies etc. The caller falls back to the raw text.
                return null;
            }
        }
    }
}
