using System.IO;

using Servitium.Contracts.Files;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed class FileRow(FileEntryDto value, LocalizationService localization)
{
    public FileEntryDto Value { get; } = value;
    public string Name => Path.GetFileName(Value.Path.Replace('\\', '/'));
    public string Kind => Value.IsDirectory ? localization.Get("common.directory") : Path.GetExtension(Name) is { Length: > 0 } extension ? extension : localization.Get("desktop.file-type-file");
    public string Size => Value.IsDirectory ? string.Empty : localization.Format("desktop.file-size", Value.Size);
    public string Modified => Value.ModifiedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public string Glyph => Value.IsDirectory ? "\uE8B7" : "\uE8A5";
    public override string ToString() { return Name; }
}
