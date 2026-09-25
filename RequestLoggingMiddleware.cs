namespace garge_api
{
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Cookie",
            "Set-Cookie",
            "Proxy-Authorization",
            "X-Api-Key",
            "Idempotency-Key"
        };

        public async Task InvokeAsync(HttpContext context)
        {
            // Probes run every few seconds for the life of the pod.
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await _next(context);
                return;
            }

            _logger.LogInformation("Incoming Request {@LogData}", new { context.Request.Method, context.Request.Path });

            foreach (var header in context.Request.Headers)
            {
                if (SensitiveHeaders.Contains(header.Key)) continue;
                _logger.LogDebug("Header {@LogData}", new { header.Key, Value = header.Value.ToString() });
            }

            await _next(context);
        }
    }
}
