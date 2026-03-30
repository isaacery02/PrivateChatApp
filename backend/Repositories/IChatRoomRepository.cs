using ChatApp.Models;

namespace ChatApp.Repositories;

public interface IChatRoomRepository
{
    Task<List<ChatRoom>> GetAllAsync(int limit = 200);
    // Returns public rooms + private rooms the user is a member of
    Task<List<ChatRoom>> GetAccessibleAsync(string userId, int limit = 200);
    Task<ChatRoom?> GetByIdAsync(string id);
    Task<ChatRoom?> GetByNameAsync(string name);
    Task CreateAsync(ChatRoom room);
    Task<bool> AddMemberAsync(string roomId, string userId);
    Task<bool> RemoveMemberAsync(string roomId, string userId);
    Task DeleteAsync(string id);
    Task<bool> UpdateAsync(string id, string? name, string? description);
}
