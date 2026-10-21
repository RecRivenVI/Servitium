using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Servitium.Contracts.Instances;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceSettingsViewModel
{
    public string Validation
    {
        get
        {
            if (!HasData) { return string.Empty; }
            if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 64 || Name.Any(char.IsControl) || SplitTags().Count > 32) { return Text("desktop.settings-name-invalid"); }
            if (!Launch.IsValid) { return Launch.Validation; }
            if (!Path.IsPathFullyQualified(WorkingDirectory) || WorkingDirectory.Any(char.IsControl)) { return Text("desktop.settings-directory-invalid"); }
            if (!double.IsNaN(PrimaryPort) && !LaunchEditor.ValidInteger(PrimaryPort, 1, 65535)) { return Text("desktop.settings-port-invalid"); }
            if (string.IsNullOrWhiteSpace(StopCommand) || StopCommand.Any(char.IsControl) || !LaunchEditor.ValidInteger(StopTimeout, 1, 600) ||
                !LaunchEditor.ValidInteger(AutoStartDelay, 0, 600) || !LaunchEditor.ValidInteger(MaximumRestartAttempts, -1, 100) ||
                !LaunchEditor.ValidInteger(RestartInterval, 0, 600) || !LaunchEditor.ValidInteger(RestartReset, 60, 86400)) { return Text("desktop.settings-lifecycle-invalid"); }
            if (RunCredentialAlias.Length > 64 || RunCredentialAlias.Any(char.IsControl)) { return Text("desktop.settings-run-alias-invalid"); }
            if (!LaunchEditor.ValidInteger(TerminalColumns, 20, 500) || !LaunchEditor.ValidInteger(TerminalRows, 5, 200) ||
                !Encodings.Any(choice => choice.Value == InputEncoding) || !Encodings.Any(choice => choice.Value == OutputEncoding) || NewLine is not ("LF" or "CRLF")) { return Text("desktop.settings-terminal-invalid"); }
            if (!LaunchEditor.ValidInteger(RconPort, 1, 65535) || !LaunchEditor.ValidInteger(QueryPort, 1, 65535) || string.IsNullOrWhiteSpace(QueryHost) || QueryHost.Any(char.IsControl) ||
                RconPassword.Any(char.IsControl) || ClearRconPassword && RconPassword.Length > 0) { return Text("desktop.settings-query-invalid"); }
            if (UpdateKind is < 0 or > 2 || IsJarUpdate && (!LaunchEditor.IsRelativeTarget(UpdateSource) || !LaunchEditor.IsRelativeTarget(UpdateTarget)) ||
                IsChannelUpdate && (SelectedChannel is null || !Channels.Any(channel => channel.Id == SelectedChannel.Id))) { return Text("desktop.settings-update-invalid"); }
            return Shortcuts.Count > 20 || Shortcuts.Any(shortcut => string.IsNullOrWhiteSpace(shortcut.Name) || shortcut.Name.Trim().Length > 32 ||
                string.IsNullOrWhiteSpace(shortcut.CommandText) || shortcut.CommandText.Length > 1024 || shortcut.CommandText.Any(char.IsControl))
                ? Text("desktop.settings-shortcuts-invalid")
                : string.Empty;
        }
    }
    public bool HasValidation => HasData && Validation.Length > 0;
    private IReadOnlyList<string> SplitTags()
    {
        return _details is not null && Tags == string.Join(Environment.NewLine, _details.Instance.Tags) ? _details.Instance.Tags :
            Tags.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    private bool DirectoryChanged => _details is not null && !string.Equals(WorkingDirectory.TrimEnd('\\', '/'), _details.Instance.WorkingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private UpdateInstanceRequest Request()
    {
        UpdateInstanceRequest original = _details!.Configuration!;
        using var before = JsonDocument.Parse(_baseline);
        using var current = JsonDocument.Parse(Snapshot());
        bool Edited(params string[] names)
        {
            return names.Any(name => before.RootElement.GetProperty(name).GetRawText() != current.RootElement.GetProperty(name).GetRawText());
        }

        string? password = ClearRconPassword ? string.Empty : RconPassword.Length == 0 ? null : RconPassword;
        // 未编辑的配置块原样提交，避免把可选空值、编码大小写等规范化为另一种表达。
        return original with
        {
            Name = Name.Trim(),
            Tags = SplitTags(),
            Notes = Notes == (_details.Notes ?? string.Empty) ? _details.Notes : Notes.Length == 0 ? null : Notes,
            Launch = Edited(nameof(Launch)) ? Launch.Request() : _details.Launch,
            StopCommand = StopCommand,
            StopTimeoutSeconds = (int)StopTimeout,
            AutoStart = AutoStart,
            AutoRestart = AutoRestart,
            MaximumRestartAttempts = (int)MaximumRestartAttempts,
            Rcon = new(RconEnabled, (int)RconPort, password),
            Query = new(QueryEnabled, QueryHost, (int)QueryPort),
            WorkingDirectory = DirectoryChanged ? WorkingDirectory : null,
            PrimaryPort = double.IsNaN(PrimaryPort) ? null : (int)PrimaryPort,
            AutoStartDelaySeconds = (int)AutoStartDelay,
            RestartIntervalSeconds = (int)RestartInterval,
            RestartResetSeconds = (int)RestartReset,
            RunCredentialAlias = Edited(nameof(RunCredentialAlias)) ? string.IsNullOrWhiteSpace(RunCredentialAlias) ? null : RunCredentialAlias : original.RunCredentialAlias,
            Terminal = Edited(nameof(UseConPty), nameof(TerminalColumns), nameof(TerminalRows), nameof(InputEncoding), nameof(OutputEncoding), nameof(NewLine), nameof(AnsiEnabled))
                ? new(UseConPty, (int)TerminalColumns, (int)TerminalRows, InputEncoding, OutputEncoding, NewLine, AnsiEnabled) : original.Terminal,
            UpdateAction = Edited(nameof(UpdateKind), nameof(UpdateSource), nameof(UpdateTarget), "Channel")
                ? UpdateKind switch { 1 => new("jar-replace", UpdateSource, UpdateTarget), 2 => new("hmcl-channel-sync", ChannelId: SelectedChannel!.Id), _ => new UpdateActionRequest() } : original.UpdateAction,
            ShortcutCommands = Edited(nameof(Shortcuts)) ? [.. Shortcuts.Select(shortcut => shortcut.Request())] : original.ShortcutCommands,
        };
    }
    private string Snapshot()
    {
        return FormSnapshot.Capture(new
        {
            Name,
            Tags,
            Notes,
            WorkingDirectory,
            PrimaryPort,
            Launch = Launch.Snapshot(),
            StopCommand,
            StopTimeout,
            AutoStart,
            AutoStartDelay,
            AutoRestart,
            MaximumRestartAttempts,
            RestartInterval,
            RestartReset,
            RunCredentialAlias,
            UseConPty,
            TerminalColumns,
            TerminalRows,
            InputEncoding,
            OutputEncoding,
            NewLine,
            AnsiEnabled,
            RconEnabled,
            RconPort,
            RconPassword,
            ClearRconPassword,
            QueryEnabled,
            QueryHost,
            QueryPort,
            UpdateKind,
            UpdateSource,
            UpdateTarget,
            Channel = SelectedChannel?.Id,
            Shortcuts = Shortcuts.Select(shortcut => shortcut.Request()).ToArray()
        });
    }
}
