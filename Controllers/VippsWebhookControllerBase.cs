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

        /// <summary>A webhook registration: its id, its protected secret and its environment.</summary>
        protected readonly record struct Registration(string? ProtectedSecret, bool IsTest);

        /// <summary>
        /// Verifies the signature against the secret of each registration. Returns the environment of
        /// the registration whose secret signed it.
        /// </summary>
        protected static (WebhookVerifyResult Result, bool IsTest) VerifySignature(
            IVippsService vipps, IWebhookSecretProtector protector, HttpRequest request, string rawBody,
            params Registration[] registrations)
        {
            var result = WebhookVerifyResult.MissingSecret;
            foreach (var registration in registrations.Where(r => !string.IsNullOrEmpty(r.ProtectedSecret)))
            {
                result = vipps.VerifyWebhookSignature(request, rawBody, protector.Unprotect(registration.ProtectedSecret!));
                if (result == WebhookVerifyResult.Valid) return (result, registration.IsTest);
            }
            return (result, false);
        }

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

        /// <summary>
        /// The key an event is marked handled under. Test and production keep separate keys, so an
        /// event from one environment cannot mark an event of the other as handled.
        /// </summary>
        protected static string EventKey(string eventId, bool sentFromTest) => sentFromTest ? $"test:{eventId}" : eventId;

        /// <summary>Whether an earlier delivery of this event was already handled.</summary>
        protected static async Task<bool> AlreadyProcessedAsync(ApplicationDbContext db, string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return false;
            return await db.ProcessedWebhookEvents.AsNoTracking().AnyAsync(e => e.Id == eventId);
        }

        /// <summary>
        /// Saves the event's changes together with its processed marker, in one SaveChanges, so either
        /// both are stored or neither is. A delivery that fails before this point is not marked, and
        /// Vipps' redelivery is handled again. Returns false when a concurrent delivery of the same
        /// event saved first, in which case nothing from this delivery is stored.
        /// </summary>
        protected static async Task<bool> SaveProcessedAsync(ApplicationDbContext db, string source, string eventId)
        {
            ProcessedWebhookEvent? marker = null;
            if (!string.IsNullOrEmpty(eventId))
            {
                marker = new ProcessedWebhookEvent { Id = eventId, Source = source };
                db.ProcessedWebhookEvents.Add(marker);
            }

            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            // PostgreSQL reports the duplicate marker as a DbUpdateException, the in-memory provider as an
            // ArgumentException. Either way it only counts as a lost race once the other marker is found.
            catch (Exception ex) when (marker != null && ex is DbUpdateException or InvalidOperationException or ArgumentException)
            {
                var raceLost = await db.ProcessedWebhookEvents
                    .AsNoTracking()
                    .AnyAsync(e => e.Id == eventId);
                if (!raceLost) throw;

                foreach (var entry in db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
                return false;
            }
        }

        /// <summary>
        /// Whether the payload's merchant serial number is the one this record was created with. A
        /// missing number does not match, so an event for another merchant cannot pass by leaving it out.
        /// </summary>
        protected static bool MerchantMatches(string? payloadMsn, string? expectedMsn)
            => !string.IsNullOrEmpty(payloadMsn) && !string.IsNullOrEmpty(expectedMsn) && payloadMsn == expectedMsn;

        /// <summary>
        /// Acknowledges a signed event that failed a check, without applying it. It is marked handled and
        /// answered with 200, because Vipps retries any 4xx or 5xx for seven days and holds back the
        /// payment's later events until one succeeds. The error log is where it gets noticed.
        /// </summary>
        protected static async Task<IActionResult> RejectAsync(ApplicationDbContext db, string source, string eventId)
        {
            await SaveProcessedAsync(db, source, eventId);
            return new OkResult();
        }
    }
}
