using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Authentication;

namespace Servitium.Desktop.Views.Authentication;

internal sealed partial class TotpPage : Page
{
    public TotpViewModel ViewModel { get; }

    internal TotpPage(AppServices services, IPageNavigator navigator)
    {

        ViewModel = new TotpViewModel(services.Localization, services.Session.CompleteTotpAsync, services.Session.SignOutAsync, navigator);

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {

        if (!App.IsIsolatedTest) { _ = UserNameBox.Focus(FocusState.Keyboard); }
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        AuthenticationFormLayout.Arrange(LeadingSpace, TrailingSpace, args.NewSize.Width);
    }

    private void OnSubmitInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.SubmitCommand.CanExecute(null)) { return; }
        ViewModel.SubmitCommand.Execute(null);
        args.Handled = true;
    }
}
