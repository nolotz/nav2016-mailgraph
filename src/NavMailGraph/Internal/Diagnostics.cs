using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NavMailGraph.Internal
{
    /// <summary>
    /// Per-sender diagnostics: an in-memory log of the current operation (what <see cref="GraphMailSender.LastDiagnostics"/>
    /// returns) plus an optional append-only file log. Every line is redacted before it is stored or written, so even a
    /// careless caller cannot leak a bearer token, client secret or client assertion.
    /// </summary>
    internal sealed class Diagnostics
    {
        /// <summary>Upper bound for the in-memory log of one operation (NAV shows it in an error dialog).</summary>
        internal const int MaxBufferChars = 256 * 1024;

        private static readonly object FileSync = new object();

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Redaction patterns. Order matters only in that the generic JWT pattern runs last.
        private static readonly Regex BearerPattern = new Regex(@"(Bearer\s+)[A-Za-z0-9\-_\.~+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FormSecretPattern = new Regex(@"((?:client_secret|client_assertion|password|pwd|authtoken|access_token)=)[^&\s""']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex JsonSecretPattern = new Regex(@"(""(?:access_token|refresh_token|id_token|client_secret|client_assertion|password)""\s*:\s*"")[^""]*("")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex JwtPattern = new Regex(@"eyJ[A-Za-z0-9\-_]{5,}\.[A-Za-z0-9\-_]{5,}\.[A-Za-z0-9\-_]+", RegexOptions.CultureInvariant);

        private readonly object _sync = new object();

        private StringBuilder _buffer = new StringBuilder();

        private string _operationTag = "";

        /// <summary>Optional path of the append-only file log; "" disables it. IO errors are swallowed.</summary>
        internal string LogFilePath { get; set; } = "";

        /// <summary>Starts a new operation: clears the in-memory log and writes a header line.</summary>
        internal void BeginOperation(string name)
        {
            lock (_sync)
            {
                _buffer = new StringBuilder();
                _operationTag = Guid.NewGuid().ToString("N").Substring(0, 8) + " " + name;
            }

            Log("--- " + name + " start (NavMailGraph " + GraphMailSender.Version + ") ---");
        }

        /// <summary>Appends a redacted, time-stamped line to the current operation (and to the file log).</summary>
        internal void Log(string message)
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                + " [" + _operationTag + "] " + Redact(message ?? "");
            lock (_sync)
            {
                if (_buffer.Length < MaxBufferChars)
                {
                    _buffer.AppendLine(line);
                }
            }

            AppendToFile(line);
        }

        /// <summary>The redacted log of the current/last operation.</summary>
        internal string Text
        {
            get
            {
                lock (_sync)
                {
                    return _buffer.ToString();
                }
            }
        }

        /// <summary>Masks bearer tokens, client secrets, client assertions and JWT-looking strings.</summary>
        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            string result = BearerPattern.Replace(text, "$1[redacted]");
            result = FormSecretPattern.Replace(result, "$1[redacted]");
            result = JsonSecretPattern.Replace(result, "$1[redacted]$2");
            result = JwtPattern.Replace(result, "[redacted-jwt]");
            return result;
        }

        /// <summary>Returns the URL without query string (upload URLs carry an auth token in the query).</summary>
        internal static string SafeUrl(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return uri.GetLeftPart(UriPartial.Path);
            }

            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        private void AppendToFile(string line)
        {
            string path = LogFilePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                lock (FileSync)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
                }
            }
            catch (Exception)
            {
                // A broken log path must never break mail sending.
            }
        }
    }
}
