namespace garge_api.Dtos.Electricity
{
    public class PriceResponseDto
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateTime Updated { get; set; }
        public string Currency { get; set; } = string.Empty;
        public Dictionary<string, AreaPricesDto> Areas { get; set; } = new();
    }
}
