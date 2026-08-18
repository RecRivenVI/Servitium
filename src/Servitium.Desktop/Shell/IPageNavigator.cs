namespace Servitium.Desktop.Shell;

internal interface IPageNavigator
{
    void Navigate(string pageId, bool addToBackStack = true);
}

internal interface INavigationGuard
{
    System.Threading.Tasks.Task<bool> CanNavigateAwayAsync();
}
