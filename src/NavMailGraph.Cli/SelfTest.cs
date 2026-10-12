using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Cli
{
    /// <summary>
    /// 'nmg selftest': fully offline end-to-end run of the real GraphMailSender against an in-process fake Graph server
    /// (HttpListener on 127.0.0.1). Uses only the public library API: GraphBaseUrl points to the fake server and
    /// UseTokenProvider supplies a fake token, so no Entra or Graph call leaves the machine.
    /// Scenarios:
    ///   1. small mail -> POST /sendMail; the first attempt is answered 429 + Retry-After: 1 (retry), fixed SenderMailbox with
    ///      a differing From address -> no "from" property, From address in replyTo.
    ///   2. 5 MiB file attachment -> draft flow: create draft, createUploadSession, chunked PUTs (no Authorization header,
    ///      contiguous Content-Range, 200 then 201), POST /send with Content-Length 0; SHA-256 of the uploaded bytes checked.
    ///   3. same message with UseDraftForLargeMessages = false -> validation error, no HTTP request.
    /// </summary>
    internal static class SelfTest
    {
        private const string Mailbox = "selftest@example.invalid";

        private const string CallerFrom = "caller@example.invalid";

        private const string FakeToken = "selftest-access-token-0123456789";

        private const string UploadSecret = "selftest-upload-secret";

        private const string DraftId = "AAMkSelfTest==";

        private const int AttachmentBytes = 5 * 1024 * 1024;

        private static int _failures;

        internal static int Run(bool verbose)
        {
            _failures = 0;
            int port = FreePort();
            string baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            string attachmentPath = Path.Combine(Path.GetTempPath(), "nmg-selftest-" + Guid.NewGuid().ToString("N") + ".bin");
            Console.Out.WriteLine("nmg selftest: fake Graph server on " + baseUrl + " (offline, library " + GraphMailSender.Version + ")");

            using (var server = new FakeGraphServer(baseUrl))
            {
                try
                {
                    byte[] expectedHash = WriteAttachment(attachmentPath);
                    Scenario1(server, baseUrl, verbose);
                    Scenario2(server, baseUrl, attachmentPath, expectedHash, verbose);
                    Scenario3(server, baseUrl, attachmentPath, verbose);
                }
                catch (Exception ex)
                {
                    Fail("selftest aborted: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    try
                    {
                        File.Delete(attachmentPath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            Console.Out.WriteLine(_failures == 0 ? "selftest PASSED" : "selftest FAILED (" + _failures + " check(s))");
            return _failures == 0 ? Program.ExitOk : Program.ExitFailure;
        }

        private static void Scenario1(FakeGraphServer server, string baseUrl, bool verbose)
        {
            Console.Out.WriteLine("[1] small mail via sendMail (with one 429 retry)");
            server.Reset();
            server.ThrottleFirstSendMail = true;
            GraphMailSender sender = NewSender(baseUrl);
            sender.SenderMailbox = Mailbox;
            var message = new GraphMailMessage();
            message.FromName = "Caller";
            message.FromAddress = CallerFrom;
            message.Subject = "nmg selftest 1";
            message.Body = "Hello from nmg selftest.";
            message.AddRecipients("Recipient <to@example.invalid>; cc-like@example.invalid");
            message.AddAttachmentBytes(Encoding.UTF8.GetBytes("small attachment"), "small.txt");

            string error = sender.Send(message);
            Finish(sender, verbose, error);
            Check(error.Length == 0, "Send returned success", error);
            Check(sender.LastMessageId.Length == 0, "LastMessageId empty for direct sendMail", sender.LastMessageId);
            List<FakeRequest> sendMails = server.Requests.Where(r => r.Method == "POST" && r.Path.EndsWith("/sendMail", StringComparison.Ordinal)).ToList();
            Check(sendMails.Count == 2, "sendMail retried once after 429", "requests: " + server.Summary());
            Check(server.Requests.All(r => r.Path.Contains("/users/" + Mailbox + "/")), "mailbox in URL with literal '@'", server.Summary());
            Check(server.Requests.All(r => r.Authorization == "Bearer " + FakeToken), "Bearer token on Graph calls", "");
            if (sendMails.Count > 0)
            {
                using (JsonDocument doc = JsonDocument.Parse(sendMails[sendMails.Count - 1].Text))
                {
                    JsonElement msg = doc.RootElement.GetProperty("message");
                    Check(!msg.TryGetProperty("from", out _), "no Graph 'from' property (From is always the mailbox)", "");
                    bool replyTo = msg.TryGetProperty("replyTo", out JsonElement replyToList)
                        && replyToList.EnumerateArray().Any(e => string.Equals(e.GetProperty("emailAddress").GetProperty("address").GetString(), CallerFrom, StringComparison.OrdinalIgnoreCase));
                    Check(replyTo, "caller's From address added to replyTo", "");
                    Check(msg.TryGetProperty("attachments", out JsonElement atts) && atts.GetArrayLength() == 1, "attachment inline in sendMail", "");
                }
            }
        }

        private static void Scenario2(FakeGraphServer server, string baseUrl, string attachmentPath, byte[] expectedHash, bool verbose)
        {
            Console.Out.WriteLine("[2] 5 MiB attachment via draft + upload session");
            server.Reset();
            GraphMailSender sender = NewSender(baseUrl);
            GraphMailMessage message = LargeMessage(attachmentPath);
            string error = sender.Send(message);
            Finish(sender, verbose, error);
            Check(error.Length == 0, "Send returned success", error);
            Check(sender.LastMessageId == DraftId, "LastMessageId is the draft id", sender.LastMessageId);

            string[] expected = { "POST messages", "POST createUploadSession", "PUT upload", "PUT upload", "POST send" };
            string[] actual = server.Requests.Select(r => r.Kind).ToArray();
            Check(expected.SequenceEqual(actual), "request sequence " + string.Join(" -> ", expected), "got " + string.Join(" -> ", actual));
            Check(server.Requests.Where(r => r.Kind == "PUT upload").All(r => r.Authorization.Length == 0), "no Authorization header on upload PUTs", "");
            Check(server.Requests.Where(r => r.Kind != "PUT upload").All(r => r.Authorization == "Bearer " + FakeToken), "Bearer token on Graph calls", "");
            Check(server.Requests.Where(r => r.Kind == "POST send").All(r => r.ContentLength == 0), "POST /send carries Content-Length: 0", "");
            Check(server.Requests.Any(r => r.Path.Contains("/messages/" + DraftId + "/")), "message id keeps '=' literal in URL", server.Summary());
            Check(server.UploadErrors.Count == 0, "Content-Range contiguous and complete", string.Join("; ", server.UploadErrors));
            Check(server.UploadedBytes == AttachmentBytes, "uploaded " + AttachmentBytes + " bytes", server.UploadedBytes.ToString(CultureInfo.InvariantCulture));
            Check(server.UploadHash().SequenceEqual(expectedHash), "SHA-256 of uploaded bytes matches the file", "");
            FakeRequest? session = server.Requests.FirstOrDefault(r => r.Kind == "POST createUploadSession");
            if (session != null)
            {
                using (JsonDocument doc = JsonDocument.Parse(session.Text))
                {
                    JsonElement item = doc.RootElement.GetProperty("AttachmentItem");
                    Check(item.GetProperty("size").GetInt64() == AttachmentBytes, "createUploadSession announces the attachment size", "");
                }
            }

            string diagnostics = sender.LastDiagnostics;
            Check(!diagnostics.Contains(UploadSecret), "upload authtoken redacted in diagnostics", "");
            Check(!diagnostics.Contains(FakeToken), "access token absent from diagnostics", "");
        }

        private static void Scenario3(FakeGraphServer server, string baseUrl, string attachmentPath, bool verbose)
        {
            Console.Out.WriteLine("[3] same message with UseDraftForLargeMessages = false");
            server.Reset();
            GraphMailSender sender = NewSender(baseUrl);
            sender.UseDraftForLargeMessages = false;
            string error = sender.Send(LargeMessage(attachmentPath));
            Finish(sender, verbose, "");
            Check(error.Length > 0, "Send returned a validation error", "");
            Console.Out.WriteLine("      error text: " + error);
            Check(server.Requests.Count == 0, "no HTTP request was made", server.Summary());
        }

        private static GraphMailMessage LargeMessage(string attachmentPath)
        {
            var message = new GraphMailMessage();
            message.FromAddress = Mailbox;
            message.Subject = "nmg selftest 2";
            message.Body = "<p>Large attachment test.</p>";
            message.HtmlFormatted = true;
            message.AddRecipients("to@example.invalid");
            message.AddAttachment(attachmentPath, "selftest-5MiB.bin");
            return message;
        }

        private static GraphMailSender NewSender(string baseUrl)
        {
            var sender = new GraphMailSender("00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000000");
            sender.GraphBaseUrl = baseUrl + "/v1.0";
            sender.UseTokenProvider(new FakeTokenProvider());
            sender.TimeoutSeconds = 30;
            return sender;
        }

        private static void Finish(GraphMailSender sender, bool verbose, string error)
        {
            if (verbose || error.Length > 0)
            {
                Program.PrintDiagnostics(sender.LastDiagnostics);
            }
        }

        private static void Check(bool condition, string what, string detail)
        {
            if (condition)
            {
                Console.Out.WriteLine("  PASS " + what);
            }
            else
            {
                Fail(what + (detail.Length > 0 ? " (" + detail + ")" : ""));
            }
        }

        private static void Fail(string what)
        {
            _failures++;
            Console.Out.WriteLine("  FAIL " + what);
        }

        private static byte[] WriteAttachment(string path)
        {
            var data = new byte[AttachmentBytes];
            new Random(4711).NextBytes(data);
            File.WriteAllBytes(path, data);
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class FakeTokenProvider : ITokenProvider
        {
            public Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
            {
                return Task.FromResult(new AccessToken(FakeToken, DateTimeOffset.UtcNow.AddHours(1)));
            }

            public string Describe()
            {
                return "SelfTest/FakeToken";
            }
        }

        private sealed class FakeRequest
        {
            internal string Method { get; set; } = "";

            internal string Path { get; set; } = "";

            internal string Kind { get; set; } = "";

            internal string Authorization { get; set; } = "";

            internal long ContentLength { get; set; }

            internal string Text { get; set; } = "";
        }

        /// <summary>Minimal fake of the Graph endpoints the library calls. Requests are handled one at a time.</summary>
        private sealed class FakeGraphServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();

            private readonly string _baseUrl;

            private readonly Task _loop;

            private readonly object _gate = new object();

            private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            private bool _throttled;

            internal FakeGraphServer(string baseUrl)
            {
                _baseUrl = baseUrl;
                _listener.Prefixes.Add(baseUrl + "/");
                _listener.Start();
                _loop = Task.Run(LoopAsync);
            }

            internal List<FakeRequest> Requests { get; } = new List<FakeRequest>();

            internal List<string> UploadErrors { get; } = new List<string>();

            internal long UploadedBytes { get; private set; }

            internal bool ThrottleFirstSendMail { get; set; }

            internal void Reset()
            {
                lock (_gate)
                {
                    Requests.Clear();
                    UploadErrors.Clear();
                    UploadedBytes = 0;
                    ThrottleFirstSendMail = false;
                    _throttled = false;
                    _hash.Dispose();
                    _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }
            }

            internal byte[] UploadHash()
            {
                lock (_gate)
                {
                    return _hash.GetHashAndReset();
                }
            }

            internal string Summary()
            {
                lock (_gate)
                {
                    return string.Join(", ", Requests.Select(r => r.Method + " " + r.Path));
                }
            }

            public void Dispose()
            {
                try
                {
                    _listener.Stop();
                    _listener.Close();
                    _loop.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // shutting down
                }

                _hash.Dispose();
            }

            private async Task LoopAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return; // listener stopped
                    }

                    try
                    {
                        Handle(context);
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            UploadErrors.Add("server error: " + ex.Message);
                        }

                        TryRespond(context.Response, 500, "{\"error\":{\"code\":\"SelfTestServerError\",\"message\":\"" + ex.GetType().Name + "\"}}");
                    }
                }
            }

            private void Handle(HttpListenerContext context)
            {
                HttpListenerRequest request = context.Request;
                byte[] body;
                using (var buffer = new MemoryStream())
                {
                    request.InputStream.CopyTo(buffer);
                    body = buffer.ToArray();
                }

                string rawUrl = request.RawUrl ?? "";
                int query = rawUrl.IndexOf('?');
                string path = query >= 0 ? rawUrl.Substring(0, query) : rawUrl;
                var record = new FakeRequest
                {
                    Method = request.HttpMethod,
                    Path = path,
                    Authorization = request.Headers["Authorization"] ?? "",
                    ContentLength = request.ContentLength64,
                };

                string draftPath = "/v1.0/users/" + Mailbox + "/messages/" + DraftId;
                int status;
                string responseBody = "";
                var headers = new Dictionary<string, string>();
                lock (_gate)
                {
                    if (record.Method == "POST" && path.EndsWith("/sendMail", StringComparison.Ordinal))
                    {
                        record.Kind = "POST sendMail";
                        record.Text = Encoding.UTF8.GetString(body);
                        if (ThrottleFirstSendMail && !_throttled)
                        {
                            _throttled = true;
                            status = 429;
                            headers["Retry-After"] = "1";
                            responseBody = "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"Selftest throttling.\"}}";
                        }
                        else
                        {
                            status = 202;
                        }
                    }
                    else if (record.Method == "POST" && path == "/v1.0/users/" + Mailbox + "/messages")
                    {
                        record.Kind = "POST messages";
                        record.Text = Encoding.UTF8.GetString(body);
                        status = 201;
                        responseBody = "{\"id\":\"" + DraftId + "\"}";
                    }
                    else if (record.Method == "POST" && path == draftPath + "/attachments/createUploadSession")
                    {
                        record.Kind = "POST createUploadSession";
                        record.Text = Encoding.UTF8.GetString(body);
                        status = 201;
                        responseBody = "{\"uploadUrl\":\"" + _baseUrl + "/upload/session1?authtoken=" + UploadSecret + "\","
                            + "\"expirationDateTime\":\"" + DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\","
                            + "\"nextExpectedRanges\":[\"0-\"]}";
                    }
                    else if (record.Method == "PUT" && path == "/upload/session1")
                    {
                        record.Kind = "PUT upload";
                        status = HandleChunk(request.Headers["Content-Range"] ?? "", body, out responseBody);
                    }
                    else if (record.Method == "POST" && path == draftPath + "/send")
                    {
                        record.Kind = "POST send";
                        status = 202;
                    }
                    else if (record.Method == "DELETE" && path == draftPath)
                    {
                        record.Kind = "DELETE draft";
                        status = 204;
                    }
                    else
                    {
                        record.Kind = record.Method + " unknown";
                        status = 404;
                        responseBody = "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"Selftest server: no route for " + record.Method + " " + path + "\"}}";
                    }

                    Requests.Add(record);
                }

                foreach (KeyValuePair<string, string> header in headers)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }

                TryRespond(context.Response, status, responseBody);
            }

            /// <summary>Validates one upload PUT (caller holds the lock): contiguous "bytes s-e/t", length matches; 200 + nextExpectedRanges, final 201.</summary>
            private int HandleChunk(string contentRange, byte[] body, out string responseBody)
            {
                responseBody = "";
                long start;
                long end;
                long total;
                if (!TryParseContentRange(contentRange, out start, out end, out total))
                {
                    UploadErrors.Add("bad Content-Range '" + contentRange + "'");
                    responseBody = "{\"error\":{\"code\":\"InvalidRange\",\"message\":\"bad Content-Range\"}}";
                    return 400;
                }

                if (start != UploadedBytes)
                {
                    UploadErrors.Add("range starts at " + start + ", expected " + UploadedBytes);
                }

                if (end - start + 1 != body.Length)
                {
                    UploadErrors.Add("range " + start + "-" + end + " but body has " + body.Length + " bytes");
                }

                if (body.Length > 4 * 1024 * 1024)
                {
                    UploadErrors.Add("chunk larger than 4 MiB: " + body.Length);
                }

                _hash.AppendData(body);
                UploadedBytes += body.Length;
                if (UploadedBytes < total)
                {
                    responseBody = "{\"expirationDateTime\":\"" + DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\",\"nextExpectedRanges\":[\"" + UploadedBytes + "-\"]}";
                    return 200;
                }

                if (UploadedBytes > total)
                {
                    UploadErrors.Add("uploaded " + UploadedBytes + " bytes, more than total " + total);
                }

                return 201;
            }

            private static bool TryParseContentRange(string value, out long start, out long end, out long total)
            {
                start = end = total = 0;
                const string prefix = "bytes ";
                if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string[] parts = value.Substring(prefix.Length).Split('-', '/');
                return parts.Length == 3
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)
                    && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out total)
                    && end >= start && total > end;
            }

            private static void TryRespond(HttpListenerResponse response, int status, string body)
            {
                try
                {
                    response.StatusCode = status;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    if (bytes.Length > 0)
                    {
                        response.ContentType = "application/json";
                    }

                    response.ContentLength64 = bytes.Length;
                    if (bytes.Length > 0)
                    {
                        response.OutputStream.Write(bytes, 0, bytes.Length);
                    }

                    response.Close();
                }
                catch (Exception)
                {
                    // client went away
                }
            }
        }
    }
}
