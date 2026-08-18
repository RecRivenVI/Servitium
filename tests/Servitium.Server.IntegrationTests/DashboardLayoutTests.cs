using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Identity;
using Servitium.Contracts.Settings;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;
using Servitium.Server.Endpoints;
using Servitium.Server.Governance;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class DashboardLayoutTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1170")]
    public async Task 布局按用户持久化_缺失默认卡片自动补齐且重置生效()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-layout-{Guid.NewGuid():N}")).FullName;
        SqliteWriteCoordinator coordinator = new();
        try
        {
            DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            ControlPlaneStore store = new(factory, coordinator, new SystemClock(), new GuidFactory());
            DashboardLayoutService layouts = CreateLayouts(store);
            var firstUser = Guid.NewGuid();
            var secondUser = Guid.NewGuid();

            DashboardLayoutDto defaults = await layouts.GetAsync(firstUser, "dashboard", CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(9, defaults.Cards.Count);
            Assert.All(
                defaults.Cards.Where(card => card.Id is "node-status" or "instance-status" or "session-status" or "system-status" or "system-info"),
                card => Assert.True(card.Required));

            DashboardCardLayoutDto custom = new(
                "welcome",
                "text",
                "Synthetic title",
                "Synthetic description",
                4,
                "small",
                false,
                false,
                new Dictionary<string, string?> { ["text"] = "Synthetic content" });
            DashboardLayoutDto saved = await layouts.SaveAsync(
                firstUser,
                "dashboard",
                new UpdateDashboardLayoutRequest(0, [custom, defaults.Cards[0] with { Width = 8, Hidden = true }]),
                CancellationToken.None).ConfigureAwait(true);

            Assert.Equal("welcome", saved.Cards[0].Id);
            Assert.Equal(10, saved.Cards.Count);
            Assert.Equal(8, saved.Cards.Single(card => card.Id == "node-status").Width);
            Assert.True(saved.Cards.Single(card => card.Id == "node-status").Hidden);

            DashboardLayoutDto reopened = await layouts.GetAsync(firstUser, "dashboard", CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(saved.Page, reopened.Page);
            Assert.Equal(saved.Version, reopened.Version);
            Assert.Equal(JsonSerializer.Serialize(saved.Cards), JsonSerializer.Serialize(reopened.Cards));
            DashboardLayoutDto isolated = await layouts.GetAsync(secondUser, "dashboard", CancellationToken.None).ConfigureAwait(true);
            Assert.DoesNotContain(isolated.Cards, card => card.Id == "welcome");

            DashboardLayoutDto reset = await layouts.ResetAsync(firstUser, "dashboard", CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(defaults, reset);
            Assert.Equal(defaults, await layouts.GetAsync(firstUser, "dashboard", CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            coordinator.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1170")]
    public async Task 布局拒绝重复标识_未知业务卡片和越界尺寸()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-layout-negative-{Guid.NewGuid():N}")).FullName;
        SqliteWriteCoordinator coordinator = new();
        try
        {
            DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            ControlPlaneStore store = new(factory, coordinator, new SystemClock(), new GuidFactory());
            (DashboardLayoutService layouts, RuntimeSettings runtimeSettings) = CreateLayoutsWithRuntime(store);
            DashboardCardLayoutDto valid = new("custom", "text", null, null, 4, "small", false, false, new Dictionary<string, string?>());

            DomainException duplicate = await Assert.ThrowsAsync<DomainException>(() => layouts.SaveAsync(
                Guid.NewGuid(), "dashboard", new UpdateDashboardLayoutRequest(1, [valid, valid]), CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", duplicate.Code);

            DomainException unknown = await Assert.ThrowsAsync<DomainException>(() => layouts.SaveAsync(
                Guid.NewGuid(), "dashboard", new UpdateDashboardLayoutRequest(1, [valid with { Kind = "terminal" }]), CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", unknown.Code);

            DomainException width = await Assert.ThrowsAsync<DomainException>(() => layouts.SaveAsync(
                Guid.NewGuid(), "dashboard", new UpdateDashboardLayoutRequest(1, [valid with { Width = 13 }]), CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", width.Code);

            DashboardCardLayoutDto embed = valid with
            {
                Id = "embed",
                Kind = "embed",
                Settings = new Dictionary<string, string?> { ["value"] = "https://metrics.example.invalid/dashboard" },
            };
            DomainException disallowedEmbed = await Assert.ThrowsAsync<DomainException>(() => layouts.SaveAsync(
                Guid.NewGuid(), "dashboard", new UpdateDashboardLayoutRequest(1, [embed]), CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", disallowedEmbed.Code);

            runtimeSettings.Apply(
                "security",
                new Dictionary<string, string?> { ["embedAllowedOrigins"] = "https://metrics.example.invalid" });
            DashboardLayoutDto accepted = await layouts.SaveAsync(
                Guid.NewGuid(), "dashboard", new UpdateDashboardLayoutRequest(1, [embed]), CancellationToken.None).ConfigureAwait(true);
            Assert.Contains(accepted.Cards, card => card.Id == "embed");

            DomainException insecureEmbed = await Assert.ThrowsAsync<DomainException>(() => layouts.SaveAsync(
                Guid.NewGuid(),
                "dashboard",
                new UpdateDashboardLayoutRequest(1, [embed with
                {
                    Settings = new Dictionary<string, string?> { ["value"] = "http://metrics.example.invalid/dashboard" },
                }]),
                CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", insecureEmbed.Code);
        }
        finally
        {
            coordinator.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DashboardLayoutService CreateLayouts(ControlPlaneStore store)
    {
        return CreateLayoutsWithRuntime(store).Layouts;
    }

    private static (DashboardLayoutService Layouts, RuntimeSettings RuntimeSettings) CreateLayoutsWithRuntime(ControlPlaneStore store)
    {
        RuntimeSettings runtimeSettings = new(
            store,
            new IdentityServiceOptions(TimeSpan.FromHours(12), TimeSpan.FromDays(7), apiKeysEnabled: true),
            new TotpOptions());
        return (new DashboardLayoutService(store, runtimeSettings), runtimeSettings);
    }

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new(options);
        }
    }
}
