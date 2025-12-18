// This service is no longer used - automation processing is handled by the operator
// The operator polls the API and processes automation rules via MQTT
// Keeping file for reference but commented out

/*
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;

namespace garge_api.Services
{
    public class SensorNotificationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SensorNotificationService> _logger;
        private readonly string _connectionString;

        public SensorNotificationService(IServiceScopeFactory scopeFactory, ILogger<SensorNotificationService> logger, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            _logger.LogInformation("SensorNotificationService initialized");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(stoppingToken);

            connection.Notification += async (sender, args) =>
            {
                try
                {
                    _logger.LogInformation("Sensor data notification received: {Payload}", args.Payload);

                    var sensorData = JsonConvert.DeserializeObject<SensorData>(args.Payload);
                    if (sensorData == null)
                    {
                        _logger.LogWarning("Deserialized SensorData is null.");
                        return;
                    }

                    // Process automation rules for this sensor data
                    using var scope = _scopeFactory.CreateScope();
                    var automationService = scope.ServiceProvider.GetRequiredService<IAutomationProcessingService>();
                    await automationService.ProcessSensorDataAsync(sensorData);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Failed to deserialize sensor notification payload: {Payload}. Error: {Message}", args.Payload, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing sensor notification: {Message}", ex.Message);
                }
            };

            await using (var command = new NpgsqlCommand("LISTEN sensordata_channel;", connection))
            {
                await command.ExecuteNonQueryAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await connection.WaitAsync(stoppingToken);
            }
        }
    }
}
*/
