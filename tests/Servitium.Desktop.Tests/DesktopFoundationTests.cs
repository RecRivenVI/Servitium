using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Instances;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels;

using Xunit;

namespace Servitium.Desktop.Tests;

/// <summary>Desktop 纯逻辑与平台边界回归测试。</summary>
[Trait("Category", "Unit")]
public sealed class DesktopFoundationTests
{
    [Fact]
    public void DesktopSelfTestDataDirectoryIsIsolatedAndIgnoredByNormalLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), "servitium-desktop-services-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(root, DesktopSelfTestOptions.ResolveDataDirectory(selfTest: true, root));
        Assert.Null(DesktopSelfTestOptions.ResolveDataDirectory(selfTest: false, root));
        Assert.Null(DesktopSelfTestOptions.ResolveDataDirectory(selfTest: true, " "));
        _ = Assert.Throws<InvalidOperationException>(() => DesktopSelfTestOptions.ResolveDataDirectory(selfTest: true, "relative"));
    }

    [Fact]
    public void LocalizationFallsBackToSharedJsonWhenUnpackagedResourceContextIsUnavailable()
    {
        LocalizationService localization = new();
        localization.Reload("zh-Hans");

        Assert.Equal("Servitium", localization.Get("app.window-title"));
        Assert.Equal("导航", localization.Get("shell.navigation"));
        Assert.Equal("missing.key", localization.Get("missing.key"));
    }

    [Theory]
    [InlineData(639, "compact")]
    [InlineData(640, "medium")]
    [InlineData(1199, "medium")]
    [InlineData(1200, "expanded")]
    public void WindowBreakpointMatchesSharedContract(double width, string expected)
    {
        // DUI-011 / REQ-DSK-050
        Assert.Equal(expected, WindowStateService.Breakpoint(width));
    }

    [Theory]
    [InlineData("servitium://instance/103fbfba-2541-42d4-b2aa-3a2e0f26a011", "PG-012")]
    [InlineData("servitium://node/103fbfba-2541-42d4-b2aa-3a2e0f26a011", "PG-021")]
    [InlineData("servitium://operations/103fbfba-2541-42d4-b2aa-3a2e0f26a011", "PG-044")]
    [InlineData("servitium://channel/103fbfba-2541-42d4-b2aa-3a2e0f26a011", "PG-061")]
    public void DeepLinksAcceptOnlyWhitelistedTargets(string value, string expectedPage)
    {
        // DUI-005
        Assert.True(DeepLinkService.TryParse(value, out DeepLinkTarget? target));
        Assert.NotNull(target);
        Assert.Equal(expectedPage, target.PageId);
        _ = Assert.NotNull(target.ResourceId);
    }

    [Theory]
    [InlineData("https://example.test/instance/id")]
    [InlineData("servitium://unknown/103fbfba-2541-42d4-b2aa-3a2e0f26a011")]
    [InlineData("servitium://instance/not-a-guid")]
    [InlineData("servitium:instance/103fbfba-2541-42d4-b2aa-3a2e0f26a011")]
    public void DeepLinksRejectUntrustedOrMalformedTargets(string value)
    {
        Assert.False(DeepLinkService.TryParse(value, out DeepLinkTarget? target));
        Assert.Null(target);
    }

    [Fact]
    public void NotificationAggregatorMergesCategoryWithinWindow()
    {
        // DUI-006 / REQ-DSK-041
        NotificationAggregator aggregator = new(TimeSpan.FromSeconds(60), 50);
        DateTimeOffset start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        aggregator.Enqueue(Notification("instance-crashed", start));
        aggregator.Enqueue(Notification("instance-crashed", start.AddSeconds(10)));

        Assert.Empty(aggregator.TakeDue(start.AddSeconds(59), false));
        AggregatedDesktopNotification item = Assert.Single(aggregator.TakeDue(start.AddSeconds(71), false));
        Assert.Equal(2, item.Count);
        Assert.Equal(start.AddSeconds(10), item.Latest.OccurredAt);
    }

    [Fact]
    public void NotificationQueueDropsOldestNonDangerousFirst()
    {
        NotificationAggregator aggregator = new(TimeSpan.FromSeconds(60), 2);
        DateTimeOffset start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        aggregator.Enqueue(Notification("danger", start, true));
        aggregator.Enqueue(Notification("informational", start.AddSeconds(1)));
        aggregator.Enqueue(Notification("new", start.AddSeconds(2)));

        IReadOnlyList<AggregatedDesktopNotification> items = aggregator.TakeDue(start.AddMinutes(2), true);
        Assert.Contains(items, item => item.Latest.Category == "danger");
        Assert.Contains(items, item => item.Latest.Category == "new");
        Assert.DoesNotContain(items, item => item.Latest.Category == "informational");
    }

    [Fact]
    public void TrayMenuUsesRecentOrderDeduplicatesAndLimitsToTen()
    {
        var nodeId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        InstanceDto[] instances = [.. Enumerable.Range(1, 12).Select(index => Instance(index, nodeId))];
        Guid[] recent = [instances[4].Id, instances[1].Id, instances[4].Id, .. instances.Select(item => item.Id)];

        IReadOnlyList<TrayInstanceMenuItem> result = TrayMenuModel.Recent(instances, recent);

        Assert.Equal(10, result.Count);
        Assert.Equal(instances[4].Id, result[0].Id);
        Assert.Equal(instances[1].Id, result[1].Id);
        Assert.Equal(10, result.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void NotificationPauseExpiresAtConfiguredUtcInstant()
    {
        DateTimeOffset now = new(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);
        DesktopSettings settings = new([], null, "zh-Hans", "system", true, true, null, [], NotificationsPausedUntil: now.AddMinutes(30));

        Assert.True(DesktopNotificationService.IsPaused(settings, now));
        Assert.False(DesktopNotificationService.IsPaused(settings, now.AddMinutes(30)));
        Assert.False(DesktopNotificationService.IsPaused(settings with { NotificationsPausedUntil = null }, now.AddYears(1)));
        Assert.False(DesktopNotificationService.IsPaused(settings with { NotificationsPaused = false }, now));
    }

    [Fact]
    public void NotificationPauseUntilRestartIsProcessLocal()
    {
        DateTimeOffset now = new(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);
        DesktopSettings settings = new([], null, "zh-Hans", "system", true, false, null, []);
        NotificationPauseState currentProcess = new();

        currentProcess.PauseUntilRestart();

        Assert.True(currentProcess.UntilRestart);
        Assert.True(currentProcess.IsPaused(settings, now));
        Assert.False(new NotificationPauseState().IsPaused(settings, now));
        currentProcess.ClearProcessPause();
        Assert.False(currentProcess.IsPaused(settings, now));
    }

    [Fact]
    public void NotificationTimedPauseModesUseExactDeadlinesAndCancelHasNone()
    {
        DateTimeOffset now = new(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddMinutes(30), NotificationPauseState.PersistedDeadline(NotificationPauseMode.ThirtyMinutes, now));
        Assert.Equal(now.AddHours(1), NotificationPauseState.PersistedDeadline(NotificationPauseMode.OneHour, now));
        Assert.Null(NotificationPauseState.PersistedDeadline(NotificationPauseMode.UntilRestart, now));
        Assert.Null(NotificationPauseState.PersistedDeadline(NotificationPauseMode.Cancel, now));
    }

    [Fact]
    public void NotificationQueueRetainsMessagesWhilePausedAndFlushesAfterResume()
    {
        NotificationAggregator aggregator = new(TimeSpan.FromSeconds(60), 50);
        DateTimeOffset start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        aggregator.Enqueue(Notification("instance-crashed", start));

        Assert.Empty(aggregator.TakeDue(start.AddMinutes(5), force: true, paused: true));
        Assert.Equal(1, aggregator.Count);
        AggregatedDesktopNotification queued = Assert.Single(aggregator.TakeDue(start.AddMinutes(5), force: true, paused: false));
        Assert.Equal("instance-crashed", queued.Latest.Category);
        Assert.Equal(0, aggregator.Count);
    }

    [Fact]
    public async Task PersistedTimedPauseReloadsButLegacyUntilRestartPauseDoesNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Servitium.Desktop.Tests", Guid.NewGuid().ToString("N"));
        DateTimeOffset deadline = new(2026, 8, 3, 1, 30, 0, TimeSpan.Zero);
        try
        {
            using (DesktopSettingsService writer = new(directory))
            {
                await writer.UpdateAsync(
                    current => current with { NotificationsPaused = true, NotificationsPausedUntil = deadline },
                    CancellationToken.None);
            }

            using (DesktopSettingsService timedReader = new(directory))
            {
                await timedReader.LoadAsync(CancellationToken.None);
                Assert.True(timedReader.Current.NotificationsPaused);
                Assert.Equal(deadline, timedReader.Current.NotificationsPausedUntil);
                await timedReader.UpdateAsync(
                    current => current with { NotificationsPaused = true, NotificationsPausedUntil = null },
                    CancellationToken.None);
            }

            using DesktopSettingsService nextProcess = new(directory);
            await nextProcess.LoadAsync(CancellationToken.None);
            Assert.False(nextProcess.Current.NotificationsPaused);
            Assert.Null(nextProcess.Current.NotificationsPausedUntil);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PrismWorkflowActionsBelongToHmclReleaseCreationPage()
    {
        UiPageContract instanceCreation = UiContractCatalog.Get("PG-019");
        UiPageContract releaseCreation = UiContractCatalog.Get("PG-062");

        Assert.DoesNotContain("prism-archive", instanceCreation.Fields);
        Assert.DoesNotContain("preview-prism", instanceCreation.Actions);
        Assert.DoesNotContain("convert-prism", instanceCreation.Actions);
        Assert.Contains("prism-archive", releaseCreation.Fields);
        Assert.Contains("inspect", releaseCreation.Actions);
        Assert.Contains("create-release", releaseCreation.Actions);
        Assert.Contains("create-and-publish", releaseCreation.Actions);
        Assert.Contains("inspection", releaseCreation.Sections);
        Assert.Contains("filter-preview", releaseCreation.Sections);
        Assert.Contains("difference-preview", releaseCreation.Sections);
    }

    [Fact]
    public void NodeRegistrationInstructionsExposeAddressWithoutEmbeddingSecretInCommand()
    {
        DateTimeOffset expiry = new(2026, 8, 4, 12, 30, 0, TimeSpan.Zero);
        var instructions = NodeRegistrationInstructions.Create(
            new Uri("https://control.example.test:5344/base/"),
            "Synthetic Node",
            "one-time-secret",
            expiry);

        Assert.Equal("https://control.example.test:5344/base", instructions.ControlPlaneAddress);
        Assert.Equal("Synthetic Node", instructions.ExpectedNodeName);
        Assert.Equal("one-time-secret", instructions.Token);
        Assert.Equal(expiry, instructions.ExpiresAt);
        Assert.Contains("--server \"https://control.example.test:5344/base\"", instructions.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("--register-token \"<ONE_TIME_TOKEN>\"", instructions.CommandTemplate, StringComparison.Ordinal);
        Assert.Contains("--node-name \"Synthetic Node\"", instructions.CommandTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain(instructions.Token, instructions.CommandTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceCreationContractExposesFiveStepInputsAndSingleSourceActions()
    {
        UiPageContract contract = UiContractCatalog.Get("PG-019");

        Assert.Contains("source-kind", contract.Fields);
        Assert.Contains("tags", contract.Fields);
        Assert.Contains("notes", contract.Fields);
        Assert.Contains("stop-command", contract.Fields);
        Assert.Contains("stop-timeout", contract.Fields);
        Assert.Contains("environment-variables", contract.Fields);
        Assert.Equal(
            ["create-empty", "import-server-archive", "attach-directory"],
            contract.Actions);
        Assert.DoesNotContain("channel", contract.Fields);
    }

    [Fact]
    public void FileClipboardBuildsNormalizedBatchDestinationsWithoutChangingSources()
    {
        FileClipboardPlan plan = new("copy", ["plugins/one.jar", "config/server.properties"]);

        IReadOnlyList<(string Source, string Destination)> destinations = plan.Destinations("backup/2026");

        Assert.Equal(("plugins/one.jar", "backup/2026/one.jar"), destinations[0]);
        Assert.Equal(("config/server.properties", "backup/2026/server.properties"), destinations[1]);
        Assert.Equal("copy", plan.Operation);
    }

    [Fact]
    public async Task GrantSafetyAcknowledgementPersistsAcrossDesktopRestarts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Servitium.Desktop.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (DesktopSettingsService writer = new(directory))
            {
                await writer.UpdateAsync(current => current with { GrantSafetyExplained = true }, CancellationToken.None);
            }

            using DesktopSettingsService reader = new(directory);
            await reader.LoadAsync(CancellationToken.None);
            Assert.True(reader.Current.GrantSafetyExplained);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileAndGrantContractsExposeBatchAndFilterControls()
    {
        UiPageContract files = UiContractCatalog.Get("PG-013");
        UiPageContract grants = UiContractCatalog.Get("PG-031");

        Assert.Contains("directory-tabs", files.Sections);
        Assert.Contains("collision-policy", files.Fields);
        Assert.Contains("cut", files.Actions);
        Assert.Contains("paste", files.Actions);
        Assert.Contains("preview", files.Actions);
        Assert.Contains("query", grants.Fields);
        Assert.Contains("grant-state", grants.Fields);
        Assert.Contains("grant", grants.Actions);
        Assert.Contains("revoke", grants.Actions);
    }

    [Fact]
    public void PageAuthorizationRequiresAdministratorForDangerousSettingsAndExactUserGrant()
    {
        var grantedId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        var otherId = Guid.Parse("10000000-0000-4000-8000-000000000002");
        DateTimeOffset now = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        PrincipalDto user = new(new(Guid.NewGuid(), "USER", "User", "User", false, null, now), [grantedId], "zh-Hans");
        PrincipalDto administrator = new(new(Guid.NewGuid(), "ADMIN", "Admin", "Administrator", false, null, now), [], "zh-Hans");

        Assert.True(DesktopPageAuthorization.CanAccess("instance-grant", user, grantedId));
        Assert.False(DesktopPageAuthorization.CanAccess("instance-grant", user, otherId));
        Assert.False(DesktopPageAuthorization.CanAccess("instance-grant-dangerous-admin", user, grantedId));
        Assert.True(DesktopPageAuthorization.CanAccess("instance-grant-dangerous-admin", administrator, otherId));
        Assert.True(DesktopPageAuthorization.CanAccess("administrator", administrator, null));
        Assert.False(DesktopPageAuthorization.CanAccess("administrator", user, null));
        Assert.False(DesktopPageAuthorization.CanAccess("unknown", administrator, null));
    }

    [Fact]
    public async Task ConcurrentSettingsUpdatesPreserveIndependentChanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Servitium.Desktop.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using DesktopSettingsService settings = new(directory);

            await Task.WhenAll(
                settings.UpdateAsync(current => current with { Language = "en-US" }, CancellationToken.None),
                settings.UpdateAsync(current => current with { Theme = "dark" }, CancellationToken.None));

            Assert.Equal("en-US", settings.Current.Language);
            Assert.Equal("dark", settings.Current.Theme);
            Assert.True(File.Exists(settings.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("interval:300", "interval")]
    [InlineData("specified:2026-08-04T12:30:00Z", "specified")]
    [InlineData("calendar:1,3,5@09:30", "calendar")]
    public void ScheduleEditorAcceptsEverySupportedTrigger(string value, string expectedKind)
    {
        Assert.True(PageActionDispatcher.TryParseScheduleTrigger(value, out ScheduleTriggerDto? trigger));
        Assert.Equal(expectedKind, trigger!.Kind);
    }

    [Theory]
    [InlineData("calendar:@09:30")]
    [InlineData("calendar:1,1@09:30")]
    [InlineData("calendar:7@09:30")]
    [InlineData("calendar:1@24:00")]
    [InlineData("cron:0 0 * * *")]
    public void ScheduleEditorRejectsMalformedTriggers(string value)
    {
        Assert.False(PageActionDispatcher.TryParseScheduleTrigger(value, out ScheduleTriggerDto? trigger));
        Assert.Null(trigger);
    }

    private static DesktopNotification Notification(string category, DateTimeOffset occurredAt, bool dangerous = false)
    {
        return new(category, "title", "body", "PG-044", null, dangerous, occurredAt);
    }

    private static InstanceDto Instance(int index, Guid nodeId)
    {
        return new(
            Guid.Parse($"{index:00000000}-0000-4000-8000-000000000001"),
            nodeId,
            $"Instance {index}",
            index % 2 == 0 ? "Running" : "Stopped",
            $"C:\\Synthetic\\{index}",
            false,
            []);
    }
}
