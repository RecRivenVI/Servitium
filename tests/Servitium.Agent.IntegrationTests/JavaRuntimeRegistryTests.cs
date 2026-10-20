using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Java;
using Servitium.Agent.Archives;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;
using System.Collections.Generic;

namespace Servitium.Agent.IntegrationTests;

public sealed class JavaRuntimeRegistryTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-430")]
    public async Task 当前Java_探测登记并持久化稳定元数据()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string java = FindJavaExecutable();

        AgentJavaRuntimeResponse first = await test.Registry.RegisterAsync(java, CancellationToken.None).ConfigureAwait(true);
        AgentJavaRuntimeResponse duplicate = await test.Registry.RegisterAsync(java, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(Path.GetFullPath(java), first.ExecutablePath, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(first.Version));
        Assert.False(string.IsNullOrWhiteSpace(first.Vendor));
        Assert.True(first.Architecture is "x64" or "arm64");
        _ = Assert.Single(await test.Registry.ListAsync(CancellationToken.None).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("java.exe")]
    [InlineData("C:\\does-not-exist\\java.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-200")]
    public async Task Java登记_拒绝相对路径缺失文件和非Java可执行文件(string candidate)
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Registry.RegisterAsync(candidate, CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal("java-runtime-invalid", rejected.Code);
        Assert.Empty(await test.Registry.ListAsync(CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-350")]
    public async Task Java探测_返回候选但不自动登记()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);

        IReadOnlyList<AgentJavaRuntimeResponse> candidates = await test.Registry.DiscoverAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Contains(candidates, candidate => candidate.ExecutablePath.Equals(Path.GetFullPath(FindJavaExecutable()), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await test.Registry.ListAsync(CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-370")]
    public async Task JavaZip导入_合成探针被验证登记且删除文件受所有权约束()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string archive = Path.Combine(test.Root, "synthetic-java.zip");
        CreateProbeArchive(archive);

        AgentJavaRuntimeResponse imported = await test.Registry.ImportAsync(archive, CancellationToken.None).ConfigureAwait(true);

        Assert.True(imported.Imported);
        Assert.Equal("21.0.99-synthetic", imported.Version);
        Assert.Equal("Servitium Test VM", imported.VmName);
        string ownedDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(imported.ExecutablePath)!, "..", ".."));
        Assert.True(Directory.Exists(ownedDirectory));
        await test.Registry.UnregisterAsync(imported.Id, deleteFiles: true, force: false, cancellationToken: CancellationToken.None).ConfigureAwait(true);
        Assert.False(Directory.Exists(ownedDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-370")]
    public async Task JavaZip导入_缺少Java可执行文件时回滚新目录()
    {
        using RegistryContext test = await RegistryContext.CreateAsync().ConfigureAwait(true);
        string archive = Path.Combine(test.Root, "missing-java.zip");
        ZipArchive zip = await ZipFile.OpenAsync(archive, ZipArchiveMode.Create, CancellationToken.None).ConfigureAwait(true);
        using (zip)
        {
            _ = zip.CreateEntry("jdk/readme.txt");
        }

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Registry.ImportAsync(archive, CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal("java-runtime-invalid", rejected.Code);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(test.Root, "java")));
    }

    private static string FindJavaExecutable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? java = path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "java.exe"))
            .FirstOrDefault(File.Exists);
        return java ?? throw new InvalidOperationException("The Java integration fixture requires java.exe on PATH.");
    }

    private static void CreateProbeArchive(string archivePath)
    {
        string fixtureAssembly = typeof(JavaProbeFixture.FixtureDescriptor).Assembly.Location;
        string fixtureDirectory = Path.GetDirectoryName(fixtureAssembly)!;
        string fixtureName = Path.GetFileNameWithoutExtension(fixtureAssembly);
        string appHost = Path.Combine(fixtureDirectory, fixtureName + ".exe");
        Assert.True(File.Exists(appHost), "The synthetic Java probe apphost was not built.");
        using ZipArchive zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        AddFile(zip, appHost, "jdk/bin/java.exe");
        foreach (string file in Directory.EnumerateFiles(fixtureDirectory, fixtureName + ".*", SearchOption.TopDirectoryOnly)
                     .Where(file => !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            AddFile(zip, file, "jdk/bin/" + Path.GetFileName(file));
        }
    }

    private static void AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream source = File.OpenRead(sourcePath);
        using Stream destination = entry.Open();
        source.CopyTo(destination);
    }

    private sealed class RegistryContext : IDisposable
    {
        private readonly SqliteWriteCoordinator _coordinator;

        private RegistryContext(string root, JavaRuntimeRegistry registry, SqliteWriteCoordinator coordinator)
        {
            Root = root;
            Registry = registry;
            _coordinator = coordinator;
        }

        internal JavaRuntimeRegistry Registry { get; }
        internal string Root { get; }

        internal static async Task<RegistryContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-java-registry-{Guid.NewGuid():N}")).FullName;
            DbContextOptions<AgentDbContext> options = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "agent.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SqliteWriteCoordinator coordinator = new();
            AgentStorageOptions storage = new(root, Path.Combine(root, "agent.db"), Path.Combine(root, "instances"), Path.Combine(root, "staging"), Path.Combine(root, "java"));
            _ = Directory.CreateDirectory(storage.JavaRoot);
            ArchiveSafetyLimits limits = ArchiveSafetyLimits.Default;
            JavaRuntimeRegistry registry = new(factory, coordinator, new GuidFactory(), new SystemClock(), storage, new SafeZipExtractor(limits), new SafeTarGzipExtractor(limits));
            return new RegistryContext(root, registry, coordinator);
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
                return new AgentDbContext(options);
            }
        }
    }
}
