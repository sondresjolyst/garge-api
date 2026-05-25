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

        /// <summary>
        /// True when the caller holds at least one of the supplied roles. Matching is
        /// case-insensitive so legacy/lower-cased role claims continue to authorize. Pass the
        /// canonical names from <see cref="garge_api.Constants.RoleNames"/>.
        /// </summary>
        public static bool IsInAnyRole(this ClaimsPrincipal user, params string[] roles)
        {
            if (roles is null || roles.Length == 0)
                return false;

            var userRoles = user.FindAll(ClaimTypes.Role).Select(r => r.Value);
            return userRoles.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase));
        }
    }
}
