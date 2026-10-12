using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Subscription
{
    /// <summary>
    /// A scheduled increase of a product's price. Each subscriber gets a notice, and pays the new price
    /// from the later of <see cref="EffectiveFrom"/> and 30 days after their notice was sent.
    /// </summary>
    public class PriceChange
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public int ProductId { get; set; }
        [ForeignKey(nameof(ProductId))]
        public Product? Product { get; set; }

        /// <summary>The price before the change, as subscribers agreed to it.</summary>
        public int OldPriceInOre { get; set; }
        public int NewPriceInOre { get; set; }

        /// <summary>The first charge due date, in UTC, that pays the new price.</summary>
        public DateTime EffectiveFrom { get; set; }

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set when an admin calls the change off before it took effect.</summary>
        public DateTime? CancelledAt { get; set; }
        /// <summary>Set when the product's price became the new price, for new subscribers.</summary>
        public DateTime? AppliedAt { get; set; }

        public List<PriceChangeNotice> Notices { get; set; } = [];
    }

    /// <summary>One subscriber's notice of a <see cref="PriceChange"/>, the proof they were told in time.</summary>
    public class PriceChangeNotice
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public int PriceChangeId { get; set; }
        [ForeignKey(nameof(PriceChangeId))]
        public PriceChange? PriceChange { get; set; }

        public int SubscriptionId { get; set; }
        [ForeignKey(nameof(SubscriptionId))]
        public Subscription? Subscription { get; set; }

        /// <summary>When the notice email was sent. Null until it is.</summary>
        public DateTime? NotifiedAt { get; set; }

        /// <summary>The first charge due date, in UTC, that pays the new price for this subscriber.</summary>
        public DateTime? EffectiveFrom { get; set; }

        /// <summary>Set when the subscription's agreed price became the new price.</summary>
        public DateTime? AppliedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
