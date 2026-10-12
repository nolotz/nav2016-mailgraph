using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NavMailGraph.Reading
{
    /// <summary>
    /// Pure helpers of <see cref="GraphMailbox"/>: query strings, folder paths, watermark validation, attachment kinds and
    /// file names. All Graph rules come from docs/research-notes-v2.md; no HTTP here so everything is unit-testable.
    /// </summary>
    internal static class MailQueries
    {
        /// <summary>Default and maximum number of messages per Fetch ($top accepts 1..1000).</summary>
        internal const int DefaultMaxMessages = 50;

        internal const int MaxMaxMessages = 1000;

        /// <summary>Page size for list and delta calls: moderate pages avoid HTTP 504 with full payloads.</summary>
        internal const int MaxPageSize = 50;

        /// <summary>Lower bound used when UnreadOnly is set without a watermark (receivedDateTime must lead the $filter).</summary>
        internal const string MinimumReceivedDateTime = "1900-01-01T00:00:00Z";

        /// <summary>Message properties selected by Fetch / FetchChanges (body only with IncludeBody).</summary>
        internal const string MessageSelect =
            "id,internetMessageId,conversationId,subject,from,sender,toRecipients,ccRecipients,replyTo,receivedDateTime,sentDateTime,isRead,importance,hasAttachments,bodyPreview,webLink";

        /// <summary>Base-type attachment properties only: never contentBytes (docs/research-notes-v2.md follow-up graph-read).</summary>
        internal const string AttachmentSelect = "id,name,contentType,size,isInline,lastModifiedDateTime";

        internal const string FolderSelect = "id,displayName";

        /// <summary>Well-known folder names usable in place of ids, in any mailbox locale.</summary>
        internal static readonly string[] WellKnownFolderNames =
        {
            "archive", "clutter", "conflicts", "conversationhistory", "deleteditems", "drafts", "inbox", "junkemail", "localfailures",
            "msgfolderroot", "outbox", "recoverableitemsdeletions", "scheduled", "searchfolders", "sentitems", "serverfailures", "syncissues",
        };

        private static readonly Regex WatermarkPattern = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

        private static readonly Regex FolderIdPattern = new Regex(@"^[A-Za-z0-9+=_\-]{40,}$", RegexOptions.CultureInvariant);

        private static readonly Regex ContentBytesPattern = new Regex(@"""contentBytes""\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Values below 1 give the default (50), values above 1000 are capped.</summary>
        internal static int EffectiveMaxMessages(int maxMessages)
        {
            if (maxMessages < 1)
            {
                return DefaultMaxMessages;
            }

            return maxMessages > MaxMaxMessages ? MaxMaxMessages : maxMessages;
        }

        internal static int PageSize(int maxMessages)
        {
            int max = EffectiveMaxMessages(maxMessages);
            return max < MaxPageSize ? max : MaxPageSize;
        }

        internal static string Select(bool includeBody)
        {
            return includeBody ? MessageSelect + ",body" : MessageSelect;
        }

        /// <summary>
        /// Validates the text watermark (ISO 8601 with Z or offset; "" = none). It goes verbatim into the OData filter, so
        /// anything else is rejected.
        /// </summary>
        internal static string NormalizeWatermark(string? receivedAfter)
        {
            string value = (receivedAfter ?? "").Trim();
            if (value.Length > 0 && !WatermarkPattern.IsMatch(value))
            {
                throw GraphMailException.Validation(
                    "Validation failed: ReceivedAfter '" + value + "' is not an ISO 8601 UTC date-time such as 2026-10-12T08:00:00Z (use ReceivedText of the newest imported mail).");
            }

            return value;
        }

        /// <summary>
        /// "$filter=..." text for list polling: receivedDateTime first (required with $orderby=receivedDateTime, else
        /// InefficientFilter), then "isRead eq false" when <paramref name="unreadOnly"/>. "" when there is no condition.
        /// </summary>
        internal static string ListFilter(string watermark, bool unreadOnly)
        {
            if (watermark.Length == 0 && !unreadOnly)
            {
                return "";
            }

            string filter = "receivedDateTime ge " + (watermark.Length > 0 ? watermark : MinimumReceivedDateTime);
            if (unreadOnly)
            {
                filter += " and isRead eq false";
            }

            return filter;
        }

        /// <summary>Query of the list call: $filter (optional), $orderby=receivedDateTime asc, $top, $select - in this order.</summary>
        internal static string ListQuery(string watermark, bool unreadOnly, int top, bool includeBody)
        {
            var sb = new StringBuilder();
            string filter = ListFilter(watermark, unreadOnly);
            if (filter.Length > 0)
            {
                sb.Append("$filter=").Append(Escape(filter)).Append('&');
            }

            sb.Append("$orderby=").Append(Escape("receivedDateTime asc"));
            sb.Append("&$top=").Append(top.ToString(CultureInfo.InvariantCulture));
            sb.Append("&$select=").Append(Escape(Select(includeBody)));
            return sb.ToString();
        }

        /// <summary>Query of the initial delta call: changeType=created, $select, optional $filter=receivedDateTime ge X.</summary>
        internal static string DeltaQuery(string watermark, bool includeBody)
        {
            var sb = new StringBuilder();
            sb.Append("changeType=created");
            sb.Append("&$select=").Append(Escape(Select(includeBody)));
            if (watermark.Length > 0)
            {
                sb.Append("&$filter=").Append(Escape("receivedDateTime ge " + watermark));
            }

            return sb.ToString();
        }

        /// <summary>OData string literal content: single quotes doubled.</summary>
        internal static string ODataQuote(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        /// <summary>"$filter=displayName eq '...'&amp;$select=id,displayName" for a child-folder lookup.</summary>
        internal static string FolderLookupQuery(string displayName)
        {
            return "$filter=" + Escape("displayName eq '" + ODataQuote(displayName) + "'") + "&$select=" + Escape(FolderSelect);
        }

        /// <summary>Percent-encodes a query value (RFC 3986), keeping ',' literal for readable $select lists.</summary>
        internal static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? "").Replace("%2C", ",");
        }

        /// <summary>The well-known name (lower case) when <paramref name="name"/> is one, else null.</summary>
        internal static string? WellKnownName(string name)
        {
            foreach (string wellKnown in WellKnownFolderNames)
            {
                if (string.Equals(wellKnown, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return wellKnown;
                }
            }

            return null;
        }

        /// <summary>True for values that look like a Graph folder id (long base64-like token without spaces or slashes in a path sense).</summary>
        internal static bool LooksLikeFolderId(string value)
        {
            return FolderIdPattern.IsMatch(value ?? "");
        }

        /// <summary>
        /// Splits "Inbox/NAV/Invoices" into trimmed segments. Leading/trailing '/' are ignored; empty inner segments are a
        /// validation error. Returns an empty list for "".
        /// </summary>
        internal static List<string> SplitFolderPath(string folderPath)
        {
            string value = (folderPath ?? "").Trim().Trim('/');
            var segments = new List<string>();
            if (value.Length == 0)
            {
                return segments;
            }

            foreach (string raw in value.Split('/'))
            {
                string segment = raw.Trim();
                if (segment.Length == 0)
                {
                    throw GraphMailException.Validation("Validation failed: folder path '" + folderPath + "' contains an empty segment.");
                }

                segments.Add(segment);
            }

            return segments;
        }

        /// <summary>Normalized cache key of a folder path (case-insensitive, like Exchange folder names).</summary>
        internal static string FolderCacheKey(IList<string> segments)
        {
            return string.Join("/", ToArray(segments)).ToLowerInvariant();
        }

        /// <summary>"#microsoft.graph.fileAttachment" → "file", itemAttachment → "item", referenceAttachment → "reference"; missing → "file".</summary>
        internal static string KindFromODataType(string? odataType)
        {
            string value = (odataType ?? "").Trim();
            if (value.EndsWith("itemAttachment", StringComparison.OrdinalIgnoreCase))
            {
                return "item";
            }

            if (value.EndsWith("referenceAttachment", StringComparison.OrdinalIgnoreCase))
            {
                return "reference";
            }

            return "file";
        }

        /// <summary>True when an attachment metadata response carries contentBytes (the $select was ignored).</summary>
        internal static bool ContainsContentBytes(string json)
        {
            return ContentBytesPattern.IsMatch(json ?? "");
        }

        /// <summary>
        /// A Windows-safe file name: invalid characters and control characters become '_', trailing dots/spaces are removed,
        /// reserved device names get a leading '_', the length is capped at 200 characters (extension kept), "" becomes
        /// "attachment"; item attachments get ".eml".
        /// </summary>
        internal static string SuggestFileName(string name, string kind)
        {
            var sb = new StringBuilder((name ?? "").Length);
            foreach (char c in (name ?? "").Trim())
            {
                if (c < 32 || c == 127 || "<>:\"/\\|?*".IndexOf(c) >= 0)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().TrimEnd('.', ' ').Trim();
            if (result.Length == 0 || result.Trim('_').Length == 0)
            {
                result = "attachment";
            }

            if (kind == "item" && !result.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                result += ".eml";
            }

            int dot = result.IndexOf('.');
            string stem = dot >= 0 ? result.Substring(0, dot) : result;
            foreach (string reserved in ReservedDeviceNames)
            {
                if (string.Equals(stem.Trim(), reserved, StringComparison.OrdinalIgnoreCase))
                {
                    result = "_" + result;
                    break;
                }
            }

            const int maxLength = 200;
            if (result.Length > maxLength)
            {
                int lastDot = result.LastIndexOf('.');
                string extension = lastDot > 0 && result.Length - lastDot <= 16 ? result.Substring(lastDot) : "";
                result = result.Substring(0, maxLength - extension.Length).TrimEnd('.', ' ') + extension;
            }

            return result;
        }

        /// <summary>Parses a Graph ISO 8601 UTC value to a DateTime with Kind=Utc; DateTime.MinValue when empty or invalid.</summary>
        internal static DateTime ParseUtc(string text)
        {
            DateTime value;
            if (!string.IsNullOrEmpty(text)
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }

            return DateTime.MinValue;
        }

        private static string[] ToArray(IList<string> values)
        {
            var array = new string[values.Count];
            values.CopyTo(array, 0);
            return array;
        }
    }
}
