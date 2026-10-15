using System.Globalization;
using System.Text;
using System.Text.Json;
using Kytto.Core.Health;

namespace Kytto.Core.Portable;

/// <summary>
/// The fingerprint of one tool's model-facing contract, identical on every platform
/// that writes a portable setup (§7.13).
/// </summary>
/// <remarks>
/// <para>
/// SHA-256 over the UTF-8 bytes of one JSON envelope,
/// <c>{"annotations"?,"description"?,"name","schema"?,"version":1}</c>, keys sorted,
/// optional fields omitted rather than null, slashes unescaped, no insignificant
/// whitespace. <c>schema</c> is the input schema's <em>canonical text</em> — a JSON
/// string holding the canonical form, not a nested object — so how a server
/// happened to space or order its schema changes nothing. Token measurements are
/// not part of it: they depend on the tokenizer and on presentation, and a count
/// that moved by two tokens has not changed what the model was told.
/// </para>
/// <para>
/// Written by hand rather than through <c>System.Text.Json</c> on purpose. Its
/// defaults escape non-ASCII text and <c>/</c>-adjacent characters differently,
/// emit nulls, keep insertion order and print numbers its own way; any of those
/// would make the same tool hash differently here than in the macOS app, and a
/// reference that never matches is worse than none. The golden vectors in
/// <c>Fixtures/compatibility</c> are what pin it.
/// </para>
/// </remarks>
public static class ContractDigest
{
    /// <summary>Larger than a real tool's schema by orders of magnitude.</summary>
    private const int MaximumSchemaBytes = 1_000_000;

    /// <summary>Foundation's own nesting limit, so both platforms refuse the same depth.</summary>
    private const int MaximumDepth = 512;

    public static string Of(ToolSummary tool)
    {
        var schema = tool.InputSchemaJSON is { } raw ? CanonicalSchema(raw) ?? raw : null;

        var envelope = new StringBuilder();
        envelope.Append('{');
        if (HintsOf(tool.Annotations) is { } hints) envelope.Append("\"annotations\":").Append(hints).Append(',');
        if (tool.Description is { } description)
        {
            envelope.Append("\"description\":");
            AppendString(envelope, description);
            envelope.Append(',');
        }
        envelope.Append("\"name\":");
        AppendString(envelope, tool.Name);
        if (schema is not null)
        {
            envelope.Append(",\"schema\":");
            AppendString(envelope, schema);
        }
        envelope.Append(",\"version\":1}");
        return Digest.Of(Encoding.UTF8.GetBytes(envelope.ToString()));
    }

    /// <summary>
    /// The schema's canonical text, or null when it cannot be canonicalized and the
    /// raw text has to stand in for it.
    /// </summary>
    /// <remarks>
    /// Object keys are sorted (the last of a duplicated key wins, as a dictionary
    /// would have it), array order is kept — <c>enum</c>, <c>oneOf</c> and
    /// <c>prefixItems</c> can mean something else reordered — and a number is
    /// printed the way Foundation prints it, so <c>1.0</c> and <c>1</c> agree. A
    /// schema that does not parse is never "fixed": the raw text decides, so a
    /// change cannot be hidden by the canonicalization.
    /// </remarks>
    public static string? CanonicalSchema(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumSchemaBytes) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var text = new StringBuilder();
            return Write(document.RootElement, text) ? text.ToString() : null;
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException or ArgumentException)
        {
            // A lone surrogate escape parses but cannot be decoded, which is the same
            // thing as a schema that does not parse: the raw text stands in for it. An
            // exception here would otherwise reach every state update that summarises
            // a pending reference, and with it the whole window.
            return null;
        }
    }

    private static bool Write(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) members[property.Name] = property.Value;
                text.Append('{');
                var first = true;
                foreach (var (key, value) in members)
                {
                    if (!first) text.Append(',');
                    first = false;
                    AppendString(text, key);
                    text.Append(':');
                    if (!Write(value, text)) return false;
                }
                text.Append('}');
                return true;
            case JsonValueKind.Array:
                text.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0) text.Append(',');
                    if (!Write(item, text)) return false;
                }
                text.Append(']');
                return true;
            case JsonValueKind.String:
                AppendString(text, element.GetString() ?? "");
                return true;
            case JsonValueKind.Number:
                if (FormatNumber(element.GetRawText()) is not { } number) return false;
                text.Append(number);
                return true;
            case JsonValueKind.True:
                text.Append("true");
                return true;
            case JsonValueKind.False:
                text.Append("false");
                return true;
            case JsonValueKind.Null:
                text.Append("null");
                return true;
            default:
                return false;
        }
    }

    /// <summary>A number as Foundation's JSON writer prints it.</summary>
    /// <remarks>
    /// Whole numbers lose a trailing <c>.0</c> and exponent (<c>1.0</c> and
    /// <c>1e3</c> are <c>1</c> and <c>1000</c>); anything else prints its shortest
    /// round-trip digits in C's exponent spelling. The golden vectors cover the
    /// common cases. Magnitudes beyond 1e15 are the one range this has not been
    /// compared with the macOS app on, and a schema that uses them is rare.
    /// </remarks>
    private static string? FormatNumber(string raw)
    {
        var isIntegerLexeme = raw.All(character => character is '-' or (>= '0' and <= '9'));
        if (isIntegerLexeme)
        {
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            {
                return whole.ToString(CultureInfo.InvariantCulture);
            }
            if (ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var large))
            {
                return large.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (!double.TryParse(
                raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
        {
            return null;
        }
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    private static string? HintsOf(ToolAnnotations? annotations)
    {
        if (annotations is null) return null;

        // An annotations object that says nothing is the same as none: the macOS app
        // never records one, and this app's parser always builds one.
        var parts = new List<string>();
        if (annotations.DestructiveHint is { } destructive) parts.Add($"\"destructiveHint\":{Bool(destructive)}");
        if (annotations.IdempotentHint is { } idempotent) parts.Add($"\"idempotentHint\":{Bool(idempotent)}");
        if (annotations.OpenWorldHint is { } openWorld) parts.Add($"\"openWorldHint\":{Bool(openWorld)}");
        if (annotations.ReadOnlyHint is { } readOnly) parts.Add($"\"readOnlyHint\":{Bool(readOnly)}");
        return parts.Count == 0 ? null : "{" + string.Join(',', parts) + "}";
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// A JSON string literal: quote, backslash and control characters escaped,
    /// everything else — <c>/</c> and non-ASCII text included — written as it is.
    /// </summary>
    private static void AppendString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ':
                    text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: text.Append(character); break;
            }
        }
        text.Append('"');
    }
}
