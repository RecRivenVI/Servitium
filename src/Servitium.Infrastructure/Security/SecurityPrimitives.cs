using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Konscious.Security.Cryptography;

using Servitium.Application.Abstractions;

namespace Servitium.Infrastructure.Security;

/// <summary>系统 UTC 时钟。</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>加密随机 Guid 工厂。</summary>
public sealed class GuidFactory : IGuidFactory
{
    /// <inheritdoc />
    public Guid Create()
    {
        return Guid.NewGuid();
    }
}

/// <summary>密码学安全的不透明令牌生成器。</summary>
public sealed class SecureTokenGenerator : ITokenGenerator
{
    /// <inheritdoc />
    public string Generate(int entropyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entropyBytes, 16);
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(entropyBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>使用固定安全基线参数的 Argon2id 密码哈希器。</summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int MemorySize = 65_536;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Derive(password, salt, MemorySize, Iterations, Parallelism, HashSize);
        return FormattableString.Invariant($"argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <inheritdoc />
    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(encodedHash);
        try
        {
            string[] parts = encodedHash.Split('$');
            if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
            {
                return false;
            }

            if (!TryParse(encodedHash, out ArgonParameters parsed))
            {
                return false;
            }

            byte[] actual = Derive(password, parsed.Salt, parsed.Memory, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);
            return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool NeedsRehash(string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        return !TryParse(encodedHash, out ArgonParameters parsed) ||
               parsed.Memory != MemorySize ||
               parsed.Iterations != Iterations ||
               parsed.Parallelism != Parallelism ||
               parsed.Salt.Length != SaltSize ||
               parsed.Hash.Length != HashSize;
    }

    private static bool TryParse(string encodedHash, out ArgonParameters parsed)
    {
        parsed = default;
        try
        {
            string[] parts = encodedHash.Split('$');
            if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
            {
                return false;
            }

            string[] parameters = parts[2].Split(',');
            if (parameters.Length != 3)
            {
                return false;
            }

            int memory = int.Parse(parameters.Single(value => value.StartsWith("m=", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture);
            int iterations = int.Parse(parameters.Single(value => value.StartsWith("t=", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture);
            int parallelism = int.Parse(parameters.Single(value => value.StartsWith("p=", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture);
            byte[] salt = Convert.FromBase64String(parts[3]);
            byte[] hash = Convert.FromBase64String(parts[4]);
            if (memory is < 8192 or > 262144 || iterations is < 1 or > 10 || parallelism is < 1 or > 16 || salt.Length is < 8 or > 64 || hash.Length is < 16 or > 64)
            {
                return false;
            }

            parsed = new(memory, iterations, parallelism, salt, hash);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int memory, int iterations, int parallelism, int length)
    {
        using Argon2id algorithm = new(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return algorithm.GetBytes(length);
    }

    private readonly record struct ArgonParameters(int Memory, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}

/// <summary>TOTP 可热更新策略。</summary>
public sealed class TotpOptions
{
    /// <summary>允许的前后时间步数。</summary>
    public int PermittedDriftSteps
    {
        get => Volatile.Read(ref field);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4);
            Volatile.Write(ref field, value);
        }
    } = 1;
}

/// <summary>RFC 6238 的六位、30 秒、HMAC-SHA1 TOTP 实现。</summary>
public sealed class TotpService(TotpOptions options) : ITotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>使用固定漂移窗口创建实例，供独立应用与测试使用。</summary>
    public TotpService(int permittedDriftSteps)
        : this(new TotpOptions { PermittedDriftSteps = permittedDriftSteps })
    {
    }

    /// <inheritdoc />
    public string GenerateSecret()
    {
        return EncodeBase32(RandomNumberGenerator.GetBytes(20));
    }

    /// <inheritdoc />
    public bool Verify(string secret, string code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 6 || code.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = DecodeBase32(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        long counter = now.ToUnixTimeSeconds() / 30;
        byte[] supplied = Encoding.ASCII.GetBytes(code);
        int permittedDriftSteps = options.PermittedDriftSteps;
        for (int offset = -permittedDriftSteps; offset <= permittedDriftSteps; offset++)
        {
            string expected = Compute(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(supplied, Encoding.ASCII.GetBytes(expected)))
            {
                return true;
            }
        }

        return false;
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 interoperability requires HMAC-SHA1 for the configured TOTP profile.")]
    private static string Compute(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        byte[] hash = HMACSHA1.HashData(key, message);
        int offset = hash[^1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) |
                     (hash[offset + 1] << 16) |
                     (hash[offset + 2] << 8) |
                     hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string EncodeBase32(byte[] data)
    {
        StringBuilder result = new(((data.Length * 8) + 4) / 5);
        int buffer = data[0];
        int next = 1;
        int bitsLeft = 8;
        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer = (buffer << 8) | data[next++];
                    bitsLeft += 8;
                }
                else
                {
                    buffer <<= 5 - bitsLeft;
                    bitsLeft = 5;
                }
            }

            _ = result.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 0x1f]);
            bitsLeft -= 5;
        }

        return result.ToString();
    }

    private static byte[] DecodeBase32(string value)
    {
        string normalized = value.Trim().TrimEnd('=').ToUpperInvariant();
        byte[] result = new byte[normalized.Length * 5 / 8];
        int buffer = 0;
        int bitsLeft = 0;
        int index = 0;
        foreach (char character in normalized)
        {
            int digit = Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0)
            {
                throw new FormatException("The TOTP secret is not valid Base32.");
            }

            buffer = (buffer << 5) | digit;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                result[index++] = (byte)(buffer >> (bitsLeft - 8));
                bitsLeft -= 8;
            }
        }

        return result;
    }
}

/// <summary>使用 Windows 计算机范围 DPAPI 保护静态秘密。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Servitium.Server.v1");

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.LocalMachine)
            : throw new PlatformNotSupportedException("Servitium secret protection requires Windows DPAPI.");
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        return OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.LocalMachine)
            : throw new PlatformNotSupportedException("Servitium secret protection requires Windows DPAPI.");
    }
}
