using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Results;
using Servitium.Application.Identity;
using Servitium.Contracts;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Identity;
using Servitium.Domain.Primitives;
using Servitium.Domain.Identity;
using Servitium.Server.Hosting;
using Servitium.Server.OpenApi;
using Servitium.Server.Realtime;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class IdentityEndpoints
{
    internal static void MapIdentityEndpoints(this WebApplication app)
    {
        RouteGroupBuilder auth = app.MapGroup("/api/v1/auth");
        _ = auth.MapGet("/bootstrap", GetBootstrapAsync).ProducesJson<AuthenticationBootstrapDto>().WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapPost("/bootstrap", InitializeAsync).ProducesJson<UserDto>(StatusCodes.Status201Created).WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapGet("/state", GetStateAsync).ProducesJson<AuthenticationStateDto>().WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapGet("/csrf", GetCsrf).ProducesJson<CsrfTokenDto>().WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapPost("/initialize", InitializeAsync).ProducesJson<UserDto>(StatusCodes.Status201Created).WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapPost("/login", SignInAsync)
            .ProducesJson<PrincipalDto>()
            .ProducesJson<AuthenticationChallengeDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapPost("/login/totp", CompleteTotpSignInAsync).ProducesJson<PrincipalDto>().WithMetadata(new RestrictedSessionEndpointAttribute());
        _ = auth.MapPost("/logout", SignOutAsync).ProducesEmpty();
        _ = auth.MapGet("/me", GetCurrentUser).ProducesJson<PrincipalDto>();
        _ = auth.MapPatch("/me", UpdateProfileAsync).ProducesJson<UserDto>();
        _ = auth.MapPost("/me/password", ChangePasswordAsync).ProducesEmpty();
        _ = auth.MapGet("/me/sessions", ListSessionsAsync).ProducesJson<CursorPage<SessionDto>>();
        _ = auth.MapDelete("/me/sessions/{sessionId:guid}", RevokeSessionAsync).ProducesEmpty();
        _ = auth.MapPost("/me/totp", BeginTotpAsync).ProducesJson<TotpSetupDto>();
        _ = auth.MapPost("/me/totp/confirm", ConfirmTotpAsync).ProducesJson<TotpRecoveryCodesDto>();
        _ = auth.MapDelete("/me/totp", RemoveTotpAsync).ProducesEmpty();
        _ = auth.MapGet("/me/api-keys", ListApiKeysAsync).ProducesJson<CursorPage<ApiKeyDto>>();
        _ = auth.MapPost("/me/api-keys", CreateApiKeyAsync).ProducesJson<CreatedApiKeyDto>(StatusCodes.Status201Created);
        _ = auth.MapDelete("/me/api-keys/{apiKeyId:guid}", RevokeApiKeyAsync).ProducesEmpty();
        _ = auth.MapPost("/api-keys", CreateApiKeyAsync).ProducesJson<CreatedApiKeyDto>(StatusCodes.Status201Created);
        _ = auth.MapPost("/local-session", ExchangeLocalSessionAsync).ProducesJson<PrincipalDto>().WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapGet("/oidc/authorize", BeginOidcAsync).ProducesEmpty(StatusCodes.Status302Found).WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapGet("/oidc/callback", CompleteOidcAsync).ProducesEmpty(StatusCodes.Status302Found).WithMetadata(new PublicEndpointAttribute());
        _ = auth.MapPost("/oidc/bind", BindOidcAsync).ProducesJson<OidcBindingStartDto>().ProducesEmpty();
        _ = auth.MapDelete("/me/external-identity", RemoveOwnExternalIdentityAsync).ProducesEmpty();

        RouteGroupBuilder users = app.MapGroup("/api/v1/users")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageUsers, ResourceKind.User));
        _ = users.MapGet("/", ListUsersAsync).ProducesJson<CursorPage<UserDto>>();
        _ = users.MapPost("/", CreateUserAsync).ProducesJson<UserDto>(StatusCodes.Status201Created);
        _ = users.MapGet("/overview", GetUserOverviewAsync).ProducesJson<UserOverviewDto>();
        _ = users.MapPost("/batch/disable", DisableUsersBatchAsync).ProducesJson<BatchUsersResultDto>();
        _ = users.MapPost("/batch/delete", DeleteUsersBatchAsync).ProducesJson<BatchUsersResultDto>();
        _ = users.MapGet("/{userId:guid}", GetUserAsync).ProducesJson<UserDto>();
        _ = users.MapPatch("/{userId:guid}", UpdateUserAsync).ProducesJson<UserDto>();
        _ = users.MapPost("/{userId:guid}/password", ResetPasswordAsync).ProducesEmpty();
        _ = users.MapPost("/{userId:guid}/disable", DisableUserAsync).ProducesJson<UserDto>();
        _ = users.MapPost("/{userId:guid}/enable", EnableUserAsync).ProducesJson<UserDto>();
        _ = users.MapDelete("/{userId:guid}", DeleteUserAsync).ProducesEmpty();
        _ = users.MapDelete("/{userId:guid}/totp", ResetUserTotpAsync).ProducesEmpty();
        _ = users.MapDelete("/{userId:guid}/sessions", RevokeUserSessionsAsync).ProducesEmpty();
        _ = users.MapDelete("/{userId:guid}/external-identity", RemoveUserExternalIdentityAsync).ProducesEmpty();
        _ = users.MapGet("/{userId:guid}/grants", GetGrantsAsync).ProducesJson<InstanceGrantsDto>();
        _ = users.MapPut("/{userId:guid}/grants", ReplaceGrantsAsync).ProducesJson<InstanceGrantsDto>();
        _ = users.MapPost("/{userId:guid}/grants", AddGrantsAsync).ProducesJson<InstanceGrantsDto>();
        _ = users.MapDelete("/{userId:guid}/grants/{instanceId:guid}", RemoveGrantAsync).ProducesEmpty();
        _ = users.MapGet("/{userId:guid}/windows-identity", GetWindowsIdentityAsync).ProducesJson<WindowsIdentityBindingDto>();
        _ = users.MapPut("/{userId:guid}/windows-identity", SetWindowsIdentityAsync).ProducesJson<WindowsIdentityBindingDto>();
    }

    private static async Task<IResult> GetBootstrapAsync(
        IdentityApplicationService identityService,
        OidcLoginService oidc,
        CancellationToken cancellationToken)
    {
        bool initialized = await identityService.IsInitializedAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(await oidc.GetBootstrapAsync(initialized, cancellationToken).ConfigureAwait(false));
    }

    private static IResult GetCsrf(HttpContext context)
    {
        string token = context.Items[RequestSecurityConstants.CsrfTokenItem]?.ToString() ?? string.Empty;
        return Results.Ok(new { token });
    }

    private static async Task<IResult> GetStateAsync(
        HttpContext context,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        bool initialized = await identityService.IsInitializedAsync(cancellationToken).ConfigureAwait(false);
        AuthenticationResult? authentication = RequestAuthentication.Get(context);
        return Results.Ok(new AuthenticationStateDto(
            initialized,
            authentication is not null,
            authentication is null ? null : ToPrincipal(authentication)));
    }

    private static async Task<IResult> InitializeAsync(
        HttpContext context,
        InitializeRequest request,
        IdentityApplicationService identityService,
        ServerHostOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress) &&
            (string.IsNullOrEmpty(options.InstallToken) || !FixedTimeEquals(options.InstallToken, request.InstallToken)))
        {
            return Problem(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, context);
        }

        ApplicationResult<StoredUser> result = await identityService.InitializeAsync(
            request.UserName,
            request.DisplayName,
            request.Password,
            request.Language,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? Results.Created("/api/v1/auth/state", ToUser(result.Value!))
            : Problem(
                result.ErrorCode == ErrorCodes.AlreadyInitialized
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest,
                result.ErrorCode!,
                context);
    }

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        SignInRequest request,
        IdentityApplicationService identityService,
        LoginThrottle throttle,
        IClock clock,
        CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = await throttle.GetDelayAsync(request.UserName, context.Connection.RemoteIpAddress, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (retryAfter is not null)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)retryAfter.Value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Problem(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, context);
        }

        ApplicationResult<AuthenticationResult> result = await identityService.SignInAsync(
            request.UserName,
            request.Password,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await throttle.RecordFailureAsync(request.UserName, context.Connection.RemoteIpAddress, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            int status = result.ErrorCode is ErrorCodes.TotpRequired or ErrorCodes.TotpInvalid
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status401Unauthorized;
            return Problem(status, result.ErrorCode!, context);
        }

        throttle.RecordSuccess(request.UserName);
        AuthenticationResult authentication = result.Value!;
        AppendSessionCookie(context, authentication);
        return authentication.Session.IsRestricted
            ? Results.Json(new AuthenticationChallengeDto(ErrorCodes.TotpRequired), statusCode: StatusCodes.Status202Accepted)
            : Results.Ok(ToPrincipal(authentication));
    }

    private static async Task<IResult> CompleteTotpSignInAsync(
        HttpContext context,
        CompleteTotpSignInRequest request,
        IdentityApplicationService identityService,
        LoginThrottle throttle,
        IClock clock,
        CancellationToken cancellationToken)
    {
        AuthenticationResult pending = RequestAuthentication.Get(context)!;
        TimeSpan? retryAfter = await throttle.GetDelayAsync(pending.User.UserName, context.Connection.RemoteIpAddress, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (retryAfter is not null)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)retryAfter.Value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Problem(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, context);
        }

        ApplicationResult<AuthenticationResult> result = await identityService.CompleteTotpSignInAsync(pending, request.Code, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await throttle.RecordFailureAsync(pending.User.UserName, context.Connection.RemoteIpAddress, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return Problem(StatusCodes.Status401Unauthorized, result.ErrorCode!, context);
        }

        throttle.RecordSuccess(pending.User.UserName);
        AuthenticationResult authentication = result.Value!;
        AppendSessionCookie(context, authentication);
        return Results.Ok(ToPrincipal(authentication));
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext context,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        await store.RevokeSessionAsync(authentication.Session.Id, cancellationToken).ConfigureAwait(false);
        DeleteSessionCookie(context);
        return Results.NoContent();
    }

    private static IResult GetCurrentUser(HttpContext context)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        return Results.Ok(ToPrincipal(authentication));
    }

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context,
        UpdateProfileRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        ApplicationResult<StoredUser> result = await identityService.UpdateProfileAsync(
            authentication.User.Id,
            request.DisplayName,
            request.Language,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Results.Ok(ToUser(result.Value!)) : Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        ChangePasswordRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        ApplicationResult<bool> result = await identityService.ChangePasswordAsync(
            authentication.User.Id,
            authentication.Session.Id,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(result.ErrorCode == ErrorCodes.Unauthenticated ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest, result.ErrorCode!, context);
        }

        DeleteSessionCookie(context);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext context,
        string? cursor,
        int? limit,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        IReadOnlyList<StoredSession> sessions = await identityService.ListSessionsAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        SessionDto[] response = [.. sessions.Select(session => new SessionDto(session.Id, session.CreatedAt, session.LastSeenAt, session.ExpiresAt, session.Id == authentication.Session.Id))];
        return Results.Ok(CursorPaginator.Page(
            response,
            session => (DateTimeOffset.MaxValue - session.LastSeenAt).Ticks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture),
            session => session.Id,
            cursor,
            limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> RevokeSessionAsync(
        HttpContext context,
        Guid sessionId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        IReadOnlyList<StoredSession> sessions = await store.ListSessionsAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        if (!sessions.Any(session => session.Id == sessionId))
        {
            return Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, context);
        }

        await store.RevokeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionId == authentication.Session.Id)
        {
            DeleteSessionCookie(context);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> BeginTotpAsync(
        HttpContext context,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        ApplicationResult<string> result = await identityService.BeginTotpSetupAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
        }

        string label = Uri.EscapeDataString("Servitium:" + authentication.User.UserName);
        string issuer = Uri.EscapeDataString("Servitium");
        Uri uri = new($"otpauth://totp/{label}?secret={result.Value}&issuer={issuer}&algorithm=SHA1&digits=6&period=30", UriKind.Absolute);
        return Results.Ok(new TotpSetupDto(result.Value!, uri));
    }

    private static async Task<IResult> ConfirmTotpAsync(
        HttpContext context,
        ConfirmTotpRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        ApplicationResult<IReadOnlyList<string>> result = await identityService.ConfirmTotpAsync(authentication.User.Id, request.Code, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
        }

        DeleteSessionCookie(context);
        return Results.Ok(new TotpRecoveryCodesDto(result.Value!));
    }

    private static async Task<IResult> RemoveTotpAsync(
        HttpContext context,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        ApplicationResult<bool> result = await identityService.RemoveTotpAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
        }

        DeleteSessionCookie(context);
        return Results.NoContent();
    }

    private static async Task<IResult> ListApiKeysAsync(
        HttpContext context,
        string? cursor,
        int? limit,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        IReadOnlyList<StoredApiKey> keys = await identityService.ListApiKeysAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        ApiKeyDto[] response = [.. keys.Select(key => new ApiKeyDto(key.Id, key.Name, key.Scope, key.CreatedAt, key.ExpiresAt, key.LastUsedAt, key.IsRevoked))];
        return Results.Ok(CursorPaginator.Page(response, key => key.Name.ToUpperInvariant(), key => key.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        HttpContext context,
        Guid apiKeyId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        IReadOnlyList<StoredApiKey> keys = await store.ListApiKeysAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        if (!keys.Any(key => key.Id == apiKeyId))
        {
            return Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, context);
        }

        await store.RevokeApiKeyAsync(apiKeyId, authentication.User.Id, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ExchangeLocalSessionAsync(
        HttpContext context,
        LocalSessionExchangeRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        ApplicationResult<AuthenticationResult> result = await identityService.ExchangeLocalSessionAsync(
            request.Token,
            request.CodeVerifier,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status401Unauthorized, result.ErrorCode!, context);
        }

        AppendSessionCookie(context, result.Value!);
        return Results.Ok(ToPrincipal(result.Value!));
    }

    private static async Task<IResult> BeginOidcAsync(
        HttpContext context,
        string? returnUrl,
        bool bind,
        string? desktopCallback,
        string? desktopCodeChallenge,
        OidcLoginService oidc,
        CancellationToken cancellationToken)
    {
        AuthenticationResult? authentication = RequestAuthentication.Get(context);
        if (bind && authentication is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, context);
        }

        try
        {
            Uri callback = AbsoluteEndpoint(context, "/api/v1/auth/oidc/callback");
            Uri? safeReturn = ResolveDesktopReturnUrl(desktopCallback, desktopCodeChallenge)
                ?? ResolveReturnUrl(context, returnUrl);
            if (bind && desktopCallback is not null)
            {
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, context);
            }
            OidcAuthorizationResult result = await oidc.BeginAsync(callback, safeReturn, bind ? authentication!.User.Id : null, cancellationToken).ConfigureAwait(false);
            return Results.Redirect(result.AuthorizationUri.AbsoluteUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DomainException or UriFormatException)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.OidcDiscoveryFailed, context);
        }
    }

    private static async Task<IResult> CompleteOidcAsync(
        HttpContext context,
        string code,
        string state,
        OidcLoginService oidc,
        CancellationToken cancellationToken)
    {
        OidcCompletionResult result = await oidc.CompleteAsync(code, state, AbsoluteEndpoint(context, "/api/v1/auth/oidc/callback"), cancellationToken).ConfigureAwait(false);
        if (result.ErrorCode is not null)
        {
            return Problem(StatusCodes.Status401Unauthorized, result.ErrorCode, context);
        }

        if (result.Authentication is not null)
        {
            AppendSessionCookie(context, result.Authentication);
        }

        return Results.Redirect(result.ReturnUrl?.AbsoluteUri ?? AbsoluteEndpoint(context, "/").AbsoluteUri);
    }

    private static async Task<IResult> BindOidcAsync(
        HttpContext context,
        BindExternalIdentityRequest request,
        OidcLoginService oidc,
        CancellationToken cancellationToken)
    {
        if (request.DesktopCallback is not null)
        {
            Uri returnUrl = ValidateOidcBindingCallback(request.DesktopCallback);
            OidcAuthorizationResult authorization = await oidc.BeginAsync(
                AbsoluteEndpoint(context, "/api/v1/auth/oidc/callback"),
                returnUrl,
                RequestAuthentication.Get(context)!.User.Id,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new OidcBindingStartDto(authorization.AuthorizationUri));
        }

        if (string.IsNullOrWhiteSpace(request.AuthorizationCode) || string.IsNullOrWhiteSpace(request.State))
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, context);
        }

        OidcCompletionResult result = await oidc.CompleteAsync(
            request.AuthorizationCode,
            request.State,
            AbsoluteEndpoint(context, "/api/v1/auth/oidc/callback"),
            cancellationToken).ConfigureAwait(false);
        return result.Bound ? Results.NoContent() : Problem(StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.OidcSubjectConflict, context);
    }

    private static async Task<IResult> RemoveOwnExternalIdentityAsync(
        HttpContext context,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        AuthenticationResult authentication = RequestAuthentication.Get(context)!;
        await store.RemoveExternalIdentityAsync(authentication.User.Id, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext context,
        IIdentityStore store,
        string? cursor,
        int? limit,
        string? role,
        string? name,
        CancellationToken cancellationToken)
    {
        List<StoredUser> users = [];
        Guid? after = null;
        while (true)
        {
            IReadOnlyList<StoredUser> batch = await store.ListUsersAsync(after, 200, cancellationToken).ConfigureAwait(false);
            users.AddRange(batch);
            if (batch.Count < 200)
            {
                break;
            }

            after = batch[^1].Id;
        }

        IEnumerable<StoredUser> query = users;
        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(user => string.Equals(user.Role.ToString(), role, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(user => user.UserName.Contains(name, StringComparison.OrdinalIgnoreCase) || user.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        List<UserDto> summaries = [];
        foreach (StoredUser user in query)
        {
            summaries.Add(await ToUserAsync(user, store, cancellationToken).ConfigureAwait(false));
        }

        return Results.Ok(CursorPaginator.Page(summaries, user => user.UserName.ToUpperInvariant(), user => user.Id, cursor, limit ?? 50));
    }

    private static async Task<UserDto> ToUserAsync(StoredUser user, IIdentityStore store, CancellationToken cancellationToken)
    {
        StoredExternalIdentity? externalIdentity = await store.FindExternalIdentityAsync(user.Id, cancellationToken).ConfigureAwait(false);
        StoredWindowsIdentity? windowsIdentity = await store.FindWindowsIdentityAsync(user.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<Guid> grants = await store.ListInstanceGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new UserDto(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Role.ToString(),
            user.Role == Role.Disabled,
            user.LastSignedInAt,
            user.CreatedAt,
            externalIdentity is not null,
            grants.Count,
            user.TotpConfirmed,
            windowsIdentity is not null);
    }

    private static async Task<IResult> CreateUserAsync(
        HttpContext context,
        CreateUserRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Role, true, out Role role))
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, context);
        }

        AuthorizationPrincipal actor = RequestAuthentication.GetPrincipal(context)!;
        ApplicationResult<StoredUser> result = await identityService.CreateUserAsync(
            actor,
            request.UserName,
            request.DisplayName,
            request.Password,
            role,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? Results.Created($"/api/v1/users/{result.Value!.Id:D}", ToUser(result.Value))
            : Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
    }

    private static async Task<IResult> GetUserOverviewAsync(
        IdentityApplicationService identityService,
        IIdentityStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        (int total, int enabled, int disabled, int withTotp, int withExternalIdentity) = await identityService.GetUserOverviewAsync(cancellationToken).ConfigureAwait(false);
        List<StoredUser> users = [];
        Guid? cursor = null;
        do
        {
            IReadOnlyList<StoredUser> page = await store.ListUsersAsync(cursor, 200, cancellationToken).ConfigureAwait(false);
            users.AddRange(page.Where(user => !user.IsDeleted));
            cursor = page.Count == 200 ? page[^1].Id : null;
        }
        while (cursor is not null);

        Dictionary<string, int> roleCounts = Enum.GetValues<Role>()
            .ToDictionary(
                role => role.ToString(),
                role => users.Count(user => user.Role == role),
                StringComparer.Ordinal);
        int activeSessions = await store.CountActiveSessionsAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new UserOverviewDto(total, enabled, disabled, withTotp, withExternalIdentity, roleCounts, activeSessions));
    }

    private static async Task<IResult> GetUserAsync(
        HttpContext context,
        Guid userId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return user is null || user.IsDeleted ? Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, context) : Results.Ok(ToUser(user));
    }

    private static async Task<IResult> UpdateUserAsync(
        HttpContext context,
        Guid userId,
        UpdateUserRequest request,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Role, true, out Role role))
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, context);
        }

        ApplicationResult<StoredUser> result = await identityService.UpdateUserAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, request.DisplayName, role, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "user-updated", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.Ok(ToUser(result.Value!)) : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> ResetPasswordAsync(
        HttpContext context,
        Guid userId,
        ResetPasswordRequest request,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        ApplicationResult<bool> result = await identityService.ResetPasswordAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, request.Password, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "password-reset", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.NoContent() : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static Task<IResult> DisableUserAsync(
        HttpContext context,
        Guid userId,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        return SetUserDisabledAsync(context, userId, true, identityService, changes, cancellationToken);
    }

    private static Task<IResult> EnableUserAsync(
        HttpContext context,
        Guid userId,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        return SetUserDisabledAsync(context, userId, false, identityService, changes, cancellationToken);
    }

    private static async Task<IResult> SetUserDisabledAsync(
        HttpContext context,
        Guid userId,
        bool disabled,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        ApplicationResult<StoredUser> result = await identityService.SetUserDisabledAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, disabled, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, disabled ? "user-disabled" : "user-enabled", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.Ok(ToUser(result.Value!)) : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> DeleteUserAsync(
        HttpContext context,
        Guid userId,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        ApplicationResult<bool> result = await identityService.DeleteUserAsync(RequestAuthentication.GetPrincipal(context)!, userId, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "user-deleted", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.NoContent() : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static Task<IResult> DisableUsersBatchAsync(
        HttpContext context,
        BatchUsersRequest request,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        return ProcessUsersBatchAsync(context, request, delete: false, identityService, changes, cancellationToken);
    }

    private static Task<IResult> DeleteUsersBatchAsync(
        HttpContext context,
        BatchUsersRequest request,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        return ProcessUsersBatchAsync(context, request, delete: true, identityService, changes, cancellationToken);
    }

    private static async Task<IResult> ProcessUsersBatchAsync(
        HttpContext context,
        BatchUsersRequest request,
        bool delete,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        if (request.UserIds is null || request.UserIds.Count is < 1 or > 100 ||
            request.UserIds.Any(id => id == Guid.Empty) || request.UserIds.Distinct().Count() != request.UserIds.Count)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, context);
        }

        AuthorizationPrincipal actor = RequestAuthentication.GetPrincipal(context)!;
        List<BatchUserResultItemDto> items = [];
        foreach (Guid userId in request.UserIds)
        {
            string? errorCode;
            if (delete)
            {
                ApplicationResult<bool> result = await identityService.DeleteUserAsync(actor, userId, cancellationToken).ConfigureAwait(false);
                errorCode = result.Succeeded ? null : result.ErrorCode;
            }
            else
            {
                ApplicationResult<StoredUser> result = await identityService.SetUserDisabledAsync(actor, userId, disabled: true, cancellationToken).ConfigureAwait(false);
                errorCode = result.Succeeded ? null : result.ErrorCode;
            }

            bool succeeded = errorCode is null;
            items.Add(new BatchUserResultItemDto(userId, succeeded, errorCode));
            if (succeeded)
            {
                await changes.InvalidateAsync(userId, delete ? "user-deleted" : "user-disabled", cancellationToken).ConfigureAwait(false);
            }
        }

        return Results.Ok(new BatchUsersResultDto(items));
    }

    private static async Task<IResult> ResetUserTotpAsync(
        HttpContext context,
        Guid userId,
        IIdentityStore store,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user?.Role == Role.Owner && RequestAuthentication.Get(context)!.User.Id != userId)
        {
            return Problem(StatusCodes.Status409Conflict, ErrorCodes.OwnerImmutable, context);
        }

        ApplicationResult<bool> result = await identityService.RemoveTotpAsync(userId, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "totp-reset", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.NoContent() : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> RevokeUserSessionsAsync(
        Guid userId,
        IIdentityStore store,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        await changes.InvalidateAsync(userId, "sessions-revoked", cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveUserExternalIdentityAsync(
        Guid userId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        await store.RemoveExternalIdentityAsync(userId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> GetGrantsAsync(
        Guid userId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> grants = await store.ListInstanceGrantsAsync(userId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { instanceIds = grants.OrderBy(id => id) });
    }

    private static async Task<IResult> ReplaceGrantsAsync(
        HttpContext context,
        Guid userId,
        ReplaceInstanceGrantsRequest request,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> grants = [.. request.InstanceIds];
        ApplicationResult<IReadOnlySet<Guid>> result = await identityService.ReplaceGrantsAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, grants, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "grants-replaced", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.Ok(new { instanceIds = result.Value!.OrderBy(id => id) }) : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> AddGrantsAsync(
        HttpContext context,
        Guid userId,
        AddInstanceGrantsRequest request,
        IIdentityStore store,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> grants = [.. await store.ListInstanceGrantsAsync(userId, cancellationToken).ConfigureAwait(false)];
        grants.UnionWith(request.InstanceIds);
        ApplicationResult<IReadOnlySet<Guid>> result = await identityService.ReplaceGrantsAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, grants, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "grants-added", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.Ok(new { instanceIds = result.Value!.OrderBy(id => id) }) : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> RemoveGrantAsync(
        HttpContext context,
        Guid userId,
        Guid instanceId,
        IIdentityStore store,
        IdentityApplicationService identityService,
        AuthorizationChangeCoordinator changes,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> grants = [.. await store.ListInstanceGrantsAsync(userId, cancellationToken).ConfigureAwait(false)];
        _ = grants.Remove(instanceId);
        ApplicationResult<IReadOnlySet<Guid>> result = await identityService.ReplaceGrantsAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, grants, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await changes.InvalidateAsync(userId, "grant-removed", cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? Results.NoContent() : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> GetWindowsIdentityAsync(
        HttpContext context,
        Guid userId,
        IIdentityStore store,
        CancellationToken cancellationToken)
    {
        StoredWindowsIdentity? identity = await store.FindWindowsIdentityAsync(userId, cancellationToken).ConfigureAwait(false);
        return identity is null ? Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, context) : Results.Ok(new WindowsIdentityBindingDto(identity.Sid));
    }

    private static async Task<IResult> SetWindowsIdentityAsync(
        HttpContext context,
        Guid userId,
        WindowsIdentityBindingDto request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        ApplicationResult<StoredWindowsIdentity> result = await identityService.SetWindowsIdentityAsync(
            RequestAuthentication.GetPrincipal(context)!, userId, request.Sid, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Results.Ok(new WindowsIdentityBindingDto(result.Value!.Sid)) : Problem(StatusFor(result.ErrorCode!), result.ErrorCode!, context);
    }

    private static async Task<IResult> CreateApiKeyAsync(
        HttpContext context,
        CreateApiKeyRequest request,
        IdentityApplicationService identityService,
        CancellationToken cancellationToken)
    {
        AuthorizationPrincipal actor = RequestAuthentication.GetPrincipal(context)!;
        ApplicationResult<ApiKeyCreationResult> result = await identityService.CreateApiKeyAsync(
            actor,
            request.Name,
            request.Scope,
            request.ExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(StatusCodes.Status400BadRequest, result.ErrorCode!, context);
        }

        ApiKeyCreationResult created = result.Value!;
        return Results.Created(
            $"/api/v1/auth/api-keys/{created.ApiKey.Id:D}",
            new CreatedApiKeyDto(created.ApiKey.Id, created.ApiKey.Name, created.PlaintextKey, created.ApiKey.ExpiresAt));
    }

    private static PrincipalDto ToPrincipal(AuthenticationResult authentication)
    {
        return new(ToUser(authentication.User), [.. authentication.GrantedInstanceIds.OrderBy(id => id)], authentication.User.PreferredLanguage);
    }

    private static UserDto ToUser(StoredUser user)
    {
        return new(user.Id, user.UserName, user.DisplayName, user.Role.ToString(), user.Role == Role.Disabled, user.LastSignedInAt, user.CreatedAt);
    }

    private static IResult Problem(int statusCode, string errorCode, HttpContext context)
    {
        return ProblemResponses.Create(context, statusCode, errorCode);
    }

    private static void AppendSessionCookie(HttpContext context, AuthenticationResult authentication)
    {
        context.Response.Cookies.Append(
            RequestSecurityConstants.SessionCookie,
            authentication.SessionToken,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = true,
                Path = "/",
                Expires = authentication.Session.ExpiresAt,
            });
    }

    private static void DeleteSessionCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(RequestSecurityConstants.SessionCookie, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    }

    private static int StatusFor(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCodes.OwnerImmutable or ErrorCodes.Conflict => StatusCodes.Status409Conflict,
            ErrorCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    private static Uri AbsoluteEndpoint(HttpContext context, string path)
    {
        return new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{path}", UriKind.Absolute);
    }

    private static Uri? ResolveReturnUrl(HttpContext context, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        Uri origin = AbsoluteEndpoint(context, "/");
        Uri candidate = Uri.TryCreate(returnUrl, UriKind.Absolute, out Uri? absolute) ? absolute : new Uri(origin, returnUrl);
        return string.Equals(candidate.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(candidate.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : throw new DomainException(ErrorCodes.ValidationFailed, "The OIDC return URL must use the current origin.");
    }

    private static Uri? ResolveDesktopReturnUrl(string? callback, string? challenge)
    {
        if (callback is null && challenge is null)
        {
            return null;
        }

        if (!Uri.TryCreate(callback, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out IPAddress? address) ||
            !IPAddress.IsLoopback(address) ||
            uri.Port is < 1024 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(challenge) || challenge.Length != 43 ||
            challenge.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The Desktop OIDC callback or PKCE challenge is invalid.");
        }

        UriBuilder builder = new(uri)
        {
            Scheme = "servitium-desktop-oidc",
            Query = QueryString.Create("servitium-desktop-challenge", challenge).Value?.TrimStart('?'),
        };
        return builder.Uri;
    }

    private static Uri ValidateOidcBindingCallback(Uri uri)
    {
        return !uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out IPAddress? address) ||
            !IPAddress.IsLoopback(address) ||
            uri.Port is < 1024 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment)
            ? throw new DomainException(ErrorCodes.ValidationFailed, "The Desktop OIDC binding callback is invalid.")
            : uri;
    }

    private static bool IsLoopback(IPAddress? address)
    {
        return address is null || IPAddress.IsLoopback(address);
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        return actual is not null && expected.Length == actual.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(actual));
    }
}
