using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Servitium.Server.Security;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class TlsCertificateValidatorTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1160")]
    public void 证书校验_接受受信有效私钥并拒绝过期与无私钥证书()
    {
        DateTimeOffset now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        using X509Certificate2 valid = CreateCertificate(now.AddDays(-1), now.AddDays(30));
        X509Certificate2Collection trust = [valid];

        TlsCertificateValidationResult accepted = TlsCertificateValidator.Validate(valid, now, trust);
        Assert.True(accepted.Succeeded, accepted.Summary);

        using X509Certificate2 expired = CreateCertificate(now.AddDays(-30), now.AddDays(-1));
        TlsCertificateValidationResult expiredResult = TlsCertificateValidator.Validate(expired, now, [expired]);
        Assert.False(expiredResult.Succeeded);
        Assert.Equal("certificate-expired", expiredResult.Summary);

        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(valid.Export(X509ContentType.Cert));
        TlsCertificateValidationResult missingKey = TlsCertificateValidator.Validate(publicOnly, now, trust);
        Assert.False(missingKey.Succeeded);
        Assert.Equal("certificate-private-key-missing", missingKey.Summary);
    }

    private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=servitium-synthetic.invalid",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        OidCollection usages = [new Oid("1.3.6.1.5.5.7.3.1")];
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        using X509Certificate2 created = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
