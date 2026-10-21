using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Contracts.Identity;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;

namespace Servitium.Desktop.ViewModels.Authentication;

internal sealed partial class SetupViewModel(
    LocalizationService localization,
    Func<InitializeRequest, CancellationToken, Task> initialize,
    IPageNavigator navigator) : DesktopViewModel(localization)
{
    public string UserName { get; set { if (SetProperty(ref field, value)) { Validate(); } } } = string.Empty;
    public string Password { get; set { if (SetProperty(ref field, value)) { Validate(); } } } = string.Empty;
    public string ConfirmPassword { get; set { if (SetProperty(ref field, value)) { Validate(); } } } = string.Empty;
    public string Language { get; set => SetProperty(ref field, value); } = "zh-Hans";
    public string ValidationMessage => string.IsNullOrWhiteSpace(UserName) || UserName.Trim().Length > 64 || UserName.Any(char.IsControl)
        ? Text("desktop.username-policy")
        : Password.Length < 12 ? Text("desktop.password-policy")
        : Password != ConfirmPassword ? Text("desktop.password-mismatch") : string.Empty;

    private bool CanSubmit() { return CanInteract && ValidationMessage.Length == 0; }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync()
    {
        return RunAsync(async token =>
        {
            await initialize(new InitializeRequest(UserName.Trim(), UserName.Trim(), Password, Language, null), token).ConfigureAwait(true);
            Password = string.Empty;
            ConfirmPassword = string.Empty;
            navigator.Navigate("PG-002", false);
        });
    }

    private void Validate() { OnPropertyChanged(nameof(ValidationMessage)); SubmitCommand.NotifyCanExecuteChanged(); }

    protected override void RefreshCommands() { SubmitCommand.NotifyCanExecuteChanged(); }

    public override void Dispose()
    {
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        base.Dispose();
    }
}
