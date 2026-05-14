using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using garge_api.Models.Admin;
using garge_api.Models.Common;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Models.Shop
{
    public class ShopItemPhoto : IPhotoEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [SwaggerSchema(ReadOnly = true)]
        public int Id { get; set; }

        [Required]
        public int ShopItemId { get; set; }

        [Required]
        public required string UserId { get; set; }

        [Required]
        public required string Data { get; set; }

        [Required]
        [MaxLength(50)]
        public required string ContentType { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(ShopItemId))]
        public ShopItem? ShopItem { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }
    }
}
