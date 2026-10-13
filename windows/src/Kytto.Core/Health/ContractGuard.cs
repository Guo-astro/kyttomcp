using System.Text;
using System.Text.Json;

namespace Kytto.Core.Health;

public enum ContractChangeKind
{
    ToolAdded,
    ToolRemoved,
    DescriptionChanged,
    InputSchemaChanged,
    AnnotationsChanged,
}

public enum ContractChangeSeverity { Info, Warning, Breaking }

/// <summary>One model-visible field that changed, as bounded excerpts (§7.10).</summary>
/// <remarks>
/// These explain a review; they do not certify that a server implements the
/// safeguards its schema names. <c>null</c> means "absent", which is not the same as
/// an empty string. Equality is never decided from these texts: they are display
/// only, and may be truncated.
/// </remarks>
public sealed record ContractFieldChange(
    string Path,
    string? Before,
    string? After,
    string? Explanation = null);

/// <param name="Details">
/// Optional on purpose: records written before 1.0.8 have none and must keep loading.
/// </param>
public sealed record ContractChange(
    ContractChangeKind Kind,
    ContractChangeSeverity Severity,
    string ToolName,
    string Summary,
    IReadOnlyList<ContractFieldChange>? Details = null);

/// <summary>Compares model-visible MCP behavior between successful checks (§7.10).</summary>
public static class ContractGuard
{
    public static IReadOnlyList<ContractChange> Changes(
        IReadOnlyList<ToolSummary> oldTools,
        IReadOnlyList<ToolSummary> newTools)
    {
        var before = ByName(oldTools);
        var after = ByName(newTools);
        var beforeNames = before.Keys.ToHashSet(StringComparer.Ordinal);
        var afterNames = after.Keys.ToHashSet(StringComparer.Ordinal);
        var changes = new List<ContractChange>();

        foreach (var name in beforeNames.Except(afterNames).Order(StringComparer.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.ToolRemoved,
                ContractChangeSeverity.Breaking,
                name,
                "Tool removed"));
        }
        foreach (var name in afterNames.Except(beforeNames).Order(StringComparer.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.ToolAdded,
                ContractChangeSeverity.Info,
                name,
                "New tool added"));
        }
        foreach (var name in beforeNames.Intersect(afterNames).Order(StringComparer.Ordinal))
        {
            var left = before[name];
            var right = after[name];
            if (left.Description != right.Description)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.DescriptionChanged,
                    ContractChangeSeverity.Warning,
                    name,
                    "Model-facing description changed",
                    ContractFieldDiff.DescriptionDetails(left.Description, right.Description)));
            }
            if (!SameSchema(left.InputSchemaJSON, right.InputSchemaJSON))
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.InputSchemaChanged,
                    ContractChangeSeverity.Breaking,
                    name,
                    "Input schema changed — review compatibility and safeguards",
                    ContractFieldDiff.SchemaDetails(left.InputSchemaJSON, right.InputSchemaJSON)));
            }
            if (left.Annotations != right.Annotations)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.AnnotationsChanged,
                    ContractChangeSeverity.Warning,
                    name,
                    "Safety annotations changed",
                    ContractFieldDiff.AnnotationDetails(left.Annotations, right.Annotations)));
            }
        }
        return changes;
    }

    /// <remarks>
    /// A server may list one name twice (or several nameless tools, which all become
    /// "(unnamed)"). That is broken input, not a reason to throw; clients resolve it
    /// last-wins, so this does too.
    /// </remarks>
    private static Dictionary<string, ToolSummary> ByName(IEnumerable<ToolSummary> tools)
    {
        var result = new Dictionary<string, ToolSummary>(StringComparer.Ordinal);
        foreach (var tool in tools) result[tool.Name] = tool;
        return result;
    }

    /// <summary>The largest schema text (UTF-8 bytes) that is parsed at all.</summary>
    /// <remarks>
    /// Parsing is bounded independently of rendering. A larger contract is still a
    /// change when its text differs; it is just compared by its raw text and shown as
    /// an excerpt instead of being walked.
    /// </remarks>
    internal const int MaxSchemaBytes = 1_000_000;

    /// <summary>Deepest JSON nesting a schema may have before it is compared as raw text.</summary>
    /// <remarks>
    /// The same ceiling a remote response is held to, so any schema Kytto accepted
    /// from a server can also be compared.
    /// </remarks>
    internal const int MaxSchemaNesting = 128;

    /// <summary>Whether two input schemas say the same thing.</summary>
    /// <remarks>
    /// The stored text is the raw slice the server sent, so a server that only
    /// re-serializes its schema — keys in another order, other whitespace — would
    /// otherwise read as a breaking change, and false alarms teach people to
    /// acknowledge without reading. Object keys are sorted; array order is kept,
    /// because <c>enum</c>, <c>oneOf</c> or <c>prefixItems</c> can mean something else
    /// reordered. If either side does not parse — or is too large or too deep to be
    /// compared within bounds — the raw text decides, so a change is never hidden by
    /// the canonicalization.
    /// </remarks>
    internal static bool SameSchema(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        if (left is null || right is null) return false;
        return Canonical(left) is { } canonicalLeft &&
               Canonical(right) is { } canonicalRight &&
               string.Equals(canonicalLeft, canonicalRight, StringComparison.Ordinal);
    }

    private static string? Canonical(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxSchemaBytes) return null;
        try
        {
            using var document = ContractFieldDiff.Parse(json);
            var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                Write(document.RootElement, writer);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException or ArgumentException)
        {
            // A lone surrogate escape parses but cannot be decoded; that is a schema
            // that cannot be compared, not a reason to fail the whole check.
            return null;
        }
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                             property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                // Re-encoded from the decoded value, so an escaped letter and the
                // letter itself agree.
                writer.WriteStringValue(element.GetString());
                break;
            default:
                // Numbers, booleans and null keep their literal, so `true` never
                // equals `1`.
                element.WriteTo(writer);
                break;
        }
    }
}
