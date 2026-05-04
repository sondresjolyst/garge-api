using Microsoft.AspNetCore.Mvc;
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
    }
}
