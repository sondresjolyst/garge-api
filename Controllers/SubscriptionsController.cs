using AutoMapper;
using garge_api.Constants;
using garge_api.Dtos.Subscription;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Subscription;
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
    [Route("api/subscriptions")]
    [Authorize]
    public class SubscriptionsController : VippsWebhookControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IVippsService _vipps;
        private readonly IInvoiceService _invoice;
        private readonly ISubscriptionEmailService _subEmail;
        private readonly IAppSettingsCache _settingsCache;
        private readonly IWebhookSecretProtector _protector;
        private readonly IWebPushService _push;
        private readonly AppOptions _appOpts;
        private readonly IMapper _mapper;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(
            ApplicationDbContext context,
            IVippsService vipps,
            IInvoiceService invoice,
            ISubscriptionEmailService subEmail,
            IAppSettingsCache settingsCache,
            IWebhookSecretProtector protector,
            IWebPushService push,
            IOptions<AppOptions> appOpts,
            IMapper mapper,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _vipps = vipps;
            _invoice = invoice;
            _subEmail = subEmail;
            _settingsCache = settingsCache;
            _protector = protector;
            _push = push;
            _appOpts = appOpts.Value;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>Admin: list every subscription with the user's name + email, plus invoice count for the in-app subscriptions admin page.</summary>
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
                    PriceInOre = s.Product != null ? s.Product.PriceInOre : 0,
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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
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
                    i.VippsChargeId,
                })
                .ToListAsync();

            return Ok(invoices);
        }

        /// <summary>Downloads a single subscription invoice PDF. Owner or admin only.</summary>
        [HttpGet("invoices/{invoiceId:int}/pdf")]
        public async Task<IActionResult> GetSubscriptionInvoicePdf(int invoiceId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var isAdmin = User.IsInRole("Admin");

            var invoice = await _context.Invoices
                .Include(i => i.Subscription)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.SubscriptionId != null);
            if (invoice == null) return NotFound("Subscription invoice not found.");
            if (!isAdmin && invoice.Subscription?.UserId != userId) return Forbid();

            return File(invoice.PdfData, "application/pdf",
                $"invoice-{invoice.Id:D4}.pdf");
        }

        /// <summary>Returns the current user's subscriptions. Stopped/Expired hidden once next-charge date passed (grace period).</summary>
        [HttpGet("my")]
        public async Task<IActionResult> GetMySubscriptions()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var now = DateTime.UtcNow;

            var subscriptions = await _context.Subscriptions
                .Include(s => s.Product)
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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            if (!dto.ConsentToWaiveWithdrawal)
                return BadRequest("Consent to waive 14-day withdrawal right is required for immediate access.");

            if (!PhoneNumber.TryNormalizeNo(dto.PhoneNumber, out var msisdn))
                return BadRequest("Invalid Norwegian phone number.");

            var product = await _context.Products.FindAsync(dto.ProductId);
            if (product == null || !product.IsActive)
                return BadRequest("Product not found or inactive.");

            if (product.Type == ProductType.Primary)
            {
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
            var effectivePriceInOre = Pricing.EffectiveInOre(product.PriceInOre, settings.VatEnabled);

            var subscription = new Subscription
            {
                UserId = userId,
                ProductId = dto.ProductId,
                VippsAgreementId = string.Empty,
                Status = SubscriptionStatus.Pending,
                IsTest = settings.VippsTestMode,
                ConsentAcceptedAt = DateTime.UtcNow,
                ConsentIp = IpTruncator.Truncate(HttpContext.Connection.RemoteIpAddress?.ToString())
            };

            _context.Subscriptions.Add(subscription);
            await _context.SaveChangesAsync();

            var redirectUrl = $"{_appOpts.FrontendBaseUrl}/profile/billing/return";
            var idempotencyKey = $"sub-{subscription.Id}";

            try
            {
                var vippsResponse = await _vipps.CreateAgreementAsync(
                    product, userId, redirectUrl, msisdn, effectivePriceInOre, idempotencyKey);

                subscription.VippsAgreementId = vippsResponse.AgreementId;
                subscription.VippsConfirmationUrl = vippsResponse.VippsConfirmationUrl;
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
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId && s.Status == SubscriptionStatus.Active);

            if (subscription == null) return NotFound("Active subscription not found.");

            await _vipps.CancelAgreementAsync(subscription.VippsAgreementId, $"cancel-{subscription.Id}");
            subscription.Status = SubscriptionStatus.Stopped;
            subscription.UpdatedAt = DateTime.UtcNow;

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
                        await _vipps.CancelAgreementAsync(addOn.VippsAgreementId, $"cancel-{addOn.Id}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to cancel add-on {SubId} after primary cancel", addOn.Id);
                    }
                    addOn.Status = SubscriptionStatus.Stopped;
                    addOn.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Subscription {SubscriptionId} cancelled for user {UserId}",
                subscription.Id, userId);

            return Ok();
        }

        /// <summary>Webhook endpoint for Vipps agreement status changes.</summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            var rawBody = await ReadRawBodyAsync(Request);
            var settings = await _settingsCache.GetAsync();
            var secret = _protector.Unprotect(settings.VippsSubscriptionWebhookSecret ?? string.Empty);

            var verify = _vipps.VerifyWebhookSignature(Request, rawBody, secret);
            if (verify != WebhookVerifyResult.Valid)
            {
                _logger.LogWarning("Subscription webhook verify failed: {Reason}", verify);
                return Unauthorized();
            }

            VippsAgreementWebhookDto? payload;
            try
            {
                payload = JsonSerializer.Deserialize<VippsAgreementWebhookDto>(rawBody, JsonOpts);
            }
            catch
            {
                return BadRequest();
            }

            if (payload == null) return BadRequest();

            var eventId = !string.IsNullOrEmpty(payload.EventId)
                ? payload.EventId
                : $"{payload.AgreementId}:{payload.EventType}:{payload.Occurred?.Ticks}";

            if (!await TryRecordEventAsync(_context, "subscription", eventId))
            {
                _logger.LogInformation("Subscription webhook duplicate {EventId} skipped", eventId);
                return Ok();
            }

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.VippsAgreementId == payload.AgreementId);

            if (subscription == null)
            {
                _logger.LogWarning("Webhook: unknown agreementId {AgreementId}", payload.AgreementId);
                await _context.SaveChangesAsync();
                return Ok();
            }

            var wasActive = subscription.Status == SubscriptionStatus.Active;

            switch (payload.EventType)
            {
                case VippsEvents.AgreementActivated:
                    subscription.Status = SubscriptionStatus.Active;
                    if (subscription.StartDate == null)
                        subscription.StartDate = payload.Occurred ?? DateTime.UtcNow;
                    if (string.IsNullOrEmpty(subscription.BillingAddress))
                        await TryPopulateBillingAddressAsync(subscription, payload.AgreementId);
                    break;
                case VippsEvents.AgreementStopped:
                case VippsEvents.AgreementRejected:
                    subscription.Status = SubscriptionStatus.Stopped;
                    break;
                case VippsEvents.AgreementExpired:
                    subscription.Status = SubscriptionStatus.Expired;
                    break;
                case VippsEvents.ChargeCaptured when payload.Occurred.HasValue:
                    var product = await _context.Products.FindAsync(subscription.ProductId);
                    if (product != null)
                        subscription.NextChargeDate = product.Interval == BillingInterval.Monthly
                            ? payload.Occurred.Value.AddMonths(1)
                            : payload.Occurred.Value.AddYears(1);
                    break;
                case VippsEvents.ChargeFailed:
                case VippsEvents.ChargeCreationFailed:
                    _logger.LogWarning("Charge failed for subscription {SubId} agreement {AgreementId}",
                        subscription.Id, payload.AgreementId);
                    break;
            }

            subscription.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Webhook: agreement {AgreementId} -> {EventType}",
                payload.AgreementId, payload.EventType);

            if (!wasActive && subscription.Status == SubscriptionStatus.Active)
            {
                try { await _subEmail.SendActivatedAsync(subscription.Id); }
                catch (Exception ex) { _logger.LogError(ex, "Activation email failed for subscription {SubscriptionId}", subscription.Id); }

                _ = SafePushAsync(subscription.UserId, "Subscription active",
                    "Your Garge subscription is confirmed.");
            }

            if (payload.EventType == VippsEvents.ChargeCaptured && !string.IsNullOrEmpty(payload.ChargeId))
            {
                var product = await _context.Products.FindAsync(subscription.ProductId);
                if (product != null)
                {
                    try
                    {
                        await _invoice.GenerateForSubscriptionChargeAsync(
                            subscription.Id,
                            payload.ChargeId,
                            product.PriceInOre,
                            payload.Occurred ?? DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Subscription invoice generation failed for subscription {SubscriptionId} charge {ChargeId}",
                            subscription.Id, payload.ChargeId);
                    }
                }
            }

            if (payload.EventType is VippsEvents.ChargeFailed or VippsEvents.ChargeCreationFailed)
            {
                try { await _subEmail.SendChargeFailedAsync(subscription.Id); }
                catch (Exception ex) { _logger.LogError(ex, "Charge-failed email failed for subscription {SubscriptionId}", subscription.Id); }

                _ = SafePushAsync(subscription.UserId, "Payment failed",
                    "We couldn't charge your Garge subscription. Please update your payment method in Vipps.");
            }

            return Ok();
        }

        private async Task SafePushAsync(string userId, string title, string body)
        {
            try { await _push.SendAsync(userId, title, body); }
            catch (Exception ex) { _logger.LogWarning(ex, "Push send failed for user {UserId}", userId); }
        }

        private async Task TryPopulateBillingAddressAsync(Subscription subscription, string agreementId)
        {
            try
            {
                var details = await _vipps.GetAgreementAsync(agreementId);
                if (string.IsNullOrEmpty(details?.Sub)) return;

                var info = await _vipps.GetUserInfoAsync(details.Sub);
                var formatted = VippsAddressFormatter.Format(info?.Address);
                if (!string.IsNullOrEmpty(formatted))
                    subscription.BillingAddress = formatted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Vipps user info for subscription {SubscriptionId}", subscription.Id);
            }
        }
    }
}
