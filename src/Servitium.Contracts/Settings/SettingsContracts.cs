namespace Servitium.Contracts.Settings;

/// <summary>系统设置响应。</summary>
/// <param name="DefaultLanguage">默认语言。</param>
/// <param name="ApiKeysEnabled">API Key 总开关。</param>
/// <param name="PublicPageEnabled">公开页开关。</param>
public sealed record SystemSettingsDto(string DefaultLanguage, bool ApiKeysEnabled, bool PublicPageEnabled);

/// <summary>系统设置组。</summary>
/// <param name="Group">稳定分组名。</param>
/// <param name="Values">非秘密值与秘密占位符。</param>
/// <param name="Revision">并发修订。</param>
/// <param name="RestartRequired">变更后是否需要重启。</param>
/// <param name="Effects">每个设置项的生效方式（<c>immediate</c> 或 <c>restart-required</c>）。</param>
public sealed record SettingsGroupDto(
    string Group,
    System.Collections.Generic.IReadOnlyDictionary<string, string?> Values,
    long Revision,
    bool RestartRequired,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Effects = null);

/// <summary>系统设置集合。</summary>
/// <param name="Groups">分组。</param>
public sealed record SettingsDto(System.Collections.Generic.IReadOnlyList<SettingsGroupDto> Groups);

/// <summary>更新设置分组。</summary>
/// <param name="Values">字段值。</param>
/// <param name="ExpectedRevision">期望修订。</param>
public sealed record UpdateSettingsGroupRequest(
    System.Collections.Generic.IReadOnlyDictionary<string, string?> Values,
    long ExpectedRevision);

/// <summary>OIDC 发现测试请求。</summary>
/// <param name="Authority">提供方基址。</param>
public sealed record TestOidcDiscoveryRequest(string Authority);

/// <summary>TLS 证书测试请求。</summary>
/// <param name="CertificatePath">PFX 绝对路径。</param>
/// <param name="Password">PFX 密码。</param>
public sealed record TestTlsCertificateRequest(string CertificatePath, string? Password);

/// <summary>设置测试结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Code">稳定结果码。</param>
/// <param name="Summary">非秘密摘要。</param>
public sealed record SettingsTestResultDto(bool Succeeded, string Code, string Summary);

/// <summary>系统信息。</summary>
/// <param name="Version">Servitium 版本。</param>
/// <param name="OperatingSystem">操作系统。</param>
/// <param name="Architecture">进程体系结构。</param>
/// <param name="DataRoot">数据目录。</param>
/// <param name="DatabaseSizeBytes">数据库大小。</param>
/// <param name="FrameworkVersion">.NET 运行时版本。</param>
/// <param name="Build">构建标识。</param>
/// <param name="ExpectedAgentProtocol">期望 Agent 协议。</param>
/// <param name="ServiceAccount">服务运行账户。</param>
/// <param name="ServerTime">服务器 UTC 时间。</param>
/// <param name="ClientTime">客户端报告时间。</param>
/// <param name="ActiveIpBans">活动 IP 封禁数。</param>
/// <param name="DeniedRequests">最近 24 小时拒绝请求数。</param>
/// <param name="ProcessMemoryBytes">控制平面工作集。</param>
/// <param name="CpuPercent">控制平面 CPU 百分比。</param>
/// <param name="HostName">主机名。</param>
public sealed record SystemInfoDto(
    string Version,
    string OperatingSystem,
    string Architecture,
    string DataRoot,
    long DatabaseSizeBytes,
    string FrameworkVersion,
    string Build,
    string ExpectedAgentProtocol,
    string ServiceAccount,
    System.DateTimeOffset ServerTime,
    System.DateTimeOffset? ClientTime,
    int ActiveIpBans,
    int DeniedRequests,
    long ProcessMemoryBytes,
    double CpuPercent,
    string HostName);

/// <summary>临时 IP 封禁摘要。</summary>
public sealed record IpBanDto(string Address, System.DateTimeOffset ExpiresAt, string ReasonCode);

/// <summary>数据库在线备份结果。</summary>
/// <param name="FileName">数据目录内的备份文件名。</param>
/// <param name="SizeBytes">备份大小。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record DatabaseBackupDto(string FileName, long SizeBytes, System.DateTimeOffset CreatedAt);

/// <summary>已保存的外观资源。</summary>
public sealed record SettingsAssetDto(string Kind, string FileName);

/// <summary>仪表盘卡片布局。</summary>
/// <param name="Id">稳定卡片标识。</param>
/// <param name="Kind">稳定卡片类型。</param>
/// <param name="Title">用户覆盖标题；空值表示使用本地化默认标题。</param>
/// <param name="Description">用户覆盖描述；空值表示使用本地化默认描述。</param>
/// <param name="Width">十二栅格中的宽度。</param>
/// <param name="Height">高度档位。</param>
/// <param name="Hidden">是否隐藏。</param>
/// <param name="Required">是否为不可删除的核心卡片。</param>
/// <param name="Settings">卡片类型专用设置。</param>
public sealed record DashboardCardLayoutDto(
    string Id,
    string Kind,
    string? Title,
    string? Description,
    int Width,
    string Height,
    bool Hidden,
    bool Required,
    System.Collections.Generic.IReadOnlyDictionary<string, string?> Settings);

/// <summary>用户某一页面的布局。</summary>
/// <param name="Page">稳定页面标识。</param>
/// <param name="Version">默认布局版本。</param>
/// <param name="Cards">有序卡片集合。</param>
public sealed record DashboardLayoutDto(
    string Page,
    int Version,
    System.Collections.Generic.IReadOnlyList<DashboardCardLayoutDto> Cards);

/// <summary>保存用户页面布局。</summary>
/// <param name="Version">客户端读取到的默认布局版本。</param>
/// <param name="Cards">完整有序卡片集合。</param>
public sealed record UpdateDashboardLayoutRequest(
    int Version,
    System.Collections.Generic.IReadOnlyList<DashboardCardLayoutDto> Cards);
