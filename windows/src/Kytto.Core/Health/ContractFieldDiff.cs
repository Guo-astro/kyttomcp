using System.Text;
using System.Text.Json;

namespace Kytto.Core.Health;

/// <summary>
/// Field-level before/after for a changed contract (§7.10), under hard bounds.
/// </summary>
/// <remarks>
/// <para>
/// Every limit here exists because the compared text comes from a server Kytto does
/// not trust: at most 40 reported fields plus one omission notice, 500 differing
/// nodes visited, 12 levels of descent, excerpts of 800 Unicode <em>scalars</em>
/// (not UTF-16 units, which a surrogate pair spends two of, and not grapheme
/// clusters, which a run of combining marks makes unbounded), and no parse of a
/// schema above 1,000,000 UTF-8 bytes.
/// </para>
/// <para>
/// Equality is decided on the parsed values, never on the display text: two
/// different contracts whose first 800 characters agree are still different, and the
/// entry says so rather than showing two identical excerpts without comment.
/// </para>
/// </remarks>
internal static class ContractFieldDiff
{
    internal const int MaxDetails = 40;
    internal const int MaxVisitedNodes = 500;
    internal const int MaxDepth = 12;
    internal const int MaxExcerptScalars = 800;

    private const int MaxKeyBytes = 256;
    private const int MaxPathBytes = 1_024;
    private const string TruncationMarker = "… [truncated]";

    private static readonly string[] GuardWords = ["dry_run", "dryrun", "confirm", "approval"];

    private const string GuardExplanation =
        "A confirmation or dry-run field changed. This may alter how the model requests a " +
        "guarded action; verify the server's actual enforcement.";

    // MARK: - Descriptions and annotations

    internal static IReadOnlyList<ContractFieldChange> DescriptionDetails(string? before, string? after) =>
    [
        new ContractFieldChange(
            "description",
            Excerpt(before),
            Excerpt(after),
            HiddenBeyondExcerpt(before, after)),
    ];

    internal static IReadOnlyList<ContractFieldChange> AnnotationDetails(
        ToolAnnotations? before,
        ToolAnnotations? after)
    {
        var fields = new (string Name, bool? Before, bool? After)[]
        {
            ("readOnlyHint", before?.ReadOnlyHint, after?.ReadOnlyHint),
            ("destructiveHint", before?.DestructiveHint, after?.DestructiveHint),
            ("idempotentHint", before?.IdempotentHint, after?.IdempotentHint),
            ("openWorldHint", before?.OpenWorldHint, after?.OpenWorldHint),
        };
        var result = fields
            .Where(field => field.Before != field.After)
            .Select(field => new ContractFieldChange(
                "annotations." + field.Name,
                field.Before?.ToString().ToLowerInvariant(),
                field.After?.ToString().ToLowerInvariant(),
                "An annotation is a hint to the client, not an enforced permission."))
            .ToList();
        if (result.Count == 0)
        {
            // Present on one side only, with nothing set: still a difference worth
            // one line, never an empty list.
            result.Add(new ContractFieldChange(
                "annotations",
                before is null ? null : "{}",
                after is null ? null : "{}"));
        }
        return result;
    }

    // MARK: - Input schema

    internal static IReadOnlyList<ContractFieldChange> SchemaDetails(string? before, string? after)
    {
        try
        {
            return WalkSchemas(before, after);
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException or ArgumentException)
        {
            return RawSchema(before, after, "Inspect the original schema; a structured comparison is unavailable.");
        }
    }

    private static IReadOnlyList<ContractFieldChange> WalkSchemas(string? before, string? after)
    {
        if (before is null || after is null ||
            Encoding.UTF8.GetByteCount(before) > ContractGuard.MaxSchemaBytes ||
            Encoding.UTF8.GetByteCount(after) > ContractGuard.MaxSchemaBytes)
        {
            return RawSchema(before, after, "Inspect the original schema; a structured comparison is unavailable.");
        }

        using var left = Parse(before);
        using var right = Parse(after);

        var walk = new SchemaWalk();
        walk.Visit(left.RootElement, right.RootElement, "inputSchema", 0);
        if (walk.Omitted)
        {
            walk.Result.Add(new ContractFieldChange(
                "inputSchema",
                null,
                null,
                "Additional changes omitted. Inspect the complete schema before acknowledging."));
        }
        if (walk.Result.Count == 0)
        {
            walk.Result.Add(new ContractFieldChange("inputSchema", Excerpt(before), Excerpt(after)));
        }
        return walk.Result;
    }

    private static IReadOnlyList<ContractFieldChange> RawSchema(string? before, string? after, string explanation) =>
    [
        new ContractFieldChange("inputSchema", Excerpt(before), Excerpt(after), explanation),
    ];

    /// <summary>The one parser every comparison uses, so "parses" means the same everywhere.</summary>
    internal static JsonDocument Parse(string json) => JsonDocument.Parse(
        json,
        new JsonDocumentOptions { MaxDepth = ContractGuard.MaxSchemaNesting });

    private sealed class SchemaWalk
    {
        internal List<ContractFieldChange> Result { get; } = [];
        internal bool Omitted { get; private set; }
        private int _visited;

        internal void Visit(JsonElement? left, JsonElement? right, string path, int depth)
        {
            // Equal subtrees cost nothing against the budgets: a schema with three
            // hundred unchanged properties and one changed one must not read as
            // "too much to show".
            if (SameValue(left, right)) return;

            if (Result.Count >= MaxDetails || ++_visited > MaxVisitedNodes)
            {
                Omitted = true;
                return;
            }

            if (depth < MaxDepth &&
                left is { ValueKind: JsonValueKind.Object } leftObject &&
                right is { ValueKind: JsonValueKind.Object } rightObject &&
                !HasDuplicateNames(leftObject) && !HasDuplicateNames(rightObject))
            {
                var keys = new SortedSet<string>(ScalarOrder.Instance);
                // Looked up by name once per object, not scanned per key: a schema is
                // server input, and a wide object of N properties would otherwise cost N
                // squared string allocations on the thread that records the check.
                var leftMembers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var rightMembers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in leftObject.EnumerateObject())
                {
                    keys.Add(property.Name);
                    leftMembers[property.Name] = property.Value;
                }
                foreach (var property in rightObject.EnumerateObject())
                {
                    keys.Add(property.Name);
                    rightMembers[property.Name] = property.Value;
                }

                foreach (var key in keys)
                {
                    var leftValue = Member(leftMembers, key);
                    var rightValue = Member(rightMembers, key);
                    // Unchanged members are skipped before anything about them is
                    // measured, so a long key that did not change never shows up.
                    if (SameValue(leftValue, rightValue)) continue;

                    // JSON Pointer escaping makes names containing '/' or '~'
                    // unambiguous. Arrays stay whole so their order is visible.
                    var escaped = key.Replace("~", "~0", StringComparison.Ordinal)
                        .Replace("/", "~1", StringComparison.Ordinal);
                    if (Encoding.UTF8.GetByteCount(escaped) > MaxKeyBytes ||
                        Encoding.UTF8.GetByteCount(path) + 1 + Encoding.UTF8.GetByteCount(escaped) > MaxPathBytes)
                    {
                        if (Result.Count >= MaxDetails)
                        {
                            Omitted = true;
                            break;
                        }
                        Result.Add(new ContractFieldChange(
                            "inputSchema/[long field path]",
                            Excerpt(Show(leftValue)),
                            Excerpt(Show(rightValue)),
                            "The schema field name or path is too long to display. Inspect the complete schema."));
                        continue;
                    }
                    Visit(leftValue, rightValue, path + "/" + escaped, depth + 1);
                }
                return;
            }

            var beforeText = Show(left);
            var afterText = Show(right);
            Result.Add(new ContractFieldChange(
                path,
                Excerpt(beforeText),
                Excerpt(afterText),
                Explain(path, left, right) ?? HiddenBeyondExcerpt(beforeText, afterText)));
        }
    }

    private static string? Explain(string path, JsonElement? left, JsonElement? right)
    {
        var lower = path.ToLowerInvariant();
        if (GuardWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) return GuardExplanation;
        if (!lower.EndsWith("/required", StringComparison.Ordinal)) return null;
        // A confirmation entry in `required` is what makes a guarded call need the
        // argument at all. Judged on the parsed entries rather than on a display
        // excerpt, so a long list cannot push the word past a truncation.
        return GuardEntries(left).SetEquals(GuardEntries(right))
            ? "Required arguments changed; existing calls may need different inputs."
            : GuardExplanation;
    }

    private static HashSet<string> GuardEntries(JsonElement? required)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        if (required is not { } value) return entries;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var name = item.GetString() ?? "";
                var lower = name.ToLowerInvariant();
                if (GuardWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) entries.Add(name);
            }
        }
        else
        {
            // Not an array at all: the safest reading is that a guard might be
            // hiding in it.
            var lower = Display(value).ToLowerInvariant();
            if (GuardWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) entries.Add(lower);
        }
        return entries;
    }

    // MARK: - Equality and display

    /// <summary>Whether two values are the same contract, in canonical terms.</summary>
    /// <remarks>
    /// The same equivalence <c>ContractGuard.SameSchema</c> uses: object keys in any
    /// order, array order kept, strings by decoded value, other literals by their
    /// text — so <c>true</c> is never <c>1</c> and <c>1</c> is never <c>1.0</c>.
    /// </remarks>
    internal static bool SameValue(JsonElement? left, JsonElement? right)
    {
        if (left is null || right is null) return left is null && right is null;
        var a = left.Value;
        var b = right.Value;
        if (a.ValueKind != b.ValueKind) return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var leftProperties = SortedProperties(a);
                var rightProperties = SortedProperties(b);
                if (leftProperties.Length != rightProperties.Length) return false;
                for (var index = 0; index < leftProperties.Length; index++)
                {
                    if (!string.Equals(leftProperties[index].Name, rightProperties[index].Name, StringComparison.Ordinal) ||
                        !SameValue(leftProperties[index].Value, rightProperties[index].Value))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using var leftItems = a.EnumerateArray().GetEnumerator();
                using var rightItems = b.EnumerateArray().GetEnumerator();
                while (leftItems.MoveNext() && rightItems.MoveNext())
                {
                    if (!SameValue(leftItems.Current, rightItems.Current)) return false;
                }
                return true;
            }
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
            default:
                return true;
        }
    }

    private static JsonProperty[] SortedProperties(JsonElement element) =>
        element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();

    private static bool HasDuplicateNames(JsonElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return true;
        }
        return false;
    }

    private static JsonElement? Member(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out var value) ? value : null;

    /// <summary>Canonical text for a value, cut off early: only its excerpt is ever shown.</summary>
    /// <remarks>Null stays null — absent is not the same as an empty string.</remarks>
    private static string? Show(JsonElement? value) => value is { } element ? Display(element) : null;

    private static string Display(JsonElement element)
    {
        var text = new StringBuilder();
        AppendCanonical(element, text);
        return text.ToString();
    }

    // Four UTF-16 units per scalar is the most an excerpt can need, plus slack for
    // the marker. Past this the rest of a huge value is never looked at.
    private const int DisplayCap = MaxExcerptScalars * 2 + 64;

    private static void AppendCanonical(JsonElement element, StringBuilder text)
    {
        if (text.Length >= DisplayCap) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                var first = true;
                foreach (var property in SortedProperties(element))
                {
                    if (text.Length >= DisplayCap) break;
                    if (!first) text.Append(',');
                    first = false;
                    AppendString(property.Name, text);
                    text.Append(':');
                    AppendCanonical(property.Value, text);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var separate = false;
                foreach (var item in element.EnumerateArray())
                {
                    if (text.Length >= DisplayCap) break;
                    if (separate) text.Append(',');
                    separate = true;
                    AppendCanonical(item, text);
                }
                text.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(element.GetString() ?? "", text);
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            case JsonValueKind.Null:
                text.Append("null");
                break;
            default:
                text.Append(element.GetRawText());
                break;
        }
    }

    private static void AppendString(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (var character in value)
        {
            if (text.Length >= DisplayCap) break;
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ': text.Append("\\u").Append(((int)character).ToString("x4")); break;
                default: text.Append(character); break;
            }
        }
        text.Append('"');
    }

    // MARK: - Excerpts

    /// <summary>At most 800 Unicode scalars, then a marker; <c>null</c> stays <c>null</c>.</summary>
    /// <remarks>
    /// Scalars, because that is what a reader sees as characters in a way a
    /// bound can be reasoned about: an astral character is one scalar and two UTF-16
    /// units, and a combining mark is a scalar of its own however many a "grapheme"
    /// would swallow. A cut never lands inside a surrogate pair.
    /// </remarks>
    internal static string? Excerpt(string? value)
    {
        if (value is null) return null;
        // UTF-16 units are never fewer than scalars, so a short string needs no scan.
        if (value.Length <= MaxExcerptScalars) return value;

        var scalars = 0;
        var index = 0;
        while (index < value.Length)
        {
            if (scalars == MaxExcerptScalars) return string.Concat(value.AsSpan(0, index), TruncationMarker);
            index += char.IsHighSurrogate(value[index]) &&
                     index + 1 < value.Length &&
                     char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;
            scalars++;
        }
        return value;
    }

    /// <summary>Said aloud when two excerpts read the same but the values do not.</summary>
    private static string? HiddenBeyondExcerpt(string? before, string? after)
    {
        if (before is null || after is null || string.Equals(before, after, StringComparison.Ordinal)) return null;
        return string.Equals(Excerpt(before), Excerpt(after), StringComparison.Ordinal)
            ? "The visible excerpts are identical; the change is further into the text. " +
              "Inspect the complete value in the server's tool list."
            : null;
    }

    /// <summary>Unicode scalar order, which UTF-16 code unit order is not above U+E000.</summary>
    private sealed class ScalarOrder : IComparer<string>
    {
        internal static readonly ScalarOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x.EnumerateRunes().GetEnumerator();
            var right = y.EnumerateRunes().GetEnumerator();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight) return hasLeft.CompareTo(hasRight);
                var order = left.Current.Value.CompareTo(right.Current.Value);
                if (order != 0) return order;
            }
        }
    }
}
