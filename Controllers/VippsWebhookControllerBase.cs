using garge_api.Models;
using garge_api.Models.Webhook;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace garge_api.Controllers
{
    public abstract class VippsWebhookControllerBase : ControllerBase
    {
        protected static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        protected static async Task<string> ReadRawBodyAsync(HttpRequest request)
        {
            if (!request.Body.CanSeek)
                request.EnableBuffering();

            request.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Seek(0, SeekOrigin.Begin);
            return body;
        }

        protected async Task<bool> TryRecordEventAsync(
            ApplicationDbContext db, string source, string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return true;

            var exists = await db.ProcessedWebhookEvents
                .AnyAsync(e => e.Source == source && e.Id == eventId);
            if (exists) return false;

            db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                Id = eventId,
                Source = source
            });
            return true;
        }
    }
}
