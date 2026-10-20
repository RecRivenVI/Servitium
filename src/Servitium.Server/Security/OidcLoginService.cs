using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.IdentityModel.Tokens;

using Servitium.Application.Abstractions;
using Servitium.Application.Results;
using Servitium.Application.Identity;
using Servitium.Contracts;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Settings;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Persistence;
using Servitium.Domain.Primitives;

namespace Servitium.Server.Security;

internal sealed record OidcAuthorizationResult(Uri AuthorizationUri, string State);
internal sealed record OidcCompletionResult(AuthenticationResult? Authentication, bool Bound, Uri? ReturnUrl, string? ErrorCode);

internal sealed class OidcLoginService(
    ControlPlaneStore settingsStore,
    IIdentityStore identityStore,
    IdentityApplicationService identityService,
    IOutboundHttpClient http,
    ISecretProtector protector,
    ITokenGenerator tokens,
    IGuidFactory guids,
    IClock clock,
    AllowedAuthoritySet allowedAuthorities) : IDisposable
{
    private const long MetadataLimit = 1024 * 1024;
    private readonly SemaphoreSlim _jwksGate = new(1, 1);
    private Uri? _cachedJwksUri;
    private string? _cachedJwks;
    private DateTimeOffset _cachedJwksExpiresAt;

    internal async Task<AuthenticationBootstrapDto> GetBootstrapAsync(bool initialized, CancellationToken cancellationToken)
    {
        OidcSettings? settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings is null
            ? new(initialized, false, null, null, false, null)
            : new(initialized, true, settings.DisplayName, settings.IconUrl, settings.AutoRedirect, settings.LoginPageText);
    }

    internal async Task<OidcAuthorizationResult> BeginAsync(
        Uri redirectUri,
        Uri? returnUrl,
        Guid? bindingUserId,
        CancellationToken cancellationToken)
    {
        OidcSettings settings = await RequireSettingsAsync(cancellationToken).ConfigureAwait(false);
        OidcDiscovery discovery = await DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        string state = tokens.Generate(32);
        string nonce = tokens.Generate(32);
        string verifier = tokens.Generate(48);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        await identityStore.AddOidcStateAsync(new StoredOidcState(
            guids.Create(),
            IdentityApplicationService.HashToken(state),
            nonce,
            verifier,
            returnUrl,
            bindingUserId,
            clock.UtcNow.AddMinutes(10),
            false), cancellationToken).ConfigureAwait(false);

        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = settings.Scope,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return new(new Uri(discovery.AuthorizationEndpoint.AbsoluteUri + Microsoft.AspNetCore.Http.QueryString.Create(query), UriKind.Absolute), state);
    }

    internal async Task<OidcCompletionResult> CompleteAsync(
        string code,
        string state,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return new(null, false, null, ErrorCodes.OidcStateInvalid);
        }

        StoredOidcState? persisted = await identityStore.ConsumeOidcStateAsync(
            IdentityApplicationService.HashToken(state),
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (persisted is null)
        {
            return new(null, false, null, ErrorCodes.OidcStateInvalid);
        }

        try
        {
            OidcSettings settings = await RequireSettingsAsync(cancellationToken).ConfigureAwait(false);
            OidcDiscovery discovery = await DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
            string body = FormEncode(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["code_verifier"] = persisted.CodeVerifier,
            });
            OutboundHttpResponse tokenResponse = await http.SendAsync(new(
                discovery.TokenEndpoint,
                "POST",
                Encoding.UTF8.GetBytes(body),
                "application/x-www-form-urlencoded",
                MetadataLimit), cancellationToken).ConfigureAwait(false);
            string idToken;
            using (tokenResponse.Content)
            {
                using JsonDocument tokenDocument = await JsonDocument.ParseAsync(tokenResponse.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (tokenResponse.StatusCode is < 200 or >= 300 || !tokenDocument.RootElement.TryGetProperty("id_token", out JsonElement idTokenElement) || string.IsNullOrWhiteSpace(idTokenElement.GetString()))
                {
                    return new(null, false, persisted.ReturnUrl, ErrorCodes.Unauthenticated);
                }

                idToken = idTokenElement.GetString()!;
            }

            ClaimsPrincipal principal = await ValidateIdTokenAsync(idToken, settings, discovery, persisted.Nonce, cancellationToken).ConfigureAwait(false);
            string subject = principal.FindFirst("sub")?.Value ?? throw new SecurityTokenValidationException("The ID token has no subject.");
            if (persisted.BindingUserId is Guid bindingUserId)
            {
                StoredExternalIdentity binding = new(guids.Create(), bindingUserId, discovery.Issuer.AbsoluteUri, subject, clock.UtcNow);
                bool bound = await identityStore.TrySetExternalIdentityAsync(binding, cancellationToken).ConfigureAwait(false);
                return new(null, bound, persisted.ReturnUrl, bound ? null : ErrorCodes.OidcSubjectConflict);
            }

            StoredExternalIdentity? identity = await identityStore.FindExternalIdentityAsync(discovery.Issuer.AbsoluteUri, subject, cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                return new(null, false, persisted.ReturnUrl, ErrorCodes.Unauthenticated);
            }

            if (TryGetDesktopReturn(persisted.ReturnUrl, out Uri? desktopCallback, out string? codeChallenge))
            {
                ApplicationResult<string> ticket = await identityService.IssueDesktopOidcTicketAsync(
                    identity.UserId,
                    codeChallenge,
                    cancellationToken).ConfigureAwait(false);
                return ticket.Succeeded
                    ? new(null, false, AppendDesktopCode(desktopCallback, ticket.Value!), null)
                    : new(null, false, desktopCallback, ticket.ErrorCode);
            }

            ApplicationResult<AuthenticationResult> authentication = await identityService.CreateExternalSessionAsync(identity.UserId, cancellationToken).ConfigureAwait(false);
            return new(authentication.Value, false, persisted.ReturnUrl, authentication.ErrorCode);
        }
        catch (DomainException exception)
        {
            return new(null, false, persisted.ReturnUrl, exception.Code == ErrorCodes.OidcNonceMismatch ? exception.Code : ErrorCodes.Unauthenticated);
        }
        catch (Exception exception) when (exception is JsonException or SecurityTokenException or InvalidOperationException or FormatException)
        {
            return new(null, false, persisted.ReturnUrl, ErrorCodes.Unauthenticated);
        }
    }

    private async Task<ClaimsPrincipal> ValidateIdTokenAsync(
        string idToken,
        OidcSettings settings,
        OidcDiscovery discovery,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        string jwks = await GetJwksAsync(discovery.JwksUri, cancellationToken).ConfigureAwait(false);

        JsonWebKeySet keySet = new(jwks);
        TokenValidationParameters validation = new()
        {
            ValidateIssuer = true,
            ValidIssuer = discovery.Issuer.AbsoluteUri,
            ValidateAudience = true,
            ValidAudience = settings.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keySet.Keys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
        TokenValidationResult result = await handler.ValidateTokenAsync(idToken, validation).ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            throw result.Exception ?? new SecurityTokenValidationException("The ID token is invalid.");
        }

        ClaimsPrincipal principal = new(result.ClaimsIdentity);
        SecurityToken? validated = result.SecurityToken;
        if (validated is not JwtSecurityToken jwt || jwt.Header.Alg is null || string.Equals(jwt.Header.Alg, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityTokenValidationException("The ID token algorithm is invalid.");
        }

        string? nonce = principal.FindFirst("nonce")?.Value;
        if (!FixedTimeEquals(expectedNonce, nonce))
        {
            throw new DomainException(ErrorCodes.OidcNonceMismatch, "The ID token nonce does not match.");
        }

        string? issuedAt = principal.FindFirst("iat")?.Value;
        return !long.TryParse(issuedAt, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long iat) || DateTimeOffset.FromUnixTimeSeconds(iat) > clock.UtcNow.AddMinutes(2)
            ? throw new SecurityTokenValidationException("The ID token issue time is invalid.")
            : principal;
    }

    private async Task<OidcDiscovery> DiscoverAsync(OidcSettings settings, CancellationToken cancellationToken)
    {
        Uri discoveryUri = new(settings.Issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute);
        OutboundHttpResponse response = await http.SendAsync(new(discoveryUri, "GET", MaximumResponseBytes: MetadataLimit), cancellationToken).ConfigureAwait(false);
        using (response.Content)
        {
            if (response.StatusCode is < 200 or >= 300)
            {
                throw new InvalidOperationException("OIDC discovery failed.");
            }

            using JsonDocument document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
            Uri issuer = RequireHttpsUri(document.RootElement, "issuer");
            return Uri.Compare(issuer, settings.Issuer, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.Ordinal) != 0
                ? throw new InvalidOperationException("OIDC issuer mismatch.")
                : new(
                issuer,
                RequireHttpsUri(document.RootElement, "authorization_endpoint"),
                RequireHttpsUri(document.RootElement, "token_endpoint"),
                RequireHttpsUri(document.RootElement, "jwks_uri"));
        }
    }

    private async Task<string> GetJwksAsync(Uri jwksUri, CancellationToken cancellationToken)
    {
        if (_cachedJwks is not null && _cachedJwksUri == jwksUri && _cachedJwksExpiresAt > clock.UtcNow)
        {
            return _cachedJwks;
        }

        await _jwksGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedJwks is not null && _cachedJwksUri == jwksUri && _cachedJwksExpiresAt > clock.UtcNow)
            {
                return _cachedJwks;
            }

            OutboundHttpResponse response = await http.SendAsync(new(jwksUri, "GET", MaximumResponseBytes: MetadataLimit), cancellationToken).ConfigureAwait(false);
            using (response.Content)
            {
                if (response.StatusCode is < 200 or >= 300)
                {
                    throw new SecurityTokenValidationException("The JWKS endpoint failed.");
                }

                using StreamReader reader = new(response.Content, new UTF8Encoding(false, true), true, 16 * 1024, leaveOpen: true);
                _cachedJwks = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                _cachedJwksUri = jwksUri;
                _cachedJwksExpiresAt = clock.UtcNow.AddMinutes(5);
                return _cachedJwks;
            }
        }
        finally
        {
            _ = _jwksGate.Release();
        }
    }

    private async Task<OidcSettings> RequireSettingsAsync(CancellationToken cancellationToken)
    {
        return await LoadSettingsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OIDC is not configured.");
    }

    private async Task<OidcSettings?> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SettingsGroupDto> groups = await settingsStore.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
        SettingsGroupDto? security = groups.SingleOrDefault(group => group.Group == "security");
        if (security?.Values.TryGetValue("privateSourceAllowList", out string? privateSources) == true)
        {
            allowedAuthorities.Replace((privateSources ?? string.Empty).Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        SettingsGroupDto? identity = groups.SingleOrDefault(group => group.Group == "identity");
        if (identity is null || !bool.TryParse(Value(identity.Values, "oidcEnabled"), out bool enabled) || !enabled ||
            !Uri.TryCreate(Value(identity.Values, "oidcIssuer"), UriKind.Absolute, out Uri? issuer) || issuer.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(Value(identity.Values, "oidcClientId")))
        {
            return null;
        }

        string secret = Unprotect(Value(identity.Values, "oidcClientSecret"));
        Uri? icon = Uri.TryCreate(Value(identity.Values, "oidcIconUrl"), UriKind.Absolute, out Uri? iconUri) ? iconUri : null;
        return new(
            issuer,
            Value(identity.Values, "oidcClientId")!,
            secret,
            Value(identity.Values, "oidcScopes") ?? "openid profile email",
            Value(identity.Values, "oidcProviderDisplayName"),
            icon,
            bool.TryParse(Value(identity.Values, "oidcAutomaticRedirect"), out bool autoRedirect) && autoRedirect,
            Value(identity.Values, "loginPageText"));
    }

    private string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !protectedValue.StartsWith("protected:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        byte[] protectedBytes = Convert.FromBase64String(protectedValue["protected:".Length..]);
        byte[] plaintext = protector.Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static Uri RequireHttpsUri(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement value) && Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidOperationException("The OIDC discovery document contains an invalid endpoint.");
    }

    private static string FormEncode(IReadOnlyDictionary<string, string> values)
    {
        return string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        return actual is not null && expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    private static bool TryGetDesktopReturn(Uri? value, out Uri callback, out string challenge)
    {
        callback = null!;
        challenge = string.Empty;
        if (value is null || value.Scheme != "servitium-desktop-oidc" ||
            !System.Net.IPAddress.TryParse(value.Host, out System.Net.IPAddress? address) ||
            !System.Net.IPAddress.IsLoopback(address) ||
            value.Port is < 1024 or > 65535 ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            return false;
        }

        _ = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(value.Query)
            .TryGetValue("servitium-desktop-challenge", out Microsoft.Extensions.Primitives.StringValues challengeValues);
        challenge = challengeValues.Count == 1 ? challengeValues[0] ?? string.Empty : string.Empty;
        if (challenge.Length != 43 ||
            challenge.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return false;
        }

        callback = new UriBuilder(value)
        {
            Scheme = Uri.UriSchemeHttp,
            Query = string.Empty,
        }.Uri;
        return true;
    }

    private static Uri AppendDesktopCode(Uri callback, string code)
    {
        UriBuilder builder = new(callback)
        {
            Query = Microsoft.AspNetCore.Http.QueryString.Create("code", code).Value?.TrimStart('?'),
        };
        return builder.Uri;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    public void Dispose()
    {
        _jwksGate.Dispose();
    }

    private sealed record OidcSettings(
        Uri Issuer,
        string ClientId,
        string ClientSecret,
        string Scope,
        string? DisplayName,
        Uri? IconUrl,
        bool AutoRedirect,
        string? LoginPageText);

    private sealed record OidcDiscovery(Uri Issuer, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri JwksUri);

}
