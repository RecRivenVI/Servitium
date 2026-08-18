using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Identity;
using Servitium.Domain.Auditing;

namespace Servitium.Server.Security;

internal sealed class MutationAuditMiddleware(
    RequestDelegate next,
    IActivityLogger activities,
    IAuditLogger audit,
    IGuidFactory guidFactory,
    IClock clock)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.Ordinal)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Trace,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context).ConfigureAwait(false);
        if (SafeMethods.Contains(context.Request.Method))
        {
            return;
        }

        AuthenticationResult? authentication = RequestAuthentication.Get(context);
        Guid? actorId = authentication?.User.Id;
        string actorName = authentication?.User.UserName ?? "anonymous";
        string action = context.Request.Method + " " + GetRoute(context);
        string resource = context.Request.Path.Value ?? "/";
        DateTimeOffset now = clock.UtcNow;
        string outcome = context.Response.StatusCode < StatusCodes.Status400BadRequest
            ? "success"
            : "http-" + context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (authentication is not null)
        {
            await activities.AppendAsync(
                new(guidFactory.Create(), actorId, actorName, action, resource, now, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["traceId"] = context.TraceIdentifier,
                    ["status"] = context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, outcome, context.Connection.RemoteIpAddress?.ToString()),
                CancellationToken.None).ConfigureAwait(false);
        }

        AuditCategory? category = AuditCategoryFor(context);
        if (category is not null)
        {
            await audit.AppendAsync(
                new AuditLogEntry(
                    guidFactory.Create(),
                    actorId,
                    actorName,
                    category.Value,
                    action,
                    resource,
                    now,
                    outcome,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["traceId"] = context.TraceIdentifier,
                        ["sourceAddress"] = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static AuditCategory? AuditCategoryFor(HttpContext context)
    {
        AuditCategory? responseCategory = context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status429TooManyRequests
            ? context.Response.StatusCode == StatusCodes.Status403Forbidden ? AuditCategory.Authorization : AuditCategory.Authentication
            : null;
        string path = context.Request.Path.Value ?? string.Empty;
        return responseCategory ?? (path.StartsWith("/api/v1/auth", StringComparison.Ordinal)
            ? path.Contains("totp", StringComparison.Ordinal) || path.Contains("api-key", StringComparison.Ordinal) || path.Contains("external-identity", StringComparison.Ordinal)
                ? AuditCategory.Credential
                : AuditCategory.Authentication
            : path.StartsWith("/api/v1/users", StringComparison.Ordinal)
            ? path.Contains("password", StringComparison.Ordinal) || path.Contains("totp", StringComparison.Ordinal) || path.Contains("sessions", StringComparison.Ordinal) ||
                path.Contains("external-identity", StringComparison.Ordinal) || path.Contains("windows-identity", StringComparison.Ordinal)
                ? AuditCategory.Credential
                : path.Contains("delete", StringComparison.Ordinal) || HttpMethods.IsDelete(context.Request.Method)
                    ? AuditCategory.DestructiveAction
                    : AuditCategory.Authorization
            : path.StartsWith("/api/v1/settings", StringComparison.Ordinal)
            ? path.Contains("database/backup", StringComparison.Ordinal) || path.Contains("diagnostics", StringComparison.Ordinal)
                ? AuditCategory.DestructiveAction
                : AuditCategory.SecuritySetting
            : path.Contains("credential", StringComparison.Ordinal) || path.Contains("registration-token", StringComparison.Ordinal)
            ? AuditCategory.Credential
            : path.Contains("working-directory", StringComparison.Ordinal) || path.Contains("/hmcl/", StringComparison.Ordinal)
            ? AuditCategory.DestructiveAction
            : null);
    }

    private static string GetRoute(HttpContext context)
    {
        return context.GetEndpoint() is RouteEndpoint route
            ? route.RoutePattern.RawText ?? context.Request.Path.Value ?? "/"
            : context.Request.Path.Value ?? "/";
    }
}
