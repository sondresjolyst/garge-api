using garge_api.Models;
using garge_api.Models.Electricity;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public class ElectricityPriceFetchService : BackgroundService
    {
        private static readonly string[] Areas = ["NO1", "NO2", "NO3", "NO4", "NO5"];
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ElectricityPriceFetchService> _logger;

        public ElectricityPriceFetchService(
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory,
            ILogger<ElectricityPriceFetchService> logger)
        {
            _scopeFactory = scopeFactory;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await FetchAllOnStartupAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var next14 = new DateTime(now.Year, now.Month, now.Day, 14, 0, 0, DateTimeKind.Utc);
                if (now >= next14)
                    next14 = next14.AddDays(1);

                _logger.LogInformation("ElectricityPriceFetchService: next fetch at {Next14} UTC", next14);

                try { await Task.Delay(next14 - now, stoppingToken); }
                catch (OperationCanceledException) { break; }

                await FetchDailyRefreshAsync(stoppingToken);
            }
        }

        private async Task FetchAllOnStartupAsync(CancellationToken stoppingToken)
        {
            var now = DateTime.UtcNow;
            var prevYear = now.Year - 1;
            var currYear = now.Year;

            var tasks = new List<Task>
            {
                // HOURLY: today + tomorrow
                FetchAndStoreAsync("HOURLY", now, stoppingToken),
                FetchAndStoreAsync("HOURLY", now.AddDays(1), stoppingToken),
                // DAILY: previous + current year
                FetchAndStoreAsync("DAILY", new DateTime(prevYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken),
                FetchAndStoreAsync("DAILY", new DateTime(currYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken),
                // MONTHLY: previous + current year
                FetchAndStoreAsync("MONTHLY", new DateTime(prevYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken),
                FetchAndStoreAsync("MONTHLY", new DateTime(currYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken),
            };

            await Task.WhenAll(tasks);
        }

        private async Task FetchDailyRefreshAsync(CancellationToken stoppingToken)
        {
            var now = DateTime.UtcNow;
            var currYear = now.Year;

            // HOURLY: next day
            await FetchAndStoreAsync("HOURLY", now.AddDays(1), stoppingToken);
            // DAILY + MONTHLY: refresh current year
            await FetchAndStoreAsync("DAILY", new DateTime(currYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken);
            await FetchAndStoreAsync("MONTHLY", new DateTime(currYear, 12, 31, 0, 0, 0, DateTimeKind.Utc), stoppingToken);
        }

        private async Task FetchAndStoreAsync(string resolution, DateTime date, CancellationToken stoppingToken)
        {
            _logger.LogInformation("ElectricityPriceFetchService: fetching {Resolution} prices for {Date:yyyy-MM-dd}", resolution, date);
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                var nordPoolService = new NordPoolService(httpClient,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<NordPoolService>.Instance);

                var priceResponse = await nordPoolService.FetchPricesAsync(resolution, date, Areas.ToList(), "NOK");
                if (priceResponse == null)
                {
                    _logger.LogWarning("ElectricityPriceFetchService: no {Resolution} data returned for {Date:yyyy-MM-dd}", resolution, date);
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var fetchedAt = DateTime.UtcNow;

                // Collect all delivery starts from the response so we can bulk-fetch existing rows
                // in a single query per area instead of one query per entry (N+1).
                foreach (var (area, areaPrices) in priceResponse.Areas)
                {
                    if (!Areas.Contains(area)) continue;

                    var incomingStarts = areaPrices.Values
                        .Select(e => e.Start.ToUniversalTime())
                        .ToHashSet();

                    var existing = await db.StoredElectricityPrices
                        .Where(p => p.Area == area && p.Resolution == resolution && incomingStarts.Contains(p.DeliveryStart))
                        .ToDictionaryAsync(p => p.DeliveryStart, stoppingToken);

                    foreach (var entry in areaPrices.Values)
                    {
                        var deliveryStart = entry.Start.ToUniversalTime();
                        var deliveryEnd = entry.End.ToUniversalTime();
                        var valueKwh = (double)(entry.Value / 1000m);

                        if (existing.TryGetValue(deliveryStart, out var row))
                        {
                            row.Value = valueKwh;
                            row.FetchedAt = fetchedAt;
                        }
                        else
                        {
                            db.StoredElectricityPrices.Add(new StoredElectricityPrice
                            {
                                Area = area,
                                Resolution = resolution,
                                DeliveryStart = deliveryStart,
                                DeliveryEnd = deliveryEnd,
                                Value = valueKwh,
                                Currency = priceResponse.Currency,
                                FetchedAt = fetchedAt,
                            });
                        }
                    }
                }

                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("ElectricityPriceFetchService: stored {Resolution} prices for {Date:yyyy-MM-dd}", resolution, date);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ElectricityPriceFetchService: error fetching {Resolution} prices for {Date:yyyy-MM-dd}", resolution, date);
            }
        }
    }
}
