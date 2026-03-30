using ChatApp.Models;

namespace ChatApp.Repositories;

public interface IPushSubscriptionRepository
{
    Task UpsertAsync(UserPushSubscription sub);
    Task<List<UserPushSubscription>> GetByUserAsync(string userId);
    Task<List<UserPushSubscription>> GetAllAsync(int limit = 10000);
    Task DeleteAsync(string endpoint);
    Task DeleteByUserAsync(string userId, string endpoint);
}
