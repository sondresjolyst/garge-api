namespace garge_api.Dtos.Subscription
{
    public class RefundChargeDto
    {
        /// <summary>How much to refund, in øre. Without it, what is left of the charge is refunded.</summary>
        public int? AmountInOre { get; set; }
    }
}
