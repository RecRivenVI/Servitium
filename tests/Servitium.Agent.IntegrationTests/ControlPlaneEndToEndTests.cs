using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Agent;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Files;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class ControlPlaneEndToEndTests
{
    private static readonly ConcurrentDictionary<int, ConcurrentQueue<string>> ProcessLogs = new();
    private static readonly int[] UploadPartOrder = [2, 0, 1];
    private static readonly int[] ExpectedReceivedPartOrder = [0, 1, 2];

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-300")]
    [Trait("TestId", "INT-440")]
    [Trait("TestId", "INT-450")]
    [Trait("TestId", "INT-500")]
    [Trait("TestId", "INT-501")]
    [Trait("TestId", "INT-800")]
    [Trait("TestId", "INT-820")]
    [Trait("TestId", "INT-830")]
    [Trait("TestId", "INT-840")]
    [Trait("TestId", "INT-960")]
    [Trait("TestId", "INT-1000")]
    [Trait("TestId", "INT-1060")]
    [Trait("TestId", "INT-1470")]
    [Trait("TestId", "INT-1480")]
    public async Task 独立Server与Agent进程_注册并贯通外部目录和服务端归档()
    {
        string root = Path.Combine(Path.GetTempPath(), $"servitium-process-e2e-{Guid.NewGuid():N}");
        string serverData = Path.Combine(root, "server");
        string agentData = Path.Combine(root, "agent");
        string externalDirectory = Path.Combine(root, "external-server");
        string archivePath = Path.Combine(root, "server.zip");
        _ = Directory.CreateDirectory(externalDirectory);
        await File.WriteAllTextAsync(Path.Combine(externalDirectory, "server.jar"), "synthetic-external").ConfigureAwait(true);
        CreateServerZip(archivePath);
        int restPort = ReservePort();
        int grpcPort = ReservePort();
        Process? server = null;
        Process? agent = null;
        try
        {
            server = StartProcess(
                Path.Combine(AppContext.BaseDirectory, "Servitium.Server.exe"),
                new Dictionary<string, string>
                {
                    ["SERVITIUM_DATA_ROOT"] = serverData,
                    ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{restPort}",
                    ["Servitium__AgentGatewayPort"] = grpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Logging__LogLevel__Default"] = "Warning",
                });
            using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{restPort}") };
            await WaitForReadyAsync(client, server, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            await InitializeAsync(client).ConfigureAwait(true);

            using HttpResponseMessage tokenResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/nodes/registration-tokens", UriKind.Relative),
                new CreateNodeRegistrationTokenRequest("Synthetic node", 15)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, tokenResponse.StatusCode);
            NodeRegistrationTokenResponse token = (await tokenResponse.Content.ReadFromJsonAsync<NodeRegistrationTokenResponse>().ConfigureAwait(true))!;

            agent = StartProcess(
                Path.Combine(AppContext.BaseDirectory, "Servitium.Agent.exe"),
                new Dictionary<string, string>
                {
                    ["SERVITIUM_DATA_ROOT"] = agentData,
                    ["Logging__LogLevel__Default"] = "Warning",
                },
                "--server", $"http://127.0.0.1:{grpcPort}",
                "--register-token", token.Token,
                "--node-name", "Synthetic node");
            Guid nodeId = await WaitForNodeAsync(client, agent, server, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            using HttpResponseMessage javaResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/java-runtimes", UriKind.Relative),
                new RegisterJavaRuntimeRequest(FindJavaExecutable())).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, javaResponse.StatusCode);
            JavaRuntimeDto java = (await javaResponse.Content.ReadFromJsonAsync<JavaRuntimeDto>().ConfigureAwait(true))!;

            using HttpResponseMessage javaListResponse = await client.GetAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/java-runtimes", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, javaListResponse.StatusCode);
            using (var javaList = JsonDocument.Parse(await javaListResponse.Content.ReadAsStringAsync().ConfigureAwait(true)))
            {
                JsonElement listed = Assert.Single(javaList.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(java.Id, listed.GetProperty("id").GetGuid());
            }

            StructuredLaunchRequest launch = new(java.Id, 64, 128, [], "jar", "server.jar", []);
            using HttpResponseMessage attachedResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/instances/attach", UriKind.Relative),
                new AttachInstanceDirectoryRequest(
                    "External synthetic",
                    nodeId,
                    externalDirectory,
                    launch,
                    "save-all stop",
                    75,
                    ["external", "synthetic"],
                    "External notes")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, attachedResponse.StatusCode);
            OperationAcceptedDto attachedOperation = (await attachedResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            AgentInstanceResponse attached = ReadOperationResult<AgentInstanceResponse>(
                await WaitForOperationAsync(client, attachedOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true));
            Assert.False(attached.OwnedByServitium);
            Assert.Equal(Path.GetFullPath(externalDirectory), attached.WorkingDirectory);
            using HttpResponseMessage attachedDetailsResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{attached.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, attachedDetailsResponse.StatusCode);
            InstanceDetailsDto attachedDetails = (await attachedDetailsResponse.Content.ReadFromJsonAsync<InstanceDetailsDto>().ConfigureAwait(true))!;
            Assert.Equal("save-all stop", attachedDetails.Configuration!.StopCommand);
            Assert.Equal(75, attachedDetails.Configuration.StopTimeoutSeconds);
            Assert.Equal(["external", "synthetic"], attachedDetails.Instance.Tags);
            Assert.Equal("External notes", attachedDetails.Notes);

            using MultipartFormDataContent archiveBody = new();
            using ByteArrayContent archiveContent = new(await File.ReadAllBytesAsync(archivePath).ConfigureAwait(true));
            using StringContent nodeContent = new(nodeId.ToString("D"));
            using StringContent nameContent = new("Archive synthetic");
            using StringContent launchContent = new(JsonSerializer.Serialize(launch), Encoding.UTF8, "application/json");
            using StringContent stopCommandContent = new("stop");
            using StringContent stopTimeoutContent = new("90");
            using StringContent tagContent = new("archive");
            using StringContent notesContent = new("Archive notes");
            archiveBody.Add(archiveContent, "file", "server.zip");
            archiveBody.Add(nodeContent, "nodeId");
            archiveBody.Add(nameContent, "name");
            archiveBody.Add(launchContent, "launch");
            archiveBody.Add(stopCommandContent, "stopCommand");
            archiveBody.Add(stopTimeoutContent, "stopTimeoutSeconds");
            archiveBody.Add(tagContent, "tags");
            archiveBody.Add(notesContent, "notes");
            using HttpResponseMessage importedResponse = await client.PostAsync(new Uri("/api/v1/instances/from-archive", UriKind.Relative), archiveBody).ConfigureAwait(true);
            Assert.True(
                importedResponse.StatusCode == HttpStatusCode.Accepted,
                "Archive import returned " + importedResponse.StatusCode + ". Body: " +
                await importedResponse.Content.ReadAsStringAsync().ConfigureAwait(true) +
                " Agent: " + ReadProcessLog(agent) + " Server: " + ReadProcessLog(server));
            OperationAcceptedDto importedOperation = (await importedResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            AgentInstanceResponse imported = ReadOperationResult<AgentInstanceResponse>(
                await WaitForOperationAsync(client, importedOperation.OperationId, TimeSpan.FromSeconds(20)).ConfigureAwait(true));
            Assert.True(imported.OwnedByServitium);
            Assert.True(File.Exists(Path.Combine(imported.WorkingDirectory, "server.jar")));
            Assert.Equal(31L * 1024 * 1024, new FileInfo(Path.Combine(imported.WorkingDirectory, "synthetic-large-payload.bin")).Length);
            using HttpResponseMessage importedDetailsResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, importedDetailsResponse.StatusCode);
            InstanceDetailsDto importedDetails = (await importedDetailsResponse.Content.ReadFromJsonAsync<InstanceDetailsDto>().ConfigureAwait(true))!;
            Assert.Equal("stop", importedDetails.Configuration!.StopCommand);
            Assert.Equal(90, importedDetails.Configuration.StopTimeoutSeconds);
            Assert.Equal(["archive"], importedDetails.Instance.Tags);
            Assert.Equal("Archive notes", importedDetails.Notes);

            byte[] uploadPayload = [.. Enumerable.Range(0, (600 * 1024) + 37).Select(index => (byte)(index * 31 % 251))];
            const int partSize = 256 * 1024;
            using HttpResponseMessage createUploadResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads?nodeId={nodeId:D}", UriKind.Relative),
                new CreateUploadSessionRequest("chunk-e2e.bin", "chunk-e2e.bin", uploadPayload.LongLength, partSize)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, createUploadResponse.StatusCode);
            UploadSessionDto upload = (await createUploadResponse.Content.ReadFromJsonAsync<UploadSessionDto>().ConfigureAwait(true))!;

            foreach (int partIndex in UploadPartOrder)
            {
                int offset = checked(partIndex * partSize);
                int count = Math.Min(partSize, uploadPayload.Length - offset);
                byte[] part = uploadPayload.AsSpan(offset, count).ToArray();
                string partHash = Convert.ToHexString(SHA256.HashData(part));
                using ByteArrayContent partContent = new(part);
                using HttpResponseMessage partResponse = await client.PutAsync(
                    new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads/{upload.Id:D}/parts/{partIndex}?nodeId={nodeId:D}&sha256={partHash}", UriKind.Relative),
                    partContent).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, partResponse.StatusCode);
            }

            using HttpResponseMessage resumedUploadResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads/{upload.Id:D}?nodeId={nodeId:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, resumedUploadResponse.StatusCode);
            UploadSessionDto resumedUpload = (await resumedUploadResponse.Content.ReadFromJsonAsync<UploadSessionDto>().ConfigureAwait(true))!;
            Assert.Equal(ExpectedReceivedPartOrder, resumedUpload.ReceivedParts.Order());

            string uploadHash = Convert.ToHexString(SHA256.HashData(uploadPayload));
            using HttpResponseMessage completeUploadResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads/{upload.Id:D}/complete?nodeId={nodeId:D}", UriKind.Relative),
                new CompleteUploadRequest(uploadHash)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, completeUploadResponse.StatusCode);
            OperationAcceptedDto uploadOperation = (await completeUploadResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            OperationDetailsDto uploadDetails = await WaitForOperationAsync(client, uploadOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.Equal("Succeeded", uploadDetails.Operation.Status);
            Assert.Equal(uploadPayload, await File.ReadAllBytesAsync(Path.Combine(imported.WorkingDirectory, "chunk-e2e.bin")).ConfigureAwait(true));

            using HttpResponseMessage copyResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/copy?nodeId={nodeId:D}", UriKind.Relative),
                new FileTransferRequest("chunk-e2e.bin", "async-copy.bin")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, copyResponse.StatusCode);
            OperationAcceptedDto copyOperation = (await copyResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            Assert.Equal($"/api/v1/operations/{copyOperation.OperationId:D}", copyResponse.Headers.Location?.OriginalString);
            OperationDetailsDto copyDetails = await WaitForOperationAsync(client, copyOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.Equal("Succeeded", copyDetails.Operation.Status);
            Assert.Equal(uploadPayload, await File.ReadAllBytesAsync(Path.Combine(imported.WorkingDirectory, "async-copy.bin")).ConfigureAwait(true));

            using HttpResponseMessage fullDownloadResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/content?nodeId={nodeId:D}&path=chunk-e2e.bin", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, fullDownloadResponse.StatusCode);
            Assert.Equal(uploadPayload, await fullDownloadResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
            Assert.Equal("bytes", Assert.Single(fullDownloadResponse.Headers.AcceptRanges));

            using HttpRequestMessage rangeRequest = new(
                HttpMethod.Get,
                new Uri($"/api/v1/instances/{imported.Id:D}/files/content?nodeId={nodeId:D}&path=chunk-e2e.bin", UriKind.Relative));
            _ = rangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=12345-67890");
            using HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal("bytes 12345-67890/614437", rangeResponse.Content.Headers.ContentRange?.ToString());
            Assert.Equal(uploadPayload.AsSpan(12345, 67890 - 12345 + 1).ToArray(), await rangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(true));

            using HttpRequestMessage rejectedRangeRequest = new(
                HttpMethod.Get,
                new Uri($"/api/v1/instances/{imported.Id:D}/files/content?nodeId={nodeId:D}&path=chunk-e2e.bin", UriKind.Relative));
            _ = rejectedRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=999999-");
            using HttpResponseMessage rejectedRangeResponse = await client.SendAsync(rejectedRangeRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, rejectedRangeResponse.StatusCode);
            Assert.Equal("bytes */614437", rejectedRangeResponse.Content.Headers.ContentRange?.ToString());

            byte[] modPayload;
            using (MemoryStream modStream = new())
            {
                using (ZipArchive modArchive = new(modStream, ZipArchiveMode.Create, true))
                {
                    ZipArchiveEntry metadata = modArchive.CreateEntry("fabric.mod.json", CompressionLevel.NoCompression);
                    Stream metadataStream = await metadata.OpenAsync(CancellationToken.None).ConfigureAwait(true);
                    await using (metadataStream.ConfigureAwait(true))
                    {
                        await metadataStream.WriteAsync(Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"id\":\"synthetic_e2e\",\"name\":\"Synthetic E2E\",\"version\":\"1.0\"}")).ConfigureAwait(true);
                    }
                }

                modPayload = modStream.ToArray();
            }

            string modHash = Convert.ToHexString(SHA256.HashData(modPayload));
            using HttpResponseMessage createModUploadResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads?nodeId={nodeId:D}", UriKind.Relative),
                new CreateUploadSessionRequest("synthetic-e2e.jar", "mods/synthetic-e2e.jar", modPayload.LongLength, partSize)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, createModUploadResponse.StatusCode);
            UploadSessionDto modUpload = (await createModUploadResponse.Content.ReadFromJsonAsync<UploadSessionDto>().ConfigureAwait(true))!;
            using (ByteArrayContent modContent = new(modPayload))
            using (HttpResponseMessage modPartResponse = await client.PutAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/files/uploads/{modUpload.Id:D}/parts/0?nodeId={nodeId:D}&sha256={modHash}", UriKind.Relative),
                modContent).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, modPartResponse.StatusCode);
            }

            using HttpResponseMessage finishModResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/mods/upload?nodeId={nodeId:D}", UriKind.Relative),
                new UploadModRequest(modUpload.Id, "mods", modHash)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, finishModResponse.StatusCode);
            OperationAcceptedDto modOperation = (await finishModResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            ModDetailsDto mod = ReadOperationResult<ModDetailsDto>(
                await WaitForOperationAsync(client, modOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true));
            Assert.Equal("synthetic_e2e", mod.Identifier);
            Assert.True(mod.Enabled);

            using HttpResponseMessage disableModResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/mods/{mod.Id}/disable?nodeId={nodeId:D}&kind=mods", UriKind.Relative),
                null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, disableModResponse.StatusCode);
            ModDetailsDto disabledMod = (await disableModResponse.Content.ReadFromJsonAsync<ModDetailsDto>().ConfigureAwait(true))!;
            Assert.False(disabledMod.Enabled);
            using HttpResponseMessage enableModResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/mods/{mod.Id}/enable?nodeId={nodeId:D}&kind=mods", UriKind.Relative),
                null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, enableModResponse.StatusCode);
            using HttpResponseMessage deleteModResponse = await client.DeleteAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/mods/{mod.Id}?nodeId={nodeId:D}&kind=mods", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, deleteModResponse.StatusCode);
            Assert.False(File.Exists(Path.Combine(imported.WorkingDirectory, "mods", "synthetic-e2e.jar")));

            using HttpResponseMessage eulaResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/eula/accept?nodeId={nodeId:D}", UriKind.Relative),
                content: null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, eulaResponse.StatusCode);
            using HttpResponseMessage startResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/start?nodeId={nodeId:D}", UriKind.Relative),
                content: null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
            OperationAcceptedDto startOperation = (await startResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            _ = ReadOperationResult<AgentInstanceRuntimeResponse>(
                await WaitForOperationAsync(client, startOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true));
            InstanceRuntimeDto running = await WaitForInstanceOutputAsync(client, nodeId, imported.Id, "fixture-ready", TimeSpan.FromSeconds(10), agent, server).ConfigureAwait(true);
            Assert.Equal("Running", running.Status);
            _ = Assert.NotNull(running.ProcessId);

            const string literalCommand = "literal & whoami | echo %PATH%";
            using HttpResponseMessage commandResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/command?nodeId={nodeId:D}", UriKind.Relative),
                new SendInstanceCommandRequest(literalCommand)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
            _ = await WaitForInstanceOutputAsync(client, nodeId, imported.Id, "fixture-input:" + literalCommand, TimeSpan.FromSeconds(10), agent, server).ConfigureAwait(true);
            using HttpResponseMessage stopResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/stop?nodeId={nodeId:D}", UriKind.Relative),
                content: null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, stopResponse.StatusCode);
            OperationAcceptedDto stopOperation = (await stopResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            AgentInstanceRuntimeResponse stopped = ReadOperationResult<AgentInstanceRuntimeResponse>(
                await WaitForOperationAsync(client, stopOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true));
            Assert.Equal("Stopped", stopped.Status);

            UpsertScheduledTaskRequest scheduleRequest = new(
                "Synthetic schedule",
                new ScheduleTriggerDto("specified", null, null, null, DateTimeOffset.UtcNow.AddHours(1)),
                [new ScheduleActionDto("delay", "1", false)],
                1,
                "skip",
                false,
                null);
            using HttpResponseMessage createScheduleResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/schedules", UriKind.Relative),
                scheduleRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, createScheduleResponse.StatusCode);
            ScheduledTaskDetailsDto schedule = (await createScheduleResponse.Content.ReadFromJsonAsync<ScheduledTaskDetailsDto>().ConfigureAwait(true))!;
            using HttpResponseMessage scheduleListResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/schedules", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, scheduleListResponse.StatusCode);
            Assert.Contains(schedule.Id.ToString("D"), await scheduleListResponse.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.OrdinalIgnoreCase);
            using HttpResponseMessage runScheduleResponse = await client.PostAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/schedules/{schedule.Id:D}/run", UriKind.Relative),
                null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, runScheduleResponse.StatusCode);
            ScheduledTaskRunDto scheduleRun = (await runScheduleResponse.Content.ReadFromJsonAsync<ScheduledTaskRunDto>().ConfigureAwait(true))!;
            Assert.True(scheduleRun.Succeeded);
            using HttpResponseMessage scheduleRunsResponse = await client.GetAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/schedules/{schedule.Id:D}/runs", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, scheduleRunsResponse.StatusCode);
            Assert.Contains(scheduleRun.Id.ToString("D"), await scheduleRunsResponse.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.OrdinalIgnoreCase);
            using HttpResponseMessage deleteScheduleResponse = await client.DeleteAsync(
                new Uri($"/api/v1/instances/{imported.Id:D}/schedules/{schedule.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, deleteScheduleResponse.StatusCode);

            using HttpResponseMessage channelResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/hmcl/channels", UriKind.Relative),
                new CreateHmclChannelRequest("agent-e2e", "Agent E2E", "public")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, channelResponse.StatusCode);
            HmclChannelDto channel = (await channelResponse.Content.ReadFromJsonAsync<HmclChannelDto>().ConfigureAwait(true))!;
            CreateHmclReleaseMetadataRequest firstMetadata = new(
                "Collected E2E", "Servitium tests", "1", "Synthetic instance collection", "1.21.1", "fabric", "0.16.0", null);
            using HttpResponseMessage collectedResponse = await client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/releases", UriKind.Relative),
                new CreateHmclReleaseFromInstanceRequest(imported.Id, firstMetadata)).ConfigureAwait(true);
            Assert.True(
                collectedResponse.StatusCode == HttpStatusCode.Accepted,
                "Instance collection returned " + collectedResponse.StatusCode + ". Body: " +
                await collectedResponse.Content.ReadAsStringAsync().ConfigureAwait(true) +
                " Agent: " + ReadProcessLog(agent) + " Server: " + ReadProcessLog(server));
            OperationAcceptedDto collectedOperation = (await collectedResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            HmclReleaseCreationResult collectedRelease = ReadOperationResult<HmclReleaseCreationResult>(
                await WaitForOperationAsync(client, collectedOperation.OperationId, TimeSpan.FromSeconds(20)).ConfigureAwait(true));
            Assert.Contains(collectedRelease.Release.Files, file => file.Path == "server.jar");
            Assert.Contains("eula.txt", collectedRelease.ExcludedPaths.Keys);

            using HttpResponseMessage firstSync = await client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/releases/{collectedRelease.Release.Id:D}/sync", UriKind.Relative),
                new SyncHmclReleaseToInstanceRequest(imported.Id)).ConfigureAwait(true);
            Assert.True(
                firstSync.StatusCode == HttpStatusCode.Accepted,
                "First HMCL sync returned " + firstSync.StatusCode + ". Body: " +
                await firstSync.Content.ReadAsStringAsync().ConfigureAwait(true) +
                " Agent: " + ReadProcessLog(agent) + " Server: " + ReadProcessLog(server));
            OperationAcceptedDto firstSyncOperation = (await firstSync.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            Assert.Equal("Succeeded", (await WaitForOperationAsync(client, firstSyncOperation.OperationId, TimeSpan.FromSeconds(20)).ConfigureAwait(true)).Operation.Status);
            await File.WriteAllTextAsync(Path.Combine(imported.WorkingDirectory, "unmanaged-preserved.txt"), "preserve").ConfigureAwait(true);

            string replacementArchive = Path.Combine(root, "hmcl-replacement.zip");
            CreateContentZip(replacementArchive, new Dictionary<string, string> { ["mods/replacement.jar"] = "replacement" });
            HmclReleaseCreationResult replacementRelease;
            using (MultipartFormDataContent releaseBody = [])
            {
                using ByteArrayContent releaseArchive = new(await File.ReadAllBytesAsync(replacementArchive).ConfigureAwait(true));
                using StringContent releaseMetadata = new(JsonSerializer.Serialize(firstMetadata with { Version = "2" }), Encoding.UTF8, "application/json");
                releaseBody.Add(releaseArchive, "file", "replacement.zip");
                releaseBody.Add(releaseMetadata, "metadata");
                using HttpResponseMessage replacementResponse = await client.PostAsync(
                    new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/releases", UriKind.Relative),
                    releaseBody).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Accepted, replacementResponse.StatusCode);
                OperationAcceptedDto replacementOperation = (await replacementResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
                replacementRelease = ReadOperationResult<HmclReleaseCreationResult>(
                    await WaitForOperationAsync(client, replacementOperation.OperationId, TimeSpan.FromSeconds(20)).ConfigureAwait(true));
            }

            using HttpResponseMessage secondSync = await client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/releases/{replacementRelease.Release.Id:D}/sync", UriKind.Relative),
                new SyncHmclReleaseToInstanceRequest(imported.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, secondSync.StatusCode);
            OperationAcceptedDto secondSyncOperation = (await secondSync.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            Assert.Equal("Succeeded", (await WaitForOperationAsync(client, secondSyncOperation.OperationId, TimeSpan.FromSeconds(20)).ConfigureAwait(true)).Operation.Status);
            Assert.False(File.Exists(Path.Combine(imported.WorkingDirectory, "server.jar")));
            Assert.True(File.Exists(Path.Combine(imported.WorkingDirectory, "mods", "replacement.jar")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(imported.WorkingDirectory, "unmanaged-preserved.txt")).ConfigureAwait(true));

            using HttpResponseMessage protectedPathResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/instances/attach", UriKind.Relative),
                new AttachInstanceDirectoryRequest("Rejected protected", nodeId, Environment.GetFolderPath(Environment.SpecialFolder.Windows), launch)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, protectedPathResponse.StatusCode);
            OperationAcceptedDto protectedPathOperation = (await protectedPathResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            OperationDetailsDto protectedPathDetails = await WaitForOperationAsync(client, protectedPathOperation.OperationId, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.Equal("Failed", protectedPathDetails.Operation.Status);
            Assert.Equal("instance-path-invalid", protectedPathDetails.Operation.ErrorCode);
            Assert.True(File.Exists(Path.Combine(externalDirectory, "server.jar")));
            await WaitForNodeMetricAsync(client, nodeId, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            using HttpResponseMessage rotate = await client.PostAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/credential/rotate", UriKind.Relative),
                content: null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            await WaitForNodeStatusAsync(client, nodeId, "Online", TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            using HttpResponseMessage revoke = await client.PostAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/credential/revoke", UriKind.Relative),
                content: null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            await WaitForNodeStatusAsync(client, nodeId, "Revoked", TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        finally
        {
            StopProcess(agent);
            StopProcess(server);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WaitForNodeMetricAsync(HttpClient client, Guid nodeId, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (true)
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/metrics", UriKind.Relative),
                cancellation.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(true));
            if (document.RootElement.GetProperty("items").GetArrayLength() > 0)
            {
                return;
            }

            await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
        }
    }

    private static async Task WaitForNodeStatusAsync(HttpClient client, Guid nodeId, string expectedStatus, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (true)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/nodes", UriKind.Relative), cancellation.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(true));
            JsonElement node = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == nodeId);
            if (string.Equals(node.GetProperty("status").GetString(), expectedStatus, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
        }
    }

    private static async Task<InstanceRuntimeDto> WaitForInstanceOutputAsync(
        HttpClient client,
        Guid nodeId,
        Guid instanceId,
        string expected,
        TimeSpan timeout,
        Process agent,
        Process server)
    {
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            while (true)
            {
                using HttpResponseMessage response = await client.GetAsync(
                    new Uri($"/api/v1/instances/{instanceId:D}/status?nodeId={nodeId:D}", UriKind.Relative),
                    cancellation.Token).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                InstanceRuntimeDto status = (await response.Content.ReadFromJsonAsync<InstanceRuntimeDto>(cancellation.Token).ConfigureAwait(true))!;
                if (status.RecentOutput.Any(line => line.Contains(expected, StringComparison.Ordinal)))
                {
                    return status;
                }

                await Task.Delay(50, cancellation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Instance output did not arrive before timeout. Agent: " + ReadProcessLog(agent) + " Server: " + ReadProcessLog(server));
            throw;
        }
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
                Assert.Fail($"Server exited with code {process.ExitCode}. {ReadProcessLog(process)}");
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

    private static async Task<Guid> WaitForNodeAsync(HttpClient client, Process agent, Process server, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            while (true)
            {
                if (agent.HasExited)
                {
                    Assert.Fail($"Agent exited with code {agent.ExitCode}. {ReadProcessLog(agent)}");
                }

                using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/nodes", UriKind.Relative), cancellation.Token).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(true));
                    JsonElement.ArrayEnumerator items = document.RootElement.GetProperty("items").EnumerateArray();
                    if (items.MoveNext() && items.Current.GetProperty("status").GetString() == "Online")
                    {
                        return items.Current.GetProperty("id").GetGuid();
                    }
                }

                await Task.Delay(100, cancellation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Agent did not register before timeout. Agent: " + ReadProcessLog(agent) + " Server: " + ReadProcessLog(server));
            throw;
        }
    }

    private static async Task<OperationDetailsDto> WaitForOperationAsync(HttpClient client, Guid operationId, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (true)
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri($"/api/v1/operations/{operationId:D}", UriKind.Relative),
                cancellation.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            OperationDetailsDto details = (await response.Content.ReadFromJsonAsync<OperationDetailsDto>(cancellation.Token).ConfigureAwait(true))!;
            if (details.Operation.Status is "Succeeded" or "Failed" or "Cancelled")
            {
                return details;
            }

            await Task.Delay(50, cancellation.Token).ConfigureAwait(true);
        }
    }

    private static T ReadOperationResult<T>(OperationDetailsDto details)
    {
        Assert.True(
            details.Operation.Status == "Succeeded",
            $"Operation {details.Operation.Id:D} finished as {details.Operation.Status} with {details.Operation.ErrorCode ?? "no error code"}. Logs: " +
            string.Join(Environment.NewLine, ProcessLogs.Values.SelectMany(queue => queue)));
        Assert.False(string.IsNullOrWhiteSpace(details.Result));
        return JsonSerializer.Deserialize<T>(details.Result!, JsonSerializerOptions.Web)!;
    }

    private static Process StartProcess(string executable, IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        Assert.True(process.Start());
        ConcurrentQueue<string> lines = new();
        ProcessLogs[process.Id] = lines;
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lines.Enqueue(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                lines.Enqueue(eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
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

            _ = ProcessLogs.TryRemove(process.Id, out _);
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

    private static string ReadProcessLog(Process process)
    {
        return ProcessLogs.TryGetValue(process.Id, out ConcurrentQueue<string>? lines)
            ? string.Join(" | ", lines)
            : "No captured process log.";
    }

    private static void CreateServerZip(string path)
    {
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("server.jar", CompressionLevel.Fastest);
        using (Stream stream = entry.Open())
        {
            stream.Write(File.ReadAllBytes(FindFixtureJar()));
        }

        ZipArchiveEntry payload = archive.CreateEntry("synthetic-large-payload.bin", CompressionLevel.NoCompression);
        using (Stream payloadStream = payload.Open())
        {
            byte[] block = new byte[1024 * 1024];
            for (int index = 0; index < 31; index++)
            {
                payloadStream.Write(block);
            }
        }

        ZipArchiveEntry eula = archive.CreateEntry("eula.txt", CompressionLevel.Fastest);
        using Stream eulaStream = eula.Open();
        eulaStream.Write(Encoding.UTF8.GetBytes("# synthetic process fixture\r\neula=false\r\n"));
        Assert.True(new FileInfo(path).Length > 30_000_000);
    }

    private static void CreateContentZip(string path, IReadOnlyDictionary<string, string> files)
    {
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        foreach ((string relative, string content) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
            using Stream stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private static string FindJavaExecutable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? java = path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "java.exe"))
            .FirstOrDefault(File.Exists);
        return java ?? throw new InvalidOperationException("The process integration fixture requires java.exe on PATH.");
    }

    private static string FindFixtureJar()
    {
        return Path.Combine(AppContext.BaseDirectory, "Servitium.JavaServerFixture.jar");
    }
}
