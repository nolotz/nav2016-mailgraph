using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NavMailGraph.Graph;

namespace NavMailGraph
{
    /// <summary>
    /// Message builder. Mirrors what Codeunit 400 "SMTP Mail" collects before Send. Data problems (bad recipient,
    /// bad header name) are recorded and reported by <see cref="GraphMailSender.Send"/> as "Validation failed: ..."
    /// so that no builder method throws into C/AL; only null arguments for content throw <see cref="ArgumentNullException"/>.
    /// </summary>
    public sealed class GraphMailMessage
    {
        /// <summary>
        /// Graph accepts custom internetMessageHeaders only when the name starts with "x-" or "X-".
        /// https://learn.microsoft.com/en-us/graph/api/resources/internetmessageheader
        /// </summary>
        internal const bool RequireCustomHeaderPrefix = true;

        internal const string CustomHeaderPrefix = "x-";

        private readonly List<MailRecipient> _to = new List<MailRecipient>();

        private readonly List<MailRecipient> _cc = new List<MailRecipient>();

        private readonly List<MailRecipient> _bcc = new List<MailRecipient>();

        private readonly List<MailRecipient> _replyTo = new List<MailRecipient>();

        private readonly List<MailAttachment> _attachments = new List<MailAttachment>();

        private readonly List<KeyValuePair<string, string>> _headers = new List<KeyValuePair<string, string>>();

        private readonly List<string> _validationErrors = new List<string>();

        private readonly StringBuilder _body = new StringBuilder();

        private string _fromName = "";

        private string _fromAddress = "";

        private string _subject = "";

        private string _importance = "";

        /// <summary>Creates an empty message.</summary>
        public GraphMailMessage()
        {
        }

        /// <summary>Display name for "from".</summary>
        public string FromName
        {
            get { return _fromName; }
            set { _fromName = (value ?? "").Trim(); }
        }

        /// <summary>SMTP address NAV wants as sender. Also the mailbox when <see cref="GraphMailSender.SenderMailbox"/> is empty.</summary>
        public string FromAddress
        {
            get { return _fromAddress; }
            set { _fromAddress = (value ?? "").Trim(); }
        }

        /// <summary>Subject line.</summary>
        public string Subject
        {
            get { return _subject; }
            set { _subject = value ?? ""; }
        }

        /// <summary>Body text; replaces everything appended so far.</summary>
        public string Body
        {
            get { return _body.ToString(); }
            set
            {
                _body.Length = 0;
                _body.Append(value ?? "");
            }
        }

        /// <summary>True sends the body as HTML, false as plain text.</summary>
        public bool HtmlFormatted { get; set; }

        /// <summary>"" (default), "low", "normal" or "high" (case-insensitive).</summary>
        public string Importance
        {
            get { return _importance; }
            set { _importance = (value ?? "").Trim(); }
        }

        /// <summary>Adds To recipients: "a@x.de; Name &lt;b@y.de&gt;, c@z.de".</summary>
        public void AddRecipients(string recipients)
        {
            EmailAddressParser.Parse(recipients ?? "", _to, _validationErrors);
        }

        /// <summary>Adds CC recipients (same syntax as <see cref="AddRecipients"/>).</summary>
        public void AddCC(string recipients)
        {
            EmailAddressParser.Parse(recipients ?? "", _cc, _validationErrors);
        }

        /// <summary>Adds BCC recipients (same syntax as <see cref="AddRecipients"/>).</summary>
        public void AddBCC(string recipients)
        {
            EmailAddressParser.Parse(recipients ?? "", _bcc, _validationErrors);
        }

        /// <summary>Adds Reply-To addresses (same syntax as <see cref="AddRecipients"/>).</summary>
        public void AddReplyTo(string recipients)
        {
            EmailAddressParser.Parse(recipients ?? "", _replyTo, _validationErrors);
        }

        /// <summary>Appends text to the body (Codeunit 400 builds bodies line by line).</summary>
        public void AppendBody(string text)
        {
            _body.Append(text ?? "");
        }

        /// <summary>
        /// Adds a file attachment. The file is read at Send time (streamed in chunks when large), so it must still exist
        /// then. <paramref name="fileName"/> "" uses the file's own name.
        /// </summary>
        public void AddAttachment(string filePath, string fileName)
        {
            AddFileAttachment(filePath, fileName, false, "");
        }

        /// <summary>Adds an in-memory attachment.</summary>
        public void AddAttachmentBytes(byte[] content, string fileName)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            string name = (fileName ?? "").Trim();
            if (name.Length == 0)
            {
                _validationErrors.Add("Attachment file name is required for in-memory attachments.");
                return;
            }

            _attachments.Add(new MailAttachment(name, ContentTypes.FromFileName(name), null, content, false, ""));
        }

        /// <summary>Adds an attachment from a stream; the stream is read to the end now and buffered in memory.</summary>
        public void AddAttachmentStream(Stream content, string fileName)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                content.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            AddAttachmentBytes(bytes, fileName);
        }

        /// <summary>Adds an inline attachment (isInline = true) referenced from an HTML body as &lt;img src="cid:{contentId}"&gt;.</summary>
        public void AddInlineAttachment(string filePath, string fileName, string contentId)
        {
            AddFileAttachment(filePath, fileName, true, (contentId ?? "").Trim());
        }

        /// <summary>Adds a custom internet message header. Graph requires the name to start with "x-" or "X-".</summary>
        public void AddHeader(string name, string value)
        {
            string headerName = (name ?? "").Trim();
            if (headerName.Length == 0)
            {
                _validationErrors.Add("Custom header name is empty.");
                return;
            }

            if (RequireCustomHeaderPrefix && !headerName.StartsWith(CustomHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _validationErrors.Add("Custom header '" + headerName + "' must start with 'x-' or 'X-' (Microsoft Graph rule for internetMessageHeaders).");
                return;
            }

            _headers.Add(new KeyValuePair<string, string>(headerName, value ?? ""));
        }

        /// <summary>Number of To + CC + BCC recipients.</summary>
        public int RecipientCount
        {
            get { return _to.Count + _cc.Count + _bcc.Count; }
        }

        /// <summary>Number of attachments (regular and inline).</summary>
        public int AttachmentCount
        {
            get { return _attachments.Count; }
        }

        /// <summary>Sum of attachment sizes in bytes (files are measured now; missing files count as 0).</summary>
        public long TotalAttachmentBytes
        {
            get
            {
                long total = 0;
                foreach (MailAttachment attachment in _attachments)
                {
                    total += attachment.TryGetSize();
                }

                return total;
            }
        }

        /// <summary>Returns the body.</summary>
        public string GetBody()
        {
            return Body;
        }

        /// <summary>Returns the subject.</summary>
        public string GetSubject()
        {
            return Subject;
        }

        /// <summary>One-line summary for logs and error texts: subject, sender, recipients, attachments. No body, no secrets.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("subject=\"").Append(GraphClient.Truncate(_subject, 80)).Append('"');
            sb.Append(" from=");
            if (_fromAddress.Length == 0)
            {
                sb.Append("(mailbox)");
            }
            else
            {
                sb.Append(_fromName.Length > 0 ? _fromName + " <" + _fromAddress + ">" : _fromAddress);
            }

            sb.Append(" to=").Append(DescribeList(_to));
            sb.Append(" cc=").Append(DescribeList(_cc));
            sb.Append(" bcc=").Append(DescribeList(_bcc));
            if (_replyTo.Count > 0)
            {
                sb.Append(" replyTo=").Append(DescribeList(_replyTo));
            }

            sb.Append(" format=").Append(HtmlFormatted ? "HTML" : "Text");
            if (_importance.Length > 0)
            {
                sb.Append(" importance=").Append(_importance);
            }

            sb.Append(" bodyChars=").Append(_body.Length);
            sb.Append(" attachments=").Append(_attachments.Count).Append(" (").Append(TotalAttachmentBytes).Append(" bytes)");
            if (_attachments.Count > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < _attachments.Count && i < 5; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(_attachments[i].Describe());
                }

                if (_attachments.Count > 5)
                {
                    sb.Append(", ... (+").Append(_attachments.Count - 5).Append(')');
                }

                sb.Append(']');
            }

            if (_headers.Count > 0)
            {
                sb.Append(" headers=").Append(_headers.Count);
            }

            if (_validationErrors.Count > 0)
            {
                sb.Append(" validationErrors=").Append(_validationErrors.Count);
            }

            return sb.ToString();
        }

        internal List<MailRecipient> ToList
        {
            get { return _to; }
        }

        internal List<MailRecipient> CcList
        {
            get { return _cc; }
        }

        internal List<MailRecipient> BccList
        {
            get { return _bcc; }
        }

        internal List<MailRecipient> ReplyToList
        {
            get { return _replyTo; }
        }

        internal List<MailAttachment> AttachmentList
        {
            get { return _attachments; }
        }

        internal List<KeyValuePair<string, string>> HeaderList
        {
            get { return _headers; }
        }

        /// <summary>Problems recorded by the builder methods, reported by Send as "Validation failed: ...".</summary>
        internal List<string> ValidationErrors
        {
            get { return _validationErrors; }
        }

        private void AddFileAttachment(string filePath, string fileName, bool isInline, string contentId)
        {
            string path = (filePath ?? "").Trim();
            if (path.Length == 0)
            {
                _validationErrors.Add("Attachment file path is empty" + (fileName != null && fileName.Trim().Length > 0 ? " for '" + fileName.Trim() + "'" : "") + ".");
                return;
            }

            string name = (fileName ?? "").Trim();
            if (name.Length == 0)
            {
                try
                {
                    name = Path.GetFileName(path);
                }
                catch (ArgumentException)
                {
                    name = "";
                }

                if (name.Length == 0)
                {
                    _validationErrors.Add("Attachment file name could not be derived from path '" + path + "'.");
                    return;
                }
            }

            _attachments.Add(new MailAttachment(name, ContentTypes.FromFileName(name), path, null, isInline, contentId));
        }

        private static string DescribeList(List<MailRecipient> recipients)
        {
            if (recipients.Count == 0)
            {
                return "[]";
            }

            var sb = new StringBuilder("[");
            for (int i = 0; i < recipients.Count && i < 5; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(recipients[i].Describe());
            }

            if (recipients.Count > 5)
            {
                sb.Append(", ... (+").Append(recipients.Count - 5).Append(')');
            }

            return sb.Append(']').ToString();
        }
    }
}
