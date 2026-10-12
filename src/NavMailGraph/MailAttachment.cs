using System.IO;

namespace NavMailGraph
{
    /// <summary>
    /// One attachment of a <see cref="GraphMailMessage"/>. The content comes either from a file path (read lazily at
    /// send time, streamed in chunks when large) or from an in-memory byte array (bytes/stream overloads).
    /// </summary>
    internal sealed class MailAttachment
    {
        internal MailAttachment(string name, string contentType, string? filePath, byte[]? bytes, bool isInline, string contentId)
        {
            Name = name;
            ContentType = contentType;
            FilePath = filePath;
            Bytes = bytes;
            IsInline = isInline;
            ContentId = contentId;
        }

        /// <summary>File name shown in the mail.</summary>
        internal string Name { get; }

        /// <summary>MIME type.</summary>
        internal string ContentType { get; }

        /// <summary>Source file path, null for in-memory content.</summary>
        internal string? FilePath { get; }

        /// <summary>In-memory content, null for file sources.</summary>
        internal byte[]? Bytes { get; }

        /// <summary>True for inline (cid:) attachments.</summary>
        internal bool IsInline { get; }

        /// <summary>Content-ID for inline attachments, "" when not set.</summary>
        internal string ContentId { get; }

        internal bool IsFile
        {
            get { return FilePath != null; }
        }

        /// <summary>True when the content is available (file exists or bytes present).</summary>
        internal bool Exists()
        {
            return FilePath == null ? Bytes != null : File.Exists(FilePath);
        }

        /// <summary>Size in bytes; throws for a missing file (validate with <see cref="Exists"/> first).</summary>
        internal long GetSize()
        {
            if (FilePath != null)
            {
                return new FileInfo(FilePath).Length;
            }

            return Bytes == null ? 0 : Bytes.LongLength;
        }

        /// <summary>Size in bytes, 0 when the file is missing or unreadable.</summary>
        internal long TryGetSize()
        {
            try
            {
                return Exists() ? GetSize() : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (System.UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>Opens the content for sequential reading (FileStream or read-only MemoryStream).</summary>
        internal Stream OpenRead()
        {
            if (FilePath != null)
            {
                return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            }

            return new MemoryStream(Bytes ?? new byte[0], false);
        }

        /// <summary>Reads the whole content into memory (used for attachments below the direct limit).</summary>
        internal byte[] ReadAllBytes()
        {
            if (FilePath != null)
            {
                return File.ReadAllBytes(FilePath);
            }

            return Bytes ?? new byte[0];
        }

        /// <summary>Short description for logs: name, type, size, inline marker. No content.</summary>
        internal string Describe()
        {
            string text = Name + " (" + ContentType + ", " + TryGetSize() + " bytes";
            if (IsInline)
            {
                text += ", inline cid=" + ContentId;
            }

            if (FilePath != null)
            {
                text += ", file";
            }

            return text + ")";
        }
    }
}
