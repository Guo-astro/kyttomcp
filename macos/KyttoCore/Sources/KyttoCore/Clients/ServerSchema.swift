import Foundation

/// How one server entry is spelled inside a JSON server map (§4).
///
/// The first five clients agreed on the per-server shape — `command`, `args`,
/// `env`, and `url` with an optional `type` — which is why a definition could
/// travel between them as text. The clients that came after them do not:
/// Antigravity writes a remote endpoint as `serverUrl` and rejects a `type`,
/// Devin Desktop states a transport as `transport`, and OpenCode spells the
/// whole entry differently. This is data on the descriptor, next to the path
/// and the off switch, so the difference is written down once.
public struct ServerSchema: Equatable, Sendable {
    public enum Shape: Equatable, Sendable {
        /// `command` + `args` + `env`, a remote endpoint under a URL key.
        case mcpServers
        /// OpenCode: `type: "local" | "remote"`, the command and its arguments
        /// as one argv array in `command`, and `environment` instead of `env`.
        case openCode
    }

    public let shape: Shape
    /// Where a remote endpoint is written. Any of `remoteURLKeys` is read.
    public let remoteURLKey: String
    /// Where a remote transport is written, or nil when the endpoint key is
    /// all the client looks at.
    public let transportKey: String?
    /// False when the client assumes Streamable HTTP and only a different
    /// transport is worth stating.
    public let statesDefaultTransport: Bool
    /// Whether the client still accepts the legacy HTTP+SSE transport.
    public let acceptsSSE: Bool

    /// Claude Desktop, Claude Code, Cursor and VS Code.
    public static let standard = ServerSchema(
        shape: .mcpServers, remoteURLKey: "url", transportKey: "type",
        statesDefaultTransport: true, acceptsSSE: true
    )
    /// Remote endpoints under `serverUrl`, no `type`, Streamable HTTP only
    /// (https://antigravity.google/docs/mcp).
    public static let antigravity = ServerSchema(
        shape: .mcpServers, remoteURLKey: "serverUrl", transportKey: nil,
        statesDefaultTransport: false, acceptsSSE: false
    )
    /// `url`, with `transport: "sse"` only for the legacy transport
    /// (https://docs.devin.ai/cli/extensibility/mcp/configuration).
    public static let devin = ServerSchema(
        shape: .mcpServers, remoteURLKey: "url", transportKey: "transport",
        statesDefaultTransport: false, acceptsSSE: true
    )
    /// https://opencode.ai/docs/mcp-servers/
    public static let openCode = ServerSchema(
        shape: .openCode, remoteURLKey: "url", transportKey: "type",
        statesDefaultTransport: true, acceptsSSE: true
    )

    /// Every key a remote endpoint is read under, the written one first.
    /// Antigravity documents `url` as an alternative to its `serverUrl`; the
    /// clients that write `url` do not read `serverUrl`, so neither does Kytto.
    var remoteURLKeys: [String] {
        remoteURLKey == "url" ? ["url"] : [remoteURLKey, "url"]
    }

    /// The key holding environment variables.
    public var environmentKey: String {
        shape == .openCode ? "environment" : "env"
    }

    /// Keys that record one client's own state about a server rather than how
    /// to start it. A copy into another client starts from that client's
    /// defaults: a server switched off in Antigravity must not arrive switched
    /// off in Devin Desktop while the matrix says on.
    static let clientStateKeys = ["disabled", "enabled", "disabledTools", "enabledTools"]

    // MARK: - Reading

    func fields(of node: JSONNode) -> ServerFields {
        switch shape {
        case .mcpServers:
            var fields = ServerFields(node: node)
            fields.url = remoteURLKeys.lazy.compactMap { node[$0]?.stringValue }.first
            fields.declaredType = node["type"]?.stringValue
                ?? transportKey.flatMap { node[$0]?.stringValue }
            return fields
        case .openCode:
            var fields = ServerFields()
            let argv = node["command"]?.elements?.compactMap(\.stringValue) ?? []
            if let command = argv.first {
                fields.command = command
                fields.args = Array(argv.dropFirst())
            } else {
                fields.command = node["command"]?.stringValue
            }
            fields.url = node["url"]?.stringValue
            fields.workingDirectory = node["cwd"]?.stringValue
            fields.env = (node["environment"]?.members ?? []).map {
                EnvEntry(key: $0.key, value: $0.value.stringValue)
            }
            switch node["type"]?.stringValue {
            case "local": fields.declaredType = Transport.stdio.rawValue
            case "remote": fields.declaredType = Transport.http.rawValue
            default: fields.declaredType = nil
            }
            return fields
        }
    }

    // MARK: - Moving a definition between clients

    /// Settings a definition carries that a definition rendered from Kytto's
    /// model (command, args, env, url) would drop.
    func unportableFields(in node: JSONNode) -> [String] {
        let keys = node.keys
        switch shape {
        case .mcpServers: return ["headers", "cwd", "envFile"].filter(keys.contains)
        case .openCode: return ["headers", "cwd", "oauth"].filter(keys.contains)
        }
    }

    /// `source`, written for a client with schema `from`, respelt for one with
    /// this schema. Only the keys the two disagree on move; `headers`, `cwd`
    /// and keys Kytto has never heard of come along as they are.
    ///
    /// Nil when this schema cannot express the definition — the legacy SSE
    /// transport in a client that only speaks Streamable HTTP — or when the
    /// shapes differ, in which case the definition is rendered from the model.
    func translating(_ source: String, from: ServerSchema) -> String? {
        guard shape == .mcpServers, from.shape == .mcpServers,
              let document = try? JSONDocument.parse(source),
              document.root.members != nil
        else { return nil }
        let transport = from.fields(of: document.root)
            .makeServer(name: "", origin: .configFile(.cursor), isBundled: false, sourceText: "")
            .transport
        if transport == .sse, !acceptsSSE { return nil }

        var text = source
        func remove(_ key: String) throws {
            text = try JSONDocument.parse(text).removingMember(key, at: [])
        }
        func set(_ key: String, _ raw: String) throws {
            text = try JSONDocument.parse(text).settingMember(key, at: [], to: raw)
        }
        do {
            for key in Self.clientStateKeys { try remove(key) }
            let transportKeys = ["type", "transport"]
            if transport == .stdio {
                // Only this client's own key may stay; Antigravity refuses an
                // entry carrying a `type` it does not know.
                for key in transportKeys where key != transportKey { try remove(key) }
                return text
            }
            let url = remoteURLKeys.lazy.compactMap { document.root[$0]?.stringValue }.first
                ?? from.remoteURLKeys.lazy.compactMap { document.root[$0]?.stringValue }.first
            guard let url else { return nil }
            for key in transportKeys { try remove(key) }
            for key in Set(remoteURLKeys + from.remoteURLKeys) where key != remoteURLKey { try remove(key) }
            try set(remoteURLKey, JSONText.string(url))
            if let transportKey, statesDefaultTransport || transport != .http {
                try set(transportKey, JSONText.string(transport.rawValue))
            }
            return text
        } catch {
            return nil
        }
    }
}
