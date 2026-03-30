using ChatApp.Models;
using MongoDB.Driver;

namespace ChatApp.Repositories;

public class UserRepository : IUserRepository
{
    private readonly IMongoCollection<User> _users;

    public UserRepository(IMongoDatabase database)
    {
        _users = database.GetCollection<User>("users");
        // Unique index on username
        var idx = Builders<User>.IndexKeys.Ascending(u => u.Username);
        _users.Indexes.CreateOne(
            new CreateIndexModel<User>(idx, new CreateIndexOptions { Unique = true }));
    }

    public Task<List<User>> GetAllAsync(int limit = 500) =>
        _users.Find(_ => true).SortBy(u => u.Username).Limit(limit).ToListAsync();

    public Task<User?> GetByUsernameAsync(string username) =>
        _users.Find(u => u.Username == username).FirstOrDefaultAsync()!;

    public Task<User?> GetByIdAsync(string id) =>
        _users.Find(u => u.Id == id).FirstOrDefaultAsync()!;

    public Task CreateAsync(User user) =>
        _users.InsertOneAsync(user);

    public Task DeleteAsync(string id) =>
        _users.DeleteOneAsync(u => u.Id == id);

    public Task UpdateAvatarAsync(string id, string? avatarFileId)
    {
        var update = Builders<User>.Update.Set(u => u.AvatarFileId, avatarFileId);
        return _users.UpdateOneAsync(u => u.Id == id, update);
    }

    public Task UpdateProfileAsync(string id, string? displayName, string? avatarColor)
    {
        var updates = new List<UpdateDefinition<User>>();
        if (displayName is not null)
            updates.Add(Builders<User>.Update.Set(u => u.DisplayName, displayName));
        if (avatarColor is not null)
            updates.Add(Builders<User>.Update.Set(u => u.AvatarColor, avatarColor));
        if (updates.Count == 0) return Task.CompletedTask;
        return _users.UpdateOneAsync(u => u.Id == id,
            Builders<User>.Update.Combine(updates));
    }

    public Task UpdateTotpAsync(string id, string? secret, bool enabled)
    {
        var update = Builders<User>.Update
            .Set(u => u.TotpSecret, secret)
            .Set(u => u.TotpEnabled, enabled);
        return _users.UpdateOneAsync(u => u.Id == id, update);
    }

    public Task SetDisabledAsync(string id, bool disabled)
    {
        var update = Builders<User>.Update.Set(u => u.Disabled, disabled);
        return _users.UpdateOneAsync(u => u.Id == id, update);
    }
}
