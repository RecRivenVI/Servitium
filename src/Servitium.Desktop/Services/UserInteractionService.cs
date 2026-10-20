using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using Servitium.Contracts.Settings;
using Servitium.Desktop.Models;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Servitium.Desktop.Services;

internal sealed class UserInteractionService(Window window, LocalizationService localization)
{
    internal async Task<string?> PromptAsync(string titleKey, string fieldKey, string? initialValue = null)
    {
        TextBox input = new()
        {
            Header = localization.Get(fieldKey),
            Text = initialValue ?? string.Empty,
            MinWidth = 360,
        };
        ContentDialog dialog = Dialog(titleKey, input);
        dialog.PrimaryButtonText = localization.Get("common.confirm");
        dialog.CloseButtonText = localization.Get("common.cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Text : null;
    }

    internal async Task<bool> ConfirmAsync(string titleKey, string message)
    {
        TextBlock content = new() { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        ContentDialog dialog = Dialog(titleKey, content);
        dialog.PrimaryButtonText = localization.Get("common.confirm");
        dialog.CloseButtonText = localization.Get("common.cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    internal async Task<bool> ConfirmNamedAsync(string titleKey, string expectedName, string impact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        StackPanel content = new() { Spacing = 12, MinWidth = 420 };
        content.Children.Add(new TextBlock { Text = impact, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        content.Children.Add(new TextBlock
        {
            Text = localization.Get("common.type-name-confirm") + " " + expectedName,
            TextWrapping = TextWrapping.Wrap,
        });
        TextBox input = new() { Header = localization.Get("common.confirm-name") };
        content.Children.Add(input);
        ContentDialog dialog = Dialog(titleKey, content);
        dialog.PrimaryButtonText = localization.Get("common.confirm");
        dialog.CloseButtonText = localization.Get("common.cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(input.Text, expectedName, StringComparison.Ordinal);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    internal async Task ShowAsync(string titleKey, string message)
    {
        ContentDialog dialog = Dialog(titleKey, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 });
        dialog.CloseButtonText = localization.Get("common.close");
        _ = await dialog.ShowAsync();
    }

    internal async Task ShowNodeRegistrationAsync(NodeRegistrationInstructions instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        StackPanel panel = new() { Spacing = 12, MinWidth = 560 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = localization.Get("node.registration.one-time-title"),
            Message = localization.Get("node.registration.one-time-message"),
        });
        panel.Children.Add(CopyableValue("field.registration-token", instructions.Token));
        panel.Children.Add(CopyableValue("field.control-plane-address", instructions.ControlPlaneAddress));
        panel.Children.Add(CopyableValue("field.expires-at", instructions.ExpiresAt.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture)));
        panel.Children.Add(new TextBlock
        {
            Text = localization.Get("node.registration.agent-instructions"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        });
        panel.Children.Add(CopyableValue("node.registration.command-template", instructions.CommandTemplate));

        ContentDialog dialog = Dialog("action.create-token", new ScrollViewer { Content = panel, MaxHeight = 560 });
        dialog.CloseButtonText = localization.Get("common.close");
        _ = await dialog.ShowAsync();
    }

    internal async Task ShowImageAsync(string title, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(content);
        using InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream))
        {
            writer.WriteBytes(content);
            _ = await writer.StoreAsync();
            _ = writer.DetachStream();
        }

        stream.Seek(0);
        BitmapImage bitmap = new();
        await bitmap.SetSourceAsync(stream);
        Image image = new()
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            MaxWidth = 960,
            MaxHeight = 680,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = (window.Content as FrameworkElement)?.XamlRoot,
            Title = title,
            Content = image,
            CloseButtonText = localization.Get("common.close"),
        };
        _ = await dialog.ShowAsync();
    }

    internal async Task<IReadOnlyDictionary<string, bool>?> ConfigureNotificationPreferencesAsync(
        IReadOnlyDictionary<string, bool>? current)
    {
        StackPanel panel = new() { Spacing = 8, MinWidth = 420 };
        List<ToggleSwitch> toggles = [];
        foreach (string category in DesktopNotificationService.Categories)
        {
            bool enabled = current?.TryGetValue(category, out bool configured) == true
                ? configured
                : DesktopNotificationService.DefaultEnabled(category);
            ToggleSwitch toggle = new()
            {
                Header = localization.Get("notification." + category + ".title"),
                IsOn = enabled,
                Tag = category,
            };
            toggles.Add(toggle);
            panel.Children.Add(toggle);
        }

        ContentDialog dialog = Dialog("tray.notification-settings", new ScrollViewer { Content = panel, MaxHeight = 520 });
        dialog.PrimaryButtonText = localization.Get("action.save");
        dialog.CloseButtonText = localization.Get("common.cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() != ContentDialogResult.Primary
            ? null
            : (IReadOnlyDictionary<string, bool>)toggles.ToDictionary(
            toggle => (string)toggle.Tag,
            toggle => toggle.IsOn,
            StringComparer.Ordinal);
    }

    internal async Task<DashboardLayoutDto?> ConfigureDashboardLayoutAsync(DashboardLayoutDto current)
    {
        ArgumentNullException.ThrowIfNull(current);
        ListView cards = new()
        {
            AllowDrop = true,
            CanDragItems = true,
            CanReorderItems = true,
            ReorderMode = ListViewReorderMode.Enabled,
            MinWidth = 720,
            MaxHeight = 520,
            SelectionMode = ListViewSelectionMode.None,
        };

        void AddEditor(DashboardCardLayoutDto card)
        {
            DashboardCardEditor editor = new(card, localization);
            ListViewItem item = new()
            {
                Content = editor.Content,
                Tag = editor,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            editor.RemoveButton.IsEnabled = !card.Required;
            editor.RemoveButton.Click += (_, _) => cards.Items.Remove(item);
            cards.Items.Add(item);
        }

        foreach (DashboardCardLayoutDto card in current.Cards)
        {
            AddEditor(card);
        }

        ComboBox kind = new()
        {
            Header = localization.Get("dashboard.editor.card-kind"),
            ItemsSource = new[] { "text", "image", "link", "clock", "spacer", "embed" },
            SelectedIndex = 0,
            MinWidth = 180,
        };
        Button add = new() { Content = localization.Get("action.add-card"), VerticalAlignment = VerticalAlignment.Bottom };
        add.Click += (_, _) =>
        {
            string selectedKind = kind.SelectedItem as string ?? "text";
            AddEditor(new DashboardCardLayoutDto(
                "custom-" + Guid.NewGuid().ToString("N"),
                selectedKind,
                null,
                null,
                6,
                "medium",
                Hidden: false,
                Required: false,
                new Dictionary<string, string?>(StringComparer.Ordinal)));
        };

        StackPanel addRow = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        addRow.Children.Add(kind);
        addRow.Children.Add(add);
        StackPanel panel = new() { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = localization.Get("dashboard.editor.instructions"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720,
        });
        panel.Children.Add(cards);
        panel.Children.Add(addRow);

        ContentDialog dialog = Dialog("action.customize", panel);
        dialog.PrimaryButtonText = localization.Get("action.save");
        dialog.CloseButtonText = localization.Get("common.cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        DashboardCardLayoutDto[] result = [.. cards.Items
            .OfType<ListViewItem>()
            .Select(item => ((DashboardCardEditor)item.Tag).Build())];
        return current with { Cards = result };
    }

    internal async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        FileOpenPicker picker = new();
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        Initialize(picker);
        StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    internal async Task<string?> PickFolderAsync()
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    internal async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        FileSavePicker picker = new() { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(extension, [extension]);
        Initialize(picker);
        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    internal static Task<bool> LaunchUriAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Windows.System.Launcher.LaunchUriAsync(uri).AsTask();
    }

    private ContentDialog Dialog(string titleKey, object content)
    {
        return new ContentDialog
        {
            XamlRoot = (window.Content as FrameworkElement)?.XamlRoot,
            Title = localization.Get(titleKey),
            Content = content,
        };
    }

    private Grid CopyableValue(string labelKey, string value)
    {
        Grid row = new() { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBox text = new()
        {
            Header = localization.Get(labelKey),
            Text = value,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
        };
        Button copy = new()
        {
            Content = localization.Get("action.copy"),
            Tag = value,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        copy.Click += static (sender, _) =>
        {
            if (sender is Button { Tag: string textToCopy })
            {
                DataPackage package = new();
                package.SetText(textToCopy);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
        };
        row.Children.Add(text);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return row;
    }

    private void Initialize(object picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    private sealed class DashboardCardEditor
    {
        private static readonly string[] Heights = ["mini", "small", "medium", "large", "extra-large", "adaptive"];
        private readonly DashboardCardLayoutDto _card;
        private readonly TextBox _title;
        private readonly TextBox _description;
        private readonly TextBox _value;
        private readonly NumberBox _width;
        private readonly ComboBox _height;
        private readonly ToggleSwitch _hidden;

        internal DashboardCardEditor(DashboardCardLayoutDto card, LocalizationService localization)
        {
            _card = card;
            _title = new TextBox { Header = localization.Get("field.name"), Text = card.Title ?? string.Empty };
            _description = new TextBox { Header = localization.Get("field.description"), Text = card.Description ?? string.Empty };
            _value = new TextBox
            {
                Header = localization.Get("field.content"),
                Text = card.Settings.GetValueOrDefault("value") ?? string.Empty,
                Visibility = card.Kind is "clock" or "spacer" ? Visibility.Collapsed : Visibility.Visible,
            };
            _width = new NumberBox
            {
                Header = localization.Get("dashboard.editor.width"),
                Minimum = 1,
                Maximum = 12,
                Value = card.Width,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            _height = new ComboBox
            {
                Header = localization.Get("dashboard.editor.height"),
                ItemsSource = Heights,
                SelectedItem = card.Height,
            };
            _hidden = new ToggleSwitch { Header = localization.Get("dashboard.editor.hidden"), IsOn = card.Hidden };
            RemoveButton = new Button { Content = localization.Get("action.remove-card") };

            Grid dimensions = new() { ColumnSpacing = 12 };
            dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dimensions.Children.Add(_width);
            Grid.SetColumn(_height, 1);
            dimensions.Children.Add(_height);
            Grid.SetColumn(_hidden, 2);
            dimensions.Children.Add(_hidden);

            StackPanel body = new() { Spacing = 8, Padding = new Thickness(8) };
            body.Children.Add(new TextBlock { Text = card.Kind + " · " + card.Id, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(_title);
            body.Children.Add(_description);
            body.Children.Add(_value);
            body.Children.Add(dimensions);
            body.Children.Add(RemoveButton);
            Content = body;
        }

        internal UIElement Content { get; }
        internal Button RemoveButton { get; }

        internal DashboardCardLayoutDto Build()
        {
            Dictionary<string, string?> settings = new(_card.Settings, StringComparer.Ordinal)
            {
                ["value"] = NullIfWhiteSpace(_value.Text),
            };
            return _card with
            {
                Title = NullIfWhiteSpace(_title.Text),
                Description = NullIfWhiteSpace(_description.Text),
                Width = Math.Clamp((int)Math.Round(double.IsNaN(_width.Value) ? _card.Width : _width.Value), 1, 12),
                Height = _height.SelectedItem as string ?? _card.Height,
                Hidden = _hidden.IsOn,
                Settings = settings,
            };
        }

        private static string? NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
