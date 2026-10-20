using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Application.Nodes;
using Servitium.Contracts.Nodes;
using Servitium.Infrastructure.Networking;

namespace Servitium.Agent.Hosting;

/// <summary>验证并原子持久化 Agent 节点级运行配置。</summary>
internal sealed class AgentNodeConfigurationStore(
    AgentHostOptions hostOptions,
    AgentStorageOptions storageOptions,
    IWindowsPathPolicy pathPolicy,
    AllowedAuthoritySet allowedAuthorities) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateNodeRequest _current = new(hostOptions.NodeName, storageOptions.ManagedInstanceRoot, null, null, JavaDataDirectory: storageOptions.JavaRoot);

    internal UpdateNodeRequest Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string path = ConfigurationPath();
        if (!File.Exists(path))
        {
            return;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        UpdateNodeRequest? stored = await JsonSerializer.DeserializeAsync<UpdateNodeRequest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            Validate(stored);
            allowedAuthorities.Replace(stored.PrivateSourceAuthorities ?? []);
            Volatile.Write(ref _current, stored);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    internal async Task<UpdateNodeRequest> UpdateAsync(UpdateNodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = ConfigurationPath();
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".new";
            try
            {
                using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, request, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, true);
                allowedAuthorities.Replace(request.PrivateSourceAuthorities ?? []);
                Volatile.Write(ref _current, request);
                return request;
            }
            finally
            {
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

    private void Validate(UpdateNodeRequest request)
    {
        NodeConfigurationValidator.Validate(request);

        if (request.DefaultInstancePath is { } root)
        {
            string probe = Path.Combine(root, "00000000000000000000000000000000");
            _ = pathPolicy.ValidateOwnedWorkingDirectory(probe, root);
            _ = Directory.CreateDirectory(root);
            WindowsPathPolicy.EnsureNoReparsePoint(root);
        }

        if (request.JavaDataDirectory is { } javaRoot)
        {
            string probe = Path.Combine(javaRoot, "00000000000000000000000000000000");
            _ = pathPolicy.ValidateOwnedWorkingDirectory(probe, javaRoot);
            _ = Directory.CreateDirectory(javaRoot);
            WindowsPathPolicy.EnsureNoReparsePoint(javaRoot);
        }

    }

    private string ConfigurationPath()
    {
        return Path.Combine(storageOptions.DataRoot, "config", "node-settings.json");
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
