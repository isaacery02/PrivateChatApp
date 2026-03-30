using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatApp.Models;

public class Message
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("roomId")]
    public string RoomId { get; set; } = string.Empty;

    [BsonElement("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [BsonElement("senderUsername")]
    public string SenderUsername { get; set; } = string.Empty;

    [BsonElement("senderDisplayName")]
    public string SenderDisplayName { get; set; } = string.Empty;

    [BsonElement("senderAvatarColor")]
    public string SenderAvatarColor { get; set; } = "#5865f2";

    [BsonElement("senderAvatarFileId")]
    public string? SenderAvatarFileId { get; set; }

    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    // true when the sender soft-deleted the message
    [BsonElement("deleted")]
    public bool Deleted { get; set; } = false;

    // set when the content was edited after first send
    [BsonElement("editedAt")]
    public DateTime? EditedAt { get; set; }

    // emoji → list of userIds who reacted
    [BsonElement("reactions")]
    public Dictionary<string, List<string>> Reactions { get; set; } = [];

    [BsonElement("attachmentId")]
    public string? AttachmentId { get; set; }

    [BsonElement("attachmentName")]
    public string? AttachmentName { get; set; }

    [BsonElement("attachmentType")]
    public string? AttachmentType { get; set; }

    [BsonElement("sentAt")]
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [BsonElement("isPinned")]
    public bool IsPinned { get; set; } = false;
}
