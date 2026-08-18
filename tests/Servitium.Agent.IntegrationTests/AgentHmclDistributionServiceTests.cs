using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Archives;
using Servitium.Agent.ControlPlane;
using Servitium.Agent.Distribution;
using Servitium.Agent.Files;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Instances;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Distribution;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentHmclDistributionServiceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1470")]
    [Trait("TestId", "INT-1480")]
    public async Task 合成实例_采集过滤并按受管清单同步且保留本地文件()
    {
        DistributionContext test = await DistributionContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable testLifetime = test.ConfigureAwait(false);
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "mods"));
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "config"));
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "mods", "a.jar"), "collected-mod").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "config", "setting.txt"), "collected-config").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "options.txt"), "private-player-state").ConfigureAwait(true);

        string streamId = Guid.NewGuid().ToString("N");
        AgentCollectedDirectoryResult collected = await test.Service.CollectAsync(
            new AgentCollectDirectoryForReleaseCommand(test.InstanceId, streamId),
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(2, collected.FileCount);
        Assert.Contains("options.txt", collected.ExcludedPaths.Keys);
        byte[] collectedArchive = await ReadStreamAsync(test.Outbound, streamId).ConfigureAwait(true);
        string collectedPath = Path.Combine(test.Root, "collected.zip");
        await File.WriteAllBytesAsync(collectedPath, collectedArchive).ConfigureAwait(true);
        HmclArchivePlan collectedPlan = HmclDistributionStore.InspectContentArchive(collectedPath);
        Assert.Equal(["config/setting.txt", "mods/a.jar"], [.. collectedPlan.Files.Select(file => file.ReleasePath)]);

        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "unmanaged.txt"), "preserve-me").ConfigureAwait(true);
        string first = CreateZip(test.Root, "first.zip", new Dictionary<string, string>
        {
            ["mods/a.jar"] = "release-v1",
            ["config/old.txt"] = "remove-on-next-sync",
        });
        AgentSyncFromReleaseResult firstResult = await test.Service.SyncArchiveAsync(
            new AgentSyncFromReleaseCommand(test.InstanceId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), ["mods/a.jar", "config/old.txt"]),
            first,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(1, firstResult.Added);
        Assert.Equal(1, firstResult.Replaced);
        Assert.Equal("release-v1", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "mods", "a.jar")).ConfigureAwait(true));

        string second = CreateZip(test.Root, "second.zip", new Dictionary<string, string>
        {
            ["mods/a.jar"] = "release-v2",
            ["config/new.txt"] = "new-managed-file",
        });
        AgentSyncFromReleaseResult secondResult = await test.Service.SyncArchiveAsync(
            new AgentSyncFromReleaseCommand(test.InstanceId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), ["mods/a.jar", "config/new.txt"]),
            second,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(1, secondResult.Added);
        Assert.Equal(1, secondResult.Replaced);
        Assert.Equal(1, secondResult.Deleted);
        Assert.Equal("release-v2", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "mods", "a.jar")).ConfigureAwait(true));
        Assert.False(File.Exists(Path.Combine(test.WorkingDirectory, "config", "old.txt")));
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "unmanaged.txt")).ConfigureAwait(true));

        string mismatch = CreateZip(test.Root, "mismatch.zip", new Dictionary<string, string> { ["mods/wrong.jar"] = "wrong" });
        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() => test.Service.SyncArchiveAsync(
            new AgentSyncFromReleaseCommand(test.InstanceId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), ["mods/expected.jar"]),
            mismatch,
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("hmcl-release-validation-failed", rejected.Code);
        Assert.False(File.Exists(mismatch));

        string invalid = CreateZip(test.Root, "invalid.zip", new Dictionary<string, string> { ["mods/a.jar"] = "invalid-command" });
        DomainException invalidRejected = await Assert.ThrowsAsync<DomainException>(() => test.Service.SyncArchiveAsync(
            new AgentSyncFromReleaseCommand(test.InstanceId, Guid.Empty, Guid.NewGuid().ToString("N"), ["mods/a.jar"]),
            invalid,
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("validation-failed", invalidRejected.Code);
        Assert.False(File.Exists(invalid));

        await test.SetStatusAsync("Running").ConfigureAwait(true);
        DomainException runningRejected = await Assert.ThrowsAsync<DomainException>(() => test.Service.CollectAsync(
            new AgentCollectDirectoryForReleaseCommand(test.InstanceId, Guid.NewGuid().ToString("N")),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("instance-not-stopped", runningRejected.Code);
    }

    private static async Task<byte[]> ReadStreamAsync(OutboundFileTransferManager outbound, string streamId)
    {
        using MemoryStream destination = new();
        await foreach (AgentMessage message in outbound.ReadOutboundAsync(CancellationToken.None).ConfigureAwait(true))
        {
            if (message.StreamChunk?.StreamId != streamId)
            {
                continue;
            }

            await destination.WriteAsync(message.StreamChunk.Data.Memory).ConfigureAwait(true);
            if (message.StreamChunk.EndOfStream)
            {
                return destination.ToArray();
            }
        }

        throw new InvalidDataException("The content collection stream ended without a final chunk.");
    }

    private static string CreateZip(string root, string name, IReadOnlyDictionary<string, string> files)
    {
        string path = Path.Combine(root, name);
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        foreach ((string relative, string content) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
            using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
        }

        return path;
    }

    private sealed class DistributionContext : IAsyncDisposable
    {
        private readonly TestContextFactory _factory;
        private readonly SqliteWriteCoordinator _coordinator;
        private readonly InboundFileTransferStore _inbound;

        private DistributionContext(
            string root,
            Guid instanceId,
            string workingDirectory,
            TestContextFactory factory,
            SqliteWriteCoordinator coordinator,
            InboundFileTransferStore inbound,
            OutboundFileTransferManager outbound,
            AgentHmclDistributionService service)
        {
            Root = root;
            InstanceId = instanceId;
            WorkingDirectory = workingDirectory;
            _factory = factory;
            _coordinator = coordinator;
            _inbound = inbound;
            Outbound = outbound;
            Service = service;
        }

        internal string Root { get; }
        internal Guid InstanceId { get; }
        internal string WorkingDirectory { get; }
        internal OutboundFileTransferManager Outbound { get; }
        internal AgentHmclDistributionService Service { get; }

        internal static async Task<DistributionContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-agent-hmcl-{Guid.NewGuid():N}")).FullName;
            AgentStorageOptions storage = new(root, Path.Combine(root, "agent.db"), Path.Combine(root, "instances"), Path.Combine(root, "staging"));
            _ = Directory.CreateDirectory(storage.ManagedInstanceRoot);
            _ = Directory.CreateDirectory(storage.StagingRoot);
            DbContextOptions<AgentDbContext> databaseOptions = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + storage.DatabasePath + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(databaseOptions);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SqliteWriteCoordinator coordinator = new();
            WindowsPathPolicy paths = new();
            SafeZipExtractor extractor = new(ArchiveSafetyLimits.Default);
            InstanceRegistry instances = new(factory, coordinator, paths, extractor, storage, new DpapiSecretProtector());
            var instanceId = Guid.NewGuid();
            LocalInstanceSnapshot instance = await instances.CreateEmptyAsync(
                instanceId,
                "Synthetic HMCL",
                new StructuredLaunchRequest(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []),
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            RestrictedOutboundHttpClient http = new(
                new SystemHostAddressResolver(),
                new RestrictedHttpOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1024 * 1024, TimeSpan.FromSeconds(5), 0));
            InstanceFileService files = new(instances, paths, extractor, http);
            InboundFileTransferStore inbound = new(storage);
            OutboundFileTransferManager outbound = new(files);
            AgentHmclDistributionService service = new(instances, paths, storage, inbound, outbound);
            return new DistributionContext(root, instanceId, instance.WorkingDirectory, factory, coordinator, inbound, outbound, service);
        }

        internal async Task SetStatusAsync(string status)
        {
            using AgentDbContext context = _factory.CreateDbContext();
            LocalInstanceRow row = await context.Instances.SingleAsync(item => item.Id == InstanceId).ConfigureAwait(true);
            row.Status = status;
            _ = await context.SaveChangesAsync().ConfigureAwait(true);
        }

        public async ValueTask DisposeAsync()
        {
            await Outbound.DisposeAsync().ConfigureAwait(false);
            await _inbound.DisposeAsync().ConfigureAwait(false);
            _coordinator.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
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
