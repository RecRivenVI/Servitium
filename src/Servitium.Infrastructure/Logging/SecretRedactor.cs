using System;
using System.Text.RegularExpressions;

namespace Servitium.Infrastructure.Logging;

/// <summary>对服务日志与诊断材料应用统一的保守秘密脱敏。</summary>
public static partial class SecretRedactor
{
    /// <summary>替换带常见秘密键名的值、Bearer 凭据与受保护密文。</summary>
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = SecretAssignment().Replace(value, "$1$2••••••");
        redacted = BearerCredential().Replace(redacted, "Bearer ••••••");
        return ProtectedPayload().Replace(redacted, "protected:••••••");
    }

    [GeneratedRegex("(?i)((?:password|secret|token|api[-_ ]?key|credential|authorization|cookie|csrf|rcon)[a-z0-9_.-]*)(\\\"?\\s*[:=]\\s*\\\"?)([^\\\",;\\s}]+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BearerCredential();

    [GeneratedRegex("(?i)protected:[A-Za-z0-9+/=]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProtectedPayload();
}
