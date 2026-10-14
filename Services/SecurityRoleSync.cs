using garge_api.Models.Admin;
using SjolystInnovation.AppKit.Auth;

namespace garge_api.Services
{
    /// <summary>
    /// Re-checks a user's Garge Security sensors after an admin changes their roles, so security a role no
    /// longer grants turns off.
    /// </summary>
    public class SecurityRoleSync(ISecurityModeService security, ILogger<SecurityRoleSync> logger) : IUserRolesChangedHandler<User>
    {
        public async Task OnRolesChangedAsync(User user, CancellationToken cancellationToken)
        {
            try
            {
                await security.ReconcileUserAsync(user.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                // The role change is saved. A failed reconcile is retried by the next sensor check.
                logger.LogWarning(ex, "Garge Security reconcile failed after a role change for {TargetUserId}", user.Id);
            }
        }
    }
}
