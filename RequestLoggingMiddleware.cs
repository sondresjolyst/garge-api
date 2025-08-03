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
            _logger.LogInformation("Incoming Request {@LogData}", new { Method = context.Request.Method, Path = context.Request.Path });

            foreach (var header in context.Request.Headers)
            {
                _logger.LogDebug("Header {@LogData}", new { Key = header.Key, Value = header.Value.ToString() });
            }

            await _next(context);
        }
    }
}
