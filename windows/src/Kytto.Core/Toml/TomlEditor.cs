using System.Globalization;
using System.Text;
using Kytto.Core.Model;

namespace Kytto.Core.Toml;

public enum TomlEditErrorKind
{
    TableNotFound,
    NotATable,
    /// <summary>
    /// The entry exists, but as an inline table on one line rather than as a
    /// <c>[header]</c> of its own. Kytto reads those; it will not rewrite one,
    /// because doing it safely means re-serialising a line it did not write.
    /// </summary>
    InlineDefinition,

    /// <summary>No value where one was expected, e.g. a server without <c>args</c>.</summary>
    ValueNotFound,

    /// <summary>The same key is written twice, so which one a client reads is a guess.</summary>
    AmbiguousValue,

    /// <summary>
    /// The value is not what the caller last read there, so the edit it was
    /// planned against no longer applies.
    /// </summary>
    ValueMismatch,

    /// <summary>
    /// A field spelled in a way Kytto reads but will not splice — <c>env.KEY = …</c>
    /// as dotted keys on the server's own table. Rewriting it would mean
    /// re-serialising lines Kytto did not write (§6.3).
    /// </summary>
    UnsupportedLayout,
}

public sealed class TomlEditException(TomlEditErrorKind kind, string subject)
    : Exception(Describe(kind, subject))
{
    public TomlEditErrorKind Kind { get; } = kind;
    public string Subject { get; } = subject;

    private static string Describe(TomlEditErrorKind kind, string subject) => kind switch
    {
        TomlEditErrorKind.TableNotFound => $"No table at {subject}.",
        TomlEditErrorKind.NotATable => $"{subject} is not a table.",
        TomlEditErrorKind.ValueNotFound => $"No value at {subject}.",
        TomlEditErrorKind.AmbiguousValue =>
            $"{subject} is written more than once. Kytto will not guess which one counts.",
        TomlEditErrorKind.ValueMismatch =>
            $"{subject} no longer says what Kytto read there, so nothing was changed.",
        TomlEditErrorKind.UnsupportedLayout =>
            $"{subject} is written as a dotted key. Kytto will not rewrite that line; " +
            "move it into its own table, then try again.",
        _ => $"\"{subject}\" is written as an inline table. Kytto will not rewrite that line.",
    };
}

/// <summary>Structural edits, expressed as splices.</summary>
/// <remarks>
/// <para>
/// Same contract as the JSON editor: every operation returns the original text
/// with one range replaced, so comments, blank lines, key order and the forty-odd
/// <c>[projects."…"]</c> tables in a real Codex config all survive untouched (§6.3).
/// </para>
/// <para>
/// The fiddly part here is not commas but <em>lines</em>. A table is a header plus
/// everything under it until the next header, and a server's <c>env</c> lives in a
/// second table of its own — so removing one server means removing two tables and
/// exactly the right number of blank lines around them.
/// </para>
/// </remarks>
public static class TomlEditor
{
    /// <summary>Adds or replaces a server's definition.</summary>
    /// <param name="block">
    /// A rendered table block, headers and all, as produced by <see cref="TomlBuilder"/>.
    /// Replacing an existing server swaps its tables for the block — so only for a
    /// definition Kytto is putting there wholesale. An edit the user typed goes
    /// through <c>SplicingToml</c> instead, which keeps every key a draft does not
    /// describe (§6.3).
    /// </param>
    public static string SettingServer(
        this TomlDocument self,
        string name,
        string key,
        string block)
    {
        block = self.WithPreferredLineEndings(block);
        if (self.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }

        // A server's tables need not sit together — `[a]`, `[b]`, `[a.env]` is valid
        // TOML — so they are replaced run by run. One span from the first to the
        // last would take `[b]` with it.
        var runs = self.ServerTableRuns(name, key);
        if (runs.Count > 0)
        {
            string[] own = [key, name];
            var ownIndex = runs.FindIndex(run => run.Any(table => table.Path.SequenceEqual(own)));
            if (ownIndex < 0) ownIndex = 0;
            return self.Replacing(runs.Select((run, index) => index == ownIndex
                ? (new TomlSpan(self.LineStart(run[0].Span.Start), run[^1].Span.End), block)
                : (self.RemovalSpan(run[0], run[^1]), "")));
        }

        // New server. Keep it with the others if there are others, so the file
        // stays grouped the way its owner left it.
        var siblings = self.ChildTables(key);
        if (siblings.Count > 0)
        {
            var last = siblings[^1];
            var under = self.TablesUnder(last.Path);
            var anchor = (under.Count > 0 ? under[^1] : last).Span.End;
            var newline = self.Newline();
            return self.Replacing(new TomlSpan(anchor, anchor), newline + newline + block);
        }

        var end = self.SourceBytes.Length;
        var lineEnding = self.Newline();
        var separator = self.SourceText.Length == 0
            ? ""
            : self.EndsWithBlankLine() ? "" : self.EndsWithNewline() ? lineEnding : lineEnding + lineEnding;
        return self.Replacing(new TomlSpan(end, end), separator + block + lineEnding);
    }

    /// <summary>
    /// Removes a server's tables entirely, leaving the file as if it had never been
    /// typed — no stranded header, no doubled blank line.
    /// </summary>
    public static string RemovingServer(this TomlDocument self, string name, string key)
    {
        if (self.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }

        // Run by run, for the reason SettingServer gives: another server's table
        // sitting between two of this one's is not this one's to delete.
        var runs = self.ServerTableRuns(name, key);
        if (runs.Count == 0) return self.SourceText;
        return self.Replacing(runs.Select(run => (self.RemovalSpan(run[0], run[^1]), "")));
    }

    /// <summary>
    /// Removes one table — header, keys, and the blank lines it occupied — and
    /// nothing under it.
    /// </summary>
    public static string RemovingTable(this TomlDocument self, IReadOnlyList<string> path)
    {
        var table = self.Table([.. path]);
        if (table?.HeaderSpan is null) return self.SourceText;
        return self.Replacing(self.RemovalSpan(table, table), "");
    }

    /// <summary>
    /// Gives a server a new name by rewriting the headers of its tables and nothing
    /// else, so every key under them survives the rename (§6.3).
    /// </summary>
    public static string RenamingServer(this TomlDocument self, string name, string newName, string key)
    {
        if (self.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }
        var tables = self.ServerTables(name, key);
        if (tables.Count == 0) throw new TomlEditException(TomlEditErrorKind.TableNotFound, $"{key}.{name}");
        return self.Replacing(tables
            .Where(table => table.HeaderSpan is not null)
            .Select(table =>
            {
                var path = TomlText.Path([key, newName, .. table.Path.Skip(2)]);
                return (table.HeaderSpan!.Value, table.IsArrayElement ? $"[[{path}]]" : $"[{path}]");
            }));
    }

    // MARK: - Values inside one table

    /// <summary>
    /// Sets <paramref name="name"/> in the table at <paramref name="path"/> to
    /// already-spelled TOML, adding the key after the table's last one if it is not
    /// there.
    /// </summary>
    public static string SettingValue(
        this TomlDocument self,
        string name,
        IReadOnlyList<string> path,
        string rawValue)
    {
        var table = self.Table([.. path])
            ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, string.Join(".", path));
        var matches = table.Pairs.Where(pair => pair.Key.Count == 1 && pair.Key[0] == name).ToArray();
        if (matches.Length > 1)
        {
            throw new TomlEditException(TomlEditErrorKind.AmbiguousValue, string.Join(".", [.. path, name]));
        }
        if (matches.Length == 1) return self.Replacing(matches[0].Value.Span, rawValue);

        // At the end of the table's last line rather than straight after its last
        // value, so a comment trailing that line stays on it.
        var anchor = self.LineContentEnd(table.Span.End);
        return self.Replacing(
            new TomlSpan(anchor, anchor),
            $"{self.Newline()}{TomlText.Key(name)} = {rawValue}");
    }

    /// <summary>
    /// Removes <paramref name="name"/> from the table at <paramref name="path"/>, line
    /// and all. A missing key is not an error — the desired end state is already true.
    /// </summary>
    public static string RemovingValue(this TomlDocument self, string name, IReadOnlyList<string> path)
    {
        var table = self.Table([.. path])
            ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, string.Join(".", path));
        var matches = table.Pairs.Where(pair => pair.Key.Count == 1 && pair.Key[0] == name).ToArray();
        if (matches.Length > 1)
        {
            throw new TomlEditException(TomlEditErrorKind.AmbiguousValue, string.Join(".", [.. path, name]));
        }
        if (matches.Length == 0) return self.SourceText;
        return self.Replacing(
            new TomlSpan(self.LineStart(matches[0].Span.Start), self.EndOfLine(matches[0].Span.End)),
            "");
    }

    /// <summary>
    /// Just past the last line of the table at <paramref name="path"/>, before its
    /// line ending: where a new table that belongs with it goes.
    /// </summary>
    public static int? EndOfTableContent(this TomlDocument self, IReadOnlyList<string> path) =>
        self.Table([.. path]) is { } table ? self.LineContentEnd(table.Span.End) : null;

    /// <summary>A server's tables grouped into runs that sit together in the file.</summary>
    private static List<List<TomlTable>> ServerTableRuns(this TomlDocument self, string name, string key)
    {
        var own = self.ServerTables(name, key);
        var runs = new List<List<TomlTable>>();
        var current = new List<TomlTable>();
        foreach (var table in self.Tables)
        {
            if (own.Any(candidate => ReferenceEquals(candidate, table)))
            {
                current.Add(table);
            }
            else if (current.Count > 0)
            {
                runs.Add(current);
                current = [];
            }
        }
        if (current.Count > 0) runs.Add(current);
        return runs;
    }

    /// <summary>The range that makes a run of tables vanish as if never typed.</summary>
    private static TomlSpan RemovalSpan(this TomlDocument self, TomlTable first, TomlTable last)
    {
        var start = self.LineStart(first.Span.Start);
        var end = self.EndOfLine(last.Span.End);

        // Take the blank lines that followed it. If there were none — because it
        // was last in the file — take the blank line that preceded it instead, so
        // the gap it leaves behind is the one it was occupying.
        var afterBlanks = self.SkippingBlankLines(end);
        if (afterBlanks > end)
        {
            end = afterBlanks;
        }
        else
        {
            start = self.StartSkippingBlankLines(start);
        }
        return new TomlSpan(start, end);
    }

    /// <summary>
    /// Sets a boolean on the server's own table, creating the key if it is not
    /// there yet.
    /// </summary>
    /// <remarks>
    /// This is Codex's off switch, and it is a better one than most: the definition
    /// stays in the file, so nothing has to be parked and switching back on cannot
    /// lose anything.
    /// </remarks>
    public static string SettingServerFlag(
        this TomlDocument self,
        bool value,
        string flagKey,
        string name,
        string key)
    {
        var table = self.Table(key, name)
            ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, $"{key}.{name}");

        if (table.Pair(flagKey) is { } pair)
        {
            return self.Replacing(pair.Value.Span, TomlText.Bool(value));
        }

        // After the last key of this table, which is before any `[…env]` header —
        // a bare key after a sub-table header would belong to the sub-table.
        var anchor = table.Span.End;
        return self.Replacing(
            new TomlSpan(anchor, anchor),
            $"{self.Newline()}{TomlText.Key(flagKey)} = {TomlText.Bool(value)}");
    }

    /// <summary>
    /// Replaces one string in a server's <c>args</c> array without reserialising
    /// the array, its table or an inline definition. The splice MCP Doctor's
    /// package pin uses (§7.10).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SettingServer"/>, an inline <c>name = { … }</c> definition
    /// is fine here: only the one string literal inside it changes, which is a
    /// splice, not a rewrite of a line Kytto did not write.
    /// </remarks>
    /// <param name="position">Counts string elements only, as <c>ServerFields</c> does.</param>
    /// <param name="expecting">What that element must still say.</param>
    public static string SettingServerArgument(
        this TomlDocument self,
        int position,
        string expecting,
        string value,
        string name,
        string key)
    {
        var server = $"{key}.{name}";
        IReadOnlyList<TomlPair> fields;
        if (self.Table(key, name) is { } table)
        {
            fields = table.Pairs;
        }
        else
        {
            var inline = self.Table(key)?.Pairs.Where(pair => pair.Name == name).ToArray() ?? [];
            if (inline.Length > 1) throw new TomlEditException(TomlEditErrorKind.AmbiguousValue, server);
            fields = inline.FirstOrDefault()?.Value.InlinePairs
                ?? throw new TomlEditException(TomlEditErrorKind.ValueNotFound, server);
        }

        var matches = fields.Where(pair => pair.Name == "args").ToArray();
        if (matches.Length > 1) throw new TomlEditException(TomlEditErrorKind.AmbiguousValue, $"{server}.args");
        var arguments = matches.FirstOrDefault()?.Value.Elements
            ?? throw new TomlEditException(TomlEditErrorKind.ValueNotFound, $"{server}.args");

        var strings = arguments.Where(element => element.StringValue is not null).ToArray();
        if (position < 0 || position >= strings.Length ||
            !string.Equals(strings[position].StringValue, expecting, StringComparison.Ordinal))
        {
            throw new TomlEditException(TomlEditErrorKind.ValueMismatch, $"{server}.args");
        }
        return self.Replacing(strings[position].Span, TomlText.String(value));
    }

    // MARK: - Line geometry

    private static bool EndsWithNewline(this TomlDocument self) =>
        self.SourceBytes.Length > 0 && self.SourceBytes[^1] == 0x0A;

    /// <summary>
    /// The line ending that ends the line containing an offset, or the end of the
    /// file — where text belonging at the end of that line goes.
    /// </summary>
    private static int LineContentEnd(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = Math.Min(offset, bytes.Length);
        while (index < bytes.Length && bytes[index] != 0x0A) index++;
        if (index > offset && index < bytes.Length && bytes[index - 1] == 0x0D) index--;
        return index;
    }

    internal static string Newline(this TomlDocument self) =>
        self.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string WithPreferredLineEndings(this TomlDocument self, string text)
    {
        var newline = self.Newline();
        if (newline == "\n") return text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);
    }

    private static bool EndsWithBlankLine(this TomlDocument self)
    {
        var bytes = self.SourceBytes;
        if (bytes.Length < 2) return false;
        var index = bytes.Length - 1;
        if (bytes[index] != 0x0A) return false;
        index--;
        while (index >= 0 && bytes[index] is 0x0D or 0x20 or 0x09) index--;
        return index >= 0 && bytes[index] == 0x0A;
    }

    private static int LineStart(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = Math.Min(offset, bytes.Length);
        while (index > 0 && bytes[index - 1] != 0x0A) index--;
        return index;
    }

    /// <summary>Just past the newline that ends the line containing an offset.</summary>
    private static int EndOfLine(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = Math.Min(offset, bytes.Length);
        while (index < bytes.Length && bytes[index] != 0x0A) index++;
        return index < bytes.Length ? index + 1 : index;
    }

    private static int? IsBlankLine(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset;
        while (index < bytes.Length && bytes[index] is 0x20 or 0x09 or 0x0D) index++;
        return index < bytes.Length && bytes[index] == 0x0A ? index + 1 : null;
    }

    private static int SkippingBlankLines(this TomlDocument self, int offset)
    {
        var index = offset;
        while (self.IsBlankLine(index) is { } next) index = next;
        return index;
    }

    private static int StartSkippingBlankLines(this TomlDocument self, int offset)
    {
        var index = offset;
        while (index > 0)
        {
            var previousLine = self.LineStart(index - 1);
            if (self.IsBlankLine(previousLine) is null) break;
            index = previousLine;
        }
        return index;
    }
}

// MARK: - Rendering

/// <summary>Literals, spelled the way TOML spells them.</summary>
public static class TomlText
{
    public static string String(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 0x20) result.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Array(IReadOnlyList<string> values) =>
        "[" + string.Join(", ", values.Select(String)) + "]";

    /// <summary>A bare key where TOML allows one, a quoted key where it does not.</summary>
    /// <remarks>
    /// Server names reach this from a config file Kytto did not write, so "allows
    /// one" has to be checked rather than assumed.
    /// </remarks>
    public static string Key(string value)
    {
        var bare = value.Length > 0 && value.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
        return bare ? value : String(value);
    }

    /// <summary>A dotted path, each component quoted only if it has to be.</summary>
    public static string Path(IReadOnlyList<string> components) =>
        string.Join(".", components.Select(Key));
}

/// <summary>Renders a server definition as a TOML table block.</summary>
/// <remarks>
/// Shaped after what <c>codex mcp add</c> itself writes — a table per server with
/// the environment in a sub-table — so a config Kytto has edited still looks like
/// one Codex wrote, and a diff shows only what changed.
/// </remarks>
public static class TomlBuilder
{
    public static string ServerBlock(
        string name,
        string key,
        string? command,
        IReadOnlyList<string> args,
        string? url,
        IReadOnlyList<EnvEntry> env,
        bool? enabled)
    {
        var lines = new List<string> { $"[{TomlText.Path([key, name])}]" };

        if (!string.IsNullOrEmpty(url))
        {
            lines.Add($"url = {TomlText.String(url)}");
        }
        else if (!string.IsNullOrEmpty(command))
        {
            lines.Add($"command = {TomlText.String(command)}");
            if (args.Count > 0) lines.Add($"args = {TomlText.Array(args)}");
        }

        if (enabled is { } flag) lines.Add($"enabled = {TomlText.Bool(flag)}");

        var realEnv = env.Where(entry => !string.IsNullOrWhiteSpace(entry.Key)).ToArray();
        if (realEnv.Length > 0)
        {
            lines.Add("");
            lines.Add($"[{TomlText.Path([key, name, "env"])}]");
            foreach (var entry in realEnv)
            {
                lines.Add($"{TomlText.Key(entry.Key)} = {TomlText.String(entry.Value ?? "")}");
            }
        }

        return string.Join("\n", lines);
    }
}
