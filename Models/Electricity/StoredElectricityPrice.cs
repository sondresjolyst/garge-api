namespace garge_api.Models.Electricity
{
    public class StoredElectricityPrice
    {
        public int Id { get; set; }
        public required string Area { get; set; }
        /// <summary>Resolution of the price entry: HOURLY, DAILY, or MONTHLY.</summary>
        public required string Resolution { get; set; }
        public DateTime DeliveryStart { get; set; }
        public DateTime DeliveryEnd { get; set; }
        /// <summary>Price in kr/kWh (NordPool value divided by 1000).</summary>
        public double Value { get; set; }
        public required string Currency { get; set; }
        public DateTime FetchedAt { get; set; }
    }
}
