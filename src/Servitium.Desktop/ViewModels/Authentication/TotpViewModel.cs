using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;

namespace Servitium.Desktop.ViewModels.Authentication;

internal sealed partial class TotpViewModel(
    LocalizationService localization,
    Func<string, CancellationToken, Task> verify,
    Func<CancellationToken, Task> cancel,
    IPageNavigator navigator) : DesktopViewModel(localization)
{
    public string Code { get; set { if (SetProperty(ref field, value)) { SubmitCommand.NotifyCanExecuteChanged(); } } } = string.Empty;
    public bool UseRecoveryCode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            Code = string.Empty;
            OnPropertyChanged(nameof(CodeLabel));
            SubmitCommand.NotifyCanExecuteChanged();
        }
    }

    public string CodeLabel => Text(UseRecoveryCode ? "field.recovery-code" : "field.code");
    private bool CanSubmit()
    {
        return CanInteract && (UseRecoveryCode ? !string.IsNullOrWhiteSpace(Code) : Code.Length == 6 && Code.All(char.IsAsciiDigit));
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync()
    {
        return RunAsync(async token =>
        {
            await verify(Code.Trim(), token).ConfigureAwait(true);
            Code = string.Empty;
            navigator.Navigate("PG-040", false);
        });
    }

    private bool CanGoBack() { return CanInteract; }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync()
    {
        return RunAsync(async token =>
        {
            await cancel(token).ConfigureAwait(true);
            navigator.Navigate("PG-002", false);
        });
    }

    protected override void RefreshCommands() { SubmitCommand.NotifyCanExecuteChanged(); BackCommand.NotifyCanExecuteChanged(); }

    public override void Dispose()
    {
        Code = string.Empty;
        base.Dispose();
    }
}
