namespace garge_api.Models.Electricity
{
    public class PriceResponse
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateTime Updated { get; set; }
        public required string Currency { get; set; }
        public required Dictionary<string, AreaPrices> Areas { get; set; }
    }
}
