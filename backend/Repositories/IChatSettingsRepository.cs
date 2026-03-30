using ChatApp.Models;

namespace ChatApp.Repositories;

public interface IChatSettingsRepository
{
    Task<ChatSettings> GetAsync();
    Task SaveAsync(ChatSettings settings);
}
