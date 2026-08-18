using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Servitium.Agent.Archives;
using Servitium.Agent.ControlPlane;
using Servitium.Agent.Instances;
using Servitium.Agent.Java;
using Servitium.Agent.Lifecycle;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceLifecycleTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-500")]
    [Trait("TestId", "INT-570")]
    [Trait("TestId", "SEC-200")]
    public async Task Java夹具_Eula门禁启动命令输出与正常停止形成纵向链路()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("eula-not-accepted", rejected.Code);

        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        string eula = await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory!, "eula.txt")).ConfigureAwait(true);
        Assert.Contains("# synthetic fixture", eula, StringComparison.Ordinal);
        Assert.Contains("eula=true", eula, StringComparison.Ordinal);
        Assert.Contains("fixture-setting=preserved", eula, StringComparison.Ordinal);

        AgentInstanceRuntimeResponse started = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("Running", started.Status);
        _ = Assert.NotNull(started.ProcessId);
        AgentInstanceRuntimeResponse ready = await WaitForOutputAsync(test.Lifecycle, instanceId, "fixture-ready").ConfigureAwait(true);
        Assert.Contains(ready.RecentOutput, line => line.Contains("fixture-ready", StringComparison.Ordinal));

        const string command = "literal & whoami | echo %PATH%";
        _ = await test.Lifecycle.SendCommandAsync(instanceId, command, CancellationToken.None).ConfigureAwait(true);
        AgentInstanceRuntimeResponse echoed = await WaitForOutputAsync(test.Lifecycle, instanceId, "fixture-input:" + command).ConfigureAwait(true);
        Assert.Contains(echoed.RecentOutput, line => line.Contains("fixture-input:" + command, StringComparison.Ordinal));

        AgentInstanceRuntimeResponse stopped = await test.Lifecycle.StopInstanceAsync(instanceId, "stop", 5, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("Stopped", stopped.Status);
        Assert.Equal(0, stopped.LastExitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-510")]
    public async Task 不响应停止命令的夹具_超时后由JobObject完整终止()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync("--ignore-stop").ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);
        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        AgentInstanceRuntimeResponse started = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        _ = await WaitForOutputAsync(test.Lifecycle, instanceId, "fixture-ready").ConfigureAwait(true);

        AgentInstanceRuntimeResponse stopped = await test.Lifecycle.StopInstanceAsync(instanceId, "stop", 1, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("Stopped", stopped.Status);
        Assert.NotEqual(0, stopped.LastExitCode);
        if (started.ProcessId is int processId)
        {
            Assert.False(IsProcessAlive(processId));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-580")]
    public async Task ConPty与回退管道_均保留Ansi和非Ascii输出()
    {
        LifecycleContext conPty = await LifecycleContext.CreateAsync("--probe-output").ConfigureAwait(true);
        await using (conPty.ConfigureAwait(true))
        {
            Guid instanceId = await conPty.CreateFixtureInstanceAsync().ConfigureAwait(true);
            await conPty.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            _ = await conPty.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            AgentInstanceRuntimeResponse output = await WaitForOutputAsync(conPty.Lifecycle, instanceId, "fixture-unicode:伺服器编码边界").ConfigureAwait(true);
            Assert.Contains(output.RecentOutput, line => line.Contains("fixture-unicode:伺服器编码边界", StringComparison.Ordinal));
            SessionLogExportDto withAnsi = conPty.Lifecycle.ExportSessionLog(instanceId, includeAnsi: true);
            SessionLogExportDto withoutAnsi = conPty.Lifecycle.ExportSessionLog(instanceId, includeAnsi: false);
            Assert.Contains("\u001b[32m", withAnsi.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b[", withoutAnsi.Content, StringComparison.Ordinal);
            Assert.Contains("fixture-unicode:伺服器编码边界", withoutAnsi.Content, StringComparison.Ordinal);
            Assert.EndsWith(".log", withoutAnsi.FileName, StringComparison.Ordinal);
            _ = await conPty.Lifecycle.StopInstanceAsync(instanceId, "stop", 5, CancellationToken.None).ConfigureAwait(true);
        }

        LifecycleContext redirected = await LifecycleContext.CreateAsync("--probe-output").ConfigureAwait(true);
        await using (redirected.ConfigureAwait(true))
        {
            Guid instanceId = await redirected.CreateFixtureInstanceAsync().ConfigureAwait(true);
            await redirected.SetTerminalAsync(instanceId, new(UseConPty: false, InputEncoding: "GBK", OutputEncoding: "GBK")).ConfigureAwait(true);
            await redirected.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            _ = await redirected.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            AgentInstanceRuntimeResponse output = await WaitForOutputAsync(redirected.Lifecycle, instanceId, "fixture-unicode:伺服器编码边界").ConfigureAwait(true);
            Assert.Contains(output.RecentOutput, line => line.Contains("\u001b[32mfixture-unicode:伺服器编码边界", StringComparison.Ordinal));
            _ = await redirected.Lifecycle.StopInstanceAsync(instanceId, "stop", 5, CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-590")]
    public async Task Gbk逐字节输出_跨块多字节被完整解码()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync("--output-charset", "GBK", "--split-output", "--probe-output").ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);
        await test.SetTerminalAsync(instanceId, new(UseConPty: false, OutputEncoding: "GBK", InputEncoding: "GBK")).ConfigureAwait(true);
        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);

        _ = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        AgentInstanceRuntimeResponse output = await WaitForOutputAsync(test.Lifecycle, instanceId, "fixture-unicode:伺服器编码边界").ConfigureAwait(true);

        Assert.Contains(output.RecentOutput, line => line.Contains("伺服器编码边界", StringComparison.Ordinal));
        Assert.DoesNotContain(output.RecentOutput, line => line.Contains('\uFFFD', StringComparison.Ordinal));
        _ = await test.Lifecycle.StopInstanceAsync(instanceId, "stop", 5, CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-540")]
    public async Task 非零退出码_识别为崩溃并持久记录()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync("--crash-exit-code", "17").ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);
        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);

        _ = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        AgentInstanceRuntimeResponse crashed = await WaitForStatusAsync(test.Lifecycle, instanceId, "Crashed").ConfigureAwait(true);
        _ = await WaitForOutboxEventAsync(test.Outbox, EventTypes.InstanceCrashed).ConfigureAwait(true);

        Assert.Equal(17, crashed.LastExitCode);
        Assert.Null(crashed.ProcessId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-520")]
    public async Task 强制终止_三层Java进程树全部退出()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync("--spawn-depth", "2").ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);
        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        _ = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);

        IReadOnlyList<int> processIds = await WaitForFixtureProcessTreeAsync(test.Lifecycle, instanceId, expectedCount: 3).ConfigureAwait(true);
        AgentInstanceRuntimeResponse stopped = await test.Lifecycle.ForceStopInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("Stopped", stopped.Status);
        Assert.All(processIds, processId => Assert.False(IsProcessAlive(processId), $"Synthetic descendant process {processId} survived Job Object termination."));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-550")]
    public async Task 自动重启_达到连续失败上限后停止并持久化事件()
    {
        LifecycleContext test = await LifecycleContext.CreateAsync("--crash-exit-code", "17").ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = await test.CreateFixtureInstanceAsync().ConfigureAwait(true);
        await test.SetRestartPolicyAsync(instanceId, maximumRestartAttempts: 2).ConfigureAwait(true);
        await test.Lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);

        _ = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        AgentOutboxItem exhausted = await WaitForOutboxEventAsync(test.Outbox, EventTypes.InstanceAutoRestartExhausted).ConfigureAwait(true);
        AgentInstanceRuntimeResponse crashed = await WaitForStatusAsync(test.Lifecycle, instanceId, "Crashed").ConfigureAwait(true);

        Assert.Equal("instance:" + instanceId.ToString("D"), exhausted.Scope);
        Assert.Equal(17, crashed.LastExitCode);
        Assert.Contains("\"AttemptCount\":2", exhausted.PayloadJson, StringComparison.Ordinal);
    }

    private static async Task<AgentInstanceRuntimeResponse> WaitForOutputAsync(
        InstanceLifecycleService lifecycle,
        Guid instanceId,
        string expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                AgentInstanceRuntimeResponse status = await lifecycle.GetStatusAsync(instanceId, timeout.Token).ConfigureAwait(true);
                if (status.RecentOutput.Any(line => line.Contains(expected, StringComparison.Ordinal)))
                {
                    return status;
                }

                Assert.False(
                    string.Equals(status.Status, "Crashed", StringComparison.Ordinal),
                    $"Fixture crashed with exit code {status.LastExitCode}; output: {string.Join(" | ", status.RecentOutput)}");

                await Task.Delay(25, timeout.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            AgentInstanceRuntimeResponse status = await lifecycle.GetStatusAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            Assert.Fail($"Timed out waiting for '{expected}'. Status: {status.Status}; output: {string.Join(" | ", status.RecentOutput)}");
            throw;
        }
    }

    private static async Task<AgentInstanceRuntimeResponse> WaitForStatusAsync(
        InstanceLifecycleService lifecycle,
        Guid instanceId,
        string expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            AgentInstanceRuntimeResponse status = await lifecycle.GetStatusAsync(instanceId, timeout.Token).ConfigureAwait(true);
            if (string.Equals(status.Status, expected, StringComparison.Ordinal))
            {
                return status;
            }

            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task<AgentOutboxItem> WaitForOutboxEventAsync(AgentOutboxStore outbox, string eventType)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            IReadOnlyList<AgentOutboxItem> events = await outbox.ReadAsync(100, timeout.Token).ConfigureAwait(true);
            AgentOutboxItem? found = events.FirstOrDefault(item => string.Equals(item.Type, eventType, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task<IReadOnlyList<int>> WaitForFixtureProcessTreeAsync(
        InstanceLifecycleService lifecycle,
        Guid instanceId,
        int expectedCount)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            AgentInstanceRuntimeResponse status = await lifecycle.GetStatusAsync(instanceId, timeout.Token).ConfigureAwait(true);
            int[] processIds = [.. status.RecentOutput
                .Where(line => line.Contains("fixture-pid:", StringComparison.Ordinal))
                .Select(line => line[(line.LastIndexOf("fixture-pid:", StringComparison.Ordinal) + "fixture-pid:".Length)..].Trim())
                .Select(value => int.TryParse(value, out int processId) ? processId : 0)
                .Where(processId => processId > 0)
                .Distinct()];
            if (processIds.Length >= expectedCount)
            {
                return processIds;
            }

            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class LifecycleContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteCoordinator _coordinator;
        private readonly string[] _serverArguments;
        private readonly InstanceRegistry _instances;
        private readonly JavaRuntimeRegistry _javaRuntimes;

        private LifecycleContext(
            string root,
            SqliteWriteCoordinator coordinator,
            string[] serverArguments,
            InstanceRegistry instances,
            JavaRuntimeRegistry javaRuntimes,
            InstanceLifecycleService lifecycle,
            AgentOutboxStore outbox)
        {
            _root = root;
            _coordinator = coordinator;
            _serverArguments = serverArguments;
            _instances = instances;
            _javaRuntimes = javaRuntimes;
            Lifecycle = lifecycle;
            Outbox = outbox;
        }

        internal InstanceLifecycleService Lifecycle { get; }
        internal AgentOutboxStore Outbox { get; }
        internal string? WorkingDirectory { get; private set; }

        internal static async Task<LifecycleContext> CreateAsync(params string[] serverArguments)
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-lifecycle-{Guid.NewGuid():N}")).FullName;
            AgentStorageOptions storage = new(root, Path.Combine(root, "agent.db"), Path.Combine(root, "instances"), Path.Combine(root, "staging"));
            _ = Directory.CreateDirectory(storage.ManagedInstanceRoot);
            _ = Directory.CreateDirectory(storage.StagingRoot);
            DbContextOptions<AgentDbContext> options = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + storage.DatabasePath + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SqliteWriteCoordinator coordinator = new();
            WindowsPathPolicy pathPolicy = new();
            DpapiSecretProtector secretProtector = new();
            InstanceRegistry instances = new(factory, coordinator, pathPolicy, new SafeZipExtractor(ArchiveSafetyLimits.Default), storage, secretProtector);
            JavaRuntimeRegistry javaRuntimes = new(factory, coordinator, new GuidFactory(), new SystemClock());
            SystemClock clock = new();
            AgentOutboxStore outbox = new(factory, coordinator, clock);
            InstanceLifecycleService lifecycle = new(instances, javaRuntimes, pathPolicy, clock, secretProtector, new MinecraftServerProtocolClient(), new RconClient(), outbox: outbox);
            await ((IHostedService)lifecycle).StartAsync(CancellationToken.None).ConfigureAwait(true);
            return new LifecycleContext(root, coordinator, serverArguments, instances, javaRuntimes, lifecycle, outbox);
        }

        internal async Task<Guid> CreateFixtureInstanceAsync()
        {
            AgentJavaRuntimeResponse runtime = await _javaRuntimes.RegisterAsync(FindJavaExecutable(), CancellationToken.None).ConfigureAwait(true);
            var id = Guid.NewGuid();
            StructuredLaunchRequest launch = new(runtime.Id, 64, 128, [], "jar", "fixture.jar", _serverArguments);
            LocalInstanceSnapshot instance = await _instances.CreateEmptyAsync(
                id,
                "Fixture " + id.ToString("N"),
                launch,
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            WorkingDirectory = instance.WorkingDirectory;
            File.Copy(FindFixtureJar(), Path.Combine(instance.WorkingDirectory, "fixture.jar"));
            await File.WriteAllTextAsync(
                Path.Combine(instance.WorkingDirectory, "eula.txt"),
                "# synthetic fixture\r\neula=false\r\nfixture-setting=preserved\r\n").ConfigureAwait(true);
            return id;
        }

        internal async Task SetTerminalAsync(Guid instanceId, TerminalSettingsRequest terminal)
        {
            LocalInstanceSnapshot instance = await _instances.FindAsync(instanceId, CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The synthetic instance registration disappeared.");
            LocalInstanceRuntimeSettings settings = instance.RuntimeSettings;
            _ = await _instances.UpdateAsync(
                instanceId,
                new UpdateInstanceRequest(
                    instance.Name,
                    instance.Tags,
                    instance.Notes,
                    instance.Launch,
                    settings.StopCommand,
                    settings.StopTimeoutSeconds,
                    settings.AutoStart,
                    settings.AutoRestart,
                    settings.MaximumRestartAttempts,
                    settings.Rcon with { Password = null },
                    settings.Query,
                    PrimaryPort: instance.PrimaryPort,
                    AutoStartDelaySeconds: settings.AutoStartDelaySeconds,
                    RestartIntervalSeconds: settings.RestartIntervalSeconds,
                    RestartResetSeconds: settings.RestartResetSeconds,
                    RunCredentialAlias: settings.RunCredentialAlias,
                    Terminal: terminal,
                    UpdateAction: settings.UpdateAction,
                    ShortcutCommands: settings.ShortcutCommands),
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
        }

        internal async Task SetRestartPolicyAsync(Guid instanceId, int maximumRestartAttempts)
        {
            LocalInstanceSnapshot instance = await _instances.FindAsync(instanceId, CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The synthetic instance registration disappeared.");
            LocalInstanceRuntimeSettings settings = instance.RuntimeSettings;
            _ = await _instances.UpdateAsync(
                instanceId,
                new UpdateInstanceRequest(
                    instance.Name,
                    instance.Tags,
                    instance.Notes,
                    instance.Launch,
                    settings.StopCommand,
                    settings.StopTimeoutSeconds,
                    settings.AutoStart,
                    AutoRestart: true,
                    MaximumRestartAttempts: maximumRestartAttempts,
                    settings.Rcon with { Password = null },
                    settings.Query,
                    PrimaryPort: instance.PrimaryPort,
                    AutoStartDelaySeconds: settings.AutoStartDelaySeconds,
                    RestartIntervalSeconds: 0,
                    RestartResetSeconds: 60,
                    RunCredentialAlias: settings.RunCredentialAlias,
                    Terminal: settings.Terminal,
                    UpdateAction: settings.UpdateAction,
                    ShortcutCommands: settings.ShortcutCommands),
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)Lifecycle).StopAsync(CancellationToken.None).ConfigureAwait(false);
            Lifecycle.Dispose();
            _coordinator.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            GC.SuppressFinalize(this);
        }

        private static string FindJavaExecutable()
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            string? java = path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(directory => Path.Combine(directory, "java.exe"))
                .FirstOrDefault(File.Exists);
            return java ?? throw new InvalidOperationException("The lifecycle fixture requires java.exe on PATH.");
        }

        private static string FindFixtureJar()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "Servitium.sln")))
            {
                current = current.Parent;
            }

            return current is null
                ? throw new InvalidOperationException("The repository root could not be located.")
                : Path.Combine(current.FullName, "artifacts", "test-fixtures", "Servitium.JavaServerFixture.jar");
        }

        private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
        {
            public AgentDbContext CreateDbContext()
            {
                return new AgentDbContext(options);
            }
        }
    }
}
