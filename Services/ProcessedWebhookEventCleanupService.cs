using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public class ProcessedWebhookEventCleanupService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ProcessedWebhookEventCleanupService> _logger;

        public ProcessedWebhookEventCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<ProcessedWebhookEventCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var cutoff = DateTime.UtcNow - Retention;
                    var deleted = await db.ProcessedWebhookEvents
                        .Where(e => e.ProcessedAt < cutoff)
                        .ExecuteDeleteAsync(stoppingToken);
                    if (deleted > 0)
                        _logger.LogInformation("Pruned {Count} processed webhook events older than {Cutoff}", deleted, cutoff);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProcessedWebhookEvent cleanup failed");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }
    }
}
