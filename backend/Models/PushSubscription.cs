using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatApp.Models;

public class UserPushSubscription
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [BsonElement("p256dh")]
    public string P256dh { get; set; } = string.Empty;

    [BsonElement("auth")]
    public string Auth { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
