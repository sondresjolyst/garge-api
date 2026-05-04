using AutoMapper;
using garge_api.Dtos.Subscription;
using garge_api.Models;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        private readonly IMapper _mapper;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(
            ApplicationDbContext context,
            IVippsService vipps,
            IMapper mapper,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _vipps = vipps;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>Returns the current user's active or pending subscription.</summary>
        [HttpGet("my")]
        public async Task<IActionResult> GetMySubscription()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var subscription = await _context.Subscriptions
                .Include(s => s.Product)
                .Where(s => s.UserId == userId &&
                            s.Status != SubscriptionStatus.Stopped &&
                            s.Status != SubscriptionStatus.Expired)
                .FirstOrDefaultAsync();

            if (subscription == null) return NoContent();
            return Ok(_mapper.Map<SubscriptionResponseDto>(subscription));
        }

        /// <summary>Initiates a Vipps subscription agreement and returns the confirmation URL.</summary>
        [HttpPost("initiate")]
        public async Task<IActionResult> InitiateSubscription([FromBody] InitiateSubscriptionDto dto)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var product = await _context.Products.FindAsync(dto.ProductId);
            if (product == null || !product.IsActive)
                return BadRequest("Product not found or inactive.");

            var existing = await _context.Subscriptions.AnyAsync(s =>
                s.UserId == userId &&
                (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending));

            if (existing)
                return Conflict("User already has an active or pending subscription.");

            var settings = await _context.AppSettings.FindAsync(1);
            var effectivePriceInOre = (settings?.VatEnabled ?? false)
                ? (int)(product.PriceInOre * 1.25)
                : product.PriceInOre;

            var vippsResponse = await _vipps.CreateAgreementAsync(
                product, userId, dto.RedirectUrl, dto.PhoneNumber, effectivePriceInOre);

            var subscription = new Subscription
            {
                UserId = userId,
                ProductId = dto.ProductId,
                VippsAgreementId = vippsResponse.AgreementId,
                Status = SubscriptionStatus.Pending,
                ConsentAcceptedAt = DateTime.UtcNow,
                ConsentIp = HttpContext.Connection.RemoteIpAddress?.ToString()
            };

            _context.Subscriptions.Add(subscription);
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

        /// <summary>Cancels the current user's active subscription.</summary>
        [HttpPost("cancel")]
        public async Task<IActionResult> CancelSubscription()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
                .FirstOrDefaultAsync();

            if (subscription == null) return NotFound("No active subscription found.");

            await _vipps.CancelAgreementAsync(subscription.VippsAgreementId);

            subscription.Status = SubscriptionStatus.Stopped;
            subscription.UpdatedAt = DateTime.UtcNow;
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

            var signature = Request.Headers["X-Vipps-Signature"].FirstOrDefault() ?? string.Empty;
            var settings = await _context.AppSettings.FindAsync(1);
            var secret = settings?.VippsSubscriptionWebhookSecret ?? string.Empty;
            if (!_vipps.VerifyWebhookSignature(rawBody, signature, secret))
            {
                _logger.LogWarning("Subscription webhook HMAC verification failed");
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

            var subscription = await _context.Subscriptions
                .FirstOrDefaultAsync(s => s.VippsAgreementId == payload.AgreementId);

            if (subscription == null)
            {
                _logger.LogWarning("Webhook: unknown agreementId {AgreementId}", payload.AgreementId);
                return Ok();
            }

            subscription.Status = payload.EventType switch
            {
                "recurring.agreement-activated.v1" => SubscriptionStatus.Active,
                "recurring.agreement-stopped.v1"   => SubscriptionStatus.Stopped,
                "recurring.agreement-expired.v1"   => SubscriptionStatus.Expired,
                "recurring.agreement-rejected.v1"  => SubscriptionStatus.Stopped,
                _                                  => subscription.Status
            };

            if (subscription.Status == SubscriptionStatus.Active && subscription.StartDate == null)
                subscription.StartDate = payload.Occurred ?? DateTime.UtcNow;

            subscription.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Webhook: agreement {AgreementId} -> {EventType}",
                payload.AgreementId, payload.EventType);

            return Ok();
        }
    }
}
