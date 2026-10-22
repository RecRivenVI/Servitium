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
using Servitium.Agent.Files;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Files;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;
using Servitium.Infrastructure.Networking;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceFileServiceTests
{
    private static readonly string[] FirstPageNames = ["a.txt", "b.txt"];
    private static readonly string[] SecondPageNames = ["c.txt"];

    [Fact]
    public async Task 文件列表观察独占占用和未知权限_探测不修改内容且不放开运行时写入()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        string lockedPath = Path.Combine(test.WorkingDirectory, "locked.txt");
        string freePath = Path.Combine(test.WorkingDirectory, "free.txt");
        string readOnlyPath = Path.Combine(test.WorkingDirectory, "read-only.txt");
        await File.WriteAllTextAsync(lockedPath, "locked fixture").ConfigureAwait(true);
        await File.WriteAllTextAsync(freePath, "free fixture").ConfigureAwait(true);
        await File.WriteAllTextAsync(readOnlyPath, "read-only fixture").ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "directory"));
        DateTime timestamp = File.GetLastWriteTimeUtc(freePath);
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        try
        {
            using (FileStream occupied = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                IReadOnlyList<FileEntryDto> entries = await test.Files.ListAsync(test.InstanceId, string.Empty, CancellationToken.None).ConfigureAwait(true);
                Assert.True(entries.Single(entry => entry.Path == "locked.txt").IsLocked);
                Assert.False(entries.Single(entry => entry.Path == "free.txt").IsLocked);
                Assert.Null(entries.Single(entry => entry.Path == "read-only.txt").IsLocked);
                Assert.Null(entries.Single(entry => entry.IsDirectory).IsLocked);
            }
            IReadOnlyList<FileEntryDto> released = await test.Files.ListAsync(test.InstanceId, string.Empty, CancellationToken.None).ConfigureAwait(true);
            Assert.False(released.Single(entry => entry.Path == "locked.txt").IsLocked);
            Assert.Equal("free fixture", await File.ReadAllTextAsync(freePath).ConfigureAwait(true));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(freePath));
            _ = await test.Instances.UpdateRuntimeAsync(test.InstanceId, "Running", 1234, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(true);
            IReadOnlyList<FileEntryDto> running = await test.Files.ListAsync(test.InstanceId, string.Empty, CancellationToken.None).ConfigureAwait(true);
            Assert.False(running.Single(entry => entry.Path == "free.txt").IsLocked);
            DomainException blocked = await Assert.ThrowsAsync<DomainException>(() => test.Files.WriteTextAsync(test.InstanceId, "free.txt", "overwrite", "utf-8", CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("instance-not-stopped", blocked.Code);
            Assert.Equal("free fixture", await File.ReadAllTextAsync(freePath).ConfigureAwait(true));
        }
        finally { File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-800")]
    [Trait("TestId", "INT-710")]
    [Trait("TestId", "INT-810")]
    [Trait("TestId", "INT-990")]
    public async Task 实例目录_文件操作与Utf8原子读写保持在根内()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);

        FileEntryDto directory = await test.Files.CreateDirectoryAsync(test.InstanceId, "config", CancellationToken.None).ConfigureAwait(true);
        Assert.True(directory.IsDirectory);
        AgentTextFileResponse written = await test.Files.WriteTextAsync(
            test.InstanceId,
            "config/server.properties",
            "motd=合成测试\r\nonline-mode=true\r\n",
            "utf-8",
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("crlf", written.LineEnding);

        AgentTextFileResponse read = await test.Files.ReadTextAsync(test.InstanceId, "config/server.properties", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(written.Content, read.Content);
        _ = await test.Files.CopyAsync(test.InstanceId, "config/server.properties", "config/copy.properties", CancellationToken.None).ConfigureAwait(true);
        _ = await test.Files.MoveAsync(test.InstanceId, "config/copy.properties", "config/moved.properties", CancellationToken.None).ConfigureAwait(true);
        FileEntryDto renamed = await test.Files.RenameAsync(test.InstanceId, "config/moved.properties", "MOVED.properties", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("config/MOVED.properties", renamed.Path);
        IReadOnlyList<FileEntryDto> entries = await test.Files.ListAsync(test.InstanceId, "config", CancellationToken.None).ConfigureAwait(true);
        Assert.Collection(
            entries,
            entry => Assert.Equal("config/MOVED.properties", entry.Path),
            entry => Assert.Equal("config/server.properties", entry.Path));

        await test.Files.DeleteAsync(test.InstanceId, "config/MOVED.properties", CancellationToken.None).ConfigureAwait(true);
        Assert.False(File.Exists(Path.Combine(test.WorkingDirectory, "config", "MOVED.properties")));
        FileSystemStatusDto status = await test.Files.GetStatusAsync(test.InstanceId, CancellationToken.None).ConfigureAwait(true);
        Assert.True(status.TotalBytes > 0);
        Assert.True(status.AvailableBytes > 0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-800")]
    public async Task 目录列表_使用绑定查询条件的不透明游标稳定翻页()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, name), name).ConfigureAwait(true);
        }

        FileEntryPageDto first = await test.Files.ListAsync(
            test.InstanceId,
            string.Empty,
            null,
            2,
            ".txt",
            "name",
            false,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(FirstPageNames, first.Items.Select(item => item.Path));
        Assert.NotNull(first.NextCursor);

        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "aa.txt"), "inserted").ConfigureAwait(true);
        FileEntryPageDto second = await test.Files.ListAsync(
            test.InstanceId,
            string.Empty,
            first.NextCursor,
            2,
            ".txt",
            "name",
            false,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(SecondPageNames, second.Items.Select(item => item.Path));
        Assert.Null(second.NextCursor);

        DomainException changedQuery = await Assert.ThrowsAsync<DomainException>(() => test.Files.ListAsync(
            test.InstanceId,
            string.Empty,
            first.NextCursor,
            2,
            null,
            "name",
            false,
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("validation-failed", changedQuery.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-800")]
    [Trait("TestId", "INT-810")]
    public async Task 复制移动_同名策略安全生效且拒绝祖先后代目标()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "source.txt"), "new").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "target.txt"), "old").ConfigureAwait(true);

        FileEntryDto skipped = await test.Files.CopyAsync(test.InstanceId, "source.txt", "target.txt", "skip", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("target.txt", skipped.Path);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "target.txt")).ConfigureAwait(true));

        _ = await test.Files.CopyAsync(test.InstanceId, "source.txt", "target.txt", "overwrite", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "target.txt")).ConfigureAwait(true));
        FileEntryDto kept = await test.Files.CopyAsync(test.InstanceId, "source.txt", "target.txt", "keep-both", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("target (2).txt", kept.Path);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "target (2).txt")).ConfigureAwait(true));

        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "source-dir"));
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "source-dir", "new.txt"), "new").ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "target-dir"));
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "target-dir", "old.txt"), "old").ConfigureAwait(true);
        _ = await test.Files.CopyAsync(test.InstanceId, "source-dir", "target-dir", "overwrite", CancellationToken.None).ConfigureAwait(true);
        Assert.True(File.Exists(Path.Combine(test.WorkingDirectory, "target-dir", "new.txt")));
        Assert.False(File.Exists(Path.Combine(test.WorkingDirectory, "target-dir", "old.txt")));

        DomainException nested = await Assert.ThrowsAsync<DomainException>(() => test.Files.CopyAsync(
            test.InstanceId,
            "source-dir",
            "source-dir/child",
            "overwrite",
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("validation-failed", nested.Code);
        Assert.False(Directory.Exists(Path.Combine(test.WorkingDirectory, "source-dir", "child")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:\\Windows\\escape.txt")]
    [InlineData("config/file.txt:stream")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-310")]
    public async Task 文件操作_拒绝逃逸绝对路径和备用数据流(string path)
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);

        DomainException rejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Files.CreateFileAsync(test.InstanceId, path, CancellationToken.None)).ConfigureAwait(true);

        Assert.True(rejected.Code is "path-escapes-root" or "path-name-invalid");
        Assert.False(File.Exists(Path.Combine(test.Root, "escape.txt")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-850")]
    public async Task 文本读写_拒绝无效Utf8与运行中修改()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        string invalid = Path.Combine(test.WorkingDirectory, "invalid.txt");
        await File.WriteAllBytesAsync(invalid, [0xff, 0xfe, 0xfd]).ConfigureAwait(true);

        DomainException decodeRejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Files.ReadTextAsync(test.InstanceId, "invalid.txt", CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("config-parse-failed", decodeRejected.Code);

        _ = await test.Instances.UpdateRuntimeAsync(
            test.InstanceId,
            "Running",
            1234,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(true);
        DomainException runningRejected = await Assert.ThrowsAsync<DomainException>(() =>
            test.Files.WriteTextAsync(test.InstanceId, "invalid.txt", "safe", "utf-8", CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("instance-not-stopped", runningRejected.Code);
        Assert.Equal(new byte[] { 0xff, 0xfe, 0xfd }, await File.ReadAllBytesAsync(invalid).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("gbk", "中文-GBK")]
    [InlineData("big5", "繁體-Big5")]
    [InlineData("shift_jis", "日本語-ShiftJIS")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-890")]
    public async Task 文本读写_显式代码页无损往返(string encoding, string text)
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        AgentTextFileResponse written = await test.Files.WriteTextAsync(
            test.InstanceId,
            "encoded.txt",
            text,
            encoding,
            CancellationToken.None).ConfigureAwait(true);
        AgentTextFileResponse read = await test.Files.ReadTextAsync(
            test.InstanceId,
            "encoded.txt",
            encoding,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(text, written.Content);
        Assert.Equal(text, read.Content);
    }

    [Theory]
    [InlineData("gbk", 936, "测试.txt")]
    [InlineData("big5", 950, "測試.txt")]
    [InlineData("shift_jis", 932, "テスト.txt")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-860")]
    public async Task Zip条目名_所选代码页压缩解压往返(string encodingName, int codePage, string entryName)
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string archivePath = Path.Combine(test.WorkingDirectory, "encoded.zip");
        using (FileStream stream = new(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, false, Encoding.GetEncoding(codePage)))
        {
            Stream entry = await archive.CreateEntry(entryName).OpenAsync(CancellationToken.None).ConfigureAwait(true);
            await using (entry.ConfigureAwait(true))
            {
                await entry.WriteAsync(Encoding.UTF8.GetBytes("synthetic")).ConfigureAwait(true);
            }
        }

        _ = await test.Files.ExtractAsync(
            test.InstanceId,
            "encoded.zip",
            "extracted",
            encodingName,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("synthetic", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "extracted", entryName)).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-980")]
    public async Task 长路径_文件目录复制移动重命名与删除均可用()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        string relative = string.Empty;
        for (int index = 0; index < 10; index++)
        {
            string segment = $"segment-{index:D2}-abcdefghijklmnopqrstuvwxyz";
            relative = relative.Length == 0 ? segment : relative + "/" + segment;
            _ = await test.Files.CreateDirectoryAsync(test.InstanceId, relative, CancellationToken.None).ConfigureAwait(true);
        }

        Assert.True(Path.Combine(test.WorkingDirectory, relative.Replace('/', Path.DirectorySeparatorChar)).Length > 260);
        _ = await test.Files.WriteTextAsync(test.InstanceId, relative + "/source.txt", "long-path", "utf-8", CancellationToken.None).ConfigureAwait(true);
        _ = await test.Files.CopyAsync(test.InstanceId, relative + "/source.txt", relative + "/copy.txt", CancellationToken.None).ConfigureAwait(true);
        _ = await test.Files.MoveAsync(test.InstanceId, relative + "/copy.txt", relative + "/moved.txt", CancellationToken.None).ConfigureAwait(true);
        FileEntryDto renamed = await test.Files.RenameAsync(test.InstanceId, relative + "/moved.txt", "MOVED.txt", CancellationToken.None).ConfigureAwait(true);
        Assert.EndsWith("/MOVED.txt", renamed.Path, StringComparison.Ordinal);
        await test.Files.DeleteAsync(test.InstanceId, relative + "/MOVED.txt", CancellationToken.None).ConfigureAwait(true);
        Assert.False(File.Exists(Path.Combine(test.WorkingDirectory, relative.Replace('/', Path.DirectorySeparatorChar), "MOVED.txt")));
    }

    [Theory]
    [InlineData("utf-8", "压缩测试.txt")]
    [InlineData("gbk", "压缩测试.txt")]
    [InlineData("big5", "壓縮測試.txt")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-860")]
    public async Task 压缩与解压_所选条目编码完整往返(string encoding, string fileName)
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        _ = await test.Files.CreateDirectoryAsync(test.InstanceId, "source", CancellationToken.None).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(test.WorkingDirectory, "source", fileName), "synthetic").ConfigureAwait(true);

        _ = await test.Files.CompressAsync(test.InstanceId, ["source"], "roundtrip.zip", encoding, CancellationToken.None).ConfigureAwait(true);
        _ = await test.Files.ExtractAsync(test.InstanceId, "roundtrip.zip", "roundtrip", encoding, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("synthetic", await File.ReadAllTextAsync(Path.Combine(test.WorkingDirectory, "roundtrip", "source", fileName)).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-190")]
    public async Task Url下载_不会把请求目标自动加入私有源白名单()
    {
        using FileServiceContext test = await FileServiceContext.CreateAsync().ConfigureAwait(true);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => test.Files.DownloadFromUrlAsync(
            test.InstanceId,
            new Uri("http://127.0.0.1:9/payload"),
            "payload.bin",
            false,
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("url-not-allowed", exception.Code);
        Assert.False(File.Exists(Path.Combine(test.WorkingDirectory, "payload.bin")));
    }

    private sealed class FileServiceContext : IDisposable
    {
        private readonly SqliteWriteCoordinator _coordinator;

        private FileServiceContext(
            string root,
            Guid instanceId,
            string workingDirectory,
            InstanceRegistry instances,
            InstanceFileService files,
            SqliteWriteCoordinator coordinator)
        {
            Root = root;
            InstanceId = instanceId;
            WorkingDirectory = workingDirectory;
            Instances = instances;
            Files = files;
            _coordinator = coordinator;
        }

        internal string Root { get; }
        internal Guid InstanceId { get; }
        internal string WorkingDirectory { get; }
        internal InstanceRegistry Instances { get; }
        internal InstanceFileService Files { get; }

        internal static async Task<FileServiceContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-files-{Guid.NewGuid():N}")).FullName;
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
            SafeZipExtractor zipExtractor = new(ArchiveSafetyLimits.Default);
            InstanceRegistry instances = new(factory, coordinator, pathPolicy, zipExtractor, storage, new DpapiSecretProtector());
            var id = Guid.NewGuid();
            LocalInstanceSnapshot instance = await instances.CreateEmptyAsync(
                id,
                "Synthetic Files",
                new StructuredLaunchRequest(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []),
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            RestrictedOutboundHttpClient outbound = new(
                new SystemHostAddressResolver(),
                new RestrictedHttpOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1024 * 1024, TimeSpan.FromSeconds(5), 0));
            return new FileServiceContext(root, id, instance.WorkingDirectory, instances, new InstanceFileService(instances, pathPolicy, zipExtractor, outbound), coordinator);
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
