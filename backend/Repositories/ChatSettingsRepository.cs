using ChatApp.Models;
using MongoDB.Driver;

namespace ChatApp.Repositories;

public class ChatSettingsRepository : IChatSettingsRepository
{
    private readonly IMongoCollection<ChatSettings> _col;

    public ChatSettingsRepository(IMongoDatabase database)
    {
        _col = database.GetCollection<ChatSettings>("chat_settings");
    }

    public async Task<ChatSettings> GetAsync()
    {
        var settings = await _col.Find(_ => true).FirstOrDefaultAsync();
        return settings ?? new ChatSettings();
    }

    public async Task SaveAsync(ChatSettings settings)
    {
        settings.UpdatedAt = DateTime.UtcNow;
        if (settings.Id is null)
        {
            await _col.InsertOneAsync(settings);
        }
        else
        {
            await _col.ReplaceOneAsync(
                s => s.Id == settings.Id,
                settings,
                new ReplaceOptions { IsUpsert = true });
        }
    }
}
