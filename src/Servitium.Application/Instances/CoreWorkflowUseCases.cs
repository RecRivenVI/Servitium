using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Contracts.Instances;
using Servitium.Domain.Files;
using Servitium.Domain.Operations;

namespace Servitium.Application.Instances;

/// <summary>为后续基础设施提供导入识别端口。</summary>
public interface IInstanceImportInspector
{
    /// <summary>读取合成或用户上传的 Prism 原生实例 ZIP 并返回安全预览。</summary>
    Task<PrismInstancePreviewDto> PreviewPrismAsync(Guid uploadId, CancellationToken cancellationToken);
}

/// <summary>编排外部绝对路径的 Server 侧预校验与长操作登记。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class RegisterExternalDirectoryUseCase(
    IAuthorizationService authorization,
    IPathValidator pathValidator,
    IOperationTracker operations)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IPathValidator _pathValidator = pathValidator;
    private readonly IOperationTracker _operations = operations;

    /// <summary>校验路径并建立 Agent 侧登记操作。</summary>
    public async Task<ApplicationResult<Guid>> ExecuteAsync(
        AuthorizationPrincipal principal,
        AttachInstanceDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        AuthorizationDecision decision = _authorization.Authorize(
            principal,
            new AuthorizationResource(ResourceKind.Node, request.NodeId),
            ServitiumAction.ManageNodes);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<Guid>(decision.Code!);
        }

        PathValidationResult validation = await _pathValidator
            .ValidateWorkingDirectoryAsync(request.NodeId, request.AbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return ApplicationResult.Failure<Guid>(validation.ErrorCode!);
        }

        Operation operation = await _operations
            .CreateAsync(OperationKind.CreateInstance, validation.NormalizedPath!, principal.UserId, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult.Success(operation.Id);
    }
}

/// <summary>登记服务端归档导入长操作。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class ImportServerArchiveUseCase(IAuthorizationService authorization, IOperationTracker operations)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IOperationTracker _operations = operations;

    /// <summary>建立归档导入操作。</summary>
    public async Task<ApplicationResult<Guid>> ExecuteAsync(
        AuthorizationPrincipal principal,
        ImportServerArchiveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        AuthorizationDecision decision = _authorization.Authorize(
            principal,
            new AuthorizationResource(ResourceKind.Node, request.NodeId),
            ServitiumAction.ManageNodes);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<Guid>(decision.Code!);
        }

        Operation operation = await _operations
            .CreateAsync(OperationKind.ImportServerArchive, request.UploadId.ToString("D"), principal.UserId, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult.Success(operation.Id);
    }
}

/// <summary>读取 Prism 原生实例 ZIP 预览。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class PreviewPrismInstanceUseCase(IAuthorizationService authorization, IInstanceImportInspector inspector)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IInstanceImportInspector _inspector = inspector;

    /// <summary>执行安全预览。</summary>
    public async Task<ApplicationResult<PrismInstancePreviewDto>> ExecuteAsync(
        AuthorizationPrincipal principal,
        PreviewPrismInstanceRequest request,
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
            return ApplicationResult.Failure<PrismInstancePreviewDto>(decision.Code!);
        }

        PrismInstancePreviewDto preview = await _inspector
            .PreviewPrismAsync(request.UploadId, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult.Success(preview);
    }
}

/// <summary>登记 Prism ZIP 到 HMCL Release 的转换操作。</summary>
/// <remarks>初始化用例。</remarks>
public sealed class ConvertPrismInstanceUseCase(IAuthorizationService authorization, IOperationTracker operations)
{
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IOperationTracker _operations = operations;

    /// <summary>建立转换操作。</summary>
    public async Task<ApplicationResult<Guid>> ExecuteAsync(
        AuthorizationPrincipal principal,
        ConvertPrismInstanceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        AuthorizationDecision decision = _authorization.Authorize(
            principal,
            new AuthorizationResource(ResourceKind.Channel, request.ChannelId),
            ServitiumAction.ManageDistribution);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<Guid>(decision.Code!);
        }

        Operation operation = await _operations
            .CreateAsync(OperationKind.ConvertPrismInstance, request.UploadId.ToString("D"), principal.UserId, cancellationToken)
            .ConfigureAwait(false);
        return ApplicationResult.Success(operation.Id);
    }
}
