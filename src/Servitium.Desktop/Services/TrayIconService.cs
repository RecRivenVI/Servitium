using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;

using Servitium.Contracts.Instances;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

internal sealed partial class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifyMessage = 1;
    private const uint NotifyIcon = 2;
    private const uint NotifyTip = 4;
    private const uint MouseLeftDoubleClick = 0x0203;
    private const uint MouseRightUp = 0x0205;
    private const uint MenuString = 0;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuPopup = 0x0010;
    private const uint MenuDisabled = 0x0002;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const nuint ShowCommand = 1001;
    private const nuint AboutCommand = 1003;
    private const nuint ExitCommand = 1004;
    private const nuint PauseThirtyMinutesCommand = 1101;
    private const nuint PauseOneHourCommand = 1102;
    private const nuint PauseUntilRestartCommand = 1103;
    private const nuint CancelPauseCommand = 1104;
    private const nuint FirstInstanceCommand = 2000;
    private const nuint SubclassId = 0x53565254;
    private readonly nint _windowHandle;
    private readonly LocalizationService _localization;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly Dictionary<nuint, TrayInstanceActionEventArgs> _instanceCommands = [];
    private NotifyIconData _data;
    private IReadOnlyList<TrayInstanceMenuItem> _instances = [];
    private bool _disposed;

    internal TrayIconService(Window window, LocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _subclassProcedure = WindowSubclass;
        if (!SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _data = CreateData(32512, _localization.Get("app.window-title"));
        if (!ShellNotifyIcon(NotifyAdd, ref _data))
        {
            _ = RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal event EventHandler? ShowRequested;

    internal event EventHandler<NotificationPauseRequestedEventArgs>? PauseRequested;

    internal event EventHandler<TrayInstanceActionEventArgs>? InstanceActionRequested;

    internal event EventHandler? AboutRequested;

    internal event EventHandler? ExitRequested;

    internal void UpdateState(string state)
    {
        int icon = state switch
        {
            "warning" => 32515,
            "disconnected" => 32513,
            _ => 32512,
        };
        _data.HIcon = LoadIcon(0, icon);
        _data.Tip = _localization.Get("app.window-title") + " · " + _localization.Get("shell.status-" + (state == "normal" ? "ready" : state));
        _ = ShellNotifyIcon(NotifyModify, ref _data);
    }

    internal void UpdateInstances(IReadOnlyList<InstanceDto> instances, IReadOnlyList<Guid> recentIds)
    {
        _instances = TrayMenuModel.Recent(instances, recentIds);
    }

    private nint WindowSubclass(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (message == CallbackMessage)
        {
            uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (mouseMessage == MouseLeftDoubleClick)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            if (mouseMessage == MouseRightUp)
            {
                ShowMenu();
                return 0;
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MenuString, ShowCommand, _localization.Get("tray.show"));
            _ = AppendMenu(menu, MenuSeparator, 0, null);
            AppendInstances(menu);
            _ = AppendMenu(menu, MenuSeparator, 0, null);
            AppendNotificationPause(menu);
            _ = AppendMenu(menu, MenuSeparator, 0, null);
            _ = AppendMenu(menu, MenuString, AboutCommand, _localization.Get("tray.about"));
            _ = AppendMenu(menu, MenuString, ExitCommand, _localization.Get("shell.exit"));
            _ = SetForegroundWindow(_windowHandle);
            _ = GetCursorPos(out Point point);
            nuint selected = TrackPopupMenu(menu, TrackRightButton | TrackReturnCommand, point.X, point.Y, 0, _windowHandle, 0);
            if (selected == ShowCommand)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (_instanceCommands.TryGetValue(selected, out TrayInstanceActionEventArgs? instanceAction))
            {
                InstanceActionRequested?.Invoke(this, instanceAction);
            }
            else if (selected == PauseThirtyMinutesCommand)
            {
                PauseRequested?.Invoke(this, new NotificationPauseRequestedEventArgs(NotificationPauseMode.ThirtyMinutes));
            }
            else if (selected == PauseOneHourCommand)
            {
                PauseRequested?.Invoke(this, new NotificationPauseRequestedEventArgs(NotificationPauseMode.OneHour));
            }
            else if (selected == PauseUntilRestartCommand)
            {
                PauseRequested?.Invoke(this, new NotificationPauseRequestedEventArgs(NotificationPauseMode.UntilRestart));
            }
            else if (selected == CancelPauseCommand)
            {
                PauseRequested?.Invoke(this, new NotificationPauseRequestedEventArgs(NotificationPauseMode.Cancel));
            }
            else if (selected == AboutCommand)
            {
                AboutRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (selected == ExitCommand)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void AppendInstances(nint menu)
    {
        nint instancesMenu = CreatePopupMenu();
        if (instancesMenu == 0)
        {
            return;
        }

        _instanceCommands.Clear();
        nuint command = FirstInstanceCommand;
        foreach (TrayInstanceMenuItem instance in _instances)
        {
            nint instanceMenu = CreatePopupMenu();
            if (instanceMenu == 0)
            {
                continue;
            }

            foreach (string action in new[] { "open-console", "start", "stop", "restart" })
            {
                _instanceCommands.Add(command, new TrayInstanceActionEventArgs(instance.Id, action));
                _ = AppendMenu(instanceMenu, MenuString, command, _localization.Get("action." + action));
                command++;
            }

            string marker = string.Equals(instance.Status, "Running", StringComparison.OrdinalIgnoreCase) ? "● " : "○ ";
            _ = AppendMenu(instancesMenu, MenuPopup, unchecked((nuint)instanceMenu), marker + instance.Name);
        }

        if (_instances.Count == 0)
        {
            _ = AppendMenu(instancesMenu, MenuString | MenuDisabled, 0, _localization.Get("common.empty"));
        }

        _ = AppendMenu(menu, MenuPopup, unchecked((nuint)instancesMenu), _localization.Get("tray.instances"));
    }

    private void AppendNotificationPause(nint menu)
    {
        nint pauseMenu = CreatePopupMenu();
        if (pauseMenu == 0)
        {
            return;
        }

        _ = AppendMenu(pauseMenu, MenuString, PauseThirtyMinutesCommand, _localization.Get("tray.pause-30-minutes"));
        _ = AppendMenu(pauseMenu, MenuString, PauseOneHourCommand, _localization.Get("tray.pause-one-hour"));
        _ = AppendMenu(pauseMenu, MenuString, PauseUntilRestartCommand, _localization.Get("tray.pause-until-restart"));
        _ = AppendMenu(pauseMenu, MenuString, CancelPauseCommand, _localization.Get("tray.cancel-pause"));
        _ = AppendMenu(menu, MenuPopup, unchecked((nuint)pauseMenu), _localization.Get("tray.pause-notifications"));
    }

    private NotifyIconData CreateData(int icon, string tip)
    {
        return new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NotifyMessage | NotifyIcon | NotifyTip,
            CallbackMessage = CallbackMessage,
            HIcon = LoadIcon(0, icon),
            Tip = tip,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShellNotifyIcon(NotifyDelete, ref _data);
        _ = RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint WindowHandle;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        internal readonly int X;
        internal readonly int Y;
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data)
    {
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NotifyIconData>());
        try
        {
            Marshal.StructureToPtr(data, buffer, false);
            return ShellNotifyIconNative(message, buffer);
        }
        finally
        {
            Marshal.DestroyStructure<NotifyIconData>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconNative(uint message, nint data);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIcon(nint instance, int resourceId);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint subclassId, nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint identifier, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nuint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
}
