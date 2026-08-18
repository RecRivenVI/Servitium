using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Servitium.Contracts.Distribution;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Distribution;

/// <summary>持久化 Channel 快照。</summary>
public sealed record StoredHmclChannel(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Visibility,
    Guid? ActiveReleaseId,
    int RetentionCount,
    Guid? LinkedInstanceId,
    string IntegrityStatus,
    DateTimeOffset? IntegrityCheckedAt,
    DateTimeOffset CreatedAt);

/// <summary>持久化 Release 文件快照。</summary>
public sealed record StoredHmclReleaseFile(string Path, string Sha1, long Size);

/// <summary>Release 在 Channel 中的一段生效区间。</summary>
public sealed record StoredHmclReleaseActivation(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>持久化不可变 Release 快照。</summary>
public sealed record StoredHmclRelease(
    Guid Id,
    Guid ChannelId,
    long Sequence,
    string Name,
    string Author,
    string Version,
    string Description,
    string GameVersion,
    string? LoaderId,
    string? LoaderVersion,
    string? OptiFineVersion,
    IReadOnlyList<StoredHmclReleaseFile> Files,
    ReadOnlyMemory<byte> Manifest,
    string ManifestSha1,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<StoredHmclReleaseActivation> Activations);

/// <summary>分发路由解析结果。</summary>
public sealed record HmclDistributionResolution(string Status, StoredHmclChannel? Channel, StoredHmclRelease? Release);

/// <summary>持久化 Archive 上传会话。</summary>
public sealed record StoredHmclArchive(
    Guid Id,
    string FileName,
    long TotalBytes,
    int PartSizeBytes,
    int PartCount,
    IReadOnlyList<int> ReceivedParts,
    string? Sha256,
    string Status,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>HMCL 发布的物化、持久化与原子指针切换实现。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "HMCL requires SHA-1 as its wire-level content integrity identifier; it is not used for secrets or signatures.")]
public sealed class HmclDistributionStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    DatabaseLocation databaseLocation) : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetDirectoryName(databaseLocation.Path)!, "content", "sha1");
    private readonly string _stagingRoot = Path.Combine(Path.GetDirectoryName(databaseLocation.Path)!, "staging", "hmcl");
    private readonly string _accessSaltPath = Path.Combine(Path.GetDirectoryName(databaseLocation.Path)!, "hmcl-access-salt");
    private readonly SemaphoreSlim _accessSaltGate = new(1, 1);

    /// <summary>列出 Channel。</summary>
    public async Task<IReadOnlyList<StoredHmclChannel>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow[] rows = await context.HmclChannels.AsNoTracking().OrderBy(row => row.Slug).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToChannel)];
    }

    /// <summary>创建 Archive 上传会话。</summary>
    public Task<StoredHmclArchive> CreateArchiveAsync(StoredHmclArchive archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclArchiveRow row = ToArchiveRow(archive);
                _ = context.HmclArchives.Add(row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToArchive(row);
            },
            cancellationToken);
    }

    /// <summary>读取 Archive 上传会话。</summary>
    public async Task<StoredHmclArchive?> GetArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclArchiveRow? row = await context.HmclArchives.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToArchive(row);
    }

    /// <summary>持久记录已校验的 Archive 分块。</summary>
    public Task<StoredHmclArchive> MarkArchivePartAsync(Guid id, int index, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclArchiveRow row = await context.HmclArchives.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("upload-session-expired", "The HMCL archive upload does not exist.");
                if (row.Status != "uploading" || index < 0 || index >= row.PartCount)
                {
                    throw new DomainException("validation-failed", "The HMCL archive part is invalid for this session.");
                }

                SortedSet<int> parts = [.. JsonSerializer.Deserialize<int[]>(row.ReceivedPartsJson) ?? []];
                _ = parts.Add(index);
                row.ReceivedPartsJson = JsonSerializer.Serialize(parts);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToArchive(row);
            },
            cancellationToken);
    }

    /// <summary>把校验完成的 Archive 标记为只读。</summary>
    public Task<StoredHmclArchive> CompleteArchiveAsync(Guid id, string sha256, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclArchiveRow row = await context.HmclArchives.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("upload-session-expired", "The HMCL archive upload does not exist.");
                int[] parts = JsonSerializer.Deserialize<int[]>(row.ReceivedPartsJson) ?? [];
                if (row.Status != "uploading" || parts.Length != row.PartCount)
                {
                    throw new DomainException("upload-incomplete", "The HMCL archive upload is incomplete.");
                }

                row.Sha256 = sha256;
                row.Status = "ready";
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToArchive(row);
            },
            cancellationToken);
    }

    /// <summary>删除 Archive 元数据。</summary>
    public Task DeleteArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                int deleted = await context.HmclArchives.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                if (deleted == 0)
                {
                    throw new DomainException("not-found", "The HMCL archive does not exist.");
                }
            },
            cancellationToken);
    }

    /// <summary>列出过期 Archive。</summary>
    public async Task<IReadOnlyList<StoredHmclArchive>> ListExpiredArchivesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclArchiveRow[] rows = await context.HmclArchives.AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(row => row.ExpiresAt <= now).Select(ToArchive)];
    }

    /// <summary>创建 Channel；私有令牌只接受哈希值。</summary>
    public Task<StoredHmclChannel> CreateChannelAsync(string slug, string name, string visibility, string? tokenHash, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(visibility);
        if (visibility is not ("public" or "token") ||
            visibility == "token" != (tokenHash is { Length: 64 } && tokenHash.All(Uri.IsHexDigit)))
        {
            throw new DomainException("validation-failed", "The channel visibility or token hash is invalid.");
        }

        Domain.Distribution.ChannelVisibility domainVisibility = visibility == "public"
            ? Domain.Distribution.ChannelVisibility.Public
            : Domain.Distribution.ChannelVisibility.TokenProtected;
        _ = new Domain.Distribution.HmclChannel(id, slug, name, domainVisibility, now);
        return name.Length > 64
            ? throw new DomainException("validation-failed", "The channel name is too long.")
            : writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (await context.HmclChannels.AnyAsync(row => row.Slug == slug, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainException("hmcl-channel-slug-taken", "The channel slug is already in use.");
                }

                HmclChannelRow row = new()
                {
                    Id = id,
                    Slug = slug,
                    Name = name,
                    Description = string.Empty,
                    Visibility = visibility,
                    TokenHash = tokenHash,
                    RetentionCount = 10,
                    IntegrityStatus = "unknown",
                    CreatedAt = now,
                };
                _ = context.HmclChannels.Add(row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToChannel(row);
            },
            cancellationToken);
    }

    /// <summary>读取 Channel 详情。</summary>
    public async Task<StoredHmclChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken)
    {
        return await FindChannelAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>修改 Channel 的可变元数据。</summary>
    public Task<StoredHmclChannel> UpdateChannelAsync(Guid id, UpdateHmclChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64 ||
            request.Description.Length > 1000 || request.RetentionCount is < 3 or > 100
            ? throw new DomainException("validation-failed", "The channel settings are outside their allowed range.")
            : writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow row = await context.HmclChannels.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
                row.Name = request.Name.Trim();
                row.Description = request.Description.Trim();
                row.RetentionCount = request.RetentionCount;
                row.LinkedInstanceId = request.LinkedInstanceId;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToChannel(row);
            },
            cancellationToken);
    }

    /// <summary>切换可见性并立即替换或清除令牌哈希。</summary>
    public Task<StoredHmclChannel> ChangeVisibilityAsync(Guid id, string visibility, string confirmationName, string? tokenHash, CancellationToken cancellationToken)
    {
        return visibility is not ("public" or "token") ||
            visibility == "token" != (tokenHash is { Length: 64 } && tokenHash.All(Uri.IsHexDigit))
            ? throw new DomainException("validation-failed", "The requested visibility is invalid.")
            : writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow row = await context.HmclChannels.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
                if (!string.Equals(row.Name, confirmationName, StringComparison.Ordinal))
                {
                    throw new DomainException("confirmation-mismatch", "The channel confirmation name does not match.");
                }

                if (row.Visibility == visibility)
                {
                    throw new DomainException("validation-failed", "The channel already has the requested visibility.");
                }

                row.Visibility = visibility;
                row.TokenHash = tokenHash;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToChannel(row);
            },
            cancellationToken);
    }

    /// <summary>轮换私有 Channel 令牌。</summary>
    public Task RotateTokenAsync(Guid id, string confirmationName, string tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        return tokenHash.Length != 64 || !tokenHash.All(Uri.IsHexDigit)
            ? throw new DomainException("validation-failed", "The token hash is invalid.")
            : writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow row = await context.HmclChannels.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
                if (row.Visibility != "token" || !string.Equals(row.Name, confirmationName, StringComparison.Ordinal))
                {
                    throw new DomainException("confirmation-mismatch", "The private channel confirmation is invalid.");
                }

                row.TokenHash = tokenHash;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>检查普通内容归档。</summary>
    public static HmclArchivePlan InspectContentArchive(string archivePath)
    {
        return HmclArchiveProcessor.InspectContentArchive(archivePath);
    }

    /// <summary>检查 Prism 原生实例归档。</summary>
    public static HmclArchivePlan InspectPrismArchive(string archivePath)
    {
        return HmclArchiveProcessor.InspectPrismArchive(archivePath);
    }

    /// <summary>从已检查的 ZIP 物化不可变 Release。</summary>
    public async Task<(StoredHmclRelease Release, IReadOnlyDictionary<string, string> Excluded)> CreateReleaseAsync(
        Guid channelId,
        string archivePath,
        HmclArchivePlan plan,
        CreateHmclReleaseMetadataRequest metadata,
        Uri publicBaseUrl,
        Guid createdBy,
        Guid releaseId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(publicBaseUrl);
        ValidateMetadata(metadata);
        StoredHmclChannel channel = await FindChannelAsync(channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
        string fileApi = publicBaseUrl.AbsoluteUri.TrimEnd('/') + "/hmcl/" + channel.Slug;
        IReadOnlyList<StoredHmclReleaseFile> files = await MaterializeAsync(archivePath, plan.Files, cancellationToken).ConfigureAwait(false);
        await RegisterMaterializedObjectsAsync(files, now, cancellationToken).ConfigureAwait(false);

        return await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                long sequence = (await context.HmclReleases
                    .Where(row => row.ChannelId == channelId)
                    .Select(row => (long?)row.Sequence)
                    .MaxAsync(cancellationToken)
                    .ConfigureAwait(false) ?? 0) + 1;
                byte[] manifest = HmclManifestWriter.Write(metadata, fileApi, files);
                string manifestSha1 = Convert.ToHexStringLower(SHA1.HashData(manifest));
                HmclReleaseRow release = new()
                {
                    Id = releaseId,
                    ChannelId = channelId,
                    Sequence = sequence,
                    Name = metadata.Name,
                    Author = metadata.Author,
                    Version = metadata.Version,
                    Description = metadata.Description,
                    GameVersion = metadata.GameVersion,
                    LoaderId = NormalizeLoader(metadata.LoaderId),
                    LoaderVersion = TrimOptional(metadata.LoaderVersion),
                    OptiFineVersion = TrimOptional(metadata.OptiFineVersion),
                    FileCount = files.Count,
                    TotalSize = files.Sum(file => file.Size),
                    Manifest = manifest,
                    ManifestSha1 = manifestSha1,
                    CreatedAt = now,
                    CreatedBy = createdBy,
                };
                _ = context.HmclReleases.Add(release);
                foreach (StoredHmclReleaseFile file in files)
                {
                    HmclContentObjectRow? content = await context.HmclContentObjects.SingleOrDefaultAsync(row => row.Sha1 == file.Sha1, cancellationToken).ConfigureAwait(false);
                    if (content is null)
                    {
                        content = new HmclContentObjectRow { Sha1 = file.Sha1, Size = file.Size, ReferenceCount = 1, CreatedAt = now };
                        _ = context.HmclContentObjects.Add(content);
                    }
                    else
                    {
                        if (content.Size != file.Size)
                        {
                            throw new DomainException("hmcl-content-missing", "A content object has an inconsistent size.");
                        }

                        content.ReferenceCount++;
                    }

                    _ = context.HmclReleaseFiles.Add(new HmclReleaseFileRow
                    {
                        ReleaseId = releaseId,
                        Path = file.Path,
                        ContentSha1 = file.Sha1,
                        Size = file.Size,
                    });
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (ToRelease(release, files), plan.ExcludedPaths);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>预览当前指针与目标 Release 的差异。</summary>
    public async Task<HmclPublishDiffDto> PreviewPublishAsync(Guid channelId, Guid releaseId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow channel = await context.HmclChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
        HmclReleaseRow target = await context.HmclReleases.AsNoTracking().SingleOrDefaultAsync(row => row.Id == releaseId && row.ChannelId == channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL release does not exist.");
        HmclReleaseFileRow[] targetFiles = await context.HmclReleaseFiles.AsNoTracking().Where(row => row.ReleaseId == releaseId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HmclReleaseFileRow[] currentFiles = channel.ActiveReleaseId is Guid currentId
            ? await context.HmclReleaseFiles.AsNoTracking().Where(row => row.ReleaseId == currentId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];
        HmclReleaseRow? current = channel.ActiveReleaseId is Guid activeId
            ? await context.HmclReleases.AsNoTracking().SingleAsync(row => row.Id == activeId, cancellationToken).ConfigureAwait(false)
            : null;
        HmclPublishDiffDto diff = CalculateDiff(current, currentFiles, target, targetFiles);
        StoredHmclRelease release = ToRelease(target, [.. targetFiles.OrderBy(file => file.Path).Select(ToFile)]);
        return diff with { ValidationIssues = await ValidatePublishAsync(release, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>在创建 Release 前，按已经通过归档安全检查的候选计划预览路径与加载器差异。</summary>
    public async Task<HmclPublishDiffDto> PreviewArchivePlanAsync(
        Guid channelId,
        HmclArchivePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow channel = await context.HmclChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
        HmclReleaseFileRow[] currentFiles = channel.ActiveReleaseId is Guid currentId
            ? await context.HmclReleaseFiles.AsNoTracking().Where(row => row.ReleaseId == currentId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];
        HmclReleaseRow? current = channel.ActiveReleaseId is Guid activeId
            ? await context.HmclReleases.AsNoTracking().SingleAsync(row => row.Id == activeId, cancellationToken).ConfigureAwait(false)
            : null;

        HashSet<string> currentPaths = [.. currentFiles.Select(file => file.Path)];
        HashSet<string> candidatePaths = [.. plan.Files.Select(file => file.ReleasePath)];
        string[] added = [.. candidatePaths.Except(currentPaths, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        // 预览阶段尚未物化内容对象；同路径会在候选 Release 中被明确列为覆盖，最终发布预览再按内容哈希精确收窄。
        string[] replaced = [.. candidatePaths.Intersect(currentPaths, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] deleted = [.. currentPaths.Except(candidatePaths, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        (string? loaderId, string? loaderVersion) = ParsePrismLoader(plan.PrismPreview?.Loader);
        bool loaderChanged = current is not null &&
            (current.GameVersion != plan.PrismPreview?.GameVersion || current.LoaderId != loaderId || current.LoaderVersion != loaderVersion);
        return new HmclPublishDiffDto(added, replaced, deleted, loaderChanged, []);
    }

    /// <summary>校验内容后在单一 SQLite 事务中切换 Channel 指针。</summary>
    public async Task<HmclPublishDiffDto> PublishAsync(
        Guid channelId,
        Guid releaseId,
        DateTimeOffset now,
        Guid auditId,
        Guid actorId,
        string actorName,
        string sourceAddress,
        CancellationToken cancellationToken)
    {
        StoredHmclRelease target = await FindReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL release does not exist.");
        if (target.ChannelId != channelId)
        {
            throw new DomainException("validation-failed", "The release does not belong to the channel.");
        }

        IReadOnlyList<HmclPublishValidationIssueDto> failures = await ValidatePublishAsync(target, cancellationToken).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new DomainException("hmcl-release-validation-failed", "The release did not pass every publication gate.");
        }

        HmclPublishDiffDto diff = await PreviewPublishAsync(channelId, releaseId, cancellationToken).ConfigureAwait(false);
        await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow channel = await context.HmclChannels.SingleAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false);
                HmclReleaseRow release = await context.HmclReleases.SingleAsync(row => row.Id == releaseId && row.ChannelId == channelId, cancellationToken).ConfigureAwait(false);
                HmclReleaseRow? previous = channel.ActiveReleaseId is Guid previousId
                    ? await context.HmclReleases.SingleAsync(row => row.Id == previousId, cancellationToken).ConfigureAwait(false)
                    : null;
                DateTimeOffset activatedAt = now.ToUniversalTime();
                if (previous?.Id != releaseId)
                {
                    HmclReleaseActivationRow? currentActivation = await context.HmclReleaseActivations
                        .SingleOrDefaultAsync(row => row.ChannelId == channelId && row.EndedAt == null, cancellationToken)
                        .ConfigureAwait(false);
                    switch (currentActivation)
                    {
                        case HmclReleaseActivationRow activation:
                            activation.EndedAt = activatedAt;
                            break;
                        default:
                            break;
                    }

                    context.Entry(new HmclReleaseActivationRow
                    {
                        Id = Guid.NewGuid(),
                        ChannelId = channelId,
                        ReleaseId = releaseId,
                        StartedAt = activatedAt,
                    }).State = EntityState.Added;
                    channel.ActiveReleaseId = releaseId;
                }

                release.PublishedAt ??= activatedAt;
                _ = context.AuditLogs.Add(new AuditLogRow
                {
                    Id = auditId,
                    ActorId = actorId,
                    ActorNameSnapshot = actorName,
                    Category = "DestructiveAction",
                    Action = previous is not null && release.Sequence < previous.Sequence ? "hmcl.rolled-back" : "hmcl.published",
                    Resource = "hmcl-channel:" + channelId.ToString("D"),
                    OccurredAt = now.ToUniversalTime(),
                    OccurredAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
                    OutcomeCode = "success",
                    PropertiesJson = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["previousReleaseId"] = previous?.Id.ToString("D") ?? string.Empty,
                        ["releaseId"] = releaseId.ToString("D"),
                        ["sourceAddress"] = sourceAddress,
                    }),
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return diff;
    }

    /// <summary>不在首个错误处中止，一次性返回发布前可见的全部问题。</summary>
    public async Task<IReadOnlyList<HmclPublishValidationIssueDto>> ValidatePublishAsync(
        StoredHmclRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        List<HmclPublishValidationIssueDto> issues = [];
        foreach (IGrouping<string, StoredHmclReleaseFile> duplicate in release.Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            issues.Add(new("path-case-conflict", duplicate.Key));
        }

        foreach (StoredHmclReleaseFile file in release.Files)
        {
            try
            {
                HmclArchiveProcessor.ValidateReleasePath(file.Path);
            }
            catch (DomainException)
            {
                issues.Add(new("path-invalid", file.Path));
            }

            if (file.Sha1.Length != 40 || file.Sha1.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                issues.Add(new("content-hash-invalid", file.Path));
                continue;
            }

            string contentPath = ContentPath(file.Sha1);
            if (!File.Exists(contentPath))
            {
                issues.Add(new("content-missing", file.Path));
            }
            else if (new FileInfo(contentPath).Length != file.Size)
            {
                issues.Add(new("content-size-mismatch", file.Path));
            }
            else if (!string.Equals(await ComputeSha1Async(contentPath, cancellationToken).ConfigureAwait(false), file.Sha1, StringComparison.Ordinal))
            {
                issues.Add(new("content-hash-mismatch", file.Path));
            }
        }

        if (string.IsNullOrWhiteSpace(release.GameVersion))
        {
            issues.Add(new("game-addon-missing", null));
        }

        string? loader = NormalizeLoader(release.LoaderId);
        if ((loader is not null && loader is not ("forge" or "neoforge" or "fabric" or "quilt" or "liteloader")) ||
            (loader is null) != string.IsNullOrWhiteSpace(release.LoaderVersion))
        {
            issues.Add(new("loader-addon-invalid", null));
        }

        ValidateMaterializedManifest(release, issues);
        return issues;
    }

    /// <summary>列出 Channel 的不可变 Release。</summary>
    public async Task<IReadOnlyList<HmclReleaseSummaryDto>> ListReleasesAsync(Guid channelId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow channel = await context.HmclChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
        return await context.HmclReleases.AsNoTracking()
            .Where(row => row.ChannelId == channelId)
            .OrderByDescending(row => row.Sequence)
            .Select(row => new HmclReleaseSummaryDto(
                row.Id, row.ChannelId, row.Sequence, row.Name, row.Version, row.GameVersion,
                row.FileCount, row.TotalSize, row.CreatedAt, row.PublishedAt, row.Id == channel.ActiveReleaseId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>读取 Release 与固化文件清单。</summary>
    public async Task<StoredHmclRelease?> GetReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        return await FindReleaseAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>分页读取 Release 文件。</summary>
    public async Task<HmclReleaseFilePageDto> ListReleaseFilesAsync(Guid releaseId, string? search, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200 || (search?.Length ?? 0) > 200 || (cursor?.Length ?? 0) > 240)
        {
            throw new DomainException("validation-failed", "The release file query is invalid.");
        }

        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await context.HmclReleases.AsNoTracking().AnyAsync(row => row.Id == releaseId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("not-found", "The HMCL release does not exist.");
        }

        IQueryable<HmclReleaseFileRow> query = context.HmclReleaseFiles.AsNoTracking().Where(row => row.ReleaseId == releaseId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string normalized = search.Trim();
            query = query.Where(row => row.Path.Contains(normalized));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query = query.Where(row => row.Path.CompareTo(cursor) > 0);
        }

        HmclReleaseFileRow[] rows = await query.OrderBy(row => row.Path).Take(limit + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string? next = rows.Length > limit ? rows[limit - 1].Path : null;
        return new HmclReleaseFilePageDto([.. rows.Take(limit).Select(row => new HmclReleaseFileDto(row.Path, row.ContentSha1, row.Size))], next);
    }

    /// <summary>删除 Channel 及其 Release，并把内容引用减为可回收状态。</summary>
    public Task DeleteChannelAsync(Guid channelId, string confirmationName, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow channel = await context.HmclChannels.SingleOrDefaultAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
                if (!string.Equals(channel.Name, confirmationName, StringComparison.Ordinal))
                {
                    throw new DomainException("confirmation-mismatch", "The channel confirmation name does not match.");
                }

                Guid[] releases = await context.HmclReleases.Where(row => row.ChannelId == channelId).Select(row => row.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                await DecrementReferencesAsync(context, releases, cancellationToken).ConfigureAwait(false);
                context.HmclReleases.RemoveRange(context.HmclReleases.Where(row => releases.Contains(row.Id)));
                context.HmclAccessStats.RemoveRange(context.HmclAccessStats.Where(row => row.ChannelId == channelId));
                context.HmclAccessSources.RemoveRange(context.HmclAccessSources.Where(row => row.ChannelId == channelId));
                _ = context.HmclChannels.Remove(channel);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>按保留策略淘汰历史 Release。</summary>
    public Task<int> ApplyRetentionAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow channel = await context.HmclChannels.SingleOrDefaultAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The HMCL channel does not exist.");
                Guid[] obsolete = await context.HmclReleases
                    .Where(row => row.ChannelId == channelId && row.Id != channel.ActiveReleaseId)
                    .OrderByDescending(row => row.Sequence)
                    .Skip(channel.RetentionCount)
                    .Select(row => row.Id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (obsolete.Length == 0)
                {
                    return 0;
                }

                await DecrementReferencesAsync(context, obsolete, cancellationToken).ConfigureAwait(false);
                context.HmclReleases.RemoveRange(context.HmclReleases.Where(row => obsolete.Contains(row.Id)));
                context.HmclAccessStats.RemoveRange(context.HmclAccessStats.Where(row => obsolete.Contains(row.ReleaseId)));
                context.HmclAccessSources.RemoveRange(context.HmclAccessSources.Where(row => obsolete.Contains(row.ReleaseId)));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return obsolete.Length;
            },
            cancellationToken);
    }

    /// <summary>回收超过安全等待期且无引用的内容对象。</summary>
    public async Task<HmclReclamationResultDto> ReclaimAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        int removedReleases = 0;
        Guid[] channelIds;
        using (ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            channelIds = await context.HmclChannels.AsNoTracking().Select(row => row.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (Guid channelId in channelIds)
        {
            removedReleases += await ApplyRetentionAsync(channelId, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset cutoff = now.AddHours(-24);
        var statisticsCutoff = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-90));
        List<(string Sha1, long Size)> removed = await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.HmclAccessStats.Where(row => row.Date < statisticsCutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.HmclAccessSources.Where(row => row.Date < statisticsCutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                HmclContentObjectRow[] candidates = await context.HmclContentObjects
                    .Where(row => row.ReferenceCount == 0)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                List<(string Sha1, long Size)> result = [];
                foreach (HmclContentObjectRow candidate in candidates.Where(row => row.CreatedAt < cutoff))
                {
                    bool referenced = await context.HmclReleaseFiles.AsNoTracking().AnyAsync(row => row.ContentSha1 == candidate.Sha1, cancellationToken).ConfigureAwait(false);
                    if (!referenced && candidate.ReferenceCount == 0)
                    {
                        result.Add((candidate.Sha1, candidate.Size));
                        _ = context.HmclContentObjects.Remove(candidate);
                    }
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return result;
            },
            cancellationToken).ConfigureAwait(false);

        foreach ((string sha1, _) in removed)
        {
            string path = ContentPath(sha1);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return new HmclReclamationResultDto(removedReleases, removed.Count, removed.Sum(item => item.Size));
    }

    /// <summary>按 slug 与可选令牌解析当前分发快照。</summary>
    public async Task<HmclDistributionResolution> ResolveActiveAsync(string slug, string? token, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow? channel = await context.HmclChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return new HmclDistributionResolution("not-found", null, null);
        }

        if (channel.Visibility == "public")
        {
            if (token is not null)
            {
                return new HmclDistributionResolution("not-found", ToChannel(channel), null);
            }
        }
        else if (channel.Visibility == "token")
        {
            if (token is null || !TokenMatches(token, channel.TokenHash))
            {
                return new HmclDistributionResolution("forbidden", ToChannel(channel), null);
            }
        }
        else
        {
            return new HmclDistributionResolution("not-found", null, null);
        }

        if (channel.ActiveReleaseId is not Guid releaseId)
        {
            return new HmclDistributionResolution("not-found", ToChannel(channel), null);
        }

        HmclReleaseRow release = await context.HmclReleases.AsNoTracking().SingleAsync(row => row.Id == releaseId, cancellationToken).ConfigureAwait(false);
        HmclReleaseFileRow[] files = await context.HmclReleaseFiles.AsNoTracking().Where(row => row.ReleaseId == releaseId).OrderBy(row => row.Path).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HmclDistributionResolution("ok", ToChannel(channel), ToRelease(release, [.. files.Select(ToFile)]));
    }

    /// <summary>为实际访问路径生成确定性 manifest；不持久化私有令牌。</summary>
    public static byte[] RenderManifest(StoredHmclRelease release, string fileApi)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(fileApi);
        CreateHmclReleaseMetadataRequest metadata = new(
            release.Name, release.Author, release.Version, release.Description, release.GameVersion,
            release.LoaderId, release.LoaderVersion, release.OptiFineVersion);
        return HmclManifestWriter.Write(metadata, fileApi.TrimEnd('/'), release.Files);
    }

    /// <summary>记录不包含完整 IP 的日聚合访问统计。</summary>
    public async Task RecordAccessAsync(
        Guid channelId,
        Guid releaseId,
        DateTimeOffset occurredAt,
        bool manifest,
        int statusCode,
        long bytes,
        IPAddress? source,
        CancellationToken cancellationToken)
    {
        string? prefixHash = source is null ? null : await HashSourcePrefixAsync(source, cancellationToken).ConfigureAwait(false);
        var date = DateOnly.FromDateTime(occurredAt.UtcDateTime);
        await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclAccessStatRow? row = await context.HmclAccessStats.SingleOrDefaultAsync(
                    item => item.ChannelId == channelId && item.ReleaseId == releaseId && item.Date == date,
                    cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    row = new HmclAccessStatRow { ChannelId = channelId, ReleaseId = releaseId, Date = date };
                    _ = context.HmclAccessStats.Add(row);
                }

                if (manifest && statusCode == 200)
                {
                    row.Manifest200++;
                }
                else if (manifest && statusCode == 304)
                {
                    row.Manifest304++;
                }
                else if (!manifest && statusCode == 200)
                {
                    row.File200++;
                }
                else if (!manifest && statusCode == 206)
                {
                    row.File206++;
                }

                if (statusCode >= 400)
                {
                    row.Errors++;
                }

                row.Bytes += Math.Max(0, bytes);
                if (prefixHash is not null && !await context.HmclAccessSources.AnyAsync(
                    item => item.ChannelId == channelId && item.ReleaseId == releaseId && item.Date == date && item.PrefixHash == prefixHash,
                    cancellationToken).ConfigureAwait(false))
                {
                    _ = context.HmclAccessSources.Add(new HmclAccessSourceRow
                    {
                        ChannelId = channelId,
                        ReleaseId = releaseId,
                        Date = date,
                        PrefixHash = prefixHash,
                    });
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取最近最多 90 天的 Channel 访问统计。</summary>
    public async Task<IReadOnlyList<HmclAccessStatDto>> ListStatsAsync(Guid channelId, int days, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 90)
        {
            throw new DomainException("validation-failed", "The statistics range is invalid.");
        }

        var cutoff = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-(days - 1)));
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await context.HmclChannels.AsNoTracking().AnyAsync(row => row.Id == channelId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("not-found", "The HMCL channel does not exist.");
        }

        HmclAccessStatRow[] stats = await context.HmclAccessStats.AsNoTracking()
            .Where(row => row.ChannelId == channelId && row.Date >= cutoff)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HmclAccessSourceRow[] sources = await context.HmclAccessSources.AsNoTracking()
            .Where(row => row.ChannelId == channelId && row.Date >= cutoff)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. stats.GroupBy(row => row.Date).OrderBy(group => group.Key).Select(group => new HmclAccessStatDto(
            group.Key,
            group.Sum(row => row.Manifest200),
            group.Sum(row => row.Manifest304),
            group.Sum(row => row.File200),
            group.Sum(row => row.File206),
            group.Sum(row => row.Errors),
            group.Sum(row => row.Bytes),
            sources.Where(row => row.Date == group.Key).Select(row => row.PrefixHash).Distinct(StringComparer.Ordinal).LongCount()))];
    }

    /// <summary>巡检所有保留 Release 并更新 Channel 完整性状态。</summary>
    public async Task<HmclIntegrityResultDto> InspectIntegrityAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<HmclIntegrityFailureDto> failures = [];
        Guid[] channelIds;
        using (ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            channelIds = await context.HmclChannels.AsNoTracking().Select(row => row.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        int checkedReleases = 0;
        foreach (Guid channelId in channelIds)
        {
            IReadOnlyList<HmclReleaseSummaryDto> releases = await ListReleasesAsync(channelId, cancellationToken).ConfigureAwait(false);
            foreach (HmclReleaseSummaryDto summary in releases)
            {
                StoredHmclRelease release = (await FindReleaseAsync(summary.Id, cancellationToken).ConfigureAwait(false))!;
                checkedReleases++;
                List<string> badPaths = [];
                foreach (StoredHmclReleaseFile file in release.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = ContentPath(file.Sha1);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
                        !string.Equals(await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false), file.Sha1, StringComparison.Ordinal))
                    {
                        badPaths.Add(file.Path);
                    }
                }

                if (badPaths.Count > 0)
                {
                    failures.Add(new HmclIntegrityFailureDto(channelId, release.Id, badPaths));
                }
            }
        }

        await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                HmclChannelRow[] channels = await context.HmclChannels.ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (HmclChannelRow channel in channels)
                {
                    channel.IntegrityCheckedAt = now;
                    channel.IntegrityStatus = failures.Any(failure => failure.ChannelId == channel.Id) ? "failed" : "healthy";
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return new HmclIntegrityResultDto(now, channelIds.Length, checkedReleases, failures);
    }

    /// <summary>按当前 Release 并回退最近三个历史 Release 查找路径。</summary>
    public async Task<StoredHmclReleaseFile?> FindDistributedFileAsync(Guid channelId, Guid currentReleaseId, string path, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid[] releaseIds = await context.HmclReleases.AsNoTracking()
            .Where(row => row.ChannelId == channelId)
            .OrderByDescending(row => row.Id == currentReleaseId)
            .ThenByDescending(row => row.Sequence)
            .Take(4)
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        HmclReleaseFileRow[] matches = await context.HmclReleaseFiles.AsNoTracking()
            .Where(row => releaseIds.Contains(row.ReleaseId) && row.Path == path)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        HmclReleaseFileRow? file = releaseIds.Select(id => matches.FirstOrDefault(candidate => candidate.ReleaseId == id)).FirstOrDefault(candidate => candidate is not null);
        return file is null ? null : ToFile(file);
    }

    /// <summary>获取内容对象的只读绝对路径。</summary>
    public string GetContentPath(string sha1)
    {
        ArgumentNullException.ThrowIfNull(sha1);
        return ContentPath(sha1);
    }

    private async Task<IReadOnlyList<StoredHmclReleaseFile>> MaterializeAsync(
        string archivePath,
        IReadOnlyList<HmclPlannedFile> plannedFiles,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_contentRoot);
        _ = Directory.CreateDirectory(_stagingRoot);
        using FileStream source = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.RandomAccess);
        using ZipArchive archive = new(source, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.ToDictionary(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        List<StoredHmclReleaseFile> files = [];
        foreach (HmclPlannedFile planned in plannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(planned.EntryName, out ZipArchiveEntry? entry))
            {
                throw new DomainException("hmcl-content-missing", "A planned archive entry is missing.");
            }

            string temporary = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".content");
            try
            {
                string sha1;
                long length = 0;
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                {
                    Stream input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using ConfiguredAsyncDisposable inputLifetime = input.ConfigureAwait(false);
                    using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    byte[] buffer = new byte[256 * 1024];
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        length += read;
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    sha1 = Convert.ToHexStringLower(hash.GetHashAndReset());
                }

                if (length != planned.Size)
                {
                    throw new DomainException("hmcl-content-missing", "An archive entry changed while materializing.");
                }

                string target = ContentPath(sha1);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    if (new FileInfo(target).Length != length)
                    {
                        throw new DomainException("hmcl-content-missing", "A content hash collision or corrupt object was detected.");
                    }
                }
                else
                {
                    try
                    {
                        File.Move(temporary, target);
                    }
                    catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == length)
                    {
                    }
                }

                files.Add(new StoredHmclReleaseFile(planned.ReleasePath, sha1, length));
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return files;
    }

    private Task RegisterMaterializedObjectsAsync(IReadOnlyList<StoredHmclReleaseFile> files, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                string[] hashes = [.. files.Select(file => file.Sha1).Distinct(StringComparer.Ordinal)];
                HmclContentObjectRow[] existing = await context.HmclContentObjects.Where(row => hashes.Contains(row.Sha1)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                Dictionary<string, HmclContentObjectRow> known = existing.ToDictionary(row => row.Sha1, StringComparer.Ordinal);
                foreach (StoredHmclReleaseFile file in files)
                {
                    if (known.TryGetValue(file.Sha1, out HmclContentObjectRow? row))
                    {
                        if (row.Size != file.Size)
                        {
                            throw new DomainException("hmcl-content-missing", "A content object has an inconsistent size.");
                        }

                        continue;
                    }

                    HmclContentObjectRow created = new() { Sha1 = file.Sha1, Size = file.Size, ReferenceCount = 0, CreatedAt = now };
                    known.Add(file.Sha1, created);
                    _ = context.HmclContentObjects.Add(created);
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static async Task DecrementReferencesAsync(ServitiumDbContext context, Guid[] releaseIds, CancellationToken cancellationToken)
    {
        if (releaseIds.Length == 0)
        {
            return;
        }

        HmclReleaseFileRow[] files = await context.HmclReleaseFiles
            .Where(row => releaseIds.Contains(row.ReleaseId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (IGrouping<string, HmclReleaseFileRow> group in files.GroupBy(row => row.ContentSha1, StringComparer.Ordinal))
        {
            HmclContentObjectRow content = await context.HmclContentObjects.SingleAsync(row => row.Sha1 == group.Key, cancellationToken).ConfigureAwait(false);
            content.ReferenceCount -= group.LongCount();
            if (content.ReferenceCount < 0)
            {
                throw new DomainException("hmcl-content-missing", "A content reference count would become negative.");
            }
        }
    }

    private async Task<string> HashSourcePrefixAsync(IPAddress source, CancellationToken cancellationToken)
    {
        byte[] address;
        int length;
        if (source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || source.IsIPv4MappedToIPv6)
        {
            address = source.MapToIPv4().GetAddressBytes();
            length = 3;
        }
        else
        {
            address = source.MapToIPv6().GetAddressBytes();
            length = 6;
        }

        byte[] salt = await GetAccessSaltAsync(cancellationToken).ConfigureAwait(false);
        byte[] input = new byte[salt.Length + length];
        salt.CopyTo(input, 0);
        address.AsSpan(0, length).CopyTo(input.AsSpan(salt.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private async Task<byte[]> GetAccessSaltAsync(CancellationToken cancellationToken)
    {
        await _accessSaltGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_accessSaltPath))
            {
                byte[] existing = await File.ReadAllBytesAsync(_accessSaltPath, cancellationToken).ConfigureAwait(false);
                return existing.Length == 32 ? existing : throw new DomainException("hmcl-content-missing", "The HMCL statistics salt is invalid.");
            }

            byte[] created = RandomNumberGenerator.GetBytes(32);
            string temporary = _accessSaltPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, created, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, _accessSaltPath);
            }
            catch (IOException) when (File.Exists(_accessSaltPath))
            {
                File.Delete(temporary);
                return await File.ReadAllBytesAsync(_accessSaltPath, cancellationToken).ConfigureAwait(false);
            }

            return created;
        }
        finally
        {
            _ = _accessSaltGate.Release();
        }
    }

    private static bool TokenMatches(string token, string? expectedHash)
    {
        if (token.Length != 43 || expectedHash is not { Length: 64 })
        {
            return false;
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedHash));
    }

    private async Task<StoredHmclChannel?> FindChannelAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclChannelRow? row = await context.HmclChannels.AsNoTracking().SingleOrDefaultAsync(channel => channel.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToChannel(row);
    }

    private async Task<StoredHmclRelease?> FindReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        HmclReleaseRow? row = await context.HmclReleases.AsNoTracking().SingleOrDefaultAsync(release => release.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        HmclReleaseFileRow[] files = await context.HmclReleaseFiles.AsNoTracking().Where(file => file.ReleaseId == id).OrderBy(file => file.Path).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HmclReleaseActivationRow[] activations = await context.HmclReleaseActivations.AsNoTracking()
            .Where(activation => activation.ReleaseId == id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToRelease(
            row,
            [.. files.Select(ToFile)],
            [.. activations.OrderByDescending(activation => activation.StartedAt).Select(ToActivation)]);
    }

    private static void ValidateMetadata(CreateHmclReleaseMetadataRequest metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.Name) || string.IsNullOrWhiteSpace(metadata.Author) ||
            string.IsNullOrWhiteSpace(metadata.Version) || string.IsNullOrWhiteSpace(metadata.GameVersion))
        {
            throw new DomainException("validation-failed", "Release metadata contains a missing required value.");
        }

        string? loader = NormalizeLoader(metadata.LoaderId);
        if ((loader is not null && loader is not ("forge" or "neoforge" or "fabric" or "quilt" or "liteloader")) ||
            (loader is null) != (TrimOptional(metadata.LoaderVersion) is null))
        {
            throw new DomainException("hmcl-release-validation-failed", "The loader declaration is invalid.");
        }
    }

    private static void ValidateMaterializedManifest(
        StoredHmclRelease release,
        List<HmclPublishValidationIssueDto> issues)
    {
        string actualSha1 = Convert.ToHexStringLower(SHA1.HashData(release.Manifest.Span));
        if (!string.Equals(actualSha1, release.ManifestSha1, StringComparison.Ordinal))
        {
            issues.Add(new("manifest-hash-mismatch", null));
        }

        try
        {
            using var document = JsonDocument.Parse(release.Manifest);
            if (!document.RootElement.TryGetProperty("fileApi", out JsonElement fileApiElement) ||
                fileApiElement.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(fileApiElement.GetString(), UriKind.Absolute, out Uri? fileApi) ||
                fileApi.AbsoluteUri.EndsWith('/'))
            {
                issues.Add(new("manifest-file-api-invalid", null));
                return;
            }

            CreateHmclReleaseMetadataRequest metadata = new(
                release.Name,
                release.Author,
                release.Version,
                release.Description,
                release.GameVersion,
                release.LoaderId,
                release.LoaderVersion,
                release.OptiFineVersion);
            byte[] expected = HmclManifestWriter.Write(metadata, fileApi.AbsoluteUri, release.Files);
            if (!release.Manifest.Span.SequenceEqual(expected))
            {
                issues.Add(new("manifest-content-mismatch", null));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            issues.Add(new("manifest-invalid", null));
        }
    }

    private string ContentPath(string sha1)
    {
        return sha1.Length != 40 || sha1.Any(character => !Uri.IsHexDigit(character))
            ? throw new DomainException("validation-failed", "The content hash is invalid.")
            : Path.Combine(_contentRoot, sha1[..2], sha1);
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static HmclPublishDiffDto CalculateDiff(HmclReleaseRow? current, IReadOnlyList<HmclReleaseFileRow> currentFiles, HmclReleaseRow target, IReadOnlyList<HmclReleaseFileRow> targetFiles)
    {
        var oldMap = currentFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var newMap = targetFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        string[] added = [.. newMap.Keys.Except(oldMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] replaced = [.. newMap.Keys.Intersect(oldMap.Keys, StringComparer.Ordinal).Where(path => newMap[path].ContentSha1 != oldMap[path].ContentSha1).Order(StringComparer.Ordinal)];
        string[] deleted = [.. oldMap.Keys.Except(newMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        bool loaderChanged = current is not null && (current.GameVersion != target.GameVersion || current.LoaderId != target.LoaderId || current.LoaderVersion != target.LoaderVersion || current.OptiFineVersion != target.OptiFineVersion);
        return new HmclPublishDiffDto(added, replaced, deleted, loaderChanged);
    }

    private static string? NormalizeLoader(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() switch
            {
                string loader when loader.Equals("forge", StringComparison.OrdinalIgnoreCase) => "forge",
                string loader when loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase) => "neoforge",
                string loader when loader.Equals("fabric", StringComparison.OrdinalIgnoreCase) => "fabric",
                string loader when loader.Equals("quilt", StringComparison.OrdinalIgnoreCase) => "quilt",
                string loader when loader.Equals("liteloader", StringComparison.OrdinalIgnoreCase) => "liteloader",
                _ => value.Trim(),
            };
    }

    private static (string? Id, string? Version) ParsePrismLoader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        string[] parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (NormalizeLoader(parts[0]), parts[1]) : (NormalizeLoader(parts[0]), null);
    }

    private static string? TrimOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static StoredHmclChannel ToChannel(HmclChannelRow row)
    {
        return new(
            row.Id,
            row.Slug,
            row.Name,
            row.Description,
            row.Visibility,
            row.ActiveReleaseId,
            row.RetentionCount,
            row.LinkedInstanceId,
            row.IntegrityStatus,
            row.IntegrityCheckedAt,
            row.CreatedAt);
    }

    private static StoredHmclReleaseFile ToFile(HmclReleaseFileRow row)
    {
        return new(row.Path, row.ContentSha1, row.Size);
    }

    private static StoredHmclRelease ToRelease(
        HmclReleaseRow row,
        IReadOnlyList<StoredHmclReleaseFile> files,
        IReadOnlyList<StoredHmclReleaseActivation>? activations = null)
    {
        return new(
        row.Id, row.ChannelId, row.Sequence, row.Name, row.Author, row.Version, row.Description, row.GameVersion,
        row.LoaderId, row.LoaderVersion, row.OptiFineVersion, files, row.Manifest, row.ManifestSha1, row.CreatedAt, row.PublishedAt,
        activations ?? []);
    }

    private static StoredHmclReleaseActivation ToActivation(HmclReleaseActivationRow row)
    {
        return new(row.StartedAt, row.EndedAt);
    }

    private static StoredHmclArchive ToArchive(HmclArchiveRow row)
    {
        return new(
            row.Id,
            row.FileName,
            row.TotalBytes,
            row.PartSizeBytes,
            row.PartCount,
            JsonSerializer.Deserialize<int[]>(row.ReceivedPartsJson) ?? [],
            row.Sha256,
            row.Status,
            row.CreatedBy,
            row.CreatedAt,
            row.ExpiresAt);
    }

    private static HmclArchiveRow ToArchiveRow(StoredHmclArchive archive)
    {
        return new HmclArchiveRow
        {
            Id = archive.Id,
            FileName = archive.FileName,
            TotalBytes = archive.TotalBytes,
            PartSizeBytes = archive.PartSizeBytes,
            PartCount = archive.PartCount,
            ReceivedPartsJson = JsonSerializer.Serialize(archive.ReceivedParts),
            Sha256 = archive.Sha256,
            Status = archive.Status,
            CreatedBy = archive.CreatedBy,
            CreatedAt = archive.CreatedAt,
            ExpiresAt = archive.ExpiresAt,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _accessSaltGate.Dispose();
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "HMCL requires SHA-1 as its wire-level content integrity identifier; it is not used for secrets or signatures.")]
internal static class HmclManifestWriter
{
    internal static byte[] Write(CreateHmclReleaseMetadataRequest metadata, string fileApi, IReadOnlyList<StoredHmclReleaseFile> files)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", metadata.Name);
            writer.WriteString("author", metadata.Author);
            writer.WriteString("version", metadata.Version);
            writer.WriteString("description", metadata.Description);
            writer.WriteString("fileApi", fileApi);
            writer.WriteStartArray("files");
            foreach (StoredHmclReleaseFile file in files.OrderBy(item => item.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("hash", file.Sha1);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("addons");
            WriteAddon(writer, "game", metadata.GameVersion);
            if (!string.IsNullOrWhiteSpace(metadata.LoaderId))
            {
                WriteAddon(writer, NormalizeManifestLoader(metadata.LoaderId), metadata.LoaderVersion!);
            }

            if (!string.IsNullOrWhiteSpace(metadata.OptiFineVersion))
            {
                WriteAddon(writer, "optifine", metadata.OptiFineVersion);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteAddon(Utf8JsonWriter writer, string id, string version)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("version", version);
        writer.WriteEndObject();
    }

    private static string NormalizeManifestLoader(string loader)
    {
        return loader.Trim() switch
        {
            string value when value.Equals("forge", StringComparison.OrdinalIgnoreCase) => "forge",
            string value when value.Equals("neoforge", StringComparison.OrdinalIgnoreCase) => "neoforge",
            string value when value.Equals("fabric", StringComparison.OrdinalIgnoreCase) => "fabric",
            string value when value.Equals("quilt", StringComparison.OrdinalIgnoreCase) => "quilt",
            string value when value.Equals("liteloader", StringComparison.OrdinalIgnoreCase) => "liteloader",
            _ => throw new DomainException("hmcl-release-validation-failed", "The loader declaration is invalid."),
        };
    }
}
