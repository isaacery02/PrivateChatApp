using ChatApp.Models;
using MongoDB.Driver;

namespace ChatApp.Repositories;

public class ChatRoomRepository : IChatRoomRepository
{
    private readonly IMongoCollection<ChatRoom> _rooms;

    public ChatRoomRepository(IMongoDatabase database)
    {
        _rooms = database.GetCollection<ChatRoom>("chat_rooms");
        var idx = Builders<ChatRoom>.IndexKeys.Ascending(r => r.Name);
        _rooms.Indexes.CreateOne(
            new CreateIndexModel<ChatRoom>(idx, new CreateIndexOptions { Unique = true }));
    }

    public Task<List<ChatRoom>> GetAllAsync(int limit = 200) =>
        _rooms.Find(_ => true).SortBy(r => r.Name).Limit(limit).ToListAsync();

    public Task<List<ChatRoom>> GetAccessibleAsync(string userId, int limit = 200) =>
        _rooms.Find(r => !r.IsPrivate || r.MemberIds.Contains(userId))
              .SortBy(r => r.Name)
              .Limit(limit)
              .ToListAsync();

    public Task<ChatRoom?> GetByIdAsync(string id) =>
        _rooms.Find(r => r.Id == id).FirstOrDefaultAsync()!;

    public Task<ChatRoom?> GetByNameAsync(string name) =>
        _rooms.Find(r => r.Name == name).FirstOrDefaultAsync()!;

    public Task CreateAsync(ChatRoom room) =>
        _rooms.InsertOneAsync(room);

    public async Task<bool> AddMemberAsync(string roomId, string userId)
    {
        var update = Builders<ChatRoom>.Update.AddToSet(r => r.MemberIds, userId);
        var result = await _rooms.UpdateOneAsync(r => r.Id == roomId, update);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> RemoveMemberAsync(string roomId, string userId)
    {
        var update = Builders<ChatRoom>.Update.Pull(r => r.MemberIds, userId);
        var result = await _rooms.UpdateOneAsync(r => r.Id == roomId, update);
        return result.ModifiedCount > 0;
    }

    public Task DeleteAsync(string id) =>
        _rooms.DeleteOneAsync(r => r.Id == id);

    public async Task<bool> UpdateAsync(string id, string? name, string? description)
    {
        var updates = new List<UpdateDefinition<ChatRoom>>();
        if (name        != null) updates.Add(Builders<ChatRoom>.Update.Set(r => r.Name,        name));
        if (description != null) updates.Add(Builders<ChatRoom>.Update.Set(r => r.Description, description));
        if (updates.Count == 0) return false;
        var combined = Builders<ChatRoom>.Update.Combine(updates);
        var result = await _rooms.UpdateOneAsync(r => r.Id == id, combined);
        return result.ModifiedCount > 0;
    }
}
