using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class DataProtectionCertificateTests
{
    private static string NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=garge-api-dataprotection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
    }

    private static IConfiguration Config(string? certificate) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataProtectionCertificate.ConfigKey] = certificate })
            .Build();

    private static IDataProtectionProvider Provider(DirectoryInfo keys, IConfiguration config, bool required)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .PersistKeysToFileSystem(keys)
            .SetApplicationName("garge-api-tests")
            .ProtectKeysWithConfiguredCertificate(config, required);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static DirectoryInfo TempKeys() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "garge-api-tests", "keys", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Certificate_EncryptsTheStoredKeys_AndTheyStillWork()
    {
        var keys = TempKeys();
        var config = Config(NewCertificate());

        var protectedText = Provider(keys, config, required: true).CreateProtector("test").Protect("webhook-secret");

        var xml = File.ReadAllText(Assert.Single(keys.GetFiles("*.xml")).FullName);
        Assert.Contains("encryptedSecret", xml);
        Assert.DoesNotContain("<masterKey", xml);
        Assert.Equal("webhook-secret", Provider(keys, config, required: true).CreateProtector("test").Unprotect(protectedText));
    }

    [Fact]
    public void Certificate_KeepsKeysStoredBeforeItWasSetReadable()
    {
        var keys = TempKeys();
        var protectedText = Provider(keys, Config(null), required: false).CreateProtector("test").Protect("webhook-secret");

        Assert.Equal("webhook-secret", Provider(keys, Config(NewCertificate()), required: true).CreateProtector("test").Unprotect(protectedText));
    }

    [Fact]
    public void MissingCertificate_StopsTheAppWhenRequired()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Provider(TempKeys(), Config(null), required: true));
        Assert.Contains(DataProtectionCertificate.ConfigKey, ex.Message);
    }

    [Fact]
    public void MissingCertificate_StoresPlainKeysWhenNotRequired()
    {
        var keys = TempKeys();
        Provider(keys, Config(""), required: false).CreateProtector("test").Protect("x");

        Assert.DoesNotContain("encryptedSecret", File.ReadAllText(Assert.Single(keys.GetFiles("*.xml")).FullName));
    }

    [Fact]
    public void BadCertificate_IsRejectedWithTheSettingName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Provider(TempKeys(), Config("not base64!"), required: true));
        Assert.Contains(DataProtectionCertificate.ConfigKey, ex.Message);
    }

    [Fact]
    public void CertificateWithoutPrivateKey_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        using var full = new CertificateRequest("CN=public-only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));
        var encoded = Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12));

        var ex = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Load(encoded));
        Assert.Contains("private key", ex.Message);
    }
}
