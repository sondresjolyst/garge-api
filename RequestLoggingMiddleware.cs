namespace garge_api
{
    public class RequestLoggingMiddleware(RequestDelegate next)
    {
        private readonly RequestDelegate _next = next;

        public async Task InvokeAsync(HttpContext context)
        {
            // Log the request headers
            Console.WriteLine("Incoming Request:");
            foreach (var header in context.Request.Headers)
            {
                Console.WriteLine($"{header.Key}: {header.Value}");
            }

            // Call the next middleware in the pipeline
            await _next(context);
        }
    }
}