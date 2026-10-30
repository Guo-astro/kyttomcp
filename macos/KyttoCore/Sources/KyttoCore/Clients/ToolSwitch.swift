import Foundation

/// Where a client lets a single tool of a server be switched off (§7.11).
///
/// A server used to be all or nothing. Several clients can say more than that
/// in their own files, each differently — which is why this is data on the
/// descriptor beside the off switch for the whole server, and why a client
/// without one falls back to Gateway's allow list instead.
public enum ToolSwitch: Sendable {
    /// Arrays of tool names on the server's own entry. Codex
    /// (`disabled_tools`, `enabled_tools`) and Antigravity (`disabledTools`,
    /// `enabledTools`). The deny list applies after the allow list.
    case entryLists(deny: String, allow: String)

    /// A top-level map from `<server>_<tool>` globs to booleans. OpenCode's
    /// `tools`; `false` keeps a tool from every agent.
    case prefixedToolMap(key: String)

    /// Permission deny rules `mcp__<server>__<tool>` in another file. A rule
    /// naming a whole tool removes it from Claude Code's context rather than
    /// only refusing the call (https://code.claude.com/docs/en/permissions).
    case permissionRules(file: PlatformPath, path: [String])

    /// What switching a tool off writes, in one sentence for the client screen.
    public var summary: String {
        switch self {
        case .entryLists(let deny, _):
            "Switching a tool off adds it to \(deny) on the server's own entry."
        case .prefixedToolMap(let key):
            "Switching a tool off writes <server>_<tool>: false into the top-level \(key) map."
        case .permissionRules:
            "Switching a tool off adds an mcp__<server>__<tool> deny rule to permissions in ~/.claude/settings.json, which removes the tool from Claude Code's context."
        }
    }
}

/// One client's rules about which of a server's tools stay on, as its files
/// state them. Pure: it answers questions about tool names and never reads.
public struct ToolFilter: Equatable, Sendable {
    public struct Rule: Equatable, Sendable {
        /// In the client's own namespace: a bare tool name for entry lists,
        /// `<server>_<tool>` for OpenCode, `mcp__<server>__<tool>` for Claude Code.
        public let pattern: String
        public let keepsOn: Bool
    }

    /// Prepended to a sanitized tool name to put it in the rules' namespace.
    /// Empty for entry lists, which name tools as the server does.
    public let prefix: String
    /// In file order. Later rules win over earlier ones of the same kind.
    public let rules: [Rule]
    /// Patterns that address the whole server rather than tools inside it —
    /// Claude Code's bare `mcp__<server>`.
    public let wholeServerPatterns: [String]
    /// When the client keeps an allow list, only these tools stay on.
    public let allowList: [String]?
    /// Whether a tool's own rule can say "on" over a broader rule. OpenCode's
    /// map can (`"github_x": true` beside `"github_*": false`); Claude Code's
    /// deny rules cannot be answered by anything but removing them.
    public let ownRuleOverrides: Bool

    public init(
        prefix: String = "",
        rules: [Rule] = [],
        wholeServerPatterns: [String] = [],
        allowList: [String]? = nil,
        ownRuleOverrides: Bool = false
    ) {
        self.prefix = prefix
        self.rules = rules
        self.wholeServerPatterns = wholeServerPatterns
        self.allowList = allowList
        self.ownRuleOverrides = ownRuleOverrides
    }

    public var isEmpty: Bool { rules.isEmpty && wholeServerPatterns.isEmpty && allowList == nil }

    /// The name a rule has to match to speak about `tool`.
    public func ruleName(for tool: String) -> String {
        prefix.isEmpty ? tool : prefix + Self.sanitized(tool)
    }

    /// Whether the client keeps `tool` from its model.
    public func isOff(_ tool: String) -> Bool {
        if let allowList, !allowList.contains(tool) { return true }
        if !wholeServerPatterns.isEmpty { return true }
        let name = ruleName(for: tool)
        if let exact = rules.last(where: { $0.pattern == name }) { return !exact.keepsOn }
        if let glob = rules.last(where: { $0.pattern != name && Self.glob($0.pattern, matches: name) }) {
            return !glob.keepsOn
        }
        return false
    }

    /// A rule broader than this tool's own that would keep it off once its own
    /// rule is gone. Nil when nothing but the tool's own rule (or none) applies.
    public func blockingPattern(for tool: String) -> String? {
        if let whole = wholeServerPatterns.first { return whole }
        let name = ruleName(for: tool)
        let glob = rules.last { $0.pattern != name && Self.glob($0.pattern, matches: name) }
        if let glob, !glob.keepsOn { return glob.pattern }
        return nil
    }

    /// The broader rule keeping an off tool off that a switch for this one
    /// tool cannot answer, or nil when switching it on is possible.
    public func lockingRule(for tool: String) -> String? {
        guard isOff(tool) else { return nil }
        if !wholeServerPatterns.isEmpty { return wholeServerPatterns.first }
        return ownRuleOverrides ? nil : blockingPattern(for: tool)
    }

    /// Tool → locking rule, for every tool in `tools` that has one. Servers can
    /// list a name twice; the first wins rather than trapping.
    public func lockedTools(among tools: [String]) -> [String: String] {
        Dictionary(
            tools.compactMap { tool in lockingRule(for: tool).map { (tool, $0) } },
            uniquingKeysWith: { first, _ in first }
        )
    }

    /// The names of `tools` this client keeps from its model.
    public func offTools(among tools: [String]) -> [String] {
        tools.filter(isOff)
    }

    // MARK: - Names

    /// How OpenCode and Claude Code turn a server or tool name into part of a
    /// tool identifier: anything outside letters, digits, `_` and `-` becomes `_`.
    public static func sanitized(_ name: String) -> String {
        String(name.unicodeScalars.map { scalar in
            let allowed = (scalar.isASCII && (CharacterSet.alphanumerics.contains(scalar))) || scalar == "_" || scalar == "-"
            return allowed ? Character(scalar) : "_"
        })
    }

    /// `*` matches any run of characters and `?` exactly one; everything else
    /// matches itself.
    static func glob(_ pattern: String, matches name: String) -> Bool {
        let p = Array(pattern), n = Array(name)
        var pi = 0, ni = 0, star = -1, mark = 0
        while ni < n.count {
            if pi < p.count, p[pi] == "?" || p[pi] == n[ni] {
                pi += 1; ni += 1
            } else if pi < p.count, p[pi] == "*" {
                star = pi; mark = ni; pi += 1
            } else if star >= 0 {
                pi = star + 1; mark += 1; ni = mark
            } else {
                return false
            }
        }
        while pi < p.count, p[pi] == "*" { pi += 1 }
        return pi == p.count
    }
}

extension ToolSwitch {
    /// The server-name part of a rule, for the server spelled `serverKey` in
    /// the client's own file.
    func prefix(for serverKey: String) -> String {
        switch self {
        case .entryLists: ""
        case .prefixedToolMap: ToolFilter.sanitized(serverKey) + "_"
        case .permissionRules: "mcp__" + ToolFilter.sanitized(serverKey) + "__"
        }
    }

    static func isGlob(_ pattern: String) -> Bool {
        pattern.contains("*") || pattern.contains("?")
    }

    /// Entry lists read off a JSON entry.
    static func entryFilter(deny: String, allow: String, in entry: JSONNode) -> ToolFilter {
        let denied = entry[deny]?.elements?.compactMap(\.stringValue) ?? []
        return ToolFilter(
            rules: denied.map { .init(pattern: $0, keepsOn: false) },
            allowList: entry[allow]?.elements?.compactMap(\.stringValue)
        )
    }

    /// Entry lists read off a TOML table.
    static func entryFilter(deny: String, allow: String, in table: TOMLTable) -> ToolFilter {
        let denied = table.value(deny)?.elements?.compactMap(\.stringValue) ?? []
        return ToolFilter(
            rules: denied.map { .init(pattern: $0, keepsOn: false) },
            allowList: table.value(allow)?.elements?.compactMap(\.stringValue)
        )
    }

    /// OpenCode's map: the rules that can match this server's tools.
    static func mapFilter(_ map: JSONNode?, serverKey: String) -> ToolFilter {
        let prefix = ToolSwitch.prefixedToolMap(key: "").prefix(for: serverKey)
        let rules = (map?.members ?? []).compactMap { member -> ToolFilter.Rule? in
            guard let value = member.value.boolValue else { return nil }
            // Only rules that can name one of this server's tools: its prefix,
            // or a glob, which is matched against each full name later.
            return member.key.hasPrefix(prefix) || Self.isGlob(member.key)
                ? .init(pattern: member.key, keepsOn: value) : nil
        }
        return ToolFilter(prefix: prefix, rules: rules, ownRuleOverrides: true)
    }

    /// Claude Code's deny rules that can reach this server's tools. Allow
    /// rules only skip a prompt, so they never switch a tool back on.
    static func permissionFilter(_ deny: [String], serverKey: String) -> ToolFilter {
        let prefix = ToolSwitch.permissionRules(file: PlatformPath(darwin: "", windows: ""), path: []).prefix(for: serverKey)
        let whole = "mcp__" + ToolFilter.sanitized(serverKey)
        var rules: [ToolFilter.Rule] = []
        var wholeServer: [String] = []
        for rule in deny where !rule.contains("(") {
            if rule == whole || rule == prefix + "*" || rule == "mcp__*" || rule == "*" {
                wholeServer.append(rule)
            } else if rule.hasPrefix(prefix) || (rule.hasPrefix("mcp__") && Self.isGlob(rule)) {
                rules.append(.init(pattern: rule, keepsOn: false))
            }
        }
        return ToolFilter(prefix: prefix, rules: rules, wholeServerPatterns: wholeServer)
    }
}

// MARK: - Renaming a server

extension ToolSwitch {
    /// OpenCode's `tools` keys for `oldServer`, respelt for `newServer`.
    ///
    /// The rules name the server, so a rename that left them behind would
    /// switch every tool the user had switched off back on. A key already
    /// under the new name gives way to the one that moves: the moving rules
    /// are what the matrix showed for this server. Keys that belong to another
    /// server whose name merely starts the same way (`github_enterprise_*`
    /// beside `github`) stay where they are.
    static func renamingMapRules(
        in text: String,
        mapKey: String,
        serversKey: String,
        from oldServer: String,
        to newServer: String
    ) throws -> String {
        let oldPrefix = ToolFilter.sanitized(oldServer) + "_"
        let newPrefix = ToolFilter.sanitized(newServer) + "_"
        guard oldPrefix != newPrefix else { return text }
        let document = try JSONDocument.parse(text)
        guard let members = document.root[mapKey]?.members else { return text }

        let otherPrefixes = (document.root[serversKey]?.keys ?? [])
            .map { ToolFilter.sanitized($0) + "_" }
            .filter { $0 != oldPrefix && $0 != newPrefix && $0.hasPrefix(oldPrefix) }
        func moves(_ key: String) -> Bool {
            key.hasPrefix(oldPrefix) && !otherPrefixes.contains(where: key.hasPrefix)
        }
        func renamed(_ key: String) -> String { newPrefix + key.dropFirst(oldPrefix.count) }

        let moving = members.map(\.key).filter(moves)
        guard !moving.isEmpty else { return text }
        var result = text
        for key in Set(moving.map(renamed)) where !moving.contains(key) && members.contains(where: { $0.key == key }) {
            result = try JSONDocument.parse(result).removingMember(key, at: [mapKey])
        }
        let current = try JSONDocument.parse(result)
        let edits = (current.root[mapKey]?.members ?? []).filter { moves($0.key) }.map { member in
            (span: member.keySpan, replacement: JSONText.string(renamed(member.key)))
        }
        return current.replacing(edits)
    }

    /// Claude Code's `mcp__<server>…` permission rules for `oldServer`,
    /// respelt for `newServer`; every other rule is left as written.
    static func renamingPermissionRules(
        in text: String,
        path: [String],
        from oldServer: String,
        to newServer: String
    ) throws -> String {
        let old = "mcp__" + ToolFilter.sanitized(oldServer)
        let new = "mcp__" + ToolFilter.sanitized(newServer)
        guard old != new else { return text }
        let document = try JSONDocument.parse(text)
        let edits = (document.value(at: path)?.elements ?? []).compactMap { element -> (span: JSONSpan, replacement: String)? in
            guard let rule = element.stringValue else { return nil }
            let lowered = rule.lowercased(), prefix = old.lowercased()
            guard lowered == prefix || lowered.hasPrefix(prefix + "__") else { return nil }
            return (element.span, JSONText.string(new + rule.dropFirst(old.count)))
        }
        return edits.isEmpty ? text : document.replacing(edits)
    }
}
