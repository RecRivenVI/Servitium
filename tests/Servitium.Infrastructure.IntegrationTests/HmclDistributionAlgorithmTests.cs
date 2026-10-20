using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

using Servitium.Contracts.Distribution;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Distribution;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class HmclDistributionAlgorithmTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-500")]
    public void Manifest写入_字节确定且字段与文件排序固定()
    {
        CreateHmclReleaseMetadataRequest metadata = new(
            "Release", "Author", "1.0", "Description", "1.21.1", "fabric", "0.16.0", null);
        StoredHmclReleaseFile[] files =
        [
            new("mods/z.jar", new string('b', 40), 2),
            new("config/a.txt", new string('a', 40), 1),
        ];

        byte[] first = HmclManifestWriter.Write(metadata, "https://example.test/hmcl/stable", files);
        byte[] second = HmclManifestWriter.Write(metadata, "https://example.test/hmcl/stable", [.. files.Reverse()]);

        Assert.Equal(first, second);
        string json = Encoding.UTF8.GetString(first);
        Assert.True(json.IndexOf("\"name\"", StringComparison.Ordinal) < json.IndexOf("\"author\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"author\"", StringComparison.Ordinal) < json.IndexOf("\"version\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"version\"", StringComparison.Ordinal) < json.IndexOf("\"description\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"description\"", StringComparison.Ordinal) < json.IndexOf("\"fileApi\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("config/a.txt", StringComparison.Ordinal) < json.IndexOf("mods/z.jar", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(first);
        Assert.False(document.RootElement.TryGetProperty("downloadURL", out _));
        Assert.Equal("game", document.RootElement.GetProperty("addons")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("options.txt", "fixed-exclusion")]
    [InlineData("logs/latest.log", "fixed-directory-exclusion")]
    [InlineData("mods/example.jar.disabled", "special-client-or-log-file")]
    [InlineData("config/example.old", "special-client-or-log-file")]
    [InlineData("mods/example.jar", null)]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-510")]
    public void 排除清单_按固定名称目录与后缀匹配(string path, string? expected)
    {
        Assert.Equal(expected, HmclArchiveProcessor.GetExclusionReason(path));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-520")]
    public void 内容检查_拒绝仅大小写不同的重复路径()
    {
        string archivePath = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/Example.jar"] = Encoding.UTF8.GetBytes("first"),
            ["mods/example.jar"] = Encoding.UTF8.GetBytes("second"),
        });
        try
        {
            DomainException exception = Assert.Throws<DomainException>(() => HmclArchiveProcessor.InspectContentArchive(archivePath));
            Assert.Equal("archive-duplicate-path", exception.Code);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Theory]
    [InlineData("forge", "47.3.0")]
    [InlineData("neoforge", "21.1.0")]
    [InlineData("fabric", "0.16.0")]
    [InlineData("quilt", "0.27.0")]
    [InlineData("liteloader", "1.12.2")]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-540")]
    public void Loader声明_允许协议规定的模组加载器(string loader, string version)
    {
        CreateHmclReleaseMetadataRequest metadata = new(
            "Release", "Author", "1.0", "Description", "1.21.1", loader, version, null);
        byte[] manifest = HmclManifestWriter.Write(metadata, "https://example.test/hmcl/stable", []);
        Assert.Contains($"\"id\":\"{loader}\"", Encoding.UTF8.GetString(manifest), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-540")]
    public void Loader声明_拒绝未知加载器()
    {
        CreateHmclReleaseMetadataRequest metadata = new(
            "Release", "Author", "1.0", "Description", "1.21.1", "unknown", "1", null);
        DomainException exception = Assert.Throws<DomainException>(() =>
            HmclManifestWriter.Write(metadata, "https://example.test/hmcl/stable", []));
        Assert.Equal("hmcl-release-validation-failed", exception.Code);
    }

    private static string CreateZip(IReadOnlyDictionary<string, byte[]> files)
    {
        string path = Path.Combine(Path.GetTempPath(), $"servitium-hmcl-algorithm-{Guid.NewGuid():N}.zip");
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        foreach ((string name, byte[] content) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using Stream stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }
}
