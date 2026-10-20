using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Archives;
using Servitium.Agent.Instances;
using Servitium.Agent.Mods;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceModServiceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-950")]
    public async Task 模组扫描_解析七种元数据并保持启停标识稳定()
    {
        using ModContext test = await ModContext.CreateAsync().ConfigureAwait(true);
        _ = await test.CreateJarAsync("fabric.jar", "fabric.mod.json", /*lang=json,strict*/ """{"id":"fixture_fabric","name":"Fabric Fixture","version":"1.2.3","authors":["A"],"depends":{"minecraft":"*"}}""").ConfigureAwait(true);
        _ = await test.CreateJarAsync("quilt.jar", "quilt.mod.json", /*lang=json,strict*/ """{"quilt_loader":{"id":"fixture_quilt","version":"2.0","metadata":{"name":"Quilt Fixture","contributors":{"B":"Owner"}},"depends":[{"id":"minecraft"}]}}""").ConfigureAwait(true);
        _ = await test.CreateJarAsync("neoforge.jar", "META-INF/neoforge.mods.toml", "modId=\"fixture_neoforge\"\ndisplayName=\"NeoForge Fixture\"\nversion=\"3.0\"\nauthors=\"C\"\n[[dependencies.fixture_neoforge]]\nmodId=\"minecraft\"\n").ConfigureAwait(true);
        _ = await test.CreateJarAsync("forge.jar", "META-INF/mods.toml", "modId=\"fixture_forge\"\ndisplayName=\"Forge Fixture\"\nversion=\"4.0\"\n[[dependencies.fixture_forge]]\nmodId=\"forge\"\n").ConfigureAwait(true);
        _ = await test.CreateJarAsync("legacy.jar", "mcmod.info", /*lang=json,strict*/ """[{"modid":"fixture_legacy","name":"Legacy Fixture","version":"5.0","authorList":["D"],"dependencies":["minecraft"]}]""").ConfigureAwait(true);
        _ = await test.CreateJarAsync("paper.jar", "paper-plugin.yml", "name: PaperFixture\nversion: 6.0\nauthor: E\ndepend: [PaperAPI]\n").ConfigureAwait(true);
        _ = await test.CreateJarAsync("bukkit.jar", "plugin.yml", "name: BukkitFixture\nversion: 7.0\nauthors: [F, G]\ndepend: [Vault]\n").ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(test.ModsDirectory, "broken.jar"), [1, 2, 3, 4]).ConfigureAwait(true);

        IReadOnlyList<ModDetailsDto> items = await test.Service.ListAsync(test.InstanceId, "mods", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(8, items.Count);
        Assert.Equal("fixture_fabric", items.Single(item => item.Loader == "fabric").Identifier);
        Assert.Contains("minecraft", items.Single(item => item.Loader == "fabric").Dependencies!);
        Assert.Equal("fixture_quilt", items.Single(item => item.Loader == "quilt").Identifier);
        Assert.Equal("fixture_neoforge", items.Single(item => item.Loader == "neoforge").Identifier);
        Assert.Contains("minecraft", items.Single(item => item.Loader == "neoforge").Dependencies!);
        Assert.Equal("fixture_forge", items.Single(item => item.Loader == "forge").Identifier);
        Assert.Equal("fixture_legacy", items.Single(item => item.Loader == "forge-legacy").Identifier);
        Assert.Equal("PaperFixture", items.Single(item => item.Loader == "paper").Identifier);
        Assert.Equal("BukkitFixture", items.Single(item => item.Loader == "bukkit").Identifier);
        Assert.True(items.Single(item => item.RelativePath.EndsWith("broken.jar", StringComparison.Ordinal)).MetadataUnreadable);

        ModDetailsDto fabric = items.Single(item => item.Identifier == "fixture_fabric");
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "config"));
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "config", "fixture_fabric-server.toml"), "value=true").ConfigureAwait(true);
        _ = Assert.Single(await test.Service.ListConfigurationFilesAsync(test.InstanceId, fabric.Id, "mods", CancellationToken.None).ConfigureAwait(true));
        ModDetailsDto disabled = await test.Service.SetEnabledAsync(test.InstanceId, fabric.Id, "mods", false, CancellationToken.None).ConfigureAwait(true);
        Assert.False(disabled.Enabled);
        Assert.Equal(fabric.Id, disabled.Id);
        ModDetailsDto enabled = await test.Service.SetEnabledAsync(test.InstanceId, disabled.Id, "mods", true, CancellationToken.None).ConfigureAwait(true);
        Assert.True(enabled.Enabled);
        Assert.Equal(fabric.Id, enabled.Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-970")]
    public async Task 模组启停_文件占用返回稳定错误码()
    {
        using ModContext test = await ModContext.CreateAsync().ConfigureAwait(true);
        string path = await test.CreateJarAsync("locked.jar", "fabric.mod.json", /*lang=json,strict*/ """{"id":"locked","name":"Locked"}""").ConfigureAwait(true);
        ModDetailsDto item = Assert.Single(await test.Service.ListAsync(test.InstanceId, "mods", CancellationToken.None).ConfigureAwait(true));
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            test.Service.SetEnabledAsync(test.InstanceId, item.Id, "mods", false, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("file-locked", exception.Code);
    }

    private sealed class ModContext : IDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteCoordinator _coordinator;

        private ModContext(string root, SqliteWriteCoordinator coordinator, Guid instanceId, string workingDirectory, InstanceModService service)
        {
            _root = root;
            _coordinator = coordinator;
            InstanceId = instanceId;
            WorkingDirectory = workingDirectory;
            Service = service;
        }

        internal Guid InstanceId { get; }
        internal string WorkingDirectory { get; }
        internal string ModsDirectory => Path.Combine(WorkingDirectory, "mods");
        internal InstanceModService Service { get; }

        internal static async Task<ModContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-mods-" + Guid.NewGuid().ToString("N"))).FullName;
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

            SqliteWriteCoordinator coordinator = new();
            WindowsPathPolicy pathPolicy = new();
            InstanceRegistry registry = new(factory, coordinator, pathPolicy, new SafeZipExtractor(ArchiveSafetyLimits.Default), storage, new TestSecretProtector());
            var id = Guid.NewGuid();
            LocalInstanceSnapshot instance = await registry.CreateEmptyAsync(
                id,
                "Synthetic Mods",
                new StructuredLaunchRequest(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []),
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            _ = Directory.CreateDirectory(Path.Combine(instance.WorkingDirectory, "mods"));
            return new(root, coordinator, id, instance.WorkingDirectory, new InstanceModService(registry));
        }

        internal async Task<string> CreateJarAsync(string fileName, string metadataPath, string content)
        {
            string path = Path.Combine(ModsDirectory, fileName);
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using ZipArchive archive = new(stream, ZipArchiveMode.Create, true);
                ZipArchiveEntry entry = archive.CreateEntry(metadataPath, CompressionLevel.NoCompression);
                Stream entryStream = await entry.OpenAsync(CancellationToken.None).ConfigureAwait(true);
                await using (entryStream.ConfigureAwait(true))
                {
                    using StreamWriter writer = new(entryStream, new UTF8Encoding(false));
                    await writer.WriteAsync(content).ConfigureAwait(true);
                }
            }

            return path;
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext()
        {
            return new(options);
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return ciphertext.ToArray();
        }
    }
}
