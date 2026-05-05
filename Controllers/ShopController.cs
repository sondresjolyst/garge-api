using AutoMapper;
using garge_api.Dtos.Shop;
using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/shop")]
    public class ShopController : VippsWebhookControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IVippsService _vipps;
        private readonly IInvoiceService _invoice;
        private readonly IMapper _mapper;
        private readonly ILogger<ShopController> _logger;

        public ShopController(
            ApplicationDbContext context,
            IVippsService vipps,
            IInvoiceService invoice,
            IMapper mapper,
            ILogger<ShopController> logger)
        {
            _context = context;
            _vipps = vipps;
            _invoice = invoice;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>Lists all active shop items.</summary>
        [HttpGet("items")]
        [AllowAnonymous]
        public async Task<IActionResult> GetItems()
        {
            var items = await _context.ShopItems
                .Where(i => i.IsActive)
                .OrderBy(i => i.PriceInOre)
                .ToListAsync();

            return Ok(_mapper.Map<List<ShopItemResponseDto>>(items));
        }

        /// <summary>Gets a shop item by ID.</summary>
        [HttpGet("items/{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetItem(int id)
        {
            var item = await _context.ShopItems.FindAsync(id);
            if (item == null) return NotFound();
            return Ok(_mapper.Map<ShopItemResponseDto>(item));
        }

        /// <summary>Creates a new shop item.</summary>
        [HttpPost("items")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CreateItem([FromBody] CreateShopItemDto dto)
        {
            var item = _mapper.Map<ShopItem>(dto);
            _context.ShopItems.Add(item);
            await _context.SaveChangesAsync();

            _logger.LogInformation("ShopItem {ItemId} created: {Name}", item.Id, item.Name);
            return CreatedAtAction(nameof(GetItem), new { id = item.Id },
                _mapper.Map<ShopItemResponseDto>(item));
        }

        /// <summary>Updates an existing shop item.</summary>
        [HttpPut("items/{id}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> UpdateItem(int id, [FromBody] UpdateShopItemDto dto)
        {
            var item = await _context.ShopItems.FindAsync(id);
            if (item == null) return NotFound();

            _mapper.Map(dto, item);
            await _context.SaveChangesAsync();

            _logger.LogInformation("ShopItem {ItemId} updated", id);
            return Ok(_mapper.Map<ShopItemResponseDto>(item));
        }

        /// <summary>Soft-deletes a shop item (sets IsActive = false).</summary>
        [HttpDelete("items/{id}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> DeleteItem(int id)
        {
            var item = await _context.ShopItems.FindAsync(id);
            if (item == null) return NotFound();

            item.IsActive = false;
            await _context.SaveChangesAsync();

            _logger.LogInformation("ShopItem {ItemId} deactivated", id);
            return NoContent();
        }

        /// <summary>Creates a Vipps payment for a sensor purchase.</summary>
        [HttpPost("checkout")]
        [Authorize]
        public async Task<IActionResult> Checkout([FromBody] CreateOrderDto dto)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var shopItemIds = dto.Items.Select(i => i.ShopItemId).Distinct().ToList();
            var shopItems = await _context.ShopItems
                .Where(i => shopItemIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id);

            foreach (var line in dto.Items)
            {
                if (!shopItems.TryGetValue(line.ShopItemId, out var shopItem) || !shopItem.IsActive)
                    return BadRequest($"ShopItem {line.ShopItemId} not found or inactive.");

                if (shopItem.StockCount != -1 && shopItem.StockCount < line.Quantity)
                    return BadRequest($"Insufficient stock for {shopItem.Name}.");
            }

            var settings = await _context.AppSettings.FindAsync(1);
            var vatMultiplier = (settings?.VatEnabled ?? false) ? 1.25 : 1.0;

            var taxPct = (settings?.VatEnabled ?? false) ? 25 : 0;
            var itemPrices = dto.Items.ToDictionary(
                line => line.ShopItemId,
                line => (int)(shopItems[line.ShopItemId].PriceInOre * vatMultiplier));
            var total = dto.Items.Sum(line => itemPrices[line.ShopItemId] * line.Quantity);

            var order = new Order
            {
                UserId = userId,
                TotalInOre = total,
                Status = OrderStatus.Pending,
                ShippingAddress = dto.ShippingAddress,
                IsTest = settings?.VippsTestMode ?? false
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var orderItems = dto.Items.Select(line => new OrderItem
            {
                OrderId = order.Id,
                ShopItemId = line.ShopItemId,
                Quantity = line.Quantity,
                PriceAtPurchaseInOre = itemPrices[line.ShopItemId],
                UnitPriceExclVatInOre = shopItems[line.ShopItemId].PriceInOre,
                VatPercentage = taxPct
            }).ToList();

            var receiptLines = dto.Items.Select(line => new VippsOrderLine
            {
                Name = shopItems[line.ShopItemId].Name,
                Id = line.ShopItemId.ToString(),
                UnitPriceInOre = itemPrices[line.ShopItemId],
                UnitPriceExclVatInOre = shopItems[line.ShopItemId].PriceInOre,
                Quantity = line.Quantity,
                TaxPercentage = taxPct
            }).ToList();

            var vippsResponse = await _vipps.CreatePaymentAsync(order, receiptLines, dto.RedirectUrl, dto.PhoneNumber);

            order.VippsOrderId = vippsResponse.Reference;
            _context.OrderItems.AddRange(orderItems);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} created for user {UserId}, total {Total}øre",
                order.Id, userId, total);

            return Ok(new CheckoutResponseDto
            {
                OrderId = order.Id,
                RedirectUrl = vippsResponse.RedirectUrl
            });
        }

        /// <summary>Returns the current user's order history.</summary>
        [HttpGet("orders/my")]
        [Authorize]
        public async Task<IActionResult> GetMyOrders()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var orders = await _context.Orders
                .Include(o => o.OrderItems).ThenInclude(oi => oi.ShopItem)
                .Include(o => o.Invoice)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return Ok(_mapper.Map<List<OrderResponseDto>>(orders));
        }

        /// <summary>Returns all orders for admin review.</summary>
        [HttpGet("orders")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> GetOrders()
        {
            var orders = await _context.Orders
                .Include(o => o.OrderItems).ThenInclude(oi => oi.ShopItem)
                .Include(o => o.User)
                .Include(o => o.Invoice)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return Ok(_mapper.Map<List<AdminOrderResponseDto>>(orders));
        }

        /// <summary>Captures a reserved Vipps payment after shipping and generates invoice.</summary>
        [HttpPost("orders/{id}/capture")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CaptureOrder(int id)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null) return NotFound();
            if (order.Status != OrderStatus.Reserved)
                return BadRequest("Order is not in Reserved state.");
            if (string.IsNullOrEmpty(order.VippsOrderId))
                return BadRequest("No Vipps reference found.");

            await _vipps.CapturePaymentAsync(order.VippsOrderId, order.TotalInOre);

            order.Status = OrderStatus.Paid;
            order.ShippedAt = DateTime.UtcNow;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            try { await _invoice.GenerateAndStoreAsync(id); }
            catch (Exception ex) { _logger.LogError(ex, "Invoice generation failed for order {OrderId}", id); }

            _logger.LogInformation("Order {OrderId} captured by admin", id);
            return Ok();
        }

        /// <summary>Returns PDF invoice for an order (admin or order owner).</summary>
        [HttpGet("orders/{id}/invoice")]
        [Authorize]
        public async Task<IActionResult> GetInvoicePdf(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var isAdmin = User.IsInRole("Admin");

            var invoice = await _context.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.OrderId == id);

            if (invoice == null) return NotFound("Invoice not yet available.");
            if (!isAdmin && invoice.Order?.UserId != userId) return Forbid();

            return File(invoice.PdfData, "application/pdf",
                $"invoice-{invoice.Id:D4}.pdf");
        }

        /// <summary>Cancels a reserved Vipps payment.</summary>
        [HttpPost("orders/{id}/cancel")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CancelOrder(int id)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null) return NotFound();
            if (order.Status != OrderStatus.Reserved)
                return BadRequest("Order is not in Reserved state.");
            if (string.IsNullOrEmpty(order.VippsOrderId))
                return BadRequest("No Vipps reference found.");

            await _vipps.CancelPaymentAsync(order.VippsOrderId);

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} cancelled by admin", id);
            return Ok();
        }

        /// <summary>Webhook endpoint for Vipps ePayment status changes.</summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            var rawBody = await ReadRawBodyAsync(Request);

            var signature = Request.Headers["X-Vipps-Signature"].FirstOrDefault() ?? string.Empty;
            var settings = await _context.AppSettings.FindAsync(1);
            var secret = settings?.VippsShopWebhookSecret ?? string.Empty;
            if (!_vipps.VerifyWebhookSignature(rawBody, signature, secret))
            {
                _logger.LogWarning("Shop webhook HMAC verification failed");
                return Unauthorized();
            }

            VippsPaymentWebhookDto? payload;
            try
            {
                payload = JsonSerializer.Deserialize<VippsPaymentWebhookDto>(rawBody, JsonOpts);
            }
            catch
            {
                return BadRequest();
            }

            if (payload == null) return BadRequest();

            if (!int.TryParse(payload.Reference, out var orderId))
            {
                _logger.LogWarning("Shop webhook: non-integer reference {Reference}", payload.Reference);
                return Ok();
            }

            var order = await _context.Orders.FindAsync(orderId);
            if (order == null)
            {
                _logger.LogWarning("Shop webhook: unknown order {OrderId}", orderId);
                return Ok();
            }

            order.Status = payload.Name switch
            {
                "AUTHORIZED" => OrderStatus.Reserved,
                "TERMINATED" => OrderStatus.Failed,
                "REFUNDED"   => OrderStatus.Refunded,
                _            => order.Status
            };

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Shop webhook: order {OrderId} -> {Status}", orderId, payload.Name);
            return Ok();
        }
    }
}
