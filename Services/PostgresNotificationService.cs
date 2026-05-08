using garge_api.Models;
using Newtonsoft.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using garge_api.Models.Switch;

public class PostgresNotificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PostgresNotificationService> _logger;
    private readonly string _connectionString;

    public PostgresNotificationService(IServiceScopeFactory scopeFactory, ILogger<PostgresNotificationService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _logger.LogWarning("PostgresNotificationService initialized");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        connection.Notification += async (sender, args) =>
        {
            try
            {
                var switchData = JsonConvert.DeserializeObject<SwitchData>(args.Payload);
                if (switchData == null)
                {
                    _logger.LogWarning("Deserialized SwitchData is null.");
                    return;
                }

                _logger.LogDebug("Notification received for switch {SwitchId}", switchData.SwitchId);

                // Retrieve the related Switch entity
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                switchData.Switch = await context.Switches.FirstOrDefaultAsync(s => s.Id == switchData.SwitchId);

                if (switchData.Switch == null)
                {
                    _logger.LogWarning("Switch with ID {SwitchId} not found.", switchData.SwitchId);
                }

                await NotifySubscribersAsync(switchData);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize switchdata_channel notification payload.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing switchdata_channel notification.");
            }
        };

        await using (var command = new NpgsqlCommand("LISTEN switchdata_channel;", connection))
        {
            await command.ExecuteNonQueryAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }
    }

    private async Task NotifySubscribersAsync(SwitchData switchData)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var webhookService = scope.ServiceProvider.GetRequiredService<WebhookNotificationService>();

        var subscriptions = await context.WebhookSubscriptions.ToListAsync();
        foreach (var subscription in subscriptions)
        {
            await webhookService.NotifyClientAsync(subscription.WebhookUrl, switchData, subscription.WebhookSecret);
        }
    }
}
