using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Servitium.Server.Security;

/// <summary>按服务端 TLS 用途校验证书、私钥、有效期与信任链。</summary>
internal static class TlsCertificateValidator
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    internal static TlsCertificateValidationResult Validate(
        X509Certificate2 certificate,
        DateTimeOffset now,
        X509Certificate2Collection? customTrustRoots = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            return new(false, "certificate-private-key-missing");
        }

        DateTime utcNow = now.UtcDateTime;
        if (certificate.NotAfter.ToUniversalTime() <= utcNow)
        {
            return new(false, "certificate-expired");
        }

        if (certificate.NotBefore.ToUniversalTime() > utcNow)
        {
            return new(false, "certificate-not-yet-valid");
        }

        X509EnhancedKeyUsageExtension? enhancedKeyUsage = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SingleOrDefault();
        if (enhancedKeyUsage is not null &&
            !enhancedKeyUsage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ServerAuthenticationOid))
        {
            return new(false, "certificate-server-auth-missing");
        }

        using X509Chain chain = new();
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = utcNow;
        _ = chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        if (customTrustRoots is { Count: > 0 })
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(customTrustRoots);
        }

        try
        {
            return chain.Build(certificate)
                ? new(true, "certificate-valid")
                : new(false, "certificate-untrusted");
        }
        catch (CryptographicException)
        {
            return new(false, "certificate-invalid");
        }
    }

    internal static X509Certificate2 LoadConfigured(
        string source,
        string? filePath,
        string? password,
        string? thumbprint)
    {
        return source switch
        {
            "file" when !string.IsNullOrWhiteSpace(filePath) && Path.IsPathFullyQualified(filePath) =>
                X509CertificateLoader.LoadPkcs12FromFile(filePath, password, X509KeyStorageFlags.EphemeralKeySet),
            "store" when !string.IsNullOrWhiteSpace(thumbprint) => FindStoreCertificate(thumbprint),
            "file" => throw new CryptographicException("The TLS certificate path is invalid."),
            "store" => throw new CryptographicException("The TLS certificate thumbprint is missing."),
            _ => throw new CryptographicException("The TLS certificate source is invalid."),
        };
    }

    private static X509Certificate2 FindStoreCertificate(string thumbprint)
    {
        string normalized = string.Concat(thumbprint.Where(Uri.IsHexDigit)).ToUpperInvariant();
        foreach (StoreLocation location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using X509Store store = new(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            X509Certificate2Collection matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (matches.Count == 0)
            {
                continue;
            }

            X509Certificate2 selected = matches[0];
            foreach (X509Certificate2 match in matches.Cast<X509Certificate2>().Skip(1))
            {
                match.Dispose();
            }

            return selected;
        }

        throw new CryptographicException("The TLS certificate was not found in the Windows certificate store.");
    }
}

internal sealed record TlsCertificateValidationResult(bool Succeeded, string Summary);
