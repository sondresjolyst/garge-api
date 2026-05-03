using garge_api.Dtos.Push;
using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace garge_api.Controllers
{
    /// <summary>
    /// Manages Web Push subscriptions for offline sensor alerts.
    /// </summary>
    [ApiController]
    [Route("api/push-subscriptions")]
    [EnableCors("AllowAllOrigins")]
    public class PushSubscriptionController(
        ApplicationDbContext db,
        IConfiguration configuration,
        IWebPushService webPushService) : ControllerBase
    {
        /// <summary>Returns the VAPID public key needed by the browser to subscribe.</summary>
        [HttpGet("vapid-public-key")]
        [AllowAnonymous]
        public IActionResult GetVapidPublicKey()
        {
            var key = configuration["Vapid:PublicKey"];
            if (string.IsNullOrEmpty(key))
                return StatusCode(503, new { message = "Push notifications not configured." });
            return Ok(new { publicKey = key });
        }

        /// <summary>Saves or updates a push subscription for the authenticated user.</summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> Subscribe([FromBody] CreatePushSubscriptionDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Forbid();

            var existing = await db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == dto.Endpoint);

            if (existing != null)
            {
                existing.P256dh = dto.P256dh;
                existing.Auth = dto.Auth;
            }
            else
            {
                db.PushSubscriptions.Add(new PushSubscription
                {
                    UserId = userId,
                    Endpoint = dto.Endpoint,
                    P256dh = dto.P256dh,
                    Auth = dto.Auth,
                });
            }

            await db.SaveChangesAsync();
            return Ok();
        }

        /// <summary>Sends a test push notification to the authenticated user.</summary>
        [HttpPost("send-test")]
        [Authorize]
        public async Task<IActionResult> SendTest()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Forbid();

            await webPushService.SendAsync(userId, "Test notification", "Push notifications are working!", CancellationToken.None);
            return Ok(new { message = "Sent." });
        }

        /// <summary>Removes a push subscription for the authenticated user.</summary>
        [HttpDelete]
        [Authorize]
        public async Task<IActionResult> Unsubscribe([FromBody] DeletePushSubscriptionDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Forbid();

            var sub = await db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == dto.Endpoint);

            if (sub != null)
            {
                db.PushSubscriptions.Remove(sub);
                await db.SaveChangesAsync();
            }

            return NoContent();
        }
    }
}
