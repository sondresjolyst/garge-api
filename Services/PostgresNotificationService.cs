using garge_api.Models;
using Newtonsoft.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;

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
        Console.WriteLine("PostgresNotificationService initialized");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        connection.Notification += async (sender, args) =>
        {
            try
            {
                Console.WriteLine($"Notification received: {args.Payload}");

                var switchData = JsonConvert.DeserializeObject<SwitchData>(args.Payload);
                if (switchData == null)
                {
                    Console.WriteLine("Deserialized SwitchData is null.");
                    return;
                }

                // Retrieve the related Switch entity
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                switchData.Switch = await context.Switches.FirstOrDefaultAsync(s => s.Id == switchData.SwitchId);

                if (switchData.Switch == null)
                {
                    Console.WriteLine($"Switch with ID {switchData.SwitchId} not found.");
                }

                await NotifySubscribersAsync(switchData);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Failed to deserialize notification payload: {args.Payload}. Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing notification: {ex.Message}");
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
            await webhookService.NotifyClientAsync(subscription.WebhookUrl, switchData);
        }
    }
}
