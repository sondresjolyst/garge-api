using System.ComponentModel.DataAnnotations;
using garge_api.Models.Subscription;

namespace garge_api.Dtos.Subscription
{
    public class ProductResponseDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int PriceInOre { get; set; }
        public string Interval { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateProductDto
    {
        [Required]
        [MaxLength(45)]
        public required string Name { get; set; }

        [MaxLength(2000)]
        public string? Description { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        public int PriceInOre { get; set; }

        [Required]
        public BillingInterval Interval { get; set; }

        [Required]
        public ProductType Type { get; set; } = ProductType.Primary;
    }

    public class UpdateProductDto
    {
        [Required]
        [MaxLength(45)]
        public required string Name { get; set; }

        [MaxLength(2000)]
        public string? Description { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        public int PriceInOre { get; set; }

        [Required]
        public BillingInterval Interval { get; set; }

        [Required]
        public ProductType Type { get; set; }

        public bool IsActive { get; set; }
    }
    public class SchedulePriceChangeDto
    {
        [Required]
        [Range(1, int.MaxValue)]
        public int NewPriceInOre { get; set; }

        /// <summary>The first charge due date, a UTC date at least 30 days out, that pays the new price.</summary>
        [Required]
        public DateTime EffectiveFrom { get; set; }
    }

    public class PriceChangeResponseDto
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public int OldPriceInOre { get; set; }
        public int NewPriceInOre { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime? AppliedAt { get; set; }
        /// <summary>Subscribers due a notice.</summary>
        public int NoticeCount { get; set; }
        /// <summary>Subscribers whose notice email was sent.</summary>
        public int NotifiedCount { get; set; }
        /// <summary>Subscribers now paying the new price.</summary>
        public int AppliedCount { get; set; }
    }
}
