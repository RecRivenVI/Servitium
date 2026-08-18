using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Servitium.Desktop.Models;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json constructs this UI contract DTO at runtime.")]
internal sealed record UiContractDocument(int Version, IReadOnlyList<UiPageContract> Pages);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json constructs this UI contract DTO at runtime.")]
internal sealed record UiPageContract(
    string Id,
    string Route,
    string Permission,
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> States,
    IReadOnlyList<string> TextKeys,
    bool PlatformSpecific);

internal static class UiContractCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<Dictionary<string, UiPageContract>> Pages = new(Load);

    internal static UiPageContract Get(string pageId)
    {
        return Pages.Value.TryGetValue(pageId, out UiPageContract? page)
            ? page
            : throw new InvalidDataException("The UI contract does not contain page " + pageId + ".");
    }

    internal static IReadOnlyCollection<UiPageContract> All => [.. Pages.Value.Values];

    private static Dictionary<string, UiPageContract> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Ui", "ui-contract.json");
        UiContractDocument document = JsonSerializer.Deserialize<UiContractDocument>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The UI contract is invalid.");
        Dictionary<string, UiPageContract> result = new(StringComparer.Ordinal);
        foreach (UiPageContract page in document.Pages)
        {
            result.Add(page.Id, page);
        }

        return result;
    }
}
