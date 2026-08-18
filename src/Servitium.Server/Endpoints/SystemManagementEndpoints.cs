using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Common;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Settings;
using Servitium.Domain.Common;
using Servitium.Domain.Auditing;
using Servitium.Domain.Identity;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Logging;
using Servitium.Server.Hosting;
using Servitium.Server.AgentGateway;
using Servitium.Server.Maintenance;
using Servitium.Server.Governance;
using Servitium.Server.Operations;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class SystemManagementEndpoints
{
    private const string SecretPlaceholder = "••••••";
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static void MapSystemManagementEndpoints(this WebApplication app)
    {
        RouteGroupBuilder operations = app.MapGroup("/api/v1/operations");
        _ = operations.MapGet("/", ListOperationsAsync).ProducesJson<CursorPage<OperationDetailsDto>>();
        _ = operations.MapGet("/{operationId:guid}", GetOperationAsync).ProducesJson<OperationDetailsDto>();
        _ = operations.MapPost("/{operationId:guid}/cancel", CancelOperationAsync).ProducesEmpty();

        RouteGroupBuilder settings = app.MapGroup("/api/v1/settings")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageUsers, ResourceKind.Global));
        _ = settings.MapGet("/", ListSettingsAsync).ProducesJson<SettingsDto>();
        _ = settings.MapPatch("/{group}", UpdateSettingsAsync).ProducesJson<SettingsGroupDto>();
        _ = settings.MapPost("/assets", UploadAssetAsync).AcceptsMultipart<SingleFileUploadRequest>().ProducesJson<SettingsAssetDto>(StatusCodes.Status201Created);
        _ = settings.MapDelete("/assets/{kind}", DeleteAssetAsync).ProducesEmpty();
        _ = settings.MapPost("/identity/test", TestOidcAsync).ProducesJson<SettingsTestResultDto>();
        _ = settings.MapPost("/tls/test", TestTls).ProducesJson<SettingsTestResultDto>();
        _ = settings.MapGet("/system-info", GetSystemInfo).ProducesJson<SystemInfoDto>();
        _ = settings.MapPost("/diagnostics", CreateDiagnosticsAsync).ProducesFile("application/zip");
        _ = settings.MapPost("/database/backup", BackupDatabaseAsync)
            .ProducesJson<DatabaseBackupDto>(StatusCodes.Status201Created)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ModifyOwner, ResourceKind.Global));

        RouteGroupBuilder bans = app.MapGroup("/api/v1/ip-bans")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageUsers, ResourceKind.Global));
        _ = bans.MapGet("/", ListIpBansAsync).ProducesJson<CursorPage<IpBanDto>>();
        _ = bans.MapDelete("/{address}", RemoveIpBanAsync).ProducesEmpty();

        RouteGroupBuilder layouts = app.MapGroup("/api/v1/layouts");
        _ = layouts.MapGet("/", ListLayoutsAsync).ProducesJson<CursorPage<DashboardLayoutDto>>();
        _ = layouts.MapGet("/{page}", GetLayoutAsync).ProducesJson<DashboardLayoutDto>();
        _ = layouts.MapPut("/{page}", SaveLayoutAsync).ProducesJson<DashboardLayoutDto>();
        _ = layouts.MapDelete("/{page}", ResetLayoutAsync).ProducesJson<DashboardLayoutDto>();
    }

    private static async Task<IResult> ListLayoutsAsync(
        HttpContext context,
        string? cursor,
        int? limit,
        DashboardLayoutService layouts,
        CancellationToken cancellationToken)
    {
        Guid userId = RequestAuthentication.Get(context)!.User.Id;
        IReadOnlyList<DashboardLayoutDto> items = await layouts.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(items, item => item.Page.ToUpperInvariant(), item => item.Page, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> GetLayoutAsync(
        HttpContext context,
        string page,
        DashboardLayoutService layouts,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await layouts.GetAsync(RequestAuthentication.Get(context)!.User.Id, page, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException exception)
        {
            return Problem(context, StatusCodes.Status404NotFound, exception.Code);
        }
    }

    private static async Task<IResult> SaveLayoutAsync(
        HttpContext context,
        string page,
        UpdateDashboardLayoutRequest request,
        DashboardLayoutService layouts,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await layouts.SaveAsync(RequestAuthentication.Get(context)!.User.Id, page, request, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException exception)
        {
            return Problem(context, exception.Code == ErrorCodes.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, exception.Code);
        }
    }

    private static async Task<IResult> ResetLayoutAsync(
        HttpContext context,
        string page,
        DashboardLayoutService layouts,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await layouts.ResetAsync(RequestAuthentication.Get(context)!.User.Id, page, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException exception)
        {
            return Problem(context, StatusCodes.Status404NotFound, exception.Code);
        }
    }

    private static async Task<IResult> ListOperationsAsync(
        HttpContext context,
        string? status,
        string? kind,
        string? target,
        Guid? initiatedBy,
        string? cursor,
        int? limit,
        ControlPlaneStore store,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        Guid? effectiveInitiator = authentication.User.Role == Role.User ? authentication.User.Id : initiatedBy;
        IReadOnlyList<OperationDetailsDto> operations = await store.ListOperationsAsync(status, kind, target, effectiveInitiator, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(
            operations,
            operation => (DateTimeOffset.MaxValue - operation.Operation.CreatedAt).Ticks.ToString("D19", CultureInfo.InvariantCulture),
            operation => operation.Operation.Id,
            cursor,
            limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> GetOperationAsync(
        HttpContext context,
        Guid operationId,
        ControlPlaneStore store,
        CancellationToken cancellationToken)
    {
        OperationDetailsDto? operation = await store.FindOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is not null && !CanAccessOperation(RequestAuthentication.Get(context)!, operation))
        {
            operation = null;
        }

        return operation is null ? Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound) : Results.Ok(operation);
    }

    private static async Task<IResult> CancelOperationAsync(
        HttpContext context,
        Guid operationId,
        ControlPlaneStore store,
        ServerOperationExecutor executor,
        CancellationToken cancellationToken)
    {
        OperationDetailsDto? operation = await store.FindOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return operation is null || !CanAccessOperation(RequestAuthentication.Get(context)!, operation)
            ? Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound)
            : await executor.CancelAsync(operationId, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status409Conflict, ErrorCodes.OperationNotCancellable);
    }

    private static bool CanAccessOperation(AuthenticationResult authentication, OperationDetailsDto operation)
    {
        return authentication.User.Role is Role.Owner or Role.Administrator || operation.InitiatedBy == authentication.User.Id;
    }

    private static async Task<IResult> ListSettingsAsync(ControlPlaneStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<SettingsGroupDto> stored = await store.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
        string[] groups = ["general", "appearance", "security", "identity"];
        SettingsGroupDto[] result = [.. groups.Select(group =>
        {
            SettingsGroupDto? item = stored.SingleOrDefault(candidate => candidate.Group == group);
            Dictionary<string, string?> values = SettingDefaults(group);
            if (item is not null)
            {
                foreach ((string key, string? value) in item.Values)
                {
                    values[key] = value;
                }
            }

            return new SettingsGroupDto(group, Redact(values), item?.Revision ?? 0, group is "security" or "identity", SettingEffects(group));
        })];
        return Results.Ok(new SettingsDto(result));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        HttpContext context,
        string group,
        UpdateSettingsGroupRequest request,
        ControlPlaneStore store,
        ISecretProtector protector,
        RuntimeSettings runtimeSettings,
        ServerHostOptions hostOptions,
        INodeControlStore nodeStore,
        IAgentGateway agentGateway,
        AgentConnectionManager connections,
        IAuditLogger audit,
        IGuidFactory guids,
        IClock clock,
        CancellationToken cancellationToken)
    {
        try
        {
            SettingsGroupDto? before = (await store.ListSettingsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Group == group);
            Dictionary<string, string?> merged = SettingDefaults(group);
            if (before is not null)
            {
                foreach ((string key, string? value) in before.Values)
                {
                    merged[key] = value;
                }
            }

            foreach ((string key, string? value) in request.Values)
            {
                merged[key] = value;
            }

            ValidateSettings(group, merged);
            if (group == "security")
            {
                ValidateTlsSettings(merged, before?.Values, protector, clock.UtcNow);
            }

            IReadOnlyDictionary<string, string?> protectedValues = ProtectSecrets(merged, before?.Values, protector);
            SettingsGroupDto updated = await store.UpdateSettingsAsync(
                group,
                protectedValues,
                request.ExpectedRevision,
                group is "security" or "identity",
                cancellationToken).ConfigureAwait(false);
            runtimeSettings.Apply(group, updated.Values);
            if (group == "security")
            {
                hostOptions.PublicBaseUri = ParsePublicBaseUri(Value(updated.Values, "publicBaseUrl"));
                await ApplyAgentSecuritySettingsAsync(runtimeSettings, nodeStore, agentGateway, connections, cancellationToken).ConfigureAwait(false);
            }
            AuthenticationResult authentication = RequestAuthentication.Get(context)!;
            await audit.AppendAsync(
                new AuditLogEntry(
                    guids.Create(),
                    authentication.User.Id,
                    authentication.User.UserName,
                    AuditCategory.SecuritySetting,
                    "settings.changed",
                    "settings:" + group,
                    clock.UtcNow,
                    "success",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["before"] = JsonSerializer.Serialize(before is null ? [] : Redact(before.Values)),
                        ["after"] = JsonSerializer.Serialize(Redact(updated.Values)),
                    }),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(updated with { Values = Redact(updated.Values), Effects = SettingEffects(group) });
        }
        catch (DomainException exception)
        {
            return Problem(context, exception.Code == ErrorCodes.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, exception.Code);
        }
    }

    private static async Task<IResult> UploadAssetAsync(
        HttpContext context,
        string kind,
        ServerHostOptions options,
        CancellationToken cancellationToken)
    {
        if (kind is not ("logo" or "background" or "icon") || !context.Request.HasFormContentType)
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        IFormFile? file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null || file.Length is <= 0 or > 2 * 1024 * 1024 || file.ContentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/svg+xml"))
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        string extension = file.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".svg",
        };
        string directory = Path.Combine(options.DataRoot, "assets");
        _ = Directory.CreateDirectory(directory);
        string destinationPath = Path.Combine(directory, kind + extension);
        string temporary = Path.Combine(directory, "." + kind + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (destination.ConfigureAwait(false))
            {
                if (extension == ".svg")
                {
                    Stream source = file.OpenReadStream();
                    await using (source.ConfigureAwait(false))
                    {
                        byte[] sanitized = await SvgSanitizer.SanitizeAsync(source, cancellationToken).ConfigureAwait(false);
                        await destination.WriteAsync(sanitized, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await file.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (extension != ".svg")
            {
                await ImageAssetValidator.ValidateAsync(temporary, extension, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destinationPath, overwrite: true);
            foreach (string existing in Directory.EnumerateFiles(directory, kind + ".*", SearchOption.TopDirectoryOnly).Where(path => !string.Equals(path, destinationPath, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(existing);
            }
        }
        catch (DomainException exception)
        {
            return Problem(context, StatusCodes.Status400BadRequest, exception.Code);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return Results.Created("/api/v1/settings/assets/" + kind, new { kind, fileName = Path.GetFileName(destinationPath) });
    }

    private static IResult DeleteAsset(HttpContext context, string kind, ServerHostOptions options)
    {
        if (kind is not ("logo" or "background" or "icon"))
        {
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        string directory = Path.Combine(options.DataRoot, "assets");
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, kind + ".*", SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
            }
        }

        return Results.NoContent();
    }

    private static Task<IResult> DeleteAssetAsync(HttpContext context, string kind, ServerHostOptions options)
    {
        return Task.FromResult(DeleteAsset(context, kind, options));
    }

    private static async Task<IResult> TestOidcAsync(
        TestOidcDiscoveryRequest request,
        IOutboundHttpClient http,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(request.Authority, UriKind.Absolute, out Uri? issuer) ||
                issuer.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(issuer.UserInfo) ||
                !string.IsNullOrEmpty(issuer.Query) ||
                !string.IsNullOrEmpty(issuer.Fragment))
            {
                return Results.Ok(new SettingsTestResultDto(false, ErrorCodes.OidcDiscoveryFailed, "discovery-invalid"));
            }

            Uri normalizedIssuer = new(issuer.AbsoluteUri.TrimEnd('/'), UriKind.Absolute);
            Uri discoveryUri = new(normalizedIssuer.AbsoluteUri + "/.well-known/openid-configuration", UriKind.Absolute);
            OutboundHttpResponse response = await http.SendAsync(new(discoveryUri, "GET", MaximumResponseBytes: 1024 * 1024), cancellationToken).ConfigureAwait(false);
            using (response.Content)
            {
                if (response.StatusCode is < 200 or >= 300)
                {
                    return Results.Ok(new SettingsTestResultDto(false, ErrorCodes.OidcDiscoveryFailed, "discovery-unavailable"));
                }

                using JsonDocument document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                bool valid = TryGetHttpsUri(document.RootElement, "issuer", out Uri? discoveredIssuer) &&
                    Uri.Compare(discoveredIssuer, normalizedIssuer, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.Ordinal) == 0 &&
                    TryGetHttpsUri(document.RootElement, "authorization_endpoint", out _) &&
                    TryGetHttpsUri(document.RootElement, "token_endpoint", out _) &&
                    TryGetHttpsUri(document.RootElement, "jwks_uri", out _);
                return Results.Ok(new SettingsTestResultDto(valid, valid ? "ok" : ErrorCodes.OidcDiscoveryFailed, valid ? "discovery-valid" : "discovery-invalid"));
            }
        }
        catch (Exception exception) when (exception is DomainException or HttpRequestException or JsonException or UriFormatException)
        {
            return Results.Ok(new SettingsTestResultDto(false, ErrorCodes.OidcDiscoveryFailed, "discovery-unavailable"));
        }
    }

    private static bool TryGetHttpsUri(JsonElement root, string property, out Uri? uri)
    {
        uri = null;
        return root.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(value.GetString(), UriKind.Absolute, out uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment);
    }

    private static IResult TestTls(TestTlsCertificateRequest request, IClock clock)
    {
        try
        {
            if (!Path.IsPathFullyQualified(request.CertificatePath) || !File.Exists(request.CertificatePath))
            {
                return Results.Ok(new SettingsTestResultDto(false, ErrorCodes.NotFound, "certificate-not-found"));
            }

            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                request.CertificatePath,
                request.Password,
                X509KeyStorageFlags.EphemeralKeySet);
            using (certificate)
            {
                TlsCertificateValidationResult result = TlsCertificateValidator.Validate(certificate, clock.UtcNow);
                return Results.Ok(new SettingsTestResultDto(
                    result.Succeeded,
                    result.Succeeded ? "ok" : ErrorCodes.ValidationFailed,
                    result.Summary));
            }
        }
        catch (CryptographicException)
        {
            return Results.Ok(new SettingsTestResultDto(false, ErrorCodes.ValidationFailed, "certificate-invalid"));
        }
    }

    private static async Task<IResult> GetSystemInfo(
        DateTimeOffset? clientTime,
        ServerHostOptions options,
        DatabaseLocation database,
        ControlPlaneStore store,
        RequestTelemetry telemetry,
        ControlPlaneProcessMetrics processMetrics,
        IClock clock,
        CancellationToken cancellationToken)
    {
        long databaseSize = File.Exists(database.Path) ? new FileInfo(database.Path).Length : 0;
        (double cpu, long memory) = processMetrics.Sample();
        int bans = (await store.ListIpBansAsync(cancellationToken).ConfigureAwait(false)).Count;
        System.Reflection.AssemblyName assembly = typeof(SystemManagementEndpoints).Assembly.GetName();
        return Results.Ok(new SystemInfoDto(
            assembly.Version?.ToString() ?? "0.0.0",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            options.DataRoot,
            databaseSize,
            RuntimeInformation.FrameworkDescription,
            assembly.Version?.ToString() ?? "development",
            "1.0",
            Environment.UserDomainName + "\\" + Environment.UserName,
            clock.UtcNow,
            clientTime,
            bans,
            telemetry.DeniedCount,
            memory,
            cpu,
            Environment.MachineName));
    }

    private static async Task<IResult> CreateDiagnosticsAsync(
        ServerHostOptions options,
        DatabaseLocation database,
        ControlPlaneStore store,
        INodeControlStore nodes,
        IClock clock,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(options.DataRoot, "exports");
        _ = Directory.CreateDirectory(directory);
        DateTimeOffset generatedAt = clock.UtcNow;
        string fileName = "diagnostics-" + generatedAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + ".zip";
        string path = Path.Combine(directory, fileName);
        IReadOnlyList<SettingsGroupDto> settings = await store.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredNode> nodeRows = await nodes.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OperationDetailsDto> operations = await store.ListOperationsAsync(null, null, null, null, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> errors = await ReadRecentErrorsAsync(options, cancellationToken).ConfigureAwait(false);
        FileStream output = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await using (output.ConfigureAwait(false))
        {
            using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);
            await AddJsonEntryAsync(archive, "system.json", new
            {
                generatedAt,
                version = typeof(SystemManagementEndpoints).Assembly.GetName().Version?.ToString(),
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                serviceAccount = Environment.UserDomainName + "\\" + Environment.UserName,
                hostName = Environment.MachineName,
                dataRoot = options.DataRoot,
                databaseSizeBytes = File.Exists(database.Path) ? new FileInfo(database.Path).Length : 0,
            }, cancellationToken).ConfigureAwait(false);
            await AddJsonEntryAsync(
                archive,
                "settings.json",
                settings.Select(group => group with { Values = Redact(group.Values) }).ToArray(),
                cancellationToken).ConfigureAwait(false);
            await AddJsonEntryAsync(
                archive,
                "nodes.json",
                nodeRows.Select(node => new
                {
                    node.Id,
                    node.Name,
                    node.Status,
                    node.AgentVersion,
                    protocol = node.ProtocolMajor.ToString(CultureInfo.InvariantCulture) + "." + node.ProtocolMinor.ToString(CultureInfo.InvariantCulture),
                    node.OperatingSystem,
                    node.Architecture,
                    node.CreatedAt,
                    node.LastSeenAt,
                    node.CredentialRevoked,
                    node.Configuration,
                }).ToArray(),
                cancellationToken).ConfigureAwait(false);
            await AddJsonEntryAsync(archive, "operations.json", operations.Take(500).ToArray(), cancellationToken).ConfigureAwait(false);
            await AddTextEntryAsync(archive, "errors.log", string.Join('\n', errors), cancellationToken).ConfigureAwait(false);
        }

        return Results.File(path, "application/zip", fileName, generatedAt, enableRangeProcessing: true);
    }

    private static async Task AddJsonEntryAsync<T>(ZipArchive archive, string name, T value, CancellationToken cancellationToken)
    {
        string json = SecretRedactor.Redact(JsonSerializer.Serialize(value, DiagnosticJsonOptions));
        await AddTextEntryAsync(archive, name, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTextEntryAsync(ZipArchive archive, string name, string value, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        Stream content = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(SecretRedactor.Redact(value));
            await content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadRecentErrorsAsync(ServerHostOptions options, CancellationToken cancellationToken)
    {
        Queue<string> recent = new(500);
        string directory = Path.Combine(options.DataRoot, "logs");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        foreach (string path in Directory.EnumerateFiles(directory, "server-*.log", SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.Ordinal))
        {
            FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                using StreamReader reader = new(input, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (!IsErrorLogLine(line))
                    {
                        continue;
                    }

                    if (recent.Count == 500)
                    {
                        _ = recent.Dequeue();
                    }

                    recent.Enqueue(SecretRedactor.Redact(line));
                }
            }
        }

        return [.. recent];
    }

    private static bool IsErrorLogLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            string? level = document.RootElement.TryGetProperty("level", out JsonElement value) ? value.GetString() : null;
            return level is "Error" or "Critical";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<IResult> BackupDatabaseAsync(DatabaseBackupService backups, CancellationToken cancellationToken)
    {
        return Results.Created(
            "/api/v1/settings/database/backup",
            await backups.CreateManualAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ListIpBansAsync(string? cursor, int? limit, ControlPlaneStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<IpBanDto> items = await store.ListIpBansAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(items, item => item.Address.ToUpperInvariant(), item => item.Address, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> RemoveIpBanAsync(string address, ControlPlaneStore store, LoginThrottle throttle, CancellationToken cancellationToken)
    {
        await store.RemoveIpBanAsync(address, cancellationToken).ConfigureAwait(false);
        throttle.ClearAddress(address);
        return Results.NoContent();
    }

    private static void ValidateSettings(string group, IReadOnlyDictionary<string, string?> values)
    {
        if (group is not ("general" or "appearance" or "security" or "identity"))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The settings group is invalid.");
        }

        if (group == "general")
        {
            string? language = Value(values, "defaultLanguage");
            if (language is not null and not ("zh-Hans" or "en-US"))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The default language is invalid.");
            }

            ValidateRange(values, "activityRetentionDays", 30, 3650);
            ValidateRange(values, "auditRetentionDays", 90, 3650);
            ValidateRange(values, "operationRetentionDays", 7, 365);
            ValidateRange(values, "metricRetentionHours", 1, 168);
            ValidateRange(values, "serviceLogRetentionDays", 7, 365);
            ValidateRange(values, "databaseBackupRetentionCount", 1, 365);
            ValidateBoolean(values, "databaseBackupEnabled");
            ValidateBoolean(values, "publicPageEnabled");
            string? backupEnabledValue = Value(values, "databaseBackupEnabled");
            if (backupEnabledValue is not null && !bool.TryParse(backupEnabledValue, out _))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The database backup switch is invalid.");
            }
        }

        if (group == "security")
        {
            ValidateBoolean(values, "responseCompressionEnabled");
            ValidateBoolean(values, "trustedProxyMode");
            ValidateBoolean(values, "sessionIpValidationEnabled");
            ValidateBoolean(values, "userFileManagementEnabled");
            ValidateBoolean(values, "apiKeysEnabled");
            ValidateBoolean(values, "advancedRawCommandsEnabled");
            ValidateRange(values, "totpDriftSteps", 0, 4);
            ValidateRange(values, "userFailureLimit", 1, 20);
            ValidateRange(values, "ipFailureLimit", 1, 200);
            ValidateRange(values, "ipFailureWindowMinutes", 1, 1440);
            ValidateRange(values, "ipBanMinutes", 1, 10080);
            ValidateRange(values, "archiveConcurrencyLimit", 1, 16);
            ValidateRange(values, "downloadConcurrencyLimit", 1, 64);
            ValidateRange(values, "uploadConcurrencyLimit", 1, 32);
            ValidateRange(values, "sessionIdleHours", 1, 168);
            ValidateRange(values, "sessionAbsoluteHours", 2, 720);
            string? addressValue = Value(values, "listenAddress");
            if (addressValue is not null)
            {
                if (!IPAddress.TryParse(addressValue, out IPAddress? address))
                {
                    throw new DomainException(ErrorCodes.ValidationFailed, "The listen address is invalid.");
                }

                bool trustedProxy = bool.TryParse(Value(values, "trustedProxyMode"), out bool proxy) && proxy;
                bool tlsConfigured = !string.IsNullOrWhiteSpace(Value(values, "tlsCertificatePath")) || !string.IsNullOrWhiteSpace(Value(values, "tlsCertificateThumbprint"));
                if (!IPAddress.IsLoopback(address) && !trustedProxy && !tlsConfigured)
                {
                    throw new DomainException(ErrorCodes.TlsRequired, "Non-loopback exposure requires TLS or trusted proxy mode.");
                }
            }

            if (int.TryParse(Value(values, "listenPort"), out int port) && port is < 1 or > 65535)
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The listen port is invalid.");
            }

            if (int.TryParse(Value(values, "sessionIdleHours"), out int idleHours) &&
                int.TryParse(Value(values, "sessionAbsoluteHours"), out int absoluteHours) &&
                absoluteHours <= idleHours)
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The absolute session lifetime must exceed the idle lifetime.");
            }

            ValidatePathBase(Value(values, "pathBase"));
            ValidatePublicBaseUrl(Value(values, "publicBaseUrl"));
            ValidateOrigins(Value(values, "corsOrigins"));
            ValidateOrigins(Value(values, "embedAllowedOrigins"));
            ValidateAuthorities(Value(values, "privateSourceAllowList"));
            string source = Value(values, "tlsCertificateSource") ?? "none";
            if (source is not ("none" or "file" or "store"))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The TLS certificate source is invalid.");
            }

            if (bool.TryParse(Value(values, "trustedProxyMode"), out bool trustedProxyMode) && trustedProxyMode)
            {
                string[] networks = SplitSetting(Value(values, "trustedProxyNetworks"));
                if (networks.Length == 0 || networks.Any(value => !IPNetwork.TryParse(value, out _)) || !IsHttpHeaderName(Value(values, "forwardedForHeader")))
                {
                    throw new DomainException(ErrorCodes.ValidationFailed, "Trusted proxy mode requires valid networks and a header name.");
                }
            }
        }

        if (group == "appearance" && Value(values, "sidebarPosition") is { } sidebar && sidebar is not ("left" or "top"))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The sidebar position is invalid.");
        }

        if (group == "identity")
        {
            ValidateBoolean(values, "oidcEnabled");
            ValidateBoolean(values, "oidcAutomaticRedirect");
            if (bool.TryParse(Value(values, "oidcEnabled"), out bool enabled) && enabled &&
                (!Uri.TryCreate(Value(values, "oidcIssuer"), UriKind.Absolute, out Uri? issuer) || issuer.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(Value(values, "oidcClientId"))))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The OIDC settings are incomplete.");
            }
        }
    }

    private static void ValidateRange(IReadOnlyDictionary<string, string?> values, string key, int minimum, int maximum)
    {
        string? value = Value(values, key);
        if (value is not null && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A retention setting is outside its allowed range.");
        }
    }

    private static void ValidateBoolean(IReadOnlyDictionary<string, string?> values, string key)
    {
        string? value = Value(values, key);
        if (value is not null && !bool.TryParse(value, out _))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A boolean setting is invalid.");
        }
    }

    private static void ValidateTlsSettings(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? existing,
        ISecretProtector protector,
        DateTimeOffset now)
    {
        string source = Value(values, "tlsCertificateSource") ?? "none";
        bool trustedProxy = bool.TryParse(Value(values, "trustedProxyMode"), out bool proxy) && proxy;
        bool nonLoopback = IPAddress.TryParse(Value(values, "listenAddress"), out IPAddress? address) && !IPAddress.IsLoopback(address);
        if (source == "none")
        {
            if (nonLoopback && !trustedProxy)
            {
                throw new DomainException(ErrorCodes.TlsRequired, "Non-loopback exposure requires a trusted TLS certificate or trusted proxy mode.");
            }

            return;
        }

        string? password = ReadPlainSecret(values, existing, "tlsCertificatePassword", protector);
        try
        {
            using X509Certificate2 certificate = TlsCertificateValidator.LoadConfigured(
                source,
                Value(values, "tlsCertificatePath"),
                password,
                Value(values, "tlsCertificateThumbprint"));
            TlsCertificateValidationResult validation = TlsCertificateValidator.Validate(certificate, now);
            if (!validation.Succeeded)
            {
                throw new DomainException(ErrorCodes.CertificateInvalid, "The TLS certificate is invalid or untrusted.");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new DomainException(ErrorCodes.CertificateInvalid, "The TLS certificate could not be loaded or validated.");
        }
    }

    private static string? ReadPlainSecret(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? existing,
        string key,
        ISecretProtector protector)
    {
        string? value = Value(values, key);
        if (value == SecretPlaceholder)
        {
            value = existing?.GetValueOrDefault(key);
        }

        if (string.IsNullOrEmpty(value) || !value.StartsWith("protected:", StringComparison.Ordinal))
        {
            return value;
        }

        byte[] protectedBytes = Convert.FromBase64String(value["protected:".Length..]);
        byte[] plaintext = protector.Unprotect(protectedBytes);
        try
        {
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidatePathBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "/")
        {
            return;
        }

        if (!value.StartsWith('/') || value.EndsWith('/') || value.Contains("..", StringComparison.Ordinal) || value.Contains('%', StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The path base is invalid.");
        }
    }

    private static void ValidatePublicBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The public base URL is invalid.");
        }
    }

    private static Uri? ParsePublicBaseUri(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : new Uri(new Uri(value, UriKind.Absolute).AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void ValidateOrigins(string? value)
    {
        foreach (string candidate in SplitSetting(value))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is not ("http" or "https") ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                candidate.Contains('*', StringComparison.Ordinal))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "An origin setting is invalid.");
            }
        }
    }

    private static void ValidateAuthorities(string? value)
    {
        foreach (string authority in SplitSetting(value))
        {
            if (!Uri.TryCreate("https://" + authority, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "A private source authority is invalid.");
            }
        }
    }

    private static string[] SplitSetting(string? value)
    {
        return value?.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    private static bool IsHttpHeaderName(string? value)
    {
        const string separators = "()<>@,;:\\\"/[]?={} \t";
        return !string.IsNullOrWhiteSpace(value) && value.All(character => character is > (char)32 and < (char)127 && !separators.Contains(character, StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> ProtectSecrets(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? existing,
        ISecretProtector protector)
    {
        Dictionary<string, string?> result = new(values, StringComparer.Ordinal);
        foreach (string key in result.Keys.Where(IsSecretKey).ToArray())
        {
            string? value = result[key];
            if (value == SecretPlaceholder)
            {
                result[key] = existing?.GetValueOrDefault(key);
                continue;
            }

            if (!string.IsNullOrEmpty(value) && !value.StartsWith("protected:", StringComparison.Ordinal))
            {
                byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(value);
                try
                {
                    result[key] = "protected:" + Convert.ToBase64String(protector.Protect(plaintext));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        return result;
    }

    private static async Task ApplyAgentSecuritySettingsAsync(
        RuntimeSettings runtimeSettings,
        INodeControlStore nodeStore,
        IAgentGateway agentGateway,
        AgentConnectionManager connections,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNode> nodes = await nodeStore.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        foreach (StoredNode node in nodes.Where(item => item.Configuration is not null))
        {
            UpdateNodeRequest configuration = node.Configuration! with
            {
                AdvancedRawCommandsEnabled = runtimeSettings.AdvancedRawCommandsEnabled,
                PrivateSourceAuthorities = runtimeSettings.PrivateSourceAuthorities,
            };
            await nodeStore.UpdateNodeAsync(node with { Configuration = configuration }, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(node.Status, "Online", StringComparison.Ordinal))
            {
                continue;
            }

            AgentCommandResult result = await agentGateway.SendAsync(
                node.Id,
                new AgentCommand("UpdateNodeConfig", JsonSerializer.SerializeToUtf8Bytes(new AgentUpdateNodeConfigurationCommand(configuration))),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // 无法确认危险开关已降级时主动断开，促使 Agent 重连并获取权威镜像。
                _ = connections.Disconnect(node.Id);
            }
        }
    }

    private static Dictionary<string, string?> Redact(IReadOnlyDictionary<string, string?> values)
    {
        return values.ToDictionary(pair => pair.Key, pair => IsSecretKey(pair.Key) && !string.IsNullOrEmpty(pair.Value) ? SecretPlaceholder : pair.Value, StringComparer.Ordinal);
    }

    private static bool IsSecretKey(string key)
    {
        return key.EndsWith("Password", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Secret", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Token", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    private static Dictionary<string, string> SettingEffects(string group)
    {
        HashSet<string> restartRequired = group == "security"
            ? ["listenAddress", "listenPort", "pathBase", "corsOrigins", "responseCompressionEnabled", "trustedProxyMode", "trustedProxyNetworks", "forwardedForHeader", "tlsCertificateSource", "tlsCertificatePath", "tlsCertificatePassword", "tlsCertificateThumbprint"]
            : [];
        return SettingDefaults(group).Keys.ToDictionary(
            key => key,
            key => restartRequired.Contains(key) ? "restart-required" : "immediate",
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string?> SettingDefaults(string group)
    {
        return group switch
        {
            "general" => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["defaultLanguage"] = "zh-Hans",
                ["displayTimeZone"] = "local",
                ["activityRetentionDays"] = "180",
                ["auditRetentionDays"] = "365",
                ["operationRetentionDays"] = "30",
                ["metricRetentionHours"] = "24",
                ["serviceLogRetentionDays"] = "14",
                ["databaseBackupEnabled"] = "true",
                ["databaseBackupRetentionCount"] = "14",
                ["publicPageEnabled"] = "false",
            },
            "appearance" => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["sidebarPosition"] = "left",
                ["pageTitle"] = "Servitium",
                ["loginTitleZhHans"] = null,
                ["loginDescriptionZhHans"] = null,
                ["loginSupportZhHans"] = null,
                ["loginTitleEnUs"] = null,
                ["loginDescriptionEnUs"] = null,
                ["loginSupportEnUs"] = null,
            },
            "security" => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["listenAddress"] = "127.0.0.1",
                ["listenPort"] = "5344",
                ["pathBase"] = null,
                ["publicBaseUrl"] = null,
                ["corsOrigins"] = null,
                ["responseCompressionEnabled"] = "true",
                ["trustedProxyMode"] = "false",
                ["trustedProxyNetworks"] = null,
                ["forwardedForHeader"] = "X-Forwarded-For",
                ["tlsCertificateSource"] = "none",
                ["tlsCertificatePath"] = null,
                ["tlsCertificatePassword"] = null,
                ["tlsCertificateThumbprint"] = null,
                ["totpDriftSteps"] = "1",
                ["userFailureLimit"] = "5",
                ["ipFailureLimit"] = "20",
                ["ipFailureWindowMinutes"] = "10",
                ["ipBanMinutes"] = "30",
                ["sessionIpValidationEnabled"] = "false",
                ["userFileManagementEnabled"] = "true",
                ["apiKeysEnabled"] = "true",
                ["archiveConcurrencyLimit"] = "2",
                ["downloadConcurrencyLimit"] = "8",
                ["uploadConcurrencyLimit"] = "4",
                ["privateSourceAllowList"] = null,
                ["sessionIdleHours"] = "12",
                ["sessionAbsoluteHours"] = "168",
                ["advancedRawCommandsEnabled"] = "false",
                ["embedAllowedOrigins"] = null,
            },
            "identity" => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["oidcEnabled"] = "false",
                ["oidcProviderDisplayName"] = null,
                ["oidcIssuer"] = null,
                ["oidcClientId"] = null,
                ["oidcClientSecret"] = null,
                ["oidcScopes"] = "openid profile email",
                ["oidcAutomaticRedirect"] = "false",
                ["oidcSubjectClaim"] = "sub",
                ["oidcUserNameClaim"] = "preferred_username",
            },
            _ => new Dictionary<string, string?>(StringComparer.Ordinal),
        };
    }

    private static IResult Problem(HttpContext context, int status, string code)
    {
        return ProblemResponses.Create(context, status, code);
    }
}
