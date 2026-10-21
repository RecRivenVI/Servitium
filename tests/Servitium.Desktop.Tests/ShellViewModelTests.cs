using System;
using System.Linq;

using Servitium.Contracts.Identity;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void RealtimeDisconnectWarningClearsOnReconnectAndSignOut()
    {
        ShellViewModel model = new(new LocalizationService());
        model.ApplyRealtimeState("reconnecting"); Assert.False(model.IsRealtimeDisconnected);
        model.IsAuthenticated = true; model.ApplyRealtimeState("reconnecting"); Assert.True(model.IsRealtimeDisconnected);
        model.ApplyRealtimeState("connected"); Assert.False(model.IsRealtimeDisconnected);
        model.ApplyRealtimeState("disconnected"); model.ApplyPrincipal(null, false); Assert.False(model.IsRealtimeDisconnected);
    }

    [Fact]
    public void UnrelatedAuthorizationEventsDoNotDiscardEditsButRevocationChangesTheScope()
    {
        PrincipalDto previous = Principal("User") with { GrantedInstanceIds = [Guid.NewGuid(), Guid.NewGuid()] };
        Assert.True(ShellViewModel.HasSameAuthorization(previous, previous with { GrantedInstanceIds = [.. previous.GrantedInstanceIds.Reverse()] }));
        Assert.False(ShellViewModel.HasSameAuthorization(previous, previous with { GrantedInstanceIds = [] }));
        Assert.False(ShellViewModel.HasSameAuthorization(previous, previous with { User = previous.User with { Role = "Administrator" } }));
        Assert.False(ShellViewModel.HasSameAuthorization(previous, null));
    }

    [Fact]
    public void SingleAccountOwnerStartsAtInstancesAndKeepsUserManagementOutOfNavigation()
    {
        ShellViewModel model = Create();
        model.ApplyPrincipal(Principal("Owner"), false);
        Assert.Equal("PG-010", model.DefaultPage);
        Assert.DoesNotContain(model.Destinations, item => item.PageId is "PG-040" or "PG-030");
        Assert.Collection(model.Destinations,
            item => Assert.Equal("PG-010", item.PageId),
            item => Assert.Equal("PG-020", item.PageId),
            item => Assert.Equal("PG-060", item.PageId));
        Assert.Equal("实例", model.Destinations[0].Title);
    }

    [Fact]
    public void ChangingToOrdinaryUserRemovesAdministrativeDestinations()
    {
        ShellViewModel model = Create();
        model.ApplyPrincipal(Principal("Owner"), true);
        Assert.Contains(model.Destinations, item => item.PageId == "PG-030");
        model.ApplyPrincipal(Principal("User"), true);
        Assert.Equal("PG-011", Assert.Single(model.Destinations).PageId);
        Assert.DoesNotContain(model.FooterDestinations, item => item.PageId == "PG-041");
        model.ApplyPrincipal(null, false);
        Assert.False(model.IsAuthenticated);
        Assert.Empty(model.Destinations);
        Assert.Empty(model.FooterDestinations);
    }

    [Fact]
    public void IsolatedWindowsNeverShareTheNormalInstanceRegistration()
    {
        string first = SingleInstanceService.RegistrationKey(true, @"D:\TaskOne");
        Assert.NotEqual(SingleInstanceService.RegistrationKey(false, null), first);
        Assert.NotEqual(first, SingleInstanceService.RegistrationKey(true, @"D:\TaskTwo"));
        Assert.Equal(first, SingleInstanceService.RegistrationKey(true, @"d:\taskone\"));
        _ = Assert.Throws<InvalidOperationException>(() => SingleInstanceService.RegistrationKey(true, null));
        Assert.NotEqual(
            WindowsCredentialSessionTokenStore.ResourceName("isolated-server", @"D:\TaskOne"),
            WindowsCredentialSessionTokenStore.ResourceName("isolated-server", @"D:\TaskTwo"));
        Assert.Equal("Servitium.Desktop/isolated-server", WindowsCredentialSessionTokenStore.ResourceName("isolated-server", null));
    }

    [Fact]
    public void NarrowShellKeepsSearchAvailableWithoutCrowdingTheConnection()
    {
        ShellViewModel model = Create();
        Assert.False(model.ShowInlineSearch); Assert.False(model.ShowCompactSearch);
        model.IsAuthenticated = true;
        Assert.True(model.ShowInlineSearch); Assert.False(model.ShowCompactSearch);
        model.IsNarrow = true;
        Assert.False(model.ShowInlineSearch); Assert.True(model.ShowCompactSearch);
        Assert.False(model.ShowStandardNavigationToggle); Assert.False(model.ShowOperationSymbol);
        model.ApplyPrincipal(null, false);
        Assert.False(model.ShowInlineSearch); Assert.False(model.ShowCompactSearch);
    }

    private static ShellViewModel Create()
    {
        LocalizationService localization = new();
        localization.Reload("zh-Hans");
        return new(localization);
    }

    private static PrincipalDto Principal(string role)
    {
        return new(new UserDto(Guid.NewGuid(), "test", "Test", role, false, null, DateTimeOffset.UnixEpoch), [], "zh-Hans");
    }
}
