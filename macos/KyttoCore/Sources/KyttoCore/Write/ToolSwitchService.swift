import Foundation

public struct ToolSwitchResult: Sendable {
    public let clientID: ClientID
    public let serverName: String
    /// The tools now off in this client, among those the caller named.
    public let disabledTools: [String]
    public let requiresRestart: Bool
    public let backupID: String?
    public let pathDisplay: String
}

public enum ToolSwitchError: Error, LocalizedError, Equatable {
    case notSupported(client: String)
    case notInClient(server: String, client: String)
    case bundled(String)
    /// A rule the user wrote for more than this tool keeps it off; changing
    /// that rule would change what it says about other tools too.
    case blockedByRule(tool: String, rule: String)
    case inlineDefinition(String)
    case noKnownTools(String)

    public var errorDescription: String? {
        switch self {
        case .notSupported(let client):
            "\(client) has no setting for single tools. Use Gateway mode to choose which tools it sees."
        case .notInClient(let server, let client):
            "“\(server)” is not in \(client)'s configuration, so there is nothing to switch tools on."
        case .bundled(let name):
            "“\(name)” comes from an installed plugin or extension. Choose its tools in the client that installed it."
        case .blockedByRule(let tool, let rule):
            "“\(tool)” is kept off by the rule \(rule), which covers other tools too. Change that rule in the file to switch this tool back on."
        case .inlineDefinition(let name):
            "“\(name)” is written as an inline table in config.toml. Move it into its own [mcp_servers.\(name)] table to switch its tools."
        case .noKnownTools(let name):
            "Kytto does not know “\(name)”'s tools yet. Run a health check first."
        }
    }
}

/// Switches single tools of a server off and on in one client (§7.11).
///
/// One call states the whole set: the tools named in `tools` end up off
/// exactly when they are in `disabled`, and nothing else in the file moves.
/// That makes a save from the tool picker one guarded write with one backup,
/// and Undo the same call with the previous set.
public struct ToolSwitchService: Sendable {
    private let home: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let ledger: DigestLedger
    private let descriptors: [ClientDescriptor]

    public init(
        home: URL,
        writer: ConfigWriter,
        ledger: DigestLedger,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        self.home = home
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        self.writer = writer
        self.ledger = ledger
        self.descriptors = descriptors
    }

    public func setDisabledTools(
        _ disabled: Set<String>,
        among tools: [String],
        server: Server,
        in clientID: ClientID
    ) throws -> ToolSwitchResult {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap,
              let toolSwitch = descriptor.toolSwitch
        else {
            let name = descriptors.first { $0.id == clientID }?.displayName ?? clientID.rawValue
            throw ToolSwitchError.notSupported(client: name)
        }
        if server.definitionsByClient[clientID]?.isBundled == true {
            throw ToolSwitchError.bundled(server.name)
        }
        guard (server.enabledIn[clientID] ?? .absent) != .absent else {
            throw ToolSwitchError.notInClient(server: server.name, client: descriptor.displayName)
        }
        let tools = Array(Set(tools)).sorted()
        guard !tools.isEmpty else { throw ToolSwitchError.noKnownTools(server.name) }
        let disabled = disabled.intersection(tools)

        let configURL = resolver.resolveServerMap(source.file, for: clientID)
        let configDisplay = resolver.displayServerMap(source.file, for: clientID)
        let receipt: WriteReceipt
        let url: URL

        switch toolSwitch {
        case .entryLists(let deny, let allow):
            url = configURL
            receipt = try source.format == .toml
                ? writer.edit(url: url, clientID: clientID, pathDisplay: configDisplay, expecting: ledger.digest(for: url), as: TOMLDocument.self) { document in
                    guard let name = try document.serverName(for: server.id, under: source.serversKey) else {
                        throw ToolSwitchError.notInClient(server: server.name, client: descriptor.displayName)
                    }
                    let path = [source.serversKey, name]
                    guard let table = document.table(at: path) else { throw ToolSwitchError.inlineDefinition(name) }
                    let current = ToolSwitch.entryFilter(deny: deny, allow: allow, in: table)
                    let plan = Self.listPlan(current, disabled: disabled, tools: tools)
                    var text = document.sourceText
                    for (key, list, wasPresent) in [(deny, plan.deny, table.value(deny) != nil), (allow, plan.allow, table.value(allow) != nil)] {
                        guard let list else { continue }
                        if list.isEmpty, key == deny {
                            if wasPresent { text = try TOMLDocument.parse(text).removingValue(key, inTable: path) }
                        } else {
                            text = try TOMLDocument.parse(text).settingValue(key, inTable: path, to: TOMLText.array(list))
                        }
                    }
                    return text
                }
                : writer.edit(url: url, clientID: clientID, pathDisplay: configDisplay, expecting: ledger.digest(for: url)) { document in
                    guard let name = try document.serverKey(for: server.id, under: source.serversKey) else {
                        throw ToolSwitchError.notInClient(server: server.name, client: descriptor.displayName)
                    }
                    let path = [source.serversKey, name]
                    guard let entry = document.value(at: path), let members = entry.members else {
                        throw JSONEditError.notAnObject(path)
                    }
                    for key in [deny, allow] { _ = try JSONDocument.soleMember(key, in: members, at: path) }
                    let current = ToolSwitch.entryFilter(deny: deny, allow: allow, in: entry)
                    let plan = Self.listPlan(current, disabled: disabled, tools: tools)
                    var text = document.sourceText
                    for (key, list) in [(deny, plan.deny), (allow, plan.allow)] {
                        guard let list else { continue }
                        if list.isEmpty, key == deny {
                            text = try JSONDocument.parse(text).removingMember(key, at: path)
                        } else {
                            text = try JSONDocument.parse(text).settingMember(key, at: path, to: Self.inlineArray(list))
                        }
                    }
                    return text
                }

        case .prefixedToolMap(let mapKey):
            url = configURL
            receipt = try writer.edit(url: url, clientID: clientID, pathDisplay: configDisplay, expecting: ledger.digest(for: url)) { document in
                guard let name = try document.serverKey(for: server.id, under: source.serversKey) else {
                    throw ToolSwitchError.notInClient(server: server.name, client: descriptor.displayName)
                }
                if let members = document.root[mapKey]?.members {
                    // Two spellings of one key would make "which one wins" a guess.
                    for key in Set(members.map(\.key)) { _ = try JSONDocument.soleMember(key, in: members, at: [mapKey]) }
                }
                let filter = ToolSwitch.mapFilter(document.root[mapKey], serverKey: name)
                var text = document.sourceText
                for tool in tools {
                    let wantOff = disabled.contains(tool)
                    guard wantOff != filter.isOff(tool) else { continue }
                    let key = filter.ruleName(for: tool)
                    if wantOff {
                        text = try JSONDocument.parse(text).settingMember(key, at: [mapKey], to: JSONText.bool(false))
                    } else {
                        if let rule = filter.blockingPattern(for: tool) {
                            throw ToolSwitchError.blockedByRule(tool: tool, rule: rule)
                        }
                        text = try JSONDocument.parse(text).removingMember(key, at: [mapKey])
                    }
                }
                return text
            }

        case .permissionRules(let file, let path):
            // The rule names the server as the config file spells it.
            guard let name = try (try? JSONDocument.parse(contentsOf: configURL))?
                .serverKey(for: server.id, under: source.serversKey)
            else { throw ToolSwitchError.notInClient(server: server.name, client: descriptor.displayName) }
            url = file.resolve(home: home)
            receipt = try writer.edit(url: url, clientID: clientID, pathDisplay: file.displayString(), expecting: ledger.digest(for: url)) { document in
                let rules = document.value(at: path)?.elements?.compactMap(\.stringValue) ?? []
                let filter = ToolSwitch.permissionFilter(rules, serverKey: name)
                var text = document.sourceText
                for tool in tools {
                    let wantOff = disabled.contains(tool)
                    guard wantOff != filter.isOff(tool) else { continue }
                    let rule = filter.ruleName(for: tool)
                    if wantOff {
                        text = try JSONDocument.parse(text).appendingElement(JSONText.string(rule), toArrayAt: path)
                    } else {
                        if let blocking = filter.blockingPattern(for: tool) {
                            throw ToolSwitchError.blockedByRule(tool: tool, rule: blocking)
                        }
                        text = try JSONDocument.parse(text).removingElements(fromArrayAt: path) { $0.stringValue == rule }
                    }
                }
                return text
            }
        }
        ledger.record(receipt.digest, for: url)

        return ToolSwitchResult(
            clientID: clientID,
            serverName: server.name,
            disabledTools: disabled.sorted(),
            requiresRestart: receipt.didWrite,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay
        )
    }

    /// The new deny and allow lists, nil where a list stays as it is.
    ///
    /// With an allow list the allow list decides: a tool switched on joins it
    /// and one switched off leaves it, and a tool switched on also leaves the
    /// deny list, which applies after it. Without one only the deny list moves.
    /// Names the caller did not mention keep their place.
    static func listPlan(_ filter: ToolFilter, disabled: Set<String>, tools: [String]) -> (deny: [String]?, allow: [String]?) {
        let deny = filter.rules.map(\.pattern)
        let known = Set(tools)
        var newDeny = deny.filter { !known.contains($0) || disabled.contains($0) }
        var newAllow: [String]? = nil
        if let allow = filter.allowList {
            let kept = allow.filter { !known.contains($0) || !disabled.contains($0) }
            newAllow = kept + tools.filter { !disabled.contains($0) && !kept.contains($0) }
        } else {
            newDeny += tools.filter { disabled.contains($0) && !newDeny.contains($0) }
        }
        return (newDeny == deny ? nil : newDeny, newAllow == filter.allowList ? nil : newAllow)
    }

    private static func inlineArray(_ names: [String]) -> String {
        "[" + names.map(JSONText.string).joined(separator: ", ") + "]"
    }
}
