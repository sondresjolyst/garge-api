using garge_api.Models;
using garge_api.Dtos.Webhook;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AutoMapper;
using garge_api.Models.Webhook;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Authorize]
    [Authorize(Policy = "ActiveSubscription")]
    [Route("api/webhooks")]
    public class WebhookController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<WebhookController> _logger;

        public WebhookController(ApplicationDbContext context, IMapper mapper, ILogger<WebhookController> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// Adds a new webhook subscription.
        /// </summary>
        /// <param name="webhookDto">The webhook subscription data.</param>
        /// <returns>The created webhook subscription.</returns>
        [HttpPost]
        public async Task<IActionResult> AddWebhook([FromBody] CreateWebhookSubscriptionDto webhookDto)
        {
            _logger.LogInformation("AddWebhook called by {@LogData}", new { CallerUserId = User.UserId() });

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("AddWebhook failed: Invalid model state");
                return BadRequest(ModelState);
            }

            var webhookSubscription = _mapper.Map<WebhookSubscription>(webhookDto);
            webhookSubscription.CreatedAt = DateTime.UtcNow;
            webhookSubscription.UserId = User.UserId() ?? string.Empty;

            _context.WebhookSubscriptions.Add(webhookSubscription);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);

            _logger.LogInformation("Webhook subscription created: {@LogData}", new { webhookSubscription.Id });
            return CreatedAtAction(nameof(GetWebhook), new { id = webhookSubscription.Id }, resultDto);
        }

        /// <summary>
        /// Gets a webhook subscription by its ID.
        /// </summary>
        /// <param name="id">The webhook subscription ID.</param>
        /// <returns>The webhook subscription data.</returns>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetWebhook(int id)
        {
            _logger.LogInformation("GetWebhook called by {@LogData}", new { CallerUserId = User.UserId(), id });

            var webhookSubscription = await _context.WebhookSubscriptions.FindAsync(id);

            if (webhookSubscription == null)
            {
                _logger.LogWarning("GetWebhook not found: {@LogData}", new { id });
                return NotFound();
            }

            var currentUserId = User.UserId();
            if (webhookSubscription.UserId != currentUserId)
                return Forbid();

            var dto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);

            _logger.LogInformation("Webhook subscription returned: {@LogData}", new { webhookSubscription.Id });
            return Ok(dto);
        }
    }
}
