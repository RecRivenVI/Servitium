using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Servitium.Application.Abstractions;
using Servitium.Application.Nodes;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Files;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class CoreEndpointWorkflowTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1492")]
    public async Task 实例端点_镜像查询配置和生命周期动作形成完整协议链路()
    {
        CoreEndpointContext test = await CoreEndpointContext.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        Guid instanceId = test.Instance.Id;
        Guid nodeId = test.Node.Id;

        await test.ExpectAsync(HttpMethod.Get, "/api/v1/instances?status=Stopped&name=Synthetic&tag=core", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/grouped?nodeId={nodeId:D}&limit=10", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Patch, $"/api/v1/instances/{instanceId:D}", test.Configuration with { Name = "Updated" }, HttpStatusCode.OK).ConfigureAwait(true);

        CreateInstanceRequest invalid = new("Invalid", nodeId, test.Launch, string.Empty, 0, [], null);
        await test.ExpectAsync(HttpMethod.Post, "/api/v1/instances", invalid, HttpStatusCode.BadRequest).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, "/api/v1/instances", new CreateInstanceRequest("Empty", nodeId, test.Launch, "stop", 60, ["core"], "notes")).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, "/api/v1/instances/attach", new AttachInstanceDirectoryRequest("External", nodeId, @"D:\Synthetic\External", test.Launch, "stop", 45, ["external"], "notes")).ConfigureAwait(true);

        await test.ExpectAcceptedAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/start?nodeId={nodeId:D}", null).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/stop?nodeId={nodeId:D}", null).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/restart?nodeId={nodeId:D}", null).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/force-stop?nodeId={nodeId:D}", null).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/update?nodeId={nodeId:D}", new RunInstanceUpdateRequest(true)).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Delete, $"/api/v1/instances/{instanceId:D}/working-directory?nodeId={nodeId:D}", null).ConfigureAwait(true);

        await test.ExpectAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/command?nodeId={nodeId:D}", new SendInstanceCommandRequest("list"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/eula/accept?nodeId={nodeId:D}", null, HttpStatusCode.NoContent).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}/status?nodeId={nodeId:D}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}/metrics?nodeId={nodeId:D}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}/players?nodeId={nodeId:D}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"/api/v1/instances/{instanceId:D}/rcon/command?nodeId={nodeId:D}", new RconCommandRequest("list"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}/logs?nodeId={nodeId:D}&file=logs/latest.log&startLine=0&count=20&search=ready", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"/api/v1/instances/{instanceId:D}/terminal/session-log?nodeId={nodeId:D}&includeAnsi=false", null, HttpStatusCode.OK).ConfigureAwait(true);

        await test.ExpectAsync(HttpMethod.Post, "/api/v1/instances/batch/invalid", new BatchInstanceActionRequest([new(nodeId, instanceId)]), HttpStatusCode.BadRequest).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, "/api/v1/instances/batch/restart", new BatchInstanceActionRequest([new(nodeId, instanceId)])).ConfigureAwait(true);
        Assert.Contains("UpdateInstance", test.Gateway.CommandTypes);
        Assert.Contains("QueryPlayers", test.Gateway.CommandTypes);
        Assert.Contains("ExportSessionLog", test.Gateway.CommandTypes);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1493")]
    public async Task 文件配置模组和计划端点_保留请求字段并返回类型化结果()
    {
        CoreEndpointContext test = await CoreEndpointContext.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        string files = $"/api/v1/instances/{test.Instance.Id:D}/files";
        string nodeQuery = $"nodeId={test.Node.Id:D}";

        await test.ExpectAsync(HttpMethod.Get, $"{files}?{nodeQuery}&path=config&pageSize=25&search=server&sort=size&descending=true", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{files}/status?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{files}/directories?{nodeQuery}", new FilePathRequest("config"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{files}/files?{nodeQuery}", new FilePathRequest("config/new.txt"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{files}/rename?{nodeQuery}", new RenameFileRequest("config/new.txt", "renamed.txt"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/copy?{nodeQuery}", new FileTransferRequest("a.txt", "b.txt", "overwrite")).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/move?{nodeQuery}", new FileTransferRequest("b.txt", "c.txt", "keep-both")).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/delete?{nodeQuery}", new FileBatchRequest(["a.txt", "b.txt"])).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/compress?{nodeQuery}", new CompressFilesRequest(["config"], "backup.zip", "utf-8")).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/extract?{nodeQuery}", new ExtractArchiveRequest("backup.zip", "restore", "auto")).ConfigureAwait(true);
        await test.ExpectAcceptedAsync(HttpMethod.Post, $"{files}/download-from-url?{nodeQuery}", new DownloadFromUrlRequest(new Uri("https://example.invalid/file"), "file.bin", false)).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{files}/text?{nodeQuery}&path=config/server.properties&encoding=utf-8", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Put, $"{files}/text?{nodeQuery}&path=config/server.properties", new WriteTextFileRequest("motd=Synthetic", "utf-8"), HttpStatusCode.OK).ConfigureAwait(true);

        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, $"{files}/content?{nodeQuery}&path=large.bin");
        _ = rangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=100-10");
        using HttpResponseMessage rangeResponse = await test.Client.SendAsync(rangeRequest).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, rangeResponse.StatusCode);

        string configurations = $"/api/v1/instances/{test.Instance.Id:D}/configurations";
        await test.ExpectAsync(HttpMethod.Get, $"{configurations}?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{configurations}/server-properties?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Put, $"{configurations}/server-properties?{nodeQuery}", new WriteConfigurationRequest(new Dictionary<string, string?> { ["motd"] = "Synthetic" }, null, "revision"), HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{configurations}/schema/server-properties?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{configurations}/server-properties/backups?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{configurations}/server-properties/backups/{test.BackupId:D}/restore?{nodeQuery}", null, HttpStatusCode.OK).ConfigureAwait(true);

        string mods = $"/api/v1/instances/{test.Instance.Id:D}/mods";
        await test.ExpectAsync(HttpMethod.Get, $"{mods}?{nodeQuery}&kind=mods&search=synthetic", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{mods}/synthetic?{nodeQuery}&kind=mods", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{mods}/synthetic/enable?{nodeQuery}&kind=mods", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{mods}/synthetic/disable?{nodeQuery}&kind=mods", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{mods}/synthetic/config-files?{nodeQuery}&kind=mods", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Delete, $"{mods}/synthetic?{nodeQuery}&kind=mods", null, HttpStatusCode.NoContent).ConfigureAwait(true);

        string schedules = $"/api/v1/instances/{test.Instance.Id:D}/schedules";
        UpsertScheduledTaskRequest scheduleRequest = new("Synthetic", new("interval", 600, null, null, null), [new("send-console-command", "say test", false)], 3, "skip", true, null);
        await test.ExpectAsync(HttpMethod.Get, schedules, null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, schedules, scheduleRequest, HttpStatusCode.Created).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{schedules}/{test.Schedule.Id:D}", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{schedules}/{Guid.NewGuid():D}", null, HttpStatusCode.NotFound).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Put, $"{schedules}/{test.Schedule.Id:D}", scheduleRequest with { Revision = 1 }, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{schedules}/{test.Schedule.Id:D}/enable", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{schedules}/{test.Schedule.Id:D}/disable", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Post, $"{schedules}/{test.Schedule.Id:D}/run", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Get, $"{schedules}/{test.Schedule.Id:D}/runs", null, HttpStatusCode.OK).ConfigureAwait(true);
        await test.ExpectAsync(HttpMethod.Delete, $"{schedules}/{test.Schedule.Id:D}", null, HttpStatusCode.NoContent).ConfigureAwait(true);
    }

    private sealed class CoreEndpointContext : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        public string Root { get; }
        public HttpClient Client { get; }
        public FakeAgentGateway Gateway { get; }
        public StoredNode Node { get; }
        public AgentInstanceResponse Instance { get; }
        public AgentJavaRuntimeResponse Java { get; }
        public ScheduledTaskDetailsDto Schedule { get; }
        public StructuredLaunchRequest Launch { get; }
        public UpdateInstanceRequest Configuration { get; }
        public Guid BackupId { get; } = Guid.Parse("50000000-0000-4000-8000-000000000001");

        private CoreEndpointContext(string root, WebApplicationFactory<Program> factory, HttpClient client, FakeAgentGateway gateway, StoredNode node, AgentInstanceResponse instance, AgentJavaRuntimeResponse java, ScheduledTaskDetailsDto schedule, StructuredLaunchRequest launch, UpdateInstanceRequest configuration)
        {
            Root = root;
            _factory = factory;
            Client = client;
            Gateway = gateway;
            Node = node;
            Instance = instance;
            Java = java;
            Schedule = schedule;
            Launch = launch;
            Configuration = configuration;
        }

        public static async Task<CoreEndpointContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"servitium-core-endpoints-{Guid.NewGuid():N}");
            var nodeId = Guid.Parse("10000000-0000-4000-8000-000000000001");
            var instanceId = Guid.Parse("20000000-0000-4000-8000-000000000001");
            var javaId = Guid.Parse("30000000-0000-4000-8000-000000000001");
            var scheduleId = Guid.Parse("40000000-0000-4000-8000-000000000001");
            DateTimeOffset now = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
            StructuredLaunchRequest launch = new(javaId, 512, 1024, ["-Dsynthetic=true"], "jar", "server.jar", ["nogui"]);
            UpdateInstanceRequest configuration = new("Synthetic", ["core"], "notes", launch, "stop", 60, false, true, 3, new(false, 25575, null), new(false, "127.0.0.1", 25565), PrimaryPort: 25565);
            AgentInstanceResponse instance = new(instanceId, "Synthetic", @"D:\Synthetic\Managed", true, "Stopped", launch, ["core"], "notes", configuration, 1, [], null);
            AgentJavaRuntimeResponse java = new(javaId, @"C:\Synthetic\java.exe", "21.0.1", "Synthetic", "x64", "manual", false, now, "Synthetic VM");
            ScheduledTaskDetailsDto schedule = new(scheduleId, instanceId, "Synthetic", new("interval", 600, null, null, null), [new("send-console-command", "say test", false)], 3, "skip", true, 1, now.AddMinutes(10));
            FakeAgentGateway gateway = new(instance, java, schedule, now);

            WebApplicationFactory<Program> factory = new CoreFactory(root, gateway);
            HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
            await InitializeAndSignInAsync(client).ConfigureAwait(true);

            UpdateNodeRequest nodeConfiguration = new("Synthetic node", @"D:\Synthetic\Instances", "play.example.invalid", null, "notes", ["test"]);
            StoredNode node = new(nodeId, "Synthetic node", "Online", "1.0.0", 1, 0, "Windows", "X64", "synthetic-machine", now, now, false, nodeConfiguration);
            using (IServiceScope scope = factory.Services.CreateScope())
            {
                INodeControlStore store = scope.ServiceProvider.GetRequiredService<INodeControlStore>();
                var registration = new StoredNodeRegistrationToken(Guid.NewGuid(), "seed-token", "Synthetic node", now, now.AddMinutes(30), null, false);
                await store.AddRegistrationTokenAsync(registration, CancellationToken.None).ConfigureAwait(true);
                StoredNode? registered = await store.TryRegisterAsync("seed-token", node, new(Guid.NewGuid(), nodeId, "credential", now, null, 1), now, CancellationToken.None).ConfigureAwait(true);
                Assert.NotNull(registered);
                await store.ReplaceMirrorAsync(new(nodeId, [instance], [java], [schedule], now), CancellationToken.None).ConfigureAwait(true);
                await store.AddMetricAsync(new(nodeId, new(now, 10, 100, 1000, 10000, 5, 6, TimeSpan.FromHours(1))), now.AddDays(-7), CancellationToken.None).ConfigureAwait(true);
            }

            return new(root, factory, client, gateway, node, instance, java, schedule, launch, configuration);
        }

        public async Task ExpectAsync(HttpMethod method, string path, object? body, HttpStatusCode status)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            Assert.True(response.StatusCode == status, $"{method} {path}: expected {(int)status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}");
        }

        public async Task ExpectAcceptedAsync(HttpMethod method, string path, object? body)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            OperationAcceptedDto? accepted = await response.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true);
            Assert.NotNull(accepted);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                using HttpResponseMessage statusResponse = await Client.GetAsync(new Uri($"/api/v1/operations/{accepted.OperationId:D}", UriKind.Relative)).ConfigureAwait(true);
                OperationDetailsDto? details = await statusResponse.Content.ReadFromJsonAsync<OperationDetailsDto>().ConfigureAwait(true);
                if (details?.Operation.Status is "Succeeded" or "Failed" or "Cancelled")
                {
                    Assert.Equal("Succeeded", details.Operation.Status);
                    return;
                }

                await Task.Delay(10).ConfigureAwait(true);
            }

            throw new TimeoutException("The synthetic operation did not complete.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync().ConfigureAwait(false);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }

        private static async Task InitializeAndSignInAsync(HttpClient client)
        {
            using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
            string csrfCookie = state.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
            string csrf = csrfCookie["servitium.csrf=".Length..csrfCookie.IndexOf(';', StringComparison.Ordinal)];
            client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
            using HttpResponseMessage initialize = await client.PostAsJsonAsync("/api/v1/auth/initialize", new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
            using HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login", new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    private sealed class CoreFactory(string root, FakeAgentGateway gateway) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.UseSetting("Servitium:DataRoot", root);
            _ = builder.ConfigureTestServices(services =>
            {
                _ = services.RemoveAll<IAgentGateway>();
                _ = services.AddSingleton<IAgentGateway>(gateway);
            });
        }
    }

    private sealed class FakeAgentGateway(AgentInstanceResponse instance, AgentJavaRuntimeResponse java, ScheduledTaskDetailsDto schedule, DateTimeOffset now) : IAgentGateway
    {
        public ConcurrentQueue<string> CommandTypes { get; } = new();

        public Task<AgentCommandResult> SendAsync(Guid nodeId, AgentCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandTypes.Enqueue(command.Type);
            object payload = command.Type switch
            {
                "Reconcile" => new AgentReconcileResponse([instance], [java], [schedule], now),
                "CreateInstance" or "UpdateInstance" => instance,
                "StartInstance" or "StopInstance" or "RestartInstance" or "ForceStopInstance" or "SendCommand" or "GetInstanceStatus" => new AgentInstanceRuntimeResponse(instance.Id, "Stopped", null, null, 0, ["synthetic"]),
                "GetMetrics" => new InstanceMetricsDto(now, 10, 1024, 1, 5, 2),
                "QueryPlayers" => new PlayerListDto(1, 20, ["SyntheticPlayer"], "query", "1.21", "Synthetic"),
                "RconCommand" => new RconCommandResultDto("Synthetic output"),
                "RunUpdateAction" => new InstanceUpdateResultDto("jar-replace", "backups/server.jar", now),
                "ReadInstanceLog" => new InstanceLogDto("logs/latest.log", 0, ["ready"], false),
                "ExportSessionLog" => new SessionLogExportDto("session.log", "synthetic", false, false),
                "ListDirectory" => new FileEntryPageDto([new("config/server.properties", false, 10, now)], null),
                "GetFileSystemStatus" => new FileSystemStatusDto(1000, 500, 0, 4, 0, 2),
                "GetFileDownloadInfo" => new FileDownloadInfoDto("large.bin", 100, now),
                "CreateDirectory" or "CreateFile" or "Rename" or "Copy" or "Move" or "Compress" or "Extract" or "DownloadFromUrl" => new FileEntryDto("result", false, 10, now),
                "DeleteBatch" or "AcceptEula" or "DeleteWorkingDirectory" or "UnregisterInstance" or "DeleteMod" or "DeleteSchedule" => new { ok = true },
                "ReadText" or "WriteText" => new AgentTextFileResponse("config/server.properties", "motd=Synthetic", "utf-8", "lf", 14),
                "ListConfigurations" => new[] { Configuration() },
                "ReadConfiguration" or "WriteConfiguration" or "RestoreConfigBackup" => ConfigurationDetails(),
                "ListConfigBackups" => new[] { new ConfigurationBackupDto(Guid.Parse("50000000-0000-4000-8000-000000000001"), now, 10, "revision") },
                "ListMods" => new[] { Mod() },
                "GetModMetadata" or "ToggleMod" => Mod(),
                "ListModConfigFiles" => new[] { "config/synthetic.toml" },
                "ListSchedules" => new[] { schedule },
                "UpsertSchedule" or "SetScheduleEnabled" => schedule,
                "RunScheduleNow" => new ScheduledTaskRunDto(Guid.NewGuid(), schedule.Id, now, now, true, "succeeded", ["succeeded"]),
                "ListScheduleRuns" => new[] { new ScheduledTaskRunDto(Guid.NewGuid(), schedule.Id, now, now, true, "succeeded", ["succeeded"]) },
                "RegisterJava" => java,
                _ => new { ok = true },
            };
            return Task.FromResult(new AgentCommandResult(true, JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType()), null));
        }

        private ConfigurationDto Configuration()
        {
            return new("server-properties", "server.properties", "properties", false, ["server.properties"], now);
        }

        private ConfigurationDetailsDto ConfigurationDetails()
        {
            return new("server-properties", "server.properties", "properties", new Dictionary<string, string?> { ["motd"] = "Synthetic" }, "motd=Synthetic", "revision", now);
        }

        private ModDetailsDto Mod()
        {
            return new("synthetic", "mods/synthetic.jar", "Synthetic", "1.0", "Synthetic", ["Tester"], "Fabric", true, 10, now, [], false, "synthetic");
        }
    }
}
