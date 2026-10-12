using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

// The token cache, the diagnostics file lock and the MSAL bridge are process-wide; run tests sequentially so the
// suite stays deterministic (it is fast: no network, no real delays).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NavMailGraph.Tests.Infrastructure
{
    internal static class TestSupport
    {
        internal const string Tenant = "contoso.onmicrosoft.com";

        internal const string ClientId = "11111111-2222-3333-4444-555555555555";

        internal const string Mailbox = "nav@contoso.com";

        internal const string GraphBase = "https://graph.microsoft.com/v1.0";

        internal const string MailboxUrl = GraphBase + "/users/nav@contoso.com";

        /// <summary>Sender over a fake handler with a fake token provider and an instant retry delay that records the requested pauses.</summary>
        internal static GraphMailSender NewSender(FakeHttpMessageHandler handler, FakeTokenProvider? tokens, List<TimeSpan>? delays)
        {
            GraphMailSender sender = GraphMailSender.CreateForTests(Tenant, ClientId, handler);
            sender.UseTokenProvider(tokens ?? new FakeTokenProvider());
            sender.RetryDelayOverride = (delay, ct) =>
            {
                if (delays != null)
                {
                    delays.Add(delay);
                }

                return Task.FromResult(0);
            };
            return sender;
        }

        internal static GraphMailSender NewSender(FakeHttpMessageHandler handler)
        {
            return NewSender(handler, null, null);
        }

        internal static GraphMailMessage NewMessage()
        {
            var message = new GraphMailMessage
            {
                FromName = "NAV Buchhaltung",
                FromAddress = Mailbox,
                Subject = "Rechnung 1001",
                Body = "Hallo",
            };
            message.AddRecipients("max@example.com");
            return message;
        }

        /// <summary>Deterministic, non-repeating-looking content (so misplaced chunks are detected).</summary>
        internal static byte[] Pattern(int length, int seed = 7)
        {
            var bytes = new byte[length];
            unchecked
            {
                uint state = (uint)seed * 2654435761u + 1;
                for (int i = 0; i < length; i++)
                {
                    state = state * 1664525u + 1013904223u;
                    bytes[i] = (byte)(state >> 24);
                }
            }

            return bytes;
        }

        internal static JsonElement Json(RecordedRequest request)
        {
            using (JsonDocument document = JsonDocument.Parse(request.Body))
            {
                return document.RootElement.Clone();
            }
        }

        internal static JsonElement ParseJson(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        internal static bool Has(JsonElement element, string property)
        {
            JsonElement ignored;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out ignored);
        }

        internal static List<string> Addresses(JsonElement recipients)
        {
            return recipients.EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString() ?? "").ToList();
        }

        /// <summary>Parses an application/x-www-form-urlencoded body.</summary>
        internal static Dictionary<string, string> Form(RecordedRequest request)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in request.BodyText.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? "" : pair.Substring(eq + 1);
                result[Decode(key)] = Decode(value);
            }

            return result;
        }

        internal static byte[] Base64UrlDecode(string value)
        {
            string s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2:
                    s += "==";
                    break;
                case 3:
                    s += "=";
                    break;
            }

            return Convert.FromBase64String(s);
        }

        internal static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string GraphError(string code, string message, string requestId)
        {
            return "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\",\"innerError\":{\"date\":\"2026-10-12T06:00:00\",\"request-id\":\"" + requestId
                + "\",\"client-request-id\":\"9a1b2c3d-0000-0000-0000-000000000000\"}}}";
        }

        internal static string Utf8(byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        private static string Decode(string value)
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
    }

    /// <summary>A temporary directory deleted on Dispose.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nmg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string name, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
