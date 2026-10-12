using NavMailGraph.Reading;

namespace NavMailGraph
{
    /// <summary>
    /// Metadata of one message attachment (no content). Content is downloaded with
    /// <see cref="GraphMailbox.SaveAttachmentToFile"/> / <see cref="GraphMailbox.SaveAttachmentToStream"/>.
    /// </summary>
    public sealed class GraphMailAttachmentInfo
    {
        internal GraphMailAttachmentInfo(GraphAttachmentMetadata metadata)
        {
            Id = metadata.Id ?? "";
            Name = metadata.Name ?? "";
            ContentType = metadata.ContentType ?? "";
            Size = metadata.Size ?? 0;
            IsInline = metadata.IsInline ?? false;
            Kind = MailQueries.KindFromODataType(metadata.ODataType);
            LastModifiedText = metadata.LastModifiedDateTime ?? "";
            SuggestedFileName = MailQueries.SuggestFileName(Name, Kind);
        }

        /// <summary>Attachment id (immutable-id format; case-sensitive).</summary>
        public string Id { get; }

        /// <summary>Display name as sent (usually the file name).</summary>
        public string Name { get; }

        /// <summary>MIME type; "" when Graph reports none (item attachments).</summary>
        public string ContentType { get; }

        /// <summary>Size reported by Graph in bytes (approximate for item attachments).</summary>
        public long Size { get; }

        /// <summary>True for inline attachments (e.g. images referenced as cid: in the HTML body).</summary>
        public bool IsInline { get; }

        /// <summary>
        /// Content-ID of inline attachments. Always "" in this version: contentId is a fileAttachment-only property that cannot
        /// be selected on the polymorphic attachments collection (docs/research-notes-v2.md).
        /// </summary>
        public string ContentId
        {
            get { return ""; }
        }

        /// <summary>"file", "item" (attached mail/event/contact, downloaded as MIME) or "reference" (cloud link, not downloadable).</summary>
        public string Kind { get; }

        /// <summary>lastModifiedDateTime as returned by Graph (ISO 8601 UTC), "" if none.</summary>
        public string LastModifiedText { get; }

        /// <summary>Name made safe for a Windows file name; item attachments get ".eml".</summary>
        public string SuggestedFileName { get; }
    }
}
