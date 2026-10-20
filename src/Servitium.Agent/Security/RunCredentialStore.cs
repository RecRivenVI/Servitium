using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Hosting;
using Servitium.Application.Abstractions;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.Security;

/// <summary>在 Agent 本机保存仅供进程启动使用的 Windows 凭据。</summary>
internal sealed class RunCredentialStore(AgentHostOptions options, ISecretProtector protector) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<IReadOnlyList<RunCredentialSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return [.. document.Items
                .OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(item => new RunCredentialSummary(item.Alias, item.UserName, item.Domain, item.UpdatedAt))];
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task<RunCredential> ResolveAsync(string alias, CancellationToken cancellationToken)
    {
        ValidateAlias(alias);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            StoredRunCredential item = document.Items.SingleOrDefault(candidate =>
                string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException("run-credential-not-found", "The configured Agent run credential does not exist.");
            return new RunCredential(item.UserName, item.Domain, item.Password);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task AddOrReplaceAsync(
        string alias,
        string userName,
        string? domain,
        string password,
        CancellationToken cancellationToken)
    {
        ValidateAlias(alias);
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 256 || userName.Any(char.IsControl) ||
            domain is { Length: > 256 } || domain?.Any(char.IsControl) == true ||
            string.IsNullOrEmpty(password) || password.Length > 1024 || password.Contains('\0', StringComparison.Ordinal))
        {
            throw new DomainException("validation-failed", "The Windows run credential is invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            _ = document.Items.RemoveAll(candidate => string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase));
            document.Items.Add(new StoredRunCredential(alias, userName.Trim(), string.IsNullOrWhiteSpace(domain) ? null : domain.Trim(), password, DateTimeOffset.UtcNow));
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task DeleteAsync(string alias, CancellationToken cancellationToken)
    {
        ValidateAlias(alias);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (document.Items.RemoveAll(candidate => string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                throw new DomainException("run-credential-not-found", "The Agent run credential does not exist.");
            }

            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<CredentialDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        string path = StorePath();
        if (!File.Exists(path))
        {
            return new CredentialDocument([]);
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = protector.Unprotect(protectedBytes);
        try
        {
            return JsonSerializer.Deserialize<CredentialDocument>(plaintext)
                ?? throw new InvalidDataException("The Agent run credential store is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Agent run credential store is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task SaveCoreAsync(CredentialDocument document, CancellationToken cancellationToken)
    {
        string path = StorePath();
        string directory = Path.GetDirectoryName(path)!;
        EnsureProtectedDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(document);
        byte[] protectedBytes;
        try
        {
            protectedBytes = protector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string temporary = path + ".new";
        try
        {
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string StorePath()
    {
        return Path.Combine(options.DataRoot, "config", "run-credentials.dat");
    }

    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 64 || alias.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new DomainException("validation-failed", "The run credential alias is invalid.");
        }
    }

    private static void EnsureProtectedDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier serviceSid = identity.User ?? throw new InvalidOperationException("The Agent service account has no SID.");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            serviceSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private sealed record CredentialDocument(List<StoredRunCredential> Items);
    private sealed record StoredRunCredential(string Alias, string UserName, string? Domain, string Password, DateTimeOffset UpdatedAt);
}

internal sealed record RunCredentialSummary(string Alias, string UserName, string? Domain, DateTimeOffset UpdatedAt);

internal sealed record RunCredential(string UserName, string? Domain, string Password);
