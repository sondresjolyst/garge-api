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

        public WebhookController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        [HttpPost]
        public async Task<IActionResult> AddWebhook([FromBody] WebhookSubscriptionDto webhookDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var webhookSubscription = _mapper.Map<WebhookSubscription>(webhookDto);
            webhookSubscription.CreatedAt = DateTime.UtcNow;

            _context.WebhookSubscriptions.Add(webhookSubscription);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);
            return CreatedAtAction(nameof(GetWebhook), new { id = webhookSubscription.Id }, resultDto);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetWebhook(int id)
        {
            var webhookSubscription = await _context.WebhookSubscriptions.FindAsync(id);

            if (webhookSubscription == null)
                return NotFound();

            var dto = _mapper.Map<WebhookSubscriptionDto>(webhookSubscription);
            return Ok(dto);
        }
    }
}
