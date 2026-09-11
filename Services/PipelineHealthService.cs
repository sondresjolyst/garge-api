using garge_api.Constants;
using garge_api.Models;
using garge_api.Models.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Tracks whether sensor readings can reach the API. garge-operator posts a heartbeat every minute;
    /// a missing heartbeat (> <see cref="SecurityMode.HeartbeatTimeout"/>), an MQTT disconnect, or the API's
    /// own downtime between detector runs is recorded as a <see cref="PipelineGap"/>.
    /// </summary>
    public interface IPipelineHealthService
    {
        Task RecordHeartbeatAsync(bool mqttConnected, DateTime now, CancellationToken ct = default);

        /// <summary>Opens/closes gaps for <paramref name="now"/> and returns gaps that ended within <paramref name="lookback"/> or are still open.</summary>
        Task<List<PipelineGap>> EvaluateAsync(DateTime now, TimeSpan detectorInterval, TimeSpan lookback, CancellationToken ct = default);
    }

    public class PipelineHealthService(ApplicationDbContext db, ILogger<PipelineHealthService> logger) : IPipelineHealthService
    {
        public async Task RecordHeartbeatAsync(bool mqttConnected, DateTime now, CancellationToken ct = default)
        {
            var row = await GetOrCreateAsync(ct);
            row.LastHeartbeatAt = now;
            row.MqttConnected = mqttConnected;
            if (mqttConnected) row.LastHealthyHeartbeatAt = now;
            await db.SaveChangesAsync(ct);
        }

        public async Task<List<PipelineGap>> EvaluateAsync(DateTime now, TimeSpan detectorInterval, TimeSpan lookback, CancellationToken ct = default)
        {
            var row = await GetOrCreateAsync(ct);

            var apiWasDown = row.LastDetectorTickAt is { } lastTick && now - lastTick > detectorInterval + SecurityMode.HeartbeatTimeout;
            if (apiWasDown)
            {
                db.PipelineGaps.Add(new PipelineGap { StartedAt = row.LastDetectorTickAt!.Value, EndedAt = now, Source = SecurityMode.GapSources.Api });
                logger.LogWarning("Pipeline gap recorded for API downtime {@LogData}", new { StartedAt = row.LastDetectorTickAt, EndedAt = now });
            }
            row.LastDetectorTickAt = now;

            var openGap = await db.PipelineGaps.FirstOrDefaultAsync(g => g.EndedAt == null, ct);
            if (row.LastHeartbeatAt != null && !apiWasDown)
            {
                var unhealthy = !row.MqttConnected || now - row.LastHeartbeatAt.Value > SecurityMode.HeartbeatTimeout;
                if (unhealthy && openGap == null)
                {
                    openGap = new PipelineGap
                    {
                        StartedAt = row.LastHealthyHeartbeatAt ?? row.LastHeartbeatAt.Value,
                        Source = SecurityMode.GapSources.Operator,
                    };
                    db.PipelineGaps.Add(openGap);
                    logger.LogWarning("Pipeline gap opened {@LogData}", new { openGap.StartedAt, row.MqttConnected, row.LastHeartbeatAt });
                }
                else if (!unhealthy && openGap != null)
                {
                    openGap.EndedAt = row.LastHealthyHeartbeatAt ?? now;
                    logger.LogInformation("Pipeline gap closed {@LogData}", new { openGap.StartedAt, openGap.EndedAt });
                }
            }

            await db.SaveChangesAsync(ct);

            var since = now - lookback;
            return await db.PipelineGaps
                .Where(g => g.EndedAt == null || g.EndedAt > since)
                .OrderBy(g => g.StartedAt)
                .ToListAsync(ct);
        }

        private async Task<PipelineHeartbeat> GetOrCreateAsync(CancellationToken ct)
        {
            var row = await db.PipelineHeartbeats.FirstOrDefaultAsync(h => h.Id == 1, ct);
            if (row != null) return row;
            row = new PipelineHeartbeat { Id = 1 };
            db.PipelineHeartbeats.Add(row);
            return row;
        }
    }
}
