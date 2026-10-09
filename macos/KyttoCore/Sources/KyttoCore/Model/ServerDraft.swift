import Foundation

/// A server the user is describing, before it becomes a definition in a file.
///
/// Kept separate from `Server` because a draft is allowed to be incomplete and
/// wrong — that is the point of validating it. `Server` is always something that
/// really exists in a config.
public struct ServerDraft: Equatable, Sendable {
    public var name: String
    public var transport: Transport
    public var command: String
    public var args: [String]
    public var env: [EnvEntry]
    public var url: String

    public init(
        name: String = "",
        transport: Transport = .stdio,
        command: String = "",
        args: [String] = [],
        env: [EnvEntry] = [],
        url: String = ""
    ) {
        self.name = name
        self.transport = transport
        self.command = command
        self.args = args
        self.env = env
        self.url = url
    }

    /// Pre-fills a draft from a server that already exists, for editing.
    public init(editing server: Server) {
        name = server.name
        transport = server.transport
        command = server.command ?? ""
        args = server.args
        env = server.env
        url = server.url ?? ""
    }

    public var trimmedName: String {
        name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Fills in environment values the editor was never given.
    ///
    /// The UI only ever sees `{ key, hasValue }` — an API token does not cross
    /// the IPC boundary just because someone opened the edit form (§6). So a
    /// value of `nil` coming back means "leave this one as it was", and the real
    /// value is looked up here, natively, at the last moment.
    public func mergingSecrets(from server: Server) -> ServerDraft {
        let existing = Dictionary(
            server.env.map { ($0.key, $0.value) },
            uniquingKeysWith: { first, _ in first }
        )
        var merged = self
        merged.env = env.map { entry in
            guard entry.value == nil else { return entry }
            return EnvEntry(key: entry.key, value: existing[entry.key] ?? nil)
        }
        return merged
    }

    // MARK: - Validation

    public enum ValidationError: Error, Equatable, LocalizedError {
        case nameEmpty
        case nameHasWhitespace
        case nameTaken(String)
        case commandEmpty
        case urlEmpty
        case urlInvalid(String)
        case envKeyEmpty
        case envKeyInvalid(String)

        public var errorDescription: String? {
            switch self {
            case .nameEmpty:
                "Give the server a name."
            case .nameHasWhitespace:
                "Server names cannot contain spaces — they are used as keys in the config file."
            case .nameTaken(let name):
                "\"\(name)\" already exists in one of the selected clients."
            case .commandEmpty:
                "A stdio server needs a command to run."
            case .urlEmpty:
                "An HTTP or SSE server needs a URL."
            case .urlInvalid(let url):
                "\"\(url)\" is not a valid URL."
            case .envKeyEmpty:
                "An environment variable has no name."
            case .envKeyInvalid(let key):
                "\"\(key)\" is not a valid environment variable name. Use letters, numbers and underscores, and do not start with a number."
            }
        }
    }

    /// - Parameter takenNames: names already present in the clients being written
    ///   to, normalized. Pass the original name in `allowing` when editing, so a
    ///   server does not collide with itself.
    public func validate(takenNames: Set<String> = [], allowing existingName: String? = nil) -> [ValidationError] {
        var errors: [ValidationError] = []

        let name = trimmedName
        if name.isEmpty {
            errors.append(.nameEmpty)
        } else if name.rangeOfCharacter(from: .whitespacesAndNewlines) != nil {
            errors.append(.nameHasWhitespace)
        } else {
            let identity = Server.identity(for: name)
            let allowed = existingName.map(Server.identity(for:))
            if takenNames.contains(identity), identity != allowed {
                errors.append(.nameTaken(name))
            }
        }

        switch transport {
        case .stdio:
            if command.trimmingCharacters(in: .whitespaces).isEmpty {
                errors.append(.commandEmpty)
            }
        case .http, .sse:
            let url = self.url.trimmingCharacters(in: .whitespaces)
            if url.isEmpty {
                errors.append(.urlEmpty)
            } else if !Self.isValidRemoteURL(url) {
                errors.append(.urlInvalid(url))
            }
        }

        for entry in env {
            let key = entry.key.trimmingCharacters(in: .whitespacesAndNewlines)
            if key.isEmpty {
                errors.append(.envKeyEmpty)
            } else if !Self.isValidEnvironmentKey(key) {
                errors.append(.envKeyInvalid(key))
            }
        }

        return errors
    }

    private static func isValidRemoteURL(_ text: String) -> Bool {
        guard let components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host,
              !host.isEmpty
        else { return false }
        return true
    }

    private static func isValidEnvironmentKey(_ key: String) -> Bool {
        guard let first = key.unicodeScalars.first,
              CharacterSet.letters.union(CharacterSet(charactersIn: "_")).contains(first)
        else { return false }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_"))
        return key.unicodeScalars.dropFirst().allSatisfy(allowed.contains)
    }

    // MARK: - Rendering

    /// The definition as it will appear in a config file.
    ///
    /// `type` is written only for HTTP and SSE. Every client understands a bare
    /// `command` as stdio, but not all of them have always understood
    /// `"type": "stdio"`, so the quieter form is the portable one.
    public func definition(baseIndent: String, unit: String) -> String {
        var fields: [JSONBuildValue.Field] = []

        switch transport {
        case .stdio:
            fields.append(.init("command", .string(command.trimmingCharacters(in: .whitespaces))))
            if !args.isEmpty {
                fields.append(.init("args", .array(args.map { .string($0) })))
            }
        case .http, .sse:
            fields.append(.init("type", .string(transport.rawValue)))
            fields.append(.init("url", .string(url.trimmingCharacters(in: .whitespaces))))
        }

        let realEnv = env.filter { !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
        if !realEnv.isEmpty {
            fields.append(
                .init("env", .object(realEnv.map { .init($0.key, .string($0.value ?? "")) }))
            )
        }

        return JSONBuilder.render(.object(fields), baseIndent: baseIndent, unit: unit)
    }

    /// The same definition, spelled as a TOML table block.
    ///
    /// Transport is carried by which key is present rather than by a `type`
    /// field: Codex reads `url` as remote and `command` as stdio, and writing
    /// both would be describing two servers.
    ///
    /// - Parameter enabled: pass `false` only to keep a server that is already
    ///   switched off switched off. Editing a definition must not quietly turn
    ///   it back on, and absent means on.
    public func tomlBlock(under key: String, enabled: Bool?) -> String {
        let isRemote = transport != .stdio
        return TOMLBuilder.serverBlock(
            name: trimmedName,
            under: key,
            command: isRemote ? nil : command.trimmingCharacters(in: .whitespaces),
            args: args,
            url: isRemote ? url.trimmingCharacters(in: .whitespaces) : nil,
            env: env,
            enabled: enabled
        )
    }
}

// MARK: - Editing in place (§6.3)

extension ServerDraft {
    /// Applies the draft to a definition that already exists, touching only
    /// what a draft describes.
    ///
    /// A draft knows five things: transport, command, args, url and env. A real
    /// definition can hold more — `headers` carrying an Authorization token,
    /// `cwd`, `envFile`, a key some client added last month — and rebuilding the
    /// object from the draft deleted all of it on a Save that changed nothing.
    /// So each of the five is compared with what the file says and spliced only
    /// where it differs. A Save with no changes writes no bytes.
    ///
    /// - Parameter path: where the definition object sits — `[serversKey, name]`
    ///   in a config file, `[]` for a parked copy, which is the object alone.
    public func splicingJSON(into source: String, at path: [String]) throws -> String {
        var text = source
        let unit = IndentStyle.detect(in: source).text

        func definition() throws -> (document: JSONDocument, node: JSONNode, members: [JSONMember]) {
            let document = try JSONDocument.parse(text)
            guard let node = document.value(at: path) else { throw JSONEditError.pathNotFound(path) }
            guard let members = node.members else { throw JSONEditError.notAnObject(path) }
            return (document, node, members)
        }

        let initial = try definition()
        // A field the draft describes, spelled twice, would make every
        // comparison below a guess about which copy the client reads.
        for key in Self.describedJSONFields {
            _ = try JSONDocument.soleMember(key, in: initial.members, at: path)
        }
        let existing = ServerFields(node: initial.node)
        let existingTransport = Self.transport(of: existing)

        /// Rendered to sit among the definition's own members: on one line if
        /// the definition is written on one line, indented like its members
        /// otherwise.
        func render(_ value: JSONBuildValue) throws -> String {
            let current = try definition()
            let oneLine = !current.members.isEmpty
                && !current.document.containsNewline(from: current.node.span.start, to: current.node.span.end)
            if oneLine, case .object(let fields) = value {
                return JSONText.inlineObject(fields.map { ($0.key, JSONBuilder.render($0.value)) })
            }
            let indent = current.members.first.map { current.document.lineIndent(at: $0.span.start) }
                ?? current.document.lineIndent(at: current.node.span.start) + unit
            return JSONBuilder.render(value, baseIndent: oneLine ? "" : indent, unit: unit)
        }
        func set(_ key: String, _ raw: String, under sub: [String] = []) throws {
            text = try JSONDocument.parse(text).settingMember(key, at: path + sub, to: raw)
        }
        func remove(_ key: String, under sub: [String] = []) throws {
            text = try JSONDocument.parse(text).removingMember(key, at: path + sub)
        }

        // Fields that describe the other transport go only when the transport
        // actually changes; a definition that already carries both keeps both.
        if transport == .stdio, existingTransport != .stdio {
            try remove("url")
            try remove("type")
        } else if transport != .stdio, existingTransport == .stdio {
            try remove("command")
            try remove("args")
        }

        switch transport {
        case .stdio:
            let command = command.trimmingCharacters(in: .whitespaces)
            if existing.command != command { try set("command", JSONText.string(command)) }

            let argsNode = try definition().node["args"]
            if args.isEmpty {
                if let argsNode, argsNode.elements?.isEmpty != true { try remove("args") }
            } else if argsNode?.elements == nil || existing.args != args {
                try set("args", render(.array(args.map { .string($0) })))
            }
        case .http, .sse:
            if existingTransport != transport { try set("type", JSONText.string(transport.rawValue)) }
            let url = url.trimmingCharacters(in: .whitespaces)
            if existing.url != url { try set("url", JSONText.string(url)) }
        }

        // Environment, key by key. A value of nil is the UI saying "keep what is
        // there" (§6), which for a key already in this file means not touching
        // it at all — including values that are not strings.
        let wanted = realEnvironment
        let envNode = try definition().node["env"]
        if let envMembers = envNode?.members {
            if wanted.isEmpty {
                if !envMembers.isEmpty { try remove("env") }
            } else {
                let keys = Set(wanted.map(\.key))
                for member in envMembers where !keys.contains(member.key) {
                    try remove(member.key, under: ["env"])
                }
                for entry in wanted {
                    let current = try definition().node["env"]?.members?.filter { $0.key == entry.key }
                    guard current?.count ?? 0 <= 1 else {
                        throw JSONEditError.duplicateKey(path + ["env", entry.key])
                    }
                    if let present = current?.first {
                        guard let value = entry.value, present.value.stringValue != value else { continue }
                        try set(entry.key, JSONText.string(value), under: ["env"])
                    } else {
                        try set(entry.key, JSONText.string(entry.value ?? ""), under: ["env"])
                    }
                }
            }
        } else if !wanted.isEmpty {
            try set("env", render(.object(wanted.map { .init($0.key, .string($0.value ?? "")) })))
        }

        return text
    }

    /// The same, for a server's table in a TOML file (§6.3).
    ///
    /// `startup_timeout_sec`, `enabled_tools`, `http_headers`,
    /// `bearer_token_env_var`, `enabled = false` and comments all stay exactly
    /// where they are. Only the keys a draft describes are spliced, each inside
    /// the table that holds it.
    public func splicingTOML(into source: String, server name: String, under key: String) throws -> String {
        var text = source
        let path = [key, name]
        let envPath = path + ["env"]

        let document = try TOMLDocument.parse(text)
        if document.table(at: [key])?.pair(name)?.value.inlinePairs != nil {
            throw TOMLEditError.inlineDefinition(name)
        }
        guard let table = document.table(at: path) else { throw TOMLEditError.tableNotFound(path) }
        for pair in table.pairs where pair.key.count > 1 && Self.describedJSONFields.contains(pair.key[0]) {
            throw TOMLEditError.unsupportedLayout(path + pair.key)
        }
        let envTable = document.table(at: envPath)
        let existing = ServerFields(table: table, envTable: envTable)
        let existingTransport = Self.transport(of: existing)

        func set(_ field: String, _ raw: String, in tablePath: [String] = path) throws {
            text = try TOMLDocument.parse(text).settingValue(field, inTable: tablePath, to: raw)
        }
        func remove(_ field: String, in tablePath: [String] = path) throws {
            text = try TOMLDocument.parse(text).removingValue(field, inTable: tablePath)
        }

        // Codex reads `url` as remote and `command` as stdio, so a transport
        // change is a change of key. A `type` is only rewritten if one is there.
        if transport != existingTransport {
            if transport == .stdio {
                try remove("url")
            } else {
                try remove("command")
                try remove("args")
            }
            if table.pair("type") != nil {
                if transport == .stdio {
                    try remove("type")
                } else {
                    try set("type", TOMLText.string(transport.rawValue))
                }
            }
        }

        switch transport {
        case .stdio:
            let command = command.trimmingCharacters(in: .whitespaces)
            if existing.command != command { try set("command", TOMLText.string(command)) }
            let argsValue = try TOMLDocument.parse(text).table(at: path)?.value("args")
            if args.isEmpty {
                if let argsValue, argsValue.elements?.isEmpty != true { try remove("args") }
            } else if argsValue?.elements == nil || existing.args != args {
                try set("args", TOMLText.array(args))
            }
        case .http, .sse:
            let url = url.trimmingCharacters(in: .whitespaces)
            if existing.url != url { try set("url", TOMLText.string(url)) }
        }

        let wanted = realEnvironment
        if let envTable {
            if wanted.isEmpty {
                if !envTable.pairs.isEmpty { text = try TOMLDocument.parse(text).removingTable(at: envPath) }
            } else {
                let keys = Set(wanted.map(\.key))
                for pair in envTable.pairs {
                    guard let name = pair.name, !keys.contains(name) else { continue }
                    try remove(name, in: envPath)
                }
                for entry in wanted {
                    let present = try TOMLDocument.parse(text).table(at: envPath)?.pairs
                        .filter { $0.key == [entry.key] } ?? []
                    guard present.count <= 1 else { throw TOMLEditError.ambiguousValue(envPath + [entry.key]) }
                    if let pair = present.first {
                        guard let value = entry.value, pair.value.stringValue != value else { continue }
                        try set(entry.key, TOMLText.string(value), in: envPath)
                    } else {
                        try set(entry.key, TOMLText.string(entry.value ?? ""), in: envPath)
                    }
                }
            }
        } else if table.value("env")?.inlinePairs != nil {
            // An inline `env = { … }` is one value. It is respelt only if
            // something in it changed, and a kept value keeps its own text.
            let current = try TOMLDocument.parse(text)
            guard let envValue = current.table(at: path)?.value("env"),
                  let pairs = envValue.inlinePairs
            else { return text }
            if wanted.isEmpty {
                if !pairs.isEmpty { try remove("env") }
            } else {
                // Changed means a different set of keys or a value that decodes
                // differently — never a different spelling of the same value
                // (`'v'` against `"v"`, spacing), which would rewrite on a no-op save.
                let sameKeys = wanted.map(\.key) == pairs.map { $0.name ?? "" }
                let sameValues = wanted.allSatisfy { entry in
                    guard let value = entry.value else { return true }
                    return pairs.last { $0.name == entry.key }?.value.stringValue == value
                }
                if !(sameKeys && sameValues) {
                    let spelled = wanted.map { entry -> String in
                        let kept = pairs.last { $0.name == entry.key }
                        let raw: String
                        if let kept, entry.value == nil || kept.value.stringValue == entry.value {
                            raw = current.slice(kept.value.span)
                        } else {
                            raw = TOMLText.string(entry.value ?? "")
                        }
                        return "\(TOMLText.key(entry.key)) = \(raw)"
                    }
                    text = current.replacing(envValue.span, with: "{ \(spelled.joined(separator: ", ")) }")
                }
            }
        } else if !wanted.isEmpty {
            // Where `codex mcp add` puts it: a sub-table right after the server's own.
            let current = try TOMLDocument.parse(text)
            guard let anchor = current.endOfTableContent(at: path) else { throw TOMLEditError.tableNotFound(path) }
            let lines = ["", "[\(TOMLText.path(envPath))]"]
                + wanted.map { "\(TOMLText.key($0.key)) = \(TOMLText.string($0.value ?? ""))" }
            text = current.replacing(TOMLSpan(start: anchor, end: anchor), with: "\n" + lines.joined(separator: "\n"))
        }

        return text
    }

    /// The keys a draft speaks for. Everything else in a definition is the
    /// user's, and an edit leaves it alone.
    private static let describedJSONFields = ["command", "args", "url", "type", "env"]

    private var realEnvironment: [EnvEntry] {
        env.filter { !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
    }

    private static func transport(of fields: ServerFields) -> Transport {
        fields.makeServer(name: "", origin: .configFile(.cursor), isBundled: false, sourceText: "").transport
    }
}
