using ChatApp.Models;
using MongoDB.Driver;

namespace ChatApp.Repositories;

public class PushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly IMongoCollection<UserPushSubscription> _col;

    public PushSubscriptionRepository(IMongoDatabase database)
    {
        _col = database.GetCollection<UserPushSubscription>("pushSubscriptions");
        // Unique index on endpoint — one document per browser subscription
        var idx = Builders<UserPushSubscription>.IndexKeys.Ascending(s => s.Endpoint);
        _col.Indexes.CreateOne(new CreateIndexModel<UserPushSubscription>(
            idx, new CreateIndexOptions { Unique = true }));
    }

    public async Task UpsertAsync(UserPushSubscription sub)
    {
        var filter = Builders<UserPushSubscription>.Filter.Eq(s => s.Endpoint, sub.Endpoint);
        var update = Builders<UserPushSubscription>.Update
            .Set(s => s.UserId,    sub.UserId)
            .Set(s => s.P256dh,    sub.P256dh)
            .Set(s => s.Auth,      sub.Auth)
            .Set(s => s.CreatedAt, sub.CreatedAt);
        await _col.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public Task<List<UserPushSubscription>> GetByUserAsync(string userId) =>
        _col.Find(s => s.UserId == userId).ToListAsync();

    public Task<List<UserPushSubscription>> GetAllAsync(int limit = 10000) =>
        _col.Find(_ => true).Limit(limit).ToListAsync();

    public Task DeleteAsync(string endpoint) =>
        _col.DeleteOneAsync(s => s.Endpoint == endpoint);

    public Task DeleteByUserAsync(string userId, string endpoint) =>
        _col.DeleteOneAsync(s => s.Endpoint == endpoint && s.UserId == userId);
}
