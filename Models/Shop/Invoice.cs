using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Shop
{
    public class Invoice
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public Order? Order { get; set; }

        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public required byte[] PdfData { get; set; }
    }
}
