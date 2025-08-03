using garge_api.Models;
using garge_api.Dtos.Webhook;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AutoMapper;
using garge_api.Models.Webhook;

namespace garge_api.Controllers
{
    [ApiController]
    [Authorize]
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
        public async Task<IActionResult> AddWebhook([FromBody] WebhookSubscriptionDto webhookDto)
        {
            _logger.LogInformation("AddWebhook called by {User} with WebhookUrl={WebhookUrl}", User.Identity?.Name, webhookDto.WebhookUrl);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("AddWebhook failed: Invalid model state for WebhookUrl={WebhookUrl}", webhookDto.WebhookUrl);
                return BadRequest(ModelState);
            }

            var webhookSubscription = _mapper.Map<WebhookSubscription>(webhookDto);
            webhookSubscription.CreatedAt = DateTime.UtcNow;

            _context.WebhookSubscriptions.Add(webhookSubscription);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);

            _logger.LogInformation("Webhook subscription created: Id={Id}, WebhookUrl={WebhookUrl}", webhookSubscription.Id, webhookSubscription.WebhookUrl);
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
            _logger.LogInformation("GetWebhook called by {User} for Id={Id}", User.Identity?.Name, id);

            var webhookSubscription = await _context.WebhookSubscriptions.FindAsync(id);

            if (webhookSubscription == null)
            {
                _logger.LogWarning("GetWebhook not found: Id={Id}", id);
                return NotFound();
            }

            var dto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);

            _logger.LogInformation("Webhook subscription returned: Id={Id}, WebhookUrl={WebhookUrl}", webhookSubscription.Id, webhookSubscription.WebhookUrl);
            return Ok(dto);
        }
    }
}
