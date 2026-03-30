using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ChatApp.Models;

public class ChatSettings
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public bool PurgeEnabled   { get; set; } = false;
    public int  PurgeAfterDays { get; set; } = 30;
    public int  HistoryLimit   { get; set; } = 50;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
}
