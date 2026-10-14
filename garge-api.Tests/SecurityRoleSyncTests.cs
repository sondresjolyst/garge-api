using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SecurityRoleSyncTests
{
    private static readonly User Target = new() { Id = "u1", FirstName = "Kari", LastName = "N" };

    [Fact]
    public async Task A_role_change_reconciles_the_users_security_sensors()
    {
        var security = new Mock<ISecurityModeService>();

        await new SecurityRoleSync(security.Object, NullLogger<SecurityRoleSync>.Instance).OnRolesChangedAsync(Target, TestContext.Current.CancellationToken);

        security.Verify(s => s.ReconcileUserAsync("u1", TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task A_failed_reconcile_does_not_fail_the_role_change()
    {
        var security = new Mock<ISecurityModeService>();
        security.Setup(s => s.ReconcileUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("mqtt down"));

        await new SecurityRoleSync(security.Object, NullLogger<SecurityRoleSync>.Instance).OnRolesChangedAsync(Target, TestContext.Current.CancellationToken);

        security.Verify(s => s.ReconcileUserAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
