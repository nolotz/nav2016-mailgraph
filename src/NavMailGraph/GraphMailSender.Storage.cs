using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Storage;

namespace NavMailGraph
{
    /// <summary>
    /// Object storage integration of <see cref="GraphMailSender"/> (docs/DESIGN-v2.md section 2.4): attachments from storage
    /// (<see cref="GraphMailMessage.AddAttachmentFromStorage"/>) are downloaded into temp files for one send, and with
    /// <see cref="UseLinkStorage"/> large attachments are uploaded and replaced by download links. The caller's message is
    /// never changed: the send works on a copy (<see cref="GraphMailMessage.CloneForSend"/>).
    /// </summary>
    public sealed partial class GraphMailSender
    {
        /// <summary>Default link text: {0} = file name, {1} = size.</summary>
        internal const string DefaultLinkText = "Download {0} ({1})";

        /// <summary>Key layout of link uploads (before the storage's KeyPrefix).</summary>
        internal const string LinkKeyRoot = "mail-attachments/";

        /// <summary>Test hook: clock for link keys and expiry texts (null = DateTime.UtcNow).</summary>
        internal Func<DateTime>? LinkClockOverride { get; set; }

        /// <summary>Test hook: directory for storage attachment temp files (null = Path.GetTempPath()).</summary>
        internal string? TempDirectoryOverride { get; set; }

        private async Task SendWithStorageAsync(GraphMailMessage message, CancellationToken cancellationToken)
        {
            bool fromStorage = message.AttachmentList.Exists(a => a.IsFromStorage);
            if (_linkStorage == null && !fromStorage)
            {
                await SendCoreAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Everything that needs no network first, so a bad recipient does not cost a download or an upload.
            ResolveMailbox(message);
            ValidateMessage(message);
            ObjectStorageClient? linkStorage = _linkStorage;
            string linkText = linkStorage == null ? "" : ValidateLinkSettings();
            if (linkStorage != null)
            {
                _diag.Log("link storage: threshold=" + _linkThresholdBytes + " bytes, validity=" + _linkValidMinutes + " min, storage: " + linkStorage.Describe());
            }

            var tempFiles = new List<string>();
            var uploaded = new List<KeyValuePair<ObjectStorageClient, string>>();
            try
            {
                var attachments = new List<MailAttachment>(message.AttachmentList.Count);
                foreach (MailAttachment attachment in message.AttachmentList)
                {
                    attachments.Add(attachment.IsFromStorage
                        ? await DownloadStorageAttachmentAsync(attachment, tempFiles, cancellationToken).ConfigureAwait(false)
                        : attachment);
                }

                string body = message.Body;
                if (linkStorage != null)
                {
                    var links = new List<KeyValuePair<string, string>>();
                    var remaining = new List<MailAttachment>(attachments.Count);
                    foreach (MailAttachment attachment in attachments)
                    {
                        long size = LinkCandidateSize(attachment);
                        if (size < 0)
                        {
                            remaining.Add(attachment);
                            continue;
                        }

                        links.Add(await UploadAsLinkAsync(linkStorage, attachment, size, linkText, uploaded, cancellationToken).ConfigureAwait(false));
                    }

                    if (links.Count > 0)
                    {
                        body = AppendLinks(body, message.HtmlFormatted, links);
                    }

                    attachments = remaining;
                }

                GraphMailMessage effective = message.CloneForSend(body, attachments);
                await SendCoreAsync(effective, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GraphMailException? graphError = ex as GraphMailException;
                if (uploaded.Count > 0 && (graphError == null || !graphError.OutcomeUnknown))
                {
                    // The mail was not sent (a later upload or the send failed): remove the uploaded link targets (best effort).
                    // When the outcome of the send is unknown the links may be in a delivered mail, so they are kept.
                    await DeleteUploadsAsync(uploaded).ConfigureAwait(false);
                }
                else if (uploaded.Count > 0)
                {
                    _diag.Log("uploaded link targets kept: the outcome of the send is unknown");
                }

                throw;
            }
            finally
            {
                foreach (string path in tempFiles)
                {
                    TryDeleteFile(path);
                }
            }
        }

        /// <summary>Checks threshold, validity and link text; returns the effective link text.</summary>
        private string ValidateLinkSettings()
        {
            if (_linkThresholdBytes <= 0)
            {
                throw GraphMailException.Validation("Validation failed: the link storage threshold must be greater than 0 bytes (UseLinkStorage).");
            }

            if (_linkValidMinutes <= 0)
            {
                throw GraphMailException.Validation("Validation failed: the link validity must be greater than 0 minutes (UseLinkStorage).");
            }

            string text = _linkText.Trim().Length == 0 ? DefaultLinkText : _linkText;
            try
            {
                string.Format(CultureInfo.InvariantCulture, text, "a.pdf", "1 KB", "2026-01-01 00:00 UTC");
            }
            catch (FormatException)
            {
                throw GraphMailException.Validation("Validation failed: the link text '" + text + "' is not a valid format text (use {0} = file name, {1} = size, {2} = expiry; write {{ and }} for braces).");
            }

            return text;
        }

        /// <summary>Size of an attachment that goes out as a link, or -1 when it stays an attachment (inline, below threshold, unreadable).</summary>
        private long LinkCandidateSize(MailAttachment attachment)
        {
            if (attachment.IsInline || !attachment.Exists())
            {
                return -1;
            }

            long size;
            try
            {
                size = attachment.GetSize();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return -1;
            }

            return size >= _linkThresholdBytes ? size : -1;
        }

        private async Task<KeyValuePair<string, string>> UploadAsLinkAsync(ObjectStorageClient storage, MailAttachment attachment, long size, string linkText,
            List<KeyValuePair<ObjectStorageClient, string>> uploaded, CancellationToken cancellationToken)
        {
            DateTime now = (LinkClockOverride ?? (() => DateTime.UtcNow))().ToUniversalTime();
            string key = LinkKeyRoot + now.ToString("yyyy", CultureInfo.InvariantCulture) + "/" + now.ToString("MM", CultureInfo.InvariantCulture) + "/"
                + Guid.NewGuid().ToString("N") + "/" + SafeKeySegment(attachment.Name);
            string label = "link storage: '" + attachment.Name + "' (" + size + " bytes)";
            storage.BeginExternalOperation("LinkUpload");
            string url;
            try
            {
                // The link is created first (local signing, no network): a mode without links fails before anything is uploaded.
                url = storage.CreateDownloadUrlCore(key, _linkValidMinutes);
                _diag.Log(label + ": uploading to key " + key);
                using (Stream source = attachment.OpenRead())
                {
                    await storage.UploadFromStreamAsync(source, size, key, attachment.ContentType, cancellationToken).ConfigureAwait(false);
                }

                uploaded.Add(new KeyValuePair<ObjectStorageClient, string>(storage, key));
                storage.EndExternalOperation(null);
            }
            catch (Exception ex)
            {
                storage.EndExternalOperation(ex);
                throw ObjectStorageClient.WrapForCaller(ex, "Attachment '" + attachment.Name + "' (" + size + " bytes) could not be sent as a download link (UseLinkStorage)");
            }

            string expiry = now.AddMinutes(_linkValidMinutes).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            string text = string.Format(CultureInfo.InvariantCulture, linkText, attachment.Name, FormatSize(size), expiry);
            _diag.Log(label + ": replaced by a download link valid " + _linkValidMinutes + " min (until " + expiry + ")");
            return new KeyValuePair<string, string>(text, url);
        }

        private async Task<MailAttachment> DownloadStorageAttachmentAsync(MailAttachment attachment, List<string> tempFiles, CancellationToken cancellationToken)
        {
            ObjectStorageClient storage = attachment.Storage!;
            string directory = TempDirectoryOverride ?? Path.GetTempPath();
            string path = Path.Combine(directory, "nmg-" + Guid.NewGuid().ToString("N") + ".tmp");
            tempFiles.Add(path);
            storage.BeginExternalOperation("DownloadAttachment");
            try
            {
                long bytes;
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, StorageHttp.CopyBufferSize, true))
                {
                    bytes = await storage.DownloadCoreAsync(attachment.StorageKey, file, cancellationToken).ConfigureAwait(false);
                }

                storage.EndExternalOperation(null);
                _diag.Log("attachment '" + attachment.Name + "': " + bytes + " bytes downloaded from object storage key " + attachment.StorageKey + " into a temp file");
            }
            catch (Exception ex)
            {
                storage.EndExternalOperation(ex);
                throw ObjectStorageClient.WrapForCaller(ex, "Attachment '" + attachment.Name + "' could not be downloaded from object storage (key " + attachment.StorageKey + ")");
            }

            return new MailAttachment(attachment.Name, attachment.ContentType, path, null, attachment.IsInline, attachment.ContentId);
        }

        private async Task DeleteUploadsAsync(List<KeyValuePair<ObjectStorageClient, string>> uploaded)
        {
            foreach (KeyValuePair<ObjectStorageClient, string> entry in uploaded)
            {
                try
                {
                    await entry.Key.DeleteCoreAsync(entry.Value, CancellationToken.None).ConfigureAwait(false);
                    _diag.Log("link storage: removed " + entry.Value + " (mail not sent)");
                }
                catch (Exception ex)
                {
                    _diag.Log("link storage: could not remove " + entry.Value + ": " + GraphClient.FirstLine(StorageHttp.Redact(ex.Message)));
                }
            }
        }

        /// <summary>Appends the link lines: HTML paragraphs (before the last &lt;/body&gt; when present) or plain "text: url" lines.</summary>
        internal static string AppendLinks(string body, bool html, IList<KeyValuePair<string, string>> links)
        {
            body = body ?? "";
            var sb = new StringBuilder();
            if (html)
            {
                foreach (KeyValuePair<string, string> link in links)
                {
                    sb.Append("<p><a href=\"").Append(HtmlEscape(link.Value)).Append("\">").Append(HtmlEscape(link.Key)).Append("</a></p>");
                }

                int end = body.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                return end >= 0 ? body.Substring(0, end) + sb + body.Substring(end) : body + sb;
            }

            foreach (KeyValuePair<string, string> link in links)
            {
                sb.Append("\r\n").Append(link.Key).Append(": ").Append(link.Value);
            }

            if (body.Length == 0)
            {
                return sb.ToString().Substring(2);
            }

            return body + (body.EndsWith("\n", StringComparison.Ordinal) ? "" : "\r\n") + sb;
        }

        /// <summary>"512 bytes", "1.5 KB", "20.0 MB", "1.2 GB" (1024-based, invariant culture).</summary>
        internal static string FormatSize(long bytes)
        {
            const double Kb = 1024.0;
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
            }

            if (bytes < 1024L * 1024)
            {
                return (bytes / Kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (Kb * Kb)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            return (bytes / (Kb * Kb * Kb)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>Escapes &amp; &lt; &gt; " ' for HTML text and attribute values.</summary>
        internal static string HtmlEscape(string value)
        {
            var sb = new StringBuilder((value ?? "").Length + 16);
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// A file name usable as the last key segment: '/', '\', control characters and unpaired surrogates become '_';
        /// "", "." and ".." become "_".
        /// </summary>
        internal static string SafeKeySegment(string fileName)
        {
            string text = (fileName ?? "").Trim();
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                sb.Append(c == '/' || c == '\\' || char.IsControl(c) || char.IsSurrogate(c) ? '_' : c);
            }

            string name = sb.ToString();
            return name.Length == 0 || name == "." || name == ".." ? "_" : name;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // best effort: a temp file that cannot be deleted is left to the OS temp cleanup
            }
        }
    }
}
