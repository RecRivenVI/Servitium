namespace Servitium.Desktop.ViewModels;

internal sealed record NavigationDestination(string PageId, string Title)
{
    public override string ToString() { return Title; }
}
