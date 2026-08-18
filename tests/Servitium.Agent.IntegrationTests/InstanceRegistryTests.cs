using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Archives;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Instances;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceRegistryTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-390")]
    public async Task 合成服务端Zip_解包并持久登记为受管实例()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        var id = Guid.NewGuid();
        string archive = test.CreateStagedArchive("server.zip", ("server.jar", "synthetic-server"));

        LocalInstanceSnapshot instance = await test.Registry.ImportServerArchiveAsync(
            id,
            "Synthetic Server",
            archive,
            Launch(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(instance.OwnedByServitium);
        Assert.Equal("synthetic-server", await File.ReadAllTextAsync(Path.Combine(instance.WorkingDirectory, "server.jar")).ConfigureAwait(true));
        Assert.NotNull(await test.Registry.FindAsync(id, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-410")]
    public async Task 外部绝对路径_登记与删除登记均不触碰目录内容()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string external = Directory.CreateDirectory(Path.Combine(test.Root, "external-server")).FullName;
        string marker = Path.Combine(external, "world-marker.txt");
        await File.WriteAllTextAsync(marker, "preserve").ConfigureAwait(true);
        var id = Guid.NewGuid();

        LocalInstanceSnapshot instance = await test.Registry.AttachExternalAsync(
            id,
            "External Server",
            external,
            Launch(),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(true);
        Assert.False(instance.OwnedByServitium);

        await test.Registry.UnregisterAsync(id, CancellationToken.None).ConfigureAwait(true);

        Assert.True(Directory.Exists(external));
        Assert.Equal("preserve", await File.ReadAllTextAsync(marker).ConfigureAwait(true));
        Assert.Null(await test.Registry.FindAsync(id, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-420")]
    public async Task 删除工作目录_外部实例始终拒绝而受管实例安全删除()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string external = Directory.CreateDirectory(Path.Combine(test.Root, "external-delete-guard")).FullName;
        var externalId = Guid.NewGuid();
        _ = await test.Registry.AttachExternalAsync(externalId, "External", external, Launch(), DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(true);

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Registry.DeleteOwnedWorkingDirectoryAsync(externalId, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("instance-not-owned", rejected.Code);
        Assert.True(Directory.Exists(external));

        var ownedId = Guid.NewGuid();
        LocalInstanceSnapshot owned = await test.Registry.CreateEmptyAsync(
            ownedId,
            "Owned",
            Launch(),
            [],
            null,
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(owned.WorkingDirectory, "marker.txt"), "delete-me").ConfigureAwait(true);

        await test.Registry.DeleteOwnedWorkingDirectoryAsync(ownedId, CancellationToken.None).ConfigureAwait(true);

        Assert.False(Directory.Exists(owned.WorkingDirectory));
        Assert.Null(await test.Registry.FindAsync(ownedId, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-320")]
    public async Task 归档指令_来源不在Agent暂存目录时独立拒绝()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string outside = Path.Combine(test.Root, "outside.zip");
        CreateArchive(outside, ("server.jar", "synthetic"));

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Registry.ImportServerArchiveAsync(Guid.NewGuid(), "Rejected", outside, Launch(), DateTimeOffset.UtcNow, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("archive-unsafe", rejected.Code);
    }

    private static StructuredLaunchRequest Launch()
    {
        return new(Guid.NewGuid(), 1024, 2048, [], "jar", "server.jar", ["nogui"]);
    }

    private static void CreateArchive(string path, params (string Name, string Content)[] entries)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: false);
            writer.Write(content);
        }
    }

    private sealed class RegistryContext : IDisposable
    {
        private readonly SqliteWriteCoordinator _coordinator;

        private RegistryContext(string root, InstanceRegistry registry, SqliteWriteCoordinator coordinator)
        {
            Root = root;
            Registry = registry;
            _coordinator = coordinator;
        }

        internal string Root { get; }
        internal InstanceRegistry Registry { get; }

        internal static async Task<RegistryContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-registry-test-{Guid.NewGuid():N}")).FullName;
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
            InstanceRegistry registry = new(factory, coordinator, new WindowsPathPolicy(), new SafeZipExtractor(ArchiveSafetyLimits.Default), storage, new DpapiSecretProtector());
            return new RegistryContext(root, registry, coordinator);
        }

        internal string CreateStagedArchive(string name, params (string Name, string Content)[] entries)
        {
            string path = Path.Combine(Root, "staging", name);
            CreateArchive(path, entries);
            return path;
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext()
        {
            return new AgentDbContext(options);
        }
    }
}
