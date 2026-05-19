namespace garge_api.Helpers
{
    public static class ElectricityPriceHelper
    {
        public static decimal GetVatRate(string? area) => area?.ToUpperInvariant() switch
        {
            "NO1" or "NO2" or "NO3" or "NO5" => 0.25m,
            "NO4" => 0m,
            _ => 0m,
        };

        public static decimal ToGross(decimal spot, string? area) =>
            spot * (1m + GetVatRate(area));
    }
}
