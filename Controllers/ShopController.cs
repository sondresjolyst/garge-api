using MapsterMapper;
using garge_api.Constants;
using garge_api.Controllers.Common;
using garge_api.Dtos.Common;
using garge_api.Dtos.Shop;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/shop")]
    public class ShopController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IVippsService _vipps;
        private readonly IInvoiceService _invoice;
        private readonly IAppSettingsCache _settingsCache;
        private readonly IWebPushService _push;
        private readonly AppOptions _appOpts;
        private readonly IMapper _mapper;
        private readonly ILogger<ShopController> _logger;

        public ShopController(
            ApplicationDbContext context,
            IVippsService vipps,
            IInvoiceService invoice,
            IAppSettingsCache settingsCache,
            IWebPushService push,
            IOptions<AppOptions> appOpts,
            IMapper mapper,
            ILogger<ShopController> logger)
        {
            _context = context;
            _vipps = vipps;
            _invoice = invoice;
            _settingsCache = settingsCache;
            _push = push;
            _appOpts = appOpts.Value;
            _mapper = mapper;
            _logger = logger;
        }

        [HttpGet("items")]
        [AllowAnonymous]
        public async Task<IActionResult> GetItems()
        {
            var photoItemIds = await _context.ShopItemPhotos
                .Select(p => p.ShopItemId)
                .ToListAsync();
            var photoSet = photoItemIds.ToHashSet();

            var items = await _context.ShopItems
                .Where(i => i.IsActive)
                .OrderBy(i => i.PriceInOre)
                .ToListAsync();

            var dtos = _mapper.Map<List<ShopItemResponseDto>>(items);
            foreach (var dto in dtos)
                dto.HasImage = photoSet.Contains(dto.Id);

            return Ok(dtos);
        }

        [HttpGet("items/{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetItem(int id)
        {
            var item = await _context.ShopItems.FindAsync(id);
            if (item == null) return NotFound();
            var dto = _mapper.Map<ShopItemResponseDto>(item);
            dto.HasImage = await _context.ShopItemPhotos.AnyAsync(p => p.ShopItemId == id);
            return Ok(dto);
        }

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

        [HttpPut("items/{id}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> UpdateItem(int id, [FromBody] UpdateShopItemDto dto)
        {
            var item = await _context.ShopItems.FindAsync(id);
            if (item == null) return NotFound();

            _mapper.Map(dto, item);
            await _context.SaveChangesAsync();

            _logger.LogInformation("ShopItem {ItemId} updated", id);
            var responseDto = _mapper.Map<ShopItemResponseDto>(item);
            responseDto.HasImage = await _context.ShopItemPhotos.AnyAsync(p => p.ShopItemId == id);
            return Ok(responseDto);
        }

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

        [HttpPost("checkout")]
        [Authorize]
        public async Task<IActionResult> Checkout([FromBody] CreateOrderDto dto)
        {
            var userId = User.GetUserId()!;

            if (!VippsPhoneNumber.TryNormalize(dto.PhoneNumber, out var msisdn))
                return BadRequest("Invalid Norwegian phone number.");

            var shopItemIds = dto.Items.Select(i => i.ShopItemId).Distinct().ToList();

            await using var tx = await _context.Database.BeginTransactionAsync();

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

            var settings = await _settingsCache.GetAsync();
            var vatPercent = Pricing.VatPercentFor(settings, DateTime.UtcNow);
            var taxBp = vatPercent > 0 ? Pricing.VatBasisPoints : 0;

            var itemPrices = dto.Items.ToDictionary(line => line.ShopItemId, line => shopItems[line.ShopItemId].PriceInOre);
            var itemPricesExclVat = itemPrices.ToDictionary(kv => kv.Key, kv => Pricing.Split(kv.Value, vatPercent).ExclVat);

            var total = dto.Items.Sum(line => itemPrices[line.ShopItemId] * line.Quantity);

            foreach (var line in dto.Items)
            {
                var item = shopItems[line.ShopItemId];
                if (item.StockCount != -1)
                    item.StockCount -= line.Quantity;
            }

            var order = new Order
            {
                UserId = userId,
                TotalInOre = total,
                Status = OrderStatus.Pending,
                ShippingAddress = dto.ShippingAddress,
                IsTest = settings.VippsTestMode
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var orderItems = dto.Items.Select(line => new OrderItem
            {
                OrderId = order.Id,
                ShopItemId = line.ShopItemId,
                Quantity = line.Quantity,
                PriceAtPurchaseInOre = itemPrices[line.ShopItemId],
                UnitPriceExclVatInOre = itemPricesExclVat[line.ShopItemId],
                VatPercentage = vatPercent
            }).ToList();
            _context.OrderItems.AddRange(orderItems);
            await _context.SaveChangesAsync();

            var receiptLines = dto.Items.Select(line => new VippsOrderLine
            {
                Name = shopItems[line.ShopItemId].Name,
                Id = line.ShopItemId.ToString(),
                UnitPriceInOre = itemPrices[line.ShopItemId],
                UnitPriceExclVatInOre = itemPricesExclVat[line.ShopItemId],
                Quantity = line.Quantity,
                TaxPercentageBasisPoints = taxBp
            }).ToList();

            var redirectUrl = $"{_appOpts.FrontendBaseUrl}/shop/return";

            try
            {
                var vippsResponse = await _vipps.CreatePaymentAsync(
                    order, receiptLines, redirectUrl, msisdn, $"order-{order.Id}");

                order.VippsOrderId = vippsResponse.Reference;
                // The environment the payment was created in decides which merchant its events come from.
                order.IsTest = vippsResponse.IsTest;
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                _logger.LogInformation("Order {OrderId} created for user {UserId}, total {Total}øre",
                    order.Id, userId, total);

                return Ok(new CheckoutResponseDto
                {
                    OrderId = order.Id,
                    RedirectUrl = vippsResponse.RedirectUrl
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "Vipps payment creation failed for user {UserId}", userId);
                return StatusCode(502, "Payment provider unavailable.");
            }
        }

        [HttpGet("orders/my")]
        [Authorize]
        public async Task<IActionResult> GetMyOrders()
        {
            var userId = User.GetUserId()!;

            var orders = await _context.Orders
                .Include(o => o.OrderItems).ThenInclude(oi => oi.ShopItem)
                .Include(o => o.Invoice)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return Ok(_mapper.Map<List<OrderResponseDto>>(orders));
        }

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

            // After a partial capture only the rest is captured, under its own key.
            var captureKey = order.CapturedInOre == 0 ? $"capture-{order.Id}" : $"capture-{order.Id}-{order.CapturedInOre}";
            var captured = await _vipps.CapturePaymentAsync(order.VippsOrderId, order.TotalInOre - order.CapturedInOre, captureKey, order.IsTest);
            order.CapturedInOre = Math.Clamp(Math.Max(order.CapturedInOre, captured.CapturedAmountInOre), 0, order.TotalInOre);
            if (captured.CapturedAmountInOre < order.TotalInOre)
            {
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                _logger.LogError("Order {OrderId} capture reported {Captured} of {Total} øre captured, order stays Reserved",
                    id, captured.CapturedAmountInOre, order.TotalInOre);
                return StatusCode(502, "Vipps did not confirm the full capture.");
            }

            order.Status = OrderStatus.Paid;
            order.ShippedAt = DateTime.UtcNow;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await TryGenerateInvoiceAsync(id, "admin capture");

            _ = SafePushAsync(order.UserId, "Payment captured",
                $"Order #{order.Id} is paid. Invoice sent to your email.");

            _logger.LogInformation("Order {OrderId} captured by admin", id);
            return Ok();
        }

        [HttpGet("orders/{id}/invoice")]
        [Authorize]
        public async Task<IActionResult> GetInvoicePdf(int id)
        {
            var userId = User.GetUserId()!;
            var isAdmin = User.IsInRole("Admin");

            var invoice = await _context.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.OrderId == id);

            if (invoice == null) return NotFound("Invoice not yet available.");
            if (!isAdmin && invoice.Order?.UserId != userId) return Forbid();

            return File(invoice.PdfData, "application/pdf",
                $"invoice-{invoice.Id:D4}.pdf");
        }

        [HttpPost("orders/{id}/refund")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> RefundOrder(int id)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null) return NotFound();
            if (order.Status != OrderStatus.Paid)
                return BadRequest("Order is not in Paid state.");
            if (string.IsNullOrEmpty(order.VippsOrderId))
                return BadRequest("No Vipps reference found.");

            // After a partial refund only the rest is refunded, under its own key.
            var refundKey = order.RefundedInOre == 0 ? $"refund-{order.Id}" : $"refund-{order.Id}-{order.RefundedInOre}";
            var refunded = await _vipps.RefundPaymentAsync(order.VippsOrderId, order.TotalInOre - order.RefundedInOre, refundKey, order.IsTest);
            order.RefundedInOre = Math.Clamp(Math.Max(order.RefundedInOre, refunded.RefundedAmountInOre), 0, order.TotalInOre);
            if (refunded.RefundedAmountInOre < order.TotalInOre)
            {
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                _logger.LogError("Order {OrderId} refund reported {Refunded} of {Total} øre refunded, order stays Paid",
                    id, refunded.RefundedAmountInOre, order.TotalInOre);
                await TryCreditRefundsAsync(order.Id);
                return StatusCode(502, "Vipps did not confirm the full refund.");
            }

            order.Status = OrderStatus.Refunded;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await TryCreditRefundsAsync(order.Id);

            _logger.LogInformation("Order {OrderId} refunded by admin", id);
            return Ok();
        }

        // A refund is never undone by a credit note that fails. The retry job makes it later.
        private async Task TryCreditRefundsAsync(int orderId)
        {
            try { await _invoice.CreditOrderRefundsAsync(orderId); }
            catch (Exception ex) { _logger.LogError(ex, "The credit note for the refund of order {OrderId} is made later", orderId); }
        }

        [HttpPost("orders/{id}/cancel")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CancelOrder(int id)
        {
            var order = await _context.Orders
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            if (order.Status != OrderStatus.Reserved)
                return BadRequest("Order is not in Reserved state.");
            if (string.IsNullOrEmpty(order.VippsOrderId))
                return BadRequest("No Vipps reference found.");

            await _vipps.CancelPaymentAsync(order.VippsOrderId, $"cancel-{order.Id}", order.IsTest);

            await OrderStock.RestoreAsync(_context, order);
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} cancelled by admin", id);
            return Ok();
        }

        [HttpPost("orders/{id}/invoice/regenerate")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> RegenerateInvoice(int id)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null) return NotFound();
            if (order.Status != OrderStatus.Paid)
                return BadRequest("Order is not in Paid state.");

            try
            {
                var invoiceId = await _invoice.GenerateAndStoreAsync(id, force: true);
                _logger.LogInformation("Invoice {InvoiceId} regenerated by admin for order {OrderId}", invoiceId, id);
                return Ok(new { invoiceId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual invoice regenerate failed for order {OrderId}", id);
                return StatusCode(500, "Failed to regenerate invoice.");
            }
        }

        private async Task SafePushAsync(string userId, string title, string body)
        {
            try { await _push.SendAsync(userId, title, body); }
            catch (Exception ex) { _logger.LogWarning(ex, "Push send failed for user {UserId}", userId); }
        }

        private async Task<int?> TryGenerateInvoiceAsync(int orderId, string context)
        {
            try { return await _invoice.GenerateAndStoreAsync(orderId); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice generation failed ({Context}) for order {OrderId}", context, orderId);
                return null;
            }
        }

        [HttpPost("items/{itemId}/photo")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> UploadShopItemPhoto(int itemId, [FromBody] UploadPhotoDto dto)
        {
            if (!await _context.ShopItems.AnyAsync(i => i.Id == itemId))
                return NotFound();

            var userId = User.GetUserId()!;
            return await PhotoEndpointHelpers.UpsertAsync(
                _context,
                _context.ShopItemPhotos,
                p => p.ShopItemId == itemId,
                () => new ShopItemPhoto
                {
                    ShopItemId = itemId,
                    UserId = userId,
                    Data = dto.Data,
                    ContentType = dto.ContentType
                },
                dto,
                userId);
        }

        [HttpGet("items/{itemId}/photo")]
        [AllowAnonymous]
        public Task<IActionResult> GetShopItemPhoto(int itemId) =>
            PhotoEndpointHelpers.GetAsync(
                _context.ShopItemPhotos,
                p => p.ShopItemId == itemId);

        [HttpDelete("items/{itemId}/photo")]
        [Authorize(Policy = "Admin")]
        public Task<IActionResult> DeleteShopItemPhoto(int itemId) =>
            PhotoEndpointHelpers.DeleteAsync(
                _context,
                _context.ShopItemPhotos,
                p => p.ShopItemId == itemId);
    }
}
