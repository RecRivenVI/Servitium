using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Hosting;
using Servitium.Application.Abstractions;

namespace Servitium.Agent.Security;

/// <summary>以 DPAPI 与显式 Windows ACL 持久化节点长期凭据。</summary>
internal sealed class AgentCredentialStore(AgentHostOptions options, ISecretProtector protector) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = CredentialPath();
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] protectedData = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] plaintext = protector.Unprotect(protectedData);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task SaveAsync(string credential, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = CredentialPath();
            string directory = Path.GetDirectoryName(path)!;
            EnsureProtectedDirectory(directory);
            byte[] plaintext = Encoding.UTF8.GetBytes(credential);
            byte[] protectedData;
            try
            {
                protectedData = protector.Protect(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            string temporary = path + ".new";
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedData, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedData);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private string CredentialPath()
    {
        return Path.Combine(options.DataRoot, "config", "node.credential");
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
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(serviceSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
