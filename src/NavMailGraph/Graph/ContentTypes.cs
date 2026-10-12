using System;
using System.Collections.Generic;
using System.IO;

namespace NavMailGraph.Graph
{
    /// <summary>File extension to MIME type map for attachments. Unknown extensions map to application/octet-stream.</summary>
    internal static class ContentTypes
    {
        internal const string Default = "application/octet-stream";

        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".pdf", "application/pdf" },
            { ".xml", "application/xml" },
            { ".csv", "text/csv" },
            { ".txt", "text/plain" },
            { ".htm", "text/html" },
            { ".html", "text/html" },
            { ".doc", "application/msword" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".xls", "application/vnd.ms-excel" },
            { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { ".ppt", "application/vnd.ms-powerpoint" },
            { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
            { ".png", "image/png" },
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" },
            { ".zip", "application/zip" },
            { ".json", "application/json" },
            { ".rtf", "application/rtf" },
            { ".msg", "application/vnd.ms-outlook" },
            { ".eml", "message/rfc822" },
        };

        /// <summary>Returns the MIME type for a file name (by extension).</summary>
        internal static string FromFileName(string fileName)
        {
            string extension;
            try
            {
                extension = Path.GetExtension(fileName ?? "");
            }
            catch (ArgumentException)
            {
                return Default;
            }

            string? mime;
            return !string.IsNullOrEmpty(extension) && Map.TryGetValue(extension, out mime) ? mime! : Default;
        }
    }
}
