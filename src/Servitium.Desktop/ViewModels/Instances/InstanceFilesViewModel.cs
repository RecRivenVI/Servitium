using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Files;
using Servitium.Contracts.Instances;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceFilesViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly InstanceDto _instance;
    private readonly Func<string, string, string?, Task<string?>> _prompt;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<Task<string?>> _pickUpload;
    private readonly Func<string, string, Task<string?>> _pickDownload;
    private readonly Func<string, byte[], Task> _preview;
    private readonly TransferPauseGate _pause = new();
    private readonly FileTransferService _transfer;
    private CancellationTokenSource? _transferCancellation;
    private FileClipboardPlan? _clipboard;
    private string? _cursor;
    private string _savedText = string.Empty;
    private string? _editingPath;
    private bool _paused;
    private string _status;

    internal InstanceFilesViewModel(LocalizationService localization, ServitiumApiClient client, InstanceDto instance,
        Func<string, string, string?, Task<string?>> prompt, Func<string, string, Task<bool>> confirm,
        Func<Task<string?>> pickUpload, Func<string, string, Task<string?>> pickDownload, Func<string, byte[], Task> preview, bool reachable = true) : base(localization)
    {
        _client = client; _instance = instance; _prompt = prompt; _confirm = confirm;
        _pickUpload = pickUpload; _pickDownload = pickDownload; _preview = preview;
        IProgress<TransferProgressEventArgs> progress = new Progress<TransferProgressEventArgs>(OnTransferProgress);
        _transfer = new(client, localization, _pause, progress.Report);
        FileDirectoryTab initial = new(localization); Tabs.Add(initial); SelectedTab = initial;
        _status = instance.Status; IsReachable = reachable; IsRunning = instance.Status == "Running"; UpdateBreadcrumbs();
    }

    public event EventHandler? EditorLoaded;
    public ObservableCollection<FileDirectoryTab> Tabs { get; } = [];
    public FileDirectoryTab SelectedTab { get; private set => SetProperty(ref field, value); }
    public IReadOnlyList<FileRow> Files { get; private set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<FileBreadcrumb> Breadcrumbs { get; private set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<FileRow> SelectedFiles { get; private set; } = [];
    public string DirectoryPath => SelectedTab.Path;
    public string Search { get; set => SetProperty(ref field, value); } = string.Empty;
    public int Sort { get; set => SetProperty(ref field, value); }
    public bool Descending { get; set => SetProperty(ref field, value); }
    public int Collision { get; set => SetProperty(ref field, value); }
    public string Destination { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } } = string.Empty;
    public string Url { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } } = string.Empty;
    public bool Overwrite { get; set => SetProperty(ref field, value); }
    public string ArchiveEncoding { get; set => SetProperty(ref field, value); } = "UTF-8";
    public string ExtractEncoding { get; set => SetProperty(ref field, value); } = "auto";
    public string TextEncoding { get; set => SetProperty(ref field, value); } = "auto";
    public string EditorText { get; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsDirty)); RefreshCommands(); } } } = string.Empty;
    public bool IsEditing { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ShowFiles)); RefreshCommands(); } } }
    public bool ShowFiles => !IsEditing;
    public bool IsDirty => IsEditing && EditorText != _savedText;
    public bool IsEmpty => Files.Count == 0;
    public bool HasMore => _cursor is not null;
    public bool IsRunning { get; private set => SetProperty(ref field, value); }
    public bool IsReachable { get; private set => SetProperty(ref field, value); }
    public bool RequiresStopped => IsReachable && !CanWrite;
    private bool CanWrite => IsReachable && _status is "Stopped" or "Crashed";
    public bool IsTransferring => _transferCancellation is not null;
    public string TransferText { get; private set => SetProperty(ref field, value); } = string.Empty;
    public double TransferPercent { get; private set => SetProperty(ref field, value); }
    public string Count => Format("desktop.files-shown", Files.Count);
    public string EditorTitle => _editingPath is null ? string.Empty : Path.GetFileName(_editingPath);
    public bool CanSelect => CanInteract && !IsEditing && IsReachable;
    public bool CanMutate => CanSelect && CanWrite;
    public bool CanMutateSelection => CanUseSelection && CanWrite;
    public bool CanMutateOne => CanUseOne && CanWrite;
    public bool CanMutateFile => CanUseFile && CanWrite;
    public bool CanUseSelection => CanSelect && SelectedFiles.Count > 0;
    public bool CanUseOne => CanSelect && SelectedFiles.Count == 1;
    public bool CanUseFile => CanUseOne && !SelectedFiles[0].Value.IsDirectory;
    public bool CanPaste => CanMutate && _clipboard is not null;
    public bool CanSave => CanInteract && IsDirty && CanWrite;
    public bool CanCloseEditor => CanInteract && IsEditing;
    public bool CanCloseTab => CanInteract && Tabs.Count > 1;
    public bool ShowTabs => Tabs.Count > 1;
    public bool CanLoadMore => CanSelect && HasMore;
    private string CollisionPolicy => Collision switch { 1 => "overwrite", 2 => "keep-both", 3 => "skip", _ => "reject" };

    public void Select(IReadOnlyList<FileRow> rows) { SelectedFiles = rows; RefreshCommands(); }

    public Task LoadAsync(string? initialPath = null)
    {
        if (initialPath is not null) { SelectedTab.Path = NormalizeRelative(initialPath); UpdateBreadcrumbs(); }
        return RunAsync(LoadCoreAsync);
    }
    private async Task LoadCoreAsync(CancellationToken token)
    {
        FileEntryPageDto page = await _client.ListFilesAsync(_instance.Id, _instance.NodeId, DirectoryPath, null, 200,
            Search, Sort switch { 1 => "size", 2 => "modified", _ => "name" }, Descending, token).ConfigureAwait(true);
        Files = [.. page.Items.Select(value => new FileRow(value, Localization))]; _cursor = page.NextCursor; SelectedFiles = [];
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasMore)); OnPropertyChanged(nameof(Count)); RefreshCommands();
    }
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync()
    {
        return RunAsync(async token =>
        {
            FileEntryPageDto page = await _client.ListFilesAsync(_instance.Id, _instance.NodeId, DirectoryPath, _cursor, 200,
                Search, Sort switch { 1 => "size", 2 => "modified", _ => "name" }, Descending, token).ConfigureAwait(true);
            Files = [.. Files, .. page.Items.Select(value => new FileRow(value, Localization))]; _cursor = page.NextCursor;
            OnPropertyChanged(nameof(HasMore)); OnPropertyChanged(nameof(Count));
        });
    }
    public async Task NavigateAsync(string path)
    {
        if (!await CanLeaveAsync().ConfigureAwait(true)) { return; }
        IsEditing = false; _editingPath = null; SelectedTab.Path = NormalizeRelative(path); UpdateBreadcrumbs();
        await LoadAsync().ConfigureAwait(true);
    }
    public async Task ChangeTabAsync(FileDirectoryTab tab)
    {
        if (ReferenceEquals(tab, SelectedTab) || !await CanLeaveAsync().ConfigureAwait(true)) { return; }
        IsEditing = false; SelectedTab = tab; UpdateBreadcrumbs(); await LoadAsync().ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void NewTab()
    {
        FileDirectoryTab tab = new(Localization) { Path = DirectoryPath }; Tabs.Add(tab); SelectedTab = tab;
        foreach (FileDirectoryTab item in Tabs) { item.CanClose = Tabs.Count > 1; }
        OnPropertyChanged(nameof(CanCloseTab)); RefreshCommands();
        OnPropertyChanged(nameof(ShowTabs));
    }
    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private async Task CloseTabAsync()
    {
        if (!await CanLeaveAsync().ConfigureAwait(true)) { return; }
        int index = Tabs.IndexOf(SelectedTab); _ = Tabs.Remove(SelectedTab); SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        foreach (FileDirectoryTab item in Tabs) { item.CanClose = Tabs.Count > 1; }
        OnPropertyChanged(nameof(ShowTabs));
        IsEditing = false; UpdateBreadcrumbs(); await LoadAsync().ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanSelect))]
    private Task RefreshAsync() { return LoadAsync(); }

    public async Task<bool> CanLeaveAsync()
    {
        return IsTransferring && !await _confirm("desktop.transfer-leave-title", Text("desktop.transfer-leave-impact")).ConfigureAwait(true)
            ? false
            : !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.unsaved-discard-impact")).ConfigureAwait(true);
    }
    private void UpdateBreadcrumbs()
    {
        List<FileBreadcrumb> values = [new(_instance.Name, string.Empty), new(Text("field.working-directory"), string.Empty)];
        string path = string.Empty;
        foreach (string segment in DirectoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries)) { path = Combine(path, segment); values.Add(new(segment, path)); }
        Breadcrumbs = values; OnPropertyChanged(nameof(DirectoryPath));
    }
    internal static string NormalizeRelative(string value)
    {
        return Path.IsPathRooted(value) || value.Split(['/', '\\']).Any(segment => segment == "..")
            ? throw new ArgumentException("The path must stay inside the instance directory.", nameof(value))
            : value.Replace('\\', '/').Trim('/');
    }
    private static string Combine(string parent, string child)
    {
        string name = NormalizeRelative(child);
        return parent.Length == 0 ? name : parent + "/" + name;
    }
    protected override void RefreshCommands()
    {
        foreach (string property in new[] { nameof(CanSelect), nameof(CanMutate), nameof(CanMutateSelection), nameof(CanMutateOne), nameof(CanMutateFile), nameof(RequiresStopped), nameof(CanUseSelection), nameof(CanUseOne), nameof(CanUseFile), nameof(CanPaste), nameof(CanSave), nameof(CanCloseEditor), nameof(CanCloseTab), nameof(CanLoadMore), nameof(IsTransferring) }) { OnPropertyChanged(property); }
        RefreshCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged(); NewTabCommand.NotifyCanExecuteChanged(); CloseTabCommand.NotifyCanExecuteChanged();
        RefreshFileCommands();
    }
    public override void Dispose()
    {
        _pause.Resume(); _transferCancellation?.Cancel(); _transferCancellation?.Dispose(); base.Dispose();
    }
}
