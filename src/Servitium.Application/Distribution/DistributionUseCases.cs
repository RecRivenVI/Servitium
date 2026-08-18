using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Contracts.Distribution;
using Servitium.Domain.Distribution;
using Servitium.Domain.Operations;

namespace Servitium.Application.Distribution;

/// <summary>创建稳定 slug 的 HMCL Channel。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class CreateHmclChannelUseCase(
    IAuthorizationService authorization,
    IHmclChannelRepository channels,
    IGuidFactory guids,
    IClock clock,
    IUnitOfWork unitOfWork)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IHmclChannelRepository _channels = channels;
    private readonly IGuidFactory _guids = guids;
    private readonly IClock _clock = clock;
    private readonly IUnitOfWork _unitOfWork = unitOfWork;

    /// <summary>创建 Channel。</summary>
    public async Task<ApplicationResult<Guid>> ExecuteAsync(
        AuthorizationPrincipal principal,
        CreateHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        AuthorizationDecision decision = _authorization.Authorize(
            principal,
            new AuthorizationResource(ResourceKind.Global),
            ServitiumAction.ManageDistribution);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<Guid>(decision.Code!);
        }

        HmclChannel? existing = await _channels.FindBySlugAsync(request.Slug, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ApplicationResult.Failure<Guid>("hmcl-channel-slug-taken");
        }

        ChannelVisibility visibility = string.Equals(request.Visibility, "public", StringComparison.Ordinal)
            ? ChannelVisibility.Public
            : ChannelVisibility.TokenProtected;
        var channel = new HmclChannel(_guids.Create(), request.Slug, request.Name, visibility, _clock.UtcNow);
        await _channels.AddAsync(channel, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(channel.Id);
    }
}

/// <summary>登记 HMCL Release 创建长操作。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class CreateHmclReleaseUseCase(IAuthorizationService authorization, IOperationTracker operations)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IOperationTracker _operations = operations;

    /// <summary>建立 Release 创建操作。</summary>
    public async Task<ApplicationResult<Guid>> ExecuteAsync(
        AuthorizationPrincipal principal,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        AuthorizationDecision decision = _authorization.Authorize(
            principal,
            new AuthorizationResource(ResourceKind.Channel, channelId),
            ServitiumAction.ManageDistribution);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<Guid>(decision.Code!);
        }

        Operation operation = await _operations
            .CreateAsync(OperationKind.CreateHmclRelease, channelId.ToString("D"), principal.UserId, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult.Success(operation.Id);
    }
}
