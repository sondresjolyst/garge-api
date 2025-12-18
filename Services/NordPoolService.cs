using System.Globalization;
using Newtonsoft.Json;
using garge_api.Models.Electricity;

namespace garge_api.Services
{
    public class NordPoolService
    {
        private const string ApiUrl = "https://dataportal-api.nordpoolgroup.com/api";
        private readonly HttpClient _httpClient;
        private readonly ILogger<NordPoolService> _logger;

        public NordPoolService(HttpClient httpClient, ILogger<NordPoolService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<PriceResponse?> FetchPricesAsync(string dataType, DateTime? endDate = null, List<string>? areas = null, string currency = "NOK")
        {
            areas ??= new List<string>();
            var (apiUrl, parameters) = GetUrlParams(dataType, endDate, areas, currency);
            var response = await _httpClient.GetAsync($"{apiUrl}?{parameters}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();

            var json = JsonConvert.DeserializeObject<dynamic>(content);
            if (json == null)
            {
                _logger.LogError("Failed to deserialize JSON response from Nord Pool API.");
                return null;
            }

            var priceResponse = new PriceResponse
            {
                Start = DateTime.MinValue,
                End = DateTime.MinValue,
                Updated = DateTime.MinValue,
                Currency = json.currency != null ? (string)json.currency : "NOK",
                Areas = new Dictionary<string, AreaPrices>()
            };

            ParsePriceEntries(json, dataType, priceResponse);

            return priceResponse;
        }

        /// <summary>
        /// Gets the current electricity price for a specific area.
        /// </summary>
        /// <param name="area">The area code (e.g., "NO2")</param>
        /// <param name="currency">The currency (default: NOK)</param>
        /// <returns>The current electricity price in the specified currency</returns>
        public async Task<decimal?> GetCurrentPriceAsync(string area = "NO2", string currency = "NOK")
        {
            try
            {
                // Get today's hourly prices
                var priceData = await FetchPricesAsync("HOURLY", DateTime.UtcNow, new List<string> { area }, currency);
                
                if (priceData?.Areas?.ContainsKey(area) != true)
                {
                    _logger.LogWarning("No price data found for area {Area}", area);
                    return null;
                }

                var now = DateTime.UtcNow;
                var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

                // Find the price entry for the current hour
                var currentPriceEntry = priceData.Areas[area].Values
                    .FirstOrDefault(p => p.Start <= currentHour && p.End > currentHour);

                if (currentPriceEntry != null)
                {
                    _logger.LogInformation("Current electricity price for {Area}: {Price} {Currency}", area, currentPriceEntry.Value, currency);
                    return currentPriceEntry.Value;
                }

                // If no exact match, get the most recent price
                var latestPrice = priceData.Areas[area].Values
                    .Where(p => p.Start <= now)
                    .OrderByDescending(p => p.Start)
                    .FirstOrDefault();

                if (latestPrice != null)
                {
                    _logger.LogInformation("Latest electricity price for {Area}: {Price} {Currency}", area, latestPrice.Value, currency);
                    return latestPrice.Value;
                }

                _logger.LogWarning("No current or recent price data found for area {Area}", area);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current electricity price for area {Area}", area);
                return null;
            }
        }

        private void ParsePriceEntries(dynamic json, string dataType, PriceResponse priceResponse)
        {
            string aggregateKey = dataType switch
            {
                "DAILY" => "multiAreaDailyAggregates",
                "WEEKLY" => "multiAreaWeeklyAggregates",
                "MONTHLY" => "multiAreaMonthlyAggregates",
                _ => "multiAreaEntries"
            };

            if (json[aggregateKey] == null)
            {
                _logger.LogWarning($"Aggregate key '{aggregateKey}' not found in JSON response.");
                return;
            }

            var entries = json[aggregateKey];

            foreach (var entry in entries)
            {
                DateTime start = entry.deliveryStart != null ? DateTime.Parse((string)entry.deliveryStart) : DateTime.MinValue;
                DateTime end = entry.deliveryEnd != null ? DateTime.Parse((string)entry.deliveryEnd) : DateTime.MinValue;

                if (priceResponse.Start == DateTime.MinValue || start < priceResponse.Start)
                {
                    priceResponse.Start = start;
                }

                if (priceResponse.End == DateTime.MinValue || end > priceResponse.End)
                {
                    priceResponse.End = end;
                }

                string areaKey = dataType == "HOURLY" ? "entryPerArea" : "averagePerArea";

                if (entry[areaKey] == null)
                {
                    _logger.LogWarning($"{areaKey} not found in entry.");
                    continue;
                }

                foreach (var area in entry[areaKey])
                {
                    string areaName = area.Name;
                    decimal value = area.Value;

                    if (!priceResponse.Areas.ContainsKey(areaName))
                    {
                        priceResponse.Areas[areaName] = new AreaPrices { Values = new List<PriceEntry>() };
                    }

                    priceResponse.Areas[areaName].Values.Add(new PriceEntry
                    {
                        Start = start,
                        End = end,
                        Value = value
                    });
                }
            }

            if (json.updatedAt != null)
            {
                priceResponse.Updated = DateTime.Parse((string)json.updatedAt);
            }
            else
            {
                _logger.LogWarning("updatedAt not found in JSON response.");
            }
        }

        private (string, string) GetUrlParams(string dataType, DateTime? endDate, List<string> areas, string currency)
        {
            endDate ??= DateTime.UtcNow.AddDays(1);
            areas ??= new List<string> { "DK1", "DK2", "FI", "NO1", "NO2", "NO3", "NO4", "SE1", "SE2", "SE3", "SE4", "EE", "LT", "LV", "AT", "BE", "FR", "GER", "NL", "PL", "SYS" };

            string endpoint = dataType switch
            {
                "DAILY" => "AggregatePrices",
                "WEEKLY" => "AggregatePrices",
                "MONTHLY" => "AggregatePrices",
                _ => "DayAheadPrices"
            };

            string apiUrl = $"{ApiUrl}/{endpoint}";
            string parameters = $"currency={currency}&market=DayAhead&deliveryArea={string.Join(",", areas)}";

            if (dataType == "HOURLY")
            {
                parameters += $"&date={endDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            }
            else if (dataType is "DAILY" or "WEEKLY" or "MONTHLY")
            {
                parameters += $"&year={endDate.Value.Year}";
            }

            return (apiUrl, parameters);
        }
    }
}
