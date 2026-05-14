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
        private readonly IOrderEmailService _orderEmail;
        private readonly IAppSettingsCache _settingsCache;
        private readonly IWebhookSecretProtector _protector;
        private readonly IWebPushService _push;
        private readonly VippsOptions _vippsOpts;
        private readonly AppOptions _appOpts;
        private readonly IMapper _mapper;
        private readonly ILogger<ShopController> _logger;

        public ShopController(
            ApplicationDbContext context,
            IVippsService vipps,
            IInvoiceService invoice,
            IOrderEmailService orderEmail,
            IAppSettingsCache settingsCache,
            IWebhookSecretProtector protector,
            IWebPushService push,
            IOptions<VippsOptions> vippsOpts,
            IOptions<AppOptions> appOpts,
            IMapper mapper,
            ILogger<ShopController> logger)
        {
            _context = context;
            _vipps = vipps;
            _invoice = invoice;
            _orderEmail = orderEmail;
            _settingsCache = settingsCache;
            _protector = protector;
            _push = push;
            _vippsOpts = vippsOpts.Value;
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
            var userId = User.UserId()!;

            if (!PhoneNumber.TryNormalizeNo(dto.PhoneNumber, out var msisdn))
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
            var vatEnabled = settings.VatEnabled;
            var taxBp = vatEnabled ? Pricing.VatBasisPoints : 0;

            var itemPrices = dto.Items.ToDictionary(
                line => line.ShopItemId,
                line => Pricing.EffectiveInOre(shopItems[line.ShopItemId].PriceInOre, vatEnabled));

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
                UnitPriceExclVatInOre = shopItems[line.ShopItemId].PriceInOre,
                VatPercentage = vatEnabled ? Pricing.VatPercent : 0
            }).ToList();
            _context.OrderItems.AddRange(orderItems);
            await _context.SaveChangesAsync();

            var receiptLines = dto.Items.Select(line => new VippsOrderLine
            {
                Name = shopItems[line.ShopItemId].Name,
                Id = line.ShopItemId.ToString(),
                UnitPriceInOre = itemPrices[line.ShopItemId],
                UnitPriceExclVatInOre = shopItems[line.ShopItemId].PriceInOre,
                Quantity = line.Quantity,
                TaxPercentageBasisPoints = taxBp
            }).ToList();

            var redirectUrl = $"{_appOpts.FrontendBaseUrl}/shop/return";

            try
            {
                var vippsResponse = await _vipps.CreatePaymentAsync(
                    order, receiptLines, redirectUrl, msisdn, $"order-{order.Id}");

                order.VippsOrderId = vippsResponse.Reference;
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
            var userId = User.UserId()!;

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

            await _vipps.CapturePaymentAsync(order.VippsOrderId, order.TotalInOre, $"capture-{order.Id}");

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
            var userId = User.UserId()!;
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

            await _vipps.RefundPaymentAsync(order.VippsOrderId, order.TotalInOre, $"refund-{order.Id}");

            order.Status = OrderStatus.Refunded;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} refunded by admin", id);
            return Ok();
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

            await _vipps.CancelPaymentAsync(order.VippsOrderId, $"cancel-{order.Id}");

            await RestoreStockAsync(order);
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

        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            var rawBody = await ReadRawBodyAsync(Request);
            var settings = await _settingsCache.GetAsync();
            var secret = _protector.Unprotect(settings.VippsShopWebhookSecret ?? string.Empty);

            var verify = _vipps.VerifyWebhookSignature(Request, rawBody, secret);
            if (verify != WebhookVerifyResult.Valid)
            {
                _logger.LogWarning("Shop webhook verify failed: {Reason}", verify);
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

            var eventId = !string.IsNullOrEmpty(payload.PspReference)
                ? payload.PspReference
                : $"{payload.Reference}:{payload.Name}";

            if (!await TryRecordEventAsync(_context, "shop", eventId))
            {
                _logger.LogInformation("Shop webhook duplicate {EventId} skipped", eventId);
                return Ok();
            }

            var order = await _context.Orders
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.VippsOrderId == payload.Reference);
            if (order == null)
            {
                _logger.LogWarning("Shop webhook: unknown reference {Reference}", payload.Reference);
                await _context.SaveChangesAsync();
                return Ok();
            }

            var expectedMsn = order.IsTest ? _vippsOpts.TestMerchantSerialNumber : _vippsOpts.MerchantSerialNumber;
            if (!string.IsNullOrEmpty(payload.Msn) && !string.IsNullOrEmpty(expectedMsn) && payload.Msn != expectedMsn)
            {
                _logger.LogWarning("Shop webhook: MSN mismatch (got {Got}, expected {Expected}) for order {OrderId}",
                    payload.Msn, expectedMsn, order.Id);
                return Unauthorized();
            }

            if (payload.Amount != null && payload.Amount.Value != order.TotalInOre &&
                payload.Name is "AUTHORIZED" or "CAPTURED")
            {
                _logger.LogWarning("Shop webhook: amount mismatch (got {Got}, expected {Expected}) for order {OrderId}",
                    payload.Amount.Value, order.TotalInOre, order.Id);
                return Unauthorized();
            }

            var prevStatus = order.Status;

            switch (payload.Name)
            {
                case "AUTHORIZED":
                    order.Status = OrderStatus.Reserved;
                    await TryPopulateShippingFromVippsAsync(order);
                    break;
                case "CAPTURED":
                    order.Status = OrderStatus.Paid;
                    if (order.ShippedAt == null) order.ShippedAt = DateTime.UtcNow;
                    break;
                case "REFUNDED":
                    order.Status = OrderStatus.Refunded;
                    break;
                case "ABORTED":
                case "EXPIRED":
                case "TERMINATED":
                    order.Status = OrderStatus.Failed;
                    break;
                case "CANCELLED":
                    order.Status = OrderStatus.Cancelled;
                    break;
                case "CREATED":
                    break;
                default:
                    _logger.LogWarning("Shop webhook: unknown event {Name} for order {OrderId}",
                        payload.Name, order.Id);
                    break;
            }

            order.UpdatedAt = DateTime.UtcNow;

            if (prevStatus == OrderStatus.Reserved &&
                order.Status is OrderStatus.Failed or OrderStatus.Cancelled)
            {
                await RestoreStockAsync(order);
            }

            await _context.SaveChangesAsync();

            if (prevStatus != OrderStatus.Paid && order.Status == OrderStatus.Paid)
            {
                await TryGenerateInvoiceAsync(order.Id, "webhook capture");

                _ = SafePushAsync(order.UserId, "Payment captured",
                    $"Order #{order.Id} is paid. Invoice on its way to your email.");
            }
            else if (payload.Name == "CAPTURED" && order.Status == OrderStatus.Paid)
            {
                // Order already Paid (admin captured first, or this is a webhook
                // redelivery). Service is idempotent: short-circuits on a complete
                // existing invoice, otherwise renders. Empty placeholder rows from
                // a prior failed render are cleaned up inside the service.
                _logger.LogInformation("Webhook recovery check for order {OrderId}", order.Id);
                await TryGenerateInvoiceAsync(order.Id, "webhook recovery");
            }
            else if (prevStatus != OrderStatus.Reserved && order.Status == OrderStatus.Reserved)
            {
                try { await _orderEmail.SendOrderConfirmedAsync(order.Id); }
                catch (Exception ex) { _logger.LogError(ex, "Order confirmation email failed for order {OrderId}", order.Id); }

                _ = SafePushAsync(order.UserId, "Order confirmed",
                    $"Order #{order.Id} confirmed. We'll get it ready to ship.");
            }

            _logger.LogInformation("Shop webhook: order {OrderId} -> {Status}", order.Id, payload.Name);
            return Ok();
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

        private async Task TryPopulateShippingFromVippsAsync(Order order)
        {
            if (string.IsNullOrEmpty(order.VippsOrderId)) return;
            try
            {
                var payment = await _vipps.GetPaymentAsync(order.VippsOrderId);
                if (string.IsNullOrEmpty(payment?.ProfileSub)) return;

                var info = await _vipps.GetUserInfoAsync(payment.ProfileSub);
                var formatted = VippsAddressFormatter.Format(info?.Address);
                if (!string.IsNullOrEmpty(formatted))
                    order.ShippingAddress = formatted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Vipps user info for order {OrderId}", order.Id);
            }
        }

        private async Task RestoreStockAsync(Order order)
        {
            var ids = order.OrderItems.Select(oi => oi.ShopItemId).ToList();
            var items = await _context.ShopItems.Where(i => ids.Contains(i.Id)).ToListAsync();
            var byId = items.ToDictionary(i => i.Id);
            foreach (var oi in order.OrderItems)
            {
                if (byId.TryGetValue(oi.ShopItemId, out var item) && item.StockCount != -1)
                    item.StockCount += oi.Quantity;
            }
        }

        [HttpPost("items/{itemId}/photo")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> UploadShopItemPhoto(int itemId, [FromBody] UploadPhotoDto dto)
        {
            if (!await _context.ShopItems.AnyAsync(i => i.Id == itemId))
                return NotFound();

            var userId = User.UserId()!;
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
