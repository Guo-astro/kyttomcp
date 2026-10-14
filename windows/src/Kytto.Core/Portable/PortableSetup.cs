using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.Core.Portable;

/// <summary>
/// The portable format is a whitelist, never a copy of a client's raw config (§7.13).
/// Values Kytto cannot prove safe to share become native-only required inputs.
/// </summary>
/// <remarks>
/// Identical in shape to the file the macOS app writes and reads, so a setup moves
/// between the two in either direction. The compatibility fixtures under
/// <c>tests/Kytto.Core.Tests/Fixtures/compatibility</c> are what keep that true.
/// </remarks>
public sealed record PortableSetup(
    string Format,
    int Version,
    IReadOnlyList<PortableServer> Servers,
    IReadOnlyList<PortableProfile> Profiles,
    IReadOnlyList<PortableRequirement> Requirements,
    IReadOnlyList<PortableContractReference> ReviewedContracts)
{
    public const string FormatName = "kytto-portable-setup";
    public const int CurrentVersion = 1;
}

/// <summary>Exactly one of a public literal and a native input, never both.</summary>
public sealed record PortableValue(string? Literal, string? RequirementID)
{
    internal static PortableValue FromLiteral(string value) => new(value, null);

    internal static PortableValue Required(string id) => new(null, id);
}

public sealed record PortableServer(
    string Name,
    ClientId SourceClient,
    bool WasEnabled,
    Transport Transport,
    PortableValue? Command,
    IReadOnlyList<PortableValue> Args,
    IReadOnlyDictionary<string, PortableValue> Environment,
    IReadOnlyDictionary<string, PortableValue> Headers,
    PortableValue? Url,
    PortableValue? WorkingDirectory);

public sealed record PortableProfile(string Name, IReadOnlyList<string> ServerIDs, int? TokenBudget);

public enum PortableRequirementKind
{
    Secret,
    Path,
    Value,
}

public sealed record PortableRequirement(string Id, string Label, PortableRequirementKind Kind)
{
    /// <summary>Everything but a path is typed into a masked field and never echoed.</summary>
    public bool IsSecret => Kind != PortableRequirementKind.Path;

    internal string KindRaw => Kind switch
    {
        PortableRequirementKind.Secret => "secret",
        PortableRequirementKind.Path => "path",
        _ => "value",
    };
}

/// <summary>
/// Hashes preserve a comparison reference without exporting descriptions, schema
/// defaults or other model-facing text that can contain credentials (§7.10).
/// A reference imported from a file is <em>pending</em>, never a trusted baseline.
/// </summary>
public sealed record PortableContractReference(
    string ServerID,
    IReadOnlyDictionary<string, string> ToolDigests);

/// <summary>
/// Imported fingerprints are review evidence, not local trust. A match exists only
/// after an actual, currently successful check on this machine.
/// </summary>
public sealed record PortablePendingContractSummary(string ServerID, int ToolCount, bool? Matching);

/// <summary>Safe to show in the web layer. No command, argument, URL or secret values.</summary>
public sealed record PortableSetupSummary(
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ProfileNames,
    IReadOnlyList<PortableRequirement> Requirements,
    int PendingContractCount,
    IReadOnlyList<string> Warnings);

public sealed record PortableImportPreview(
    string Id,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ProfileNames,
    IReadOnlyList<ClientId> TargetClients,
    IReadOnlyList<string> PathDisplays,
    int PendingContractCount,
    IReadOnlyList<string> Warnings,
    bool Enabled);

public sealed record PortableImportResult(
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ImportedProfiles,
    IReadOnlyList<ClientId> ChangedClients,
    IReadOnlyList<string> BackupIDs,
    int PendingContractCount);

public enum PortableSetupFailure
{
    Invalid,
    MissingInput,
    Conflict,
    StalePreview,
    RollbackFailed,
    PendingContractsUnreadable,
    LocalContractNotChecked,
    StoreUnreadable,
}

/// <summary>
/// Why a setup could not be exported, previewed, imported or reviewed. The message
/// never carries a value the user typed or a definition's contents — only names the
/// user already sees, so it can be shown anywhere (§6).
/// </summary>
public sealed class PortableSetupException(
    PortableSetupFailure failure,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public PortableSetupFailure Failure { get; } = failure;

    public static PortableSetupException Invalid(string detail) =>
        new(PortableSetupFailure.Invalid, $"This is not a supported portable setup: {detail}");

    public static PortableSetupException MissingInput(string label) =>
        new(PortableSetupFailure.MissingInput, $"Provide {label} in the native import dialog.");

    public static PortableSetupException Conflict(string name) =>
        new(PortableSetupFailure.Conflict, $"{name} already exists. The import left it untouched.");

    public static PortableSetupException StalePreview() =>
        new(
            PortableSetupFailure.StalePreview,
            "The setup or destination changed after the preview. Preview the import again.");

    public static PortableSetupException RollbackFailed(string detail, Exception? cause = null) =>
        new(
            PortableSetupFailure.RollbackFailed,
            $"The import failed and could not completely restore its previous files: {detail}",
            cause);

    /// <summary>One of Kytto's own stores could not be read, so nothing was changed.</summary>
    public static PortableSetupException StoreUnreadable(string store, Exception? cause = null) =>
        new(
            PortableSetupFailure.StoreUnreadable,
            $"Kytto could not read its own {store}, so it left everything untouched.",
            cause);

    public static PortableSetupException PendingContractsUnreadable() =>
        new(
            PortableSetupFailure.PendingContractsUnreadable,
            "Kytto could not read its pending imported contract references, so it left them untouched.");

    public static PortableSetupException LocalContractNotChecked() =>
        new(
            PortableSetupFailure.LocalContractNotChecked,
            "Run a successful local check before reviewing this imported contract reference.");
}
