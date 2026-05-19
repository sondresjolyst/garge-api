namespace garge_api.Dtos.Electricity
{
    public class AreaPricesDto
    {
        public List<PriceEntryDto> Values { get; set; } = new();
        public decimal VatRate { get; set; }
    }
}
