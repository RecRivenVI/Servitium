using System;

using Servitium.Contracts.Identity;

namespace Servitium.Client.Session;

/// <summary>密码登录结果；需要 TOTP 时 Principal 为空。</summary>
public sealed record SignInResult(bool TotpRequired, PrincipalDto? Principal);

/// <summary>本机 Windows 身份换票结果。</summary>
public sealed record LocalSessionResult(bool Succeeded, PrincipalDto? Principal, string? ErrorCode, DateTimeOffset? TicketExpiresAt);
