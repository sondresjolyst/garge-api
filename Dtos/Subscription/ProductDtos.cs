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
        [MaxLength(100)]
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
        [MaxLength(100)]
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
}
