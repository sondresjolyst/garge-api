using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Shop
{
    public class OrderItem
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public Order? Order { get; set; }

        [Required]
        public int ShopItemId { get; set; }

        [ForeignKey(nameof(ShopItemId))]
        public ShopItem? ShopItem { get; set; }

        [Required]
        public int Quantity { get; set; }

        [Required]
        public int PriceAtPurchaseInOre { get; set; }

        [Required]
        public int UnitPriceExclVatInOre { get; set; }

        [Required]
        public int VatPercentage { get; set; }
    }
}
