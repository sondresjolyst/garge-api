using MapsterMapper;
using garge_api.Constants;
using garge_api.Dtos.Subscription;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Hosting;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/subscriptions")]
    [Authorize]
    public class SubscriptionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IVippsService _vipps;
        private readonly IInvoiceService _invoice;
        private readonly IAppSettingsCache _settingsCache;
        private readonly AppOptions _appOpts;
        private readonly IMapper _mapper;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(
            ApplicationDbContext context,
            IVippsService vipps,
            IInvoiceService invoice,
            IAppSettingsCache settingsCache,
            IOptions<AppOptions> appOpts,
            IMapper mapper,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _vipps = vipps;
            _invoice = invoice;
            _settingsCache = settingsCache;
            _appOpts = appOpts.Value;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>Lists every subscription with the owner's name, email, and invoice count. Admin only.</summary>
        [HttpGet("all")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> GetAllSubscriptions()
        {
            var rows = await _context.Subscriptions
                .Include(s => s.User)
                .Include(s => s.Product)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new AdminSubscriptionResponseDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    UserEmail = s.User != null ? s.User.Email ?? string.Empty : string.Empty,
                    UserName = s.User != null ? (s.User.FirstName + " " + s.User.LastName).Trim() : string.Empty,
                    ProductName = s.Product != null ? s.Product.Name : string.Empty,
                    ProductType = s.Product != null ? s.Product.Type.ToString() : string.Empty,
                    PriceInOre = s.UnitPriceInOre,
                    Quantity = s.Quantity,
                    Interval = s.Product != null ? s.Product.Interval.ToString() : string.Empty,
                    Status = s.Status.ToString(),
                    IsTest = s.IsTest,
                    StartDate = s.StartDate,
                    NextChargeDate = s.NextChargeDate,
                    InvoiceCount = _context.Invoices.Count(i => i.SubscriptionId == s.Id),
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt,
                })
                .ToListAsync();

            return Ok(rows);
        }

        /// <summary>Lists invoice metadata for a subscription. Owner or admin only.</summary>
        [HttpGet("{id:int}/invoices")]
        public async Task<IActionResult> GetSubscriptionInvoices(int id)
        {
            var userId = User.GetUserId()!;
            var isAdmin = User.IsInRole("Admin");

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.Id == id);
            if (subscription == null) return NotFound();
            if (!isAdmin && subscription.UserId != userId) return Forbid();

            var invoices = await _context.Invoices
                .Where(i => i.SubscriptionId == id)
                .OrderByDescending(i => i.IssuedAt)
                .Select(i => new
                {
                    i.Id,
                    i.IssuedAt,
                    i.AmountInOre,
                    i.RefundedInOre,
                    i.VippsChargeId,
                })
                .ToListAsync();

            return Ok(invoices);
        }

        /// <summary>
        /// Refunds part or all of a captured subscription charge through Vipps, records it and sends the
        /// customer a credit note. Without an amount, what is left of the charge is refunded. Admin only.
        /// </summary>
        [HttpPost("invoices/{invoiceId:int}/refund")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> RefundCharge(int invoiceId, [FromBody] RefundChargeDto dto)
        {
            var invoice = await _context.Invoices
                .Include(i => i.Subscription).ThenInclude(s => s!.Product)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.SubscriptionId != null);
            if (invoice?.Subscription == null) return NotFound("Subscription charge not found.");
            if (string.IsNullOrEmpty(invoice.VippsChargeId)) return BadRequest("The charge has no Vipps charge id.");

            var left = invoice.AmountInOre - invoice.RefundedInOre;
            var amount = dto.AmountInOre ?? left;
            if (left <= 0) return BadRequest("The charge is already refunded in full.");
            if (amount <= 0 || amount > left) return BadRequest($"The refund must be between 1 and {left} øre.");

            // Each refund of the charge has its own key, so a retried request never refunds twice.
            var key = $"refund-inv-{invoice.Id}-{invoice.RefundedInOre}";
            try
            {
                await _vipps.RefundChargeAsync(invoice.Subscription.VippsAgreementId, invoice.VippsChargeId, amount,
                    $"Refund, {invoice.Subscription.Product?.Name ?? "subscription"}", key, invoice.Subscription.IsTest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refund of {Amount} øre on subscription invoice {InvoiceId} failed at Vipps", amount, invoice.Id);
                return StatusCode(502, "Vipps did not accept the refund.");
            }

            invoice.RefundedInOre += amount;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Refunded {Amount} øre on subscription invoice {InvoiceId}", amount, invoice.Id);

            int? creditNoteId = null;
            try { creditNoteId = await _invoice.CreditRefundsAsync(invoice.Id); }
            catch (Exception ex) { _logger.LogError(ex, "The credit note for the refund on invoice {InvoiceId} is made later", invoice.Id); }

            return Ok(new { invoice.Id, invoice.AmountInOre, invoice.RefundedInOre, CreditNoteId = creditNoteId });
        }

        /// <summary>Downloads a single subscription invoice PDF. Owner or admin only.</summary>
        [HttpGet("invoices/{invoiceId:int}/pdf")]
        public async Task<IActionResult> GetSubscriptionInvoicePdf(int invoiceId)
        {
            var userId = User.GetUserId()!;
            var isAdmin = User.IsInRole("Admin");

            var invoice = await _context.Invoices
                .Include(i => i.Subscription)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.SubscriptionId != null);
            if (invoice == null) return NotFound("Subscription invoice not found.");
            if (!isAdmin && invoice.Subscription?.UserId != userId) return Forbid();

            return File(invoice.PdfData, "application/pdf",
                $"invoice-{invoice.Id:D4}.pdf");
        }

        /// <summary>Returns the current user's subscriptions. Stopped or expired subscriptions are hidden once their next-charge date has passed.</summary>
        [HttpGet("my")]
        public async Task<IActionResult> GetMySubscriptions()
        {
            var userId = User.GetUserId()!;
            var now = DateTime.UtcNow;

            var subscriptions = await _context.Subscriptions
                .Include(s => s.Product)
                .Include(s => s.PriceChangeNotices.Where(n => n.AppliedAt == null && n.NotifiedAt != null))
                    .ThenInclude(n => n.PriceChange)
                .Where(s => s.UserId == userId &&
                            (s.Status == SubscriptionStatus.Active ||
                             s.Status == SubscriptionStatus.Pending ||
                             ((s.Status == SubscriptionStatus.Stopped || s.Status == SubscriptionStatus.Expired) &&
                              s.NextChargeDate != null && s.NextChargeDate > now)))
                .ToListAsync();

            return Ok(_mapper.Map<List<SubscriptionResponseDto>>(subscriptions));
        }

        /// <summary>Returns the Vipps confirmation URL for a Pending subscription so the user can resume payment.</summary>
        [HttpGet("{id:int}/confirmation-url")]
        public async Task<IActionResult> GetConfirmationUrl(int id)
        {
            var userId = User.GetUserId()!;

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
            if (subscription == null) return NotFound();
            if (subscription.Status != SubscriptionStatus.Pending)
                return BadRequest("Subscription is not pending.");
            if (string.IsNullOrEmpty(subscription.VippsConfirmationUrl))
                return BadRequest("No confirmation URL stored.");

            return Ok(new { vippsConfirmationUrl = subscription.VippsConfirmationUrl });
        }

        /// <summary>Initiates a Vipps subscription agreement and returns the confirmation URL.</summary>
        [HttpPost("initiate")]
        public async Task<IActionResult> InitiateSubscription([FromBody] InitiateSubscriptionDto dto)
        {
            var userId = User.GetUserId()!;

            if (!dto.ConsentToWaiveWithdrawal)
                return BadRequest("Consent to waive 14-day withdrawal right is required for immediate access.");

            if (!VippsPhoneNumber.TryNormalize(dto.PhoneNumber, out var msisdn))
                return BadRequest("Invalid Norwegian phone number.");

            var product = await _context.Products.FindAsync(dto.ProductId);
            if (product == null || !product.IsActive)
                return BadRequest("Product not found or inactive.");

            if (product.Type == ProductType.Primary)
            {
                if (dto.Quantity != 1)
                    return BadRequest("Primary plan must have quantity 1.");

                var hasActivePrimary = await _context.Subscriptions
                    .Include(s => s.Product)
                    .AnyAsync(s => s.UserId == userId &&
                                   s.Product!.Type == ProductType.Primary &&
                                   (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending));

                if (hasActivePrimary)
                    return Conflict("User already has an active or pending primary subscription.");
            }
            else
            {
                var hasPrimary = await _context.Subscriptions
                    .Include(s => s.Product)
                    .AnyAsync(s => s.UserId == userId &&
                                   s.Product!.Type == ProductType.Primary &&
                                   s.Status == SubscriptionStatus.Active);

                if (!hasPrimary)
                    return BadRequest("A primary subscription is required before adding an add-on.");
            }

            var settings = await _settingsCache.GetAsync();
            var unitPriceInOre = product.PriceInOre;
            if ((long)unitPriceInOre * dto.Quantity > SubscriptionCharges.MaxAgreementAmountInOre)
                return BadRequest("The total is above the 20 000 kr a Vipps agreement allows.");

            var subscription = new Subscription
            {
                UserId = userId,
                ProductId = dto.ProductId,
                Quantity = dto.Quantity,
                UnitPriceInOre = unitPriceInOre,
                VippsAgreementId = string.Empty,
                Status = SubscriptionStatus.Pending,
                IsTest = settings.VippsTestMode,
                ConsentAcceptedAt = DateTime.UtcNow,
                ConsentIp = IpTruncator.Truncate(HttpContext.Connection.RemoteIpAddress)
            };

            _context.Subscriptions.Add(subscription);
            await _context.SaveChangesAsync();

            var redirectUrl = $"{_appOpts.FrontendBaseUrl}/profile/billing/return?subscriptionId={subscription.Id}";
            var idempotencyKey = $"sub-{subscription.Id}";

            try
            {
                var vippsResponse = await _vipps.CreateAgreementAsync(
                    product, userId, redirectUrl, msisdn, unitPriceInOre, dto.Quantity, idempotencyKey);

                subscription.VippsAgreementId = vippsResponse.AgreementId;
                subscription.VippsConfirmationUrl = vippsResponse.VippsConfirmationUrl;
                // The environment the agreement was created in decides which merchant its events come from.
                subscription.IsTest = vippsResponse.IsTest;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Subscription {SubscriptionId} initiated for user {UserId}",
                    subscription.Id, userId);

                return Ok(new InitiateSubscriptionResponseDto
                {
                    SubscriptionId = subscription.Id,
                    VippsAgreementId = vippsResponse.AgreementId,
                    VippsConfirmationUrl = vippsResponse.VippsConfirmationUrl
                });
            }
            catch (Exception ex)
            {
                _context.Subscriptions.Remove(subscription);
                await _context.SaveChangesAsync();
                _logger.LogError(ex, "Vipps agreement creation failed for user {UserId}", userId);
                return StatusCode(502, "Payment provider unavailable.");
            }
        }

        /// <summary>Cancels a specific active subscription by ID.</summary>
        [HttpPost("cancel/{id:int}")]
        public async Task<IActionResult> CancelSubscription(int id)
        {
            var userId = User.GetUserId()!;

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId && s.Status == SubscriptionStatus.Active);

            if (subscription == null) return NotFound("Active subscription not found.");

            // Add-ons go first. An agreement is marked Stopped only after Vipps confirms the cancel, and
            // the primary plan is cancelled only when all its add-ons are.
            var product = await _context.Products.FindAsync(subscription.ProductId);
            if (product?.Type == ProductType.Primary)
            {
                var addOns = await _context.Subscriptions
                    .Include(s => s.Product)
                    .Where(s => s.UserId == userId &&
                                s.Product!.Type == ProductType.AddOn &&
                                s.Status == SubscriptionStatus.Active)
                    .ToListAsync();

                foreach (var addOn in addOns)
                {
                    try
                    {
                        await _vipps.CancelAgreementAsync(addOn.VippsAgreementId, $"cancel-{addOn.Id}", addOn.IsTest);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Vipps agreement cancel failed for add-on {SubscriptionId}, primary {PrimaryId} left active", addOn.Id, subscription.Id);
                        await _context.SaveChangesAsync();
                        return StatusCode(502, "Payment provider unavailable.");
                    }
                    addOn.Status = SubscriptionStatus.Stopped;
                    addOn.UpdatedAt = DateTime.UtcNow;
                }
            }

            try
            {
                await _vipps.CancelAgreementAsync(subscription.VippsAgreementId, $"cancel-{subscription.Id}", subscription.IsTest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vipps agreement cancel failed for subscription {SubscriptionId}", subscription.Id);
                await _context.SaveChangesAsync();
                return StatusCode(502, "Payment provider unavailable.");
            }
            subscription.Status = SubscriptionStatus.Stopped;
            subscription.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Subscription {SubscriptionId} cancelled for user {UserId}",
                subscription.Id, userId);

            return Ok();
        }

        /// <summary>Updates the quantity of an active add-on subscription. Increasing the quantity raises the Vipps charge ceiling and requires reconfirmation in Vipps.</summary>
        [HttpPatch("{id:int}/quantity")]
        public async Task<IActionResult> UpdateSubscriptionQuantity(int id, [FromBody] UpdateSubscriptionQuantityDto dto)
        {
            var userId = User.GetUserId()!;

            var subscription = await _context.Subscriptions
                .Include(s => s.Product)
                .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

            if (subscription == null) return NotFound();
            if (subscription.Status != SubscriptionStatus.Active)
                return BadRequest("Subscription must be active.");
            if (subscription.Product == null)
                return BadRequest("Subscription product missing.");
            if (subscription.Product.Type == ProductType.Primary)
                return BadRequest("Primary plan quantity is fixed at 1.");
            if (dto.Quantity == subscription.Quantity)
                return BadRequest("New quantity matches current quantity.");

            var isIncrease = dto.Quantity > subscription.Quantity;

            if (isIncrease)
            {
                // The ceiling covers a notified price increase too, so its first charge can go through.
                var notices = await _context.PriceChangeNotices
                    .Include(n => n.PriceChange)
                    .Where(n => n.SubscriptionId == subscription.Id && n.AppliedAt == null)
                    .ToListAsync();
                var unitPriceInOre = SubscriptionPricing.CeilingUnitPrice(subscription, notices);
                if ((long)unitPriceInOre * dto.Quantity > SubscriptionCharges.MaxAgreementAmountInOre)
                    return BadRequest("The total is above the 20 000 kr a Vipps agreement allows.");
                var newCeiling = unitPriceInOre * dto.Quantity;

                try
                {
                    await _vipps.UpdateAgreementMaxAmountAsync(
                        subscription.VippsAgreementId,
                        newCeiling,
                        $"qtyceiling-{subscription.Id}-{dto.Quantity}",
                        isTest: subscription.IsTest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Vipps ceiling update failed for subscription {SubscriptionId}", subscription.Id);
                    return StatusCode(502, "Payment provider unavailable.");
                }
            }

            subscription.Quantity = dto.Quantity;
            subscription.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Subscription {SubscriptionId} quantity updated to {Quantity} for user {UserId}",
                subscription.Id, dto.Quantity, userId);

            var dtoOut = _mapper.Map<SubscriptionResponseDto>(subscription);
            var message = isIncrease
                ? "Quantity updated. Confirm the new charge ceiling in your Vipps app before the next charge."
                : "Quantity updated. The next charge will reflect the new total.";
            return Ok(new { subscription = dtoOut, message });
        }
    }
}
