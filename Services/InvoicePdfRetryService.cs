namespace garge_api.Services
{
    /// <summary>Makes invoice PDFs that failed, so every sale ends up with its invoice.</summary>
    public class InvoicePdfRetryService(IServiceScopeFactory scopeFactory, ILogger<InvoicePdfRetryService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var made = await scope.ServiceProvider.GetRequiredService<IInvoiceService>().RetryMissingPdfsAsync(stoppingToken);
                    if (made > 0) logger.LogInformation("Made {Count} invoice PDFs that had failed", made);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Invoice PDF retry failed");
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
