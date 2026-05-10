using System.Security.Claims;

namespace garge_api.Helpers
{
    public static class ClaimsPrincipalExtensions
    {
        /// <summary>The opaque user id from the NameIdentifier claim.</summary>
        public static string? UserId(this ClaimsPrincipal user) =>
            user.FindFirstValue(ClaimTypes.NameIdentifier);

        /// <summary>True when the caller's NameIdentifier matches the supplied id.</summary>
        public static bool IsCallerOf(this ClaimsPrincipal user, string id) =>
            user.UserId() == id;
    }
}
