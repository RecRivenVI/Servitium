using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Contracts.Instances;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class InstanceSoftwareInspectorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-software-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly InstanceSoftwareInspector _inspector = new(new WindowsPathPolicy());
    private readonly StructuredLaunchRequest _launch = new(Guid.NewGuid(), 64, 128, [], "jar", "misleading-fabric-9.9.jar", []);

    [Fact]
    public async Task 启动元数据_原版只认主类和版本内容并在替换后重新读取()
    {
        await JarAsync("Main-Class: net.minecraft.bundler.Main\n", "{\"id\":\"1.21.1\"}");
        InstanceSoftwareDto actual = await InspectAsync();
        Assert.Equal("Vanilla", actual.Loader);
        Assert.Equal("1.21.1", actual.GameVersion);
        Assert.Null(actual.LoaderVersion);
        Assert.Null(actual.Issue);
        await JarAsync("Main-Class: net.minecraft.bundler.Main\n", "{\"id\":\"26.3\"}");
        Assert.Equal("26.3", (await InspectAsync()).GameVersion);
        await JarAsync("Main-Class: synthetic.Unknown\n", "{\"id\":\"1.21.1\"}");
        actual = await InspectAsync();
        Assert.Null(actual.GameVersion);
        Assert.Null(actual.Loader);
        Assert.Equal("unrecognized-launch-format", actual.Issue);
    }

    [Fact]
    public async Task 启动元数据_Fabric清单续行及依赖坐标_缺失和冲突不猜测()
    {
        string game = await LibraryAsync("intermediary", "1.21.1");
        string loader = await LibraryAsync("fabric-loader", "0.16.10");
        string manifest = "Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\nClass-Path: " + game + "\n " + " " + loader + "\n";
        await JarAsync(manifest);
        InstanceSoftwareDto actual = await InspectAsync();
        Assert.Equal("Fabric", actual.Loader);
        Assert.Equal("1.21.1", actual.GameVersion);
        Assert.Equal("0.16.10", actual.LoaderVersion);
        Assert.Null(actual.Issue);
        string other = await LibraryAsync("fabric-loader", "0.17.0");
        await JarAsync(manifest.TrimEnd() + " " + other + "\n");
        Assert.Equal("metadata-conflict", (await InspectAsync()).Issue);
        await JarAsync("Main-Class: net.fabricmc.loader.impl.launch.server.FabricServerLauncher\nClass-Path: libraries/net/fabricmc/intermediary/1.0/intermediary-1.0.jar\n");
        Assert.Equal("metadata-dependency-missing", (await InspectAsync()).Issue);
    }

    [Theory]
    [InlineData("net.neoforged.fml.startup.Server", "neoforgeserver", "neoForgeVersion", "NeoForge", "21.1.80")]
    [InlineData("cpw.mods.bootstraplauncher.BootstrapLauncher", "forgeserver", "forgeVersion", "Forge", "47.3.0")]
    public async Task 启动元数据_参数文件只读明确主类和独立版本选项(string main, string target, string key, string loader, string version)
    {
        StructuredLaunchRequest launch = _launch with { TargetKind = "argfile", Target = "unix_args.txt" };
        string content = $"# --fml.mcVersion 0.0\n{main}\n--launchTarget {target}\n--fml.mcVersion \"1.21.1\"\n--fml.{key} {version}\n";
        await File.WriteAllTextAsync(Path.Combine(_root, launch.Target), content);
        InstanceSoftwareDto actual = await _inspector.InspectAsync(_root, launch, CancellationToken.None);
        Assert.Equal("1.21.1", actual.GameVersion);
        Assert.Equal(loader, actual.Loader);
        Assert.Equal(version, actual.LoaderVersion);
        Assert.Null(actual.Issue);
        actual = await _inspector.InspectAsync(_root, launch with { ServerArguments = ["--fml.mcVersion", "other"] }, CancellationToken.None);
        Assert.Null(actual.GameVersion);
        Assert.Equal("metadata-conflict", actual.Issue);
    }

    [Fact]
    public async Task 启动元数据_未知损坏超大和越界目标均返回明确原因()
    {
        Assert.Equal("launch-target-missing", (await InspectAsync()).Issue);
        await JarAsync("Main-Class: net.minecraft.server.Main\n", "[]");
        Assert.Equal("metadata-unreadable", (await InspectAsync()).Issue);
        await JarAsync("Main-Class: net.minecraft.server.Main\n", new string('x', 65537));
        Assert.Equal("metadata-unreadable", (await InspectAsync()).Issue);
        await JarAsync("Main-Class: net.minecraft.server.Main\nMain-Class: synthetic.Other\n");
        Assert.Equal("metadata-unreadable", (await InspectAsync()).Issue);
        Assert.Equal("unsafe-metadata-path", (await _inspector.InspectAsync(_root, _launch with { Target = "../outside.jar" }, CancellationToken.None)).Issue);
        Assert.Equal("advanced-launch", (await _inspector.InspectAsync(_root, _launch with { AdvancedMode = true }, CancellationToken.None)).Issue);
    }

    private Task<InstanceSoftwareDto> InspectAsync() => _inspector.InspectAsync(_root, _launch, CancellationToken.None);

    private async Task JarAsync(string manifest, string? version = null)
    {
        using FileStream stream = new(Path.Combine(_root, _launch.Target), FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive jar = new(stream, ZipArchiveMode.Create);
        await EntryAsync(jar, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n" + manifest + "\n").ConfigureAwait(true);
        if (version is not null) { await EntryAsync(jar, "version.json", version).ConfigureAwait(true); }
    }

    private static async Task EntryAsync(ZipArchive jar, string name, string content)
    {
        using Stream stream = await jar.CreateEntry(name).OpenAsync().ConfigureAwait(true);
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content).ConfigureAwait(true);
    }

    private async Task<string> LibraryAsync(string artifact, string version)
    {
        string relative = $"libraries/net/fabricmc/{artifact}/{version}/{artifact}-{version}.jar";
        string file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "synthetic library coordinate fixture").ConfigureAwait(true);
        return relative;
    }

    public void Dispose() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(_root,
        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
