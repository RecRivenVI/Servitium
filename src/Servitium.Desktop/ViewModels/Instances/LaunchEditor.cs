using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class LaunchEditor : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly HashSet<LaunchEnvironmentVariable> _subscribedVariables = [];
    internal LaunchEditor(LocalizationService localization)
    {
        _localization = localization; EnvironmentVariables.CollectionChanged += OnVariablesChanged;
    }
    public string Text(string key) { return _localization.Get(key); }
    public Guid NodeId { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public IReadOnlyList<JavaRuntimeDto> Runtimes { get; private set => SetProperty(ref field, value); } = [];
    public JavaRuntimeDto? SelectedJava { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public double MinimumHeap { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 512;
    public double MaximumHeap { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 2048;
    public int TargetKind { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public string Target { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "server.jar";
    public string JvmArguments { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string ServerArguments { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "nogui";
    public bool AdvancedMode { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool AdvancedAllowed { get; internal set { if (SetProperty(ref field, value)) { Changed(); } } }
    public string AdvancedArguments { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public ObservableCollection<LaunchEnvironmentVariable> EnvironmentVariables { get; } = [];
    public bool HasRuntimes => Runtimes.Count > 0;
    public bool ShowStructured => !AdvancedMode;
    public bool CanChangeAdvanced => AdvancedAllowed || AdvancedMode;
    public bool IsValid => Validation.Length == 0;
    public string Validation
    {
        get
        {
            if (SelectedJava is null || SelectedJava.NodeId != NodeId || !Runtimes.Any(runtime => runtime.Id == SelectedJava.Id)) { return Text("desktop.create-java-required"); }
            if (!ValidInteger(MinimumHeap, 1, 1048576) || !ValidInteger(MaximumHeap, MinimumHeap, 1048576)) { return Text("desktop.launch-memory-invalid"); }
            if (AdvancedMode && !AdvancedAllowed) { return Text("desktop.launch-advanced-disabled"); }
            if (AdvancedMode ? Arguments(AdvancedArguments).Length == 0 || !ValidArguments(AdvancedArguments) :
                !IsRelativeTarget(Target) || TargetKind is < 0 or > 1 || !ValidArguments(JvmArguments) || !ValidArguments(ServerArguments)) { return Text("desktop.launch-arguments-invalid"); }
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            return EnvironmentVariables.Any(variable => string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Contains('=', StringComparison.Ordinal) ||
                variable.Name.Any(char.IsControl) || variable.Value.Contains('\0', StringComparison.Ordinal) || !keys.Add(variable.Name))
                ? Text("desktop.launch-environment-invalid")
                : string.Empty;
        }
    }
    public string Preview
    {
        get
        {
            if (!IsValid) { return Validation; }
            List<string> arguments = AdvancedMode ? [.. Arguments(AdvancedArguments)] :
                [$"-Xms{MinimumHeap.ToString(CultureInfo.InvariantCulture)}m", $"-Xmx{MaximumHeap.ToString(CultureInfo.InvariantCulture)}m", .. Arguments(JvmArguments),
                    .. TargetKind == 0 ? new[] { "-jar", Target } : new[] { "@" + Target }, .. Arguments(ServerArguments)];
            return string.Join(' ', new[] { SelectedJava!.ExecutablePath }.Concat(arguments).Select(QuoteArgument));
        }
    }
    internal void SetRuntimes(IReadOnlyList<JavaRuntimeDto> runtimes, Guid? selectedId = null)
    {
        Runtimes = runtimes; SelectedJava = selectedId is Guid id ? runtimes.FirstOrDefault(runtime => runtime.Id == id) : runtimes.Count > 0 ? runtimes[0] : null; Changed();
    }
    internal void Load(StructuredLaunchRequest launch)
    {
        SelectedJava = Runtimes.FirstOrDefault(runtime => runtime.Id == launch.JavaRuntimeId);
        MinimumHeap = launch.MinimumHeapMegabytes; MaximumHeap = launch.MaximumHeapMegabytes; TargetKind = launch.TargetKind == "argfile" ? 1 : 0;
        Target = launch.Target; JvmArguments = string.Join(Environment.NewLine, launch.JvmArguments); ServerArguments = string.Join(Environment.NewLine, launch.ServerArguments);
        AdvancedMode = launch.AdvancedMode; AdvancedArguments = string.Join(Environment.NewLine, launch.AdvancedArguments ?? []);
        EnvironmentVariables.Clear();
        foreach (KeyValuePair<string, string> variable in launch.EnvironmentVariables ?? new Dictionary<string, string>()) { EnvironmentVariables.Add(new(_localization, variable.Key, variable.Value)); }
        Changed();
    }
    internal StructuredLaunchRequest Request()
    {
        string[] advanced = Arguments(AdvancedArguments);
        return new(SelectedJava!.Id, (int)MinimumHeap, (int)MaximumHeap, Arguments(JvmArguments), TargetKind == 0 ? "jar" : "argfile", Target.Trim(), Arguments(ServerArguments),
            EnvironmentVariables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.OrdinalIgnoreCase), AdvancedMode,
            advanced.Length == 0 ? null : advanced);
    }
    internal string Snapshot()
    {
        return FormSnapshot.Capture(new
        {
            Java = SelectedJava?.Id,
            MinimumHeap,
            MaximumHeap,
            TargetKind,
            Target,
            JvmArguments,
            ServerArguments,
            AdvancedMode,
            AdvancedArguments,
            Environment = EnvironmentVariables.Select(variable => new { variable.Name, variable.Value }).ToArray()
        });
    }
    internal static string[] Arguments(string value) { return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries); }
    private static bool ValidArguments(string value) { return Arguments(value).All(argument => !string.IsNullOrWhiteSpace(argument) && !argument.Any(char.IsControl)); }
    internal static bool ValidInteger(double value, double minimum, double maximum) { return double.IsFinite(value) && value >= minimum && value <= maximum && value == Math.Truncate(value); }
    internal static bool IsRelativeTarget(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Any(char.IsControl) && !value.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
    }
    internal static string QuoteArgument(string value)
    {
        // Windows 的参数解析会把引号前及尾部的反斜杠折叠；预览按同一规则显示，实际启动仍使用参数数组。
        StringBuilder result = new("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            _ = result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [RelayCommand]
    private void AddEnvironment() { EnvironmentVariables.Add(new(_localization)); }
    [RelayCommand]
    private void RemoveEnvironment(LaunchEnvironmentVariable variable) { _ = EnvironmentVariables.Remove(variable); }
    private void OnVariablesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (LaunchEnvironmentVariable removed in _subscribedVariables.Where(variable => !EnvironmentVariables.Contains(variable)).ToArray()) { removed.PropertyChanged -= OnVariableChanged; _ = _subscribedVariables.Remove(removed); }
        foreach (LaunchEnvironmentVariable added in EnvironmentVariables.Where(variable => !_subscribedVariables.Contains(variable))) { added.PropertyChanged += OnVariableChanged; _ = _subscribedVariables.Add(added); }
        Changed();
    }
    private void OnVariableChanged(object? sender, PropertyChangedEventArgs args) { Changed(); }
    private void Changed()
    {
        foreach (string name in new[] { nameof(IsValid), nameof(Validation), nameof(Preview), nameof(HasRuntimes), nameof(ShowStructured), nameof(CanChangeAdvanced) }) { OnPropertyChanged(name); }
    }
    public void Dispose()
    {
        EnvironmentVariables.CollectionChanged -= OnVariablesChanged;
        foreach (LaunchEnvironmentVariable variable in _subscribedVariables) { variable.PropertyChanged -= OnVariableChanged; }
        GC.SuppressFinalize(this);
    }
}
