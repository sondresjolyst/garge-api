namespace garge_api.Dtos.Electricity
{
    public class CurrentPriceDto
    {
        public string Area { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public decimal SpotValue { get; set; }
        public decimal VatRate { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime DeliveryStart { get; set; }
        public DateTime DeliveryEnd { get; set; }
    }
}
