using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceCreationViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    internal InstanceCreationViewModel(LocalizationService localization, ServitiumApiClient client) : base(localization)
    {
        _client = client; Launch = new(localization); Launch.PropertyChanged += OnLaunchChanged;
    }
    public LaunchEditor Launch { get; }
    public event EventHandler? Submitted;
    public int Step { get; private set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool IsSourceStep => Step == 0;
    public bool IsBasicStep => Step == 1;
    public bool IsReviewStep => Step == 2;
    public bool IsBeforeReview => Step < 2;
    public string StepTitle => Text(Step switch { 0 => "desktop.create-step-source", 1 => "desktop.create-step-basic", _ => "desktop.create-step-review" });
    public int SourceMode { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool IsArchive => SourceMode == 1;
    public bool IsAttach => SourceMode == 2;
    public string ArchivePath { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string AbsolutePath { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string Name { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public IReadOnlyList<NodeDto> Nodes { get; private set => SetProperty(ref field, value); } = [];
    public NodeDto? SelectedNode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            Launch.NodeId = value?.Id ?? Guid.Empty; Launch.AdvancedAllowed = false; Launch.SetRuntimes([]); Changed();
        }
    }
    public string StopCommand { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "stop";
    public double StopTimeout { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 60;
    public string Tags { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string Notes { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string Review => Format("desktop.create-review", Name.Trim(), SelectedNode?.Name ?? string.Empty,
        Launch.SelectedJava?.ExecutablePath ?? string.Empty, Launch.MinimumHeap, Launch.MaximumHeap, Launch.AdvancedMode ? Text("desktop.launch-advanced") : Launch.Target,
        SourceMode == 2 ? AbsolutePath : SourceMode == 1 ? Path.GetFileName(ArchivePath) : Text("desktop.create-managed-directory")) + Environment.NewLine + Launch.Preview;
    public string Validation => !SourceValid() ? Text("desktop.create-source-required") : Step == 0 ? string.Empty : SelectedNode?.Status != "Online" ? Text("desktop.create-node-required") :
        !Launch.IsValid ? Launch.Validation : !ValidForm() ? Text("desktop.create-fields-required") : string.Empty;
    public bool HasValidation => Validation.Length > 0;
    private bool SourceValid() { return SourceMode is >= 0 and <= 2 && (SourceMode == 1 ? !string.IsNullOrWhiteSpace(ArchivePath) : SourceMode != 2 || Path.IsPathFullyQualified(AbsolutePath)); }
    private bool ValidForm()
    {
        return !string.IsNullOrWhiteSpace(Name) && Name.Trim().Length <= 64 && !Name.Any(char.IsControl) &&
            SelectedNode is { Status: "Online" } && Launch.IsValid && !string.IsNullOrWhiteSpace(StopCommand) &&
            !StopCommand.Any(char.IsControl) && double.IsFinite(StopTimeout) && StopTimeout >= 1 && StopTimeout <= 600 && StopTimeout == Math.Truncate(StopTimeout);
    }
    private bool CanNext() { return CanInteract && Step < 2 && (Step == 0 ? SourceValid() : ValidForm()); }
    private bool CanBack() { return CanInteract && Step > 0; }
    private bool CanCreate() { return CanInteract && Step == 2 && SourceValid() && ValidForm(); }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() { Step++; }
    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() { Step--; }

    public Task LoadAsync()
    {
        return RunAsync(async token =>
        {
            List<NodeDto> nodes = []; string? cursor = null;
            do { CursorPage<NodeDto> page = await _client.ListNodesAsync(null, null, cursor, 200, token).ConfigureAwait(true); nodes.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
            Nodes = nodes; Changed();
        });
    }
    public Task LoadJavaAsync()
    {
        NodeDto? node = SelectedNode;
        return node is null
            ? Task.CompletedTask
            : RunAsync(async token =>
        {
            NodeDetailsDto details = await _client.GetNodeAsync(node.Id, token).ConfigureAwait(true);
            Launch.AdvancedAllowed = details.Configuration.AdvancedRawCommandsEnabled;
            List<JavaRuntimeDto> runtimes = []; string? cursor = null;
            do { CursorPage<JavaRuntimeDto> page = await _client.ListJavaRuntimesAsync(node.Id, cursor, 200, token).ConfigureAwait(true); runtimes.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
            Launch.SetRuntimes(runtimes); Changed();
        });
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync()
    {
        return RunAsync(async token =>
        {
            StructuredLaunchRequest launch = Launch.Request();
            OperationAcceptedDto accepted;
            string[] tags = Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (IsAttach)
            {
                accepted = await _client.AttachInstanceDirectoryAsync(new(Name.Trim(), SelectedNode!.Id, AbsolutePath.Trim(), launch, StopCommand, (int)StopTimeout, tags, Notes), token).ConfigureAwait(true);
            }
            else if (IsArchive)
            {
                FileStream archive = new(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await using (archive.ConfigureAwait(false))
                {
                    accepted = await _client.ImportServerArchiveAsync(archive, Path.GetFileName(ArchivePath), Name.Trim(), SelectedNode!.Id, launch,
                        StopCommand, (int)StopTimeout, tags, Notes, token).ConfigureAwait(true);
                }
            }
            else { accepted = await _client.CreateInstanceAsync(new(Name.Trim(), SelectedNode!.Id, launch, StopCommand, (int)StopTimeout, tags, Notes), token).ConfigureAwait(true); }
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            Submitted?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnLaunchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { Changed(); }
    public override void Dispose() { Launch.PropertyChanged -= OnLaunchChanged; Launch.Dispose(); base.Dispose(); }
    private void Changed()
    {
        foreach (string property in new[] { nameof(IsSourceStep), nameof(IsBasicStep), nameof(IsReviewStep), nameof(IsBeforeReview), nameof(IsArchive), nameof(IsAttach), nameof(StepTitle), nameof(Review), nameof(Validation), nameof(HasValidation) }) { OnPropertyChanged(property); }
        RefreshCommands();
    }
    protected override void RefreshCommands() { NextCommand.NotifyCanExecuteChanged(); BackCommand.NotifyCanExecuteChanged(); CreateCommand.NotifyCanExecuteChanged(); }
}
