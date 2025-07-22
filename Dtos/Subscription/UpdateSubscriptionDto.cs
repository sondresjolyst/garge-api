using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Subscription
{
    public class UpdateSubscriptionDto
    {
        [Required]
        [MaxLength(50)]
        public required string Name { get; set; }

        [MaxLength(250)]
        public string? Description { get; set; }

        [Required]
        public decimal Price { get; set; }

        [MaxLength(3)]
        public string? Currency { get; set; }

        [Required]
        public int DurationMonths { get; set; }

        [Required]
        public bool IsRecurring { get; set; }
    }
}
