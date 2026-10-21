using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Client;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Files;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels;

internal sealed partial class ShellViewModel(LocalizationService localization) : ObservableObject
{
    internal static bool HasSameAuthorization(PrincipalDto? previous, PrincipalDto? current)
    {
        return previous is null || current is null
            ? previous is null && current is null
            : previous.User.Id == current.User.Id && previous.User.Role == current.User.Role &&
            previous.User.Disabled == current.User.Disabled && previous.GrantedInstanceIds.ToHashSet().SetEquals(current.GrantedInstanceIds);
    }

    public string ConnectionTitle { get; set => SetProperty(ref field, value); } = string.Empty;

    public string ConnectionAddress { get; set => SetProperty(ref field, value); } = string.Empty;

    public string ConnectionStatus { get; set => SetProperty(ref field, value); } = string.Empty;

    public bool IsAuthenticated { get; set { if (SetProperty(ref field, value)) { NotifyResponsiveSearch(); } } }
    public bool IsNarrow { get; set { if (SetProperty(ref field, value)) { NotifyResponsiveSearch(); OnPropertyChanged(nameof(ShowOperationSymbol)); OnPropertyChanged(nameof(ShowStandardNavigationToggle)); } } }
    public bool ShowInlineSearch => IsAuthenticated && !IsNarrow;
    public bool ShowCompactSearch => IsAuthenticated && IsNarrow;
    public bool ShowOperationSymbol => !IsNarrow;
    public bool ShowStandardNavigationToggle => !IsNarrow;
    private void NotifyResponsiveSearch() { OnPropertyChanged(nameof(ShowInlineSearch)); OnPropertyChanged(nameof(ShowCompactSearch)); }
    public bool IsRealtimeDisconnected { get; private set => SetProperty(ref field, value); }
    public string RealtimeNotice { get; private set => SetProperty(ref field, value); } = string.Empty;

    internal void ApplyRealtimeState(string state)
    {
        RealtimeNotice = state == "connected" ? string.Empty : localization.Get(state == "reconnecting" ? "desktop.server-reconnecting" : "desktop.server-disconnected-next");
        IsRealtimeDisconnected = IsAuthenticated && state != "connected";
    }

    public string OperationSummary { get; set => SetProperty(ref field, value); } = string.Empty;

    public string SearchMessage { get; set => SetProperty(ref field, value); } = string.Empty;
    public bool IsCompact
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ShowOperationSummary)); OnPropertyChanged(nameof(SearchHint)); }
        }
    }
    public bool ShowOperationSummary => !IsCompact;

    public ObservableCollection<NavigationDestination> Destinations { get; } = [];

    public ObservableCollection<NavigationDestination> FooterDestinations { get; } = [];

    public string SearchHint => localization.Get(IsCompact ? "desktop.search-hint-short" : "desktop.search-hint");
    public int OperationCount { get; private set => SetProperty(ref field, value); }

    public string ManageConnectionsText => localization.Get("desktop.manage-connections");
    public string InstancesText => localization.Get("desktop.nav-instances");
    public string NodesText => localization.Get("desktop.nav-nodes");
    public string DistributionText => localization.Get("desktop.nav-distribution");
    public string UsersText => localization.Get("desktop.nav-users");
    public string ActivityText => localization.Get("desktop.nav-activity");
    public string SettingsText => localization.Get("desktop.nav-settings");
    public bool ShowAdministration => Destinations.Any(item => item.PageId == "PG-020");
    public bool ShowUsers => Destinations.Any(item => item.PageId == "PG-030");

    internal void SetConnection(string name, Uri address, string statusKey)
    {
        ConnectionTitle = name;
        ConnectionAddress = address.AbsoluteUri.TrimEnd('/');
        ConnectionStatus = localization.Get(statusKey);
    }

    internal void ApplyPrincipal(PrincipalDto? principal, bool multipleUsers)
    {
        IsAuthenticated = principal is not null;
        Destinations.Clear();
        FooterDestinations.Clear();
        if (principal is null)
        {
            IsRealtimeDisconnected = false;
            OperationSummary = string.Empty; OperationCount = 0;
            NotifyNavigationVisibility();
            return;
        }

        bool administrator = principal.User.Role is "Owner" or "Administrator";
        Destinations.Add(Destination(administrator ? "PG-010" : "PG-011", "desktop.nav-instances"));
        if (administrator)
        {
            Destinations.Add(Destination("PG-020", "desktop.nav-nodes"));
            Destinations.Add(Destination("PG-060", "desktop.nav-distribution"));
            if (multipleUsers)
            {
                Destinations.Add(Destination("PG-030", "desktop.nav-users"));
            }
        }

        FooterDestinations.Add(Destination("PG-044", "desktop.nav-activity"));
        FooterDestinations.Add(Destination(administrator ? "PG-041" : "PG-032", "desktop.nav-settings"));
        NotifyNavigationVisibility();
    }

    private void NotifyNavigationVisibility()
    {
        OnPropertyChanged(nameof(ShowAdministration));
        OnPropertyChanged(nameof(ShowUsers));
    }

    internal async Task RefreshAsync(ServitiumApiClient client, PrincipalDto? principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        bool multipleUsers = principal?.User.Role is "Owner" or "Administrator" &&
            (await client.GetUserOverviewAsync(cancellationToken).ConfigureAwait(true)).Total > 1;
        cancellationToken.ThrowIfCancellationRequested();
        ApplyPrincipal(principal, multipleUsers);
        if (principal is null)
        {
            return;
        }

        int count = 0;
        string? cursor = null;
        do
        {
            CursorPage<OperationDetailsDto> page = await client.ListOperationsAsync(null, null, null, null, cursor, 200, cancellationToken).ConfigureAwait(true);
            count += page.Items.Count(item => item.Operation.Status is "Pending" or "Running");
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        cancellationToken.ThrowIfCancellationRequested();
        OperationCount = count; OperationSummary = localization.Format("desktop.operations-count", count);
    }

    internal string DefaultPage => Destinations.Count == 0 ? "PG-002" : Destinations[0].PageId;

    internal async Task<IReadOnlyList<ShellSearchResult>> SearchAsync(
        ServitiumApiClient client,
        PrincipalDto principal,
        IReadOnlyList<InstanceDto> instances,
        InstanceDto? selected,
        string query,
        CancellationToken cancellationToken)
    {
        SearchMessage = string.Empty;
        List<ShellSearchResult> results = [.. instances.Where(instance =>
            DesktopPageAuthorization.CanAccess("instance-grant", principal, instance.Id) &&
            instance.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(instance => new ShellSearchResult(instance.Name + " · " + instance.NodeName, instance, null, null))];
        if (selected is null || string.IsNullOrWhiteSpace(query) ||
            !DesktopPageAuthorization.CanAccess("instance-grant", principal, selected.Id)) { return results; }
        try
        {
            InstanceDetailsDto details = await client.GetInstanceAsync(selected.Id, cancellationToken).ConfigureAwait(true);
            results.AddRange((details.Configuration?.ShortcutCommands ?? []).Where(command => command.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(command => new ShellSearchResult(localization.Get("desktop.search-command") + " · " + command.Name, selected, null, command.Command)));
            FileEntryPageDto files = await client.ListFilesAsync(selected.Id, selected.NodeId, string.Empty, null, 20, query, "name", false, cancellationToken).ConfigureAwait(true);
            results.AddRange(files.Items.Select(file => new ShellSearchResult(localization.Get("desktop.search-file") + " · " + file.Path, selected, file.Path, null)));
        }
        catch (Exception exception) when (exception is ServitiumApiException or System.Net.Http.HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchMessage = DesktopErrorPresentation.Describe(exception, localization);
        }
        return results;
    }

    private NavigationDestination Destination(string pageId, string titleKey)
    {
        return new(pageId, localization.Get(titleKey));
    }
}
