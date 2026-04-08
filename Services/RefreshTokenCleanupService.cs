using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public class RefreshTokenCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RefreshTokenCleanupService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

        public RefreshTokenCleanupService(IServiceScopeFactory scopeFactory, ILogger<RefreshTokenCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CleanupAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }

        private async Task CleanupAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var deleted = await context.RefreshTokens
                    .Where(t => t.Revoked != null || t.Expires < DateTime.UtcNow)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("RefreshTokenCleanup: deleted {Count} expired or revoked tokens.", deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshTokenCleanup: error during cleanup.");
            }
        }
    }
}
