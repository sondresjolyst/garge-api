using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Shop
{
    public class ShopItemResponseDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int PriceInOre { get; set; }
        public int StockCount { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateShopItemDto
    {
        [Required]
        [MaxLength(100)]
        public required string Name { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        public int PriceInOre { get; set; }

        public int StockCount { get; set; } = -1;
    }

    public class UpdateShopItemDto
    {
        [Required]
        [MaxLength(100)]
        public required string Name { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        public int PriceInOre { get; set; }

        public int StockCount { get; set; } = -1;

        public bool IsActive { get; set; }
    }

    public class OrderItemRequestDto
    {
        [Required]
        public int ShopItemId { get; set; }

        [Required]
        [Range(1, 100)]
        public int Quantity { get; set; }
    }

    public class CreateOrderDto
    {
        [Required]
        [MinLength(1)]
        public required List<OrderItemRequestDto> Items { get; set; }

        [Required]
        public required string PhoneNumber { get; set; }

        [Required]
        public required string RedirectUrl { get; set; }

        [Required]
        [MaxLength(500)]
        public required string ShippingAddress { get; set; }
    }

    public class OrderItemResponseDto
    {
        public int Id { get; set; }
        public int ShopItemId { get; set; }
        public string ShopItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int PriceAtPurchaseInOre { get; set; }
        public int UnitPriceExclVatInOre { get; set; }
        public int VatPercentage { get; set; }
    }

    public class OrderResponseDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? VippsOrderId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int TotalInOre { get; set; }
        public string? ShippingAddress { get; set; }
        public DateTime? ShippedAt { get; set; }
        public bool HasInvoice { get; set; }
        public bool IsTest { get; set; }
        public List<OrderItemResponseDto> Items { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class InvoiceResponseDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public DateTime IssuedAt { get; set; }
    }

    public class CheckoutResponseDto
    {
        public string RedirectUrl { get; set; } = string.Empty;
        public int OrderId { get; set; }
    }

    public class AdminOrderResponseDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string? VippsOrderId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int TotalInOre { get; set; }
        public string? ShippingAddress { get; set; }
        public DateTime? ShippedAt { get; set; }
        public bool HasInvoice { get; set; }
        public bool IsTest { get; set; }
        public List<OrderItemResponseDto> Items { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class VippsPaymentWebhookDto
    {
        public string Reference { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? Amount { get; set; }
    }
}
