using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatApp.Models;

public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("username")]
    public string Username { get; set; } = string.Empty;

    [BsonElement("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [BsonElement("passwordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    [BsonElement("avatarColor")]
    public string AvatarColor { get; set; } = "#5865f2";

    [BsonElement("avatarFileId")]
    public string? AvatarFileId { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("totpSecret")]
    public string? TotpSecret { get; set; }

    [BsonElement("totpEnabled")]
    public bool TotpEnabled { get; set; } = false;

    [BsonElement("disabled")]
    public bool Disabled { get; set; } = false;
}
