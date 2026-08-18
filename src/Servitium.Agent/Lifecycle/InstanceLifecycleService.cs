using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

using Servitium.Agent.Configurations;
using Servitium.Agent.ControlPlane;
using Servitium.Agent.Instances;
using Servitium.Agent.Java;
using Servitium.Agent.Paths;
using Servitium.Agent.Processes;
using Servitium.Agent.Security;
using Servitium.Agent.Terminals;
using Servitium.Agent.Hosting;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Events;
using Servitium.Domain.Common;

namespace Servitium.Agent.Lifecycle;

internal sealed class InstanceLifecycleService(
    InstanceRegistry instances,
    JavaRuntimeRegistry javaRuntimes,
    IWindowsPathPolicy pathPolicy,
    IClock clock,
    ISecretProtector secretProtector,
    MinecraftServerProtocolClient statusClient,
    RconClient rconClient,
    AgentNodeConfigurationStore? nodeConfiguration = null,
    AgentOutboxStore? outbox = null,
    RunCredentialStore? runCredentials = null) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly ConcurrentDictionary<Guid, Task> _exitMonitors = new();
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _restartHistory = new();
    private int _stopping;
    private bool _disposed;

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
        await RecoverPersistedStateAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LocalInstanceSnapshot> registrations = await instances.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (LocalInstanceSnapshot instance in registrations.Where(candidate => candidate.RuntimeSettings.AutoStart && candidate.Status == "Stopped"))
        {
            _ = StartAutomaticallyAsync(instance.Id, instance.RuntimeSettings.AutoStartDelaySeconds, cancellationToken);
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 1);
        ManagedProcess[] running = [.. _processes.Values];
        if (nodeConfiguration?.Current.GracefulShutdownEnabled == false)
        {
            foreach (ManagedProcess process in running)
            {
                process.IntentionalStop = true;
                process.Job.Terminate(1);
            }
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(nodeConfiguration?.Current.ShutdownWaitSeconds ?? 60));
            try
            {
                await Task.WhenAll(running.Select(process => StopForShutdownAsync(process, timeout.Token))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                foreach (ManagedProcess process in _processes.Values)
                {
                    process.IntentionalStop = true;
                    process.Job.Terminate(1);
                }
            }
        }

        Task[] monitors = [.. _exitMonitors.Values];
        if (monitors.Length > 0)
        {
            await Task.WhenAll(monitors).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Process and Job Object ownership transfers to ManagedProcess and is released by its exit monitor.")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The stored exit monitor owns ManagedProcess until the monitor completes and disposes it in finally.")]
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "Native ConPTY creation can throw before ManagedProcess ownership is established, so both ownership paths require cleanup.")]
    internal async Task<AgentInstanceRuntimeResponse> StartInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (instance.Status is not ("Stopped" or "Crashed"))
        {
            throw new DomainException("instance-not-stopped", "The instance must be stopped before it can be started.");
        }

        EnsureEulaAccepted(instance.WorkingDirectory);
        AgentJavaRuntimeResponse runtime = await javaRuntimes.FindAsync(instance.Launch.JavaRuntimeId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("java-runtime-invalid", "The configured Java runtime is not registered on this Agent.");
        RunCredential? runCredential = null;
        if (!string.IsNullOrWhiteSpace(instance.RuntimeSettings.RunCredentialAlias))
        {
            if (runCredentials is null)
            {
                throw new DomainException("run-credential-not-found", "The Agent run credential store is unavailable.");
            }

            runCredential = await runCredentials.ResolveAsync(instance.RuntimeSettings.RunCredentialAlias, cancellationToken).ConfigureAwait(false);
        }

        ProcessStartInfo startInfo = BuildStartInfo(instance, runtime, runCredential);
        WindowsJobObject job = new();
        Process? process = null;
        ConPtySession? conPty = null;
        TextWriter? input = null;
        ManagedProcess? managed = null;
        bool started = false;
        try
        {
            TerminalSettingsRequest terminal = instance.RuntimeSettings.Terminal ?? new();
            if (terminal.UseConPty)
            {
                conPty = ConPtySession.Start(startInfo, job, terminal.Columns, terminal.Rows, runCredential);
                process = conPty.Process;
                process.EnableRaisingEvents = true;
                input = new StreamWriter(conPty.Input, ResolveTerminalEncoding(terminal.InputEncoding), 4096, leaveOpen: true)
                {
                    NewLine = terminal.NewLine == "LF" ? "\n" : "\r\n",
                };
            }
            else
            {
                process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new DomainException("java-runtime-invalid", "The Java server process did not start.");
                }

                job.Assign(process);
                input = process.StandardInput;
                input.NewLine = terminal.NewLine == "LF" ? "\n" : "\r\n";
            }

            started = true;
            long configuredBytes = nodeConfiguration?.Current.TerminalBufferBytes ?? (1024 * 1024);
            int configuredLines = nodeConfiguration?.Current.TerminalBufferLines ?? 5000;
            managed = new(
                instanceId,
                process,
                job,
                new OutputRingBuffer(checked((int)Math.Min(configuredBytes, int.MaxValue)), configuredLines),
                clock.UtcNow,
                input,
                conPty,
                terminal.Columns,
                terminal.Rows);
            if (!_processes.TryAdd(instanceId, managed))
            {
                job.Terminate(1);
                throw new DomainException("conflict", "The instance already has a managed process.");
            }

            if (managed.ConPty is not null)
            {
                managed.StandardOutputTask = StartConPtyPump(
                    managed.ConPty.Output,
                    ResolveTerminalEncoding(terminal.OutputEncoding),
                    managed.Output,
                    managed.LifetimeCancellation.Token);
            }
            else
            {
                managed.StandardOutputTask = PumpAsync(process.StandardOutput, managed.Output, managed.LifetimeCancellation.Token);
                managed.StandardErrorTask = PumpAsync(process.StandardError, managed.Output, managed.LifetimeCancellation.Token);
            }
            _ = await instances.UpdateRuntimeAsync(instanceId, "Running", process.Id, managed.StartedAt, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await EnqueueStatusAsync(instanceId, instance.Status, "Running", null, null, cancellationToken).ConfigureAwait(false);
            AgentInstanceRuntimeResponse response = ToResponse(managed, "Running", null);
            Task monitor = MonitorExitAsync(managed);
            managed.MonitorTask = monitor;
            _exitMonitors[instanceId] = monitor;
            _ = RemoveExitMonitorWhenCompletedAsync(instanceId, monitor);
            return response;
        }
        catch
        {
            if (started && process is not null && !process.HasExited)
            {
                try
                {
                    job.Terminate(1);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            if (managed is not null)
            {
                managed.Dispose();
            }
            else
            {
                if (input is not null)
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                }

                conPty?.Dispose();
                process?.Dispose();
                job.Dispose();
            }

            throw;
        }
    }

    internal async Task<AgentInstanceRuntimeResponse> StopInstanceAsync(
        Guid instanceId,
        string stopCommand,
        int stopTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed))
        {
            return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        if (managed.HasExited)
        {
            await managed.MonitorTask.ConfigureAwait(false);
            return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(stopCommand) || stopCommand.Any(character => character is '\r' or '\n' || char.IsControl(character)) || stopTimeoutSeconds is < 1 or > 600)
        {
            throw new DomainException("validation-failed", "The stop command or timeout is invalid.");
        }

        managed.IntentionalStop = true;
        _ = await instances.UpdateRuntimeAsync(instanceId, "Stopping", managed.Process.Id, managed.StartedAt, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await EnqueueStatusAsync(instanceId, "Running", "Stopping", "stop-requested", null, cancellationToken).ConfigureAwait(false);
        bool stopCommandWritten = await managed.TryWriteInputAsync(
            stopCommand, appendNewLine: true, cancellationToken).ConfigureAwait(false);
        if (!stopCommandWritten && managed.HasExited)
        {
            await managed.MonitorTask.ConfigureAwait(false);
            return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(stopTimeoutSeconds));
        try
        {
            await managed.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = await managed.TryWriteInputAsync(
                "\u001c", appendNewLine: false, cancellationToken).ConfigureAwait(false);
            using var breakTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            breakTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await managed.Process.WaitForExitAsync(breakTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                managed.Job.Terminate(1);
                await managed.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await managed.MonitorTask.ConfigureAwait(false);
        return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentInstanceRuntimeResponse> ForceStopInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed))
        {
            return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        managed.IntentionalStop = true;
        _ = await instances.UpdateRuntimeAsync(instanceId, "Stopping", managed.Process.Id, managed.StartedAt, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await EnqueueStatusAsync(instanceId, "Running", "Stopping", "force-stop-requested", null, cancellationToken).ConfigureAwait(false);
        managed.Job.Terminate(1);
        await managed.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await managed.MonitorTask.ConfigureAwait(false);
        return await GetStatusAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentInstanceRuntimeResponse> RestartInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        _ = await StopInstanceAsync(instanceId, instance.RuntimeSettings.StopCommand, instance.RuntimeSettings.StopTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        return await StartInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentInstanceRuntimeResponse> SendCommandAsync(Guid instanceId, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(character => character is '\r' or '\n' || char.IsControl(character)))
        {
            throw new DomainException("validation-failed", "The console command must be one line of text.");
        }

        ManagedProcess managed = !_processes.TryGetValue(instanceId, out ManagedProcess? existing) || existing.HasExited
            ? throw new DomainException("conflict", "The instance is not running.")
            : existing;

        return !await managed.TryWriteInputAsync(command, appendNewLine: true, cancellationToken).ConfigureAwait(false)
            ? throw new DomainException("conflict", "The instance is not accepting console input.")
            : ToResponse(managed, "Running", null);
    }

    internal async Task SendTerminalInputAsync(Guid instanceId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64 * 1024 || text.Contains('\0', StringComparison.Ordinal))
        {
            throw new DomainException("validation-failed", "The terminal input is invalid.");
        }

        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed) || managed.HasExited)
        {
            throw new DomainException("conflict", "The instance is not running.");
        }

        if (!await managed.TryWriteInputAsync(text, appendNewLine: false, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("conflict", "The instance is not accepting terminal input.");
        }
    }

    internal TerminalAttachment AttachTerminal(Guid instanceId, Action<string> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return !_processes.TryGetValue(instanceId, out ManagedProcess? managed) || managed.HasExited
            ? throw new DomainException("conflict", "The instance is not running.")
            : managed.Attach(output);
    }

    internal SessionLogExportDto ExportSessionLog(Guid instanceId, bool includeAnsi)
    {
        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed))
        {
            throw new DomainException("conflict", "The instance has no active terminal session.");
        }

        IReadOnlyList<string> snapshot = managed.Output.Snapshot(5000);
        string content = string.Join("\r\n", snapshot);
        if (content.Length > 0)
        {
            content += "\r\n";
        }

        if (!includeAnsi)
        {
            content = StripAnsi(content);
        }

        string fileName = $"servitium-session-{instanceId:N}-{clock.UtcNow:yyyyMMdd-HHmmss}.log";
        return new(fileName, content, includeAnsi, managed.Output.Truncated);
    }

    internal void ResizeTerminal(Guid instanceId, int columns, int rows)
    {
        if (columns is < 20 or > 500 || rows is < 5 or > 200)
        {
            throw new DomainException("validation-failed", "The terminal size is invalid.");
        }

        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed) || managed.HasExited)
        {
            throw new DomainException("conflict", "The instance is not running.");
        }

        managed.Resize(columns, rows);
    }

    internal async Task<AgentInstanceRuntimeResponse> GetStatusAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(instanceId, out ManagedProcess? managed) && !managed.HasExited)
        {
            return ToResponse(managed, managed.IntentionalStop ? "Stopping" : "Running", null);
        }

        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        return new(instance.Id, instance.Status, instance.ProcessId, instance.StartedAt, instance.LastExitCode, []);
    }

    internal async Task AcceptEulaAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (instance.Status is not ("Stopped" or "Crashed"))
        {
            throw new DomainException("instance-not-stopped", "The EULA can only be changed while the instance is stopped.");
        }

        string path = Path.Combine(instance.WorkingDirectory, "eula.txt");
        byte[] content = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : [];
        var document = PreservingPropertiesDocument.Parse(content);
        document.SetValue("eula", "true");
        byte[] updated = document.Write();
        string temporary = path + ".servitium-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(updated, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task<PlayerListDto> QueryPlayersAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (!instance.RuntimeSettings.Query.Enabled)
        {
            throw new DomainException("query-disabled", "Server List Ping is disabled for this instance.");
        }

        PlayerListDto status = await statusClient.QueryAsync(instance.RuntimeSettings.Query.Host, instance.RuntimeSettings.Query.Port, cancellationToken).ConfigureAwait(false);
        if (!instance.RuntimeSettings.Rcon.Enabled)
        {
            return status;
        }

        try
        {
            RconCommandResultDto result = await ExecuteRconAsync(instanceId, "list", cancellationToken).ConfigureAwait(false);
            int separator = result.Output.IndexOf(':', StringComparison.Ordinal);
            string[] players = separator < 0
                ? []
                : result.Output[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return status with { Players = players, Source = "rcon" };
        }
        catch (DomainException)
        {
            return status;
        }
    }

    internal async Task<RconCommandResultDto> ExecuteRconAsync(Guid instanceId, string command, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        RconSettingsRequest settings = instance.RuntimeSettings.Rcon;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Password) || !settings.Password.StartsWith("protected:", StringComparison.Ordinal))
        {
            throw new DomainException("rcon-disabled", "RCON is disabled or has no configured secret.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(settings.Password["protected:".Length..]);
        }
        catch (FormatException)
        {
            throw new DomainException("rcon-auth-failed", "The protected RCON secret is invalid.");
        }

        byte[] plaintext = secretProtector.Unprotect(protectedBytes);
        try
        {
            string password = Encoding.UTF8.GetString(plaintext);
            string output = await rconClient.ExecuteAsync(instance.RuntimeSettings.Query.Host, settings.Port, password, command, cancellationToken).ConfigureAwait(false);
            return new(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal async Task<InstanceMetricsDto> GetMetricsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(instanceId, out ManagedProcess? managed) || managed.HasExited)
        {
            _ = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException("not-found", "The instance registration does not exist.");
            return new(clock.UtcNow, 0, 0, null);
        }

        DateTimeOffset now = clock.UtcNow;
        (TimeSpan processorTime, long workingSetBytes, int handleCount, int threadCount) = managed.Job.QueryUsage();
        TimeSpan elapsed = now - managed.LastMetricsAt;
        double cpu = elapsed <= TimeSpan.Zero
            ? 0
            : Math.Clamp((processorTime - managed.LastProcessorTime).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100);
        managed.LastMetricsAt = now;
        managed.LastProcessorTime = processorTime;
        int? onlinePlayers = null;
        LocalInstanceSnapshot? instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance?.RuntimeSettings.Query.Enabled == true)
        {
            try
            {
                onlinePlayers = (await statusClient.QueryAsync(instance.RuntimeSettings.Query.Host, instance.RuntimeSettings.Query.Port, cancellationToken).ConfigureAwait(false)).Online;
            }
            catch (DomainException)
            {
            }
        }

        return new(now, cpu, workingSetBytes, onlinePlayers, handleCount, threadCount);
    }

    internal async Task<InstanceLogDto> ReadLogAsync(
        Guid instanceId,
        string relativePath,
        long startLine,
        int count,
        string? search,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (!relativePath.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) || count is < 1 or > 5000 || startLine < 0)
        {
            throw new DomainException("validation-failed", "The log query is invalid.");
        }

        string logsRoot = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, "logs");
        string path = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, relativePath);
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(logsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("path-escapes-root", "The requested log is outside the logs directory.");
        }

        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("not-found", "The log file does not exist.");
        }

        List<string> lines = [];
        long current = 0;
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using Stream content = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
            : file;
        using StreamReader reader = new(content, new UTF8Encoding(false, true), true, 64 * 1024);
        bool truncated = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (current++ < startLine)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(search) || line.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(line);
            }

            if (lines.Count >= count)
            {
                truncated = true;
                break;
            }
        }

        return new(relativePath, startLine, lines, truncated);
    }

    internal async Task<InstanceUpdateResultDto> RunUpdateActionAsync(Guid instanceId, bool confirm, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (instance.Status is not ("Stopped" or "Crashed"))
        {
            throw new DomainException("instance-not-stopped", "An update action can only run while the instance is stopped.");
        }

        if (!confirm)
        {
            throw new DomainException("confirmation-required", "The update action requires explicit confirmation.");
        }

        UpdateActionRequest action = instance.RuntimeSettings.UpdateAction ?? new();
        if (action.Kind == "none")
        {
            throw new DomainException("validation-failed", "No update action is configured.");
        }

        if (action.Kind == "hmcl-channel-sync")
        {
            throw new DomainException("validation-failed", "HMCL Channel synchronization is handled by the phase 17 content synchronization service.");
        }

        if (action.Kind != "jar-replace" || string.IsNullOrWhiteSpace(action.SourceRelativePath) || string.IsNullOrWhiteSpace(action.TargetRelativePath))
        {
            throw new DomainException("validation-failed", "The JAR replacement action is incomplete.");
        }

        string source = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, action.SourceRelativePath);
        string target = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, action.TargetRelativePath);
        if (!source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !target.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
        {
            throw new DomainException("validation-failed", "The JAR replacement paths are invalid.");
        }

        string backupDirectory = pathPolicy.ResolveRelativePath(
            instance.WorkingDirectory,
            Path.Combine(".servitium", "backups", "updates", clock.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")));
        _ = Directory.CreateDirectory(backupDirectory);
        string? backupRelative = null;
        if (File.Exists(target))
        {
            string backup = Path.Combine(backupDirectory, Path.GetFileName(target));
            await CopyDurablyAsync(target, backup, cancellationToken).ConfigureAwait(false);
            backupRelative = Path.GetRelativePath(instance.WorkingDirectory, backup).Replace(Path.DirectorySeparatorChar, '/');
        }

        string temporary = target + ".servitium-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyDurablyAsync(source, temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new(action.Kind, backupRelative, clock.UtcNow);
    }

    private static async Task CopyDurablyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ManagedProcess managed in _processes.Values)
        {
            managed.Job.Dispose();
            managed.Dispose();
        }

        _processes.Clear();
    }

    private async Task RecoverPersistedStateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalInstanceSnapshot> registrations = await instances.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (LocalInstanceSnapshot instance in registrations.Where(candidate => candidate.Status is "Running" or "Starting" or "Stopping"))
        {
            _ = await instances.UpdateRuntimeAsync(instance.Id, "Crashed", null, instance.StartedAt, instance.LastExitCode, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorExitAsync(ManagedProcess managed)
    {
        bool restart = false;
        try
        {
            await managed.Process.WaitForExitAsync().ConfigureAwait(false);
            managed.CompleteProcessExit();
            await Task.WhenAll(managed.StandardOutputTask, managed.StandardErrorTask).ConfigureAwait(false);
            int exitCode = managed.Process.ExitCode;
            string status = managed.IntentionalStop ? "Stopped" : "Crashed";
            LocalInstanceSnapshot updated = await instances.UpdateRuntimeAsync(managed.InstanceId, status, null, managed.StartedAt, exitCode, clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            await EnqueueStatusAsync(managed.InstanceId, "Running", status, managed.IntentionalStop ? "stopped" : "process-exited", exitCode, CancellationToken.None).ConfigureAwait(false);
            if (!managed.IntentionalStop)
            {
                if (outbox is not null)
                {
                    await outbox.EnqueueAsync(
                        EventTypes.InstanceCrashed,
                        "instance:" + managed.InstanceId.ToString("D"),
                        new InstanceCrashedPayload(managed.InstanceId, exitCode, [.. managed.Output.Snapshot().TakeLast(200)]),
                        critical: true,
                        CancellationToken.None).ConfigureAwait(false);
                }

                restart = updated.RuntimeSettings.AutoRestart && Volatile.Read(ref _stopping) == 0;
            }
        }
        finally
        {
            _ = _processes.TryRemove(new KeyValuePair<Guid, ManagedProcess>(managed.InstanceId, managed));
            managed.Dispose();
        }

        if (restart)
        {
            await TryAutoRestartAsync(managed.InstanceId).ConfigureAwait(false);
        }
    }

    private async Task RemoveExitMonitorWhenCompletedAsync(Guid instanceId, Task monitor)
    {
        try
        {
            await monitor.ConfigureAwait(false);
        }
        finally
        {
            _ = _exitMonitors.TryRemove(new KeyValuePair<Guid, Task>(instanceId, monitor));
        }
    }

    private async Task StopForShutdownAsync(ManagedProcess managed, CancellationToken cancellationToken)
    {
        try
        {
            LocalInstanceSnapshot? instance = await instances.FindAsync(managed.InstanceId, cancellationToken).ConfigureAwait(false);
            string command = instance?.RuntimeSettings.StopCommand ?? "stop";
            int timeout = instance?.RuntimeSettings.StopTimeoutSeconds ?? 60;
            _ = await StopInstanceAsync(managed.InstanceId, command, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            managed.IntentionalStop = true;
            managed.Job.Terminate(1);
        }
    }

    private ProcessStartInfo BuildStartInfo(LocalInstanceSnapshot instance, AgentJavaRuntimeResponse runtime, RunCredential? runCredential)
    {
        StructuredLaunchRequest launch = instance.Launch;
        if (launch.MinimumHeapMegabytes <= 0 || launch.MaximumHeapMegabytes < launch.MinimumHeapMegabytes || launch.MaximumHeapMegabytes > 1024 * 1024)
        {
            throw new DomainException("validation-failed", "The configured heap range is invalid.");
        }

        TerminalSettingsRequest terminal = instance.RuntimeSettings.Terminal ?? new();
        Encoding inputEncoding = ResolveTerminalEncoding(terminal.InputEncoding);
        Encoding outputEncoding = ResolveTerminalEncoding(terminal.OutputEncoding);
        ProcessStartInfo startInfo = new()
        {
            FileName = runtime.ExecutablePath,
            WorkingDirectory = instance.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = inputEncoding,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        if (runCredential is not null)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Alternate Windows run credentials require Windows.");
            }

            startInfo.UserName = runCredential.UserName;
            startInfo.Domain = runCredential.Domain ?? (runCredential.UserName.Contains('@', StringComparison.Ordinal) ? string.Empty : ".");
            startInfo.PasswordInClearText = runCredential.Password;
            startInfo.LoadUserProfile = true;
        }

        foreach ((string key, string value) in launch.EnvironmentVariables ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=', StringComparison.Ordinal) || key.Any(char.IsControl) ||
                value.Contains('\0', StringComparison.Ordinal))
            {
                throw new DomainException("validation-failed", "An instance environment variable is invalid.");
            }

            startInfo.Environment[key] = value;
        }

        if (launch.AdvancedMode)
        {
            if (nodeConfiguration?.Current.AdvancedRawCommandsEnabled != true || launch.AdvancedArguments is not { Count: > 0 })
            {
                throw new DomainException("raw-command-disabled", "Advanced raw command mode is disabled.");
            }

            foreach (string argument in launch.AdvancedArguments)
            {
                AddArgument(startInfo, argument);
            }

            return startInfo;
        }

        string target = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, launch.Target);
        if (!File.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("path-escapes-root", "The launch target does not exist or is a reparse point.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(target)!);
        AddArgument(startInfo, $"-Xms{launch.MinimumHeapMegabytes}m");
        AddArgument(startInfo, $"-Xmx{launch.MaximumHeapMegabytes}m");
        foreach (string argument in launch.JvmArguments)
        {
            AddArgument(startInfo, argument);
        }

        string relativeTarget = Path.GetRelativePath(instance.WorkingDirectory, target);
        if (string.Equals(launch.TargetKind, "jar", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(relativeTarget);
        }
        else if (string.Equals(launch.TargetKind, "argfile", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("@" + relativeTarget);
        }
        else
        {
            throw new DomainException("validation-failed", "The launch target kind is invalid.");
        }

        foreach (string argument in launch.ServerArguments)
        {
            AddArgument(startInfo, argument);
        }

        return startInfo;
    }

    private static Encoding ResolveTerminalEncoding(string configured)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return configured.ToUpperInvariant() switch
        {
            "UTF-8" => new UTF8Encoding(false, true),
            "GBK" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "BIG5" => Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "SHIFT_JIS" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "SYSTEM-ANSI" => Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new DomainException("validation-failed", "The terminal encoding is unsupported."),
        };
    }

    private static void AddArgument(ProcessStartInfo startInfo, string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Any(char.IsControl))
        {
            throw new DomainException("raw-command-invalid", "Process arguments cannot be empty or contain control characters.");
        }

        startInfo.ArgumentList.Add(argument);
    }

    private static async Task PumpAsync(StreamReader reader, OutputRingBuffer output, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            output.Append(line);
        }
    }

    private static Task StartConPtyPump(Stream stream, Encoding encoding, OutputRingBuffer output, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => PumpBytes(stream, encoding, output, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void PumpBytes(Stream stream, Encoding encoding, OutputRingBuffer output, CancellationToken cancellationToken)
    {
        Decoder decoder = encoding.GetDecoder();
        byte[] bytes = new byte[4096];
        char[] characters = new char[encoding.GetMaxCharCount(bytes.Length)];
        StringBuilder line = new();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, 0, bytes.Length);
            bool flush = read == 0;
            int byteOffset = 0;
            do
            {
                decoder.Convert(
                    bytes,
                    byteOffset,
                    read - byteOffset,
                    characters,
                    0,
                    characters.Length,
                    flush,
                    out int bytesUsed,
                    out int charsUsed,
                    out _);
                byteOffset += bytesUsed;
                for (int index = 0; index < charsUsed; index++)
                {
                    char character = characters[index];
                    if (character == '\n')
                    {
                        if (line.Length > 0 && line[^1] == '\r')
                        {
                            _ = line.Remove(line.Length - 1, 1);
                        }

                        output.Append(line.ToString());
                        _ = line.Clear();
                    }
                    else
                    {
                        _ = line.Append(character);
                    }
                }
            }
            while (byteOffset < read);

            if (flush)
            {
                if (line.Length > 0)
                {
                    output.Append(line.ToString());
                }

                return;
            }
        }
    }

    private static void EnsureEulaAccepted(string workingDirectory)
    {
        string path = Path.Combine(workingDirectory, "eula.txt");
        if (!File.Exists(path))
        {
            throw new DomainException("eula-not-accepted", "Minecraft EULA has not been accepted for this instance.");
        }

        var document = PreservingPropertiesDocument.Parse(File.ReadAllBytes(path));
        if (!string.Equals(document.GetValue("eula"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("eula-not-accepted", "Minecraft EULA has not been accepted for this instance.");
        }
    }

    private static string StripAnsi(string value)
    {
        StringBuilder result = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\u001b')
            {
                _ = result.Append(current);
                continue;
            }

            if (++index >= value.Length)
            {
                break;
            }

            if (value[index] == '[')
            {
                while (++index < value.Length && value[index] is not (>= '@' and <= '~'))
                {
                }
            }
            else if (value[index] == ']')
            {
                while (++index < value.Length)
                {
                    if (value[index] == '\a')
                    {
                        break;
                    }

                    if (value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')
                    {
                        index++;
                        break;
                    }
                }
            }
        }

        return result.ToString();
    }

    private static AgentInstanceRuntimeResponse ToResponse(ManagedProcess managed, string status, int? lastExitCode)
    {
        return new(managed.InstanceId, status, managed.Process.Id, managed.StartedAt, lastExitCode, managed.Output.Snapshot());
    }

    private async Task TryAutoRestartAsync(Guid instanceId)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        LocalInstanceSnapshot? instance = await instances.FindAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        if (instance is null || !instance.RuntimeSettings.AutoRestart || instance.RuntimeSettings.MaximumRestartAttempts == 0)
        {
            return;
        }

        Queue<DateTimeOffset> history = _restartHistory.GetOrAdd(instanceId, _ => new Queue<DateTimeOffset>());
        bool allowed;
        lock (history)
        {
            DateTimeOffset cutoff = clock.UtcNow.AddSeconds(-instance.RuntimeSettings.RestartResetSeconds);
            while (history.TryPeek(out DateTimeOffset timestamp) && timestamp < cutoff)
            {
                _ = history.Dequeue();
            }

            allowed = history.Count < instance.RuntimeSettings.MaximumRestartAttempts;
            if (allowed)
            {
                history.Enqueue(clock.UtcNow);
            }
        }

        if (!allowed)
        {
            if (outbox is not null)
            {
                await outbox.EnqueueAsync(
                    EventTypes.InstanceAutoRestartExhausted,
                    "instance:" + instanceId.ToString("D"),
                    new InstanceAutoRestartExhaustedPayload(instanceId, history.Count),
                    critical: true,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(instance.RuntimeSettings.RestartIntervalSeconds)).ConfigureAwait(false);
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            _ = await StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DomainException)
        {
        }
    }

    private async Task StartAutomaticallyAsync(Guid instanceId, int delaySeconds, CancellationToken cancellationToken)
    {
        try
        {
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }

            _ = await StartInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DomainException or OperationCanceledException)
        {
        }
    }

    private Task EnqueueStatusAsync(
        Guid instanceId,
        string previousStatus,
        string status,
        string? reason,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        return outbox is null
            ? Task.CompletedTask
            : outbox.EnqueueAsync(
                EventTypes.InstanceStatusChanged,
                "instance:" + instanceId.ToString("D"),
                new InstanceStatusChangedPayload(instanceId, previousStatus, status, reason, exitCode),
                critical: status is "Crashed" or "Stopped",
                cancellationToken);
    }

    private sealed class ManagedProcess(
        Guid instanceId,
        Process process,
        WindowsJobObject job,
        OutputRingBuffer output,
        DateTimeOffset startedAt,
        TextWriter input,
        ConPtySession? conPty,
        int terminalColumns,
        int terminalRows) : IDisposable
    {
        private bool _disposed;

        internal Guid InstanceId { get; } = instanceId;
        internal Process Process { get; } = process;
        internal WindowsJobObject Job { get; } = job;
        internal OutputRingBuffer Output { get; } = output;
        internal TextWriter Input { get; } = input;
        internal ConPtySession? ConPty { get; } = conPty;
        internal DateTimeOffset StartedAt { get; } = startedAt;
        internal CancellationTokenSource LifetimeCancellation { get; } = new();
        internal bool IntentionalStop { get; set; }
        internal Task StandardOutputTask { get; set; } = Task.CompletedTask;
        internal Task StandardErrorTask { get; set; } = Task.CompletedTask;
        internal Task MonitorTask { get; set; } = Task.CompletedTask;
        internal DateTimeOffset LastMetricsAt { get; set; } = startedAt;
        internal TimeSpan LastProcessorTime { get; set; }
        internal int TerminalColumns { get; private set; } = terminalColumns;
        internal int TerminalRows { get; private set; } = terminalRows;

        internal bool HasExited
        {
            get
            {
                try
                {
                    return Process.HasExited;
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        internal async Task<bool> TryWriteInputAsync(string text, bool appendNewLine, CancellationToken cancellationToken)
        {
            try
            {
                if (appendNewLine)
                {
                    await Input.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Input.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }

        internal void CompleteProcessExit()
        {
            try
            {
                Input.Dispose();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            ConPty?.CompleteProcessExit();
        }

        internal void Resize(int columns, int rows)
        {
            TerminalColumns = columns;
            TerminalRows = rows;
            ConPty?.Resize(columns, rows);
        }

        internal TerminalAttachment Attach(Action<string> callback)
        {
            IReadOnlyList<string> snapshot = Output.Subscribe(callback);
            return new TerminalAttachment(Output, callback, snapshot, Output.Truncated);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            LifetimeCancellation.Cancel();
            LifetimeCancellation.Dispose();
            try
            {
                Input.Dispose();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            ConPty?.Dispose();
            Process.Dispose();
            Job.Dispose();
        }
    }

    internal sealed class TerminalAttachment(
        OutputRingBuffer output,
        Action<string> callback,
        IReadOnlyList<string> snapshot,
        bool truncated) : IDisposable
    {
        private int _disposed;

        internal IReadOnlyList<string> Snapshot { get; } = snapshot;
        internal bool Truncated { get; } = truncated;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                output.Unsubscribe(callback);
            }
        }
    }
}
