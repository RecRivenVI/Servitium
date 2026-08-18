using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Distribution;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Operations;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class ArchiveUploadProcessTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1300")]
    [Trait("TestId", "INT-1310")]
    public async Task 独立Kestrel进程_接受超过旧三十MB限制的Prism与Hmcl归档()
    {
        string root = Path.Combine(Path.GetTempPath(), $"servitium-large-upload-test-{Guid.NewGuid():N}");
        string archive = Path.Combine(root, "large-prism.zip");
        _ = Directory.CreateDirectory(root);
        CreateLargePrismArchive(archive);
        Assert.True(new FileInfo(archive).Length > 30_000_000);
        int port = ReservePort();
        Process? server = null;
        try
        {
            server = StartServer(root, port);
            using SocketsHttpHandler handler = new() { UseCookies = false, UseProxy = false };
            using HttpClient client = new(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromMinutes(2),
            };
            await WaitForReadyAsync(client, server, TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            await InitializeAsync(client).ConfigureAwait(true);

            using HttpResponseMessage channelResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/hmcl/channels", UriKind.Relative),
                new CreateHmclChannelRequest("large-upload", "Large upload", "public")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, channelResponse.StatusCode);
            HmclChannelDto channel = (await channelResponse.Content.ReadFromJsonAsync<HmclChannelDto>().ConfigureAwait(true))!;

            using (MultipartFormDataContent prismBody = [])
            using (FileStream prismStream = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (StreamContent prismContent = new(prismStream))
            {
                prismBody.Add(prismContent, "file", "large-prism.zip");
                using HttpResponseMessage preview = await client.PostAsync(
                    new Uri("/api/v1/imports/prism/preview", UriKind.Relative),
                    prismBody).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Created, preview.StatusCode);
            }

            CreateHmclReleaseMetadataRequest metadata = new(
                "Large synthetic release",
                "Servitium tests",
                "large-1",
                "Synthetic upload limit regression",
                "1.21.1",
                "fabric",
                "0.16.0",
                null);
            using MultipartFormDataContent releaseBody = [];
            using FileStream releaseStream = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamContent releaseContent = new(releaseStream);
            using StringContent metadataContent = new(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
            releaseBody.Add(releaseContent, "file", "large-content.zip");
            releaseBody.Add(metadataContent, "metadata");
            using HttpResponseMessage release = await client.PostAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/releases", UriKind.Relative),
                releaseBody).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, release.StatusCode);
            await WaitForOperationAsync(client, release, TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        }
        finally
        {
            StopProcess(server);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Process StartServer(string dataRoot, int port)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "Servitium.Server.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--urls");
        process.StartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        process.StartInfo.ArgumentList.Add("--Servitium:AgentGatewayPort");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("--environment");
        process.StartInfo.ArgumentList.Add("Development");
        process.StartInfo.Environment["SERVITIUM_DATA_ROOT"] = dataRoot;
        process.StartInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        Assert.True(process.Start());
        return process;
    }

    private static async Task InitializeAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
        string csrf = ExtractCookie(state, "servitium.csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        client.DefaultRequestHeaders.Add("Cookie", "servitium.csrf=" + csrf);
        using HttpResponseMessage initialize = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string session = ExtractCookie(login, "servitium.session");
        _ = client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"servitium.csrf={csrf}; servitium.session={session}");
    }

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        string value = response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith(name + "=", StringComparison.Ordinal));
        int start = name.Length + 1;
        return value[start..value.IndexOf(';', start)];
    }

    private static async Task WaitForReadyAsync(HttpClient client, Process process, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (true)
        {
            if (process.HasExited)
            {
                Assert.Fail($"Server exited with code {process.ExitCode}.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellation.Token).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!cancellation.IsCancellationRequested)
            {
            }

            await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
        }
    }

    private static async Task WaitForOperationAsync(HttpClient client, HttpResponseMessage acceptedResponse, TimeSpan timeout)
    {
        OperationAcceptedDto accepted = (await acceptedResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
        using CancellationTokenSource cancellation = new(timeout);
        while (true)
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri($"/api/v1/operations/{accepted.OperationId:D}", UriKind.Relative),
                cancellation.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            OperationDetailsDto details = (await response.Content.ReadFromJsonAsync<OperationDetailsDto>(cancellation.Token).ConfigureAwait(true))!;
            if (details.Operation.Status == "Succeeded")
            {
                return;
            }

            Assert.False(details.Operation.Status is "Failed" or "Cancelled", details.Operation.ErrorCode);
            await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
        }
    }

    private static int ReservePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(10_000);
            }
        }
    }

    private static void CreateLargePrismArchive(string path)
    {
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        WriteTextEntry(archive, "wrapper/instance.cfg", "name=Large Synthetic Prism\n");
        WriteTextEntry(
            archive,
            "wrapper/mmc-pack.json",
            /*lang=json,strict*/ "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.0\"}]}");
        ZipArchiveEntry payload = archive.CreateEntry("wrapper/minecraft/mods/large-client.jar", CompressionLevel.NoCompression);
        using Stream stream = payload.Open();
        byte[] block = new byte[1024 * 1024];
        for (int index = 0; index < 31; index++)
        {
            stream.Write(block);
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(value));
    }
}
