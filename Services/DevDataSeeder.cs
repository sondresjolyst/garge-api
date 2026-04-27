using System.Globalization;
using garge_api.Models;
using garge_api.Models.Sensor;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Seeds dummy data for local development so the app has something to render
    /// before any real devices have been registered.
    /// </summary>
    public static class DevDataSeeder
    {
        // Each entry becomes one motorcycle voltmeter sensor.
        // The Name format follows the existing convention <prefix>_<code>_<type>
        // so GenerateDefaultName/GenerateParentName work the same as for real sensors.
        private static readonly (string Code, string DisplayName, double IdleVolts)[] DummyBikes =
        {
            ("MC1A2B", "MT-07",          12.6),
            ("MC3C4D", "Tenere 700",     12.4),
            ("MC5E6F", "DR-Z 400",       12.8),
        };

        public static async Task SeedAsync(
            ApplicationDbContext context,
            ILogger logger,
            CancellationToken ct = default)
        {
            // Only seed when there are no sensors at all — never overwrite real data.
            var anySensors = await context.Sensors.AnyAsync(ct);
            if (anySensors)
            {
                logger.LogInformation("DevDataSeeder skipped: sensors already exist.");
                await EnsureAccessForAllUsersAsync(context, logger, ct);
                return;
            }

            logger.LogInformation("DevDataSeeder: inserting {Count} dummy motorcycle voltmeters.", DummyBikes.Length);

            var random = new Random(42);
            var now = DateTime.UtcNow;

            foreach (var (code, displayName, idle) in DummyBikes)
            {
                // Name pattern matches CreateSensor's expectations: <prefix>_<code>_<type>
                var name = $"mc_{code}_voltage";
                var sensor = new Sensor
                {
                    Name = name,
                    Type = "voltage",
                    Role = name,
                    RegistrationCode = $"DEV{code}", // stable + obvious in logs
                    DefaultName = $"Garge {code} voltage",
                    ParentName = $"mc_{code}"
                };
                context.Sensors.Add(sensor);
                await context.SaveChangesAsync(ct); // need the generated Id for the data + custom name

                // Set a friendly per-sensor display name as a *global* default. Custom names are per-user
                // (UserSensorCustomNames), so we instead just rely on DefaultName above and let the user
                // rename it inside the app. We do, however, give the data a realistic shape:
                var data = new List<SensorData>();
                // 24 hours of readings, one every ~10 minutes, with a slow daily droop + small noise
                for (var i = 0; i < 24 * 6; i++)
                {
                    var ts = now.AddMinutes(-i * 10);
                    // Idle sag: drop ~0.4V over 24h with sinusoidal jitter ±0.05V
                    var droop = (i / (24.0 * 6.0)) * 0.4;
                    var jitter = (random.NextDouble() - 0.5) * 0.1;
                    var v = idle - droop + jitter;
                    data.Add(new SensorData
                    {
                        SensorId = sensor.Id,
                        Value = Math.Round(v, 3).ToString(CultureInfo.InvariantCulture),
                        Timestamp = ts
                    });
                }
                context.SensorData.AddRange(data);
                logger.LogInformation("DevDataSeeder seeded {Display} ({Name}, code={Code})", displayName, name, sensor.RegistrationCode);
            }

            await context.SaveChangesAsync(ct);
            await EnsureAccessForAllUsersAsync(context, logger, ct);
            logger.LogInformation("DevDataSeeder finished.");
        }

        /// <summary>
        /// Idempotently grants every existing user access to every dev-seeded sensor.
        /// Runs every startup so a freshly-registered dev user gets the dummy sensors after a restart.
        /// </summary>
        private static async Task EnsureAccessForAllUsersAsync(
            ApplicationDbContext context,
            ILogger logger,
            CancellationToken ct)
        {
            var devSensorIds = await context.Sensors
                .Where(s => s.RegistrationCode.StartsWith("DEV"))
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (devSensorIds.Count == 0) return;

            var userIds = await context.Users.Select(u => u.Id).ToListAsync(ct);
            if (userIds.Count == 0)
            {
                logger.LogInformation("DevDataSeeder: no users yet — register a user, then restart the API to gain access.");
                return;
            }

            var existing = await context.UserSensors
                .Where(us => devSensorIds.Contains(us.SensorId))
                .Select(us => new { us.UserId, us.SensorId })
                .ToListAsync(ct);
            var existingSet = existing.Select(x => (x.UserId, x.SensorId)).ToHashSet();

            var toAdd = new List<UserSensor>();
            foreach (var userId in userIds)
            {
                foreach (var sensorId in devSensorIds)
                {
                    if (!existingSet.Contains((userId, sensorId)))
                    {
                        toAdd.Add(new UserSensor { UserId = userId, SensorId = sensorId });
                    }
                }
            }

            if (toAdd.Count > 0)
            {
                context.UserSensors.AddRange(toAdd);
                await context.SaveChangesAsync(ct);
                logger.LogInformation("DevDataSeeder granted {Count} new UserSensor mappings.", toAdd.Count);
            }
        }
    }
}
