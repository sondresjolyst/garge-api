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

        public async Task InvokeAsync(HttpContext context)
        {
            _logger.LogInformation("Incoming Request {@LogData}", new { context.Request.Method, context.Request.Path });

            foreach (var header in context.Request.Headers)
            {
                _logger.LogDebug("Header {@LogData}", new { header.Key, Value = header.Value.ToString() });
            }

            await _next(context);
        }
    }
}
