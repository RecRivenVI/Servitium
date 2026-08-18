using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Common;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;
using Servitium.Domain.Operations;
using Servitium.Infrastructure.Distribution;
using Servitium.Server.AgentGateway;
using Servitium.Server.Hosting;
using Servitium.Server.Security;
using Servitium.Server.Realtime;
using Servitium.Server.Operations;
using Servitium.Server.OpenApi;

namespace Servitium.Server.Hmcl;

internal static class HmclManagementEndpoints
{
    internal static void MapHmclManagementEndpoints(this WebApplication app)
    {
        RouteGroupBuilder channels = app.MapGroup("/api/v1/hmcl/channels")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageDistribution, ResourceKind.Channel));
        _ = channels.MapGet("/", ListChannelsAsync).ProducesJson<CursorPage<HmclChannelDto>>();
        _ = channels.MapPost("/", CreateChannelAsync).ProducesJson<HmclChannelDto>(StatusCodes.Status201Created);
        _ = channels.MapGet("/{channelId:guid}", GetChannelAsync).ProducesJson<HmclChannelDetailsDto>();
        _ = channels.MapPatch("/{channelId:guid}", UpdateChannelAsync).ProducesJson<HmclChannelDto>();
        _ = channels.MapDelete("/{channelId:guid}", DeleteChannelAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = channels.MapPost("/{channelId:guid}/visibility", ChangeVisibilityAsync).ProducesJson<HmclTokenResultDto>();
        _ = channels.MapPost("/{channelId:guid}/token/rotate", RotateTokenAsync).ProducesJson<HmclTokenResultDto>();
        _ = channels.MapGet("/{channelId:guid}/releases", ListReleasesAsync).ProducesJson<CursorPage<HmclReleaseSummaryDto>>();
        _ = channels.MapPost("/{channelId:guid}/releases", CreateReleaseAsync)
            .AcceptsMultipart<ReleaseArchiveUploadRequest>()
            .AcceptsJsonAlternatives(typeof(CreateHmclReleaseFromArchiveRequest), typeof(CreateHmclReleaseFromInstanceRequest))
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(ArchiveUploadPolicy.RequestSizeLimit);
        _ = channels.MapPost("/{channelId:guid}/preview", PreviewPublishAsync).ProducesJson<HmclPublishDiffDto>();
        _ = channels.MapPost("/{channelId:guid}/publish", PublishAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = channels.MapGet("/{channelId:guid}/stats", ListStatsAsync).ProducesJson<CursorPage<HmclAccessStatDto>>();

        RouteGroupBuilder releases = app.MapGroup("/api/v1/hmcl/releases")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageDistribution, ResourceKind.Channel));
        _ = releases.MapGet("/{releaseId:guid}", GetReleaseAsync).ProducesJson<HmclReleaseDto>();
        _ = releases.MapGet("/{releaseId:guid}/files", ListReleaseFilesAsync).ProducesJson<HmclReleaseFilePageDto>();
        _ = releases.MapGet("/{releaseId:guid}/manifest", GetManifestAsync).ProducesFile("application/json");
        _ = releases.MapPost("/{releaseId:guid}/sync", SyncReleaseAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);

        _ = app.MapPost("/api/v1/hmcl/integrity/run", RunIntegrityAsync)
            .ProducesJson<HmclIntegrityResultDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageDistribution, ResourceKind.Global));

        RouteGroupBuilder archives = app.MapGroup("/api/v1/hmcl/archives")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageDistribution, ResourceKind.Global));
        _ = archives.MapPost("/", CreateArchiveAsync).ProducesJson<HmclArchiveDto>(StatusCodes.Status201Created);
        _ = archives.MapGet("/{archiveId:guid}", GetArchiveAsync).ProducesJson<HmclArchiveDto>();
        _ = archives.MapPut("/{archiveId:guid}/parts/{index:int}", SaveArchivePartAsync)
            .AcceptsBinary()
            .ProducesJson<HmclArchiveDto>()
            .WithMetadata(ArchiveUploadPolicy.RequestSizeLimit);
        _ = archives.MapPost("/{archiveId:guid}/complete", CompleteArchiveAsync).ProducesJson<HmclArchiveDto>();
        _ = archives.MapDelete("/{archiveId:guid}", DeleteArchiveAsync).ProducesEmpty();

        RouteGroupBuilder prism = app.MapGroup("/api/v1/imports/prism")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageDistribution, ResourceKind.Global));
        _ = prism.MapPost("/preview", PreviewPrismAsync)
            .AcceptsMultipart<PrismArchivePreviewUploadRequest>()
            .ProducesJson<StagedPrismPreviewDto>(StatusCodes.Status201Created)
            .WithMetadata(ArchiveUploadPolicy.RequestSizeLimit);
        _ = prism.MapPost("/{uploadId:guid}/convert", ConvertPrismAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListChannelsAsync(string? cursor, int? limit, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredHmclChannel> channels = await store.ListChannelsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<HmclChannelDto> items = channels.Select(channel => new HmclChannelDto(channel.Id, channel.Slug, channel.Name, channel.Visibility, channel.ActiveReleaseId));
        return Results.Ok(CursorPaginator.Page(items, channel => channel.Name.ToUpperInvariant(), channel => channel.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> CreateChannelAsync(
        HttpContext context,
        CreateHmclChannelRequest request,
        HmclDistributionStore store,
        IGuidFactory guids,
        ITokenGenerator tokens,
        IClock clock,
        ServerHostOptions options,
        CancellationToken cancellationToken)
    {
        string? token = request.Visibility == "token" ? tokens.Generate(32) : null;
        StoredHmclChannel channel = await store.CreateChannelAsync(
            request.Slug,
            request.Name,
            request.Visibility,
            token is null ? null : TokenHash(token),
            guids.Create(),
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return Results.Created(
            "/api/v1/hmcl/channels/" + channel.Id.ToString("D"),
            new HmclChannelDto(
                channel.Id, channel.Slug, channel.Name, channel.Visibility, channel.ActiveReleaseId,
                token, ManifestUri(context.Request, options, channel, token)));
    }

    private static async Task<IResult> GetChannelAsync(HttpContext context, Guid channelId, HmclDistributionStore store, ServerHostOptions options, CancellationToken cancellationToken)
    {
        StoredHmclChannel channel = await store.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new Domain.Common.DomainException("not-found", "The HMCL channel does not exist.");
        return Results.Ok(new HmclChannelDetailsDto(
            ToChannelDto(channel),
            channel.Description,
            channel.RetentionCount,
            channel.LinkedInstanceId,
            ManifestUri(context.Request, options, channel, channel.Visibility == "token" ? "{token}" : null),
            channel.IntegrityStatus));
    }

    private static async Task<IResult> UpdateChannelAsync(Guid channelId, UpdateHmclChannelRequest request, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        return Results.Ok(ToChannelDto(await store.UpdateChannelAsync(channelId, request, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<IResult> DeleteChannelAsync(
        HttpContext context,
        Guid channelId,
        [FromBody] ConfirmHmclChannelRequest request,
        HmclDistributionStore store,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.DeleteHmclChannel,
            channelId.ToString("D"),
            RequireUserId(context),
            async (operationId, operationToken) =>
            {
                await store.DeleteChannelAsync(channelId, request.ConfirmationName, operationToken).ConfigureAwait(false);
                return new ServerOperationResult(true, null, null);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static async Task<IResult> ChangeVisibilityAsync(
        HttpContext context,
        Guid channelId,
        ChangeHmclVisibilityRequest request,
        HmclDistributionStore store,
        ITokenGenerator tokens,
        ServerHostOptions options,
        CancellationToken cancellationToken)
    {
        string? token = request.Visibility == "token" ? tokens.Generate(32) : null;
        StoredHmclChannel channel = await store.ChangeVisibilityAsync(
            channelId, request.Visibility, request.ConfirmationName, token is null ? null : TokenHash(token), cancellationToken).ConfigureAwait(false);
        return token is null
            ? Results.Ok(new HmclTokenResultDto(string.Empty, ManifestUri(context.Request, options, channel, null)))
            : Results.Ok(new HmclTokenResultDto(token, ManifestUri(context.Request, options, channel, token)));
    }

    private static async Task<IResult> RotateTokenAsync(
        HttpContext context,
        Guid channelId,
        ConfirmHmclChannelRequest request,
        HmclDistributionStore store,
        ITokenGenerator tokens,
        ServerHostOptions options,
        CancellationToken cancellationToken)
    {
        StoredHmclChannel channel = await store.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new Domain.Common.DomainException("not-found", "The HMCL channel does not exist.");
        string token = tokens.Generate(32);
        await store.RotateTokenAsync(channelId, request.ConfirmationName, TokenHash(token), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new HmclTokenResultDto(token, ManifestUri(context.Request, options, channel, token)));
    }

    private static async Task<IResult> ListReleasesAsync(Guid channelId, string? cursor, int? limit, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<HmclReleaseSummaryDto> releases = await store.ListReleasesAsync(channelId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(
            releases,
            release => (long.MaxValue - release.Sequence).ToString("D19", CultureInfo.InvariantCulture),
            release => release.Id,
            cursor,
            limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> CreateReleaseAsync(
        HttpContext context,
        Guid channelId,
        HmclDistributionStore store,
        IGuidFactory guids,
        IClock clock,
        ServerHostOptions options,
        IAgentDistributionGateway distribution,
        INodeControlStore nodes,
        HmclArchiveUploadManager archives,
        IRealtimeEventStream events,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        Uri baseUri = RequestBaseUri(context.Request, options);
        Guid userId = RequireUserId(context);
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            if (form.Files.Count != 1 || !TryReadMetadata(form, out CreateHmclReleaseMetadataRequest? formMetadata))
            {
                return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
            }

            if (ArchiveUploadPolicy.ExceedsFileLimit(form.Files[0]))
            {
                return Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge);
            }

            string upload = await SaveTemporaryAsync(form.Files[0], options, "hmcl-archives", cancellationToken).ConfigureAwait(false);
            try
            {
                OperationAcceptedDto formAccepted = await operations.EnqueueAsync(
                    OperationKind.CreateHmclRelease,
                    channelId.ToString("D") + ":archive",
                    userId,
                    async (operationId, operationToken) => await CreateReleaseResultAsync(
                        channelId, upload, HmclDistributionStore.InspectContentArchive(upload), formMetadata!, baseUri, userId,
                        store, guids, clock, events, operationToken).ConfigureAwait(false),
                    null,
                    _ => DeleteFileAsync(upload),
                    cancellationToken).ConfigureAwait(false);
                return Accepted(formAccepted);
            }
            catch
            {
                await DeleteFileAsync(upload).ConfigureAwait(false);
                throw;
            }
        }

        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("archiveId", out _))
        {
            CreateHmclReleaseFromArchiveRequest archiveRequest = document.RootElement.Deserialize<CreateHmclReleaseFromArchiveRequest>(JsonSerializerOptions.Web)
                ?? throw new Domain.Common.DomainException("validation-failed", "The Archive Release request is invalid.");
            string readyArchivePath = await archives.GetReadyPathAsync(archiveRequest.ArchiveId, userId, cancellationToken).ConfigureAwait(false);
            OperationAcceptedDto archiveAccepted = await operations.EnqueueAsync(
                OperationKind.CreateHmclRelease,
                channelId.ToString("D") + ":" + archiveRequest.ArchiveId.ToString("D"),
                userId,
                async (operationId, operationToken) => await CreateReleaseResultAsync(
                    channelId, readyArchivePath, HmclDistributionStore.InspectContentArchive(readyArchivePath), archiveRequest.Metadata,
                    baseUri, userId, store, guids, clock, events, operationToken).ConfigureAwait(false),
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            return Accepted(archiveAccepted);
        }

        CreateHmclReleaseFromInstanceRequest request = document.RootElement.Deserialize<CreateHmclReleaseFromInstanceRequest>(JsonSerializerOptions.Web)
            ?? throw new Domain.Common.DomainException("validation-failed", "The instance collection request is invalid.");
        (Guid nodeId, _) = await FindInstanceAsync(request.InstanceId, nodes, cancellationToken).ConfigureAwait(false);
        string root = Path.Combine(options.DataRoot, "staging", "hmcl-instance-collections");
        _ = Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            OperationAcceptedDto instanceAccepted = await operations.EnqueueAsync(
                OperationKind.CreateHmclRelease,
                channelId.ToString("D") + ":" + request.InstanceId.ToString("D"),
                userId,
                async (operationId, operationToken) =>
                {
                    AgentCollectedDirectoryResult collected;
                    FileStream output = new(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await using (output.ConfigureAwait(false))
                    {
                        collected = await distribution.CollectDirectoryAsync(nodeId, request.InstanceId, output, operationToken).ConfigureAwait(false);
                    }

                    HmclArchivePlan plan = HmclDistributionStore.InspectContentArchive(archivePath);
                    (StoredHmclRelease release, IReadOnlyDictionary<string, string> excluded) = await store.CreateReleaseAsync(
                        channelId, archivePath, plan, request.Metadata, baseUri, userId, guids.Create(), clock.UtcNow, operationToken).ConfigureAwait(false);
                    Dictionary<string, string> allExcluded = new(collected.ExcludedPaths, StringComparer.Ordinal);
                    foreach ((string path, string reason) in excluded)
                    {
                        allExcluded[path] = reason;
                    }

                    _ = await events.PublishAsync(EventTypes.HmclReleaseCreated, "global", new HmclReleaseCreatedPayload(channelId, release.Id), operationToken).ConfigureAwait(false);
                    return new ServerOperationResult(true, JsonSerializer.Serialize(new HmclReleaseCreationResult(ToDto(release), allExcluded)), null);
                },
                null,
                _ => DeleteFileAsync(archivePath),
                cancellationToken).ConfigureAwait(false);
            return Accepted(instanceAccepted);
        }
        catch
        {
            await DeleteFileAsync(archivePath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ServerOperationResult> CreateReleaseResultAsync(
        Guid channelId,
        string archivePath,
        HmclArchivePlan plan,
        CreateHmclReleaseMetadataRequest metadata,
        Uri baseUri,
        Guid userId,
        HmclDistributionStore store,
        IGuidFactory guids,
        IClock clock,
        IRealtimeEventStream events,
        CancellationToken cancellationToken)
    {
        (StoredHmclRelease release, IReadOnlyDictionary<string, string> excluded) = await store.CreateReleaseAsync(
            channelId, archivePath, plan, metadata, baseUri, userId, guids.Create(), clock.UtcNow, cancellationToken).ConfigureAwait(false);
        _ = await events.PublishAsync(EventTypes.HmclReleaseCreated, "global", new HmclReleaseCreatedPayload(channelId, release.Id), cancellationToken).ConfigureAwait(false);
        return new ServerOperationResult(true, JsonSerializer.Serialize(new HmclReleaseCreationResult(ToDto(release), excluded)), null);
    }

    private static Task DeleteFileAsync(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static async Task<IResult> PreviewPrismAsync(
        HttpContext context,
        StagedPrismUploadStore uploads,
        HmclDistributionStore store,
        CancellationToken cancellationToken)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files.Count != 1)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "validation-failed");
        }

        if (ArchiveUploadPolicy.ExceedsFileLimit(form.Files[0]))
        {
            return Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge);
        }

        (Guid id, HmclArchivePlan plan) = await uploads.SaveAndInspectAsync(form.Files[0], cancellationToken).ConfigureAwait(false);
        try
        {
            HmclPublishDiffDto? difference = Guid.TryParse(form["channelId"].ToString(), out Guid channelId)
                ? await store.PreviewArchivePlanAsync(channelId, plan, cancellationToken).ConfigureAwait(false)
                : null;
            return Results.Created(
                "/api/v1/imports/prism/" + id.ToString("D"),
                new StagedPrismPreviewDto(id, plan.PrismPreview!, difference));
        }
        catch
        {
            uploads.Delete(id);
            throw;
        }
    }

    private static async Task<IResult> ConvertPrismAsync(
        HttpContext context,
        Guid uploadId,
        ConvertStagedPrismRequest request,
        StagedPrismUploadStore uploads,
        HmclDistributionStore store,
        IGuidFactory guids,
        IClock clock,
        ServerHostOptions options,
        IRealtimeEventStream events,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        string upload = uploads.GetPath(uploadId);
        HmclArchivePlan plan = HmclDistributionStore.InspectPrismArchive(upload);
        Uri baseUri = RequestBaseUri(context.Request, options);
        Guid userId = RequireUserId(context);
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.ConvertPrismInstance,
            uploadId.ToString("D"),
            userId,
            async (operationId, operationToken) =>
            {
                (StoredHmclRelease release, IReadOnlyDictionary<string, string> excluded) = await store.CreateReleaseAsync(
                    request.ChannelId, upload, plan, request.Metadata, baseUri, userId, guids.Create(), clock.UtcNow, operationToken).ConfigureAwait(false);
                uploads.Delete(uploadId);
                _ = await events.PublishAsync(EventTypes.HmclReleaseCreated, "global", new HmclReleaseCreatedPayload(request.ChannelId, release.Id), operationToken).ConfigureAwait(false);
                return new ServerOperationResult(true, JsonSerializer.Serialize(new HmclReleaseCreationResult(ToDto(release), excluded)), null);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static async Task<IResult> PreviewPublishAsync(Guid channelId, PublishHmclChannelRequest request, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        return Results.Ok(await store.PreviewPublishAsync(channelId, request.ReleaseId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PublishAsync(
        HttpContext context,
        Guid channelId,
        PublishHmclChannelRequest request,
        HmclDistributionStore store,
        IClock clock,
        IRealtimeEventStream events,
        ServerOperationExecutor operations,
        IGuidFactory guids,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        Guid auditId = guids.Create();
        string sourceAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.PublishHmclChannel,
            channelId.ToString("D") + ":" + request.ReleaseId.ToString("D"),
            authentication.User.Id,
            async (operationId, operationToken) =>
            {
                HmclPublishDiffDto preview = await store.PreviewPublishAsync(channelId, request.ReleaseId, operationToken).ConfigureAwait(false);
                if (preview.ValidationIssues is { Count: > 0 })
                {
                    return new ServerOperationResult(false, JsonSerializer.Serialize(preview), ErrorCodes.HmclReleaseValidationFailed);
                }

                HmclPublishDiffDto diff = await PublishCoreAsync(
                    channelId,
                    request.ReleaseId,
                    store,
                    clock,
                    events,
                    auditId,
                    authentication.User.Id,
                    authentication.User.UserName,
                    sourceAddress,
                    operationToken).ConfigureAwait(false);
                return new ServerOperationResult(true, JsonSerializer.Serialize(diff), null);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static async Task<HmclPublishDiffDto> PublishCoreAsync(
        Guid channelId,
        Guid releaseId,
        HmclDistributionStore store,
        IClock clock,
        IRealtimeEventStream events,
        Guid auditId,
        Guid actorId,
        string actorName,
        string sourceAddress,
        CancellationToken cancellationToken)
    {
        HmclPublishDiffDto diff = await store.PublishAsync(
            channelId,
            releaseId,
            clock.UtcNow,
            auditId,
            actorId,
            actorName,
            sourceAddress,
            cancellationToken).ConfigureAwait(false);
        _ = await store.ApplyRetentionAsync(channelId, cancellationToken).ConfigureAwait(false);
        _ = await events.PublishAsync(
            EventTypes.HmclPublished,
            "global",
            new HmclPublishedPayload(channelId, releaseId, diff.Added.Count, diff.Replaced.Count, diff.Deleted.Count),
            cancellationToken).ConfigureAwait(false);
        return diff;
    }

    private static async Task<IResult> GetReleaseAsync(Guid releaseId, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        StoredHmclRelease release = await store.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new Domain.Common.DomainException("not-found", "The HMCL release does not exist.");
        return Results.Ok(ToDto(release));
    }

    private static async Task<IResult> ListReleaseFilesAsync(
        Guid releaseId,
        HmclDistributionStore store,
        string? search,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await store.ListReleaseFilesAsync(releaseId, search, cursor, limit ?? 100, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetManifestAsync(Guid releaseId, HmclDistributionStore store, CancellationToken cancellationToken)
    {
        StoredHmclRelease release = await store.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new Domain.Common.DomainException("not-found", "The HMCL release does not exist.");
        return Results.Bytes(release.Manifest.ToArray(), "application/json; charset=utf-8");
    }

    private static async Task<IResult> ListStatsAsync(Guid channelId, HmclDistributionStore store, IClock clock, int? days, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<HmclAccessStatDto> items = await store.ListStatsAsync(channelId, days ?? 30, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(
            items,
            item => item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item => item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            cursor,
            limit ?? 50));
    }

    private static async Task<IResult> RunIntegrityAsync(
        HmclDistributionStore store,
        IClock clock,
        IRealtimeEventStream events,
        CancellationToken cancellationToken)
    {
        HmclIntegrityResultDto result = await store.InspectIntegrityAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (HmclIntegrityFailureDto failure in result.Failures)
        {
            _ = await events.PublishAsync(
                EventTypes.HmclIntegrityAlert,
                "global",
                new HmclIntegrityAlertPayload(failure.ChannelId, failure.ReleaseId, failure.Paths.Count),
                cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> SyncReleaseAsync(
        HttpContext context,
        Guid releaseId,
        SyncHmclReleaseToInstanceRequest request,
        HmclDistributionStore store,
        IAgentDistributionGateway distribution,
        INodeControlStore nodes,
        ServerHostOptions options,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        StoredHmclRelease release = await store.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new Domain.Common.DomainException("not-found", "The HMCL release does not exist.");
        (Guid nodeId, AgentInstanceResponse instance) = await FindInstanceAsync(request.InstanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (instance.Status != "Stopped")
        {
            throw new Domain.Common.DomainException("instance-not-stopped", "HMCL content synchronization requires a stopped instance.");
        }

        string root = Path.Combine(options.DataRoot, "staging", "hmcl-instance-sync");
        _ = Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.SyncHmclRelease,
            releaseId.ToString("D") + ":" + request.InstanceId.ToString("D"),
            RequireUserId(context),
            async (operationId, operationToken) =>
            {
                await CreateReleaseArchiveAsync(store, release, archivePath, operationToken).ConfigureAwait(false);
                string streamId = Guid.NewGuid().ToString("N");
                AgentSyncFromReleaseCommand command = new(request.InstanceId, release.Id, streamId, [.. release.Files.Select(file => file.Path)]);
                FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (stream.ConfigureAwait(false))
                {
                    AgentCommandResult result = await distribution.SyncReleaseAsync(nodeId, command, stream, operationToken).ConfigureAwait(false);
                    return ServerOperationResult.FromAgent(result);
                }
            },
            null,
            _ =>
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static Task<IResult> CreateArchiveAsync(
        HttpContext context,
        CreateHmclArchiveRequest request,
        HmclArchiveUploadManager archives,
        CancellationToken cancellationToken)
    {
        return CreateArchiveCoreAsync(context, request, archives, cancellationToken);
    }

    private static async Task<IResult> CreateArchiveCoreAsync(
        HttpContext context,
        CreateHmclArchiveRequest request,
        HmclArchiveUploadManager archives,
        CancellationToken cancellationToken)
    {
        HmclArchiveDto archive = await archives.CreateAsync(RequireUserId(context), request, cancellationToken).ConfigureAwait(false);
        return Results.Created("/api/v1/hmcl/archives/" + archive.Id.ToString("D"), archive);
    }

    private static async Task<IResult> GetArchiveAsync(HttpContext context, Guid archiveId, HmclArchiveUploadManager archives, CancellationToken cancellationToken)
    {
        return Results.Ok(await archives.GetAsync(archiveId, RequireUserId(context), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SaveArchivePartAsync(
        HttpContext context,
        Guid archiveId,
        int index,
        string sha256,
        HmclArchiveUploadManager archives,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await archives.SavePartAsync(
            archiveId, RequireUserId(context), index, sha256, context.Request.Body, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CompleteArchiveAsync(
        HttpContext context,
        Guid archiveId,
        CompleteHmclArchiveRequest request,
        HmclArchiveUploadManager archives,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await archives.CompleteAsync(archiveId, RequireUserId(context), request.Sha256, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> DeleteArchiveAsync(
        HttpContext context,
        Guid archiveId,
        HmclArchiveUploadManager archives,
        CancellationToken cancellationToken)
    {
        await archives.DeleteAsync(archiveId, RequireUserId(context), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task CreateReleaseArchiveAsync(
        HmclDistributionStore store,
        StoredHmclRelease release,
        string archivePath,
        CancellationToken cancellationToken)
    {
        FileStream output = new(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (output.ConfigureAwait(false))
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (StoredHmclReleaseFile file in release.Files.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                string contentPath = store.GetContentPath(file.Sha1);
                if (!File.Exists(contentPath) || new FileInfo(contentPath).Length != file.Size)
                {
                    throw new Domain.Common.DomainException("hmcl-content-missing", "A Release content object is missing or has the wrong size.");
                }

                ZipArchiveEntry entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
                Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (entryStream.ConfigureAwait(false))
                {
                    FileStream source = new(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (source.ConfigureAwait(false))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                        byte[] buffer = new byte[256 * 1024];
                        long copied = 0;
                        while (true)
                        {
                            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            hash.AppendData(buffer, 0, read);
                            copied += read;
                        }

                        string actualSha1 = Convert.ToHexStringLower(hash.GetHashAndReset());
                        if (copied != file.Size || !string.Equals(actualSha1, file.Sha1, StringComparison.Ordinal))
                        {
                            throw new Domain.Common.DomainException("hmcl-content-missing", "A Release content object is corrupt.");
                        }
                    }
                }
            }
        }
    }

    private static async Task<(Guid NodeId, AgentInstanceResponse Instance)> FindInstanceAsync(
        Guid instanceId,
        INodeControlStore nodes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (StoredNodeMirror mirror in mirrors)
        {
            AgentInstanceResponse? instance = mirror.Instances.SingleOrDefault(item => item.Id == instanceId);
            if (instance is not null)
            {
                return (mirror.NodeId, instance);
            }
        }

        throw new Domain.Common.DomainException("not-found", "The instance does not exist in an Agent mirror.");
    }

    private static bool TryReadMetadata(IFormCollection form, out CreateHmclReleaseMetadataRequest? metadata)
    {
        try
        {
            metadata = JsonSerializer.Deserialize<CreateHmclReleaseMetadataRequest>(form["metadata"].ToString());
            return metadata is not null;
        }
        catch (JsonException)
        {
            metadata = null;
            return false;
        }
    }

    private static async Task<string> SaveTemporaryAsync(IFormFile file, ServerHostOptions options, string category, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 20L * 1024 * 1024 * 1024)
        {
            throw new Domain.Common.DomainException("archive-limit-exceeded", "The upload size is outside the allowed range.");
        }

        string root = Path.Combine(options.DataRoot, "staging", category);
        _ = Directory.CreateDirectory(root);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await file.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static Uri RequestBaseUri(HttpRequest request, ServerHostOptions options)
    {
        string pathBase = request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        return options.PublicBaseUri ?? new Uri($"{request.Scheme}://{request.Host}{pathBase}/", UriKind.Absolute);
    }

    private static Guid RequireUserId(HttpContext context)
    {
        return RequestAuthentication.Get(context)?.User.Id
        ?? throw new Domain.Common.DomainException("unauthenticated", "An authenticated user is required.");
    }

    private static HmclReleaseDto ToDto(StoredHmclRelease release)
    {
        return new(
        release.Id,
        release.ChannelId,
        release.Sequence,
        release.Name,
        release.Version,
        release.GameVersion,
        [.. release.Files.Select(file => new HmclReleaseFileDto(file.Path, file.Sha1, file.Size))],
        release.CreatedAt,
        [.. release.Activations.Select(activation => new HmclReleaseActivationDto(activation.StartedAt, activation.EndedAt))]);
    }

    private static HmclChannelDto ToChannelDto(StoredHmclChannel channel)
    {
        return new(channel.Id, channel.Slug, channel.Name, channel.Visibility, channel.ActiveReleaseId);
    }

    private static Uri ManifestUri(HttpRequest request, ServerHostOptions options, StoredHmclChannel channel, string? token)
    {
        string tokenSegment = token is null ? string.Empty : "/" + token;
        return new Uri(RequestBaseUri(request, options), $"hmcl/{channel.Slug}{tokenSegment}/server-manifest.json");
    }

    private static string TokenHash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    private static IResult Accepted(OperationAcceptedDto accepted)
    {
        return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
    }

    private static IResult Problem(HttpContext context, int status, string code)
    {
        return ProblemResponses.Create(context, status, code);
    }
}
