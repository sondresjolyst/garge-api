using System.Reflection;
using garge_api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Locks the routes <see cref="AdminController"/> serves. garge-app manages roles through AppKit.Auth's
/// user routes, so the controller has no base route and no <c>/api/roles</c> endpoints.
/// </summary>
public class AdminControllerRoutesTests
{
    private static IEnumerable<string> Routes() =>
        typeof(AdminController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods.Select(verb => $"{verb} {a.Template}")));

    [Fact]
    public void ServesOnlyTheAdminSettingsStatsDevicesAndUserDeleteRoutes()
    {
        Assert.Equal(
            [
                "DELETE /api/users/{id}",
                "GET /api/admin/devices",
                "GET /api/admin/security-settings",
                "GET /api/admin/settings",
                "GET /api/admin/stats",
                "PUT /api/admin/security-settings",
                "PUT /api/admin/settings",
            ],
            Routes().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HasNoBaseRoute()
    {
        Assert.Null(typeof(AdminController).GetCustomAttribute<RouteAttribute>());
    }

    [Fact]
    public void IsForAdminsOnly()
    {
        Assert.Equal("Admin", typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }
}
