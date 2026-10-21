using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml.Controls;

using Servitium.Client;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels;

internal abstract class DesktopViewModel(LocalizationService localization) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public bool IsBusy
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) { return; }
            OnPropertyChanged(nameof(CanInteract));
            RefreshCommands();
        }
    }

    public bool CanInteract => !IsBusy && !_disposed;

    public string Message
    {
        get;
        protected set
        {
            if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasMessage)); }
        }
    } = string.Empty;

    public bool HasMessage => Message.Length > 0;

    public InfoBarSeverity Severity { get; protected set => SetProperty(ref field, value); } = InfoBarSeverity.Informational;

    public string Text(string key) { return localization.Get(key); }
    protected LocalizationService Localization => localization;
    protected CancellationToken LifetimeToken => _lifetime.Token;

    protected string Format(string key, params object?[] values) { return localization.Format(key, values); }

    protected virtual void RefreshCommands() { }

    protected async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) { return; }
        IsBusy = true;
        Message = string.Empty;
        Severity = InfoBarSeverity.Informational;
        try
        {
            await action(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            Severity = InfoBarSeverity.Warning;
            Message = Text("desktop.request-timeout");
        }
        catch (Exception exception) when (exception is ServitiumApiException or DesktopOperationException or AggregateException or HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Severity = InfoBarSeverity.Error;
            Message = DesktopErrorPresentation.Describe(exception, localization);
        }
        finally { IsBusy = false; }
    }

    public virtual void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        OnPropertyChanged(nameof(CanInteract));
        RefreshCommands();
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
