using System.Collections.Generic;
using System.Runtime.Serialization;
using NavMailGraph.Graph;

// Graph shapes read by GraphMailbox (docs/research-notes-v2.md, "Microsoft Graph: reading and polling mail").
// Only the members we read are declared; DataContractJsonSerializer ignores everything else.
//   message:     https://learn.microsoft.com/en-us/graph/api/resources/message
//   attachment:  https://learn.microsoft.com/en-us/graph/api/resources/attachment
//   mailFolder:  https://learn.microsoft.com/en-us/graph/api/resources/mailfolder
//   delta:       https://learn.microsoft.com/en-us/graph/delta-query-messages
namespace NavMailGraph.Reading
{
    [DataContract]
    internal sealed class GraphRemovedInfo
    {
        [DataMember(Name = "reason", EmitDefaultValue = false)]
        public string? Reason { get; set; }
    }

    [DataContract]
    internal sealed class GraphReadMessage
    {
        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string? Id { get; set; }

        [DataMember(Name = "internetMessageId", EmitDefaultValue = false)]
        public string? InternetMessageId { get; set; }

        [DataMember(Name = "conversationId", EmitDefaultValue = false)]
        public string? ConversationId { get; set; }

        [DataMember(Name = "subject", EmitDefaultValue = false)]
        public string? Subject { get; set; }

        [DataMember(Name = "from", EmitDefaultValue = false)]
        public GraphRecipient? From { get; set; }

        [DataMember(Name = "sender", EmitDefaultValue = false)]
        public GraphRecipient? Sender { get; set; }

        [DataMember(Name = "toRecipients", EmitDefaultValue = false)]
        public List<GraphRecipient>? ToRecipients { get; set; }

        [DataMember(Name = "ccRecipients", EmitDefaultValue = false)]
        public List<GraphRecipient>? CcRecipients { get; set; }

        [DataMember(Name = "replyTo", EmitDefaultValue = false)]
        public List<GraphRecipient>? ReplyTo { get; set; }

        [DataMember(Name = "receivedDateTime", EmitDefaultValue = false)]
        public string? ReceivedDateTime { get; set; }

        [DataMember(Name = "sentDateTime", EmitDefaultValue = false)]
        public string? SentDateTime { get; set; }

        [DataMember(Name = "isRead", EmitDefaultValue = false)]
        public bool? IsRead { get; set; }

        [DataMember(Name = "importance", EmitDefaultValue = false)]
        public string? Importance { get; set; }

        [DataMember(Name = "hasAttachments", EmitDefaultValue = false)]
        public bool? HasAttachments { get; set; }

        [DataMember(Name = "bodyPreview", EmitDefaultValue = false)]
        public string? BodyPreview { get; set; }

        [DataMember(Name = "body", EmitDefaultValue = false)]
        public GraphItemBody? Body { get; set; }

        [DataMember(Name = "webLink", EmitDefaultValue = false)]
        public string? WebLink { get; set; }

        /// <summary>Delta only: present on entries that were deleted or moved out of the folder.</summary>
        [DataMember(Name = "@removed", EmitDefaultValue = false)]
        public GraphRemovedInfo? Removed { get; set; }
    }

    [DataContract]
    internal sealed class GraphReadMessagePage
    {
        [DataMember(Name = "value", EmitDefaultValue = false)]
        public List<GraphReadMessage>? Value { get; set; }

        [DataMember(Name = "@odata.nextLink", EmitDefaultValue = false)]
        public string? NextLink { get; set; }

        [DataMember(Name = "@odata.deltaLink", EmitDefaultValue = false)]
        public string? DeltaLink { get; set; }
    }

    [DataContract]
    internal sealed class GraphAttachmentMetadata
    {
        /// <summary>Control annotation returned without being selected, e.g. "#microsoft.graph.fileAttachment".</summary>
        [DataMember(Name = "@odata.type", EmitDefaultValue = false)]
        public string? ODataType { get; set; }

        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string? Id { get; set; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string? Name { get; set; }

        [DataMember(Name = "contentType", EmitDefaultValue = false)]
        public string? ContentType { get; set; }

        [DataMember(Name = "size", EmitDefaultValue = false)]
        public long? Size { get; set; }

        [DataMember(Name = "isInline", EmitDefaultValue = false)]
        public bool? IsInline { get; set; }

        [DataMember(Name = "lastModifiedDateTime", EmitDefaultValue = false)]
        public string? LastModifiedDateTime { get; set; }
    }

    [DataContract]
    internal sealed class GraphAttachmentPage
    {
        [DataMember(Name = "value", EmitDefaultValue = false)]
        public List<GraphAttachmentMetadata>? Value { get; set; }

        [DataMember(Name = "@odata.nextLink", EmitDefaultValue = false)]
        public string? NextLink { get; set; }
    }

    [DataContract]
    internal sealed class GraphMailFolder
    {
        [DataMember(Name = "id", EmitDefaultValue = false)]
        public string? Id { get; set; }

        [DataMember(Name = "displayName", EmitDefaultValue = false)]
        public string? DisplayName { get; set; }
    }

    [DataContract]
    internal sealed class GraphMailFolderPage
    {
        [DataMember(Name = "value", EmitDefaultValue = false)]
        public List<GraphMailFolder>? Value { get; set; }

        [DataMember(Name = "@odata.nextLink", EmitDefaultValue = false)]
        public string? NextLink { get; set; }
    }

    /// <summary>POST /users/{id}/mailFolders (or .../childFolders): {"displayName":"...","isHidden":false}.</summary>
    [DataContract]
    internal sealed class CreateMailFolderRequest
    {
        [DataMember(Name = "displayName", Order = 1)]
        public string? DisplayName { get; set; }

        [DataMember(Name = "isHidden", Order = 2)]
        public bool IsHidden { get; set; }
    }

    /// <summary>POST /users/{id}/messages/{id}/move: {"destinationId":"..."}.</summary>
    [DataContract]
    internal sealed class MoveMessageRequest
    {
        [DataMember(Name = "destinationId")]
        public string? DestinationId { get; set; }
    }

    /// <summary>PATCH /users/{id}/messages/{id}: {"isRead":true|false} (false must be emitted).</summary>
    [DataContract]
    internal sealed class MarkReadRequest
    {
        [DataMember(Name = "isRead")]
        public bool IsRead { get; set; }
    }
}
