using Microsoft.UI.Xaml.Controls;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;

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
            "PG-010" => new InstanceListPage(services, navigator),
            "PG-011" => new MyInstancesPage(services, navigator),
            "PG-012" => new InstanceConsolePage(services, navigator),
            "PG-013" => new FileManagerPage(services, navigator),
            "PG-014" => new ConfigurationListPage(services, navigator),
            "PG-015" => new ConfigurationEditorPage(services, navigator),
            "PG-016" => new ModsPage(services, navigator),
            "PG-017" => new SchedulesPage(services, navigator),
            "PG-018" => new InstanceSettingsPage(services, navigator),
            "PG-019" => new InstanceCreationPage(services, navigator),
            "PG-020" => new NodesPage(services, navigator),
            "PG-021" => new NodeDetailsPage(services, navigator),
            "PG-022" => new JavaRuntimesPage(services, navigator),
            "PG-030" => new UsersPage(services, navigator),
            "PG-031" => new UserGrantsPage(services, navigator),
            "PG-032" => new AccountPage(services, navigator),
            "PG-040" => new DashboardPage(services, navigator),
            "PG-041" => new SystemSettingsPage(services, navigator),
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
}

internal sealed partial class SetupPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-001", services, navigator);
internal sealed partial class LoginPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-002", services, navigator);
internal sealed partial class TotpPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-003", services, navigator);
internal sealed partial class InstanceListPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-010", services, navigator);
internal sealed partial class MyInstancesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-011", services, navigator);
internal sealed partial class InstanceConsolePage(AppServices services, IPageNavigator navigator) : ContractPage("PG-012", services, navigator);
internal sealed partial class FileManagerPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-013", services, navigator);
internal sealed partial class ConfigurationListPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-014", services, navigator);
internal sealed partial class ConfigurationEditorPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-015", services, navigator);
internal sealed partial class ModsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-016", services, navigator);
internal sealed partial class SchedulesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-017", services, navigator);
internal sealed partial class InstanceSettingsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-018", services, navigator);
internal sealed partial class InstanceCreationPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-019", services, navigator);
internal sealed partial class NodesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-020", services, navigator);
internal sealed partial class NodeDetailsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-021", services, navigator);
internal sealed partial class JavaRuntimesPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-022", services, navigator);
internal sealed partial class UsersPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-030", services, navigator);
internal sealed partial class UserGrantsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-031", services, navigator);
internal sealed partial class AccountPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-032", services, navigator);
internal sealed partial class DashboardPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-040", services, navigator);
internal sealed partial class SystemSettingsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-041", services, navigator);
internal sealed partial class ActivityLogPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-042", services, navigator);
internal sealed partial class SecurityAuditPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-043", services, navigator);
internal sealed partial class OperationsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-044", services, navigator);
internal sealed partial class PublicPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-050", services, navigator);
internal sealed partial class NotFoundPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-051", services, navigator);
internal sealed partial class DistributionPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-060", services, navigator);
internal sealed partial class ChannelPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-061", services, navigator);
internal sealed partial class ReleaseCreationPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-062", services, navigator);
internal sealed partial class ReleaseDetailsPage(AppServices services, IPageNavigator navigator) : ContractPage("PG-063", services, navigator);
