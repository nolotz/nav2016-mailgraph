using System.Collections.Generic;
using System.Runtime.Serialization;

// Microsoft Graph resource shapes used by this library. Only the members we send or read are declared; the
// serializer ignores unknown members on input and omits null members on output (EmitDefaultValue = false).
// References:
//   message:            https://learn.microsoft.com/en-us/graph/api/resources/message
//   sendMail:           https://learn.microsoft.com/en-us/graph/api/user-sendmail
//   fileAttachment:     https://learn.microsoft.com/en-us/graph/api/resources/fileattachment
//   attachmentItem:     https://learn.microsoft.com/en-us/graph/api/resources/attachmentitem
//   upload session:     https://learn.microsoft.com/en-us/graph/outlook-large-attachments
//   error response:     https://learn.microsoft.com/en-us/graph/errors
//   token response:     https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
namespace NavMailGraph.Graph
{
    [DataContract]
    internal sealed class GraphEmailAddress
    {
        [DataMember(Name = "address", EmitDefaultValue = false)]
        public string? Address { get; set; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string? Name { get; set; }
    }

    [DataContract]
    internal sealed class GraphRecipient
    {
        [DataMember(Name = "emailAddress", EmitDefaultValue = false)]
        public GraphEmailAddress? EmailAddress { get; set; }
    }

    [DataContract]
    internal sealed class GraphItemBody
    {
        /// <summary>"Text" or "HTML".</summary>
        [DataMember(Name = "contentType", EmitDefaultValue = false)]
        public string? ContentType { get; set; }

        [DataMember(Name = "content", EmitDefaultValue = false)]
        public string? Content { get; set; }
    }

    [DataContract]
    internal sealed class GraphInternetMessageHeader
    {
        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string? Name { get; set; }

        [DataMember(Name = "value", EmitDefaultValue = false)]
        public string? Value { get; set; }
    }

    [DataContract]
    internal sealed class GraphFileAttachment
    {
        internal const string ODataTypeValue = "#microsoft.graph.fileAttachment";

        [DataMember(Name = "@odata.type", EmitDefaultValue = false)]
        public string? ODataType { get; set; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string? Name { get; set; }

        [DataMember(Name = "contentType", EmitDefaultValue = false)]
        public string? ContentType { get; set; }

        /// <summary>Base64-encoded content.</summary>
        [DataMember(Name = "contentBytes", EmitDefaultValue = false)]
        public string? ContentBytes { get; set; }

        [DataMember(Name = "isInline", EmitDefaultValue = false)]
        public bool? IsInline { get; set; }

        [DataMember(Name = "contentId", EmitDefaultValue = false)]
        public string? ContentId { get; set; }
    }

    [DataContract]
    internal sealed class GraphMessage
    {
        [DataMember(Name = "subject", EmitDefaultValue = false)]
        public string? Subject { get; set; }

        [DataMember(Name = "body", EmitDefaultValue = false)]
        public GraphItemBody? Body { get; set; }

        [DataMember(Name = "toRecipients", EmitDefaultValue = false)]
        public List<GraphRecipient>? ToRecipients { get; set; }

        [DataMember(Name = "ccRecipients", EmitDefaultValue = false)]
        public List<GraphRecipient>? CcRecipients { get; set; }

        [DataMember(Name = "bccRecipients", EmitDefaultValue = false)]
        public List<GraphRecipient>? BccRecipients { get; set; }

        [DataMember(Name = "replyTo", EmitDefaultValue = false)]
        public List<GraphRecipient>? ReplyTo { get; set; }

        [DataMember(Name = "from", EmitDefaultValue = false)]
        public GraphRecipient? From { get; set; }

        /// <summary>"low", "normal" or "high".</summary>
        [DataMember(Name = "importance", EmitDefaultValue = false)]
        public string? Importance { get; set; }

        [DataMember(Name = "internetMessageHeaders", EmitDefaultValue = false)]
        public List<GraphInternetMessageHeader>? InternetMessageHeaders { get; set; }

        [DataMember(Name = "attachments", EmitDefaultValue = false)]
        public List<GraphFileAttachment>? Attachments { get; set; }
    }

    /// <summary>Envelope of POST /users/{id}/sendMail.</summary>
    [DataContract]
    internal sealed class SendMailRequest
    {
        [DataMember(Name = "message", EmitDefaultValue = false)]
        public GraphMessage? Message { get; set; }

        /// <summary>Always emitted (false must reach Graph).</summary>
        [DataMember(Name = "saveToSentItems")]
        public bool SaveToSentItems { get; set; }
    }

    /// <summary>Subset of the message returned by POST /users/{id}/messages.</summary>
    [DataContract]
    internal sealed class GraphMessageResponse
    {
        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string? Id { get; set; }
    }

    [DataContract]
    internal sealed class AttachmentItem
    {
        [DataMember(Name = "attachmentType", EmitDefaultValue = false)]
        public string? AttachmentType { get; set; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string? Name { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "contentType", EmitDefaultValue = false)]
        public string? ContentType { get; set; }

        [DataMember(Name = "isInline", EmitDefaultValue = false)]
        public bool? IsInline { get; set; }

        [DataMember(Name = "contentId", EmitDefaultValue = false)]
        public string? ContentId { get; set; }
    }

    /// <summary>Body of POST .../attachments/createUploadSession.</summary>
    [DataContract]
    internal sealed class UploadSessionRequest
    {
        [DataMember(Name = "AttachmentItem", EmitDefaultValue = false)]
        public AttachmentItem? AttachmentItem { get; set; }
    }

    [DataContract]
    internal sealed class UploadSessionResponse
    {
        [DataMember(Name = "uploadUrl", EmitDefaultValue = false)]
        public string? UploadUrl { get; set; }

        [DataMember(Name = "expirationDateTime", EmitDefaultValue = false)]
        public string? ExpirationDateTime { get; set; }

        [DataMember(Name = "nextExpectedRanges", EmitDefaultValue = false)]
        public List<string>? NextExpectedRanges { get; set; }
    }

    [DataContract]
    internal sealed class GraphInnerError
    {
        [DataMember(Name = "request-id", EmitDefaultValue = false)]
        public string? RequestId { get; set; }

        [DataMember(Name = "client-request-id", EmitDefaultValue = false)]
        public string? ClientRequestId { get; set; }

        [DataMember(Name = "date", EmitDefaultValue = false)]
        public string? Date { get; set; }
    }

    [DataContract]
    internal sealed class GraphError
    {
        [DataMember(Name = "code", EmitDefaultValue = false)]
        public string? Code { get; set; }

        [DataMember(Name = "message", EmitDefaultValue = false)]
        public string? Message { get; set; }

        [DataMember(Name = "innerError", EmitDefaultValue = false)]
        public GraphInnerError? InnerError { get; set; }
    }

    [DataContract]
    internal sealed class GraphErrorResponse
    {
        [DataMember(Name = "error", EmitDefaultValue = false)]
        public GraphError? Error { get; set; }
    }

    /// <summary>Success body of the token endpoint. expires_in is read as text to accept both numbers and strings.</summary>
    [DataContract]
    internal sealed class TokenResponse
    {
        [DataMember(Name = "access_token", EmitDefaultValue = false)]
        public string? AccessToken { get; set; }

        [DataMember(Name = "token_type", EmitDefaultValue = false)]
        public string? TokenType { get; set; }

        [DataMember(Name = "expires_in", EmitDefaultValue = false)]
        public string? ExpiresIn { get; set; }
    }

    /// <summary>Error body of the token endpoint.</summary>
    [DataContract]
    internal sealed class TokenErrorResponse
    {
        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string? Error { get; set; }

        [DataMember(Name = "error_description", EmitDefaultValue = false)]
        public string? ErrorDescription { get; set; }

        [DataMember(Name = "error_codes", EmitDefaultValue = false)]
        public long[]? ErrorCodes { get; set; }

        [DataMember(Name = "correlation_id", EmitDefaultValue = false)]
        public string? CorrelationId { get; set; }

        [DataMember(Name = "trace_id", EmitDefaultValue = false)]
        public string? TraceId { get; set; }
    }
}
