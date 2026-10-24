import Foundation

public struct GatewayMigrationPreview: Equatable, Sendable {
    public let routeID: String
    public let serverID: String
    public let serverName: String
    public let clientID: ClientID
    public let pathDisplay: String
    public let formatDisplay: String
    /// A structurally faithful view with all environment values redacted.
    public let directDefinitionPreview: String
    /// The exact replacement definition that will be written.
    public let gatewayDefinitionPreview: String
    public let environmentKeys: [String]
}

public struct GatewayMigrationResult: Sendable {
    public let route: GatewayRoute
    public let backupID: String?
    public let pathDisplay: String
}

public enum GatewayMigrationError: Error, LocalizedError, Equatable {
    case helperUnavailable(String)
    case unsupportedTransport
    case bundledServer
    case serverNotEnabled
    case routeAlreadyExists
    case definitionMissing
    case invalidDefinition(String)
    case routeMismatch
    case orphanedRouteInBackup(String)
    /// The app is running from a place that will not exist later — a disk
    /// image, or the random folder macOS translocates a quarantined app to.
    case helperLocationUnstable(String)

    public var errorDescription: String? {
        switch self {
        case .helperUnavailable(let path):
            "The bundled gateway helper is unavailable at \(path). Reinstall or rebuild Kytto."
        case .unsupportedTransport:
            "Gateway mode currently supports stdio servers only."
        case .bundledServer:
            "Installed extension and plugin servers cannot be migrated."
        case .serverNotEnabled:
            "Enable this server in the selected client before turning on Gateway mode."
        case .routeAlreadyExists:
            "This server already uses Gateway mode in that client."
        case .definitionMissing:
            "The selected client no longer contains this server definition."
        case .invalidDefinition(let reason):
            "This server cannot use Gateway mode: \(reason)"
        case .routeMismatch:
            "The client configuration no longer points at this gateway route. Kytto did not overwrite it."
        case .orphanedRouteInBackup(let id):
            "That backup points at gateway route \(id), but its credentials no longer exist. Restore a Direct backup instead."
        case .helperLocationUnstable(let path):
            "Kytto is running from \(path), which will not exist after a restart or once the disk image is ejected. Move Kytto to the Applications folder, open it from there, then turn on Gateway mode."
        }
    }
}

/// Opt-in Direct ↔ Gateway migration through the same guarded write path as all
/// other config changes (§3.3, §6).
public struct GatewayMigrationService: Sendable {
    private struct DirectDefinition {
        let serverName: String
        let sourceText: String
        let command: String
        let arguments: [String]
        let environment: [EnvEntry]
        let replacement: String
    }

    private let home: URL
    private let helperURL: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let ledger: DigestLedger
    private let routes: GatewayRouteStore
    private let secrets: any SecretStoring
    private let descriptors: [ClientDescriptor]

    public init(
        home: URL,
        helperURL: URL,
        writer: ConfigWriter,
        ledger: DigestLedger,
        routes: GatewayRouteStore,
        secrets: any SecretStoring,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        self.home = home
        self.helperURL = helperURL
        self.writer = writer
        self.ledger = ledger
        self.routes = routes
        self.secrets = secrets
        self.descriptors = descriptors
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
    }

    public func preview(
        server: Server,
        clientID: ClientID,
        routeID: String = UUID().uuidString.lowercased()
    ) throws -> GatewayMigrationPreview {
        let target = try target(for: server, clientID: clientID, requireNoRoute: true)
        guard UUID(uuidString: routeID) != nil else { throw GatewayRouteError.invalidRouteID(routeID) }
        let direct = try directDefinition(target: target, serverID: server.id, routeID: routeID)

        return GatewayMigrationPreview(
            routeID: routeID.lowercased(),
            serverID: server.id,
            serverName: direct.serverName,
            clientID: clientID,
            pathDisplay: target.pathDisplay,
            formatDisplay: target.source.format.displayName,
            directDefinitionPreview: redactedPreview(of: direct, format: target.source.format),
            gatewayDefinitionPreview: direct.replacement,
            environmentKeys: direct.environment.map(\.key).sorted()
        )
    }

    public func enable(
        server: Server,
        clientID: ClientID,
        routeID: String
    ) throws -> GatewayMigrationResult {
        let target = try target(for: server, clientID: clientID, requireNoRoute: true)
        guard UUID(uuidString: routeID) != nil else { throw GatewayRouteError.invalidRouteID(routeID) }
        let normalizedRouteID = routeID.lowercased()
        guard try !routes.all().contains(where: { $0.id == normalizedRouteID }) else {
            throw GatewayMigrationError.routeAlreadyExists
        }
        let direct = try directDefinition(target: target, serverID: server.id, routeID: normalizedRouteID)
        let definitionSecretID = GatewayRoute.directDefinitionSecretID(routeID: normalizedRouteID)
        let references = direct.environment.map {
            GatewayEnvironmentReference(
                key: $0.key,
                secretID: GatewayRoute.environmentSecretID(routeID: normalizedRouteID, key: $0.key)
            )
        }
        let route = GatewayRoute(
            id: normalizedRouteID,
            serverID: server.id,
            serverName: direct.serverName,
            clientID: clientID,
            gatewayCommand: helperURL.path,
            command: direct.command,
            arguments: direct.arguments,
            environment: references,
            directDefinitionSecretID: definitionSecretID
        )

        var storedSecretIDs: [String] = []
        do {
            try secrets.store(direct.sourceText, for: definitionSecretID)
            storedSecretIDs.append(definitionSecretID)
            for (entry, reference) in zip(direct.environment, references) {
                try secrets.store(entry.value ?? "", for: reference.secretID)
                storedSecretIDs.append(reference.secretID)
            }
            try routes.insert(route)

            let receipt = try writeReplacement(
                direct.replacement,
                target: target,
                routeID: nil,
                serverName: direct.serverName
            )
            ledger.record(receipt.digest, for: target.url)
            return GatewayMigrationResult(route: route, backupID: receipt.backupID, pathDisplay: receipt.pathDisplay)
        } catch {
            try? routes.remove(id: normalizedRouteID)
            for secretID in storedSecretIDs { try? secrets.delete(for: secretID) }
            throw error
        }
    }

    public func restore(routeID: String) throws -> GatewayMigrationResult {
        let route = try routes.route(id: routeID)
        guard let source = try secrets.value(for: route.directDefinitionSecretID) else {
            throw GatewayRouteError.missingSecret("original definition")
        }
        let target = try target(clientID: route.clientID)
        let receipt = try writeReplacement(source, target: target, routeID: route.id, serverName: route.serverName)
        ledger.record(receipt.digest, for: target.url)

        // The direct config is already safely restored. Cleanup is deliberately
        // best-effort so a Keychain deletion problem cannot turn that success
        // into a false failure or tempt the caller to repeat the config write.
        try? routes.remove(id: route.id)
        try? secrets.delete(for: route.directDefinitionSecretID)
        for reference in route.environment { try? secrets.delete(for: reference.secretID) }

        return GatewayMigrationResult(route: route, backupID: receipt.backupID, pathDisplay: receipt.pathDisplay)
    }

    /// Refuses a backup that would revive a helper route after its credentials
    /// were deliberately removed by Restore Direct.
    public func validateBackupRestore(_ text: String, clientID: ClientID) throws {
        let known = Set(try routes.all().map(\.id))
        for id in try routeIDs(in: text, clientID: clientID) where !known.contains(id) {
            throw GatewayMigrationError.orphanedRouteInBackup(id)
        }
    }

    /// A Direct backup can intentionally replace an active gateway definition.
    /// Once that write succeeds, remove route material the config no longer uses.
    public func reconcileAfterBackupRestore(clientID: ClientID) throws {
        let target = try target(clientID: clientID)
        let configured = Set(try routeIDs(in: target.sourceText, clientID: clientID))
        for route in try routes.all() where route.clientID == clientID && !configured.contains(route.id) {
            try? routes.remove(id: route.id)
            try? secrets.delete(for: route.directDefinitionSecretID)
            for reference in route.environment { try? secrets.delete(for: reference.secretID) }
        }
    }

    // MARK: - Targets and parsing

    private typealias Source = (
        file: PlatformPath,
        format: ConfigFormat,
        serversKey: String,
        enablement: EnablementStrategy
    )

    private struct Target {
        let clientID: ClientID
        let source: Source
        let url: URL
        let pathDisplay: String
        let sourceText: String
    }

    private func target(
        for server: Server,
        clientID: ClientID,
        requireNoRoute: Bool
    ) throws -> Target {
        guard FileManager.default.isExecutableFile(atPath: helperURL.path) else {
            throw GatewayMigrationError.helperUnavailable(helperURL.path)
        }
        // The helper's path is written into the client's config, so it has to
        // outlive this launch.
        if helperURL.path.contains("/AppTranslocation/") || helperURL.path.hasPrefix("/Volumes/") {
            throw GatewayMigrationError.helperLocationUnstable(helperURL.deletingLastPathComponent().path)
        }
        guard server.transport == .stdio else { throw GatewayMigrationError.unsupportedTransport }
        guard !server.isBundled else { throw GatewayMigrationError.bundledServer }
        guard server.enabledIn[clientID] == .enabled else { throw GatewayMigrationError.serverNotEnabled }
        if requireNoRoute, try routes.route(serverID: server.id, clientID: clientID) != nil {
            throw GatewayMigrationError.routeAlreadyExists
        }
        return try target(clientID: clientID)
    }

    private func target(clientID: ClientID) throws -> Target {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw AuthoringError.noSourceForClient(clientID) }

        let url = resolver.resolveServerMap(source.file, for: clientID)
        let display = resolver.displayServerMap(source.file, for: clientID)
        guard ConfigWriter.digest(of: url) == ledger.digest(for: url) else {
            throw ConfigWriteError.changedOnDisk(pathDisplay: display)
        }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            throw ConfigWriteError.unreadable(pathDisplay: display, reason: "not valid UTF-8")
        }
        return Target(clientID: clientID, source: source, url: url, pathDisplay: display, sourceText: text)
    }

    private func directDefinition(target: Target, serverID: String, routeID: String) throws -> DirectDefinition {
        switch target.source.format {
        case .json:
            let document = try JSONDocument.parse(target.sourceText)
            guard let map = document.value(at: [target.source.serversKey]),
                  let member = map.members?.first(where: { Server.identity(for: $0.key) == serverID })
            else { throw GatewayMigrationError.definitionMissing }
            let node = member.value
            let extra = node.keys.filter { !["command", "args", "env", "type"].contains($0) }
            if !extra.isEmpty { throw Self.unmodelled(extra) }
            if let type = node["type"], type.stringValue != "stdio" {
                throw GatewayMigrationError.invalidDefinition("it is not a stdio server")
            }
            guard let command = node["command"]?.stringValue, !command.isEmpty else {
                throw GatewayMigrationError.invalidDefinition("its command is missing or is not a string")
            }
            let arguments = node["args"]?.elements?.compactMap(\.stringValue) ?? []
            if let elements = node["args"]?.elements, arguments.count != elements.count {
                throw GatewayMigrationError.invalidDefinition("one of its arguments is not a string")
            }
            let environment = try jsonEnvironment(node["env"])
            try Self.refusePlaceholders(command: command, arguments: arguments, environment: environment)
            let unit = IndentStyle.detect(in: document.sourceText).text
            let replacement = ServerDraft(
                name: member.key,
                command: helperURL.path,
                args: ["--route", routeID]
            ).definition(baseIndent: document.lineIndent(at: member.value.span.start), unit: unit)
            return DirectDefinition(
                serverName: member.key,
                sourceText: document.slice(member.value.span),
                command: command,
                arguments: arguments,
                environment: environment,
                replacement: replacement
            )

        case .toml:
            let document = try TOMLDocument.parse(target.sourceText)
            guard let name = document.serverNames(under: target.source.serversKey)
                .first(where: { Server.identity(for: $0) == serverID }),
                  let table = document.table(at: [target.source.serversKey, name]),
                  let sourceText = document.serverDefinitionText(name, under: target.source.serversKey)
            else { throw GatewayMigrationError.definitionMissing }
            // Restore puts this text back as one block, so it must be this
            // server's tables and nothing else — not a neighbour sitting between
            // two of them, which would then be written twice.
            let ownPath = [target.source.serversKey, name]
            guard let slice = try? TOMLDocument.parse(sourceText),
                  slice.tables.allSatisfy({ $0.path.starts(with: ownPath) }) else {
                throw GatewayMigrationError.invalidDefinition("its tables are not next to each other in config.toml; move them together first")
            }
            let subtables = slice.tables.map(\.path).filter { $0.count > ownPath.count }
            if let other = subtables.first(where: { $0 != ownPath + ["env"] }) {
                throw Self.unmodelled([other[ownPath.count]])
            }
            // Launch settings move into the route; settings that tell Codex how
            // to treat the entry (timeouts, which tools to show) apply to the
            // helper just the same and stay in the block, spelled as they were.
            let clientSide = table.pairs.filter { Self.codexClientKeys.contains($0.key.first ?? "") }
            let extra = table.pairs.compactMap(\.key.first)
                .filter { !["command", "args", "env"].contains($0) && !Self.codexClientKeys.contains($0) }
            if !extra.isEmpty { throw Self.unmodelled(Array(Set(extra)).sorted()) }
            guard let command = table.value("command")?.stringValue, !command.isEmpty else {
                throw GatewayMigrationError.invalidDefinition("its command is missing or is not a string")
            }
            let arguments = table.value("args")?.elements?.compactMap(\.stringValue) ?? []
            if let elements = table.value("args")?.elements, arguments.count != elements.count {
                throw GatewayMigrationError.invalidDefinition("one of its arguments is not a string")
            }
            let environment = try tomlEnvironment(
                table: table,
                subtable: document.table(at: [target.source.serversKey, name, "env"])
            )
            try Self.refusePlaceholders(command: command, arguments: arguments, environment: environment)
            let replacement = ([TOMLBuilder.serverBlock(
                name: name,
                under: target.source.serversKey,
                command: helperURL.path,
                args: ["--route", routeID],
                url: nil,
                env: [],
                enabled: nil
            )] + clientSide.map { document.slice($0.span) }).joined(separator: "\n")
            return DirectDefinition(
                serverName: name,
                sourceText: sourceText,
                command: command,
                arguments: arguments,
                environment: environment,
                replacement: replacement
            )
        }
    }

    private func jsonEnvironment(_ node: JSONNode?) throws -> [EnvEntry] {
        guard let node else { return [] }
        guard let members = node.members else {
            throw GatewayMigrationError.invalidDefinition("its environment is not an object")
        }
        return try members.map { member in
            guard let value = member.value.stringValue else {
                throw GatewayMigrationError.invalidDefinition("environment value \(member.key) is not a string")
            }
            return EnvEntry(key: member.key, value: value)
        }
    }

    /// Every way Codex accepts an environment: an `[….env]` sub-table, an
    /// inline `env = { … }` (what Codex's own docs show), or dotted `env.K = …`.
    /// Missing one would start the server without it — and leave its values
    /// unredacted in the preview.
    private func tomlEnvironment(table: TOMLTable, subtable: TOMLTable?) throws -> [EnvEntry] {
        var pairs: [(String?, TOMLValue)] = (subtable?.pairs ?? []).map { ($0.name, $0.value) }
        if let env = table.value("env") {
            guard let inline = env.inlinePairs else {
                throw GatewayMigrationError.invalidDefinition("its environment is not a table")
            }
            pairs += inline.map { ($0.name, $0.value) }
        }
        pairs += table.pairs.filter { $0.key.count == 2 && $0.key[0] == "env" }.map { ($0.key[1], $0.value) }
        return try pairs.map { key, value in
            guard let key, let value = value.stringValue else {
                throw GatewayMigrationError.invalidDefinition("one of its environment values is not a string")
            }
            return EnvEntry(key: key, value: value)
        }
    }

    /// Codex keys about the entry rather than the process it launches.
    private static let codexClientKeys: Set<String> = [
        "enabled", "startup_timeout_sec", "startup_timeout_ms", "tool_timeout_sec", "enabled_tools", "disabled_tools",
    ]

    private static func unmodelled(_ keys: [String]) -> GatewayMigrationError {
        .invalidDefinition("it uses \(keys.joined(separator: ", ")), which the gateway does not carry over. Remove \(keys.count == 1 ? "it" : "them") or keep this server Direct")
    }

    /// `${…}` is expanded by the client that launches a definition. Behind the
    /// gateway the client launches the helper instead, so the server would
    /// receive the placeholder text itself.
    private static func refusePlaceholders(command: String, arguments: [String], environment: [EnvEntry]) throws {
        let values = [command] + arguments + environment.compactMap(\.value)
        if values.contains(where: { $0.contains("${") }) {
            throw GatewayMigrationError.invalidDefinition("it uses ${…} placeholders, which only its client can expand")
        }
    }

    private func routeIDs(in text: String, clientID: ClientID) throws -> [String] {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw AuthoringError.noSourceForClient(clientID) }

        switch source.format {
        case .json:
            let document = try JSONDocument.parse(text)
            return document.value(at: [source.serversKey])?.members?.compactMap { member in
                routeID(command: member.value["command"]?.stringValue, arguments: member.value["args"]?.elements?.compactMap(\.stringValue))
            } ?? []
        case .toml:
            let document = try TOMLDocument.parse(text)
            return document.serverNames(under: source.serversKey).compactMap { name in
                let table = document.table(at: [source.serversKey, name])
                return routeID(command: table?.value("command")?.stringValue, arguments: table?.value("args")?.elements?.compactMap(\.stringValue))
            }
        }
    }

    private func routeID(command: String?, arguments: [String]?) -> String? {
        guard let command,
              URL(fileURLWithPath: command).lastPathComponent == "kytto-mcp-proxy",
              let arguments,
              arguments.count == 2,
              arguments[0] == "--route",
              UUID(uuidString: arguments[1]) != nil
        else { return nil }
        return arguments[1].lowercased()
    }

    // MARK: - Splicing

    private func writeReplacement(
        _ replacement: String,
        target: Target,
        routeID: String?,
        serverName: String? = nil
    ) throws -> WriteReceipt {
        switch target.source.format {
        case .json:
            return try writer.edit(
                url: target.url,
                clientID: target.clientID,
                pathDisplay: target.pathDisplay,
                expecting: ledger.digest(for: target.url),
                as: JSONDocument.self
            ) { document in
                guard let serverName,
                      let map = document.value(at: [target.source.serversKey]),
                      let member = map.members?.first(where: {
                          Server.identity(for: $0.key) == Server.identity(for: serverName)
                      })
                else { throw GatewayMigrationError.definitionMissing }
                if let routeID {
                    guard member.value["args"]?.elements?.compactMap(\.stringValue) == ["--route", routeID] else {
                        throw GatewayMigrationError.routeMismatch
                    }
                }
                return try document.settingMember(member.key, at: [target.source.serversKey], to: replacement)
            }

        case .toml:
            return try writer.edit(
                url: target.url,
                clientID: target.clientID,
                pathDisplay: target.pathDisplay,
                expecting: ledger.digest(for: target.url),
                as: TOMLDocument.self
            ) { document in
                guard let serverName else { throw GatewayMigrationError.definitionMissing }
                let name = document.serverNames(under: target.source.serversKey)
                    .first(where: { Server.identity(for: $0) == Server.identity(for: serverName) }) ?? serverName
                if let routeID {
                    let arguments = document.table(at: [target.source.serversKey, name])?
                        .value("args")?.elements?.compactMap(\.stringValue)
                    guard arguments == ["--route", routeID] else { throw GatewayMigrationError.routeMismatch }
                }
                return try document.settingServer(name, under: target.source.serversKey, to: replacement)
            }
        }
    }

    private func redactedPreview(of direct: DirectDefinition, format: ConfigFormat) -> String {
        switch format {
        case .json:
            guard let document = try? JSONDocument.parse(direct.sourceText),
                  let members = document.root["env"]?.members
            else { return direct.sourceText }
            return document.replacing(members.map {
                ($0.value.span, JSONText.string("<stored in credentials>"))
            })
        case .toml:
            guard let document = try? TOMLDocument.parse(direct.sourceText) else {
                return direct.environment.isEmpty ? direct.sourceText : "<preview withheld: the definition could not be redacted>"
            }
            var spans: [TOMLSpan] = []
            for table in document.tables {
                if table.path.last == "env" { spans += table.pairs.map(\.value.span) }
                for pair in table.pairs where pair.key.first == "env" {
                    if pair.key.count == 2 { spans.append(pair.value.span) }
                    spans += (pair.value.inlinePairs ?? []).map(\.value.span)
                }
            }
            guard spans.count >= direct.environment.count else {
                return "<preview withheld: the definition could not be redacted>"
            }
            return document.replacing(spans.map { ($0, TOMLText.string("<stored in credentials>")) })
        }
    }
}
