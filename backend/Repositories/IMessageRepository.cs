using ChatApp.Models;

namespace ChatApp.Repositories;

public interface IMessageRepository
{
    Task<List<Message>> GetByRoomAsync(string roomId, int limit = 50, string? before = null);
    Task<long> CountByRoomAsync(string roomId);
    Task<Message?> GetByIdAsync(string id);
    Task CreateAsync(Message message);
    Task<bool> EditAsync(string id, string senderId, string newContent);
    Task<bool> SoftDeleteAsync(string id, string senderId);
    Task<bool> ToggleReactionAsync(string id, string emoji, string userId);
    Task<long> DeleteOlderThanAsync(DateTime cutoff);
    Task<long> DeleteByRoomAsync(string roomId);
    Task<List<string>> GetDmRoomsForUserAsync(string userId);
    Task<List<Message>> SearchAsync(string roomId, string query, int limit = 20);
    Task<List<Message>> GetPinnedByRoomAsync(string roomId);
    Task<bool> SetPinnedAsync(string id, bool isPinned);
}
