using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Archives;
using Servitium.Agent.Paths;
using Servitium.Domain.Common;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class PathAndArchiveSecurityTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-400")]
    public void 外部绝对路径_合成可写目录被接受且冲突目录被拒绝()
    {
        using TemporaryDirectory temporary = new();
        string first = Directory.CreateDirectory(Path.Combine(temporary.Path, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(temporary.Path, "second")).FullName;
        WindowsPathPolicy policy = new();

        Assert.Equal(first, policy.ValidateExternalWorkingDirectory(first, [], []));
        DomainException conflict = Assert.Throws<DomainException>(() =>
            policy.ValidateExternalWorkingDirectory(second, [temporary.Path], []));
        Assert.Equal("instance-path-conflict", conflict.Code);
    }

    [Theory]
    [InlineData("relative\\server")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\data:stream")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-140")]
    public void 外部路径_拒绝非本地绝对路径与备用数据流(string candidate)
    {
        WindowsPathPolicy policy = new();
        DomainException exception = Assert.Throws<DomainException>(() =>
            policy.ValidateExternalWorkingDirectory(candidate, [], []));
        Assert.Equal("instance-path-invalid", exception.Code);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("world.")]
    [InlineData("bad:name")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-150")]
    public void 文件名_拒绝Windows非法形式(string name)
    {
        DomainException exception = Assert.Throws<DomainException>(() => WindowsPathPolicy.ValidateFileName(name));
        Assert.Equal("path-name-invalid", exception.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-390")]
    public async Task 安全Zip_完整预检后解包合成服务端()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "server.zip");
        CreateArchive(archive, ("server.jar", "synthetic"), ("config/server.properties", "motd=test"));
        string target = Path.Combine(temporary.Path, "instance");
        SafeZipExtractor extractor = new(ArchiveSafetyLimits.Default);

        SafeArchivePlan plan = await extractor.ExtractToNewDirectoryAsync(archive, target, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("synthetic", await File.ReadAllTextAsync(Path.Combine(target, "server.jar")).ConfigureAwait(true));
        Assert.Equal("motd=test", await File.ReadAllTextAsync(Path.Combine(target, "config", "server.properties")).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/../../escape.txt")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-160")]
    public void 恶意Zip路径_在写文件前被拒绝(string entryName)
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "unsafe.zip");
        CreateArchive(archive, (entryName, "payload"));
        SafeZipExtractor extractor = new(ArchiveSafetyLimits.Default);

        DomainException exception = Assert.Throws<DomainException>(() => extractor.Inspect(archive));
        Assert.True(exception.Code is "archive-unsafe" or "path-name-invalid");
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escape.txt")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-161")]
    public void Zip符号链接条目_被拒绝()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = Path.Combine(temporary.Path, "link.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("link");
            entry.ExternalAttributes = 0xa000 << 16;
        }

        SafeZipExtractor extractor = new(ArchiveSafetyLimits.Default);
        DomainException exception = Assert.Throws<DomainException>(() => extractor.Inspect(archivePath));
        Assert.Equal("archive-unsafe", exception.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-162")]
    public void Zip压缩比_超过限制时被拒绝()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "bomb.zip");
        CreateArchive(archive, ("zeros.bin", new string('0', 50_000)));
        SafeZipExtractor extractor = new(new ArchiveSafetyLimits(10, 100_000, 2, 8));

        DomainException exception = Assert.Throws<DomainException>(() => extractor.Inspect(archive));
        Assert.Equal("archive-unsafe", exception.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-164")]
    public void Zip目录深度_超过限制时被拒绝()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "deep.zip");
        CreateArchive(archive, ("a/b/c/d/file.txt", "deep"));
        SafeZipExtractor extractor = new(new ArchiveSafetyLimits(10, 1024, 200, 4));

        DomainException exception = Assert.Throws<DomainException>(() => extractor.Inspect(archive));
        Assert.Equal("archive-unsafe", exception.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-165")]
    public async Task 解包取消_只清理本次新建目录()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "cancel.zip");
        CreateArchive(archive, ("server.jar", "synthetic"));
        string target = Path.Combine(temporary.Path, "cancelled-instance");
        SafeZipExtractor extractor = new(ArchiveSafetyLimits.Default);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(true);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractToNewDirectoryAsync(archive, target, cancellation.Token)).ConfigureAwait(true);
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-370")]
    public async Task 安全TarGzip_完整预检后解包合成Java目录()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "java.tar.gz");
        CreateTarGzip(archive, new PaxTarEntry(TarEntryType.RegularFile, "jdk/bin/java.exe")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("synthetic-java"), writable: false),
        });
        string target = Path.Combine(temporary.Path, "java");
        SafeTarGzipExtractor extractor = new(ArchiveSafetyLimits.Default);

        SafeArchivePlan plan = await extractor.ExtractToNewDirectoryAsync(archive, target, CancellationToken.None).ConfigureAwait(true);

        _ = Assert.Single(plan.Entries);
        Assert.Equal("synthetic-java", await File.ReadAllTextAsync(Path.Combine(target, "jdk", "bin", "java.exe")).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-163")]
    public async Task TarGzip符号链接条目_在写文件前被拒绝()
    {
        using TemporaryDirectory temporary = new();
        string archive = Path.Combine(temporary.Path, "link.tar.gz");
        CreateTarGzip(archive, new PaxTarEntry(TarEntryType.SymbolicLink, "jdk/bin/java.exe") { LinkName = "../../outside" });
        SafeTarGzipExtractor extractor = new(ArchiveSafetyLimits.Default);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            extractor.ExtractToNewDirectoryAsync(archive, Path.Combine(temporary.Path, "target"), CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal("archive-unsafe", exception.Code);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "target")));
    }

    private static void CreateArchive(string path, params (string Name, string Content)[] entries)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: false);
            writer.Write(content);
        }
    }

    private static void CreateTarGzip(string path, params TarEntry[] entries)
    {
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using GZipStream gzip = new(file, CompressionLevel.SmallestSize, leaveOpen: false);
        using TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (TarEntry entry in entries)
        {
            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"servitium-path-test-{Guid.NewGuid():N}")).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
