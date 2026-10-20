using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

namespace Servitium.Server.Hosting;

/// <summary>把数据库中的重启生效设置投影到 Server 宿主选项。</summary>
internal static class PersistedServerHostSettings
{
    internal static void Apply(ServerHostOptions options, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyDictionary<string, string?> values = PersistedSettingsReader.ReadGroup(databasePath, "security");
        if (values.Count == 0)
        {
            return;
        }

        options.ListenAddress = Value(values, "listenAddress") ?? options.ListenAddress;
        options.ListenPort = ParseInt(values, "listenPort", options.ListenPort, 1, 65535);
        options.PathBase = NormalizePathBase(Value(values, "pathBase"));
        options.PublicBaseUri = ParsePublicBaseUri(Value(values, "publicBaseUrl"));
        options.AllowedOrigins = ParseOrigins(Value(values, "corsOrigins"));
        options.ResponseCompressionEnabled = ParseBool(values, "responseCompressionEnabled", options.ResponseCompressionEnabled);
        options.TrustedProxyMode = ParseBool(values, "trustedProxyMode", options.TrustedProxyMode);
        options.TrustedProxyNetworks = Split(Value(values, "trustedProxyNetworks"));
        options.ForwardedForHeader = Value(values, "forwardedForHeader") ?? options.ForwardedForHeader;
        options.TlsCertificateSource = Value(values, "tlsCertificateSource") ?? "none";
        options.TlsCertificatePath = Value(values, "tlsCertificatePath");
        options.TlsCertificatePassword = Unprotect(Value(values, "tlsCertificatePassword"));
        options.TlsCertificateThumbprint = NormalizeThumbprint(Value(values, "tlsCertificateThumbprint"));
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.StartsWith("protected:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A persisted TLS password is not protected.");
        }

        byte[] protectedBytes = Convert.FromBase64String(value["protected:".Length..]);
        byte[] plaintext = new DpapiSecretProtector().Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string[] ParseOrigins(string? value)
    {
        return [.. Split(value).Select(item =>
        {
            return !Uri.TryCreate(item, UriKind.Absolute, out Uri? origin) ||
                (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) ||
                origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
                item.Contains('*', StringComparison.Ordinal)
                ? throw new InvalidOperationException("A persisted CORS origin is invalid.")
                : origin.GetLeftPart(UriPartial.Authority);
        }).Distinct(StringComparer.Ordinal)];
    }

    private static Uri? ParsePublicBaseUri(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            ? throw new InvalidOperationException("The persisted public base URL is invalid.")
            : new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static string NormalizePathBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "/")
        {
            return string.Empty;
        }

        string normalized = value.TrimEnd('/');
        return !normalized.StartsWith('/') || normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains('%', StringComparison.Ordinal)
            ? throw new InvalidOperationException("The persisted path base is invalid.")
            : normalized;
    }

    private static string? NormalizeThumbprint(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : string.Concat(value.Where(Uri.IsHexDigit)).ToUpperInvariant();
    }

    private static string[] Split(string? value)
    {
        return value?.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string?> values, string key, bool fallback)
    {
        return bool.TryParse(Value(values, key), out bool parsed) ? parsed : fallback;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key, int fallback, int minimum, int maximum)
    {
        return int.TryParse(Value(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value : null;
    }
}
