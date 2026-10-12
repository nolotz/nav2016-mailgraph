using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Internal;
using NavMailGraph.Reading;

namespace NavMailGraph
{
    /// <summary>
    /// Reads and processes mail of one mailbox through Microsoft Graph (application permissions; docs/DESIGN-v2.md sections
    /// 2.2 and 5.1). Every public method is synchronous and returns "" on success or the complete error text; it never throws
    /// for Graph, auth, IO or validation problems. Every Graph call sends Prefer: IdType="ImmutableId", so the ids are stable
    /// across folder moves (and case-sensitive). At most 4 requests per mailbox run concurrently (process-wide).
    /// Permissions: reading needs Mail.Read, MarkAsRead/MarkAsUnread/MoveToFolder/DeleteMessage/EnsureFolder need Mail.ReadWrite.
    /// </summary>
    public sealed class GraphMailbox
    {
        private readonly GraphConnection _connection;

        private readonly string _mailbox;

        private readonly Diagnostics _diag = new Diagnostics();

        private readonly List<GraphMailItem> _messages = new List<GraphMailItem>();

        private readonly List<string> _removedIds = new List<string>();

        /// <summary>Resolved folder ids per normalized path (case-insensitive key), for this instance.</summary>
        private readonly Dictionary<string, string> _folderCache = new Dictionary<string, string>(StringComparer.Ordinal);

        private string _folder = "inbox";

        private string _receivedAfter = "";

        /// <summary>Creates a reader for <paramref name="mailbox"/> (UPN/SMTP address or object id) using the connection's credentials.</summary>
        public GraphMailbox(GraphConnection connection, string mailbox)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _mailbox = (mailbox ?? "").Trim();
            MaxMessages = MailQueries.DefaultMaxMessages;
            IncludeBody = true;
            NextDeltaLink = "";
            LastMovedMessageId = "";
            LastFolderId = "";
            LastRequestId = "";
        }

        /// <summary>The mailbox (UPN/SMTP address or object id).</summary>
        public string Mailbox
        {
            get { return _mailbox; }
        }

        /// <summary>Source folder of Fetch/FetchChanges: well-known name (default "inbox"), folder id, or path such as "Inbox/NAV/Invoices".</summary>
        public string Folder
        {
            get { return _folder; }
            set { _folder = (value ?? "").Trim(); }
        }

        /// <summary>Fetch: only unread messages (appended after the receivedDateTime condition). Default false: use the ReceivedAfter watermark and move processed mail instead.</summary>
        public bool UnreadOnly { get; set; }

        /// <summary>
        /// Watermark: only messages with receivedDateTime greater than or equal to this ISO 8601 text (e.g. the ReceivedText of the
        /// newest imported mail, "2026-10-12T08:00:00Z"). Inclusive, so de-duplicate by Id / InternetMessageId. "" = no filter.
        /// Also bounds the initial FetchChanges("").
        /// </summary>
        public string ReceivedAfter
        {
            get { return _receivedAfter; }
            set { _receivedAfter = (value ?? "").Trim(); }
        }

        /// <summary>Messages per Fetch (default 50, at most 1000). FetchChanges stops after the page that reaches it.</summary>
        public int MaxMessages { get; set; }

        /// <summary>Load the body with the message list (default true).</summary>
        public bool IncludeBody { get; set; }

        /// <summary>False (default): body as sent (HTML or text). True: Graph converts the body to text.</summary>
        public bool BodyAsText { get; set; }

        /// <summary>Fetch: true when more matching messages exist than MaxMessages (call Fetch again after processing). FetchChanges: true when NextDeltaLink is a page link to continue with.</summary>
        public bool MoreAvailable { get; private set; }

        /// <summary>Link to pass to the next FetchChanges (store it completely, it is long). "" after a failure.</summary>
        public string NextDeltaLink { get; private set; }

        /// <summary>True when the last FetchChanges failed because the delta link expired: restart with FetchChanges("") and ReceivedAfter set.</summary>
        public bool DeltaExpired { get; private set; }

        /// <summary>Number of messages loaded by the last Fetch/FetchChanges.</summary>
        public int Count
        {
            get { return _messages.Count; }
        }

        /// <summary>Message by 0-based index.</summary>
        public GraphMailItem GetMessage(int index)
        {
            if (index < 0 || index >= _messages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Message index " + index + " is out of range (Count " + _messages.Count + ").");
            }

            return _messages[index];
        }

        /// <summary>FetchChanges: number of ids reported as removed (deleted or moved out of the folder).</summary>
        public int RemovedCount
        {
            get { return _removedIds.Count; }
        }

        /// <summary>Removed id by 0-based index.</summary>
        public string GetRemovedId(int index)
        {
            if (index < 0 || index >= _removedIds.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Removed index " + index + " is out of range (RemovedCount " + _removedIds.Count + ").");
            }

            return _removedIds[index];
        }

        /// <summary>Id of the message after the last successful MoveToFolder (with immutable ids normally the same id).</summary>
        public string LastMovedMessageId { get; private set; }

        /// <summary>Folder id resolved by the last successful EnsureFolder (a well-known name stays as the name, Graph accepts it as id).</summary>
        public string LastFolderId { get; private set; }

        /// <summary>Bytes written by the last successful SaveAttachmentToFile/SaveAttachmentToStream/SaveMimeToFile.</summary>
        public long LastDownloadSize { get; private set; }

        /// <summary>
        /// Object key (without the storage's KeyPrefix, i.e. what DownloadToFile/GetDownloadUrl expect) written by the last successful
        /// CopyAttachmentToStorage; "" otherwise.
        /// </summary>
        public string LastStorageKey { get; private set; } = "";

        /// <summary>Bytes stored by the last successful CopyAttachmentToStorage; 0 otherwise.</summary>
        public long LastStorageSize { get; private set; }

        /// <summary>ETag returned by the storage for the last successful CopyAttachmentToStorage ("" when the service sent none).</summary>
        public string LastStorageETag { get; private set; } = "";

        /// <summary>Graph request-id of the last failed call (for Microsoft support), "" if none.</summary>
        public string LastRequestId { get; private set; }

        /// <summary>Redacted diagnostic log of the last operation.</summary>
        public string LastDiagnostics
        {
            get { return _diag.Text; }
        }

        // ----------------------------------------------------------------------------------------------------------
        // Polling
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Loads up to MaxMessages messages of Folder, oldest first: GET .../mailFolders/{folder}/messages with
        /// $filter=receivedDateTime ge {ReceivedAfter} [and isRead eq false], $orderby=receivedDateTime asc, $top, $select,
        /// following @odata.nextLink. "" or the error text. When MaxMessages is reached while the messages still carry exactly
        /// the ReceivedAfter timestamp, Fetch keeps loading the messages with that timestamp (it may return more than
        /// MaxMessages), so the watermark can advance even when many messages share one receivedDateTime.
        /// </summary>
        public string Fetch()
        {
            ClearResults();
            return ClearOnFailure(Run("Fetch", false, async (client, ct) =>
            {
                string watermark = MailQueries.NormalizeWatermark(_receivedAfter);
                int max = MailQueries.EffectiveMaxMessages(MaxMessages);
                string folderId = await ResolveFolderAsync(client, _folder, false, ct).ConfigureAwait(false);
                string url = client.MailboxUrl + "/mailFolders/" + GraphClient.EscapePathSegment(folderId) + "/messages?"
                    + MailQueries.ListQuery(watermark, UnreadOnly, MailQueries.PageSize(max), IncludeBody);
                IList<string>? preferences = BodyAsText ? new[] { GraphReadClient.TextBodyPreference } : null;
                // Messages are sorted ascending and all are >= the watermark, so a message at exactly the watermark after the
                // cap means every collected message ties with it: without loading the rest of the tie the next Fetch would
                // return the same messages again and the watermark could never advance.
                DateTime tie = watermark.Length > 0 ? MailQueries.ParseUtc(watermark) : DateTime.MinValue;
                int page = 0;
                int beyondCap = 0;
                while (true)
                {
                    page++;
                    GraphResponse response = await client.GetJsonAsync("list messages", url, preferences, ct).ConfigureAwait(false);
                    GraphReadMessagePage parsed = ParseMessagePage(response, "list messages");
                    List<GraphReadMessage> values = parsed.Value ?? new List<GraphReadMessage>();
                    for (int i = 0; i < values.Count; i++)
                    {
                        if (_messages.Count >= max)
                        {
                            if (!IsAtWatermark(values[i], tie))
                            {
                                MoreAvailable = true;
                                break;
                            }

                            beyondCap++;
                        }

                        _messages.Add(new GraphMailItem(values[i], this));
                    }

                    if (MoreAvailable)
                    {
                        break;
                    }

                    if (string.IsNullOrEmpty(parsed.NextLink))
                    {
                        break;
                    }

                    if (_messages.Count >= max && !IsAtWatermark(_messages[_messages.Count - 1].ReceivedText, tie))
                    {
                        MoreAvailable = true;
                        break;
                    }

                    url = client.EnsureGraphUrl(parsed.NextLink!, "@odata.nextLink");
                }

                if (beyondCap > 0)
                {
                    _diag.Log("Fetch: " + beyondCap + " message(s) beyond MaxMessages loaded because they share the watermark timestamp " + watermark);
                }

                _diag.Log("Fetch: " + _messages.Count + " message(s) in " + page + " page(s)" + (MoreAvailable ? ", more available" : ""));
            }));
        }

        private static bool IsAtWatermark(GraphReadMessage message, DateTime watermark)
        {
            return IsAtWatermark(message.ReceivedDateTime ?? "", watermark);
        }

        private static bool IsAtWatermark(string receivedText, DateTime watermark)
        {
            return watermark != DateTime.MinValue && MailQueries.ParseUtc(receivedText) == watermark;
        }

        /// <summary>
        /// Delta query on Folder (changeType=created). <paramref name="deltaLink"/> "" starts a new sync (bounded by
        /// ReceivedAfter when set); otherwise pass the stored NextDeltaLink. Follows @odata.nextLink until a deltaLink arrives
        /// or MaxMessages is reached (then NextDeltaLink is the page link to continue with and MoreAvailable is true).
        /// Removed entries go to RemovedCount/GetRemovedId. An expired link returns an error text starting with "DeltaExpired:"
        /// and sets DeltaExpired: HTTP 410, a sync-state error code, or (for a stored link, whose expiry status/code Microsoft
        /// does not tabulate) any other 4xx except 401, 403, 408 and 429.
        /// </summary>
        public string FetchChanges(string deltaLink)
        {
            ClearResults();
            string link = (deltaLink ?? "").Trim();
            return ClearOnFailure(Run("FetchChanges", false, async (client, ct) =>
            {
                int max = MailQueries.EffectiveMaxMessages(MaxMessages);
                string url;
                if (link.Length == 0)
                {
                    string watermark = MailQueries.NormalizeWatermark(_receivedAfter);
                    string folderId = await ResolveFolderAsync(client, _folder, false, ct).ConfigureAwait(false);
                    url = client.MailboxUrl + "/mailFolders/" + GraphClient.EscapePathSegment(folderId) + "/messages/delta?" + MailQueries.DeltaQuery(watermark, IncludeBody);
                    _diag.Log("FetchChanges: initial sync of folder " + _folder + (watermark.Length > 0 ? " from " + watermark : ""));
                }
                else
                {
                    url = client.EnsureGraphUrl(link, "delta link");
                    _diag.Log("FetchChanges: continuing with the stored link");
                }

                var preferences = new List<string> { "odata.maxpagesize=" + MailQueries.PageSize(max).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (BodyAsText)
                {
                    preferences.Add(GraphReadClient.TextBodyPreference);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var removedSeen = new HashSet<string>(StringComparer.Ordinal);
                int skipped = 0;
                while (true)
                {
                    GraphResponse response;
                    try
                    {
                        response = await client.GetJsonAsync("delta", url, preferences, ct).ConfigureAwait(false);
                    }
                    catch (GraphMailException ex) when (ex.StatusCode == 410 || ErrorHints.IsDeltaExpiredCode(ex.ErrorCode)
                        || (link.Length > 0 && IsStoredLinkRejection(ex.StatusCode)))
                    {
                        DeltaExpired = true;
                        throw new GraphMailException("DeltaExpired: " + ex.Message, ex.ErrorCode, ex.StatusCode, ex.RequestId, false, ex);
                    }

                    GraphReadMessagePage parsed = ParseMessagePage(response, "delta");
                    foreach (GraphReadMessage entry in parsed.Value ?? new List<GraphReadMessage>())
                    {
                        string id = entry.Id ?? "";
                        if (entry.Removed != null)
                        {
                            if (id.Length > 0 && removedSeen.Add(id))
                            {
                                _removedIds.Add(id);
                            }

                            continue;
                        }

                        if (id.Length == 0 || string.IsNullOrEmpty(entry.ReceivedDateTime) || !seen.Add(id))
                        {
                            skipped++;
                            continue;
                        }

                        _messages.Add(new GraphMailItem(entry, this));
                    }

                    if (!string.IsNullOrEmpty(parsed.DeltaLink))
                    {
                        NextDeltaLink = parsed.DeltaLink!;
                        break;
                    }

                    if (string.IsNullOrEmpty(parsed.NextLink))
                    {
                        throw new GraphMailException(
                            "Microsoft Graph delta failed (HTTP " + response.StatusCode + "): the response contained neither @odata.nextLink nor @odata.deltaLink. [mailbox " + _mailbox + "]",
                            "NoDeltaLink", response.StatusCode, response.RequestId, false);
                    }

                    string next = client.EnsureGraphUrl(parsed.NextLink!, "@odata.nextLink");
                    if (_messages.Count >= max)
                    {
                        NextDeltaLink = next;
                        MoreAvailable = true;
                        break;
                    }

                    url = next;
                }

                _diag.Log("FetchChanges: " + _messages.Count + " message(s), " + _removedIds.Count + " removed, " + skipped + " skipped (duplicates/updates)"
                    + (MoreAvailable ? ", more available" : ""));
            }));
        }

        // ----------------------------------------------------------------------------------------------------------
        // Actions (Mail.ReadWrite)
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>PATCH {"isRead":true}. "" or the error text.</summary>
        public string MarkAsRead(string messageId)
        {
            return SetIsRead("MarkAsRead", messageId, true);
        }

        /// <summary>PATCH {"isRead":false}. "" or the error text.</summary>
        public string MarkAsUnread(string messageId)
        {
            return SetIsRead("MarkAsUnread", messageId, false);
        }

        /// <summary>
        /// Moves the message to <paramref name="folder"/> (well-known name, folder id or path; missing path segments are
        /// created). Sets LastMovedMessageId. "" or the error text.
        /// </summary>
        public string MoveToFolder(string messageId, string folder)
        {
            LastMovedMessageId = "";
            string id = (messageId ?? "").Trim();
            string target = (folder ?? "").Trim();
            return Run("MoveToFolder", true, async (client, ct) =>
            {
                RequireId(id, "message id");
                if (target.Length == 0)
                {
                    throw GraphMailException.Validation("Validation failed: the target folder is required.");
                }

                bool fromCache = IsCached(target);
                string destination = await ResolveFolderAsync(client, target, true, ct).ConfigureAwait(false);
                GraphResponse response;
                try
                {
                    response = await PostMoveAsync(client, id, destination, ct).ConfigureAwait(false);
                }
                catch (GraphMailException ex) when (ex.StatusCode == 404 && fromCache)
                {
                    // A cached folder id may be stale (folder deleted/recreated): resolve again once.
                    _diag.Log("move failed with HTTP 404 using a cached folder id: resolving the folder again");
                    _folderCache.Clear();
                    destination = await ResolveFolderAsync(client, target, true, ct).ConfigureAwait(false);
                    response = await PostMoveAsync(client, id, destination, ct).ConfigureAwait(false);
                }

                GraphReadMessage? moved = GraphJson.Deserialize<GraphReadMessage>(response.Body);
                LastMovedMessageId = moved != null && !string.IsNullOrEmpty(moved.Id) ? moved.Id! : id;
                _diag.Log("moved to " + target + ": " + LastMovedMessageId);
            });
        }

        /// <summary>DELETE the message (Graph soft delete). "" or the error text.</summary>
        public string DeleteMessage(string messageId)
        {
            string id = (messageId ?? "").Trim();
            return Run("DeleteMessage", true, async (client, ct) =>
            {
                RequireId(id, "message id");
                await client.SendJsonAsync("delete message", HttpMethod.Delete, client.MessageUrl(id), null, ct).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Resolves the folder path (creating missing segments) and sets LastFolderId. First segment: well-known name or a
        /// display name below the mailbox root; further segments: child folders by display name. "" or the error text.
        /// </summary>
        public string EnsureFolder(string folderPath)
        {
            LastFolderId = "";
            string path = (folderPath ?? "").Trim();
            return Run("EnsureFolder", true, async (client, ct) =>
            {
                if (path.Length == 0)
                {
                    throw GraphMailException.Validation("Validation failed: the folder path is required.");
                }

                LastFolderId = await ResolveFolderAsync(client, path, true, ct).ConfigureAwait(false);
                _diag.Log("folder " + path + " = " + LastFolderId);
            });
        }

        // ----------------------------------------------------------------------------------------------------------
        // Content (streamed, any size)
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>Saves the full MIME content (.eml) of the message: GET .../messages/{id}/$value streamed to the file.</summary>
        public string SaveMimeToFile(string messageId, string filePath)
        {
            LastDownloadSize = 0;
            string id = (messageId ?? "").Trim();
            return Run("SaveMimeToFile", false, async (client, ct) =>
            {
                RequireId(id, "message id");
                LastDownloadSize = await DownloadToFileAsync(client, "download message MIME", client.MessageUrl(id) + "/$value", filePath, "the message", ct).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Saves an attachment to a file: GET .../attachments/{id}/$value streamed (file attachments: raw bytes; item attachments:
        /// MIME). The file is written as "{filePath}.nmgpart" and renamed on success (an existing file is replaced).
        /// </summary>
        public string SaveAttachmentToFile(string messageId, string attachmentId, string filePath)
        {
            LastDownloadSize = 0;
            string id = (messageId ?? "").Trim();
            string aid = (attachmentId ?? "").Trim();
            return Run("SaveAttachmentToFile", false, async (client, ct) =>
            {
                RequireId(id, "message id");
                RequireId(aid, "attachment id");
                LastDownloadSize = await DownloadAttachmentAsync(client, id, aid,
                    (url, c) => DownloadToFileAsync(client, "download attachment", url, filePath, "attachment " + aid, c), ct).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Writes an attachment's content into <paramref name="target"/> (e.g. a NAV OutStream): sequential Write calls only,
        /// never Seek/Length/Position; the stream is flushed, not closed. On failure the target may hold partial content.
        /// </summary>
        public string SaveAttachmentToStream(string messageId, string attachmentId, Stream target)
        {
            LastDownloadSize = 0;
            string id = (messageId ?? "").Trim();
            string aid = (attachmentId ?? "").Trim();
            return Run("SaveAttachmentToStream", false, async (client, ct) =>
            {
                RequireId(id, "message id");
                RequireId(aid, "attachment id");
                if (target == null)
                {
                    throw GraphMailException.Validation("Validation failed: the target stream is required.");
                }

                if (!target.CanWrite)
                {
                    throw GraphMailException.Validation("Validation failed: the target stream is not writable.");
                }

                string what = "attachment " + aid;
                LastDownloadSize = await DownloadAttachmentAsync(client, id, aid,
                    (url, c) => client.DownloadAsync("download attachment", url,
                        async (source, response, reset, token) =>
                        {
                            long written = await GraphReadClient.CopyAsync(source, target, reset, what, token).ConfigureAwait(false);
                            CheckLength(response, written, what);
                            return written;
                        },
                        c),
                    ct).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Copies an attachment straight from Graph into object storage: GET .../attachments/{id}/$value is streamed into the
        /// storage upload (single PUT when it fits into one part, otherwise Put Block/List or S3 multipart with one reused part
        /// buffer) - no temp file, never the whole attachment in memory. <paramref name="objectKey"/> is relative to the storage's
        /// KeyPrefix. The content type is taken from the Graph response (else from the key's extension). On success
        /// LastStorageKey/LastStorageSize/LastStorageETag are set; on failure they are cleared and the error text names the side
        /// (Graph or object storage) that failed. The storage's LastError/LastDiagnostics describe the storage part.
        /// Throws <see cref="ArgumentNullException"/> only for a null <paramref name="storage"/>.
        /// </summary>
        public string CopyAttachmentToStorage(string messageId, string attachmentId, ObjectStorageClient storage, string objectKey)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            LastDownloadSize = 0;
            LastStorageKey = "";
            LastStorageSize = 0;
            LastStorageETag = "";
            string id = (messageId ?? "").Trim();
            string aid = (attachmentId ?? "").Trim();
            string key = objectKey ?? "";
            Storage.StorageUploadResult? stored = null;
            storage.BeginExternalOperation("CopyAttachmentToStorage");
            Exception? storageFailure = null;
            string error = Run("CopyAttachmentToStorage", false, async (client, ct) =>
            {
                RequireId(id, "message id");
                RequireId(aid, "attachment id");
                if (key.Trim().Length == 0)
                {
                    throw GraphMailException.Validation("Validation failed: the object key is required.");
                }

                // Fail fast on configuration errors (before any Graph traffic).
                try
                {
                    storage.ValidateForUpload(key);
                }
                catch (Exception ex)
                {
                    storageFailure = ex;
                    throw ObjectStorageClient.WrapForCaller(ex, "Copy to object storage failed");
                }

                TimeSpan keepAlive = TimeSpan.FromSeconds(Math.Max(1, _connection.Settings.EffectiveTimeoutSeconds / 4));
                string what = "attachment " + aid;
                await StreamAttachmentAsync(client, id, aid, async (source, response, reset, token) =>
                {
                    long? expected = response.Content == null ? null : response.Content.Headers.ContentLength;
                    string contentType = ContentTypeOf(response);
                    _diag.Log("piping " + what + " (" + (expected.HasValue ? expected.Value + " bytes" : "unknown length") + (contentType.Length > 0 ? ", " + contentType : "")
                        + ") into object storage key " + key);
                    using (var pipe = new IdleTimeoutReadStream(source, reset, keepAlive, what))
                    {
                        try
                        {
                            stored = await storage.UploadFromStreamAsync(pipe, expected.HasValue ? expected.Value : -1, key, contentType, token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (pipe.ReadFailure != null)
                            {
                                // Graph side: the upload was given up because the attachment could not be read.
                                storageFailure = pipe.ReadFailure;
                                throw pipe.ReadFailure;
                            }

                            if (token.IsCancellationRequested && ex is OperationCanceledException)
                            {
                                throw;
                            }

                            storageFailure = ex;
                            throw ObjectStorageClient.WrapForCaller(ex, "Copy of " + what + " to object storage failed after " + pipe.BytesRead + " bytes read from Graph");
                        }

                        return stored.Size;
                    }
                }, ct).ConfigureAwait(false);
            });

            if (error.Length == 0 && stored != null)
            {
                LastStorageKey = storage.StripKeyPrefix(stored.Key);
                LastStorageSize = stored.Size;
                LastStorageETag = stored.ETag ?? "";
                LastDownloadSize = stored.Size;
                _diag.Log("stored " + stored.Size + " bytes as " + stored.Key + (stored.Parts > 0 ? " (" + stored.Parts + " parts)" : " (single PUT)"));
                storage.EndExternalOperation(null);
            }
            else if (storageFailure != null)
            {
                storage.EndExternalOperation(storageFailure);
            }

            return error;
        }

        // ----------------------------------------------------------------------------------------------------------
        // Internals
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>Content type of a $value response ("" when absent or generic application/octet-stream).</summary>
        private static string ContentTypeOf(HttpResponseMessage response)
        {
            if (response.Content == null || response.Content.Headers.ContentType == null)
            {
                return "";
            }

            string value = response.Content.Headers.ContentType.ToString();
            return string.Equals(response.Content.Headers.ContentType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        /// <summary>
        /// Streams an attachment's $value to <paramref name="consumer"/> (the hook used by storage copies): the consumer gets
        /// the response stream, the response (Content-Length, Content-Type) and the idle-timeout reset action. A reference
        /// attachment (HTTP 405) becomes a clear error. Runs inside an operation started with <see cref="Run"/>.
        /// </summary>
        internal Task<long> StreamAttachmentAsync(GraphReadClient client, string messageId, string attachmentId,
            Func<Stream, HttpResponseMessage, Action, CancellationToken, Task<long>> consumer, CancellationToken cancellationToken)
        {
            return DownloadAttachmentAsync(client, messageId, attachmentId, (url, c) => client.DownloadAsync("download attachment", url, consumer, c), cancellationToken);
        }

        /// <summary>Loads the attachment metadata of one message (used lazily by <see cref="GraphMailItem"/>). "" or the error text.</summary>
        internal string LoadAttachmentMetadata(string messageId, out List<GraphMailAttachmentInfo>? attachments)
        {
            var result = new List<GraphMailAttachmentInfo>();
            attachments = null;
            string error = Run("LoadAttachments", false, async (client, ct) =>
            {
                RequireId(messageId, "message id");
                string url = client.MessageUrl(messageId) + "/attachments?$select=" + MailQueries.Escape(MailQueries.AttachmentSelect);
                while (true)
                {
                    GraphResponse response = await client.GetJsonAsync("list attachments", url, null, ct).ConfigureAwait(false);
                    if (MailQueries.ContainsContentBytes(response.Body))
                    {
                        throw new GraphMailException(
                            "Microsoft Graph list attachments returned attachment content (contentBytes) although only metadata was selected ($select was ignored); "
                            + "the response (" + response.Body.Length + " chars) was discarded. [" + (response.RequestId.Length > 0 ? "request-id " + response.RequestId + ", " : "") + "mailbox " + _mailbox + "]",
                            "UnexpectedContentBytes", response.StatusCode, response.RequestId, false);
                    }

                    GraphAttachmentPage? page = GraphJson.Deserialize<GraphAttachmentPage>(response.Body);
                    if (page == null)
                    {
                        throw UnexpectedResponse("list attachments", response);
                    }

                    foreach (GraphAttachmentMetadata metadata in page.Value ?? new List<GraphAttachmentMetadata>())
                    {
                        result.Add(new GraphMailAttachmentInfo(metadata));
                    }

                    if (string.IsNullOrEmpty(page.NextLink))
                    {
                        break;
                    }

                    url = client.EnsureGraphUrl(page.NextLink!, "@odata.nextLink");
                }

                _diag.Log("attachments of " + messageId + ": " + result.Count);
            });

            if (error.Length == 0)
            {
                attachments = result;
            }

            return error;
        }

        /// <summary>Test hook: number of cached folder paths.</summary>
        internal int CachedFolderCount
        {
            get { return _folderCache.Count; }
        }

        private string SetIsRead(string operation, string messageId, bool isRead)
        {
            string id = (messageId ?? "").Trim();
            return Run(operation, true, async (client, ct) =>
            {
                RequireId(id, "message id");
                byte[] payload = GraphJson.SerializeToUtf8Bytes(new MarkReadRequest { IsRead = isRead });
                await client.SendJsonAsync(isRead ? "mark as read" : "mark as unread", new HttpMethod("PATCH"), client.MessageUrl(id), payload, ct).ConfigureAwait(false);
            });
        }

        private static Task<GraphResponse> PostMoveAsync(GraphReadClient client, string messageId, string destinationId, CancellationToken ct)
        {
            byte[] payload = GraphJson.SerializeToUtf8Bytes(new MoveMessageRequest { DestinationId = destinationId });
            return client.SendJsonAsync("move message", HttpMethod.Post, client.MessageUrl(messageId) + "/move", payload, ct);
        }

        private async Task<long> DownloadAttachmentAsync(GraphReadClient client, string messageId, string attachmentId,
            Func<string, CancellationToken, Task<long>> download, CancellationToken ct)
        {
            string url = client.MessageUrl(messageId) + "/attachments/" + GraphClient.EscapePathSegment(attachmentId) + "/$value";
            try
            {
                return await download(url, ct).ConfigureAwait(false);
            }
            catch (GraphMailException ex) when (ex.StatusCode == 405)
            {
                throw new GraphMailException(
                    "Attachment " + attachmentId + " cannot be downloaded: reference attachments (links to cloud files) have no content in Microsoft Graph (HTTP 405); "
                    + "open the message in Outlook to follow the link. [" + (ex.RequestId.Length > 0 ? "request-id " + ex.RequestId + ", " : "") + "mailbox " + _mailbox + "]",
                    "ReferenceAttachment", 405, ex.RequestId, false, ex);
            }
        }

        private async Task<long> DownloadToFileAsync(GraphReadClient client, string operation, string url, string filePath, string what, CancellationToken ct)
        {
            string path = ValidateFilePath(filePath);
            string partial = path + ".nmgpart";
            try
            {
                return await client.DownloadAsync(operation, url, async (source, response, reset, token) =>
                {
                    long written;
                    using (FileStream file = OpenForWrite(partial))
                    {
                        written = await GraphReadClient.CopyAsync(source, file, reset, what, token).ConfigureAwait(false);
                    }

                    CheckLength(response, written, what);
                    try
                    {
                        if (File.Exists(path))
                        {
                            // Replace in one step: a failure leaves the existing file untouched.
                            File.Replace(partial, path, null);
                        }
                        else
                        {
                            File.Move(partial, path);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new GraphMailException("Saving " + what + " to " + path + " failed: " + GraphClient.FirstLine(ex.Message), GraphReadClient.IoErrorCode, 0, "", false, ex);
                    }

                    return written;
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }

        private static void CheckLength(HttpResponseMessage response, long written, string what)
        {
            long? expected = response.Content == null ? null : response.Content.Headers.ContentLength;
            if (expected.HasValue && expected.Value != written)
            {
                throw new GraphMailException(
                    "Download of " + what + " is incomplete: received " + written + " of " + expected.Value + " bytes.",
                    GraphMailException.TransportCode, 0, "", true);
            }
        }

        private static string ValidateFilePath(string filePath)
        {
            string value = (filePath ?? "").Trim();
            if (value.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: the file path is required.");
            }

            try
            {
                string full = Path.GetFullPath(value);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                throw new GraphMailException("Invalid file path '" + value + "': " + GraphClient.FirstLine(ex.Message), GraphMailException.ValidationCode, 0, "", false, ex);
            }
        }

        private static FileStream OpenForWrite(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, GraphReadClient.CopyBufferSize);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                throw new GraphMailException("Cannot create " + path + ": " + GraphClient.FirstLine(ex.Message), GraphReadClient.IoErrorCode, 0, "", false, ex);
            }
        }

        private static void TryDelete(string path)
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
                // best effort
            }
        }

        private bool IsCached(string folder)
        {
            try
            {
                List<string> segments = MailQueries.SplitFolderPath(folder);
                return segments.Count > 0 && _folderCache.ContainsKey(MailQueries.FolderCacheKey(segments));
            }
            catch (GraphMailException)
            {
                return false;
            }
        }

        /// <summary>
        /// Folder value → id usable in URLs and as destinationId. A single well-known name is used directly; otherwise the
        /// value is resolved segment by segment, creating missing segments when <paramref name="create"/> is true. First
        /// segment: well-known name; else, when it looks like a folder id, GET mailFolders/{id} (so "&lt;id&gt;/Processed"
        /// works), falling back to a display-name lookup below the root when Graph rejects it (400/404); else a display name
        /// below the root. A first segment that looks like an id is never created as a root folder. Results are cached per
        /// path in this instance.
        /// </summary>
        internal async Task<string> ResolveFolderAsync(GraphReadClient client, string folder, bool create, CancellationToken ct)
        {
            string value = (folder ?? "").Trim();
            if (value.Length == 0)
            {
                value = "inbox";
            }

            if (value.IndexOf('/') < 0)
            {
                string? wellKnown = MailQueries.WellKnownName(value);
                if (wellKnown != null)
                {
                    return wellKnown;
                }
            }

            List<string> segments = MailQueries.SplitFolderPath(value);
            string key = MailQueries.FolderCacheKey(segments);
            string? cached;
            if (_folderCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            string? parentId = null;
            for (int i = 0; i < segments.Count; i++)
            {
                string segment = segments[i];
                string partialKey = MailQueries.FolderCacheKey(segments.GetRange(0, i + 1));
                string? id;
                if (_folderCache.TryGetValue(partialKey, out id))
                {
                    parentId = id;
                    continue;
                }

                bool idLike = false;
                if (i == 0)
                {
                    id = MailQueries.WellKnownName(segment);
                    if (id == null && MailQueries.LooksLikeFolderId(segment))
                    {
                        idLike = true;
                        id = await GetFolderByIdAsync(client, segment, ct).ConfigureAwait(false);
                    }
                }

                if (id == null)
                {
                    id = await FindChildFolderAsync(client, parentId, segment, ct).ConfigureAwait(false);
                }

                if (id == null)
                {
                    if (!create || idLike)
                    {
                        throw new GraphMailException(
                            "Mail folder '" + segment + "' of path '" + value + "' was not found in mailbox " + _mailbox + "."
                            + (idLike ? " It looks like a folder id, but Graph does not know it and no folder below the mailbox root has this name; it is not created." : ""),
                            "FolderNotFound", 404, "", false);
                    }

                    id = await CreateChildFolderAsync(client, parentId, segment, ct).ConfigureAwait(false);
                }

                _folderCache[partialKey] = id;
                parentId = id;
            }

            return parentId!;
        }

        private static string ChildFoldersUrl(GraphReadClient client, string? parentId)
        {
            return parentId == null
                ? client.MailboxUrl + "/mailFolders"
                : client.MailboxUrl + "/mailFolders/" + GraphClient.EscapePathSegment(parentId) + "/childFolders";
        }

        /// <summary>GET mailFolders/{id}: the folder id, or null when Graph rejects the value as id (HTTP 400 or 404).</summary>
        private async Task<string?> GetFolderByIdAsync(GraphReadClient client, string folderId, CancellationToken ct)
        {
            string url = client.MailboxUrl + "/mailFolders/" + GraphClient.EscapePathSegment(folderId) + "?$select=" + MailQueries.Escape(MailQueries.FolderSelect);
            GraphResponse response;
            try
            {
                response = await client.GetJsonAsync("get folder", url, null, ct).ConfigureAwait(false);
            }
            catch (GraphMailException ex) when (ex.StatusCode == 400 || ex.StatusCode == 404)
            {
                _diag.Log("folder value looks like an id but Graph rejected it (HTTP " + ex.StatusCode + "): looking it up as a display name");
                return null;
            }

            GraphMailFolder? folder = GraphJson.Deserialize<GraphMailFolder>(response.Body);
            if (folder == null || string.IsNullOrEmpty(folder.Id))
            {
                throw UnexpectedResponse("get folder", response);
            }

            return folder.Id!;
        }

        private async Task<string?> FindChildFolderAsync(GraphReadClient client, string? parentId, string displayName, CancellationToken ct)
        {
            string baseUrl = ChildFoldersUrl(client, parentId);
            string url = baseUrl + "?" + MailQueries.FolderLookupQuery(displayName);
            GraphResponse response;
            try
            {
                response = await client.GetJsonAsync("find folder '" + displayName + "'", url, null, ct).ConfigureAwait(false);
            }
            catch (GraphMailException ex) when (ex.StatusCode == 400)
            {
                // $filter on displayName is not confirmed by a Graph doc example (research-notes-v2): fall back to listing.
                _diag.Log("folder $filter rejected (HTTP 400): listing the folders instead");
                url = baseUrl + "?$select=" + MailQueries.Escape(MailQueries.FolderSelect) + "&$top=100";
                while (true)
                {
                    GraphResponse listed = await client.GetJsonAsync("list folders", url, null, ct).ConfigureAwait(false);
                    GraphMailFolderPage? listPage = GraphJson.Deserialize<GraphMailFolderPage>(listed.Body);
                    if (listPage == null)
                    {
                        throw UnexpectedResponse("list folders", listed);
                    }

                    string? match = MatchFolder(listPage, displayName, false);
                    if (match != null || string.IsNullOrEmpty(listPage.NextLink))
                    {
                        return match;
                    }

                    url = client.EnsureGraphUrl(listPage.NextLink!, "@odata.nextLink");
                }
            }

            GraphMailFolderPage? page = GraphJson.Deserialize<GraphMailFolderPage>(response.Body);
            if (page == null)
            {
                throw UnexpectedResponse("find folder", response);
            }

            return MatchFolder(page, displayName, true);
        }

        private static string? MatchFolder(GraphMailFolderPage page, string displayName, bool acceptWithoutName)
        {
            foreach (GraphMailFolder folder in page.Value ?? new List<GraphMailFolder>())
            {
                if (string.IsNullOrEmpty(folder.Id))
                {
                    continue;
                }

                if (string.Equals(folder.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)
                    || (acceptWithoutName && string.IsNullOrEmpty(folder.DisplayName)))
                {
                    return folder.Id;
                }
            }

            return null;
        }

        private async Task<string> CreateChildFolderAsync(GraphReadClient client, string? parentId, string displayName, CancellationToken ct)
        {
            byte[] payload = GraphJson.SerializeToUtf8Bytes(new CreateMailFolderRequest { DisplayName = displayName, IsHidden = false });
            GraphResponse response;
            try
            {
                response = await client.SendJsonAsync("create folder '" + displayName + "'", HttpMethod.Post, ChildFoldersUrl(client, parentId), payload, ct).ConfigureAwait(false);
            }
            catch (GraphMailException ex) when (ex.StatusCode == 409)
            {
                // Created concurrently (another NAV session): look it up again.
                string? existing = await FindChildFolderAsync(client, parentId, displayName, ct).ConfigureAwait(false);
                if (existing != null)
                {
                    return existing;
                }

                throw;
            }

            GraphMailFolder? created = GraphJson.Deserialize<GraphMailFolder>(response.Body);
            if (created == null || string.IsNullOrEmpty(created.Id))
            {
                throw UnexpectedResponse("create folder", response);
            }

            _diag.Log("folder '" + displayName + "' created");
            return created.Id!;
        }

        private GraphReadMessagePage ParseMessagePage(GraphResponse response, string operation)
        {
            GraphReadMessagePage? page = GraphJson.Deserialize<GraphReadMessagePage>(response.Body);
            if (page == null)
            {
                throw UnexpectedResponse(operation, response);
            }

            return page;
        }

        private GraphMailException UnexpectedResponse(string operation, GraphResponse response)
        {
            return new GraphMailException(
                "Microsoft Graph " + operation + " failed (HTTP " + response.StatusCode + "): the response could not be read ("
                + GraphClient.Truncate(GraphClient.FirstLine(response.Body), 120) + "). [" + (response.RequestId.Length > 0 ? "request-id " + response.RequestId + ", " : "")
                + "mailbox " + _mailbox + "]",
                "UnexpectedResponse", response.StatusCode, response.RequestId, false);
        }

        private static void RequireId(string value, string what)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw GraphMailException.Validation("Validation failed: the " + what + " is required.");
            }
        }

        /// <summary>
        /// research-notes-v2: the status and code of an expired message delta token are not tabulated ("40X ... such as
        /// syncStateNotFound"), so any 4xx on a link from a previous run means a full resync - except authentication,
        /// permission, timeout and throttling responses, which a resync would not fix.
        /// </summary>
        private static bool IsStoredLinkRejection(int statusCode)
        {
            return statusCode >= 400 && statusCode < 500 && statusCode != 401 && statusCode != 403 && statusCode != 408 && statusCode != 429;
        }

        /// <summary>A failed Fetch/FetchChanges exposes no partial results (DeltaExpired stays as set).</summary>
        private string ClearOnFailure(string error)
        {
            if (error.Length > 0)
            {
                bool expired = DeltaExpired;
                ClearResults();
                DeltaExpired = expired;
            }

            return error;
        }

        private void ClearResults()
        {
            _messages.Clear();
            _removedIds.Clear();
            MoreAvailable = false;
            DeltaExpired = false;
            NextDeltaLink = "";
        }

        private GraphReadClient CreateClient()
        {
            if (_mailbox.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: the mailbox is required.");
            }

            GraphMailSender settings = _connection.Settings;
            CloudEndpoints endpoints = settings.ResolveEndpoints();
            string graphBaseUrl = settings.EffectiveGraphBaseUrl(endpoints);
            ITokenProvider provider = settings.ResolveTokenProvider(endpoints);
            _diag.Log("settings: cloud=" + endpoints.Name + " graph=" + graphBaseUrl + " mailbox=" + _mailbox + " folder=" + _folder
                + " receivedAfter=" + (_receivedAfter.Length > 0 ? _receivedAfter : "-") + " unreadOnly=" + UnreadOnly + " maxMessages=" + MailQueries.EffectiveMaxMessages(MaxMessages)
                + " includeBody=" + IncludeBody + " bodyAsText=" + BodyAsText + " timeout=" + settings.EffectiveTimeoutSeconds + "s retries=" + settings.EffectiveMaxRetries
                + " token provider: " + provider.Describe());
            return new GraphReadClient(settings.HttpClient, provider, _diag, graphBaseUrl, _mailbox, settings.EffectiveTimeoutSeconds, settings.EffectiveMaxRetries, settings.RetryDelayOverride);
        }

        /// <summary>Runs one operation synchronously; converts every exception into the returned error text.</summary>
        internal string Run(string name, bool requiresWrite, Func<GraphReadClient, CancellationToken, Task> work)
        {
            _diag.LogFilePath = _connection.LogFilePath;
            _diag.BeginOperation(name);
            LastRequestId = "";
            try
            {
                AsyncHelper.RunSync(() => work(CreateClient(), CancellationToken.None));
                return "";
            }
            catch (Exception ex)
            {
                return Fail(ex, requiresWrite);
            }
        }

        private string Fail(Exception exception, bool requiresWrite)
        {
            GraphMailException? known = exception as GraphMailException;
            string text;
            if (known != null)
            {
                text = ErrorHints.Append(known.Message, ErrorHints.GetReadHint(known.ErrorCode, known.StatusCode, requiresWrite, known.Message));
                if (known.RequestId.Length > 0)
                {
                    LastRequestId = known.RequestId;
                }
            }
            else if (exception is OperationCanceledException)
            {
                text = "Operation canceled: " + exception.Message;
            }
            else
            {
                text = "Unexpected error (" + exception.GetType().Name + "): " + GraphClient.FirstLine(exception.Message);
            }

            text = Diagnostics.Redact(text);
            _diag.Log("FAILED: " + text);
            if (known == null)
            {
                _diag.Log(exception.ToString());
            }

            return text;
        }
    }
}
