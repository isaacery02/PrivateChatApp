using ChatApp.Models;
using MongoDB.Driver;

namespace ChatApp.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly IMongoCollection<Message> _messages;

    public MessageRepository(IMongoDatabase database)
    {
        _messages = database.GetCollection<Message>("messages");
        // Compound index: room + time for efficient history queries
        var idx = Builders<Message>.IndexKeys
            .Ascending(m => m.RoomId)
            .Descending(m => m.SentAt);
        _messages.Indexes.CreateOne(new CreateIndexModel<Message>(idx));
    }

    public async Task<List<Message>> GetByRoomAsync(string roomId, int limit = 50, string? before = null)
    {
        // Clamp the limit to prevent callers from requesting unbounded results
        limit = Math.Clamp(limit, 1, 200);

        var filter = Builders<Message>.Filter.Eq(m => m.RoomId, roomId);
        if (before != null)
        {
            var pivot = await _messages.Find(m => m.Id == before).FirstOrDefaultAsync();
            if (pivot != null)
                filter = Builders<Message>.Filter.And(filter,
                    Builders<Message>.Filter.Lt(m => m.SentAt, pivot.SentAt));
        }
        var messages = await _messages
            .Find(filter)
            .SortByDescending(m => m.SentAt)
            .Limit(limit)
            .ToListAsync();
        messages.Reverse(); // oldest first for display
        return messages;
    }

    public Task<long> CountByRoomAsync(string roomId) =>
        _messages.CountDocumentsAsync(m => m.RoomId == roomId);

    public Task<Message?> GetByIdAsync(string id) =>
        _messages.Find(m => m.Id == id).FirstOrDefaultAsync()!;

    public Task CreateAsync(Message message) =>
        _messages.InsertOneAsync(message);

    public async Task<bool> EditAsync(string id, string senderId, string newContent)
    {
        var update = Builders<Message>.Update
            .Set(m => m.Content, newContent)
            .Set(m => m.EditedAt, DateTime.UtcNow);
        var result = await _messages.UpdateOneAsync(
            m => m.Id == id && m.SenderId == senderId && !m.Deleted, update);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> SoftDeleteAsync(string id, string senderId)
    {
        var update = Builders<Message>.Update.Set(m => m.Deleted, true);
        var result = await _messages.UpdateOneAsync(
            m => m.Id == id && m.SenderId == senderId, update);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> ToggleReactionAsync(string id, string emoji, string userId)
    {
        // Atomic toggle: first attempt to remove the userId from the reaction list.
        // If no document was modified (user wasn't in the list), add them instead.
        // This avoids the read-then-update race condition where two concurrent toggles
        // could both read the same state and produce an incorrect result.
        var pullFilter = Builders<Message>.Filter.And(
            Builders<Message>.Filter.Eq(m => m.Id, id),
            Builders<Message>.Filter.AnyEq($"reactions.{emoji}", userId));

        var pullUpdate = Builders<Message>.Update.Pull($"reactions.{emoji}", userId);
        var pullResult = await _messages.UpdateOneAsync(pullFilter, pullUpdate);

        if (pullResult.MatchedCount > 0)
            return pullResult.ModifiedCount > 0; // user was present → removed

        // User wasn't in the list → add them atomically
        var addUpdate = Builders<Message>.Update.AddToSet($"reactions.{emoji}", userId);
        var addResult = await _messages.UpdateOneAsync(m => m.Id == id, addUpdate);
        return addResult.ModifiedCount > 0;
    }

    public async Task<long> DeleteOlderThanAsync(DateTime cutoff)
    {
        var result = await _messages.DeleteManyAsync(m => m.SentAt < cutoff);
        return result.DeletedCount;
    }

    public async Task<long> DeleteByRoomAsync(string roomId)
    {
        var result = await _messages.DeleteManyAsync(m => m.RoomId == roomId);
        return result.DeletedCount;
    }

    public async Task<List<string>> GetDmRoomsForUserAsync(string userId)
    {
        // Match any DM room whose roomId contains this user's ID as one of the two segments.
        // Format is always dm_{uid1}_{uid2} where both are 24-char hex ObjectIds (no underscores).
        var filter = Builders<Message>.Filter.And(
            Builders<Message>.Filter.Regex(m => m.RoomId, new MongoDB.Bson.BsonRegularExpression("^dm_")),
            Builders<Message>.Filter.Regex(m => m.RoomId, new MongoDB.Bson.BsonRegularExpression(userId))
        );
        return await _messages.Distinct<string>("roomId", filter).ToListAsync();
    }
}
