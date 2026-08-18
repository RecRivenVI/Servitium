using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Application.Authorization;
using Servitium.Application.Identity;
using Servitium.Contracts;
using Servitium.Infrastructure.Persistence;
using Servitium.Server.Governance;

namespace Servitium.Server.Security;

internal static class RequestSecurityConstants
{
    internal const string SessionCookie = "servitium.session";
    internal const string CsrfCookie = "servitium.csrf";
    internal const string CsrfHeader = "X-CSRF-Token";
    internal const string AuthenticationItem = "Servitium.Authentication";
    internal const string AuthenticationKindItem = "Servitium.AuthenticationKind";
    internal const string CsrfTokenItem = "Servitium.CsrfToken";
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class PublicEndpointAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class RestrictedSessionEndpointAttribute : Attribute;

internal sealed record RequiredAuthorization(ServitiumAction Action, ResourceKind ResourceKind);

internal static class RequestAuthentication
{
    internal static AuthenticationResult? Get(HttpContext context)
    {
        return context.Items.TryGetValue(RequestSecurityConstants.AuthenticationItem, out object? value)
            ? value as AuthenticationResult
            : null;
    }

    internal static AuthorizationPrincipal? GetPrincipal(HttpContext context)
    {
        AuthenticationResult? authentication = Get(context);
        return authentication is null
            ? null
            : new AuthorizationPrincipal(authentication.User.Id, authentication.User.Role, authentication.GrantedInstanceIds);
    }
}

internal sealed class ServitiumAuthenticationMiddleware(RequestDelegate next, IdentityApplicationService identityService)
{
    public async Task InvokeAsync(HttpContext context)
    {
        AuthenticationResult? authentication = null;
        string authenticationKind = string.Empty;
        if (context.Request.Cookies.TryGetValue(RequestSecurityConstants.SessionCookie, out string? sessionToken) &&
            !string.IsNullOrWhiteSpace(sessionToken))
        {
            authentication = await identityService.AuthenticateSessionAsync(sessionToken, context.RequestAborted).ConfigureAwait(false);
            authenticationKind = "session";
        }
        else if (context.Request.Headers.Authorization.ToString() is string authorization &&
                 authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal))
            {
                await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden).ConfigureAwait(false);
                return;
            }

            authentication = await identityService.AuthenticateApiKeyAsync(authorization[7..].Trim(), context.RequestAborted).ConfigureAwait(false);
            authenticationKind = "api-key";
        }

        if (authentication is not null)
        {
            context.Items[RequestSecurityConstants.AuthenticationItem] = authentication;
            context.Items[RequestSecurityConstants.AuthenticationKindItem] = authenticationKind;
        }

        await next(context).ConfigureAwait(false);
    }
}

internal sealed class ServitiumAuthorizationMiddleware(
    RequestDelegate next,
    IAuthorizationService authorizationService,
    RuntimeSettings runtimeSettings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<PublicEndpointAttribute>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        AuthorizationPrincipal? principal = RequestAuthentication.GetPrincipal(context);
        if (principal is null)
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated).ConfigureAwait(false);
            return;
        }

        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        if (authentication.Session.IsRestricted && endpoint.Metadata.GetMetadata<RestrictedSessionEndpointAttribute>() is null)
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.TotpRequired).ConfigureAwait(false);
            return;
        }

        RequiredAuthorization? required = endpoint.Metadata.GetMetadata<RequiredAuthorization>();
        if (required is not null)
        {
            if (principal.Role == Domain.Identity.Role.User && required.Action == ServitiumAction.ManageFiles && !runtimeSettings.UserFileManagementEnabled)
            {
                await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden).ConfigureAwait(false);
                return;
            }

            Guid? resourceId = TryReadResourceId(context, required.ResourceKind);
            AuthorizationDecision decision = authorizationService.Authorize(principal, new(required.ResourceKind, resourceId), required.Action);
            if (!decision.Allowed)
            {
                await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden, decision.Code ?? ErrorCodes.Forbidden).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Guid? TryReadResourceId(HttpContext context, ResourceKind kind)
    {
        string key = kind switch
        {
            ResourceKind.Instance => "instanceId",
            ResourceKind.Node => "nodeId",
            ResourceKind.User => "userId",
            ResourceKind.Channel => "channelId",
            ResourceKind.Operation => "operationId",
            ResourceKind.Global => string.Empty,
            _ => string.Empty,
        };
        return key.Length > 0 && context.Request.RouteValues.TryGetValue(key, out object? raw) && Guid.TryParse(raw?.ToString(), out Guid id)
            ? id
            : null;
    }
}

internal sealed class CsrfProtectionMiddleware(RequestDelegate next)
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
        EnsureCsrfCookie(context);
        bool isApiKey = string.Equals(context.Items[RequestSecurityConstants.AuthenticationKindItem]?.ToString(), "api-key", StringComparison.Ordinal);
        bool hubHandshake = context.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal);
        if ((!SafeMethods.Contains(context.Request.Method) || hubHandshake) && !isApiKey && !HasValidToken(context))
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.CsrfTokenMissing).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static void EnsureCsrfCookie(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(RequestSecurityConstants.CsrfCookie))
        {
            context.Items[RequestSecurityConstants.CsrfTokenItem] = context.Request.Cookies[RequestSecurityConstants.CsrfCookie];
            return;
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        context.Response.Cookies.Append(
            RequestSecurityConstants.CsrfCookie,
            token,
            new CookieOptions
            {
                HttpOnly = false,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = true,
                Path = "/",
            });
        context.Items[RequestSecurityConstants.CsrfTokenItem] = token;
    }

    private static bool HasValidToken(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(RequestSecurityConstants.CsrfCookie, out string? cookie) ||
            string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        string supplied = context.Request.Headers[RequestSecurityConstants.CsrfHeader].ToString();
        if (supplied.Length == 0 && context.Request.Query.TryGetValue("csrf", out Microsoft.Extensions.Primitives.StringValues queryValue))
        {
            supplied = queryValue.ToString();
        }

        return supplied.Length == cookie.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(supplied));
    }
}

internal sealed class LoginThrottle(ControlPlaneStore store, RuntimeSettings runtimeSettings)
{
    private readonly ConcurrentDictionary<string, FailureState> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _addresses = new(StringComparer.Ordinal);

    internal async Task<TimeSpan?> GetDelayAsync(string userName, IPAddress? address, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string normalized = userName.Trim().ToUpperInvariant();
        if (_users.TryGetValue(normalized, out FailureState? failure) && failure.BlockedUntil > now)
        {
            return failure.BlockedUntil - now;
        }

        if (address is not null && _addresses.TryGetValue(address.ToString(), out Queue<DateTimeOffset>? attempts))
        {
            lock (attempts)
            {
                RemoveExpired(attempts, now);
                if (attempts.Count >= runtimeSettings.IpFailureLimit)
                {
                    return TimeSpan.FromMinutes(runtimeSettings.IpBanMinutes);
                }
            }
        }

        return address is not null && await store.FindActiveIpBanAsync(address.ToString(), cancellationToken).ConfigureAwait(false) is { } bannedUntil
            ? bannedUntil - now
            : null;
    }

    internal async Task RecordFailureAsync(string userName, IPAddress? address, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string normalized = userName.Trim().ToUpperInvariant();
        _ = _users.AddOrUpdate(
            normalized,
            _ => new FailureState(1, now),
            (_, previous) =>
            {
                int failures = previous.Failures + 1;
                int limit = runtimeSettings.UserFailureLimit;
                int delayMinutes = failures < limit ? 0 : Math.Min(16, 1 << Math.Min(failures - limit, 4));
                return new FailureState(failures, now.AddMinutes(delayMinutes));
            });
        if (address is not null)
        {
            bool banRequired = false;
            Queue<DateTimeOffset> attempts = _addresses.GetOrAdd(address.ToString(), _ => new Queue<DateTimeOffset>());
            lock (attempts)
            {
                RemoveExpired(attempts, now);
                attempts.Enqueue(now);
                if (attempts.Count >= runtimeSettings.IpFailureLimit)
                {
                    banRequired = true;
                }
            }

            if (banRequired)
            {
                await store.UpsertIpBanAsync(address.ToString(), now.AddMinutes(runtimeSettings.IpBanMinutes), "login-failure-threshold", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal void ClearAddress(string address)
    {
        _ = _addresses.TryRemove(address, out _);
    }

    internal void RecordSuccess(string userName)
    {
        _ = _users.TryRemove(userName.Trim().ToUpperInvariant(), out _);
    }

    private void RemoveExpired(Queue<DateTimeOffset> attempts, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddMinutes(-runtimeSettings.IpFailureWindowMinutes);
        while (attempts.TryPeek(out DateTimeOffset attempt) && attempt < cutoff)
        {
            _ = attempts.Dequeue();
        }
    }

    private sealed record FailureState(int Failures, DateTimeOffset BlockedUntil);
}
