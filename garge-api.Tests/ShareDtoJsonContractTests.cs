using System.Text.Json;
using System.Text.Json.Serialization;
using garge_api.Dtos.Common;
using garge_api.Models;
using Xunit;

namespace garge_api.Tests;

/// <summary>
/// Pins the wire contract of the consolidated share DTOs (<see cref="ShareRequestDto"/> /
/// <see cref="ShareRecipientDto"/> in Dtos/Common) that the frontend consumes. The API registers MVC's
/// JSON options with only <c>ReferenceHandler.IgnoreCycles</c> added, leaving the framework defaults:
/// camelCase property names and numeric enum serialization (no <see cref="JsonStringEnumConverter"/>).
/// These tests serialize with the same options and assert the exact property names and enum encoding so a
/// future naming-policy or enum-converter change that would break the frontend fails loudly here.
/// </summary>
public class ShareDtoJsonContractTests
{
    // Mirrors Program.cs: AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.ReferenceHandler =
    // ReferenceHandler.IgnoreCycles). MVC seeds its JsonSerializerOptions from JsonSerializerDefaults.Web
    // (camelCase + case-insensitive); no enum converter is registered, so enums emit as numbers.
    private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    private static HashSet<string> PropertyNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    [Fact]
    public void ShareRequestDto_SerializesWithCamelCaseNames()
    {
        var dto = new ShareRequestDto { Email = "viewer@x", Permission = SharePermission.Edit };

        var json = JsonSerializer.Serialize(dto, ApiOptions);

        Assert.Equal(new HashSet<string> { "email", "permission" }, PropertyNames(json));
    }

    [Fact]
    public void ShareRequestDto_PermissionSerializesAsNumber()
    {
        var dto = new ShareRequestDto { Email = "viewer@x", Permission = SharePermission.Edit };

        var json = JsonSerializer.Serialize(dto, ApiOptions);

        using var doc = JsonDocument.Parse(json);
        var permission = doc.RootElement.GetProperty("permission");
        Assert.Equal(JsonValueKind.Number, permission.ValueKind);
        Assert.Equal((int)SharePermission.Edit, permission.GetInt32());
    }

    [Fact]
    public void ShareRecipientDto_SerializesWithCamelCaseNames()
    {
        var dto = new ShareRecipientDto
        {
            UserId = "viewer",
            Email = "viewer@x",
            FirstName = "Vee",
            LastName = "Wer",
            Permission = SharePermission.Read,
            SharedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(dto, ApiOptions);

        Assert.Equal(
            new HashSet<string> { "userId", "email", "firstName", "lastName", "permission", "sharedAt" },
            PropertyNames(json));
    }

    [Fact]
    public void ShareRecipientDto_PermissionSerializesAsNumber()
    {
        var dto = new ShareRecipientDto
        {
            UserId = "viewer",
            Email = "viewer@x",
            FirstName = "Vee",
            LastName = "Wer",
            Permission = SharePermission.Read,
            SharedAt = DateTime.UtcNow,
        };

        var json = JsonSerializer.Serialize(dto, ApiOptions);

        using var doc = JsonDocument.Parse(json);
        var permission = doc.RootElement.GetProperty("permission");
        Assert.Equal(JsonValueKind.Number, permission.ValueKind);
        Assert.Equal((int)SharePermission.Read, permission.GetInt32());
    }

    [Fact]
    public void ShareRequestDto_DeserializesFromCamelCaseFrontendPayload()
    {
        // The frontend posts the share request with camelCase names; round-trip from that shape.
        const string body = """{ "email": "viewer@x", "permission": 1 }""";

        var dto = JsonSerializer.Deserialize<ShareRequestDto>(body, ApiOptions);

        Assert.NotNull(dto);
        Assert.Equal("viewer@x", dto!.Email);
        Assert.Equal(SharePermission.Edit, dto.Permission);
    }
}
