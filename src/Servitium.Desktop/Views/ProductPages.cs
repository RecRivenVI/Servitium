using Microsoft.UI.Xaml.Controls;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;
using Servitium.Desktop.Views.Authentication;
using Servitium.Desktop.Views.Instances;
using Servitium.Desktop.Views.Settings;

namespace Servitium.Desktop.Views;

internal static class ProductPageFactory
{
    internal static Page Create(string pageId, AppServices services, IPageNavigator navigator)
    {
        return pageId switch
        {
            "PG-001" => new SetupPage(services, navigator),
            "PG-002" => new LoginPage(services, navigator),
            "PG-003" => new TotpPage(services, navigator),
            "PG-010" or "PG-011" => new InstanceWorkspacePage(services, navigator),
            "PG-012" or "PG-013" or "PG-014" or "PG-015" or "PG-016" or "PG-017" or "PG-018" => new InstanceWorkspacePage(services, navigator, pageId == "PG-015" ? "PG-014" : pageId),
            "PG-019" => new InstanceWorkspacePage(services, navigator, showCreation: true),
            "PG-020" => new NodesPage(services, navigator),
            "PG-021" => new NodeDetailsPage(services, navigator),
            "PG-022" => new JavaRuntimesPage(services, navigator),
            "PG-030" => new UsersPage(services, navigator),
            "PG-031" => new UserGrantsPage(services, navigator),
            "PG-032" => new SettingsPage(services, navigator, account: true),
            "PG-040" => new DashboardPage(services, navigator),
            "PG-041" => new SettingsPage(services, navigator),
            "PG-042" => new ActivityLogPage(services, navigator),
            "PG-043" => new SecurityAuditPage(services, navigator),
            "PG-044" => new OperationsPage(services, navigator),
            "PG-050" => new PublicPage(services, navigator),
            "PG-051" => new NotFoundPage(services, navigator),
            "PG-060" => new DistributionPage(services, navigator),
            "PG-061" => new ChannelPage(services, navigator),
            "PG-062" => new ReleaseCreationPage(services, navigator),
            "PG-063" => new ReleaseDetailsPage(services, navigator),
            _ => new NotFoundPage(services, navigator),
        };
    }

    internal static Microsoft.UI.Xaml.FrameworkElement CreateInstanceTab(string pageId, AppServices services, IPageNavigator navigator)
    {
        return pageId switch
        {
            "PG-012" => new InstanceConsolePage(services, navigator),
            "PG-013" => new InstanceFilesPage(services),
            "PG-014" => new InstanceConfigurationPage(services, navigator),
            "PG-016" => new InstanceModsPage(services, navigator),
            "PG-017" => new InstanceSchedulesPage(services),
            "PG-018" => new InstanceSettingsPage(services, navigator),
            _ => throw new System.ArgumentOutOfRangeException(nameof(pageId)),
        };
    }
}

internal sealed partial class NodesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-020", services, navigator);
internal sealed partial class NodeDetailsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-021", services, navigator);
internal sealed partial class JavaRuntimesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-022", services, navigator);
internal sealed partial class UsersPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-030", services, navigator);
internal sealed partial class UserGrantsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-031", services, navigator);
internal sealed partial class DashboardPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-040", services, navigator);
internal sealed partial class ActivityLogPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-042", services, navigator);
internal sealed partial class SecurityAuditPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-043", services, navigator);
internal sealed partial class OperationsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-044", services, navigator);
internal sealed partial class PublicPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-050", services, navigator);
internal sealed partial class NotFoundPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-051", services, navigator);
internal sealed partial class DistributionPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-060", services, navigator);
internal sealed partial class ChannelPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-061", services, navigator);
internal sealed partial class ReleaseCreationPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-062", services, navigator);
internal sealed partial class ReleaseDetailsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-063", services, navigator);
