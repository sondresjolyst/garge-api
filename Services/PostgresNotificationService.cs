using garge_api.Hubs;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;

namespace garge_api.Services
{
    public class PostgresNotificationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PostgresNotificationService> _logger;
        private readonly string _connectionString;
        private readonly CoalescingDispatcher _dispatcher;
        private readonly IDeviceOwnershipService _ownership;

        public PostgresNotificationService(
            IServiceScopeFactory scopeFactory,
            ILogger<PostgresNotificationService> logger,
            IConfiguration configuration,
            CoalescingDispatcher dispatcher,
            IDeviceOwnershipService ownership)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            _dispatcher = dispatcher;
            _ownership = ownership;
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
                    if (args.Channel == "switchdata_channel")
                    {
                        await HandleSwitchNotificationAsync(args.Payload, stoppingToken);
                    }
                    else if (args.Channel == "sensordata_channel")
                    {
                        await HandleSensorNotificationAsync(args.Payload, stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug("Ignoring notification on unknown channel {Channel}", args.Channel);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize notification payload from {Channel}", args.Channel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing notification from {Channel}", args.Channel);
                }
            };

            await using (var command = new NpgsqlCommand("LISTEN switchdata_channel; LISTEN sensordata_channel;", connection))
            {
                await command.ExecuteNonQueryAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await connection.WaitAsync(stoppingToken);
            }
        }

        private async Task HandleSwitchNotificationAsync(string payload, CancellationToken ct)
        {
            var switchData = JsonConvert.DeserializeObject<SwitchData>(payload);
            if (switchData == null)
            {
                _logger.LogWarning("Deserialized SwitchData is null.");
                return;
            }

            _logger.LogDebug("Notification received for switch {SwitchId}", switchData.SwitchId);

            // Project to a wire-safe DTO. Never let EF entities (with RegistrationCode etc.) reach the hub.
            SwitchSummaryDto? summary = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                summary = await context.Switches
                    .AsNoTracking()
                    .Where(s => s.Id == switchData.SwitchId)
                    .Select(s => new SwitchSummaryDto(s.Id, s.Name, s.Type))
                    .FirstOrDefaultAsync(ct);
            }

            if (summary == null)
            {
                _logger.LogWarning("Switch with ID {SwitchId} not found.", switchData.SwitchId);
            }

            var evt = new SwitchEventDto(switchData.Id, switchData.SwitchId, switchData.Value, switchData.Timestamp, summary);

            // Fan out to bridges (e.g., garge-operator publishes MQTT /set) and to each owning user.
            _dispatcher.EnqueueSwitchForBridges(evt);

            var owners = await _ownership.ListSwitchOwnersAsync(switchData.SwitchId, ct);
            foreach (var userId in owners)
            {
                _dispatcher.EnqueueSwitchForUser(userId, evt);
            }
        }

        private async Task HandleSensorNotificationAsync(string payload, CancellationToken ct)
        {
            var sensorData = JsonConvert.DeserializeObject<SensorData>(payload);
            if (sensorData == null)
            {
                _logger.LogWarning("Deserialized SensorData is null.");
                return;
            }

            _logger.LogDebug("Notification received for sensor {SensorId}", sensorData.SensorId);

            SensorSummaryDto? summary = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                summary = await context.Sensors
                    .AsNoTracking()
                    .Where(s => s.Id == sensorData.SensorId)
                    .Select(s => new SensorSummaryDto(s.Id, s.Name, s.Type))
                    .FirstOrDefaultAsync(ct);
            }

            if (summary == null)
            {
                _logger.LogWarning("Sensor with ID {SensorId} not found.", sensorData.SensorId);
            }

            var evt = new SensorEventDto(sensorData.Id, sensorData.SensorId, sensorData.Value, sensorData.Timestamp, summary);

            var owners = await _ownership.ListSensorOwnersAsync(sensorData.SensorId, ct);
            foreach (var userId in owners)
            {
                _dispatcher.EnqueueSensorForUser(userId, evt);
            }
        }
    }
}
