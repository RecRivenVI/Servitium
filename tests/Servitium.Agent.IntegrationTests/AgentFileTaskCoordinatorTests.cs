using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Files;
using Servitium.Agent.Hosting;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Nodes;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentFileTaskCoordinatorTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-850")]
    public async Task 文件任务_按动态配额排队并可取消且持久化终态()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-file-tasks-" + Guid.NewGuid().ToString("N"))).FullName;
        SqliteWriteCoordinator writeCoordinator = new();
        AgentNodeConfigurationStore? nodeConfiguration = null;
        AgentFileTaskCoordinator? coordinator = null;
        try
        {
            AgentStorageOptions storage = new(root, Path.Combine(root, "agent.db"), Path.Combine(root, "instances"), Path.Combine(root, "staging"));
            _ = Directory.CreateDirectory(storage.ManagedInstanceRoot);
            DbContextOptions<AgentDbContext> options = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + storage.DatabasePath + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SystemClock clock = new();
            nodeConfiguration = new AgentNodeConfigurationStore(
                new AgentHostOptions { DataRoot = root, NodeName = "Synthetic Node" },
                storage,
                new WindowsPathPolicy(),
                new AllowedAuthoritySet());
            _ = await nodeConfiguration.UpdateAsync(
                new UpdateNodeRequest("Synthetic Node", storage.ManagedInstanceRoot, null, null, FileTaskConcurrency: 1),
                CancellationToken.None).ConfigureAwait(true);
            AgentOutboxStore outbox = new(factory, writeCoordinator, clock);
            coordinator = new AgentFileTaskCoordinator(factory, writeCoordinator, nodeConfiguration, outbox, clock);
            await coordinator.StartAsync(CancellationToken.None).ConfigureAwait(true);

            var instanceId = Guid.NewGuid();
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            TaskCompletionSource<bool> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> first = coordinator.RunAsync(
                firstId,
                instanceId,
                "copy",
                "first",
                false,
                false,
                async token =>
                {
                    _ = firstStarted.TrySetResult(true);
                    _ = await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
                    return "first";
                },
                CancellationToken.None);
            _ = await firstStarted.Task.ConfigureAwait(true);

            Task<string> second = coordinator.RunAsync(
                secondId,
                instanceId,
                "copy",
                "second",
                false,
                false,
                _ => Task.FromResult("second"),
                CancellationToken.None);
            await WaitForRowCountAsync(factory, 2).ConfigureAwait(true);
            Assert.Equal(1, coordinator.ActiveFileTasks);
            await coordinator.CancelAsync(instanceId, secondId, CancellationToken.None).ConfigureAwait(true);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second).ConfigureAwait(true);

            _ = releaseFirst.TrySetResult(true);
            Assert.Equal("first", await first.ConfigureAwait(true));

            var progressTaskId = Guid.NewGuid();
            string progressResult = await coordinator.RunWithProgressAsync(
                progressTaskId,
                instanceId,
                "copy",
                "progress",
                false,
                false,
                async (report, token) =>
                {
                    await report(0.25d, token).ConfigureAwait(false);
                    await report(0.75d, token).ConfigureAwait(false);
                    return "progress";
                },
                CancellationToken.None).ConfigureAwait(true);
            Assert.Equal("progress", progressResult);
            IReadOnlyList<AgentOutboxItem> progressEvents = await outbox.ReadAsync(100, CancellationToken.None).ConfigureAwait(true);
            double[] progressValues = [.. progressEvents
                .Where(item => item.Type == Contracts.Events.EventTypes.FileTaskProgress)
                .Select(item => ReadProgress(item.PayloadJson))];
            Assert.Contains(0.25d, progressValues);
            Assert.Contains(0.75d, progressValues);

            using AgentDbContext finalContext = factory.CreateDbContext();
            LocalFileTaskRow[] rows = await finalContext.FileTasks.OrderBy(row => row.TargetPath).ToArrayAsync().ConfigureAwait(true);
            Assert.Equal("Succeeded", rows.Single(row => row.Id == firstId).Status);
            Assert.Equal("Cancelled", rows.Single(row => row.Id == secondId).Status);
            Assert.True(rows.Single(row => row.Id == secondId).CancellationRequested);
        }
        finally
        {
            coordinator?.Dispose();
            nodeConfiguration?.Dispose();
            writeCoordinator.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WaitForRowCountAsync(TestContextFactory factory, int expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using AgentDbContext context = factory.CreateDbContext();
            if (await context.FileTasks.CountAsync(timeout.Token).ConfigureAwait(true) == expected)
            {
                return;
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
    }

    private static double ReadProgress(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("progress").GetDouble();
    }

    private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext()
        {
            return new(options);
        }
    }
}
