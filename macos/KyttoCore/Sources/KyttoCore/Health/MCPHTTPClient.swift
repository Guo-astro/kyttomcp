import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Streamable HTTP inspection (§7.3): handshake and read-only listings, never tools/call.
public struct MCPHTTPClient: Sendable {
    public static let preferredProtocolVersion = MCPStdioClient.preferredProtocolVersion
    private let transport: RemoteHTTPTransport
    private let maxResponseBytes = 4 * 1024 * 1024
    private let maxTotalBytes = 8 * 1024 * 1024

    public init(transport: RemoteHTTPTransport = .init()) { self.transport = transport }

    public func listTools(url: String, headers: [String: String] = [:], timeout: TimeInterval = 10) throws -> ToolListing {
        let endpoint = try RemoteHTTPTransport.endpoint(url)
        try Self.validate(headers)
        let deadline = Date().addingTimeInterval(min(max(timeout, 0.05), 60))
        var sessionID: String?
        var protocolVersion: String?
        var receivedBytes = 0
        func send(_ body: [String: Any], id: Int?) throws -> JSONDocument? {
            var request = URLRequest(url: endpoint)
            request.httpMethod = "POST"
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
            for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
            if let sessionID { request.setValue(sessionID, forHTTPHeaderField: "MCP-Session-Id") }
            if let protocolVersion { request.setValue(protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version") }
            let reply = try transport.send(request, deadline: deadline, limit: min(maxResponseBytes, maxTotalBytes - receivedBytes), responseID: id)
            receivedBytes += reply.body.count
            if reply.status == 401 || (reply.status == 403 && reply.header("www-authenticate") != nil) {
                throw RemoteHTTPError.authorizationRequired(challenge: reply.header("www-authenticate"))
            }
            guard (200..<300).contains(reply.status) else { throw RemoteHTTPError.httpStatus(reply.status) }
            if id == nil {
                guard reply.status == 202, reply.body.isEmpty else {
                    throw RemoteHTTPError.protocolFailure("the initialized notification was not accepted with HTTP 202")
                }
                return nil
            }
            if let assigned = reply.header("mcp-session-id"), sessionID == nil {
                guard assigned.utf8.count <= 1024,
                      !assigned.isEmpty, assigned.utf8.allSatisfy({ (0x21...0x7E).contains($0) }) else {
                    throw RemoteHTTPError.protocolFailure("invalid session header")
                }
                sessionID = assigned
            }
            let mime = (reply.header("content-type") ?? "").lowercased().split(separator: ";", maxSplits: 1).first.map(String.init)
            let messages: [String]
            if mime == "application/json", let text = String(data: reply.body, encoding: .utf8) { messages = [text] }
            else if mime == "text/event-stream" { messages = Self.sseMessages(reply.body) }
            else { throw RemoteHTTPError.protocolFailure("expected application/json or text/event-stream") }
            for message in messages {
                guard let document = RemoteHTTPTransport.document(message), document.root["jsonrpc"]?.stringValue == "2.0",
                      document.root["id"]?.numberValue == Double(id!) else { continue }
                if document.root["error"] != nil {
                    // Server error messages can echo Authorization headers or signed URL queries.
                    throw RemoteHTTPError.protocolFailure("the server returned a JSON-RPC error")
                }
                guard document.root["result"]?.members != nil else {
                    throw RemoteHTTPError.protocolFailure("the response has no result object")
                }
                return document
            }
            throw RemoteHTTPError.protocolFailure("no matching JSON-RPC response")
        }
        func request(_ method: String, id: Int, params: [String: Any] = [:]) throws -> JSONDocument {
            try send(["jsonrpc": "2.0", "id": id, "method": method, "params": params], id: id)!
        }
        defer {
            if let sessionID, Date() < deadline {
                var request = URLRequest(url: endpoint)
                request.httpMethod = "DELETE"
                for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
                request.setValue(sessionID, forHTTPHeaderField: "MCP-Session-Id")
                request.setValue(protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version")
                _ = try? transport.send(request, deadline: min(deadline, Date().addingTimeInterval(0.3)), limit: 1024)
            }
        }
        let initialize = try request("initialize", id: 1, params: [
            "protocolVersion": Self.preferredProtocolVersion, "capabilities": [:],
            "clientInfo": ["name": "Kytto", "version": "1.0"],
        ])
        guard let negotiated = initialize.value(at: ["result", "protocolVersion"])?.stringValue,
              [Self.preferredProtocolVersion, "2025-11-25", "2025-06-18", "2025-03-26"].contains(negotiated),
              initialize.value(at: ["result", "capabilities"])?.members != nil else {
            throw RemoteHTTPError.protocolFailure("unsupported protocol version or missing capabilities")
        }
        protocolVersion = negotiated
        _ = try send(["jsonrpc": "2.0", "method": "notifications/initialized"], id: nil)
        let capabilities = initialize.value(at: ["result", "capabilities"])?.keys.sorted() ?? []
        var entries: [(document: JSONDocument, tool: JSONNode)] = []
        var arrays: [String] = []
        var cursors = Set<String>()
        var cursor: String?
        if capabilities.contains("tools") {
            for page in 0..<50 {
                guard Date() < deadline else { throw RemoteHTTPError.timedOut }
                let document = try request("tools/list", id: 2 + page, params: cursor.map { ["cursor": $0] } ?? [:])
                guard let tools = document.value(at: ["result", "tools"]), let elements = tools.elements,
                      elements.allSatisfy({ $0.lastValue("name")?.stringValue != nil && $0.lastValue("inputSchema")?.members != nil }) else {
                    throw RemoteHTTPError.protocolFailure("tools/list did not return valid tool definitions")
                }
                guard entries.count + elements.count <= 10_000 else { throw RemoteHTTPError.responseTooLarge }
                entries.append(contentsOf: elements.map { (document, $0) })
                arrays.append(document.slice(tools.span))
                if let nextNode = document.value(at: ["result", "nextCursor"]) {
                    guard let next = nextNode.stringValue, !next.isEmpty, next.utf8.count <= 8192,
                          cursors.insert(next).inserted, page < 49 else {
                        throw RemoteHTTPError.protocolFailure("tools pagination did not finish within the inspection limit")
                    }
                    cursor = next
                } else { break }
            }
        }
        var notes: [String] = []
        func optionalCount(_ capability: String, method: String, id: Int) -> Int? {
            guard capabilities.contains(capability) else { return nil }
            do {
                let document = try request(method, id: id)
                guard let values = document.value(at: ["result", capability])?.elements else {
                    notes.append("\(method) did not return its expected array."); return nil
                }
                if document.value(at: ["result", "nextCursor"]) != nil {
                    notes.append("\(capability.capitalized) count shows the first page; the server has more.")
                }
                return values.count
            } catch {
                notes.append("\(method) could not be inspected."); return nil
            }
        }
        let prompts = optionalCount("prompts", method: "prompts/list", id: 100)
        let resources = optionalCount("resources", method: "resources/list", id: 101)
        let summaries = try entries.map { document, tool in
                guard Date() < deadline else { throw RemoteHTTPError.timedOut }
                let hints = tool.lastValue("annotations")
                return ToolSummary(
                    name: tool.lastValue("name")!.stringValue!,
                    description: tool.lastValue("description")?.stringValue,
                    inputSchemaJSON: tool.lastValue("inputSchema").map { document.slice($0.span) },
                    annotations: hints.map { ToolAnnotations(
                        readOnlyHint: $0.lastValue("readOnlyHint")?.boolValue,
                        destructiveHint: $0.lastValue("destructiveHint")?.boolValue,
                        idempotentHint: $0.lastValue("idempotentHint")?.boolValue,
                        openWorldHint: $0.lastValue("openWorldHint")?.boolValue
                    ) },
                    tokenCount: try BPETokenizer.cl100kBase?.countTokens(document.slice(tool.span), deadline: deadline)
                )
            }
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        let rawToolsJSON = arrays.count == 1 ? arrays[0] : "[" + entries.map { $0.document.slice($0.tool.span) }.joined(separator: ",") + "]"
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        return ToolListing(
            tools: summaries,
            rawToolsJSON: rawToolsJSON,
            serverName: initialize.value(at: ["result", "serverInfo", "name"])?.stringValue,
            serverVersion: initialize.value(at: ["result", "serverInfo", "version"])?.stringValue,
            negotiatedProtocolVersion: negotiated,
            capabilityNames: capabilities,
            promptCount: prompts, resourceCount: resources, inspectionNotes: notes,
            resolvedExecutable: ""
        )
    }

    static func validate(_ headers: [String: String]) throws {
        let forbidden: Set<String> = ["host", "content-length", "content-type", "accept", "connection", "transfer-encoding", "mcp-session-id", "mcp-protocol-version", "cookie", "proxy-authorization"]
        guard headers.count <= 64 else { throw RemoteHTTPError.invalidHeaders }
        var names = Set<String>()
        for (name, value) in headers {
            let lowered = name.lowercased()
            guard !name.isEmpty, name.utf8.count <= 128, value.utf8.count <= 16_384,
                  name.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || "!#$%&'*+-.^_`|~".utf8.contains($0) }),
                  value.utf8.allSatisfy({ $0 >= 0x20 && $0 != 0x7F }),
                  !forbidden.contains(lowered), names.insert(lowered).inserted else {
                throw RemoteHTTPError.invalidHeaders
            }
        }
    }

    /// Only complete SSE events count; preserves multi-line data and ignores comments/heartbeats.
    static func sseMessages(_ data: Data) -> [String] {
        guard let text = String(data: data, encoding: .utf8) else { return [] }
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        return normalized.components(separatedBy: "\n\n").dropLast().compactMap { event in
            let lines = event.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> String? in
                guard line.hasPrefix("data:") else { return nil }
                let value = line.dropFirst(5)
                return String(value.first == " " ? value.dropFirst() : value)
            }
            return lines.isEmpty ? nil : lines.joined(separator: "\n")
        }
    }
}
