using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace garge_api.Dtos.Admin
{
    public class VatThresholdDto
    {
        public long TurnoverInOre { get; set; }
        public long ThresholdInOre { get; set; }
        public bool VatEnabled { get; set; }
        public int? CrossingInvoiceId { get; set; }
        public DateTime? CrossedAt { get; set; }
        public long OwedVatInOre { get; set; }
        public List<VatOwedSaleDto> Owed { get; set; } = [];

        /// <summary>The months the 12 months up to today overlap, oldest first.</summary>
        public List<VatMonthDto> Months { get; set; } = [];
    }

    /// <summary>
    /// One month of the 12 months up to today. <c>SalesInOre</c> holds garge's sales in the month and inside
    /// the period, less refunds. <c>OutsideInOre</c> holds the sales entered for the month outside garge-api.
    /// </summary>
    public class VatMonthDto
    {
        /// <summary>The month, as yyyy-MM.</summary>
        public required string Month { get; set; }
        public long SalesInOre { get; set; }
        public long OutsideInOre { get; set; }
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

    public class UpdateOutsideSalesDto : IValidatableObject
    {
        /// <summary>The month, as yyyy-MM.</summary>
        [Required]
        public required string Month { get; set; }

        /// <summary>Sales outside garge-api that month, in øre, up to 10 000 000 kr. Zero removes the month.</summary>
        [Range(0, 1_000_000_000L)]
        public long AmountInOre { get; set; }

        /// <summary>The first day of <paramref name="value"/> when it is a yyyy-MM month from 2000 on.</summary>
        public static bool TryParseMonth(string? value, out DateOnly month)
        {
            month = default;
            if (!DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || parsed.Year < 2000)
                return false;
            month = parsed;
            return true;
        }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Month != null && !TryParseMonth(Month, out _))
                yield return new ValidationResult("Use a month as yyyy-MM.", [nameof(Month)]);
        }
    }
}
