using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Subscription
{
    public enum BillingInterval
    {
        Monthly = 0,
        Yearly = 1
    }

    public enum ProductType
    {
        Primary = 0,
        AddOn = 1
    }

    public class Product
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public required string Name { get; set; }

        [MaxLength(2000)]
        public string? Description { get; set; }

        [Required]
        public int PriceInOre { get; set; }

        [Required]
        public BillingInterval Interval { get; set; }

        [Required]
        public ProductType Type { get; set; } = ProductType.Primary;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
