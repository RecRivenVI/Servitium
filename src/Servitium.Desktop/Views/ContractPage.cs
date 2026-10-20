using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Settings;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Servitium.Desktop.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WinUI owns page lifetime; Unloaded cancels outstanding work and disposes the embedded WebView and cancellation source.")]
internal abstract partial class ContractPage : Page, INavigationGuard
{
    private static readonly HashSet<string> DangerousActions = new(StringComparer.Ordinal)
    {
        "change-visibility", "delete", "delete-channel", "delete-runtime-files", "delete-working-directory", "force-stop",
        "remove-node", "remove-totp", "reset-dashboard", "reset-totp", "revoke", "revoke-all-sessions", "revoke-api-key",
        "revoke-session", "rollback", "rotate-credential", "rotate-token", "sync-content", "unbind-identity", "unbind-oidc", "unregister",
    };
    private static readonly HashSet<string> BooleanFields = new(StringComparer.Ordinal)
    {
        "advanced-mode", "advanced-raw-commands", "auto-restart", "auto-start", "descending", "enabled", "graceful-shutdown", "overwrite", "query-enabled", "rcon-enabled", "terminal-ansi", "terminal-conpty",
    };
    private static readonly HashSet<string> NumericFields = new(StringComparer.Ordinal)
    {
        "auto-start-delay", "compression-task-concurrency", "download-rate", "file-task-concurrency", "maximum-heap", "maximum-restart-attempts",
        "maximum-schedules", "metric-interval", "minimum-heap", "primary-port", "query-port", "rcon-port", "remaining-runs", "restart-interval",
        "registration-lifetime", "restart-reset", "retention-count", "shutdown-wait", "stop-timeout", "terminal-buffer-bytes", "terminal-buffer-lines", "terminal-columns",
        "terminal-rows", "upload-rate", "url-download-concurrency",
    };

    private readonly UiPageContract _contract;
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    private readonly PageActionDispatcher _dispatcher;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListView> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PageListItem>> _sectionItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _loadMoreButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _sectionCards = new(StringComparer.Ordinal);
    private readonly Grid _dashboardGrid = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly List<IDisposable> _dashboardEmbeds = [];
    private readonly List<Control> _commands = [];
    private readonly InfoBar _status = new() { IsOpen = false, IsClosable = true };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly InfoBar _dropStatus = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly ProgressBar _transferProgress = new() { Minimum = 0, Maximum = 100 };
    private readonly TextBlock _transferSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _transferPauseButton = new();
    private readonly Button _transferCancelButton = new();
    private readonly StackPanel _settingsEditor = new() { Spacing = 12 };
    private readonly TextBlock _settingsEditorTitle = new();
    private readonly TextBlock _instanceCreationReview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _instanceOwnershipNotice = new() { IsOpen = true, IsClosable = false };
    private readonly CancellationTokenSource _lifetime = new();
    private Grid? _pageHeader;
    private TextBlock? _pageTitle;
    private CommandBar? _pageCommands;
    private RestrictedWebViewHost? _embeddedHost;
    private StackPanel? _structuredConfigurationEditor;
    private Border? _transferCard;
    private CancellationTokenSource? _operation;
    private PageListItem? _selectedItem;
    private bool _busy;
    private bool _structuredConfigurationDirty;
    private bool _applyingStructuredConfiguration;
    private bool _settingsDirty;
    private bool _applyingSettings;
    private bool _suppressSettingsSelection;
    private bool _applyingInstanceChoices;
    private SettingsGroupDto? _currentSettingsGroup;
    private bool _transferPaused;

    protected ContractPage(string pageId, AppServices services, IPageNavigator navigator)
    {
        _contract = UiContractCatalog.Get(pageId);
        _services = services;
        _navigator = navigator;
        _dispatcher = new PageActionDispatcher(services);
        _dispatcher.TransferProgressChanged += OnTransferProgressChanged;
        AutomationProperties.SetAutomationId(this, _contract.Id);
        if (SupportsDrop(_contract.Id))
        {
            AllowDrop = true;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
        }
        Content = BuildContent();
        ConfigureKeyboardAccelerators();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal string PageId => _contract.Id;

    private ScrollViewer BuildContent()
    {
        StackPanel root = new()
        {
            Spacing = 16,
            Padding = new Thickness(28, 22, 28, 40),
            MaxWidth = 1280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        root.SizeChanged += OnPageRootSizeChanged;
        root.Children.Add(BuildPageHeader());
        if (_contract.Id != "PG-001")
        {
            root.Children.Add(_status);
            root.Children.Add(_dropStatus);
        }

        root.Children.Add(_progress);
        root.Children.Add(BuildTransferCard());

        if (!CanAccess())
        {
            root.Children.Add(BuildPermissionNotice());
            return CreatePageScrollViewer(root);
        }

        if (_contract.Fields.Count > 0 && _contract.Id != "PG-041")
        {
            foreach (string field in _contract.Fields)
            {
                Control control = CreateField(field);
                if (_contract.Id == "PG-018" && !IsAdministrator && field is not ("name" or "tags"))
                {
                    control.IsEnabled = false;
                }

                _fields.Add(field, control);
            }

            if (_contract.Id == "PG-019")
            {
                root.Children.Add(BuildInstanceCreationWizard());
            }
            else
            {
                foreach (IGrouping<string, string> group in _contract.Fields
                .Where(field => field != "content" || _contract.Id is not ("PG-013" or "PG-015"))
                .GroupBy(field => FieldSection(_contract.Id, field), StringComparer.Ordinal))
                {
                    StackPanel fields = new() { Spacing = 12 };
                    if (_contract.Id == "PG-001" && group.Key == "owner-form")
                    {
                        fields.Children.Add(_status);
                        fields.Children.Add(_dropStatus);
                        fields.Children.Add(BuildFormActions());
                    }

                    foreach (string field in group)
                    {
                        fields.Children.Add(_fields[field]);
                    }

                    if (group.Key == "filters")
                    {
                        Button applyFilters = new()
                        {
                            Content = _services.Localization.Get("action.apply-filters"),
                            HorizontalAlignment = HorizontalAlignment.Left,
                        };
                        applyFilters.Click += OnApplyFilters;
                        AutomationProperties.SetAutomationId(applyFilters, $"{_contract.Id}:action:apply-filters");
                        fields.Children.Add(applyFilters);
                    }

                    root.Children.Add(Card(_services.Localization.Get("section." + group.Key), fields));
                }
            }

        }

        HashSet<string> fieldSections = [.. _contract.Fields.Select(field => FieldSection(_contract.Id, field))];
        if (_contract.Id == "PG-040")
        {
            for (int column = 0; column < 12; column++)
            {
                _dashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        foreach (string section in _contract.Sections)
        {
            if (_contract.Id == "PG-019")
            {
                continue;
            }

            if (!fieldSections.Contains(section) || section is "console" or "editor" or "text-editor" || SectionHasData(_contract.Id, section))
            {
                Border card = BuildSection(section);
                _sectionCards[section] = card;
                if (_contract.Id == "PG-040")
                {
                    _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(card, _dashboardGrid.RowDefinitions.Count - 1);
                    Grid.SetColumnSpan(card, 12);
                    _dashboardGrid.Children.Add(card);
                }
                else
                {
                    root.Children.Add(card);
                }
            }
        }

        if (_contract.Id == "PG-040")
        {
            root.Children.Add(_dashboardGrid);
        }

        if (_contract.Id == "PG-041")
        {
            root.Children.Add(BuildSettingsEditor());
        }

        return CreatePageScrollViewer(root);
    }

    private static ScrollViewer CreatePageScrollViewer(UIElement content)
    {
        Grid contentHost = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            MinHeight = 0,
        };
        contentHost.Children.Add(content);
        return new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 0,
            Content = contentHost,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private void OnPageRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pageHeader is null || _pageTitle is null || _pageCommands is null)
        {
            return;
        }

        bool narrow = e.NewSize.Width < 900;
        bool compact = e.NewSize.Width < 640;
        _pageTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _pageCommands.DefaultLabelPosition = narrow
            ? CommandBarDefaultLabelPosition.Collapsed
            : CommandBarDefaultLabelPosition.Right;
        _pageHeader.RowDefinitions.Clear();
        _pageHeader.ColumnDefinitions.Clear();
        _pageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (narrow)
        {
            _pageHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _pageHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_pageTitle, 0);
            Grid.SetColumnSpan(_pageTitle, 2);
            Grid.SetRow(_pageCommands, 1);
            Grid.SetColumn(_pageCommands, 0);
            Grid.SetColumnSpan(_pageCommands, 2);
            _pageCommands.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            _pageHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_pageTitle, 0);
            Grid.SetColumn(_pageTitle, 0);
            Grid.SetColumnSpan(_pageTitle, 1);
            Grid.SetRow(_pageCommands, 0);
            Grid.SetColumn(_pageCommands, 1);
            Grid.SetColumnSpan(_pageCommands, 1);
            _pageCommands.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private static string FieldSection(string pageId, string field)
    {
        return pageId switch
        {
            "PG-001" => "owner-form",
            "PG-002" => "local-login",
            "PG-003" => "verification",
            "PG-010" or "PG-011" or "PG-014" or "PG-016" or "PG-042" or "PG-043" or "PG-044" or "PG-063" => "filters",
            "PG-012" => "controls",
            "PG-013" when field is "search" or "sort" or "descending" => "filters",
            "PG-013" when field is "url" or "target-file" or "upload" or "overwrite" => "source-input",
            "PG-013" when field is "encoding" or "entry-name-encoding" or "extract-encoding" or "collision-policy" => "text-options",
            "PG-013" => "breadcrumb",
            "PG-015" => "editor",
            "PG-017" => "editor",
            "PG-018" when field is "name" or "tags" or "notes" => "general",
            "PG-018" when field == "working-directory" => "working-directory",
            "PG-018" when field.StartsWith("stop-", StringComparison.Ordinal) || field is "auto-start" or "auto-start-delay" or "auto-restart" or "maximum-restart-attempts" or "restart-interval" or "restart-reset" => "stop",
            "PG-018" when field.StartsWith("terminal-", StringComparison.Ordinal) || field == "shortcut-commands" => "terminal",
            "PG-018" when field.StartsWith("rcon-", StringComparison.Ordinal) => "rcon",
            "PG-018" when field.StartsWith("query-", StringComparison.Ordinal) || field == "primary-port" => "query",
            "PG-018" when field.StartsWith("update-", StringComparison.Ordinal) => "integrated-content",
            "PG-018" => "launch",
            "PG-019" when field == "source-kind" => "source",
            "PG-019" when field is "name" or "tags" or "notes" => "basic",
            "PG-019" when field is "node" or "absolute-path" or "archive" => "source-input",
            "PG-019" => "launch",
            "PG-020" when field is "query" or "status" => "filters",
            "PG-020" => "registration",
            "PG-021" => "configuration",
            "PG-022" when field == "archive" => "import",
            "PG-022" => "registration",
            "PG-030" when field is "query" or "role" => "filters",
            "PG-030" => "editor",
            "PG-031" => "filters",
            "PG-032" when field is "current-password" or "new-password" => "password",
            "PG-032" => "profile",
            "PG-040" => "filters",
            "PG-060" => "create-channel",
            "PG-061" => "settings",
            "PG-062" when field is "source-kind" or "archive" or "prism-archive" or "instance" => "source",
            "PG-062" => "metadata",
            _ => "details",
        };
    }

    private static bool SectionHasData(string pageId, string section)
    {
        return (pageId, section) is ("PG-013", "breadcrumb") or ("PG-031", "grants") or ("PG-061", "settings");
    }

    private Border BuildSettingsEditor()
    {
        _settingsEditorTitle.Text = _services.Localization.Get("settings.select-group");
        _settingsEditorTitle.Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style;
        _settingsEditor.Children.Add(_settingsEditorTitle);
        return Card(_services.Localization.Get("settings.group-editor"), _settingsEditor);
    }

    private TabView BuildInstanceCreationWizard()
    {
        TabView wizard = new()
        {
            IsAddTabButtonVisible = false,
            TabWidthMode = TabViewWidthMode.Equal,
        };
        wizard.TabItems.Add(InstanceCreationStep("instance.create.step.source", ["source-kind"]));
        wizard.TabItems.Add(InstanceCreationStep("instance.create.step.basic", ["name", "tags", "notes"]));
        wizard.TabItems.Add(InstanceCreationStep("instance.create.step.location", ["node", "absolute-path", "archive"]));
        wizard.TabItems.Add(InstanceCreationStep(
            "instance.create.step.runtime",
            ["java-runtime", "minimum-heap", "maximum-heap", "jvm-arguments", "target-kind", "launch", "server-arguments", "environment-variables", "advanced-mode", "advanced-arguments", "stop-command", "stop-timeout"]));

        StackPanel review = new() { Spacing = 12, Padding = new Thickness(4, 12, 4, 12) };
        review.Children.Add(_instanceCreationReview);
        review.Children.Add(_instanceOwnershipNotice);
        wizard.TabItems.Add(new TabViewItem
        {
            Header = _services.Localization.Get("instance.create.step.review"),
            Content = review,
            IsClosable = false,
        });
        wizard.SelectionChanged += (_, _) => UpdateInstanceCreationReview();
        if (_fields["source-kind"] is ComboBox source)
        {
            source.SelectionChanged += (_, _) => UpdateInstanceCreationReview();
        }

        UpdateInstanceCreationReview();
        AutomationProperties.SetAutomationId(wizard, "PG-019:wizard");
        return wizard;
    }

    private TabViewItem InstanceCreationStep(string titleKey, IReadOnlyList<string> fieldNames)
    {
        StackPanel fields = new() { Spacing = 12, Padding = new Thickness(4, 12, 4, 12) };
        foreach (string field in fieldNames)
        {
            fields.Children.Add(_fields[field]);
        }

        return new TabViewItem
        {
            Header = _services.Localization.Get(titleKey),
            Content = new ScrollViewer
            {
                Content = fields,
                MaxHeight = 620,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            IsClosable = false,
        };
    }

    private StackPanel BuildFormActions()
    {
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (string action in _contract.Actions)
        {
            Button button = new()
            {
                Content = _services.Localization.Get("action." + action),
                Tag = action,
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            if (action == _contract.Actions[0] && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accentStyle) && accentStyle is Style style)
            {
                button.Style = style;
            }

            AutomationProperties.SetAutomationId(button, $"{_contract.Id}:action:{action}");
            button.Click += OnAction;
            _commands.Add(button);
            actions.Children.Add(button);
        }

        return actions;
    }

    private void UpdateInstanceCreationReview()
    {
        if (_contract.Id != "PG-019")
        {
            return;
        }

        IReadOnlyDictionary<string, string> fields = ReadFields();
        string source = fields.GetValueOrDefault("source-kind") ?? "empty";
        bool owned = source is not "directory";
        _instanceCreationReview.Text = _services.Localization.Format(
            "instance.create.review-summary",
            fields.GetValueOrDefault("name") ?? string.Empty,
            LocalizedChoiceText("source-kind", source),
            fields.GetValueOrDefault("node") ?? string.Empty,
            fields.GetValueOrDefault("java-runtime") ?? string.Empty,
            fields.GetValueOrDefault("stop-command") ?? string.Empty,
            fields.GetValueOrDefault("stop-timeout") ?? string.Empty);
        _instanceOwnershipNotice.Severity = owned ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        _instanceOwnershipNotice.Title = _services.Localization.Get(owned
            ? "instance.create.owned-title"
            : "instance.create.external-title");
        _instanceOwnershipNotice.Message = _services.Localization.Get(owned
            ? "instance.create.owned-message"
            : "instance.create.external-message");
        SetCommandsEnabled(!_busy);
    }

    private void ShowSettingsGroup(SettingsGroupDto group)
    {
        _applyingSettings = true;
        _fields.Clear();
        _settingsEditor.Children.Clear();
        _settingsEditorTitle.Text = _services.Localization.Get("section." + group.Group);
        _settingsEditorTitle.Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style;
        _settingsEditor.Children.Add(_settingsEditorTitle);
        foreach ((string key, string? value) in group.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string fieldKey = "settingsfield." + ToKebabCase(key);
            Control control;
            if (string.Equals(key, "DefaultLanguage", StringComparison.OrdinalIgnoreCase))
            {
                ComboBox language = new()
                {
                    Header = _services.Localization.Get(fieldKey),
                    ItemsSource = new[]
                    {
                        new FieldChoice("zh-Hans", _services.Localization.Get("language.zh-hans")),
                        new FieldChoice("en-US", _services.Localization.Get("language.en-us")),
                    },
                    DisplayMemberPath = nameof(FieldChoice.Display),
                    SelectedValuePath = nameof(FieldChoice.Value),
                    MinWidth = 320,
                    MaxWidth = 720,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                language.SelectedItem = language.Items.OfType<FieldChoice>().FirstOrDefault(choice =>
                    string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));
                language.SelectionChanged += OnSettingsChanged;
                control = language;
            }
            else if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                control = new ToggleSwitch
                {
                    Header = _services.Localization.Get(fieldKey),
                    IsOn = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                };
                ((ToggleSwitch)control).Toggled += OnSettingsChanged;
            }
            else if (value == "••••••" || key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase))
            {
                control = new PasswordBox
                {
                    Header = _services.Localization.Get(fieldKey),
                    Password = value ?? string.Empty,
                    MinWidth = 320,
                    MaxWidth = 720,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                ((PasswordBox)control).PasswordChanged += OnSettingsChanged;
            }
            else
            {
                control = new TextBox
                {
                    Header = _services.Localization.Get(fieldKey),
                    Text = value ?? string.Empty,
                    AcceptsReturn = key.Contains("Origins", StringComparison.Ordinal) || key.Contains("AllowList", StringComparison.Ordinal),
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 280,
                };
                ((TextBox)control).TextChanged += OnSettingsChanged;
            }

            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.MaxWidth = control is TextBox { AcceptsReturn: true } ? 720 : control is ToggleSwitch ? double.PositiveInfinity : 720;
            AutomationProperties.SetAutomationId(control, $"PG-041:field:{key}");
            _fields[key] = control;
            _settingsEditor.Children.Add(control);
            if (group.Effects?.GetValueOrDefault(key) == "restart-required")
            {
                _settingsEditor.Children.Add(new TextBlock
                {
                    Text = _services.Localization.Get("settings.restart-required"),
                    Foreground = Application.Current.Resources["SystemFillColorCautionBrush"] as Brush,
                });
            }
        }

        _currentSettingsGroup = group;
        _settingsDirty = false;
        _applyingSettings = false;
    }

    private Border BuildTransferCard()
    {
        _transferPauseButton.Content = _services.Localization.Get("action.pause");
        _transferPauseButton.Click += OnTransferPause;
        _transferCancelButton.Content = _services.Localization.Get("action.cancel");
        _transferCancelButton.Click += OnTransferCancel;
        StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(_transferPauseButton);
        controls.Children.Add(_transferCancelButton);
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(_transferSummary);
        body.Children.Add(_transferProgress);
        body.Children.Add(controls);
        _transferCard = Card(_services.Localization.Get("section.task-progress"), body);
        _transferCard.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_transferCard, $"{_contract.Id}:transfer-progress");
        return _transferCard;
    }

    private Grid BuildPageHeader()
    {
        Grid header = new() { ColumnSpacing = 16, RowSpacing = 8 };
        _pageHeader = header;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel titles = new() { Spacing = 2 };
        _pageTitle = new TextBlock
        {
            Text = _services.Localization.Get(_contract.TextKeys[0]),
            Style = Application.Current.Resources["TitleTextBlockStyle"] as Style,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        titles.Children.Add(_pageTitle);
        string? context = _services.Session.SelectedInstance?.Name ?? _services.Session.SelectedNode?.Name ?? _services.Session.SelectedChannel?.Name;
        if (!string.IsNullOrWhiteSpace(context))
        {
            titles.Children.Add(new TextBlock
            {
                Text = context,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            });
        }

        header.Children.Add(titles);
        CommandBar commands = new()
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsDynamicOverflowEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _pageCommands = commands;
        Grid.SetColumn(commands, 1);
        foreach (string action in _contract.Actions)
        {
            if (_contract.Id == "PG-001")
            {
                continue;
            }

            AppBarButton button = new()
            {
                Label = _services.Localization.Get("action." + action),
                Tag = action,
                Icon = new SymbolIcon(ActionSymbol(action)),
            };
            AutomationProperties.SetAutomationId(button, $"{_contract.Id}:action:{action}");
            button.Click += OnAction;
            _commands.Add(button);
            if (DangerousActions.Contains(action))
            {
                commands.SecondaryCommands.Add(button);
            }
            else
            {
                commands.PrimaryCommands.Add(button);
            }
        }

        header.Children.Add(commands);
        return header;
    }

    private Border BuildPermissionNotice()
    {
        StackPanel content = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new SymbolIcon(Symbol.ProtectedDocument) { Width = 36, Height = 36 });
        content.Children.Add(new TextBlock
        {
            Text = _services.Localization.Get("common.permission-denied"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 520,
        });
        Button back = new() { Content = _services.Localization.Get("shell.back") };
        back.Click += (_, _) => _navigator.Navigate(_services.Session.Principal?.User.Role == "User" ? "PG-011" : "PG-040");
        content.Children.Add(back);
        return Card(_services.Localization.Get("common.permission-denied"), content);
    }

    private Border BuildSection(string section)
    {
        if ((_contract.Id == "PG-012" && section == "console") ||
            (_contract.Id == "PG-013" && section == "text-editor") ||
            (_contract.Id == "PG-015" && section == "editor"))
        {
            _embeddedHost = new RestrictedWebViewHost(_services, _contract.Id == "PG-012" ? "terminal" : "editor");
            if (_contract.Id is "PG-013" or "PG-015")
            {
                _embeddedHost.EditorSaveRequested += OnEditorSaveRequested;
                _embeddedHost.EditorDirtyChanged += OnEditorDirtyChanged;
            }

            AutomationProperties.SetAutomationId(_embeddedHost, $"{_contract.Id}:section:{section}");
            if (_contract.Id == "PG-012")
            {
                Button openWindow = new()
                {
                    Content = _services.Localization.Get("action.open-terminal"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                AutomationProperties.SetAutomationId(openWindow, "PG-012:action:open-terminal");
                openWindow.Click += OnOpenTerminalWindow;
                StackPanel terminal = new() { Spacing = 8 };
                terminal.Children.Add(openWindow);
                terminal.Children.Add(_embeddedHost);
                return Card(_services.Localization.Get("section." + section), terminal);
            }

            if (_contract.Id == "PG-015")
            {
                _structuredConfigurationEditor = new StackPanel { Spacing = 16 };
                Grid editorViews = new();
                editorViews.Children.Add(_embeddedHost);
                editorViews.Children.Add(_structuredConfigurationEditor);
                return Card(_services.Localization.Get("section." + section), editorViews);
            }

            return Card(_services.Localization.Get("section." + section), _embeddedHost);
        }

        ListView list = new()
        {
            SelectionMode = (_contract.Id, section) is ("PG-013", "file-list") or ("PG-031", "grants")
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single,
            MinHeight = 76,
            MaxHeight = 420,
            IsItemClickEnabled = false,
        };
        list.SelectionChanged += OnSectionSelectionChanged;
        list.DoubleTapped += OnSectionDoubleTapped;
        AutomationProperties.SetAutomationId(list, $"{_contract.Id}:section:{section}");
        Button loadMore = new()
        {
            Content = _services.Localization.Get("common.load-more"),
            Tag = section,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        loadMore.Click += OnLoadMore;
        AutomationProperties.SetAutomationId(loadMore, $"{_contract.Id}:section:{section}:load-more");
        StackPanel content = new() { Spacing = 4 };
        content.Children.Add(list);
        content.Children.Add(loadMore);
        _sections.Add(section, list);
        _sectionItems.Add(section, []);
        _loadMoreButtons.Add(section, loadMore);
        return Card(_services.Localization.Get("section." + section), content);
    }

    private static Border Card(string title, UIElement body)
    {
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
        });
        content.Children.Add(body);
        return new Border
        {
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
    }

    private Control CreateField(string field)
    {
        Control control;
        if (field is "password" or "confirm-password" or "current-password" or "new-password" or "access-token" or "rcon-password")
        {
            control = new PasswordBox
            {
                Header = _services.Localization.Get("field." + field),
                MaxWidth = 720,
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else if (BooleanFields.Contains(field))
        {
            control = new ToggleSwitch { Header = _services.Localization.Get("field." + field), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        }
        else if (NumericFields.Contains(field))
        {
            control = new NumberBox
            {
                Header = _services.Localization.Get("field." + field),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MaxWidth = 720,
                MinWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else if (_contract.Id == "PG-019" && field is "node" or "java-runtime")
        {
            ComboBox combo = new()
            {
                Header = _services.Localization.Get("field." + field),
                DisplayMemberPath = nameof(FieldChoice.Display),
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (field == "node")
            {
                combo.SelectionChanged += OnInstanceCreationNodeChanged;
            }

            control = combo;
        }
        else if (FieldChoices(_contract.Id, field) is string[] choices)
        {
            control = new ComboBox
            {
                Header = _services.Localization.Get("field." + field),
                ItemsSource = choices.Select(choice => new FieldChoice(choice, LocalizedChoiceText(field, choice))).ToArray(),
                DisplayMemberPath = nameof(FieldChoice.Display),
                MaxWidth = 720,
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else
        {
            bool multiline = field is "actions" or "advanced-arguments" or "content" or "description" or "environment-variables" or
                "jvm-arguments" or "launch" or "notes" or "private-source-authorities" or "retention" or "server-arguments" or
                "shortcut-commands" or "address-mappings";
            control = new TextBox
            {
                Header = _services.Localization.Get("field." + field),
                MaxWidth = 720,
                MinWidth = multiline ? 320 : 280,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                AcceptsReturn = multiline,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = multiline ? 96 : 0,
            };
            if (_contract.Id is "PG-013" or "PG-015" && field == "content")
            {
                control.Visibility = Visibility.Collapsed;
            }
        }

        AutomationProperties.SetAutomationId(control, $"{_contract.Id}:field:{field}");
        return control;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_contract.Id == "PG-031" && !_services.Settings.Current.GrantSafetyExplained)
        {
            await _services.Interaction.ShowAsync(
                "user.grants.safety-title",
                _services.Localization.Get("user.grants.safety-message")).ConfigureAwait(true);
            await _services.Settings.UpdateAsync(
                current => current with { GrantSafetyExplained = true },
                _lifetime.Token).ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        _lifetime.Cancel();
        _dispatcher.ResumeTransfer();
        _dispatcher.TransferProgressChanged -= OnTransferProgressChanged;
        DragOver -= OnDragOver;
        DragLeave -= OnDragLeave;
        Drop -= OnDrop;
        DisposeEmbeddedHost();
        DisposeDashboardEmbeds();
        _lifetime.Dispose();
    }

    private void DisposeEmbeddedHost()
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        if (_embeddedHost is not null)
        {
            _embeddedHost.EditorSaveRequested -= OnEditorSaveRequested;
            _embeddedHost.EditorDirtyChanged -= OnEditorDirtyChanged;
            _embeddedHost.Dispose();
            _embeddedHost = null;
        }
    }

    private async void OnSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSelection)
        {
            return;
        }

        if (sender is ListView { SelectionMode: ListViewSelectionMode.Multiple } multi)
        {
            PageListItem[] selectedItems = [.. multi.SelectedItems
                .OfType<ListViewItem>()
                .Select(container => container.Tag)
                .OfType<PageListItem>()];
            _selectedItem = selectedItems.LastOrDefault();
            _dispatcher.SelectMany([.. selectedItems.Select(item => item.Value)]);
            return;
        }

        if (sender is ListView list && list.SelectedItem is ListViewItem selected && selected.Tag is PageListItem item)
        {
            if (item.Value is SettingsGroupDto nextGroup &&
                _currentSettingsGroup is not null &&
                nextGroup.Group != _currentSettingsGroup.Group &&
                _settingsDirty &&
                !await _services.Interaction.ConfirmAsync(
                    "editor.unsaved-title", _services.Localization.Get("editor.unsaved-impact")).ConfigureAwait(true))
            {
                _suppressSettingsSelection = true;
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(container =>
                    container.Tag is PageListItem previous && ReferenceEquals(previous.Value, _currentSettingsGroup));
                _suppressSettingsSelection = false;
                _dispatcher.Select(_currentSettingsGroup);
                return;
            }

            _selectedItem = item;
            _dispatcher.Select(item.Value);
            ApplyFields(_dispatcher.SelectionFields());
            if (item.Value is SettingsGroupDto group)
            {
                ShowSettingsGroup(group);
            }
        }
        else
        {
            _selectedItem = null;
        }
    }

    private async void OnSectionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: ListViewItem { Tag: PageListItem item } })
        {
            return;
        }

        string? target = _dispatcher.ActivateSelection(item.Value);
        if (target is not null)
        {
            _navigator.Navigate(target);
        }
        else if (item.Value is Contracts.Files.FileEntryDto { IsDirectory: true })
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async void OnLoadMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section, CommandParameter: string cursor })
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            PageSectionSnapshot page = await _dispatcher.LoadMoreAsync(
                _contract.Id, section, cursor, ReadFields(), _lifetime.Token).ConfigureAwait(true);
            _sectionItems[section].AddRange(page.Items);
            PopulateSection(section, _sectionItems[section]);
            ConfigureLoadMore(section, page.NextCursor);
        }).ConfigureAwait(true);
    }

    private async void OnApplyFilters(object sender, RoutedEventArgs e)
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnEditorSaveRequested(object? sender, EventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _ = await _dispatcher.ExecuteAsync(
                _contract.Id, _contract.Id == "PG-013" ? "save-text" : "save", ReadFields(), _selectedItem, _lifetime.Token).ConfigureAwait(true);
            SetStatus("common.operation-completed", InfoBarSeverity.Success);
        }).ConfigureAwait(true);
    }

    private void OnEditorDirtyChanged(object? sender, EventArgs e)
    {
        bool dirty = _embeddedHost?.IsEditorDirty == true;
        SetStatus(
            dirty ? "editor.unsaved-status" : "common.operation-completed",
            dirty ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    public Task<bool> CanNavigateAwayAsync()
    {
        return _embeddedHost?.IsEditorDirty == true || _structuredConfigurationDirty || _settingsDirty
            ? _services.Interaction.ConfirmAsync("editor.unsaved-title", _services.Localization.Get("editor.unsaved-impact"))
            : Task.FromResult(true);
    }

    private async void OnOpenTerminalWindow(object sender, RoutedEventArgs e)
    {
        if (_services.Session.SelectedInstance is null)
        {
            SetStatus("error.selection-required", InfoBarSeverity.Error);
            return;
        }

        await _services.TerminalWindows.OpenAsync(_services.Session.SelectedInstance).ConfigureAwait(true);
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string action })
        {
            return;
        }

        await ExecutePageActionAsync(action).ConfigureAwait(true);
    }

    private Task ExecutePageActionAsync(string action)
    {
        return RunOperationAsync(async cancellationToken =>
        {
            PageActionResult result = await _dispatcher.ExecuteAsync(
                _contract.Id, action, ReadFields(), _selectedItem, cancellationToken).ConfigureAwait(true);
            if (_contract.Id == "PG-041" && action == "save")
            {
                _settingsDirty = false;
            }
            if (result.NavigatePageId is not null)
            {
                _navigator.Navigate(result.NavigatePageId);
            }

            if (result.StatusKey is not null)
            {
                SetStatus(result.StatusKey, InfoBarSeverity.Success);
            }

            if (result.Refresh)
            {
                await RefreshCoreAsync().ConfigureAwait(true);
            }
        });
    }

    private void ConfigureKeyboardAccelerators()
    {
        AddKeyboardAccelerator("refresh", Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control);
        AddKeyboardAccelerator("save", Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control);
        AddKeyboardAccelerator("delete", Windows.System.VirtualKey.Delete, Windows.System.VirtualKeyModifiers.None);
        AddKeyboardAccelerator("rename", Windows.System.VirtualKey.F2, Windows.System.VirtualKeyModifiers.None);
        AddKeyboardAccelerator("open", Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.None);
    }

    private void AddKeyboardAccelerator(
        string action,
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers)
    {
        if (!_contract.Actions.Contains(action, StringComparer.Ordinal))
        {
            return;
        }

        KeyboardAccelerator accelerator = new() { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await ExecutePageActionAsync(action).ConfigureAwait(true);
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_busy)
        {
            return;
        }

        _operation?.Dispose();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _transferPaused = false;
        _dispatcher.ResumeTransfer();
        _transferPauseButton.Content = _services.Localization.Get("action.pause");
        try
        {
            await RunBusyAsync(() => action(operation.Token)).ConfigureAwait(true);
        }
        finally
        {
            _dispatcher.ResumeTransfer();
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }

            operation.Dispose();
        }
    }

    private async Task RefreshAsync()
    {
        await RunBusyAsync(RefreshCoreAsync).ConfigureAwait(true);
    }

    private async Task RefreshCoreAsync()
    {
        PageSnapshot snapshot = await _dispatcher.LoadAsync(_contract.Id, ReadFields(), _lifetime.Token).ConfigureAwait(true);
        if (_contract.Id == "PG-019")
        {
            ConfigureInstanceCreationChoices(snapshot);
        }

        ApplyFields(snapshot.Fields);
        UpdateInstanceCreationReview();
        if (_contract.Id == "PG-015")
        {
            ConfigureStructuredConfigurationEditor();
        }
        foreach ((string key, ListView _) in _sections)
        {
            PageSectionSnapshot section = snapshot.Sections.TryGetValue(key, out PageSectionSnapshot? value)
                ? value
                : new PageSectionSnapshot([]);
            _sectionItems[key].Clear();
            _sectionItems[key].AddRange(section.Items);
            PopulateSection(key, _sectionItems[key]);
            ConfigureLoadMore(key, section.NextCursor);
        }

        if (_contract.Id == "PG-040" && snapshot.DashboardLayout is not null)
        {
            ApplyDashboardLayout(snapshot.DashboardLayout);
        }

        if (snapshot.StatusKey is not null)
        {
            SetStatus(snapshot.StatusKey, InfoBarSeverity.Informational);
        }
    }

    private void ConfigureInstanceCreationChoices(PageSnapshot snapshot)
    {
        _applyingInstanceChoices = true;
        try
        {
            PopulateInstanceChoice("node", snapshot.Sections.GetValueOrDefault("node")?.Items ?? []);
            PopulateInstanceChoice("java-runtime", snapshot.Sections.GetValueOrDefault("launch")?.Items ?? []);
        }
        finally
        {
            _applyingInstanceChoices = false;
        }
    }

    private void PopulateInstanceChoice(string field, IReadOnlyList<PageListItem> items)
    {
        if (_fields.GetValueOrDefault(field) is not ComboBox combo)
        {
            return;
        }

        string current = ReadFields().GetValueOrDefault(field) ?? string.Empty;
        FieldChoice[] choices = [.. items.Select(item => new FieldChoice(
            item.Value switch
            {
                NodeDto node => node.Id.ToString("D"),
                JavaRuntimeDto runtime => runtime.Id.ToString("D"),
                _ => string.Empty,
            },
            string.IsNullOrWhiteSpace(item.Secondary) ? item.Primary : item.Primary + " · " + item.Secondary))];
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Value, current, StringComparison.Ordinal)) ?? choices.FirstOrDefault();
    }

    private async void OnInstanceCreationNodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingInstanceChoices || sender is not ComboBox { SelectedItem: FieldChoice choice } || !Guid.TryParse(choice.Value, out Guid nodeId))
        {
            return;
        }

        NodeDto? node = _services.Session.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null)
        {
            return;
        }

        _services.Session.SelectedNode = node;
        await RunBusyAsync(async () =>
        {
            CursorPage<JavaRuntimeDto> runtimes = await _services.Session.Client.ListJavaRuntimesAsync(nodeId, _lifetime.Token).ConfigureAwait(true);
            PopulateInstanceChoice(
                "java-runtime",
                [.. runtimes.Items.Select(runtime => new PageListItem(
                    runtime.DisplayName ?? runtime.Version,
                    runtime.ExecutablePath,
                    runtime))]);
            UpdateInstanceCreationReview();
        }).ConfigureAwait(true);
    }

    private void PopulateSection(string section, List<PageListItem> items)
    {
        ListView list = _sections[section];
        list.Items.Clear();
        if (items.Count == 0)
        {
            ListViewItem empty = new()
            {
                IsEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock
                {
                    Text = _services.Localization.Get("common.empty"),
                    Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
                    Margin = new Thickness(8, 18, 8, 18),
                    TextAlignment = TextAlignment.Center,
                },
            };
            list.Items.Add(empty);
            return;
        }

        foreach (PageListItem item in items)
        {
            StackPanel content = new() { Spacing = 3, Padding = new Thickness(4, 6, 4, 6) };
            content.Children.Add(new TextBlock { Text = item.Primary, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(item.Secondary))
            {
                content.Children.Add(new TextBlock
                {
                    Text = item.Secondary,
                    Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            ListViewItem container = new() { Tag = item, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(container, item.ToString());
            list.Items.Add(container);
        }

        if (_contract.Id == "PG-041" && section == "general" && list.SelectedIndex < 0 && list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void ConfigureLoadMore(string section, string? cursor)
    {
        Button button = _loadMoreButtons[section];
        button.CommandParameter = cursor;
        button.Visibility = cursor is null ? Visibility.Collapsed : Visibility.Visible;
        button.IsEnabled = !_busy && cursor is not null;
    }

    private void ApplyDashboardLayout(DashboardLayoutDto layout)
    {
        DisposeDashboardEmbeds();
        _dashboardGrid.Children.Clear();
        _dashboardGrid.RowDefinitions.Clear();
        int row = 0;
        int column = 0;
        foreach (DashboardCardLayoutDto card in layout.Cards.Where(card => !card.Hidden))
        {
            int width = Math.Clamp(card.Width, 1, 12);
            if (column + width > 12)
            {
                row++;
                column = 0;
            }

            while (_dashboardGrid.RowDefinitions.Count <= row)
            {
                _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Border element = _sectionCards.TryGetValue(card.Id, out Border? builtIn)
                ? builtIn
                : BuildDashboardContentCard(card);
            element.MinHeight = DashboardHeight(card.Height);
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, width);
            _dashboardGrid.Children.Add(element);
            column += width;
            if (column == 12)
            {
                row++;
                column = 0;
            }
        }
    }

    private Border BuildDashboardContentCard(DashboardCardLayoutDto card)
    {
        string value = card.Settings.GetValueOrDefault("value") ?? string.Empty;
        string title = card.Title ?? _services.Localization.Get("dashboard.card." + card.Kind);
        UIElement content;
        if (card.Kind == "image" && Uri.TryCreate(value, UriKind.Absolute, out Uri? imageUri) && imageUri.Scheme == Uri.UriSchemeHttps)
        {
            content = new Image
            {
                Source = new BitmapImage(imageUri),
                Stretch = Stretch.Uniform,
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else if (card.Kind == "link" && Uri.TryCreate(value, UriKind.Absolute, out Uri? linkUri) && linkUri.Scheme is "http" or "https")
        {
            content = new HyperlinkButton
            {
                Content = card.Description ?? linkUri.AbsoluteUri,
                NavigateUri = linkUri,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        else if (card.Kind == "embed" && Uri.TryCreate(value, UriKind.Absolute, out Uri? embedUri) && embedUri.Scheme == Uri.UriSchemeHttps)
        {
            DashboardEmbedHost embed = new(_services, embedUri);
            _dashboardEmbeds.Add(embed);
            content = embed;
        }
        else
        {
            string body = card.Kind == "clock"
                ? _services.TimeProvider.GetLocalNow().ToString("F", CultureInfo.CurrentCulture)
                : card.Description ?? value;
            content = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            };
        }

        return Card(title, content);
    }

    private static double DashboardHeight(string height)
    {
        return height switch
        {
            "mini" => 96,
            "small" => 144,
            "medium" => 240,
            "large" => 360,
            "extra-large" => 520,
            _ => 0,
        };
    }

    private void DisposeDashboardEmbeds()
    {
        foreach (IDisposable embed in _dashboardEmbeds)
        {
            embed.Dispose();
        }

        _dashboardEmbeds.Clear();
    }

    private Dictionary<string, string> ReadFields()
    {
        return _fields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value switch
            {
                TextBox when pair.Key == "content" && _embeddedHost is not null => _embeddedHost.EditorContent,
                TextBox text => text.Text,
                PasswordBox password => password.Password,
                ToggleSwitch toggle => toggle.IsOn ? "true" : "false",
                ComboBox combo => combo.SelectedItem is FieldChoice choice ? choice.Value : combo.SelectedItem?.ToString() ?? string.Empty,
                NumberBox number => double.IsNaN(number.Value) ? string.Empty : number.Value.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty,
            },
            StringComparer.Ordinal);
    }

    private void ApplyFields(IReadOnlyDictionary<string, string> values)
    {
        foreach ((string key, string value) in values)
        {
            if (!_fields.TryGetValue(key, out Control? control))
            {
                continue;
            }

            if (control is TextBox text)
            {
                text.Text = value;
                if (key == "content" && _embeddedHost is not null)
                {
                    _ = _embeddedHost.LoadEditorAsync(value, _dispatcher.EditorLanguage);
                }
            }
            else if (control is ToggleSwitch toggle && bool.TryParse(value, out bool enabled))
            {
                toggle.IsOn = enabled;
            }
            else if (control is NumberBox number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
            {
                number.Value = numeric;
            }
            else if (control is ComboBox combo)
            {
                combo.SelectedItem = combo.Items.OfType<FieldChoice>().FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal));
            }
        }
    }

    private static string[]? FieldChoices(string pageId, string field)
    {
        return field switch
        {
            "language" => ["zh-Hans", "en-US"],
            "overlap-policy" => ["skip", "queue", "concurrent"],
            "role" => ["User", "Administrator"],
            "status" when pageId is "PG-010" or "PG-011" => ["", "Stopped", "Starting", "Running", "Stopping", "Crashed"],
            "status" when pageId == "PG-020" => ["", "Online", "Offline"],
            "status" when pageId == "PG-044" => ["", "Pending", "Running", "Succeeded", "Failed", "Cancelled"],
            "category" when pageId == "PG-043" => ["", "Authentication", "Authorization", "Credential", "SecuritySetting", "DestructiveAction"],
            "outcome" when pageId is "PG-042" or "PG-043" => ["", "success", "ok", "denied", "failed"],
            "grant-state" => ["all", "granted", "not-granted"],
            "sort" when pageId == "PG-013" => ["name", "size", "modified"],
            "collision-policy" => ["reject", "overwrite", "keep-both", "skip"],
            "entry-name-encoding" => ["utf-8", "gbk"],
            "extract-encoding" => ["auto", "utf-8", "gbk"],
            "source-kind" when pageId == "PG-019" => ["empty", "archive", "directory"],
            "source-kind" when pageId == "PG-062" => ["archive", "instance", "prism"],
            "target-kind" => ["jar", "argfile"],
            "terminal-newline" => ["CRLF", "LF"],
            "type" => ["mods", "plugins"],
            "update-kind" => ["none", "jar-replace", "hmcl-channel-sync"],
            "visibility" => ["public", "token"],
            _ => null,
        };
    }

    private string LocalizedChoiceText(string field, string value)
    {
        string category = field switch
        {
            "source-kind" => _contract.Id == "PG-019" ? "instance-source" : "release-source",
            "category" when _contract.Id == "PG-043" => "audit-category",
            _ => field,
        };
        if (value.Length == 0)
        {
            return _services.Localization.Get("common.all");
        }

        string key = category == "status"
            ? "state." + ToKebabCase(value)
            : "value." + category + "." + ToKebabCase(value);
        string localized = _services.Localization.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? _services.Localization.Get("common.unknown")
            : localized;
    }

    private bool CanAccess()
    {
        return DesktopPageAuthorization.CanAccess(
            _contract.Permission,
            _services.Session.Principal,
            _services.Session.SelectedInstance?.Id);
    }

    private bool IsAdministrator => _services.Session.Principal?.User.Role is "Owner" or "Administrator";

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _progress.Visibility = Visibility.Visible;
        SetCommandsEnabled(false);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("state.cancelled", InfoBarSeverity.Warning);
        }
        catch (Client.ServitiumApiException exception)
        {
            string localized = _services.Localization.Get("error." + exception.Code);
            SetStatus(localized + (exception.TraceId is null ? string.Empty : " · " + _services.Localization.Get("common.trace-id") + ": " + exception.TraceId), InfoBarSeverity.Error, false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error, false);
        }
        finally
        {
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
            SetCommandsEnabled(true);
        }
    }

    private void OnTransferProgressChanged(object? sender, TransferProgressEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_transferCard is null)
            {
                return;
            }

            _transferCard.Visibility = Visibility.Visible;
            _transferProgress.IsIndeterminate = e.TotalBytes <= 0;
            _transferProgress.Value = e.TotalBytes <= 0 ? 0 : Math.Clamp(e.TransferredBytes * 100d / e.TotalBytes, 0, 100);
            string remaining = e.Remaining is null ? string.Empty : " · " + _services.Localization.Get("transfer.remaining") + " " + e.Remaining.Value.ToString("g", CultureInfo.CurrentCulture);
            _transferSummary.Text = e.FileName + " · " + FormatBytes(e.TransferredBytes) + " / " + FormatBytes(e.TotalBytes) +
                " · " + FormatBytes((long)e.BytesPerSecond) + "/s" + remaining + " · " + _services.Localization.Get("state." + e.State);
            bool active = e.State is "uploading" or "downloading";
            _transferPauseButton.IsEnabled = active;
            _transferCancelButton.IsEnabled = active;
        });
    }

    private void OnTransferPause(object sender, RoutedEventArgs e)
    {
        _transferPaused = !_transferPaused;
        if (_transferPaused)
        {
            _dispatcher.PauseTransfer();
        }
        else
        {
            _dispatcher.ResumeTransfer();
        }

        _transferPauseButton.Content = _services.Localization.Get(_transferPaused ? "action.resume" : "action.pause");
    }

    private void OnTransferCancel(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _services.Localization.Get("transfer.drop-caption");
        e.DragUIOverride.IsCaptionVisible = true;
        _dropStatus.Message = _services.Localization.Get("transfer.drop-caption");
        _dropStatus.IsOpen = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dropStatus.IsOpen = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropStatus.IsOpen = false;
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        if (_contract.Id is "PG-013" or "PG-016")
        {
            await RunOperationAsync(async cancellationToken =>
            {
                await _dispatcher.UploadDroppedAsync(
                    _contract.Id,
                    [.. items.Select(item => item.Path)],
                    cancellationToken).ConfigureAwait(true);
                SetStatus("common.operation-accepted", InfoBarSeverity.Success);
                await RefreshCoreAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
            return;
        }

        StorageFile? file = items.OfType<StorageFile>().SingleOrDefault();
        if (file is null || items.Count != 1 || !IsAcceptedArchive(_contract.Id, file.FileType))
        {
            SetStatus("error.validation-failed", InfoBarSeverity.Error);
            return;
        }

        string field = _contract.Id switch
        {
            "PG-019" => "archive",
            "PG-062" when string.Equals(ReadFields().GetValueOrDefault("source-kind"), "prism", StringComparison.OrdinalIgnoreCase) => "prism-archive",
            "PG-022" or "PG-062" => "archive",
            _ => throw new ArgumentOutOfRangeException(nameof(e)),
        };
        if (_fields[field] is TextBox text)
        {
            text.Text = file.Path;
            SetStatus("transfer.drop-ready", InfoBarSeverity.Success);
        }
    }

    private static bool SupportsDrop(string pageId)
    {
        return pageId is "PG-013" or "PG-016" or "PG-019" or "PG-022" or "PG-062";
    }

    private static bool IsAcceptedArchive(string pageId, string extension)
    {
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            (pageId == "PG-022" && extension.Equals(".gz", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = Math.Max(0, value);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string ToKebabCase(string value)
    {
        System.Text.StringBuilder result = new(value.Length + 8);
        foreach (char character in value)
        {
            if (character is '-' or '_' or ' ')
            {
                if (result.Length > 0 && result[^1] != '-')
                {
                    _ = result.Append('-');
                }

                continue;
            }

            if (char.IsUpper(character) && result.Length > 0 && result[^1] != '-')
            {
                _ = result.Append('-');
            }

            _ = result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private void SetCommandsEnabled(bool enabled)
    {
        foreach (Control command in _commands)
        {
            command.IsEnabled = enabled &&
                (_contract.Id != "PG-018" || IsAdministrator || (command.Tag as string) == "save") &&
                InstanceCreationCommandAllowed(command.Tag as string);
        }

        foreach ((_, Button button) in _loadMoreButtons)
        {
            button.IsEnabled = enabled && (button.CommandParameter is string);
        }
    }

    private bool InstanceCreationCommandAllowed(string? action)
    {
        if (_contract.Id != "PG-019" || action is not ("create-empty" or "import-server-archive" or "attach-directory"))
        {
            return true;
        }

        string source = ReadFields().GetValueOrDefault("source-kind") ?? "empty";
        return (source, action) is
            ("empty", "create-empty") or
            ("archive", "import-server-archive") or
            ("directory", "attach-directory");
    }

    private void SetStatus(string value, InfoBarSeverity severity, bool resourceKey = true)
    {
        _status.Message = resourceKey ? _services.Localization.Get(value) : value;
        _status.Severity = severity;
        _status.IsOpen = true;
    }

    private void ConfigureStructuredConfigurationEditor()
    {
        if (_structuredConfigurationEditor is null || _embeddedHost is null)
        {
            return;
        }

        foreach (string key in _fields.Keys.Where(key => key.StartsWith("config:", StringComparison.Ordinal)).ToArray())
        {
            _ = _fields.Remove(key);
        }

        _structuredConfigurationEditor.Children.Clear();
        ConfigurationDetailsDto? details = _dispatcher.ConfigurationDetails;
        bool raw = _dispatcher.ConfigurationRawMode || details is null;
        _embeddedHost.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        _structuredConfigurationEditor.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        if (raw || details is null)
        {
            _structuredConfigurationDirty = false;
            return;
        }

        _applyingStructuredConfiguration = true;
        HashSet<string> defined = new(StringComparer.Ordinal);
        foreach (IGrouping<string, ConfigurationFieldDto> group in _dispatcher.ConfigurationSchema.GroupBy(field => field.Group, StringComparer.Ordinal))
        {
            StackPanel fields = new() { Spacing = 12 };
            foreach (ConfigurationFieldDto field in group)
            {
                _ = defined.Add(field.Key);
                string value = details.Values.TryGetValue(field.Key, out string? current)
                    ? current ?? string.Empty
                    : field.DefaultValue ?? string.Empty;
                Control control = CreateConfigurationControl(field, value);
                fields.Children.Add(control);
                _fields.Add("config:" + field.Key, control);
            }

            _structuredConfigurationEditor.Children.Add(new Expander
            {
                Header = _services.Localization.Get("configgroup." + group.Key),
                Content = fields,
                IsExpanded = true,
            });
        }

        KeyValuePair<string, string?>[] unknown = [.. details.Values.Where(pair => !defined.Contains(pair.Key))];
        if (unknown.Length > 0)
        {
            StackPanel fields = new() { Spacing = 12 };
            foreach ((string key, string? value) in unknown)
            {
                TextBox control = new() { Header = key, Text = value ?? string.Empty };
                control.TextChanged += OnStructuredConfigurationChanged;
                fields.Children.Add(control);
                _fields.Add("config:" + key, control);
            }

            _structuredConfigurationEditor.Children.Add(new Expander
            {
                Header = _services.Localization.Get("configgroup.other"),
                Content = fields,
                IsExpanded = true,
            });
        }

        _structuredConfigurationDirty = false;
        _applyingStructuredConfiguration = false;
    }

    private Control CreateConfigurationControl(ConfigurationFieldDto field, string value)
    {
        Control control;
        if (field.AllowedValues is { Count: > 0 })
        {
            ComboBox combo = new()
            {
                Header = field.Key,
                ItemsSource = field.AllowedValues,
                SelectedItem = field.AllowedValues.Contains(value, StringComparer.Ordinal) ? value : field.AllowedValues[0],
                MinWidth = 280,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            combo.SelectionChanged += OnStructuredConfigurationChanged;
            control = combo;
        }
        else if (field.Type == "boolean")
        {
            ToggleSwitch toggle = new() { Header = field.Key, IsOn = bool.TryParse(value, out bool enabled) && enabled, HorizontalAlignment = HorizontalAlignment.Left };
            toggle.Toggled += OnStructuredConfigurationChanged;
            control = toggle;
        }
        else if (field.Type is "integer" or "decimal")
        {
            NumberBox number = new()
            {
                Header = field.Key,
                Minimum = field.Minimum is decimal minimum ? decimal.ToDouble(minimum) : double.MinValue,
                Maximum = field.Maximum is decimal maximum ? decimal.ToDouble(maximum) : double.MaxValue,
                Value = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MinWidth = 280,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            number.ValueChanged += OnStructuredConfigurationChanged;
            control = number;
        }
        else
        {
            TextBox text = new()
            {
                Header = field.Key,
                Text = value,
                AcceptsReturn = field.Type == "multiline",
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 280,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            text.TextChanged += OnStructuredConfigurationChanged;
            control = text;
        }

        AutomationProperties.SetAutomationId(control, $"PG-015:field:config:{field.Key}");
        return control;
    }

    private void OnStructuredConfigurationChanged(object sender, object e)
    {
        if (_applyingStructuredConfiguration)
        {
            return;
        }

        _structuredConfigurationDirty = true;
        SetStatus("editor.unsaved-status", InfoBarSeverity.Warning);
    }

    private void OnSettingsChanged(object sender, object e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsDirty = true;
        SetStatus("editor.unsaved-status", InfoBarSeverity.Warning);
    }

    private static Symbol ActionSymbol(string action)
    {
        return action switch
        {
            "create" or "create-channel" or "create-release" or "create-token" => Symbol.Add,
            "refresh" => Symbol.Refresh,
            "save" => Symbol.Save,
            "delete" or "delete-working-directory" or "unregister" => Symbol.Delete,
            "start" or "run-now" => Symbol.Play,
            "stop" or "force-stop" => Symbol.Stop,
            "open" or "open-channel" => Symbol.OpenFile,
            "upload" or "import" or "import-server-archive" => Symbol.Upload,
            "download" or "download-manifest" => Symbol.Download,
            _ => Symbol.Document,
        };
    }

    private sealed record FieldChoice(string Value, string Display);
}
