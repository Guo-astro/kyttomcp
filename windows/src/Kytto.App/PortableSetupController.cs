using System.IO;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Portable;
using Kytto.Core.Profiles;

namespace Kytto.App;

/// <summary>What the user chose, and typed, in the import window.</summary>
/// <remarks>
/// Native memory only. It is handed to the model and dropped; it is never serialized,
/// logged, or given a way to reach the web layer (§7.13).
/// </remarks>
internal sealed record PortableImportSelection(
    IReadOnlyList<ClientId> Targets,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The questions portable setup asks the person at the keyboard. Native dialogs in the
/// application; fakes in tests, which is why the flow around them can be exercised
/// without a window.
/// </summary>
internal interface IPortableSetupPrompts
{
    /// <summary>Null when cancelled; otherwise whether reviewed fingerprints are included.</summary>
    bool? ConfirmExport();

    string? ChooseExportFile();

    string? ChooseImportFile();

    /// <summary>
    /// Shows the requirement inputs and destinations. <paramref name="submit"/> returns
    /// an error to show beside what was typed, or null when the preview was made. The
    /// window stays open on an error, with every field as it was.
    /// </summary>
    bool CollectInputs(
        PortableSetupSummary summary,
        IReadOnlyList<(ClientId Id, string DisplayName)> targets,
        Func<PortableImportSelection, string?> submit);
}

/// <summary>
/// File paths and replacement values never enter the web process. It receives only an
/// opaque, digest-guarded preview after this native form is accepted (§7.13).
/// </summary>
internal static class PortableSetupController
{
    /// <summary>Replaced in tests; the application always uses the native dialogs.</summary>
    internal static IPortableSetupPrompts Prompts { get; set; } = new WpfPortableSetupPrompts();

    internal static PortableExportDto Export(AppModel model)
    {
        if (Prompts.ConfirmExport() is not { } includeReviewedContracts) return PortableExportDto.CancelledExport;
        if (Prompts.ChooseExportFile() is not { } file) return PortableExportDto.CancelledExport;

        var summary = model.ExportPortableSetup(file, includeReviewedContracts);
        return new PortableExportDto(
            Cancelled: false,
            ServerCount: summary.ServerNames.Count,
            ProfileCount: summary.ProfileNames.Count,
            Warnings: summary.Warnings);
    }

    /// <summary>
    /// Picks the file, shows its requirements natively, and returns the preview — or
    /// null when the person cancelled. A refused input keeps the window and its values.
    /// </summary>
    internal static PortableImportPreviewDto? Preview(AppModel model)
    {
        if (Prompts.ChooseImportFile() is not { } file) return null;

        var summary = model.InspectPortableSetup(file);
        var targets = model.PortableTargets();
        if (summary.ServerNames.Count > 0 && targets.Count == 0)
        {
            throw PortableSetupException.NoDestination();
        }

        PortableImportPreview? made = null;
        var accepted = Prompts.CollectInputs(summary, targets, selection =>
        {
            try
            {
                made = model.PreviewPortableSetup(file, selection.Targets, selection.Values);
                return null;
            }
            catch (Exception error) when (
                error is PortableSetupException or ConfigWriteException or AuthoringException or
                         ProfileException or IOException or UnauthorizedAccessException)
            {
                // The message names the requirement or the destination and never what
                // was typed, so it is safe to show beside the field it is about.
                return error.Message;
            }
        });
        if (accepted && made is not null) return made.ToDto();

        // Cancelled after a plan was made: nothing may keep the typed values alive.
        if (made is not null) model.DiscardPortableSetup(made.Id);
        return null;
    }
}
