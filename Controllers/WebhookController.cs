using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace garge_api.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public WebhookController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<IActionResult> AddWebhook([FromBody] WebhookSubscriptionDto webhookDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var webhookSubscription = new WebhookSubscription
            {
                WebhookUrl = webhookDto.WebhookUrl,
                CreatedAt = DateTime.UtcNow
            };

            _context.WebhookSubscriptions.Add(webhookSubscription);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetWebhook), new { id = webhookSubscription.Id }, webhookSubscription);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetWebhook(int id)
        {
            var webhookSubscription = await _context.WebhookSubscriptions.FindAsync(id);

            if (webhookSubscription == null)
            {
                return NotFound();
            }

            return Ok(webhookSubscription);
        }
    }

    public class WebhookSubscriptionDto
    {
        [Required]
        [MaxLength(500)]
        public string WebhookUrl { get; set; }
    }
}
