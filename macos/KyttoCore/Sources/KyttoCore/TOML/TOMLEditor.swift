import Foundation

public enum TOMLEditError: Error, Equatable {
    case tableNotFound([String])
    case notATable([String])
    /// The entry exists, but as an inline table on one line rather than as a
    /// `[header]` of its own. Kytto reads those; it will not rewrite one,
    /// because doing it safely means re-serialising a line it did not write.
    case inlineDefinition(String)
    case valueNotFound([String])
    case ambiguousValue([String])
    /// The value is not what the caller last read there.
    case valueMismatch([String])
    /// A field spelled in a way Kytto reads but will not splice — `env.KEY = …`
    /// as dotted keys on the server's own table. Rewriting it would mean
    /// re-serialising lines Kytto did not write (§6.3).
    case unsupportedLayout([String])
}

/// Structural edits, expressed as splices.
///
/// Same contract as `JSONEditor`: every operation returns the original text with
/// one range replaced, so comments, blank lines, key order and the forty-odd
/// `[projects."…"]` tables in a real Codex config all survive untouched (§6.3).
///
/// The fiddly part here is not commas but *lines*. A table is a header plus
/// everything under it until the next header, and a server's `env` lives in a
/// second table of its own — so removing one server means removing two tables
/// and exactly the right number of blank lines around them.
public extension TOMLDocument {

    // MARK: - Reading

    /// The servers defined under `key`, in file order.
    ///
    /// Both spellings count: a `[mcp_servers.name]` table, and a `name = { … }`
    /// inline table sitting under a `[mcp_servers]` header. Codex writes the
    /// first; a hand-edited file can hold either.
    func serverNames(under key: String) -> [String] {
        var names: [String] = []
        for table in childTables(of: [key]) where !table.isArrayElement {
            if let name = table.path.last { names.append(name) }
        }
        for pair in table(at: [key])?.pairs ?? [] {
            if let name = pair.name, pair.value.inlinePairs != nil, !names.contains(name) {
                names.append(name)
            }
        }
        return names
    }

    /// Everything that makes up one server: its own table and any sub-table of
    /// it, such as `env`.
    func serverTables(_ name: String, under key: String) -> [TOMLTable] {
        let path = [key, name]
        guard let own = table(at: path) else { return [] }
        return [own] + tables(under: path)
    }

    /// The source text of a server's definition, exactly as it appears.
    ///
    /// This is what gets parked when a server is switched off, so switching it
    /// back on is a restoration rather than a reconstruction.
    func serverDefinitionText(_ name: String, under key: String) -> String? {
        let tables = serverTables(name, under: key)
        guard let first = tables.first, let last = tables.last else {
            // An inline definition has no table of its own; park the pair.
            guard let pair = table(at: [key])?.pair(name) else { return nil }
            return slice(pair.span)
        }
        return slice(TOMLSpan(start: first.span.start, end: last.span.end))
    }

    // MARK: - Writing

    /// Replaces one environment value without reserializing its table or inline
    /// definition. This is the splice used by transactional secret rotation.
    func settingServerEnvironmentValue(
        _ environmentKey: String,
        to value: String,
        forServer serverName: String,
        under serversKey: String
    ) throws -> String {
        let tablePath = [serversKey, serverName, "env"]
        if let environmentTable = table(at: tablePath) {
            let matches = environmentTable.pairs.filter { $0.name == environmentKey }
            guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(tablePath + [environmentKey]) }
            guard let pair = matches.first else { throw TOMLEditError.valueNotFound(tablePath + [environmentKey]) }
            return replacing(pair.value.span, with: TOMLText.string(value))
        }

        let serverPath = [serversKey, serverName]
        guard let serverPair = table(at: [serversKey])?.pairs.first(where: { $0.name == serverName }),
              let serverFields = serverPair.value.inlinePairs
        else { throw TOMLEditError.valueNotFound(serverPath) }
        let environments = serverFields.filter { $0.name == "env" }
        guard environments.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath + ["env"]) }
        guard let environment = environments.first, let values = environment.value.inlinePairs else {
            throw TOMLEditError.valueNotFound(serverPath + ["env"])
        }
        let matches = values.filter { $0.name == environmentKey }
        guard matches.count <= 1 else {
            throw TOMLEditError.ambiguousValue(serverPath + ["env", environmentKey])
        }
        guard let pair = matches.first else {
            throw TOMLEditError.valueNotFound(serverPath + ["env", environmentKey])
        }
        return replacing(pair.value.span, with: TOMLText.string(value))
    }

    /// Replaces one string in a server's `args` array without reserializing the
    /// array, its table or an inline definition. The splice MCP Doctor's package
    /// pin uses (§7.10).
    ///
    /// `position` counts string elements only, as `ServerFields` does, and
    /// `expecting` is what that element must still say.
    func settingServerArgument(
        at position: Int,
        expecting: String,
        to value: String,
        forServer serverName: String,
        under serversKey: String
    ) throws -> String {
        let serverPath = [serversKey, serverName]
        let fields: [TOMLPair]
        if let table = table(at: serverPath) {
            fields = table.pairs
        } else {
            let servers = table(at: [serversKey])?.pairs.filter { $0.name == serverName } ?? []
            guard servers.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath) }
            guard let inline = servers.first?.value.inlinePairs else {
                throw TOMLEditError.valueNotFound(serverPath)
            }
            fields = inline
        }

        let matches = fields.filter { $0.name == "args" }
        guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(serverPath + ["args"]) }
        guard let arguments = matches.first?.value.elements else {
            throw TOMLEditError.valueNotFound(serverPath + ["args"])
        }
        let strings = arguments.filter { $0.stringValue != nil }
        guard strings.indices.contains(position),
              strings[position].stringValue == expecting
        else { throw TOMLEditError.valueMismatch(serverPath + ["args"]) }
        return replacing(strings[position].span, with: TOMLText.string(value))
    }

    /// Adds or replaces a server's definition.
    ///
    /// `block` is a rendered table block, headers and all, as produced by
    /// `TOMLBuilder`. Replacing an existing server swaps its tables for the
    /// block — so only for a definition Kytto is putting there wholesale. An
    /// edit the user typed goes through `ServerDraft.splicingTOML` instead,
    /// which keeps every key a draft does not describe (§6.3).
    func settingServer(_ name: String, under key: String, to block: String) throws -> String {
        if table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }

        // A server's tables need not sit together — `[a]`, `[b]`, `[a.env]` is
        // valid TOML — so they are replaced run by run. One span from the first
        // to the last would take `[b]` with it.
        let runs = serverTableRuns(name, under: key)
        if let own = runs.firstIndex(where: { $0.contains { $0.path == [key, name] } }) ?? runs.indices.first {
            var edits: [(span: TOMLSpan, replacement: String)] = []
            for (index, run) in runs.enumerated() {
                if index == own {
                    edits.append((TOMLSpan(start: lineStart(of: run[0].span.start), end: run[run.count - 1].span.end), block))
                } else {
                    edits.append((removalSpan(first: run[0], last: run[run.count - 1]), ""))
                }
            }
            return replacing(edits)
        }

        // New server. Keep it with the others if there are others, so the file
        // stays grouped the way its owner left it.
        let siblings = childTables(of: [key])
        if let last = siblings.last {
            let anchor = (tables(under: last.path).last ?? last).span.end
            return replacing(TOMLSpan(start: anchor, end: anchor), with: "\n\n" + block)
        }

        let end = sourceBytes.count
        let separator = sourceText.isEmpty ? "" : (endsWithBlankLine ? "" : (endsWithNewline ? "\n" : "\n\n"))
        return replacing(TOMLSpan(start: end, end: end), with: separator + block + "\n")
    }

    /// Removes a server's tables entirely, leaving the file as if it had never
    /// been typed — no stranded header, no doubled blank line.
    func removingServer(_ name: String, under key: String) throws -> String {
        if table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }

        // Run by run, for the reason `settingServer` gives: another server's
        // table sitting between two of this one's is not this one's to delete.
        let runs = serverTableRuns(name, under: key)
        guard !runs.isEmpty else { return sourceText }
        return replacing(runs.map { (removalSpan(first: $0[0], last: $0[$0.count - 1]), "") })
    }

    /// Removes one table — header, keys, and the blank lines it occupied —
    /// and nothing under it.
    func removingTable(at path: [String]) -> String {
        guard let table = table(at: path), table.headerSpan != nil else { return sourceText }
        return replacing(removalSpan(first: table, last: table), with: "")
    }

    /// Gives a server a new name by rewriting the headers of its tables and
    /// nothing else, so every key under them survives the rename (§6.3).
    func renamingServer(_ name: String, to newName: String, under key: String) throws -> String {
        if table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }
        let tables = serverTables(name, under: key)
        guard !tables.isEmpty else { throw TOMLEditError.tableNotFound([key, name]) }
        return replacing(tables.compactMap { table in
            guard let header = table.headerSpan else { return nil }
            let path = TOMLText.path([key, newName] + table.path.dropFirst(2))
            return (header, table.isArrayElement ? "[[\(path)]]" : "[\(path)]")
        })
    }

    // MARK: - Values inside one table

    /// Sets `name` in the table at `path` to `rawValue` (already-spelled TOML),
    /// adding the key after the table's last one if it is not there.
    func settingValue(_ name: String, inTable path: [String], to rawValue: String) throws -> String {
        guard let table = table(at: path) else { throw TOMLEditError.tableNotFound(path) }
        let matches = table.pairs.filter { $0.key == [name] }
        guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(path + [name]) }
        if let pair = matches.first {
            return replacing(pair.value.span, with: rawValue)
        }
        // At the end of the table's last line rather than straight after its
        // last value, so a comment trailing that line stays on it.
        let anchor = lineContentEnd(from: table.span.end)
        return replacing(
            TOMLSpan(start: anchor, end: anchor),
            with: "\n\(TOMLText.key(name)) = \(rawValue)"
        )
    }

    /// Removes `name` from the table at `path`, line and all. A missing key is
    /// not an error — the desired end state is already true.
    func removingValue(_ name: String, inTable path: [String]) throws -> String {
        guard let table = table(at: path) else { throw TOMLEditError.tableNotFound(path) }
        let matches = table.pairs.filter { $0.key == [name] }
        guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue(path + [name]) }
        guard let pair = matches.first else { return sourceText }
        return replacing(
            TOMLSpan(start: lineStart(of: pair.span.start), end: endOfLine(from: pair.span.end)),
            with: ""
        )
    }

    /// Just past the last line of the table at `path`, before its line ending:
    /// where a new table that belongs with it goes.
    func endOfTableContent(at path: [String]) -> Int? {
        table(at: path).map { lineContentEnd(from: $0.span.end) }
    }

    /// A server's tables grouped into runs that sit together in the file, in
    /// file order.
    private func serverTableRuns(_ name: String, under key: String) -> [[TOMLTable]] {
        let own = Set(serverTables(name, under: key).map(\.path))
        var runs: [[TOMLTable]] = []
        var current: [TOMLTable] = []
        for table in tables {
            if own.contains(table.path) {
                current.append(table)
            } else if !current.isEmpty {
                runs.append(current)
                current = []
            }
        }
        if !current.isEmpty { runs.append(current) }
        return runs
    }

    /// The range that makes a run of tables vanish as if never typed.
    private func removalSpan(first: TOMLTable, last: TOMLTable) -> TOMLSpan {
        var start = lineStart(of: first.span.start)
        var end = endOfLine(from: last.span.end)

        // Take the blank lines that followed it. If there were none — because it
        // was last in the file — take the blank line that preceded it instead,
        // so the gap it leaves behind is the one it was occupying.
        let afterBlanks = skippingBlankLines(from: end)
        if afterBlanks > end {
            end = afterBlanks
        } else {
            start = startSkippingBlankLines(before: start)
        }
        return TOMLSpan(start: start, end: end)
    }

    /// Sets a boolean on the server's own table, creating the key if it is not
    /// there yet.
    ///
    /// This is Codex's off switch, and it is a better one than most: the
    /// definition stays in the file, so nothing has to be parked and switching
    /// back on cannot lose anything.
    func settingServerFlag(
        _ value: Bool,
        key flagKey: String,
        forServer name: String,
        under key: String
    ) throws -> String {
        let path = [key, name]
        guard let table = table(at: path) else { throw TOMLEditError.tableNotFound(path) }

        if let pair = table.pair(flagKey) {
            return replacing(pair.value.span, with: TOMLText.bool(value))
        }

        // After the last key of this table, which is before any `[…env]` header —
        // a bare key after a sub-table header would belong to the sub-table.
        let anchor = table.span.end
        return replacing(TOMLSpan(start: anchor, end: anchor), with: "\n\(TOMLText.key(flagKey)) = \(TOMLText.bool(value))")
    }

    // MARK: - Line geometry

    private var endsWithNewline: Bool {
        sourceBytes.last == 0x0A
    }

    private var endsWithBlankLine: Bool {
        guard sourceBytes.count >= 2 else { return false }
        var index = sourceBytes.count - 1
        guard sourceBytes[index] == 0x0A else { return false }
        index -= 1
        while index >= 0, sourceBytes[index] == 0x0D || sourceBytes[index] == 0x20 || sourceBytes[index] == 0x09 {
            index -= 1
        }
        return index >= 0 && sourceBytes[index] == 0x0A
    }

    private func lineStart(of offset: Int) -> Int {
        var index = min(offset, sourceBytes.count)
        while index > 0, sourceBytes[index - 1] != 0x0A { index -= 1 }
        return index
    }

    /// The line ending that ends the line containing `offset`, or the end of
    /// the file — where text belonging at the end of that line goes.
    private func lineContentEnd(from offset: Int) -> Int {
        var index = min(offset, sourceBytes.count)
        while index < sourceBytes.count, sourceBytes[index] != 0x0A { index += 1 }
        if index > offset, index < sourceBytes.count, sourceBytes[index - 1] == 0x0D { index -= 1 }
        return index
    }

    /// Just past the newline that ends the line containing `offset`.
    private func endOfLine(from offset: Int) -> Int {
        var index = min(offset, sourceBytes.count)
        while index < sourceBytes.count, sourceBytes[index] != 0x0A { index += 1 }
        return index < sourceBytes.count ? index + 1 : index
    }

    private func isBlankLine(startingAt offset: Int) -> Int? {
        var index = offset
        while index < sourceBytes.count, sourceBytes[index] == 0x20 || sourceBytes[index] == 0x09
            || sourceBytes[index] == 0x0D {
            index += 1
        }
        guard index < sourceBytes.count, sourceBytes[index] == 0x0A else { return nil }
        return index + 1
    }

    private func skippingBlankLines(from offset: Int) -> Int {
        var index = offset
        while let next = isBlankLine(startingAt: index) { index = next }
        return index
    }

    private func startSkippingBlankLines(before offset: Int) -> Int {
        var index = offset
        while index > 0 {
            let previousLine = lineStart(of: index - 1)
            guard isBlankLine(startingAt: previousLine) != nil else { break }
            index = previousLine
        }
        return index
    }
}

// MARK: - Rendering

/// Literals, spelled the way TOML spells them.
public enum TOMLText {
    public static func string(_ value: String) -> String {
        var out = "\""
        for scalar in value.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                // DEL is a control character TOML forbids unescaped too.
                if scalar.value < 0x20 || scalar.value == 0x7F {
                    out += String(format: "\\u%04X", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        return out + "\""
    }

    public static func bool(_ value: Bool) -> String {
        value ? "true" : "false"
    }

    public static func array(_ values: [String]) -> String {
        "[" + values.map(string).joined(separator: ", ") + "]"
    }

    /// A bare key where TOML allows one, a quoted key where it does not.
    ///
    /// Server names reach this from a config file Kytto did not write, so
    /// "allows one" has to be checked rather than assumed.
    public static func key(_ value: String) -> String {
        let bare = !value.isEmpty && value.unicodeScalars.allSatisfy { scalar in
            (scalar >= "A" && scalar <= "Z")
                || (scalar >= "a" && scalar <= "z")
                || (scalar >= "0" && scalar <= "9")
                || scalar == "_"
                || scalar == "-"
        }
        return bare ? value : string(value)
    }

    /// A dotted path, each component quoted only if it has to be.
    public static func path(_ components: [String]) -> String {
        components.map(key).joined(separator: ".")
    }
}

/// Renders a server definition as a TOML table block.
///
/// Shaped after what `codex mcp add` itself writes — a table per server with the
/// environment in a sub-table — so a config Kytto has edited still looks like
/// one Codex wrote, and a diff shows only what changed.
public enum TOMLBuilder {
    public static func serverBlock(
        name: String,
        under key: String,
        command: String?,
        args: [String],
        url: String?,
        env: [EnvEntry],
        enabled: Bool?
    ) -> String {
        var lines = ["[\(TOMLText.path([key, name]))]"]

        if let url, !url.isEmpty {
            lines.append("url = \(TOMLText.string(url))")
        } else if let command, !command.isEmpty {
            lines.append("command = \(TOMLText.string(command))")
            if !args.isEmpty {
                lines.append("args = \(TOMLText.array(args))")
            }
        }

        if let enabled {
            lines.append("enabled = \(TOMLText.bool(enabled))")
        }

        let realEnv = env.filter { !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
        if !realEnv.isEmpty {
            lines.append("")
            lines.append("[\(TOMLText.path([key, name, "env"]))]")
            for entry in realEnv {
                lines.append("\(TOMLText.key(entry.key)) = \(TOMLText.string(entry.value ?? ""))")
            }
        }

        return lines.joined(separator: "\n")
    }
}
