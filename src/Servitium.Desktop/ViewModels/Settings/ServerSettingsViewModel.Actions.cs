using System.IO;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Contracts.Settings;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ServerSettingsViewModel
{
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task TestOidcAsync()
    {
        return RunAsync(async token =>
    {
        SettingsTestResultDto result = await _client.TestOidcAsync(new(OidcIssuer.Value), token).ConfigureAwait(true);
        Severity = result.Succeeded ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        Message = Text(result.Succeeded ? "desktop.settings-test-success" : "desktop.settings-oidc-failed");
    });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task TestTlsAsync()
    {
        return RunAsync(async token =>
    {
        SettingsTestResultDto result = await _client.TestTlsAsync(new(TlsCertificatePath.Value, TlsCertificatePassword.Value.Length == 0 ? null : TlsCertificatePassword.Value), token).ConfigureAwait(true);
        Severity = result.Succeeded ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        Message = Text(result.Succeeded ? "desktop.settings-test-success" : "desktop.settings-tls-failed");
    });
    }

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private Task BackupAsync()
    {
        return RunAsync(async token =>
    {
        DatabaseBackupDto backup = await _client.BackupDatabaseAsync(token).ConfigureAwait(true);
        Message = Format("desktop.settings-backup-created", backup.FileName, backup.CreatedAt.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture));
    });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ExportDiagnosticsAsync()
    {
        return RunAsync(async token =>
    {
        string? path = await _saveFile("servitium-diagnostics.zip", ".zip").ConfigureAwait(true);
        if (path is null) { return; }
        FileStream destination = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await using (destination.ConfigureAwait(false))
        { _ = await _client.DownloadDiagnosticsAsync(destination, token).ConfigureAwait(true); }
        Message = Text("desktop.settings-diagnostics-saved");
    });
    }
}
