using System;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client.Session;
using Servitium.Contracts.Identity;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;

namespace Servitium.Desktop.ViewModels.Authentication;

internal sealed partial class LoginViewModel(
    LocalizationService localization,
    Func<SignInRequest, CancellationToken, Task<SignInResult>> signIn,
    Func<CancellationToken, Task> externalSignIn,
    Func<CancellationToken, Task<AuthenticationBootstrapDto>> bootstrap,
    IPageNavigator navigator) : DesktopViewModel(localization)
{
    public string UserName { get; set { if (SetProperty(ref field, value)) { SubmitCommand.NotifyCanExecuteChanged(); } } } = string.Empty;
    public string Password { get; set { if (SetProperty(ref field, value)) { SubmitCommand.NotifyCanExecuteChanged(); } } } = string.Empty;
    public bool ExternalEnabled { get; private set => SetProperty(ref field, value); }
    public string ExternalLabel { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string LoginNote { get; private set => SetProperty(ref field, value); } = string.Empty;
    public bool HasLoginNote => LoginNote.Length > 0;
    public bool IsRedirecting { get; private set => SetProperty(ref field, value); }
    public string RedirectNote { get; private set => SetProperty(ref field, value); } = string.Empty;
    private bool _cancelRedirect;

    internal Task LoadAsync()
    {
        return RunAsync(async token =>
        {
            AuthenticationBootstrapDto state = await bootstrap(token).ConfigureAwait(true);
            ExternalEnabled = state.OidcEnabled;
            ExternalLabel = state.OidcDisplayName ?? Text("action.external-sign-in");
            LoginNote = state.LoginPageText ?? string.Empty;
            OnPropertyChanged(nameof(HasLoginNote));
            if (state.OidcEnabled && state.AutoRedirectToOidc)
            {
                IsRedirecting = true;
                for (int remaining = 5; remaining > 0 && !_cancelRedirect; remaining--)
                {
                    RedirectNote = Format("desktop.oidc-countdown", remaining);
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(true);
                }
                IsRedirecting = false;
                RedirectNote = string.Empty;
                if (!_cancelRedirect)
                {
                    await externalSignIn(token).ConfigureAwait(true);
                    navigator.Navigate("PG-040", false);
                }
            }
        });
    }

    private bool CanSubmit() { return CanInteract && !string.IsNullOrWhiteSpace(UserName) && Password.Length > 0; }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync()
    {
        return RunAsync(async token =>
        {
            SignInResult result = await signIn(new SignInRequest(UserName.Trim(), Password), token).ConfigureAwait(true);
            Password = string.Empty;
            navigator.Navigate(result.TotpRequired ? "PG-003" : "PG-040", false);
        });
    }

    private bool CanUseExternal() { return CanInteract && ExternalEnabled; }

    [RelayCommand]
    private void CancelRedirect()
    {
        _cancelRedirect = true;
        IsRedirecting = false;
        RedirectNote = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanUseExternal))]
    private Task ExternalAsync()
    {
        return RunAsync(async token =>
        {
            await externalSignIn(token).ConfigureAwait(true);
            navigator.Navigate("PG-040", false);
        });
    }

    protected override void RefreshCommands()
    {
        SubmitCommand.NotifyCanExecuteChanged();
        ExternalCommand.NotifyCanExecuteChanged();
    }

    public override void Dispose()
    {
        Password = string.Empty;
        base.Dispose();
    }
}
