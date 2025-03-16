using System.Globalization;
using Newtonsoft.Json;
using garge_api.Models;

namespace garge_api.Services
{
    public class NordPoolService
    {
        private const string ApiUrl = "https://dataportal-api.nordpoolgroup.com/api";
        private readonly HttpClient _httpClient;

        public NordPoolService(HttpClient httpClient)
        {
            _httpClient = httpClient;
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
                Console.WriteLine("Failed to deserialize JSON response.");
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
                Console.WriteLine($"Aggregate key '{aggregateKey}' not found in JSON response.");
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
                    Console.WriteLine($"{areaKey} not found in entry.");
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
                Console.WriteLine("updatedAt not found in JSON response.");
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
