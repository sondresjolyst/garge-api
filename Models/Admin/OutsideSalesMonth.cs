using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Admin
{
    /// <summary>
    /// Sales made outside garge-api in one month, entered by an admin. They count toward the VAT
    /// registration threshold together with garge's own sales. Amounts are øre.
    /// </summary>
    public class OutsideSalesMonth
    {
        /// <summary>The first day of the month, in Norwegian time.</summary>
        [Key]
        public DateOnly Month { get; set; }

        public long AmountInOre { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
