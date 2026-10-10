using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Admin
{
    public class VatThresholdDto
    {
        public long TurnoverInOre { get; set; }
        public long OtherTurnoverInOre { get; set; }
        public long ThresholdInOre { get; set; }
        public bool VatEnabled { get; set; }
        public int? CrossingInvoiceId { get; set; }
        public DateTime? CrossedAt { get; set; }
        public long OwedVatInOre { get; set; }
        public List<VatOwedSaleDto> Owed { get; set; } = [];
    }

    public class VatOwedSaleDto
    {
        public int InvoiceId { get; set; }
        public DateTime IssuedAt { get; set; }
        public int AmountInOre { get; set; }
        public int VatInOre { get; set; }
        public DateTime? CorrectedAt { get; set; }
        public int? CreditNoteId { get; set; }
        public int? ReplacementInvoiceId { get; set; }
    }

    public class UpdateOtherTurnoverDto
    {
        /// <summary>Sales outside garge-api in the last 12 months, in øre.</summary>
        [Range(0, 100_000_000_00L)]
        public long OtherTurnoverInOre { get; set; }
    }
}
