using System;
using System.Collections.Generic;
using NavMailGraph.Graph;
using NavMailGraph.Reading;

namespace NavMailGraph
{
    /// <summary>
    /// One message returned by <see cref="GraphMailbox.Fetch"/> / <see cref="GraphMailbox.FetchChanges"/>. Attachment
    /// metadata is loaded lazily (one Graph call per message) on the first <see cref="AttachmentCount"/> /
    /// <see cref="GetAttachment"/> access, or explicitly with <see cref="LoadAttachments"/>. In C/AL always call
    /// LoadAttachments first and stop on a non-empty result; never use AttachmentCount for error detection (it returns -1
    /// on failure, and a FOR loop to AttachmentCount - 1 then silently imports no attachments).
    /// </summary>
    public sealed class GraphMailItem
    {
        private readonly GraphMailbox? _owner;

        private List<GraphMailAttachmentInfo>? _attachments;

        /// <summary>True when inline (cid:) content can be detected from Body: the body was loaded as sent (not converted to text).</summary>
        private readonly bool _inlineDetectable;

        internal GraphMailItem(GraphReadMessage message, GraphMailbox? owner)
        {
            _owner = owner;
            Id = message.Id ?? "";
            InternetMessageId = message.InternetMessageId ?? "";
            ConversationId = message.ConversationId ?? "";
            Subject = message.Subject ?? "";
            FromAddress = Address(message.From);
            FromName = Name(message.From);
            SenderAddress = Address(message.Sender);
            SenderName = Name(message.Sender);
            ToRecipients = Join(message.ToRecipients);
            CcRecipients = Join(message.CcRecipients);
            ReplyTo = Join(message.ReplyTo);
            ReceivedText = message.ReceivedDateTime ?? "";
            ReceivedUtc = MailQueries.ParseUtc(ReceivedText);
            SentText = message.SentDateTime ?? "";
            SentUtc = MailQueries.ParseUtc(SentText);
            IsRead = message.IsRead ?? false;
            Importance = message.Importance ?? "";
            HasAttachments = message.HasAttachments ?? false;
            Body = message.Body == null ? "" : (message.Body.Content ?? "");
            BodyIsHtml = message.Body != null && string.Equals(message.Body.ContentType, "html", StringComparison.OrdinalIgnoreCase);
            BodyPreview = message.BodyPreview ?? "";
            WebLink = message.WebLink ?? "";
            AttachmentError = "";
            _inlineDetectable = message.Body != null && !(owner != null && owner.BodyAsText);
        }

        /// <summary>Immutable message id (Prefer: IdType="ImmutableId"): stable across moves in the mailbox; case-sensitive.</summary>
        public string Id { get; }

        /// <summary>RFC 2822 Message-ID (duplicate key together with the id).</summary>
        public string InternetMessageId { get; }

        /// <summary>Graph conversation id.</summary>
        public string ConversationId { get; }

        /// <summary>Subject.</summary>
        public string Subject { get; }

        /// <summary>"from" SMTP address (mailbox owner the message was sent from).</summary>
        public string FromAddress { get; }

        /// <summary>"from" display name.</summary>
        public string FromName { get; }

        /// <summary>Account that actually sent the message (differs from From for delegates / shared mailboxes).</summary>
        public string SenderAddress { get; }

        /// <summary>"sender" display name.</summary>
        public string SenderName { get; }

        /// <summary>Addresses separated by "; ".</summary>
        public string ToRecipients { get; }

        /// <summary>Addresses separated by "; ".</summary>
        public string CcRecipients { get; }

        /// <summary>Addresses separated by "; ".</summary>
        public string ReplyTo { get; }

        /// <summary>receivedDateTime as DateTime (Kind=Utc); DateTime.MinValue when missing.</summary>
        public DateTime ReceivedUtc { get; }

        /// <summary>receivedDateTime exactly as returned by Graph (ISO 8601 UTC, e.g. 2026-10-12T08:00:00Z). Use it as watermark (GraphMailbox.ReceivedAfter).</summary>
        public string ReceivedText { get; }

        /// <summary>sentDateTime as DateTime (Kind=Utc); DateTime.MinValue when missing.</summary>
        public DateTime SentUtc { get; }

        /// <summary>sentDateTime exactly as returned by Graph.</summary>
        public string SentText { get; }

        /// <summary>Read flag at fetch time.</summary>
        public bool IsRead { get; }

        /// <summary>"low", "normal" or "high".</summary>
        public string Importance { get; }

        /// <summary>Graph hasAttachments (false for messages with inline attachments only).</summary>
        public bool HasAttachments { get; }

        /// <summary>Body ("" when GraphMailbox.IncludeBody was false).</summary>
        public string Body { get; }

        /// <summary>True when Body is HTML.</summary>
        public bool BodyIsHtml { get; }

        /// <summary>First 255 characters of the body as text.</summary>
        public string BodyPreview { get; }

        /// <summary>Outlook on the web link to the message.</summary>
        public string WebLink { get; }

        /// <summary>
        /// Number of attachments (inline ones included). Loads the metadata on first access when the message has attachments
        /// or inline content may exist (see <see cref="LoadAttachments"/>). Returns -1 when loading failed (see
        /// <see cref="AttachmentError"/>): check for a negative value, or better call <see cref="LoadAttachments"/> first and
        /// treat a non-empty result as an error.
        /// </summary>
        public int AttachmentCount
        {
            get
            {
                if (_attachments == null && LoadAttachments().Length > 0)
                {
                    return -1;
                }

                return _attachments == null ? 0 : _attachments.Count;
            }
        }

        /// <summary>Error text of the last failed attachment metadata load, "" if none.</summary>
        public string AttachmentError { get; private set; }

        /// <summary>
        /// Loads the attachment metadata (once; later calls return "" without a Graph call). "" on success or the error text.
        /// Skipped (no Graph call, zero attachments) only when hasAttachments is false and the body was loaded as sent
        /// (IncludeBody true, BodyAsText false) and contains no "cid:" reference. Without such a body inline-only attachments
        /// cannot be detected (hasAttachments is false for them), so the metadata is always loaded.
        /// </summary>
        public string LoadAttachments()
        {
            if (_attachments != null)
            {
                return "";
            }

            if (!MayHaveAttachments)
            {
                _attachments = new List<GraphMailAttachmentInfo>();
                return "";
            }

            if (_owner == null)
            {
                AttachmentError = "Validation failed: this message is not bound to a GraphMailbox.";
                return AttachmentError;
            }

            List<GraphMailAttachmentInfo>? loaded;
            string error = _owner.LoadAttachmentMetadata(Id, out loaded);
            AttachmentError = error;
            if (error.Length == 0)
            {
                _attachments = loaded ?? new List<GraphMailAttachmentInfo>();
            }

            return error;
        }

        /// <summary>Attachment metadata by 0-based index (loads lazily).</summary>
        public GraphMailAttachmentInfo GetAttachment(int index)
        {
            int count = AttachmentCount;
            if (index < 0 || index >= count || _attachments == null)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Attachment index " + index + " is out of range (AttachmentCount " + count + ").");
            }

            return _attachments[index];
        }

        /// <summary>
        /// True when a metadata call is needed: hasAttachments, inline content (cid:) in the body, or a body that cannot show
        /// inline content (not loaded, or converted to text).
        /// </summary>
        internal bool MayHaveAttachments
        {
            get { return HasAttachments || !_inlineDetectable || Body.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        /// <summary>True once the metadata was loaded.</summary>
        internal bool AttachmentsLoaded
        {
            get { return _attachments != null; }
        }

        private static string Address(GraphRecipient? recipient)
        {
            return recipient == null || recipient.EmailAddress == null ? "" : (recipient.EmailAddress.Address ?? "");
        }

        private static string Name(GraphRecipient? recipient)
        {
            return recipient == null || recipient.EmailAddress == null ? "" : (recipient.EmailAddress.Name ?? "");
        }

        private static string Join(List<GraphRecipient>? recipients)
        {
            if (recipients == null || recipients.Count == 0)
            {
                return "";
            }

            var parts = new List<string>(recipients.Count);
            foreach (GraphRecipient recipient in recipients)
            {
                string address = Address(recipient);
                if (address.Length > 0)
                {
                    parts.Add(address);
                }
            }

            return string.Join("; ", parts.ToArray());
        }
    }
}
