using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Settings;
using Servitium.Domain.Auditing;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class AuditEndpointTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1100")]
    [Trait("TestId", "INT-1110")]
    [Trait("TestId", "INT-1130")]
    [Trait("TestId", "INT-1140")]
    [Trait("TestId", "INT-1200")]
    [Trait("TestId", "SEC-121")]
    [Trait("TestId", "SEC-330")]
    [Trait("TestId", "UNT-450")]
    public async Task 双日志使用正式路由过滤分页_设置审计脱敏且CSV阻断公式注入()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-audit-endpoint-{Guid.NewGuid():N}");
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Servitium:DataRoot", dataRoot));
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        try
        {
            string csrf = await GetCsrfAsync(client).ConfigureAwait(true);
            client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
            using HttpResponseMessage initialize = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/initialize", UriKind.Relative),
                new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
            using HttpResponseMessage login = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            Dictionary<string, string?> identityValues = new(StringComparer.Ordinal)
            {
                ["oidcEnabled"] = "false",
                ["oidcClientSecret"] = "synthetic-secret-must-not-leak",
            };
            using HttpResponseMessage settings = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/identity", UriKind.Relative),
                new UpdateSettingsGroupRequest(identityValues, 0)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

            using HttpResponseMessage listedSettings = await client.GetAsync(new Uri("/api/v1/settings", UriKind.Relative)).ConfigureAwait(true);
            string listedSettingsJson = await listedSettings.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, listedSettings.StatusCode);
            Assert.Contains("\"listenPort\":\"restart-required\"", listedSettingsJson, StringComparison.Ordinal);
            Assert.Contains("\"oidcEnabled\":\"immediate\"", listedSettingsJson, StringComparison.Ordinal);
            Assert.Contains("\"oidcClientSecret\":\"••••••\"", listedSettingsJson, StringComparison.Ordinal);

            using ByteArrayContent validAssetFile = new([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
            using MultipartFormDataContent validAsset = CreateAsset(validAssetFile, "image/png", "logo.png");
            using HttpResponseMessage assetCreated = await client.PostAsync(new Uri("/api/v1/settings/assets?kind=logo", UriKind.Relative), validAsset).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, assetCreated.StatusCode);
            Assert.True(File.Exists(Path.Combine(dataRoot, "assets", "logo.png")));

            using ByteArrayContent invalidAssetFile = new(Encoding.UTF8.GetBytes("not-a-png"));
            using MultipartFormDataContent invalidAsset = CreateAsset(invalidAssetFile, "image/png", "fake.png");
            using HttpResponseMessage assetRejected = await client.PostAsync(new Uri("/api/v1/settings/assets?kind=logo", UriKind.Relative), invalidAsset).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, assetRejected.StatusCode);

            using ByteArrayContent oversizedAssetFile = new(new byte[(2 * 1024 * 1024) + 1]);
            using MultipartFormDataContent oversizedAsset = CreateAsset(oversizedAssetFile, "image/png", "large.png");
            using HttpResponseMessage oversizedRejected = await client.PostAsync(new Uri("/api/v1/settings/assets?kind=background", UriKind.Relative), oversizedAsset).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, oversizedRejected.StatusCode);

            using HttpResponseMessage disableApiKeys = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/security", UriKind.Relative),
                new UpdateSettingsGroupRequest(
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["apiKeysEnabled"] = "false" },
                    0)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, disableApiKeys.StatusCode);
            using HttpResponseMessage disabledApiKeyCreation = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/me/api-keys", UriKind.Relative),
                new CreateApiKeyRequest("disabled-key", "read", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, disabledApiKeyCreation.StatusCode);
            Assert.Contains("api-key-disabled", await disabledApiKeyCreation.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

            using HttpResponseMessage activities = await client.GetAsync(new Uri("/api/v1/audit/activity?actor=OWNER&outcome=success&limit=1", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, activities.StatusCode);
            string activityJson = await activities.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("sourceAddress", activityJson, StringComparison.Ordinal);
            Assert.Contains("success", activityJson, StringComparison.Ordinal);

            using HttpResponseMessage security = await client.GetAsync(new Uri("/api/v1/audit/security?action=settings.changed", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, security.StatusCode);
            string securityJson = await security.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("settings.changed", securityJson, StringComparison.Ordinal);
            Assert.Contains("before", securityJson, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-secret-must-not-leak", securityJson, StringComparison.Ordinal);

            using HttpResponseMessage dashboard = await client.GetAsync(new Uri("/api/v1/dashboard", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
            string dashboardJson = await dashboard.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("controlPlaneMemoryBytes", dashboardJson, StringComparison.Ordinal);
            Assert.Contains("trend", dashboardJson, StringComparison.Ordinal);

            using HttpResponseMessage systemInfo = await client.GetAsync(new Uri("/api/v1/settings/system-info?clientTime=2026-08-03T12:00:00Z", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, systemInfo.StatusCode);
            string systemJson = await systemInfo.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("expectedAgentProtocol", systemJson, StringComparison.Ordinal);
            Assert.Contains("serviceAccount", systemJson, StringComparison.Ordinal);
            Assert.Contains("deniedRequests", systemJson, StringComparison.Ordinal);

            const string diagnosticSecret = "diagnostic-plain-secret";
            ILogger diagnosticLogger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Servitium.Tests.Diagnostics");
            diagnosticLogger.Log(
                LogLevel.Error,
                new EventId(9999),
                "Synthetic password=" + diagnosticSecret + " token=synthetic-token-value",
                null,
                static (state, _) => state);
            using HttpResponseMessage diagnostics = await client.PostAsync(new Uri("/api/v1/settings/diagnostics", UriKind.Relative), null).ConfigureAwait(true);
            string diagnosticsFailure = diagnostics.StatusCode == HttpStatusCode.OK
                ? string.Empty
                : "Diagnostics returned " + diagnostics.StatusCode + ": " + await diagnostics.Content.ReadAsStringAsync().ConfigureAwait(true) +
                    " Logs: " + string.Join('\n', Directory.Exists(Path.Combine(dataRoot, "logs"))
                        ? Directory.EnumerateFiles(Path.Combine(dataRoot, "logs")).Select(ReadSharedFile)
                        : []);
            Assert.True(
                diagnostics.StatusCode == HttpStatusCode.OK,
                diagnosticsFailure);
            Assert.Equal("application/zip", diagnostics.Content.Headers.ContentType?.MediaType);
            byte[] diagnosticBytes = await diagnostics.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            using MemoryStream diagnosticStream = new(diagnosticBytes);
            using ZipArchive diagnosticArchive = new(diagnosticStream, ZipArchiveMode.Read);
            Assert.Equal(
                ["errors.log", "nodes.json", "operations.json", "settings.json", "system.json"],
                diagnosticArchive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
            string diagnosticText = string.Join('\n', diagnosticArchive.Entries.Select(entry =>
            {
                using StreamReader reader = new(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }));
            Assert.DoesNotContain(diagnosticSecret, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-token-value", diagnosticText, StringComparison.Ordinal);
            Assert.Contains("••••••", diagnosticText, StringComparison.Ordinal);

            using HttpResponseMessage legacy = await client.GetAsync(new Uri("/api/v1/activity", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);

            IActivityLogger logger = factory.Services.GetRequiredService<IActivityLogger>();
            await logger.AppendAsync(
                new ActivityLogEntry(
                    Guid.NewGuid(),
                    null,
                    "=cmd|' /C calc'!A0",
                    "quoted\"action\r\nline",
                    "synthetic",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>(),
                    "success",
                    "127.0.0.1"),
                CancellationToken.None).ConfigureAwait(true);
            for (int index = 0; index < 1001; index++)
            {
                await logger.AppendAsync(
                    new ActivityLogEntry(
                        Guid.NewGuid(),
                        null,
                        "bulk-actor",
                        "bulk-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "synthetic",
                        DateTimeOffset.UtcNow,
                        new Dictionary<string, string>(),
                        "success",
                        "127.0.0.1"),
                    CancellationToken.None).ConfigureAwait(true);
            }

            using HttpResponseMessage export = await client.GetAsync(new Uri("/api/v1/audit/activity/export", UriKind.Relative)).ConfigureAwait(true);
            byte[] csvBytes = await export.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes[..3]);
            string csv = Encoding.UTF8.GetString(csvBytes.AsSpan(3));
            Assert.StartsWith("id,actor", csv, StringComparison.Ordinal);
            Assert.Contains("\"'=cmd|' /C calc'!A0\"", csv, StringComparison.Ordinal);
            Assert.Contains("\"quoted\"\"action\r\nline\"", csv, StringComparison.Ordinal);
            Assert.Contains("\"bulk-0\"", csv, StringComparison.Ordinal);

            using HttpResponseMessage filteredExport = await client.GetAsync(
                new Uri("/api/v1/audit/activity/export?actor=bulk-actor&action=bulk-1000", UriKind.Relative)).ConfigureAwait(true);
            string filteredCsv = await filteredExport.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("\"bulk-1000\"", filteredCsv, StringComparison.Ordinal);
            Assert.DoesNotContain("\"bulk-999\"", filteredCsv, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static MultipartFormDataContent CreateAsset(HttpContent file, string mediaType, string fileName)
    {
        MultipartFormDataContent form = [];
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
        string cookie = state.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
        return cookie["servitium.csrf=".Length..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    private static string ReadSharedFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
