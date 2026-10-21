using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class AccountSettingsViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, string?, Task<string?>> _prompt;
    private readonly Func<string, string, Task> _show;
    private readonly Func<CancellationToken, Task> _bindOidc;

    internal AccountSettingsViewModel(LocalizationService localization, ServitiumApiClient client,
        Func<string, string, Task<bool>> confirm, Func<string, string, string?, Task<string?>> prompt,
        Func<string, string, Task> show, Func<CancellationToken, Task> bindOidc) : base(localization)
    {
        _client = client;
        _confirm = confirm;
        _prompt = prompt;
        _show = show;
        _bindOidc = bindOidc;
    }

    private string _baseline = string.Empty;
    private bool _loading;

    public event EventHandler? SignedOut;
    public bool HasData { get; private set => SetProperty(ref field, value); }
    public string UserName { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string DisplayName { get; set { if (SetProperty(ref field, value)) { Edited(); } } } = string.Empty;
    public int LanguageIndex { get; set { if (SetProperty(ref field, value)) { Edited(); } } }
    public string CurrentPassword { get; set { if (SetProperty(ref field, value)) { Edited(); } } } = string.Empty;
    public string NewPassword { get; set { if (SetProperty(ref field, value)) { Edited(); } } } = string.Empty;
    public string ConfirmPassword { get; set { if (SetProperty(ref field, value)) { Edited(); } } } = string.Empty;
    public string TotpMaterial { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsEnrolling)); RefreshCommands(); } } } = string.Empty;
    public string TotpCode { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } } = string.Empty;
    public bool IsEnrolling => TotpMaterial.Length > 0;
    public bool TotpEnabled { get; private set => SetProperty(ref field, value); }
    public bool HasExternalIdentity { get; private set => SetProperty(ref field, value); }
    public bool OidcEnabled { get; private set => SetProperty(ref field, value); }
    public string TotpStatus => Text(TotpEnabled ? "desktop.account-totp-enabled" : "desktop.account-totp-disabled");
    public string ExternalStatus => Text(HasExternalIdentity ? "desktop.account-oidc-bound" : "desktop.account-oidc-unbound");
    public bool CanReload => CanInteract;
    public bool CanEdit => CanInteract && HasData;
    public ObservableCollection<PageListItem> Sessions { get; } = [];
    public ObservableCollection<PageListItem> ApiKeys { get; } = [];
    public PageListItem? SelectedSession { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } }
    public PageListItem? SelectedKey { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } }
    public bool SessionsEmpty => Sessions.Count == 0;
    public bool KeysEmpty => ApiKeys.Count == 0;
    public bool ProfileDirty => HasData && _baseline != ProfileSnapshot();
    public bool IsDirty => ProfileDirty || CurrentPassword.Length > 0 || NewPassword.Length > 0 || ConfirmPassword.Length > 0 || IsEnrolling;
    private string ProfileSnapshot()
    {
        return DisplayName + "\n" + LanguageIndex.ToString(CultureInfo.InvariantCulture);
    }

    public bool CanSaveProfile => CanEdit && ProfileDirty && !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Trim().Length <= 128 && !DisplayName.Any(char.IsControl) && LanguageIndex is 0 or 1;
    public bool CanChangePassword => CanEdit && CurrentPassword.Length > 0 && NewPassword.Length >= 12 && NewPassword == ConfirmPassword;
    public bool CanBeginTotp => CanEdit && !TotpEnabled && !IsEnrolling;
    public bool CanConfirmTotp => CanEdit && IsEnrolling && TotpCode.Length == 6 && TotpCode.All(char.IsAsciiDigit);
    public bool CanRemoveTotp => CanEdit && TotpEnabled;
    public bool CanBindOidc => CanEdit && OidcEnabled && !HasExternalIdentity;
    public bool CanUnbindOidc => CanEdit && HasExternalIdentity;
    public bool CanRevokeKey => CanEdit && SelectedKey?.Value is ApiKeyDto { Revoked: false };
    public bool CanRevokeSession => CanEdit && SelectedSession is not null;
    public Task LoadAsync()
    {
        return RunAsync(LoadCoreAsync);
    }

    private async Task LoadCoreAsync(CancellationToken token)
    {
        PrincipalDto principal = await _client.Session.GetCurrentAsync(token).ConfigureAwait(true);
        AuthenticationBootstrapDto bootstrap = await _client.GetAuthenticationBootstrapAsync(token).ConfigureAwait(true);
        _loading = true;
        try { UserName = principal.User.UserName; DisplayName = principal.User.DisplayName; LanguageIndex = principal.Language == "en-US" ? 1 : 0; _baseline = ProfileSnapshot(); }
        finally { _loading = false; }
        TotpEnabled = principal.User.TotpEnabled; HasExternalIdentity = principal.User.HasExternalIdentity; OidcEnabled = bootstrap.OidcEnabled; OnPropertyChanged(nameof(TotpStatus)); OnPropertyChanged(nameof(ExternalStatus));
        await ReloadListsAsync(token).ConfigureAwait(true); HasData = true; Edited();
    }
    private async Task ReloadListsAsync(CancellationToken token)
    {
        Sessions.Clear(); ApiKeys.Clear(); SelectedSession = null; SelectedKey = null;
        string? cursor = null;
        do
        {
            CursorPage<SessionDto> page = await _client.ListSessionsAsync(cursor, 100, token).ConfigureAwait(true);
            foreach (SessionDto item in page.Items) { Sessions.Add(new(item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), item.Current ? Text("state.current") : Format("desktop.account-session-expires", item.ExpiresAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), item)); }
            cursor = page.NextCursor;
        } while (cursor is not null);
        do
        {
            CursorPage<ApiKeyDto> page = await _client.ListApiKeysAsync(cursor, 100, token).ConfigureAwait(true);
            foreach (ApiKeyDto item in page.Items) { ApiKeys.Add(new(item.Name, Text(item.Revoked ? "desktop.account-key-revoked" : "desktop.account-key-active"), item)); }
            cursor = page.NextCursor;
        } while (cursor is not null);
        OnPropertyChanged(nameof(SessionsEmpty)); OnPropertyChanged(nameof(KeysEmpty));
    }
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private Task SaveProfileAsync()
    {
        return RunAsync(async token =>
    {
        UserDto updated = await _client.UpdateProfileAsync(new(DisplayName.Trim(), LanguageIndex == 1 ? "en-US" : "zh-Hans"), token).ConfigureAwait(true);
        DisplayName = updated.DisplayName; _baseline = ProfileSnapshot(); Edited(); Message = Text("desktop.account-profile-saved");
    });
    }

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private Task ChangePasswordAsync()
    {
        return RunAsync(async token =>
    {
        if (!await _confirm("action.change-password", Text("desktop.account-password-impact")).ConfigureAwait(true)) { return; }
        await _client.ChangePasswordAsync(new(CurrentPassword, NewPassword), token).ConfigureAwait(true);
        CurrentPassword = NewPassword = ConfirmPassword = string.Empty; _baseline = ProfileSnapshot(); SignedOut?.Invoke(this, EventArgs.Empty);
    });
    }

    [RelayCommand(CanExecute = nameof(CanBeginTotp))]
    private Task BeginTotpAsync()
    {
        return RunAsync(async token =>
    {
        TotpSetupDto setup = await _client.BeginTotpAsync(token).ConfigureAwait(true);
        TotpCode = string.Empty; TotpMaterial = Format("desktop.account-totp-material", setup.Secret, setup.OtpAuthUri.AbsoluteUri);
    });
    }

    [RelayCommand(CanExecute = nameof(CanConfirmTotp))]
    private Task ConfirmTotpAsync()
    {
        return RunAsync(async token =>
    {
        TotpRecoveryCodesDto recovery = await _client.ConfirmTotpAsync(new(TotpCode), token).ConfigureAwait(true);
        TotpMaterial = TotpCode = string.Empty; TotpEnabled = true; OnPropertyChanged(nameof(TotpStatus));
        await _show("desktop.account-recovery-title", Text("desktop.account-recovery-help") + Environment.NewLine + string.Join(Environment.NewLine, recovery.RecoveryCodes)).ConfigureAwait(true);
    });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void CancelTotp() { TotpMaterial = TotpCode = string.Empty; }
    [RelayCommand(CanExecute = nameof(CanRemoveTotp))]
    private Task RemoveTotpAsync()
    {
        return RunAsync(async token =>
    {
        if (!await _confirm("action.remove-totp", Text("danger.remove-totp-impact")).ConfigureAwait(true)) { return; }
        await _client.RemoveTotpAsync(token).ConfigureAwait(true); TotpEnabled = false; OnPropertyChanged(nameof(TotpStatus)); Message = Text("common.operation-completed");
    });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task CreateKeyAsync()
    {
        return RunAsync(async token =>
    {
        string? name = await _prompt("action.create-api-key", "field.name", null).ConfigureAwait(true);
        if (name is null) { return; }
        if (string.IsNullOrWhiteSpace(name)) { Message = Text("desktop.account-key-name-required"); return; }
        CreatedApiKeyDto key = await _client.CreateApiKeyAsync(new(name.Trim(), "full", null), token).ConfigureAwait(true);
        await _show("action.create-api-key", Text("desktop.account-key-once") + Environment.NewLine + key.PlaintextKey).ConfigureAwait(true);
        await ReloadListsAsync(token).ConfigureAwait(true);
    });
    }

    [RelayCommand(CanExecute = nameof(CanRevokeKey))]
    private Task RevokeKeyAsync()
    {
        return RunAsync(async token =>
    {
        var key = (ApiKeyDto)SelectedKey!.Value;
        if (!await _confirm("action.revoke-api-key", Format("desktop.account-revoke-key-impact", key.Name)).ConfigureAwait(true)) { return; }
        await _client.RevokeApiKeyAsync(key.Id, token).ConfigureAwait(true); await ReloadListsAsync(token).ConfigureAwait(true);
    });
    }

    [RelayCommand(CanExecute = nameof(CanRevokeSession))]
    private Task RevokeSessionAsync()
    {
        return RunAsync(async token =>
    {
        var session = (SessionDto)SelectedSession!.Value;
        if (!await _confirm("action.revoke-session", Text("danger.revoke-session-impact")).ConfigureAwait(true)) { return; }
        await _client.RevokeSessionAsync(session.Id, token).ConfigureAwait(true);
        if (session.Current) { _baseline = ProfileSnapshot(); CurrentPassword = NewPassword = ConfirmPassword = TotpMaterial = string.Empty; SignedOut?.Invoke(this, EventArgs.Empty); }
        else { await ReloadListsAsync(token).ConfigureAwait(true); }
    });
    }

    [RelayCommand(CanExecute = nameof(CanBindOidc))]
    private Task BindOidcAsync()
    {
        return RunAsync(async token => { await _bindOidc(token).ConfigureAwait(true); HasExternalIdentity = true; OnPropertyChanged(nameof(ExternalStatus)); });
    }

    [RelayCommand(CanExecute = nameof(CanUnbindOidc))]
    private Task UnbindOidcAsync()
    {
        return RunAsync(async token =>
    {
        if (!await _confirm("action.unbind-oidc", Text("danger.unbind-identity-impact")).ConfigureAwait(true)) { return; }
        await _client.RemoveOwnExternalIdentityAsync(token).ConfigureAwait(true); HasExternalIdentity = false; OnPropertyChanged(nameof(ExternalStatus));
    });
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task RefreshAsync() { if (await CanLeaveAsync().ConfigureAwait(true)) { CurrentPassword = NewPassword = ConfirmPassword = TotpMaterial = string.Empty; await LoadAsync().ConfigureAwait(true); } }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SignOutAsync()
    {
        return RunAsync(async token =>
    {
        if (!await _confirm("action.sign-out", Text("desktop.account-signout-impact")).ConfigureAwait(true)) { return; }
        await _client.Session.SignOutAsync(token).ConfigureAwait(true); _baseline = ProfileSnapshot(); CurrentPassword = NewPassword = ConfirmPassword = TotpMaterial = string.Empty; SignedOut?.Invoke(this, EventArgs.Empty);
    });
    }

    public async Task<bool> CanLeaveAsync()
    {
        return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.settings-discard")).ConfigureAwait(true);
    }

    private void Edited() { if (_loading) { return; } OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(ProfileDirty)); RefreshCommands(); }
    protected override void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSaveProfile)); OnPropertyChanged(nameof(CanChangePassword)); OnPropertyChanged(nameof(CanBeginTotp)); OnPropertyChanged(nameof(CanConfirmTotp)); OnPropertyChanged(nameof(CanRemoveTotp)); OnPropertyChanged(nameof(CanBindOidc)); OnPropertyChanged(nameof(CanUnbindOidc));
        CancelTotpCommand.NotifyCanExecuteChanged(); SaveProfileCommand.NotifyCanExecuteChanged(); ChangePasswordCommand.NotifyCanExecuteChanged(); BeginTotpCommand.NotifyCanExecuteChanged(); ConfirmTotpCommand.NotifyCanExecuteChanged(); RemoveTotpCommand.NotifyCanExecuteChanged(); CreateKeyCommand.NotifyCanExecuteChanged(); RevokeKeyCommand.NotifyCanExecuteChanged(); RevokeSessionCommand.NotifyCanExecuteChanged(); BindOidcCommand.NotifyCanExecuteChanged(); UnbindOidcCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged(); SignOutCommand.NotifyCanExecuteChanged();
    }
    public override void Dispose() { CurrentPassword = NewPassword = ConfirmPassword = TotpMaterial = TotpCode = string.Empty; base.Dispose(); }
}
