using System;
using System.Linq;

using Servitium.Contracts.Identity;

namespace Servitium.Desktop.Models;

/// <summary>在导航呈现前执行与 Server 授权边界一致的页面级预判。</summary>
internal static class DesktopPageAuthorization
{
    internal static bool CanAccess(string permission, PrincipalDto? principal, Guid? selectedInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        string? role = principal?.User.Role;
        bool administrator = role is "Owner" or "Administrator";
        return permission switch
        {
            "anonymous" or "anonymous-restricted" or "pending-session" => true,
            "authenticated" => principal is not null,
            "administrator" or "instance-grant-dangerous-admin" => administrator,
            "user" => role == "User",
            "instance-grant" => administrator ||
                (role == "User" && selectedInstanceId is Guid instanceId && principal!.GrantedInstanceIds.Contains(instanceId)),
            _ => false,
        };
    }
}
