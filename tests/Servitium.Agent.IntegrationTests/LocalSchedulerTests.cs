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
using Servitium.Agent.Scheduling;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Scheduling;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class LocalSchedulerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1010")]
    [Trait("TestId", "INT-1050")]
    public async Task 控制平面不存在时_本地定时器仍触发并把历史写入Outbox()
    {
        SchedulerContext test = await SchedulerContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        var taskId = Guid.NewGuid();
        _ = await test.Scheduler.UpsertAsync(
            test.InstanceId,
            taskId,
            new UpsertScheduledTaskRequest(
                "autonomous",
                new("interval", 10, null, null, null),
                [new("start", null, false), new("force-stop", null, false)],
                1,
                "skip",
                true,
                null),
            CancellationToken.None).ConfigureAwait(true);

        await test.Scheduler.StartAsync(CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<ScheduledTaskRunDto> runs = await WaitForRunsAsync(test.Scheduler, test.InstanceId, taskId, 1, 20).ConfigureAwait(true);
        await test.Scheduler.StopAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(Assert.Single(runs).Succeeded);
        ScheduledTaskDetailsDto task = await test.Scheduler.GetAsync(test.InstanceId, taskId, CancellationToken.None).ConfigureAwait(true);
        Assert.False(task.Enabled);
        Assert.Equal(0, task.RemainingRuns);
        Assert.Contains(
            await test.Outbox.ReadAsync(100, CancellationToken.None).ConfigureAwait(true),
            item => item.Type == EventTypes.ScheduleRunCompleted &&
                item.Scope == "instance:" + test.InstanceId.ToString("D") &&
                item.PayloadJson.Contains(taskId.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1000")]
    [Trait("TestId", "INT-1020")]
    [Trait("TestId", "INT-1030")]
    [Trait("TestId", "INT-1040")]
    public async Task 本地调度器_CRUD六种动作三种重叠策略和历史保留均生效()
    {
        SchedulerContext test = await SchedulerContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = test.InstanceId;

        var actionTaskId = Guid.NewGuid();
        UpsertScheduledTaskRequest actionDefinition = Definition(
            "all-actions",
            [
                new("start", null, false),
                new("delay", "1", false),
                new("send-console-command", "synthetic scheduled input", false),
                new("restart", null, false),
                new("force-stop", null, false),
            ]);
        ScheduledTaskDetailsDto created = await test.Scheduler.UpsertAsync(instanceId, actionTaskId, actionDefinition, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(1, created.Revision);
        _ = Assert.Single(await test.Scheduler.ListAsync(instanceId, CancellationToken.None).ConfigureAwait(true));

        DomainException stale = await Assert.ThrowsAsync<DomainException>(() => test.Scheduler.UpsertAsync(
            instanceId, actionTaskId, actionDefinition with { Revision = 99 }, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("concurrency-conflict", stale.Code);
        ScheduledTaskDetailsDto updated = await test.Scheduler.UpsertAsync(
            instanceId, actionTaskId, actionDefinition with { Revision = created.Revision }, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(2, updated.Revision);

        ScheduledTaskRunDto firstRun = await test.Scheduler.RunNowAsync(instanceId, actionTaskId, CancellationToken.None).ConfigureAwait(true);
        Assert.True(firstRun.Succeeded);
        Assert.Equal(5, firstRun.ActionResults.Count);
        Assert.All(firstRun.ActionResults, result => Assert.EndsWith(":succeeded", result, StringComparison.Ordinal));

        var stopTaskId = Guid.NewGuid();
        _ = await test.Scheduler.UpsertAsync(instanceId, stopTaskId, Definition(
            "stop-action",
            [new("start", null, false), new("stop", null, false)]), CancellationToken.None).ConfigureAwait(true);
        ScheduledTaskRunDto stopRun = await test.Scheduler.RunNowAsync(instanceId, stopTaskId, CancellationToken.None).ConfigureAwait(true);
        Assert.True(stopRun.Succeeded);
        Assert.Equal(["start:succeeded", "stop:succeeded"], stopRun.ActionResults);

        await AssertOverlapAsync(test, "skip", triggerCount: 3, expectedRuns: 3, expectedSpecial: "overlap-skipped").ConfigureAwait(true);
        await AssertOverlapAsync(test, "queue", triggerCount: 5, expectedRuns: 5, expectedSpecial: "overlap-queue-dropped").ConfigureAwait(true);
        await AssertOverlapAsync(test, "concurrent", triggerCount: 3, expectedRuns: 3, expectedSpecial: null).ConfigureAwait(true);

        _ = await test.Lifecycle.StartInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        var historyTaskId = Guid.NewGuid();
        _ = await test.Scheduler.UpsertAsync(instanceId, historyTaskId, Definition("history", [new("start", null, false)]), CancellationToken.None).ConfigureAwait(true);
        for (int index = 0; index < 101; index++)
        {
            ScheduledTaskRunDto run = await test.Scheduler.RunNowAsync(instanceId, historyTaskId, CancellationToken.None).ConfigureAwait(true);
            Assert.False(run.Succeeded);
            Assert.Equal("instance-not-stopped", run.OutcomeCode);
        }

        Assert.Equal(100, (await test.Scheduler.ListRunsAsync(instanceId, historyTaskId, CancellationToken.None).ConfigureAwait(true)).Count);
        _ = await test.Lifecycle.ForceStopInstanceAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
        Assert.Contains(await test.Outbox.ReadAsync(100, CancellationToken.None).ConfigureAwait(true), item => item.Type == EventTypes.ScheduleRunCompleted);

        ScheduledTaskDetailsDto disabled = await test.Scheduler.SetEnabledAsync(instanceId, actionTaskId, false, CancellationToken.None).ConfigureAwait(true);
        Assert.False(disabled.Enabled);
        await test.Scheduler.DeleteAsync(instanceId, actionTaskId, CancellationToken.None).ConfigureAwait(true);
        _ = await Assert.ThrowsAsync<DomainException>(() => test.Scheduler.GetAsync(instanceId, actionTaskId, CancellationToken.None)).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1070")]
    public async Task 本地调度器_强制名称唯一与每实例数量上限()
    {
        SchedulerContext test = await SchedulerContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        _ = await test.Scheduler.UpsertAsync(
            test.InstanceId,
            Guid.NewGuid(),
            Definition("unique", [new("delay", "1", false)]),
            CancellationToken.None).ConfigureAwait(true);
        DomainException duplicate = await Assert.ThrowsAsync<DomainException>(() => test.Scheduler.UpsertAsync(
            test.InstanceId,
            Guid.NewGuid(),
            Definition("unique", [new("delay", "1", false)]),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("conflict", duplicate.Code);

        for (int index = 1; index < 32; index++)
        {
            _ = await test.Scheduler.UpsertAsync(
                test.InstanceId,
                Guid.NewGuid(),
                Definition("quota-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), [new("delay", "1", false)]),
                CancellationToken.None).ConfigureAwait(true);
        }

        DomainException quota = await Assert.ThrowsAsync<DomainException>(() => test.Scheduler.UpsertAsync(
            test.InstanceId,
            Guid.NewGuid(),
            Definition("over-quota", [new("delay", "1", false)]),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("quota-exceeded", quota.Code);
        Assert.Equal(32, (await test.Scheduler.ListAsync(test.InstanceId, CancellationToken.None).ConfigureAwait(true)).Count);
    }

    private static async Task AssertOverlapAsync(
        SchedulerContext test,
        string policy,
        int triggerCount,
        int expectedRuns,
        string? expectedSpecial)
    {
        var taskId = Guid.NewGuid();
        _ = await test.Scheduler.UpsertAsync(
            test.InstanceId,
            taskId,
            Definition("overlap-" + policy, [new("delay", "1", false)], overlap: policy),
            CancellationToken.None).ConfigureAwait(true);
        for (int index = 0; index < triggerCount; index++)
        {
            test.Scheduler.ScheduleExecution(taskId, policy, CancellationToken.None);
        }

        IReadOnlyList<ScheduledTaskRunDto> runs = await WaitForRunsAsync(test.Scheduler, test.InstanceId, taskId, expectedRuns).ConfigureAwait(true);
        if (expectedSpecial is null)
        {
            Assert.All(runs, run => Assert.True(run.Succeeded));
        }
        else
        {
            Assert.Contains(runs, run => run.OutcomeCode == expectedSpecial);
        }
    }

    private static async Task<IReadOnlyList<ScheduledTaskRunDto>> WaitForRunsAsync(LocalScheduler scheduler, Guid instanceId, Guid taskId, int expected, int timeoutSeconds = 15)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
        while (true)
        {
            IReadOnlyList<ScheduledTaskRunDto> runs = await scheduler.ListRunsAsync(instanceId, taskId, timeout.Token).ConfigureAwait(true);
            if (runs.Count == expected)
            {
                return runs;
            }

            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
    }

    private static UpsertScheduledTaskRequest Definition(
        string name,
        IReadOnlyList<ScheduleActionDto> actions,
        string overlap = "skip")
    {
        return new(name, new("interval", 60, null, null, null), actions, -1, overlap, false, null);
    }

    private sealed class SchedulerContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteCoordinator _coordinator;

        private SchedulerContext(
            string root,
            SqliteWriteCoordinator coordinator,
            Guid instanceId,
            InstanceLifecycleService lifecycle,
            LocalScheduler scheduler,
            AgentOutboxStore outbox)
        {
            _root = root;
            _coordinator = coordinator;
            InstanceId = instanceId;
            Lifecycle = lifecycle;
            Scheduler = scheduler;
            Outbox = outbox;
        }

        internal Guid InstanceId { get; }
        internal InstanceLifecycleService Lifecycle { get; }
        internal LocalScheduler Scheduler { get; }
        internal AgentOutboxStore Outbox { get; }

        internal static async Task<SchedulerContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-scheduler-{Guid.NewGuid():N}")).FullName;
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
            WindowsPathPolicy paths = new();
            DpapiSecretProtector secrets = new();
            SystemClock clock = new();
            InstanceRegistry instances = new(factory, coordinator, paths, new SafeZipExtractor(ArchiveSafetyLimits.Default), storage, secrets);
            JavaRuntimeRegistry runtimes = new(factory, coordinator, new GuidFactory(), clock);
            AgentOutboxStore outbox = new(factory, coordinator, clock);
            InstanceLifecycleService lifecycle = new(instances, runtimes, paths, clock, secrets, new MinecraftServerProtocolClient(), new RconClient(), outbox: outbox);
            await ((IHostedService)lifecycle).StartAsync(CancellationToken.None).ConfigureAwait(true);

            AgentJavaRuntimeResponse runtime = await runtimes.RegisterAsync(FindJavaExecutable(), CancellationToken.None).ConfigureAwait(true);
            var instanceId = Guid.NewGuid();
            LocalInstanceSnapshot instance = await instances.CreateEmptyAsync(
                instanceId,
                "Scheduler " + instanceId.ToString("N"),
                new StructuredLaunchRequest(runtime.Id, 64, 128, [], "jar", "fixture.jar", []),
                [],
                null,
                clock.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            File.Copy(FindFixtureJar(), Path.Combine(instance.WorkingDirectory, "fixture.jar"));
            await File.WriteAllTextAsync(Path.Combine(instance.WorkingDirectory, "eula.txt"), "eula=false\r\n").ConfigureAwait(true);
            await lifecycle.AcceptEulaAsync(instanceId, CancellationToken.None).ConfigureAwait(true);
            LocalScheduler scheduler = new(factory, coordinator, instances, lifecycle, clock, new GuidFactory());
            return new SchedulerContext(root, coordinator, instanceId, lifecycle, scheduler, outbox);
        }

        public async ValueTask DisposeAsync()
        {
            Scheduler.Dispose();
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
            return path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(directory => Path.Combine(directory, "java.exe"))
                .FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("The scheduler fixture requires java.exe on PATH.");
        }

        private static string FindFixtureJar()
        {
            return Path.Combine(AppContext.BaseDirectory, "Servitium.JavaServerFixture.jar");
        }

        private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
        {
            public AgentDbContext CreateDbContext()
            {
                return new(options);
            }
        }
    }
}
