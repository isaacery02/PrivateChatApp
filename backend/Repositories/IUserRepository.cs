using ChatApp.Models;

namespace ChatApp.Repositories;

public interface IUserRepository
{
    Task<List<User>> GetAllAsync(int limit = 500);
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByIdAsync(string id);
    Task CreateAsync(User user);
    Task DeleteAsync(string id);
    Task UpdateAvatarAsync(string id, string? avatarFileId);
    Task UpdateProfileAsync(string id, string? displayName, string? avatarColor);
    Task UpdateTotpAsync(string id, string? secret, bool enabled);
    Task SetDisabledAsync(string id, bool disabled);
}
