using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using Servitium.Contracts.Distribution;
using Servitium.Contracts.Common;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;
using Servitium.Infrastructure.Distribution;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class HmclDistributionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1300")]
    [Trait("TestId", "INT-1390")]
    [Trait("TestId", "INT-1400")]
    [Trait("TestId", "INT-1410")]
    [Trait("TestId", "SEC-280")]
    [Trait("TestId", "SEC-281")]
    public async Task 合成归档_物化发布并提供Hmcl缓存与Range契约()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("synthetic", "Synthetic").ConfigureAwait(true);
        string archive = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/example.jar"] = Encoding.UTF8.GetBytes("synthetic-mod-v1"),
            ["配置/含 空格.txt"] = Encoding.UTF8.GetBytes("synthetic-unicode"),
            ["options.txt"] = Encoding.UTF8.GetBytes("must-not-distribute"),
        });
        try
        {
            HmclReleaseCreationResult created = await test.CreateReleaseAsync(channelId, archive, Metadata("1.0.0")).ConfigureAwait(true);
            Assert.Equal(2, created.Release.Files.Count);
            Assert.Contains("options.txt", created.ExcludedPaths.Keys);

            await test.PublishAsync(channelId, created.Release.Id).ConfigureAwait(true);

            using HttpRequestMessage manifestRequest = new(HttpMethod.Get, new Uri("/hmcl/synthetic/server-manifest.json", UriKind.Relative));
            manifestRequest.Headers.Add("Origin", "https://allowed.example");
            using HttpResponseMessage manifest = await test.PublicClient.SendAsync(manifestRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            Assert.False(manifest.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.Equal("no-cache", manifest.Headers.CacheControl?.ToString());
            Assert.NotNull(manifest.Headers.ETag);
            string manifestText = await manifest.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain("options.txt", manifestText, StringComparison.Ordinal);
            Assert.True(manifestText.IndexOf("\"name\"", StringComparison.Ordinal) < manifestText.IndexOf("\"fileApi\"", StringComparison.Ordinal));

            using HttpRequestMessage conditionalRequest = new(HttpMethod.Get, new Uri("/hmcl/synthetic/server-manifest.json", UriKind.Relative));
            conditionalRequest.Headers.IfNoneMatch.Add(manifest.Headers.ETag);
            using HttpResponseMessage notModified = await test.PublicClient.SendAsync(conditionalRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal("no-cache", notModified.Headers.CacheControl?.ToString());

            using HttpRequestMessage rangeRequest = new(HttpMethod.Get, new Uri("/hmcl/synthetic/overrides/%E9%85%8D%E7%BD%AE/%E5%90%AB%20%E7%A9%BA%E6%A0%BC.txt", UriKind.Relative));
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 8);
            using HttpResponseMessage range = await test.PublicClient.SendAsync(rangeRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal("bytes", string.Join(',', range.Headers.AcceptRanges));
            Assert.Null(range.Content.Headers.ContentEncoding.SingleOrDefault());
            Assert.Equal("synthetic", await range.Content.ReadAsStringAsync().ConfigureAwait(true));

            using HttpRequestMessage headRequest = new(HttpMethod.Head, new Uri("/hmcl/synthetic/overrides/mods/example.jar", UriKind.Relative));
            using HttpResponseMessage head = await test.PublicClient.SendAsync(headRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(16, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1310")]
    [Trait("TestId", "INT-1320")]
    [Trait("TestId", "SEC-165")]
    public async Task Prism合成包_预览转换且危险归档被拒()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("prism-test", "Prism test").ConfigureAwait(true);
        string prism = CreateZip(new Dictionary<string, byte[]>
        {
            ["wrapper/instance.cfg"] = Encoding.UTF8.GetBytes("name=Synthetic Prism\n"),
            ["wrapper/mmc-pack.json"] = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.0\"}]}"),
            ["wrapper/minecraft/mods/client.jar"] = Encoding.UTF8.GetBytes("client-mod"),
            ["wrapper/minecraft/options.txt"] = Encoding.UTF8.GetBytes("local-state"),
        });
        string malicious = CreateZip(new Dictionary<string, byte[]> { ["../escape.txt"] = Encoding.UTF8.GetBytes("escape") });
        string changedPrism = CreateZip(new Dictionary<string, byte[]>
        {
            ["changed/instance.cfg"] = Encoding.UTF8.GetBytes("name=Changed Prism\n"),
            ["changed/mmc-pack.json"] = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.2\"},{\"uid\":\"net.minecraftforge\",\"version\":\"52.0.0\"}]}"),
            ["changed/minecraft/mods/other.jar"] = Encoding.UTF8.GetBytes("other-mod"),
        });
        try
        {
            using MultipartFormDataContent previewBody = new();
            using ByteArrayContent previewContent = new(await File.ReadAllBytesAsync(prism).ConfigureAwait(true));
            using StringContent previewChannel = new(channelId.ToString("D"));
            previewBody.Add(previewContent, "file", "prism.zip");
            previewBody.Add(previewChannel, "channelId");
            using HttpResponseMessage previewResponse = await test.Client.PostAsync(new Uri("/api/v1/imports/prism/preview", UriKind.Relative), previewBody).ConfigureAwait(true);
            string previewResponseBody = await previewResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(previewResponse.StatusCode == HttpStatusCode.Created, previewResponseBody);
            StagedPrismPreviewDto preview = JsonSerializer.Deserialize<StagedPrismPreviewDto>(previewResponseBody, JsonSerializerOptions.Web)!;
            Assert.Equal("Synthetic Prism", preview.Preview.InstanceName);
            Assert.Equal("1.21.1", preview.Preview.GameVersion);
            Assert.Equal("fabric 0.16.0", preview.Preview.Loader);
            _ = Assert.Single(preview.Preview.IncludedPaths);
            Assert.Contains("options.txt", preview.Preview.ExcludedPaths.Keys);
            Assert.NotNull(preview.Difference);
            Assert.Equal(["mods/client.jar"], preview.Difference.Added);
            Assert.Empty(preview.Difference.Replaced);
            Assert.Empty(preview.Difference.Deleted);

            ConvertStagedPrismRequest request = new(channelId, Metadata("prism-1"));
            using HttpResponseMessage convertedResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/imports/prism/{preview.UploadId:D}/convert", UriKind.Relative),
                request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, convertedResponse.StatusCode);
            HmclReleaseCreationResult converted = await test.ReadOperationResultAsync<HmclReleaseCreationResult>(convertedResponse).ConfigureAwait(true);
            _ = Assert.Single(converted.Release.Files);
            Assert.Equal("mods/client.jar", converted.Release.Files[0].Path);

            using HttpResponseMessage publishResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/publish", UriKind.Relative),
                new PublishHmclChannelRequest(converted.Release.Id)).ConfigureAwait(true);
            _ = await test.ReadOperationResultAsync<HmclPublishDiffDto>(publishResponse).ConfigureAwait(true);

            using MultipartFormDataContent changedBody = new();
            using ByteArrayContent changedContent = new(await File.ReadAllBytesAsync(changedPrism).ConfigureAwait(true));
            using StringContent changedChannel = new(channelId.ToString("D"));
            changedBody.Add(changedContent, "file", "changed-prism.zip");
            changedBody.Add(changedChannel, "channelId");
            using HttpResponseMessage changedResponse = await test.Client.PostAsync(
                new Uri("/api/v1/imports/prism/preview", UriKind.Relative),
                changedBody).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, changedResponse.StatusCode);
            StagedPrismPreviewDto changed = (await changedResponse.Content.ReadFromJsonAsync<StagedPrismPreviewDto>().ConfigureAwait(true))!;
            Assert.NotNull(changed.Difference);
            Assert.Equal(["mods/other.jar"], changed.Difference.Added);
            Assert.Equal(["mods/client.jar"], changed.Difference.Deleted);
            Assert.True(changed.Difference.LoaderChanged);

            using MultipartFormDataContent maliciousBody = new();
            using ByteArrayContent maliciousContent = new(await File.ReadAllBytesAsync(malicious).ConfigureAwait(true));
            maliciousBody.Add(maliciousContent, "file", "malicious.zip");
            using HttpResponseMessage rejected = await test.Client.PostAsync(new Uri("/api/v1/imports/prism/preview", UriKind.Relative), maliciousBody).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            string rejectedJson = await rejected.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("archive-unsafe-path", rejectedJson, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(prism);
            File.Delete(malicious);
            File.Delete(changedPrism);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1330")]
    [Trait("TestId", "INT-1340")]
    [Trait("TestId", "INT-1350")]
    public async Task 发布门禁_汇总路径加载器与内容的全部问题()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("gate-test", "Gate test").ConfigureAwait(true);
        string archive = CreateZip(new Dictionary<string, byte[]> { ["mods/gate.jar"] = Encoding.UTF8.GetBytes("gate-content") });
        try
        {
            HmclReleaseCreationResult release = await test.CreateReleaseAsync(channelId, archive, Metadata("gate")).ConfigureAwait(true);
            HmclDistributionStore store = test.Services.GetRequiredService<HmclDistributionStore>();
            File.Delete(store.GetContentPath(Assert.Single(release.Release.Files).Sha1));

            string databasePath = test.Services.GetRequiredService<Infrastructure.Persistence.DatabaseLocation>().Path;
            SqliteConnection connection = new("Data Source=" + databasePath + ";Pooling=False");
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync().ConfigureAwait(true);
                SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE HmclReleases SET LoaderId = 'invalid-loader', LoaderVersion = '1' WHERE lower(Id) = lower($id);";
                _ = command.Parameters.AddWithValue("$id", release.Release.Id.ToString("D"));
                Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
                command.CommandText = "UPDATE HmclReleaseFiles SET Path = '../escape.jar' WHERE lower(ReleaseId) = lower($id);";
                Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
            }

            using HttpResponseMessage previewResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/preview", UriKind.Relative),
                new PublishHmclChannelRequest(release.Release.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            HmclPublishDiffDto preview = (await previewResponse.Content.ReadFromJsonAsync<HmclPublishDiffDto>().ConfigureAwait(true))!;
            Assert.Contains(preview.ValidationIssues!, issue => issue.Code == "path-invalid");
            Assert.Contains(preview.ValidationIssues!, issue => issue.Code == "loader-addon-invalid");
            Assert.Contains(preview.ValidationIssues!, issue => issue.Code == "content-missing");
            Assert.Contains(preview.ValidationIssues!, issue => issue.Code == "manifest-invalid");

            using HttpResponseMessage publishResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/publish", UriKind.Relative),
                new PublishHmclChannelRequest(release.Release.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);
            OperationDetailsDto failed = await test.ReadOperationDetailsAsync(publishResponse).ConfigureAwait(true);
            Assert.Equal("Failed", failed.Operation.Status);
            Assert.Equal("hmcl-release-validation-failed", failed.Operation.ErrorCode);
            string failedResult = Assert.IsType<string>(failed.Result);
            Assert.Contains("path-invalid", failedResult, StringComparison.Ordinal);
            Assert.Contains("loader-addon-invalid", failedResult, StringComparison.Ordinal);
            Assert.Contains("content-missing", failedResult, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1370")]
    [Trait("TestId", "INT-1430")]
    [Trait("TestId", "INT-1440")]
    [Trait("TestId", "INT-1450")]
    [Trait("TestId", "SEC-250")]
    [Trait("TestId", "SEC-251")]
    public async Task 私有频道_令牌轮换双向可见性回滚与统计均生效()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        HmclChannelDto channel = await test.CreateChannelDetailsAsync("private-test", "Private test", "token").ConfigureAwait(true);
        Assert.NotNull(channel.Token);
        Assert.Equal(43, channel.Token.Length);
        Assert.EndsWith("/server-manifest.json", channel.ManifestUrl!.AbsoluteUri, StringComparison.Ordinal);

        string firstArchive = CreateZip(new Dictionary<string, byte[]> { ["mods/example.jar"] = Encoding.UTF8.GetBytes("v1") });
        string secondArchive = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/example.jar"] = Encoding.UTF8.GetBytes("v2"),
            ["config/new.txt"] = Encoding.UTF8.GetBytes("added"),
        });
        try
        {
            HmclReleaseCreationResult first = await test.CreateReleaseAsync(channel.Id, firstArchive, Metadata("1")).ConfigureAwait(true);
            HmclReleaseCreationResult second = await test.CreateReleaseAsync(channel.Id, secondArchive, Metadata("2")).ConfigureAwait(true);
            await test.PublishAsync(channel.Id, first.Release.Id).ConfigureAwait(true);

            using HttpResponseMessage noToken = await test.PublicClient.GetAsync(new Uri("/hmcl/private-test/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
            using HttpResponseMessage wrongToken = await test.PublicClient.GetAsync(new Uri("/hmcl/private-test/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
            string oldToken = channel.Token;
            using HttpResponseMessage privateManifest = await test.PublicClient.GetAsync(new Uri($"/hmcl/private-test/{oldToken}/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, privateManifest.StatusCode);
            Assert.Contains($"/hmcl/private-test/{oldToken}", await privateManifest.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

            using HttpResponseMessage rotate = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/token/rotate", UriKind.Relative),
                new ConfirmHmclChannelRequest("Private test")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            HmclTokenResultDto rotated = (await rotate.Content.ReadFromJsonAsync<HmclTokenResultDto>().ConfigureAwait(true))!;
            Assert.NotEqual(oldToken, rotated.Token);
            using HttpResponseMessage oldRejected = await test.PublicClient.GetAsync(new Uri($"/hmcl/private-test/{oldToken}/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, oldRejected.StatusCode);
            using HttpResponseMessage newAccepted = await test.PublicClient.GetAsync(new Uri($"/hmcl/private-test/{rotated.Token}/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, newAccepted.StatusCode);

            await test.PublishAsync(channel.Id, second.Release.Id).ConfigureAwait(true);
            using HttpResponseMessage previewRollback = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/preview", UriKind.Relative),
                new PublishHmclChannelRequest(first.Release.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, previewRollback.StatusCode);
            HmclPublishDiffDto rollbackDiff = (await previewRollback.Content.ReadFromJsonAsync<HmclPublishDiffDto>().ConfigureAwait(true))!;
            Assert.Contains("config/new.txt", rollbackDiff.Deleted);
            await test.PublishAsync(channel.Id, first.Release.Id).ConfigureAwait(true);
            using HttpResponseMessage rollbackAudit = await test.Client.GetAsync(
                new Uri("/api/v1/audit/security?action=hmcl.rolled-back", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, rollbackAudit.StatusCode);
            string rollbackAuditJson = await rollbackAudit.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("hmcl.rolled-back", rollbackAuditJson, StringComparison.Ordinal);
            Assert.Contains(first.Release.Id.ToString("D"), rollbackAuditJson, StringComparison.OrdinalIgnoreCase);

            using HttpResponseMessage firstHistoryResponse = await test.Client.GetAsync(
                new Uri($"/api/v1/hmcl/releases/{first.Release.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, firstHistoryResponse.StatusCode);
            HmclReleaseDto firstHistory = (await firstHistoryResponse.Content.ReadFromJsonAsync<HmclReleaseDto>().ConfigureAwait(true))!;
            Assert.Equal(2, firstHistory.Activations!.Count);
            Assert.Null(firstHistory.Activations[0].EndedAt);
            _ = Assert.NotNull(firstHistory.Activations[1].EndedAt);
            Assert.True(firstHistory.Activations[1].EndedAt <= firstHistory.Activations[0].StartedAt);

            using HttpResponseMessage secondHistoryResponse = await test.Client.GetAsync(
                new Uri($"/api/v1/hmcl/releases/{second.Release.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, secondHistoryResponse.StatusCode);
            HmclReleaseDto secondHistory = (await secondHistoryResponse.Content.ReadFromJsonAsync<HmclReleaseDto>().ConfigureAwait(true))!;
            HmclReleaseActivationDto secondActivation = Assert.Single(secondHistory.Activations!);
            _ = Assert.NotNull(secondActivation.EndedAt);

            using HttpResponseMessage makePublic = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/visibility", UriKind.Relative),
                new ChangeHmclVisibilityRequest("public", "Private test")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, makePublic.StatusCode);
            using HttpResponseMessage stalePrivate = await test.PublicClient.GetAsync(new Uri($"/hmcl/private-test/{rotated.Token}/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, stalePrivate.StatusCode);
            using HttpResponseMessage publicManifest = await test.PublicClient.GetAsync(new Uri("/hmcl/private-test/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, publicManifest.StatusCode);

            using HttpResponseMessage makePrivate = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/visibility", UriKind.Relative),
                new ChangeHmclVisibilityRequest("token", "Private test")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, makePrivate.StatusCode);
            HmclTokenResultDto replacement = (await makePrivate.Content.ReadFromJsonAsync<HmclTokenResultDto>().ConfigureAwait(true))!;
            using HttpResponseMessage publicRejected = await test.PublicClient.GetAsync(new Uri("/hmcl/private-test/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, publicRejected.StatusCode);
            using HttpResponseMessage replacementAccepted = await test.PublicClient.GetAsync(new Uri($"/hmcl/private-test/{replacement.Token}/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, replacementAccepted.StatusCode);

            using HttpResponseMessage statsResponse = await test.Client.GetAsync(new Uri($"/api/v1/hmcl/channels/{channel.Id:D}/stats?days=7", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
            CursorPage<HmclAccessStatDto> stats = (await statsResponse.Content.ReadFromJsonAsync<CursorPage<HmclAccessStatDto>>().ConfigureAwait(true))!;
            Assert.True(stats.Items.Sum(item => item.Manifest200) >= 4);
            Assert.True(stats.Items.Sum(item => item.Errors) >= 4);
        }
        finally
        {
            File.Delete(firstArchive);
            File.Delete(secondArchive);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1380")]
    [Trait("TestId", "INT-1460")]
    public async Task 保留淘汰_引用计数回收且完整性巡检发现缺失对象()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("retention-test", "Retention test").ConfigureAwait(true);
        using HttpResponseMessage update = await test.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/hmcl/channels/{channelId:D}", UriKind.Relative),
            new UpdateHmclChannelRequest("Retention test", "synthetic", 3, null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        List<string> archives = [];
        List<HmclReleaseCreationResult> releases = [];
        try
        {
            for (int index = 0; index < 5; index++)
            {
                string archive = CreateZip(new Dictionary<string, byte[]>
                {
                    ["mods/shared.jar"] = Encoding.UTF8.GetBytes("shared"),
                    [$"config/version-{index}.txt"] = Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
                archives.Add(archive);
                HmclReleaseCreationResult release = await test.CreateReleaseAsync(channelId, archive, Metadata(index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(true);
                releases.Add(release);
                await test.PublishAsync(channelId, release.Release.Id).ConfigureAwait(true);
            }

            using HttpResponseMessage list = await test.Client.GetAsync(new Uri($"/api/v1/hmcl/channels/{channelId:D}/releases", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            string listJson = await list.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain(releases[0].Release.Id.ToString("D"), listJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(releases[4].Release.Id.ToString("D"), listJson, StringComparison.OrdinalIgnoreCase);

            HmclDistributionStore store = test.Services.GetRequiredService<HmclDistributionStore>();
            string sharedSha1 = releases[4].Release.Files.Single(file => file.Path == "mods/shared.jar").Sha1;
            Assert.True(File.Exists(store.GetContentPath(sharedSha1)));
            File.Delete(store.GetContentPath(releases[4].Release.Files.Single(file => file.Path.StartsWith("config/", StringComparison.Ordinal)).Sha1));

            using HttpResponseMessage integrityResponse = await test.Client.PostAsync(new Uri("/api/v1/hmcl/integrity/run", UriKind.Relative), null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, integrityResponse.StatusCode);
            HmclIntegrityResultDto integrity = (await integrityResponse.Content.ReadFromJsonAsync<HmclIntegrityResultDto>().ConfigureAwait(true))!;
            Assert.Contains(integrity.Failures, failure => failure.ReleaseId == releases[4].Release.Id);

            using HttpRequestMessage deleteRequest = new(HttpMethod.Delete, new Uri($"/api/v1/hmcl/channels/{channelId:D}", UriKind.Relative))
            {
                Content = JsonContent.Create(new ConfirmHmclChannelRequest("Retention test")),
            };
            using HttpResponseMessage delete = await test.Client.SendAsync(deleteRequest).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, delete.StatusCode);
            _ = await test.ReadOperationResultAsync<object>(delete, resultRequired: false).ConfigureAwait(true);
            HmclReclamationResultDto reclaimed = await store.ReclaimAsync(DateTimeOffset.UtcNow.AddHours(25), default).ConfigureAwait(true);
            Assert.True(reclaimed.RemovedObjects >= 1);
            Assert.False(File.Exists(store.GetContentPath(sharedSha1)));
        }
        finally
        {
            foreach (string archive in archives)
            {
                File.Delete(archive);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1350")]
    [Trait("TestId", "INT-1360")]
    [Trait("TestId", "INT-1420")]
    [Trait("TestId", "UNT-530")]
    public async Task 并发发布_指针始终有效且历史路径可以回退()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("atomic-test", "Atomic test").ConfigureAwait(true);
        string baselineArchive = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/shared.jar"] = Encoding.UTF8.GetBytes("v1"),
            ["config/removed.txt"] = Encoding.UTF8.GetBytes("removed"),
            ["legacy/fallback.txt"] = Encoding.UTF8.GetBytes("historical"),
        });
        string secondArchive = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/shared.jar"] = Encoding.UTF8.GetBytes("v2"),
            ["config/added.txt"] = Encoding.UTF8.GetBytes("second"),
        });
        string thirdArchive = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/shared.jar"] = Encoding.UTF8.GetBytes("v3"),
            ["config/added.txt"] = Encoding.UTF8.GetBytes("third"),
        });
        try
        {
            HmclReleaseCreationResult baseline = await test.CreateReleaseAsync(channelId, baselineArchive, Metadata("baseline")).ConfigureAwait(true);
            HmclReleaseCreationResult second = await test.CreateReleaseAsync(channelId, secondArchive, Metadata("second")).ConfigureAwait(true);
            HmclReleaseCreationResult third = await test.CreateReleaseAsync(channelId, thirdArchive, Metadata("third")).ConfigureAwait(true);
            await test.PublishAsync(channelId, baseline.Release.Id).ConfigureAwait(true);

            using HttpResponseMessage previewResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/preview", UriKind.Relative),
                new PublishHmclChannelRequest(second.Release.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            HmclPublishDiffDto preview = (await previewResponse.Content.ReadFromJsonAsync<HmclPublishDiffDto>().ConfigureAwait(true))!;
            Assert.Equal(["config/added.txt"], preview.Added);
            Assert.Equal(["mods/shared.jar"], preview.Replaced);
            Assert.Equal(["config/removed.txt", "legacy/fallback.txt"], preview.Deleted);
            Assert.False(preview.LoaderChanged);

            using HttpResponseMessage publishSecond = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/publish", UriKind.Relative),
                new PublishHmclChannelRequest(second.Release.Id)).ConfigureAwait(true);
            using HttpResponseMessage publishThird = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/publish", UriKind.Relative),
                new PublishHmclChannelRequest(third.Release.Id)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, publishSecond.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, publishThird.StatusCode);

            Guid secondOperationId = (await publishSecond.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!.OperationId;
            Guid thirdOperationId = (await publishThird.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!.OperationId;
            Task<OperationDetailsDto> secondCompletion = test.ReadOperationDetailsAsync(secondOperationId);
            Task<OperationDetailsDto> thirdCompletion = test.ReadOperationDetailsAsync(thirdOperationId);
            while (!secondCompletion.IsCompleted || !thirdCompletion.IsCompleted)
            {
                using HttpResponseMessage currentResponse = await test.Client.GetAsync(
                    new Uri($"/api/v1/hmcl/channels/{channelId:D}", UriKind.Relative)).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
                HmclChannelDetailsDto current = (await currentResponse.Content.ReadFromJsonAsync<HmclChannelDetailsDto>().ConfigureAwait(true))!;
                Assert.Contains(current.Channel.ActiveReleaseId, new Guid?[] { baseline.Release.Id, second.Release.Id, third.Release.Id });
                _ = await Task.WhenAny(Task.WhenAll(secondCompletion, thirdCompletion), Task.Delay(10)).ConfigureAwait(true);
            }

            Assert.Equal("Succeeded", (await secondCompletion.ConfigureAwait(true)).Operation.Status);
            Assert.Equal("Succeeded", (await thirdCompletion.ConfigureAwait(true)).Operation.Status);
            using HttpResponseMessage finalResponse = await test.Client.GetAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}", UriKind.Relative)).ConfigureAwait(true);
            HmclChannelDetailsDto final = (await finalResponse.Content.ReadFromJsonAsync<HmclChannelDetailsDto>().ConfigureAwait(true))!;
            Assert.Contains(final.Channel.ActiveReleaseId, new Guid?[] { second.Release.Id, third.Release.Id });

            using HttpResponseMessage fallback = await test.PublicClient.GetAsync(
                new Uri("/hmcl/atomic-test/overrides/legacy/fallback.txt", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Equal("historical", await fallback.Content.ReadAsStringAsync().ConfigureAwait(true));
        }
        finally
        {
            File.Delete(baselineArchive);
            File.Delete(secondArchive);
            File.Delete(thirdArchive);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-260")]
    public async Task 分发路径_拒绝穿越与绝对形式且不泄露外部文件()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("path-test", "Path test").ConfigureAwait(true);
        string archive = CreateZip(new Dictionary<string, byte[]> { ["safe.txt"] = Encoding.UTF8.GetBytes("safe") });
        string sentinel = Path.Combine(Path.GetDirectoryName(test.DataRoot)!, $"servitium-hmcl-sentinel-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(sentinel, "outside-secret").ConfigureAwait(true);
        try
        {
            HmclReleaseCreationResult release = await test.CreateReleaseAsync(channelId, archive, Metadata("path")).ConfigureAwait(true);
            await test.PublishAsync(channelId, release.Release.Id).ConfigureAwait(true);

            string[] rejectedPaths =
            [
                "/hmcl/path-test/overrides/%2E%2E%2Fsafe.txt",
                "/hmcl/path-test/overrides/%2FWindows%2Fwin.ini",
                "/hmcl/path-test/overrides/C%3A%2FWindows%2Fwin.ini",
            ];
            foreach (string rejectedPath in rejectedPaths)
            {
                using HttpResponseMessage response = await test.PublicClient.GetAsync(new Uri(rejectedPath, UriKind.Relative)).ConfigureAwait(true);
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
                Assert.DoesNotContain("outside-secret", await response.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(sentinel);
            File.Delete(archive);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-270")]
    public async Task 分发限流_允许客户端并发预算且超过专用阈值后拒绝()
    {
        using HmclTestContext test = new(hmclRequestsPerMinute: 640);
        for (int index = 0; index < 640; index++)
        {
            using HttpResponseMessage response = await test.PublicClient.GetAsync(
                new Uri("/hmcl/missing/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using HttpResponseMessage limited = await test.PublicClient.GetAsync(
            new Uri("/hmcl/missing/server-manifest.json", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1300")]
    public async Task Archive会话_乱序续传逐块校验并创建不可变Release()
    {
        using HmclTestContext test = new();
        await test.InitializeAndSignInAsync().ConfigureAwait(true);
        Guid channelId = await test.CreateChannelAsync("archive-session", "Archive session").ConfigureAwait(true);
        byte[] payload = new byte[700 * 1024];
        for (int offset = 0, block = 0; offset < payload.Length; offset += 32, block++)
        {
            byte[] hash = SHA256.HashData(BitConverter.GetBytes(block));
            hash.AsSpan(0, Math.Min(hash.Length, payload.Length - offset)).CopyTo(payload.AsSpan(offset));
        }
        string sourcePath = CreateZip(new Dictionary<string, byte[]>
        {
            ["mods/large.jar"] = payload,
            ["config/value.txt"] = Encoding.UTF8.GetBytes("synthetic"),
        });
        try
        {
            byte[] source = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true);
            const int partSize = 256 * 1024;
            using HttpResponseMessage create = await test.Client.PostAsJsonAsync(
                new Uri("/api/v1/hmcl/archives", UriKind.Relative),
                new CreateHmclArchiveRequest("synthetic.zip", source.LongLength, partSize)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            HmclArchiveDto session = (await create.Content.ReadFromJsonAsync<HmclArchiveDto>().ConfigureAwait(true))!;
            Assert.True(session.PartCount >= 2);

            byte[] firstPart = source.AsSpan(0, Math.Min(partSize, source.Length)).ToArray();
            using ByteArrayContent wrongContent = new(firstPart);
            using HttpResponseMessage wrong = await test.Client.PutAsync(
                new Uri($"/api/v1/hmcl/archives/{session.Id:D}/parts/0?sha256={new string('0', 64)}", UriKind.Relative),
                wrongContent).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

            foreach (int index in Enumerable.Range(0, session.PartCount).Reverse())
            {
                int offset = checked(index * partSize);
                byte[] part = source.AsSpan(offset, Math.Min(partSize, source.Length - offset)).ToArray();
                string hash = Convert.ToHexString(SHA256.HashData(part));
                using ByteArrayContent content = new(part);
                using HttpResponseMessage uploaded = await test.Client.PutAsync(
                    new Uri($"/api/v1/hmcl/archives/{session.Id:D}/parts/{index}?sha256={hash}", UriKind.Relative),
                    content).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            }

            string fullHash = Convert.ToHexString(SHA256.HashData(source));
            using HttpResponseMessage completedResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/archives/{session.Id:D}/complete", UriKind.Relative),
                new CompleteHmclArchiveRequest(fullHash)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
            HmclArchiveDto completed = (await completedResponse.Content.ReadFromJsonAsync<HmclArchiveDto>().ConfigureAwait(true))!;
            Assert.Equal("ready", completed.Status);

            using HttpResponseMessage releaseResponse = await test.Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/releases", UriKind.Relative),
                new CreateHmclReleaseFromArchiveRequest(session.Id, Metadata("archive-session"))).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, releaseResponse.StatusCode);
            HmclReleaseCreationResult release = await test.ReadOperationResultAsync<HmclReleaseCreationResult>(releaseResponse).ConfigureAwait(true);
            Assert.Equal(2, release.Release.Files.Count);

            using HttpResponseMessage deleted = await test.Client.DeleteAsync(new Uri($"/api/v1/hmcl/archives/{session.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using HttpResponseMessage missing = await test.Client.GetAsync(new Uri($"/api/v1/hmcl/archives/{session.Id:D}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static CreateHmclReleaseMetadataRequest Metadata(string version)
    {
        return new(
        "Synthetic release", "Servitium tests", version, "Synthetic content only", "1.21.1", "fabric", "0.16.0", null);
    }

    private static string CreateZip(IReadOnlyDictionary<string, byte[]> files)
    {
        string path = Path.Combine(Path.GetTempPath(), $"servitium-hmcl-{Guid.NewGuid():N}.zip");
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }

    private sealed class HmclTestContext : IDisposable
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;

        internal HmclTestContext(int? hmclRequestsPerMinute = null)
        {
            DataRoot = Path.Combine(Path.GetTempPath(), $"servitium-hmcl-test-{Guid.NewGuid():N}");
            _baseFactory = new WebApplicationFactory<Program>();
            _factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("Servitium:DataRoot", DataRoot);
                _ = builder.UseSetting("Servitium:AllowedOrigins:0", "https://allowed.example");
                if (hmclRequestsPerMinute is int limit)
                {
                    _ = builder.UseSetting("Servitium:HmclRequestsPerMinute", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            });
            Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
            PublicClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        }

        internal string DataRoot { get; }
        internal HttpClient Client { get; }
        internal HttpClient PublicClient { get; }
        internal IServiceProvider Services => _factory.Services;

        internal async Task InitializeAndSignInAsync()
        {
            using HttpResponseMessage state = await Client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
            string csrfCookie = state.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
            string csrf = csrfCookie["servitium.csrf=".Length..csrfCookie.IndexOf(';', StringComparison.Ordinal)];
            Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
            using HttpResponseMessage initialize = await Client.PostAsJsonAsync(
                new Uri("/api/v1/auth/initialize", UriKind.Relative),
                new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
            using HttpResponseMessage login = await Client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        internal async Task<Guid> CreateChannelAsync(string slug, string name)
        {
            return (await CreateChannelDetailsAsync(slug, name, "public").ConfigureAwait(true)).Id;
        }

        internal async Task<HmclChannelDto> CreateChannelDetailsAsync(string slug, string name, string visibility)
        {
            using HttpResponseMessage response = await Client.PostAsJsonAsync(
                new Uri("/api/v1/hmcl/channels", UriKind.Relative),
                new CreateHmclChannelRequest(slug, name, visibility)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<HmclChannelDto>().ConfigureAwait(true))!;
        }

        internal async Task<HmclReleaseCreationResult> CreateReleaseAsync(Guid channelId, string archive, CreateHmclReleaseMetadataRequest metadata)
        {
            using MultipartFormDataContent body = new();
            using ByteArrayContent archiveContent = new(await File.ReadAllBytesAsync(archive).ConfigureAwait(true));
            using StringContent metadataContent = new(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
            body.Add(archiveContent, "file", "content.zip");
            body.Add(metadataContent, "metadata");
            using HttpResponseMessage response = await Client.PostAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/releases", UriKind.Relative),
                body).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return await ReadOperationResultAsync<HmclReleaseCreationResult>(response).ConfigureAwait(true);
        }

        internal async Task PublishAsync(Guid channelId, Guid releaseId)
        {
            using HttpResponseMessage response = await Client.PostAsJsonAsync(
                new Uri($"/api/v1/hmcl/channels/{channelId:D}/publish", UriKind.Relative),
                new PublishHmclChannelRequest(releaseId)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            _ = await ReadOperationResultAsync<HmclPublishDiffDto>(response).ConfigureAwait(true);
        }

        internal async Task<T> ReadOperationResultAsync<T>(HttpResponseMessage acceptedResponse, bool resultRequired = true)
        {
            OperationDetailsDto details = await ReadOperationDetailsAsync(acceptedResponse).ConfigureAwait(true);
            Assert.True(
                string.Equals("Succeeded", details.Operation.Status, StringComparison.Ordinal),
                $"Operation failed with {details.Operation.ErrorCode}: {details.Result}");
            if (!resultRequired)
            {
                return default!;
            }

            Assert.False(string.IsNullOrWhiteSpace(details.Result));
            return JsonSerializer.Deserialize<T>(details.Result!, JsonSerializerOptions.Web)!;
        }

        internal async Task<OperationDetailsDto> ReadOperationDetailsAsync(HttpResponseMessage acceptedResponse)
        {
            OperationAcceptedDto accepted = (await acceptedResponse.Content.ReadFromJsonAsync<OperationAcceptedDto>().ConfigureAwait(true))!;
            return await ReadOperationDetailsAsync(accepted.OperationId).ConfigureAwait(true);
        }

        internal async Task<OperationDetailsDto> ReadOperationDetailsAsync(Guid operationId)
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                using HttpResponseMessage response = await Client.GetAsync(
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

        public void Dispose()
        {
            PublicClient.Dispose();
            Client.Dispose();
            _factory.Dispose();
            _baseFactory.Dispose();
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }
    }
}
