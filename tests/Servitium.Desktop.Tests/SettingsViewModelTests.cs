using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Servitium.Client;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Settings;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Settings;
using Xunit;
namespace Servitium.Desktop.Tests;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task ServerSaveUsesReadRevisionOnlyChangedFieldsAndNeverResendsStoredSecrets()
    {
        using SettingsHandler handler = new(); using ServitiumApiClient client = Client(handler);
        using ServerSettingsViewModel model = ServerModel(client, "server-security");
        await model.LoadAsync().ConfigureAwait(true);
        Assert.False(model.IsDirty); Assert.False(model.HasValidation); Assert.False(model.CanSave);
        model.UserFailureLimit.Number = 6;
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        UpdateSettingsGroupRequest write = Assert.IsType<UpdateSettingsGroupRequest>(handler.Write);
        Assert.Equal(9, write.ExpectedRevision); Assert.Equal("6", Assert.Single(write.Values).Value);
        Assert.False(write.Values.ContainsKey("tlsCertificatePassword")); Assert.False(model.IsDirty);
        model.TlsCertificatePassword.ClearSecret = true;
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Null(Assert.Single(handler.Write!.Values).Value); Assert.Equal(10, handler.Write.ExpectedRevision);
        Assert.False(model.TlsCertificatePassword.ClearSecret);
    }
    [Fact]
    public async Task ConflictsKeepEditsAndPrivilegedChangesRespectConfirmation()
    {
        using SettingsHandler handler = new(); using ServitiumApiClient client = Client(handler);
        using ServerSettingsViewModel model = ServerModel(client, "server-security", confirm: false);
        await model.LoadAsync().ConfigureAwait(true); model.AdvancedRawCommandsEnabled.IsOn = true;
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true); Assert.Null(handler.Write); Assert.True(model.IsDirty);
        model.AdvancedRawCommandsEnabled.IsOn = false; model.UserFailureLimit.Number = 7; handler.Conflict = true;
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.True(model.IsDirty); Assert.Equal(7, model.UserFailureLimit.Number); Assert.True(model.HasMessage);
    }
    [Fact]
    public async Task ServerValidationRejectsUnsafeExposureInvalidLifetimesAndNaNNumbers()
    {
        using SettingsHandler handler = new(); using ServitiumApiClient client = Client(handler);
        using ServerSettingsViewModel model = ServerModel(client, "server-security");
        await model.LoadAsync().ConfigureAwait(true);
        model.ListenAddress.Value = "0.0.0.0"; Assert.False(model.CanSave); Assert.True(model.HasValidation);
        model.ListenAddress.Value = "127.0.0.1"; model.SessionAbsoluteHours.Number = 3; model.SessionIdleHours.Number = 4; Assert.True(model.HasValidation);
        model.SessionAbsoluteHours.Number = 168; model.SessionIdleHours.Number = double.NaN; Assert.True(model.HasValidation);
        model.SessionIdleHours.Number = 12; model.ListenPort.Number = 70000; Assert.False(model.CanSave);
    }
    [Fact]
    public async Task OrdinaryUserCannotReadOrChangeServerSettingsAndBackupRequiresOwner()
    {
        using SettingsHandler handler = new(); using ServitiumApiClient client = Client(handler);
        using ServerSettingsViewModel user = ServerModel(client, "server-security", "User");
        await user.LoadAsync().ConfigureAwait(true); Assert.Equal(0, handler.Requests); Assert.False(user.HasData); Assert.False(user.CanEdit);
        using ServerSettingsViewModel admin = ServerModel(client, "server-about", "Administrator");
        await admin.LoadAsync().ConfigureAwait(true); Assert.True(admin.CanEdit); Assert.False(admin.CanBackup);
        Assert.False(new SettingsNavigationViewModel(new(), false, "Test").CanOpen("server-security"));
        Assert.True(new SettingsNavigationViewModel(new(), false, "Test").CanOpen("connections"));
    }
    [Fact]
    public async Task LocalCategorySavePreservesOtherPreferencesAndUnsavedEditsAreGuarded()
    {
        using DesktopSettingsService settings = new(Path.Combine(Path.GetTempPath(), "Servitium-settings-" + Guid.NewGuid().ToString("N")));
        using LocalPreferencesViewModel model = new(new(), settings, "appearance", (_, _) => Task.FromResult(false), _ => { }, TimeProvider.System, false);
        model.ThemeIndex = 2; Assert.True(model.CanSave); Assert.False(await model.CanLeaveAsync().ConfigureAwait(true));
        await settings.UpdateAsync(current => current with { TerminalFontSize = 22 }, CancellationToken.None).ConfigureAwait(true);
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("dark", settings.Current.Theme); Assert.Equal(22, settings.Current.TerminalFontSize); Assert.False(model.IsDirty);
        using LocalPreferencesViewModel terminal = new(new(), settings, "terminal", (_, _) => Task.FromResult(true), _ => { }, TimeProvider.System, false);
        terminal.TerminalFontSize = double.NaN; Assert.False(terminal.CanSave); Assert.True(terminal.HasValidation);
    }
    [Fact]
    public async Task ChangingNotificationCategoriesDoesNotExtendAnExistingPause()
    {
        using DesktopSettingsService settings = new(Path.Combine(Path.GetTempPath(), "Servitium-settings-" + Guid.NewGuid().ToString("N")));
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(50);
        await settings.UpdateAsync(current => current with { NotificationsPaused = true, NotificationsPausedUntil = deadline }, CancellationToken.None).ConfigureAwait(true);
        int callbacks = 0;
        using LocalPreferencesViewModel model = new(new(), settings, "notifications", (_, _) => Task.FromResult(true), _ => callbacks++, TimeProvider.System, true);
        model.NodeOnline = true; await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(deadline, settings.Current.NotificationsPausedUntil); Assert.Equal(0, callbacks);
        model.PauseIndex = 3; await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Null(settings.Current.NotificationsPausedUntil); Assert.False(settings.Current.NotificationsPaused); Assert.Equal(1, callbacks);
    }
    [Fact]
    public async Task ConnectionManagementIsLocalRejectsCredentialUrlsAndConfirmsRemovalInline()
    {
        using DesktopSettingsService settings = new(Path.Combine(Path.GetTempPath(), "Servitium-settings-" + Guid.NewGuid().ToString("N")));
        ServerProfileService profiles = new(settings, TimeProvider.System);
        _ = await profiles.AddOrSelectAsync(new Uri("http://127.0.0.1:9650"), CancellationToken.None).ConfigureAwait(true);
        Uri? requested = null;
        using ConnectionsViewModel model = new(new(), profiles, address => { requested = address; return Task.CompletedTask; });
        model.Address = "https://user:secret@example.test"; Assert.False(model.AddCommand.CanExecute(null));
        model.Address = "https://example.test/servitium"; Assert.True(model.AddCommand.CanExecute(null));
        await model.AddCommand.ExecuteAsync(null).ConfigureAwait(true); Assert.Equal(model.Address, requested!.AbsoluteUri);
        model.Selected = model.Profiles.Single(item => item.Id != profiles.Active.Id);
        model.DeleteCommand.Execute(null); Assert.True(model.IsConfirmingDelete); Assert.Equal(2, profiles.Profiles.Count);
        model.CancelDeleteCommand.Execute(null); Assert.False(model.IsConfirmingDelete); Assert.Equal(2, profiles.Profiles.Count);
        model.Selected = model.Profiles.Single(item => item.Id == profiles.Active.Id); Assert.False(model.CanDelete);
    }
    [Fact]
    public async Task AccountCommandsValidatePasswordAndHideSecretsAfterTotpConfirmation()
    {
        using SettingsHandler handler = new(); using ServitiumApiClient client = Client(handler);
        string? shown = null;
        using AccountSettingsViewModel model = new(new(), client, (_, _) => Task.FromResult(false), (_, _, _) => Task.FromResult<string?>(null), (_, value) => { shown = value; return Task.CompletedTask; }, _ => Task.CompletedTask);
        await model.LoadAsync().ConfigureAwait(true); Assert.True(model.HasData); Assert.True(model.CanBeginTotp); Assert.False(model.CanChangePassword);
        model.CurrentPassword = "synthetic-current"; model.NewPassword = "synthetic-new-password"; model.ConfirmPassword = "different"; Assert.False(model.CanChangePassword);
        model.ConfirmPassword = model.NewPassword; Assert.True(model.CanChangePassword);
        await model.ChangePasswordCommand.ExecuteAsync(null).ConfigureAwait(true); Assert.False(handler.PasswordChanged);
        await model.BeginTotpCommand.ExecuteAsync(null).ConfigureAwait(true); Assert.True(model.IsEnrolling); Assert.False(model.CanConfirmTotp);
        model.TotpCode = "123456"; await model.ConfirmTotpCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.False(model.IsEnrolling); Assert.Equal(string.Empty, model.TotpCode); Assert.True(model.TotpEnabled); Assert.Contains("synthetic-recovery", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-recovery", model.Message, StringComparison.Ordinal); Assert.False(model.CanBindOidc);
    }
    private static ServerSettingsViewModel ServerModel(ServitiumApiClient client, string category, string role = "Owner", bool confirm = true)
    {
        return new(new(), client, category, role, (_, _) => Task.FromResult(confirm), (_, _) => Task.FromResult<string?>(null), TimeProvider.System);
    }

    private static ServitiumApiClient Client(HttpMessageHandler handler)
    {
        return new(new Uri("http://localhost/"), handler: handler);
    }

    private sealed partial class SettingsHandler : HttpMessageHandler
    {
        internal SettingsDto Settings { get; private set; } = Defaults();
        internal UpdateSettingsGroupRequest? Write { get; private set; }
        internal bool Conflict { get; set; }
        internal bool PasswordChanged { get; private set; }
        internal int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/settings" && request.Method == HttpMethod.Get) { return Json(Settings); }
            if (path.StartsWith("/api/v1/settings/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
            {
                Write = await request.Content!.ReadFromJsonAsync<UpdateSettingsGroupRequest>(cancellationToken).ConfigureAwait(false);
                if (Conflict) { return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { code = "concurrency-conflict" }) }; }
                SettingsGroupDto old = Settings.Groups.Single(group => path.EndsWith('/' + group.Group, StringComparison.Ordinal));
                Dictionary<string, string?> values = new(old.Values);
                foreach ((string key, string? value) in Write!.Values) { values[key] = value; }
                SettingsGroupDto saved = old with { Values = values, Revision = old.Revision + 1 };
                Settings = new([.. Settings.Groups.Select(group => group.Group == old.Group ? saved : group)]); return Json(saved);
            }
            if (path == "/api/v1/settings/system-info") { return Json(new SystemInfoDto("1", "Windows", "x64", "test", 1, "10", "test", "v1", "test", DateTimeOffset.UnixEpoch, null, 0, 0, 1, 0, "test")); }
            if (path == "/api/v1/auth/me" && request.Method == HttpMethod.Get) { return Json(new PrincipalDto(new(Guid.NewGuid(), "synthetic", "Synthetic", "User", false, null, DateTimeOffset.UnixEpoch), [], "zh-Hans")); }
            if (path == "/api/v1/auth/bootstrap") { return Json(new AuthenticationBootstrapDto(true, false, null, null, false, null)); }
            if (path == "/api/v1/auth/me/sessions") { return Json(new CursorPage<SessionDto>([], null)); }
            if (path == "/api/v1/auth/me/api-keys") { return Json(new CursorPage<ApiKeyDto>([], null)); }
            if (path == "/api/v1/auth/me/totp") { return Json(new TotpSetupDto("synthetic-secret", new Uri("otpauth://totp/synthetic"))); }
            if (path == "/api/v1/auth/me/totp/confirm") { return Json(new TotpRecoveryCodesDto(["synthetic-recovery"])); }
            if (path == "/api/v1/auth/me/password") { PasswordChanged = true; return new(HttpStatusCode.NoContent); }
            throw new InvalidOperationException("Unexpected settings request: " + request.Method + " " + path);
        }
        private static HttpResponseMessage Json<T>(T value)
        {
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }

        // 与现有 Server 默认响应形状对照；测试目标是增量保存、秘密保留和权限，不重复测试默认值。
        private static SettingsDto Defaults()
        {
            return new([
            new("general", new Dictionary<string, string?>(StringComparer.Ordinal) { ["defaultLanguage"] = "zh-Hans", ["displayTimeZone"] = "local", ["activityRetentionDays"] = "180", ["auditRetentionDays"] = "365", ["operationRetentionDays"] = "30", ["metricRetentionHours"] = "24", ["serviceLogRetentionDays"] = "14", ["databaseBackupEnabled"] = "true", ["databaseBackupRetentionCount"] = "14", ["publicPageEnabled"] = "false" }, 9, false),
            new("appearance", new Dictionary<string, string?>(StringComparer.Ordinal) { ["sidebarPosition"] = "left", ["pageTitle"] = "Servitium", ["loginTitleZhHans"] = null, ["loginDescriptionZhHans"] = null, ["loginSupportZhHans"] = null, ["loginTitleEnUs"] = null, ["loginDescriptionEnUs"] = null, ["loginSupportEnUs"] = null }, 9, false),
            new("security", new Dictionary<string, string?>(StringComparer.Ordinal) { ["listenAddress"] = "127.0.0.1", ["listenPort"] = "5344", ["pathBase"] = null, ["publicBaseUrl"] = null, ["corsOrigins"] = null, ["responseCompressionEnabled"] = "true", ["trustedProxyMode"] = "false", ["trustedProxyNetworks"] = null, ["forwardedForHeader"] = "X-Forwarded-For", ["tlsCertificateSource"] = "none", ["tlsCertificatePath"] = null, ["tlsCertificatePassword"] = "••••••", ["tlsCertificateThumbprint"] = null, ["totpDriftSteps"] = "1", ["userFailureLimit"] = "5", ["ipFailureLimit"] = "20", ["ipFailureWindowMinutes"] = "10", ["ipBanMinutes"] = "30", ["sessionIpValidationEnabled"] = "false", ["userFileManagementEnabled"] = "true", ["apiKeysEnabled"] = "true", ["archiveConcurrencyLimit"] = "2", ["downloadConcurrencyLimit"] = "8", ["uploadConcurrencyLimit"] = "4", ["privateSourceAllowList"] = null, ["sessionIdleHours"] = "12", ["sessionAbsoluteHours"] = "168", ["advancedRawCommandsEnabled"] = "false", ["embedAllowedOrigins"] = null }, 9, false),
            new("identity", new Dictionary<string, string?>(StringComparer.Ordinal) { ["oidcEnabled"] = "false", ["oidcProviderDisplayName"] = null, ["oidcIssuer"] = null, ["oidcClientId"] = null, ["oidcClientSecret"] = "••••••", ["oidcScopes"] = "openid profile email", ["oidcAutomaticRedirect"] = "false", ["oidcSubjectClaim"] = "sub", ["oidcUserNameClaim"] = "preferred_username" }, 9, false)
        ]);
        }
    }
}
