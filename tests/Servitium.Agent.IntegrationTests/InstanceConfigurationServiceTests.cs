using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Archives;
using Servitium.Agent.Configurations;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceConfigurationServiceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-900")]
    [Trait("TestId", "INT-910")]
    [Trait("TestId", "INT-930")]
    public async Task 配置服务_保留BOM注释并备份限额与恢复()
    {
        using ConfigurationContext test = await ConfigurationContext.CreateAsync().ConfigureAwait(true);
        string path = Path.Combine(test.WorkingDirectory, "server.properties");
        byte[] body = Encoding.UTF8.GetBytes("# 保留\r\nmotd : 初始\r\nmax-players=20\r\ncustom-key=value\r\n");
        byte[] original = new byte[Encoding.UTF8.Preamble.Length + body.Length];
        Encoding.UTF8.Preamble.CopyTo(original.AsSpan());
        body.CopyTo(original.AsSpan(Encoding.UTF8.Preamble.Length));
        await File.WriteAllBytesAsync(path, original).ConfigureAwait(true);

        ConfigurationDetailsDto details = await test.Service.ReadAsync(test.InstanceId, "server-properties", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("utf-8-bom", details.Encoding);
        Assert.Equal("crlf", details.LineEnding);
        Assert.Contains("custom-key", details.UnknownKeys ?? []);
        ConfigurationDetailsDto updated = await test.Service.WriteAsync(
            test.InstanceId,
            "server-properties",
            new(new Dictionary<string, string?> { ["motd"] = "更新" }, null, details.Revision),
            CancellationToken.None).ConfigureAwait(true);
        byte[] firstUpdate = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        Assert.True(firstUpdate.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("# 保留\r\nmotd : 更新\r\nmax-players=20\r\ncustom-key=value\r\n", Encoding.UTF8.GetString(firstUpdate.AsSpan(Encoding.UTF8.Preamble.Length)));

        DomainException stale = await Assert.ThrowsAsync<DomainException>(() => test.Service.WriteAsync(
            test.InstanceId,
            "server-properties",
            new(new Dictionary<string, string?> { ["motd"] = "不应覆盖" }, null, details.Revision),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("concurrency-conflict", stale.Code);

        for (int index = 0; index < 11; index++)
        {
            updated = await test.Service.WriteAsync(
                test.InstanceId,
                "server-properties",
                new(new Dictionary<string, string?> { ["max-players"] = (30 + index).ToString(System.Globalization.CultureInfo.InvariantCulture) }, null, updated.Revision),
                CancellationToken.None).ConfigureAwait(true);
        }

        IReadOnlyList<ConfigurationBackupDto> backups = await test.Service.ListBackupsAsync(test.InstanceId, "server-properties", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(10, backups.Count);
        ConfigurationBackupDto restore = backups[^1];
        ConfigurationDetailsDto restored = await test.Service.RestoreBackupAsync(test.InstanceId, "server-properties", restore.Id, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(restore.Revision, restored.Revision);
        Assert.Equal(10, (await test.Service.ListBackupsAsync(test.InstanceId, "server-properties", CancellationToken.None).ConfigureAwait(true)).Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-940")]
    public async Task 配置识别_多个候选时标记冲突并拒绝写入()
    {
        using ConfigurationContext test = await ConfigurationContext.CreateAsync().ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "config"));
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "paper-global.yml"), "value: one\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "config", "paper-global.yml"), "value: two\n").ConfigureAwait(true);

        ConfigurationDto conflict = Assert.Single(
            await test.Service.ListAsync(test.InstanceId, CancellationToken.None).ConfigureAwait(true),
            configuration => configuration.Key == "paper-global");
        Assert.True(conflict.HasConflict);
        Assert.Equal(2, conflict.Candidates?.Count);
        ConfigurationDetailsDto details = await test.Service.ReadAsync(test.InstanceId, "paper-global", CancellationToken.None).ConfigureAwait(true);
        Assert.Contains("configuration-candidates-conflict", details.Diagnostics ?? []);
        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() => test.Service.WriteAsync(
            test.InstanceId,
            "paper-global",
            new(new Dictionary<string, string?> { ["value"] = "three" }, null, details.Revision),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("conflict", rejected.Code);
    }

    private sealed class ConfigurationContext : IDisposable
    {
        private readonly SqliteWriteCoordinator _coordinator;

        private ConfigurationContext(string root, Guid instanceId, string workingDirectory, InstanceConfigurationService service, SqliteWriteCoordinator coordinator)
        {
            Root = root;
            InstanceId = instanceId;
            WorkingDirectory = workingDirectory;
            Service = service;
            _coordinator = coordinator;
        }

        internal string Root { get; }
        internal Guid InstanceId { get; }
        internal string WorkingDirectory { get; }
        internal InstanceConfigurationService Service { get; }

        internal static async Task<ConfigurationContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-config-{Guid.NewGuid():N}")).FullName;
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
            InstanceRegistry instances = new(
                factory,
                coordinator,
                pathPolicy,
                new SafeZipExtractor(ArchiveSafetyLimits.Default),
                storage,
                new DpapiSecretProtector());
            var id = Guid.NewGuid();
            LocalInstanceSnapshot instance = await instances.CreateEmptyAsync(
                id,
                "Synthetic Configuration",
                new StructuredLaunchRequest(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []),
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            return new(root, id, instance.WorkingDirectory, new InstanceConfigurationService(instances, storage, factory, coordinator, new SystemClock()), coordinator);
        }

        public void Dispose()
        {
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
