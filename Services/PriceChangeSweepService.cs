namespace garge_api.Services
{
    /// <summary>
    /// Runs <see cref="IPriceChangeService.SweepAsync"/> every hour, and soon after an admin schedules a
    /// change. One sweep runs at a time, so a subscriber gets one notice.
    /// </summary>
    public partial class PriceChangeSweepService(IServiceScopeFactory scopeFactory, ILogger<PriceChangeSweepService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
        private readonly SemaphoreSlim _trigger = new(0, 1);

        /// <summary>Starts a sweep now instead of at the next hour.</summary>
        public void Trigger()
        {
            try { _trigger.Release(); }
            catch (SemaphoreFullException) { }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IPriceChangeService>().SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    LogSweepFailed(logger, ex);
                }

                try { await _trigger.WaitAsync(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        public override void Dispose()
        {
            _trigger.Dispose();
            base.Dispose();
            GC.SuppressFinalize(this);
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "Price change sweep failed")]
        private static partial void LogSweepFailed(ILogger logger, Exception ex);
    }
}
