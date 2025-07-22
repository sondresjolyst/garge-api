using garge_api.Dtos.Subscription;
using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using AutoMapper;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/subscriptions")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SubscriptionController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private static readonly List<string> AdminRoles = new() { "subscription_admin", "admin" };

        public SubscriptionController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        private bool UserHasRequiredRole()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all subscriptions.")]
        [SwaggerResponse(200, "A list of all subscriptions.", typeof(IEnumerable<SubscriptionDto>))]
        public async Task<IActionResult> GetAllSubscriptions()
        {
            var subscriptions = await _context.Subscriptions.ToListAsync();
            var dtos = _mapper.Map<IEnumerable<SubscriptionDto>>(subscriptions);
            return Ok(dtos);
        }

        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a subscription by its ID.")]
        [SwaggerResponse(200, "The subscription with the specified ID.", typeof(SubscriptionDto))]
        [SwaggerResponse(404, "Subscription not found.")]
        public async Task<IActionResult> GetSubscription(int id)
        {
            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound(new { message = "Subscription not found!" });

            var dto = _mapper.Map<SubscriptionDto>(subscription);
            return Ok(dto);
        }

        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new subscription.")]
        [SwaggerResponse(201, "The created subscription.", typeof(SubscriptionDto))]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionDto dto)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var subscription = _mapper.Map<Subscription>(dto);
            subscription.CreatedAt = DateTime.UtcNow;
            subscription.UpdatedAt = DateTime.UtcNow;

            _context.Subscriptions.Add(subscription);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<SubscriptionDto>(subscription);
            return CreatedAtAction(nameof(GetSubscription), new { id = subscription.Id }, resultDto);
        }

        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing subscription.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Subscription not found.")]
        public async Task<IActionResult> UpdateSubscription(int id, [FromBody] UpdateSubscriptionDto dto)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound(new { message = "Subscription not found!" });

            _mapper.Map(dto, subscription);
            subscription.UpdatedAt = DateTime.UtcNow;

            _context.Entry(subscription).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a subscription by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Subscription not found.")]
        public async Task<IActionResult> DeleteSubscription(int id)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var subscription = await _context.Subscriptions.FindAsync(id);
            if (subscription == null)
                return NotFound(new { message = "Subscription not found!" });

            _context.Subscriptions.Remove(subscription);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
