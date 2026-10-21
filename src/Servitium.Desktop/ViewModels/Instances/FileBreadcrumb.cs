namespace Servitium.Desktop.ViewModels.Instances;

internal sealed record FileBreadcrumb(string Name, string Path)
{
    public override string ToString() { return Name; }
}
