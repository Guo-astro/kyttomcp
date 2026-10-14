using Kytto.Core.Clients;
using Kytto.Core.Portable;

namespace Kytto.App.Ipc;

/// <summary>
/// What crosses the boundary for portable setup (§7.13).
/// </summary>
/// <remarks>
/// Names, counts, display strings and an opaque id — never a path the web layer has
/// to interpret, never a value anyone typed, never a command, argument or URL from
/// the file. The values live in a native dialog and in a native plan, and the page
/// holds only the id of that plan.
/// </remarks>
internal sealed record PortableExportDto(
    bool Cancelled,
    int ServerCount,
    int ProfileCount,
    IReadOnlyList<string> Warnings)
{
    internal static PortableExportDto CancelledExport { get; } = new(true, 0, 0, []);
}

/// <param name="Enabled">
/// Always false from the application: an import arrives switched off, and nothing
/// in the command set can ask otherwise.
/// </param>
internal sealed record PortableImportPreviewDto(
    string Id,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ProfileNames,
    IReadOnlyList<string> TargetClients,
    IReadOnlyList<string> PathDisplays,
    int PendingContractCount,
    IReadOnlyList<string> Warnings,
    bool Enabled);

/// <summary>An imported fingerprint awaiting a check made here, and a review.</summary>
/// <param name="Matching">
/// Null until a successful local check that finished after the import. After that,
/// whether it agrees with the imported fingerprints — evidence, never trust.
/// </param>
internal sealed record PendingContractImportDto(string ServerID, int ToolCount, bool? Matching);

/// <param name="Error">
/// Safe text when the store could not be read. It names nothing from the file, and
/// the store is left exactly as it was.
/// </param>
internal sealed record PendingContractImportsDto(
    IReadOnlyList<PendingContractImportDto> Entries,
    string? Error);

internal sealed record PortableApplyPayload(string PreviewID);

internal static class PortableDtoMapping
{
    internal static PortableImportPreviewDto ToDto(this PortableImportPreview preview) => new(
        Id: preview.Id,
        ServerNames: preview.ServerNames,
        ProfileNames: preview.ProfileNames,
        TargetClients: preview.TargetClients.Select(client => client.Raw()).ToArray(),
        PathDisplays: preview.PathDisplays,
        PendingContractCount: preview.PendingContractCount,
        Warnings: preview.Warnings,
        Enabled: preview.Enabled);

    internal static PendingContractImportsDto ToPendingContractImportsDto(this AppModel model)
    {
        var (entries, error) = model.PendingContractImports();
        return new PendingContractImportsDto(
            entries.Select(entry => new PendingContractImportDto(
                entry.ServerID, entry.ToolCount, entry.Matching)).ToArray(),
            error);
    }
}
