namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ServerSettingsViewModel
{
    public ServerSettingValue DefaultLanguage { get; }
    public ServerSettingValue DisplayTimeZone { get; }
    public ServerSettingValue ActivityRetentionDays { get; }
    public ServerSettingValue AuditRetentionDays { get; }
    public ServerSettingValue OperationRetentionDays { get; }
    public ServerSettingValue MetricRetentionHours { get; }
    public ServerSettingValue ServiceLogRetentionDays { get; }
    public ServerSettingValue DatabaseBackupEnabled { get; }
    public ServerSettingValue DatabaseBackupRetentionCount { get; }
    public ServerSettingValue PublicPageEnabled { get; }
    public ServerSettingValue PageTitle { get; }
    public ServerSettingValue SidebarPosition { get; }
    public ServerSettingValue LoginTitleZhHans { get; }
    public ServerSettingValue LoginDescriptionZhHans { get; }
    public ServerSettingValue LoginSupportZhHans { get; }
    public ServerSettingValue LoginTitleEnUs { get; }
    public ServerSettingValue LoginDescriptionEnUs { get; }
    public ServerSettingValue LoginSupportEnUs { get; }
    public ServerSettingValue ListenAddress { get; }
    public ServerSettingValue ListenPort { get; }
    public ServerSettingValue PathBase { get; }
    public ServerSettingValue PublicBaseUrl { get; }
    public ServerSettingValue CorsOrigins { get; }
    public ServerSettingValue ResponseCompressionEnabled { get; }
    public ServerSettingValue TrustedProxyMode { get; }
    public ServerSettingValue TrustedProxyNetworks { get; }
    public ServerSettingValue ForwardedForHeader { get; }
    public ServerSettingValue TlsCertificateSource { get; }
    public ServerSettingValue TlsCertificatePath { get; }
    public ServerSettingValue TlsCertificatePassword { get; }
    public ServerSettingValue TlsCertificateThumbprint { get; }
    public ServerSettingValue TotpDriftSteps { get; }
    public ServerSettingValue UserFailureLimit { get; }
    public ServerSettingValue IpFailureLimit { get; }
    public ServerSettingValue IpFailureWindowMinutes { get; }
    public ServerSettingValue IpBanMinutes { get; }
    public ServerSettingValue SessionIpValidationEnabled { get; }
    public ServerSettingValue UserFileManagementEnabled { get; }
    public ServerSettingValue ApiKeysEnabled { get; }
    public ServerSettingValue ArchiveConcurrencyLimit { get; }
    public ServerSettingValue DownloadConcurrencyLimit { get; }
    public ServerSettingValue UploadConcurrencyLimit { get; }
    public ServerSettingValue PrivateSourceAllowList { get; }
    public ServerSettingValue SessionIdleHours { get; }
    public ServerSettingValue SessionAbsoluteHours { get; }
    public ServerSettingValue AdvancedRawCommandsEnabled { get; }
    public ServerSettingValue EmbedAllowedOrigins { get; }
    public ServerSettingValue OidcEnabled { get; }
    public ServerSettingValue OidcProviderDisplayName { get; }
    public ServerSettingValue OidcIssuer { get; }
    public ServerSettingValue OidcClientId { get; }
    public ServerSettingValue OidcClientSecret { get; }
    public ServerSettingValue OidcScopes { get; }
    public ServerSettingValue OidcAutomaticRedirect { get; }
    public ServerSettingValue OidcSubjectClaim { get; }
    public ServerSettingValue OidcUserNameClaim { get; }
    private ServerSettingValue[] AllFields => [DefaultLanguage, DisplayTimeZone, ActivityRetentionDays, AuditRetentionDays, OperationRetentionDays, MetricRetentionHours, ServiceLogRetentionDays, DatabaseBackupEnabled, DatabaseBackupRetentionCount, PublicPageEnabled, PageTitle, SidebarPosition, LoginTitleZhHans, LoginDescriptionZhHans, LoginSupportZhHans, LoginTitleEnUs, LoginDescriptionEnUs, LoginSupportEnUs, ListenAddress, ListenPort, PathBase, PublicBaseUrl, CorsOrigins, ResponseCompressionEnabled, TrustedProxyMode, TrustedProxyNetworks, ForwardedForHeader, TlsCertificateSource, TlsCertificatePath, TlsCertificatePassword, TlsCertificateThumbprint, TotpDriftSteps, UserFailureLimit, IpFailureLimit, IpFailureWindowMinutes, IpBanMinutes, SessionIpValidationEnabled, UserFileManagementEnabled, ApiKeysEnabled, ArchiveConcurrencyLimit, DownloadConcurrencyLimit, UploadConcurrencyLimit, PrivateSourceAllowList, SessionIdleHours, SessionAbsoluteHours, AdvancedRawCommandsEnabled, EmbedAllowedOrigins, OidcEnabled, OidcProviderDisplayName, OidcIssuer, OidcClientId, OidcClientSecret, OidcScopes, OidcAutomaticRedirect, OidcSubjectClaim, OidcUserNameClaim];
}
