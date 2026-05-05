using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Shop
{
    public enum OrderStatus
    {
        Pending = 0,
        Paid = 1,
        Failed = 2,
        Refunded = 3,
        Reserved = 4,
        Cancelled = 5
    }

    public class Order
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [MaxLength(200)]
        public string? VippsOrderId { get; set; }

        [Required]
        public OrderStatus Status { get; set; } = OrderStatus.Pending;

        [Required]
        public int TotalInOre { get; set; }

        [MaxLength(500)]
        public string? ShippingAddress { get; set; }

        public DateTime? ShippedAt { get; set; }

        public bool IsTest { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public List<OrderItem> OrderItems { get; set; } = [];

        public Invoice? Invoice { get; set; }
    }
}
