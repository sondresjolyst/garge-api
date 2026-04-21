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
                DateTime start = entry.deliveryStart != null ? ParseDeliveryDate((string)entry.deliveryStart) : DateTime.MinValue;
                DateTime end = entry.deliveryEnd != null ? ParseDeliveryDate((string)entry.deliveryEnd) : DateTime.MinValue;

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
                priceResponse.Updated = DateTime.Parse((string)json.updatedAt, CultureInfo.InvariantCulture);
            }
            else
            {
                _logger.LogWarning("updatedAt not found in JSON response.");
            }
        }

        /// <summary>
        /// Parses a NordPool delivery date string to UTC.
        /// HOURLY entries use full ISO timestamps (e.g. "2026-04-18T22:00:00Z") — parsed as exact UTC.
        /// DAILY/MONTHLY entries may arrive as "MM/dd/yyyy HH:mm:ss" or "yyyy-MM-dd" — always stored
        /// as UTC midnight of that calendar date so results are environment-independent (UTC vs UTC+2).
        /// </summary>
        private static DateTime ParseDeliveryDate(string raw)
        {
            if (raw.Contains('T'))
            {
                // Full datetime string — parse with timezone awareness and convert to UTC
                return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture).UtcDateTime;
            }

            // "MM/dd/yyyy HH:mm:ss" format returned by NordPool
            if (DateTime.TryParseExact(raw, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtSlash))
            {
                return DateTime.SpecifyKind(dtSlash, DateTimeKind.Utc);
            }

            // Date-only string — always store as UTC midnight of that date
            return DateTime.SpecifyKind(
                DateTime.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
        }


        private static (string, string) GetUrlParams(string dataType, DateTime? endDate, List<string> areas, string currency)
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
