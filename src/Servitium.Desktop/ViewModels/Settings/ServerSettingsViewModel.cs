using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Settings;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ServerSettingsViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, Task<string?>> _saveFile;
    private readonly TimeProvider _clock;
    private readonly bool _administrator;
    private readonly bool _owner;
    private readonly string _category;
    private readonly Dictionary<string, SettingsGroupDto> _groups = new(StringComparer.Ordinal);
    public ServerSettingsViewModel(LocalizationService localization, ServitiumApiClient client, string category, string? role,
        Func<string, string, Task<bool>> confirm, Func<string, string, Task<string?>> saveFile, TimeProvider clock) : base(localization)
    {
        _client = client; _category = category; _administrator = role is "Owner" or "Administrator"; _owner = role == "Owner";
        _confirm = confirm; _saveFile = saveFile; _clock = clock;
        DefaultLanguage = new("general", "defaultLanguage", localization, choices: [new("zh-Hans", Text("desktop.choice-zh-Hans")), new("en-US", Text("desktop.choice-en-US"))]);
        DisplayTimeZone = new("general", "displayTimeZone", localization);
        ActivityRetentionDays = new("general", "activityRetentionDays", localization, 30, 3650);
        AuditRetentionDays = new("general", "auditRetentionDays", localization, 90, 3650);
        OperationRetentionDays = new("general", "operationRetentionDays", localization, 7, 365);
        MetricRetentionHours = new("general", "metricRetentionHours", localization, 1, 168);
        ServiceLogRetentionDays = new("general", "serviceLogRetentionDays", localization, 7, 365);
        DatabaseBackupEnabled = new("general", "databaseBackupEnabled", localization);
        DatabaseBackupRetentionCount = new("general", "databaseBackupRetentionCount", localization, 1, 365);
        PublicPageEnabled = new("general", "publicPageEnabled", localization);
        PageTitle = new("appearance", "pageTitle", localization);
        SidebarPosition = new("appearance", "sidebarPosition", localization, choices: [new("left", Text("desktop.choice-left")), new("top", Text("desktop.choice-top"))]);
        LoginTitleZhHans = new("appearance", "loginTitleZhHans", localization);
        LoginDescriptionZhHans = new("appearance", "loginDescriptionZhHans", localization);
        LoginSupportZhHans = new("appearance", "loginSupportZhHans", localization);
        LoginTitleEnUs = new("appearance", "loginTitleEnUs", localization);
        LoginDescriptionEnUs = new("appearance", "loginDescriptionEnUs", localization);
        LoginSupportEnUs = new("appearance", "loginSupportEnUs", localization);
        ListenAddress = new("security", "listenAddress", localization);
        ListenPort = new("security", "listenPort", localization, 1, 65535);
        PathBase = new("security", "pathBase", localization);
        PublicBaseUrl = new("security", "publicBaseUrl", localization);
        CorsOrigins = new("security", "corsOrigins", localization);
        ResponseCompressionEnabled = new("security", "responseCompressionEnabled", localization);
        TrustedProxyMode = new("security", "trustedProxyMode", localization);
        TrustedProxyNetworks = new("security", "trustedProxyNetworks", localization);
        ForwardedForHeader = new("security", "forwardedForHeader", localization);
        TlsCertificateSource = new("security", "tlsCertificateSource", localization, choices: [new("none", Text("desktop.choice-none")), new("file", Text("desktop.choice-file")), new("store", Text("desktop.choice-store"))]);
        TlsCertificatePath = new("security", "tlsCertificatePath", localization);
        TlsCertificatePassword = new("security", "tlsCertificatePassword", localization, secret: true);
        TlsCertificateThumbprint = new("security", "tlsCertificateThumbprint", localization);
        TotpDriftSteps = new("security", "totpDriftSteps", localization, 0, 4);
        UserFailureLimit = new("security", "userFailureLimit", localization, 1, 20);
        IpFailureLimit = new("security", "ipFailureLimit", localization, 1, 200);
        IpFailureWindowMinutes = new("security", "ipFailureWindowMinutes", localization, 1, 1440);
        IpBanMinutes = new("security", "ipBanMinutes", localization, 1, 10080);
        SessionIpValidationEnabled = new("security", "sessionIpValidationEnabled", localization);
        UserFileManagementEnabled = new("security", "userFileManagementEnabled", localization);
        ApiKeysEnabled = new("security", "apiKeysEnabled", localization);
        ArchiveConcurrencyLimit = new("security", "archiveConcurrencyLimit", localization, 1, 16);
        DownloadConcurrencyLimit = new("security", "downloadConcurrencyLimit", localization, 1, 64);
        UploadConcurrencyLimit = new("security", "uploadConcurrencyLimit", localization, 1, 32);
        PrivateSourceAllowList = new("security", "privateSourceAllowList", localization);
        SessionIdleHours = new("security", "sessionIdleHours", localization, 1, 168);
        SessionAbsoluteHours = new("security", "sessionAbsoluteHours", localization, 2, 720);
        AdvancedRawCommandsEnabled = new("security", "advancedRawCommandsEnabled", localization);
        EmbedAllowedOrigins = new("security", "embedAllowedOrigins", localization);
        OidcEnabled = new("identity", "oidcEnabled", localization);
        OidcProviderDisplayName = new("identity", "oidcProviderDisplayName", localization);
        OidcIssuer = new("identity", "oidcIssuer", localization);
        OidcClientId = new("identity", "oidcClientId", localization);
        OidcClientSecret = new("identity", "oidcClientSecret", localization, secret: true);
        OidcScopes = new("identity", "oidcScopes", localization);
        OidcAutomaticRedirect = new("identity", "oidcAutomaticRedirect", localization);
        OidcSubjectClaim = new("identity", "oidcSubjectClaim", localization);
        OidcUserNameClaim = new("identity", "oidcUserNameClaim", localization);
        foreach (ServerSettingValue setting in AllFields) { setting.PropertyChanged += OnFieldChanged; }
    }
    public bool HasData { get; private set => SetProperty(ref field, value); }
    public bool CanReload => CanInteract && _administrator;
    public bool CanEdit => HasData && _administrator && CanInteract;
    public bool CanBackup => CanEdit && _owner;
    public bool IsDirty => ActiveFields.Any(setting => setting.IsDirty);
    private IEnumerable<ServerSettingValue> ActiveFields => AllFields.Where(setting => ActiveGroups.Contains(setting.Group, StringComparer.Ordinal));
    private string[] ActiveGroups => _category switch { "server-general" => ["general", "appearance"], "server-security" => ["security"], "server-identity" => ["identity"], _ => [] };
    public bool CanSave => CanEdit && IsDirty && Validation.Length == 0;
    public string DirtyText => IsDirty ? Format("desktop.settings-unsaved-count", ActiveFields.Count(setting => setting.IsDirty)) : Text("desktop.all-saved");
    public string Validation => !HasData ? string.Empty : string.Join(Environment.NewLine, ActiveFields.Select(setting => setting.Error).Where(error => error.Length > 0).Concat(ValidateRelationships()));
    public bool HasValidation => Validation.Length > 0;
    public string SystemInformation { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string BackupRestriction => _owner ? string.Empty : Text("desktop.settings-backup-owner");
    public bool HasBackupRestriction => !_owner;
    public Task LoadAsync()
    {
        return RunAsync(async token =>
    {
        if (!_administrator) { Message = Text("desktop.settings-server-denied"); return; }
        SettingsDto result = await _client.GetSettingsAsync(token).ConfigureAwait(true);
        foreach (SettingsGroupDto group in result.Groups) { _groups[group.Group] = group; }
        foreach (ServerSettingValue setting in AllFields)
        {
            if (!_groups.TryGetValue(setting.Group, out SettingsGroupDto? group) || !group.Values.ContainsKey(setting.Key))
            { throw new InvalidDataException("The server settings response is incomplete."); }
            setting.Load(group.Values[setting.Key], group.Effects?.GetValueOrDefault(setting.Key));
        }
        if (_category == "server-about")
        {
            SystemInfoDto info = await _client.GetSystemInfoAsync(_clock.GetUtcNow(), token).ConfigureAwait(true);
            SystemInformation = Format("desktop.settings-system-info", info.Version, info.OperatingSystem, info.Architecture, info.FrameworkVersion,
                info.HostName, info.DataRoot, info.ServiceAccount, (info.DatabaseSizeBytes / 1048576d).ToString("N1", CultureInfo.CurrentCulture), info.ServerTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture));
        }
        HasData = true; RefreshFields();
    });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        return RunAsync(async token =>
    {
        foreach (string name in ActiveGroups)
        {
            ServerSettingValue[] changed = [.. AllFields.Where(setting => setting.Group == name && setting.IsDirty)];
            if (changed.Length == 0) { continue; }
            if (name == "identity" && changed.Any(setting => setting.Key is "oidcIssuer" or "oidcClientId" or "oidcSubjectClaim" or "oidcUserNameClaim") &&
                !await _confirm("action.save", Text("danger.oidc-impact")).ConfigureAwait(true)) { return; }
            if (name == "security" && AdvancedRawCommandsEnabled.IsDirty && AdvancedRawCommandsEnabled.IsOn &&
                !await _confirm("action.save", Text("danger.enable-advanced-commands-impact")).ConfigureAwait(true)) { return; }
            if (name == "security" && ListenAddress.IsDirty && IPAddress.TryParse(ListenAddress.Value, out IPAddress? address) && !IPAddress.IsLoopback(address) &&
                !await _confirm("action.save", Text("danger.non-loopback-listen-impact")).ConfigureAwait(true)) { return; }
            Dictionary<string, string?> values = changed.ToDictionary(setting => setting.Key, setting => setting.WriteValue, StringComparer.Ordinal);
            SettingsGroupDto saved = await _client.UpdateSettingsAsync(name, new(values, _groups[name].Revision), token).ConfigureAwait(true);
            _groups[name] = saved;
            foreach (ServerSettingValue setting in AllFields.Where(setting => setting.Group == name)) { setting.Load(saved.Values.GetValueOrDefault(setting.Key), saved.Effects?.GetValueOrDefault(setting.Key)); }
        }
        Message = Text("desktop.settings-server-saved"); RefreshFields();
    });
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task RefreshAsync() { if (await CanLeaveAsync().ConfigureAwait(true)) { await LoadAsync().ConfigureAwait(true); } }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DiscardAsync()
    {
        if (!IsDirty || !await _confirm("action.discard", Text("desktop.settings-discard")).ConfigureAwait(true)) { return; }
        foreach (ServerSettingValue setting in ActiveFields) { SettingsGroupDto group = _groups[setting.Group]; setting.Load(group.Values[setting.Key], group.Effects?.GetValueOrDefault(setting.Key)); }
        RefreshFields();
    }
    public async Task<bool> CanLeaveAsync()
    {
        return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.settings-discard")).ConfigureAwait(true);
    }

    protected override void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanBackup));
        SaveCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        TestOidcCommand.NotifyCanExecuteChanged(); TestTlsCommand.NotifyCanExecuteChanged(); BackupCommand.NotifyCanExecuteChanged(); ExportDiagnosticsCommand.NotifyCanExecuteChanged();
    }
    private void RefreshFields() { OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(DirtyText)); OnPropertyChanged(nameof(Validation)); OnPropertyChanged(nameof(HasValidation)); RefreshCommands(); }
    private void OnFieldChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(ServerSettingValue.Value) or nameof(ServerSettingValue.ClearSecret)) { RefreshFields(); } }
    public override void Dispose() { foreach (ServerSettingValue setting in AllFields) { setting.PropertyChanged -= OnFieldChanged; if (setting.Secret) { setting.Value = string.Empty; } } base.Dispose(); }
}
