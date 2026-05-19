using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace garge_api.Authorization
{
    public class JsonForbidResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden
                && authorizeResult.AuthorizationFailure?.FailedRequirements
                    .Any(r => r is ActiveSubscriptionRequirement) == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "You need an active subscription to claim more sensors or switches. Your existing devices keep working. See Shop for plans."
                });
                return;
            }

            await _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
