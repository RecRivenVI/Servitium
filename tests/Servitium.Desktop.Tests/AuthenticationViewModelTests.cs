using System;
using System.Net;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Client.Session;
using Servitium.Contracts.Identity;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Authentication;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed class AuthenticationViewModelTests
{
    [Fact]
    public async Task SetupRejectsMismatchedPasswordsAndCannotSubmitTwiceWhilePending()
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InitializeRequest? sent = null;
        Navigator navigation = new();
        using SetupViewModel model = new(Localization(), (request, _) => { sent = request; return ready.Task; }, navigation);
        model.UserName = " owner ";
        model.Password = "RoundOne-Example-Password";
        model.ConfirmPassword = "different";
        Assert.False(model.SubmitCommand.CanExecute(null));
        model.ConfirmPassword = model.Password;
        Task submit = model.SubmitCommand.ExecuteAsync(null);
        Assert.True(model.IsBusy);
        Assert.False(model.SubmitCommand.CanExecute(null));
        Assert.NotNull(sent);
        Assert.Equal("owner", sent.UserName);
        ready.SetResult();
        await submit.ConfigureAwait(true);
        Assert.Equal("PG-002", navigation.LastPage);
        Assert.Empty(model.Password);
        Assert.Empty(model.ConfirmPassword);
    }

    [Fact]
    public async Task TwoFactorChallengeKeepsNavigationUnauthenticatedUntilCodeVerification()
    {
        Navigator navigation = new();
        using LoginViewModel model = new(Localization(), (_, _) => Task.FromResult(new SignInResult(true, null)),
            _ => Task.CompletedTask, _ => Task.FromResult(Bootstrap()), navigation);
        model.UserName = "owner";
        model.Password = "RoundOne-Example-Password";
        await model.SubmitCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("PG-003", navigation.LastPage);
        Assert.Empty(model.Password);
    }

    [Fact]
    public async Task SignInErrorExplainsNextStepWithoutExposingTraceOrPassword()
    {
        Navigator navigation = new();
        using LoginViewModel model = new(Localization(), (_, _) => Task.FromException<SignInResult>(
            new ServitiumApiException(HttpStatusCode.Unauthorized, "invalid-credentials", "private-trace", new System.Collections.Generic.Dictionary<string, string>(), null)),
            _ => Task.CompletedTask, _ => Task.FromResult(Bootstrap()), navigation);
        model.UserName = "owner";
        model.Password = "RoundOne-Example-Password";
        await model.SubmitCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.True(model.HasMessage);
        Assert.Contains("重试", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-trace", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(model.Password, model.Message, StringComparison.Ordinal);
        Assert.Null(navigation.LastPage);
    }

    [Fact]
    public async Task TotpRequiresAsciiDigitsAndAllowsAnExplicitRecoveryCode()
    {
        string? sent = null;
        Navigator navigation = new();
        using TotpViewModel model = new(Localization(), (code, _) => { sent = code; return Task.CompletedTask; }, _ => Task.CompletedTask, navigation);
        model.Code = "１２３４５６";
        Assert.False(model.SubmitCommand.CanExecute(null));
        model.Code = "123456";
        Assert.True(model.SubmitCommand.CanExecute(null));
        model.UseRecoveryCode = true;
        Assert.Empty(model.Code);
        model.Code = " recovery-code ";
        await model.SubmitCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("recovery-code", sent);
        Assert.Equal("PG-040", navigation.LastPage);
    }

    [Fact]
    public async Task AutomaticExternalSignInCanBeCancelledBeforeOpeningTheBrowser()
    {
        bool opened = false;
        using LoginViewModel model = new(Localization(), (_, _) => throw new InvalidOperationException(),
            _ => { opened = true; return Task.CompletedTask; },
            _ => Task.FromResult(new AuthenticationBootstrapDto(true, true, "Fixture OIDC", null, true, null)), new Navigator());
        Task loading = model.LoadAsync();
        Assert.True(model.IsRedirecting);
        model.CancelRedirectCommand.Execute(null);
        await loading.ConfigureAwait(true);
        Assert.False(opened);
        Assert.False(model.IsRedirecting);
        Assert.True(model.ExternalCommand.CanExecute(null));
    }

    private static AuthenticationBootstrapDto Bootstrap() { return new(true, false, null, null, false, null); }

    private static LocalizationService Localization()
    {
        LocalizationService service = new();
        service.Reload("zh-Hans");
        return service;
    }

    private sealed class Navigator : IPageNavigator
    {
        internal string? LastPage { get; private set; }
        public void Navigate(string pageId, bool addToBackStack = true) { LastPage = pageId; }
    }
}
