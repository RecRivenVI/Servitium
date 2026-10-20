using System;
using System.Collections.Generic;

using Servitium.Domain.Identity;

namespace Servitium.Application.Authorization;

/// <summary>定义授权资源类型。</summary>
public enum ResourceKind
{
    /// <summary>全局资源。</summary>
    Global,
    /// <summary>节点。</summary>
    Node,
    /// <summary>实例。</summary>
    Instance,
    /// <summary>用户。</summary>
    User,
    /// <summary>HMCL Channel。</summary>
    Channel,
    /// <summary>长操作。</summary>
    Operation,
}

/// <summary>定义受保护动作。</summary>
public enum ServitiumAction
{
    /// <summary>查看。</summary>
    View,
    /// <summary>控制实例。</summary>
    ControlInstance,
    /// <summary>使用控制台。</summary>
    UseConsole,
    /// <summary>管理文件。</summary>
    ManageFiles,
    /// <summary>管理配置。</summary>
    ManageConfigurations,
    /// <summary>管理模组。</summary>
    ManageMods,
    /// <summary>管理计划任务。</summary>
    ManageSchedules,
    /// <summary>修改实例非危险设置。</summary>
    EditSafeInstanceSettings,
    /// <summary>执行危险实例设置。</summary>
    EditDangerousInstanceSettings,
    /// <summary>管理节点。</summary>
    ManageNodes,
    /// <summary>管理用户。</summary>
    ManageUsers,
    /// <summary>修改 Owner。</summary>
    ModifyOwner,
    /// <summary>移交 Owner。</summary>
    TransferOwner,
    /// <summary>管理分发。</summary>
    ManageDistribution,
    /// <summary>取消长操作。</summary>
    CancelOperation,
}

/// <summary>授权资源。</summary>
/// <param name="Kind">资源类型。</param>
/// <param name="Id">可选资源标识。</param>
public sealed record AuthorizationResource(ResourceKind Kind, Guid? Id = null);

/// <summary>授权主体。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="Role">角色。</param>
/// <param name="GrantedInstanceIds">实例白名单。</param>
public sealed record AuthorizationPrincipal(Guid UserId, Role Role, IReadOnlySet<Guid> GrantedInstanceIds);

/// <summary>授权结果。</summary>
/// <param name="Allowed">是否允许。</param>
/// <param name="Code">拒绝错误码。</param>
public sealed record AuthorizationDecision(bool Allowed, string? Code)
{
    /// <summary>允许。</summary>
    public static AuthorizationDecision Permit { get; } = new(true, null);

    /// <summary>拒绝。</summary>
    public static AuthorizationDecision Deny(string code)
    {
        return new(false, code);
    }
}

/// <summary>所有用例和端点共享的单一授权入口。</summary>
public interface IAuthorizationService
{
    /// <summary>判定主体对资源执行动作的权限。</summary>
    AuthorizationDecision Authorize(AuthorizationPrincipal principal, AuthorizationResource resource, ServitiumAction action);
}

/// <summary>实现固定角色与实例白名单策略。</summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private static readonly HashSet<ServitiumAction> UserInstanceActions =
    [
        ServitiumAction.View,
        ServitiumAction.ControlInstance,
        ServitiumAction.UseConsole,
        ServitiumAction.ManageFiles,
        ServitiumAction.ManageConfigurations,
        ServitiumAction.ManageMods,
        ServitiumAction.ManageSchedules,
        ServitiumAction.EditSafeInstanceSettings,
    ];

    /// <inheritdoc />
    public AuthorizationDecision Authorize(
        AuthorizationPrincipal principal,
        AuthorizationResource resource,
        ServitiumAction action)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(resource);

        return principal.Role switch
        {
            Role.Disabled => AuthorizationDecision.Deny("account-disabled"),
            Role.Owner => AuthorizationDecision.Permit,
            Role.Administrator when action is ServitiumAction.ModifyOwner or ServitiumAction.TransferOwner =>
                AuthorizationDecision.Deny("forbidden"),
            Role.Administrator => AuthorizationDecision.Permit,
            Role.User when resource.Kind == ResourceKind.Instance &&
                           resource.Id is Guid instanceId &&
                           principal.GrantedInstanceIds.Contains(instanceId) &&
                           UserInstanceActions.Contains(action) => AuthorizationDecision.Permit,
            _ => AuthorizationDecision.Deny("forbidden"),
        };
    }
}

/// <summary>标记不需要已认证授权的用例。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuthorizationNotRequiredAttribute : Attribute;
