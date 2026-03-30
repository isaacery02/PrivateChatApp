using ChatApp.Repositories;

namespace ChatApp.Services;

public class MessagePurgeService : BackgroundService
{
    private readonly IChatSettingsRepository _settings;
    private readonly IMessageRepository _messages;
    private readonly ILogger<MessagePurgeService> _logger;

    public MessagePurgeService(
        IChatSettingsRepository settings,
        IMessageRepository messages,
        ILogger<MessagePurgeService> logger)
    {
        _settings = settings;
        _messages = messages;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger first run by 5 minutes so startup noise settles
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = await _settings.GetAsync();
                if (cfg.PurgeEnabled && cfg.PurgeAfterDays > 0)
                {
                    var cutoff  = DateTime.UtcNow.AddDays(-cfg.PurgeAfterDays);
                    var deleted = await _messages.DeleteOlderThanAsync(cutoff);
                    if (deleted > 0)
                        _logger.LogInformation(
                            "Auto-purge: deleted {Count} messages older than {Days} days.",
                            deleted, cfg.PurgeAfterDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MessagePurgeService encountered an error.");
            }

            // Check once per hour
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
