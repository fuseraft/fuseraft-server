using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class WebOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-keys-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AKeyCertificate_EncryptsTheKeysOnDisk_AndStillReadsThem()
    {
        var pfx = WritePfx(RSA.Create(2048), "pw");
        var keys = Path.Combine(_dir, "keys");

        var secret = Protector(keys, WebOptions.LoadKeyCertificate(pfx, "pw")).Protect("daemon token");

        var keyFile = File.ReadAllText(Directory.GetFiles(keys).Single());
        Assert.Contains("EncryptedData", keyFile);
        Assert.Equal("daemon token", Protector(keys, WebOptions.LoadKeyCertificate(pfx, "pw")).Unprotect(secret));
    }

    [Fact]
    public void AWrongPassword_SaysWhichVariableToSet()
    {
        var pfx = WritePfx(RSA.Create(2048), "pw");

        var ex = Assert.Throws<InvalidOperationException>(() => WebOptions.LoadKeyCertificate(pfx, "nope"));

        Assert.Contains(WebOptions.KeyCertificatePasswordVariable, ex.Message);
    }

    [Fact]
    public void ANonRsaCertificate_IsRefused()
    {
        var pfx = WritePfx(ECDsa.Create(ECCurve.NamedCurves.nistP256), "pw");

        Assert.Contains("RSA", Assert.Throws<InvalidOperationException>(() => WebOptions.LoadKeyCertificate(pfx, "pw")).Message);
    }

    private static IDataProtector Protector(string keys, X509Certificate2 cert)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("fuseraft-web")
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .ProtectKeysWithCertificate(cert)
            .UnprotectKeysWithAnyCertificate(cert);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    private string WritePfx(AsymmetricAlgorithm key, string password)
    {
        var request = key switch
        {
            RSA rsa     => new CertificateRequest("CN=fuseraft-web-keys", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest("CN=fuseraft-web-keys", ecdsa, HashAlgorithmName.SHA256),
            _           => throw new ArgumentException("Unsupported key", nameof(key)),
        };
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        key.Dispose();
        return path;
    }
}
