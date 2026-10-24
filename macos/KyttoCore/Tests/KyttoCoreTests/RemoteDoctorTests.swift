import Foundation
import CryptoKit
import Testing
@testable import KyttoCore

private final class RemoteFixture: @unchecked Sendable {
    private let lock = NSLock()
    private var replies: [RemoteHTTPTransport.Reply]
    private var captured: [URLRequest] = []
    init(_ replies: [RemoteHTTPTransport.Reply]) { self.replies = replies }
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return captured }
    var transport: RemoteHTTPTransport {
        .init { request, _, _, _ in
            self.lock.lock(); defer { self.lock.unlock() }
            self.captured.append(request)
            guard !self.replies.isEmpty else { throw RemoteHTTPError.unavailable }
            return self.replies.removeFirst()
        }
    }
    static func reply(_ text: String = "", status: Int = 200, headers: [String: String] = [:]) -> RemoteHTTPTransport.Reply {
        .init(status: status, headers: ["content-type": "application/json"].merging(headers) { _, new in new }, body: Data(text.utf8))
    }
    static let initialize = #"{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"remote-fixture","version":"1"}}}"#
    /// What a handshake-era TypeScript server answers a request without a session.
    static let legacyRejection = reply(#"{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Bad Request: No valid session ID provided"}}"#, status: 400)
    static let emptyTools = #"{"jsonrpc":"2.0","id":2,"result":{"tools":[]}}"#
}

@Suite("Remote Doctor — bounded Streamable HTTP")
struct RemoteDoctorTests {
    @Test func paginatesJSONAndSSEWithSessionHeaders() throws {
        let first = #"{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read","description":"Read","inputSchema": {"type":"object"},"annotations":{"readOnlyHint":true}}],"nextCursor":"two"}}"#
        let second = #"{"jsonrpc":"2.0","id":3,"result":{"tools":[{"name":"write","inputSchema":{"type":"object"}}]}}"#
        let fixture = RemoteFixture([
            RemoteFixture.legacyRejection,
            RemoteFixture.reply(RemoteFixture.initialize, headers: ["mcp-session-id": "session-secret"]),
            RemoteFixture.reply(status: 202), RemoteFixture.reply(first),
            RemoteFixture.reply(": heartbeat\r\n\r\nevent: message\r\ndata: " + second + "\r\n\r\n", headers: ["content-type": "text/event-stream"]),
            RemoteFixture.reply(status: 204),
        ])
        let listing = try MCPHTTPClient(transport: fixture.transport).listTools(url: "https://mcp.example/mcp", headers: ["Authorization": "Bearer fixture-secret"])
        #expect(listing.tools.map(\.name) == ["read", "write"])
        #expect(listing.tools[0].inputSchemaJSON == #"{"type":"object"}"#)
        #expect(listing.tools[0].annotations?.readOnlyHint == true)
        #expect(listing.serverName == "remote-fixture")
        #expect(listing.negotiatedProtocolVersion == "2025-11-25")
        #expect(fixture.requests.count == 6)
        // The modern probe carries the 2026-07-28 metadata and no session.
        #expect(fixture.requests[0].value(forHTTPHeaderField: "MCP-Protocol-Version") == "2026-07-28")
        #expect(fixture.requests[0].value(forHTTPHeaderField: "Mcp-Method") == "server/discover")
        #expect(fixture.requests[1].value(forHTTPHeaderField: "MCP-Session-Id") == nil)
        #expect(fixture.requests[1].value(forHTTPHeaderField: "MCP-Protocol-Version") == nil)
        for request in fixture.requests.dropFirst(2) {
            #expect(request.value(forHTTPHeaderField: "MCP-Session-Id") == "session-secret")
            #expect(request.value(forHTTPHeaderField: "MCP-Protocol-Version") == "2025-11-25")
            #expect(request.value(forHTTPHeaderField: "Mcp-Method") == nil)
            #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer fixture-secret")
        }
        #expect(fixture.requests.last?.httpMethod == "DELETE")
        #expect(String(decoding: fixture.requests[4].httpBody!, as: UTF8.self).contains("two"))
    }

    @Test func speaksTheModernRevisionWithoutAHandshake() throws {
        let discover = #"{"jsonrpc":"2.0","id":1,"result":{"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{}},"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-remote","version":"3"}}}}"#
        let tools = #"{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read","inputSchema":{"type":"object"}}]}}"#
        let fixture = RemoteFixture([RemoteFixture.reply(discover), RemoteFixture.reply(tools)])
        let listing = try MCPHTTPClient(transport: fixture.transport).listTools(url: "https://mcp.example/mcp")
        #expect(listing.negotiatedProtocolVersion == "2026-07-28")
        #expect(listing.serverName == "modern-remote")
        #expect(listing.tools.map(\.name) == ["read"])
        #expect(fixture.requests.count == 2)
        let list = fixture.requests[1]
        #expect(list.value(forHTTPHeaderField: "Mcp-Method") == "tools/list")
        #expect(list.value(forHTTPHeaderField: "MCP-Protocol-Version") == "2026-07-28")
        #expect(String(decoding: list.httpBody!, as: UTF8.self).contains("io.modelcontextprotocol/protocolVersion"))
    }

    @Test func aModernVersionRefusalIsNotMistakenForAHandshakeServer() throws {
        let refusal = #"{"jsonrpc":"2.0","id":1,"error":{"code":-32022,"message":"Unsupported","data":{"supported":["2027-01-01"],"requested":"2026-07-28"}}}"#
        let fixture = RemoteFixture([RemoteFixture.reply(refusal, status: 400)])
        #expect(throws: RemoteHTTPError.self) {
            try MCPHTTPClient(transport: fixture.transport).listTools(url: "https://mcp.example/mcp")
        }
        #expect(fixture.requests.count == 1)
    }

    @Test func aLegacySSEEndpointIsUnsupportedNotFailed() throws {
        let fixture = RemoteFixture([
            RemoteFixture.reply(status: 405), RemoteFixture.reply(status: 405),
            RemoteFixture.reply("event: endpoint\ndata: /messages?session=1\n\n", headers: ["content-type": "text/event-stream"]),
        ])
        let checker = HealthChecker(httpClient: MCPHTTPClient(transport: fixture.transport))
        let server = Server(id: "remote", name: "remote", transport: .http, command: nil, args: [], env: [],
                            url: "https://mcp.example/events", enabledIn: [:], origin: .configFile(.cursor),
                            fingerprint: "test", isBundled: false, definitionSource: "{}")
        let health = checker.check(server).health
        #expect(health.status == .unsupported)
        #expect(fixture.requests.last?.httpMethod == "GET")
    }

    @Test func refusesRepeatedCursorAndDoesNotReportPartialToolsAsPassed() throws {
        let fixture = RemoteFixture([
            RemoteFixture.legacyRejection, RemoteFixture.reply(RemoteFixture.initialize), RemoteFixture.reply(status: 202),
            RemoteFixture.reply(#"{"jsonrpc":"2.0","id":2,"result":{"tools":[],"nextCursor":"same"}}"#),
            RemoteFixture.reply(#"{"jsonrpc":"2.0","id":3,"result":{"tools":[],"nextCursor":"same"}}"#),
        ])
        #expect(throws: RemoteHTTPError.self) {
            try MCPHTTPClient(transport: fixture.transport).listTools(url: "https://mcp.example/mcp")
        }
    }

    @Test func distinguishesAuthorizationFromForbiddenAndDoesNotPersistSecrets() throws {
        func check(status: Int, challenge: String?) -> HealthResult {
            let fixture = RemoteFixture([RemoteFixture.reply("sensitive echoed body", status: status,
                                               headers: challenge.map { ["www-authenticate": $0] } ?? [:])])
            let checker = HealthChecker(httpClient: MCPHTTPClient(transport: fixture.transport))
            let server = Server(id: "remote", name: "remote", transport: .http, command: nil, args: [], env: [],
                                url: "https://mcp.example/mcp?token=hidden-query", enabledIn: [:], origin: .configFile(.cursor),
                                fingerprint: "test", isBundled: false, definitionSource: "{}")
            return checker.check(server).health
        }
        let needsAuth = check(status: 401, challenge: #"Bearer resource_metadata="https://mcp.example/meta?token=challenge-secret""#)
        #expect(needsAuth.status == .needsAuthorization)
        #expect(needsAuth.authorizationURL == nil)
        let encoded = String(decoding: try JSONEncoder().encode(needsAuth), as: UTF8.self)
        #expect(!encoded.contains("hidden-query"))
        #expect(!encoded.contains("challenge-secret"))
        #expect(!encoded.contains("echoed body"))
        let forbidden = check(status: 403, challenge: nil)
        #expect(forbidden.status == .failed)
        #expect(forbidden.currentMessage?.contains("403") == true)
        #expect(check(status: 403, challenge: #"Bearer error="insufficient_scope",scope="write""#).status == .needsAuthorization)
    }

    @Test func healthCheckMergesOAuthOverrideAndDoctorUsesRemoteOutcome() throws {
        let fixture = RemoteFixture([
            RemoteFixture.legacyRejection, RemoteFixture.reply(RemoteFixture.initialize), RemoteFixture.reply(status: 202),
            RemoteFixture.reply(RemoteFixture.emptyTools),
        ])
        var server = Server(id: "remote", name: "remote", transport: .http, command: nil, args: [], env: [],
                            url: "https://mcp.example/mcp", enabledIn: [:], origin: .configFile(.cursor),
                            fingerprint: "test", isBundled: false,
                            definitionSource: #"{"headers":{"authorization":"Bearer ${MISSING}","X-Version":"2026"}}"#)
        let checker = HealthChecker(httpClient: MCPHTTPClient(transport: fixture.transport))
        let outcome = checker.check(server, httpHeaders: ["Authorization": "Bearer oauth-fixture"])
        #expect(outcome.health.status == .passed)
        #expect(fixture.requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer oauth-fixture" })
        #expect(fixture.requests.allSatisfy { $0.value(forHTTPHeaderField: "X-Version") == "2026" })
        #expect(MCPDoctor.analyze(server).findings.contains { $0.code == "not-checked" })
        server.health = outcome.health
        #expect(MCPDoctor.analyze(server).findings.isEmpty)
        server.health = HealthResult(status: .needsAuthorization, checkedAt: .timestamp(), toolCount: nil, tools: [],
                                     message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil)
        #expect(MCPDoctor.analyze(server).findings.contains { $0.code == "needs-authorization" })
    }

    @Test func hostileDescriptionCannotOutliveProcessingDeadlineOrReturnPartialWeight() throws {
        // Ensure the vocabulary loads before the timed check; this isolates
        // hostile input work from a one-time bundled-resource load.
        _ = BPETokenizer.cl100kBase
        let hugeTool: [String: Any] = ["name": "huge", "description": String(repeating: "abcdefghi", count: 200_000), "inputSchema": ["type": "object"]]
        let response = try JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": 2, "result": ["tools": [hugeTool]]])
        let fixture = RemoteFixture([
            RemoteFixture.legacyRejection, RemoteFixture.reply(RemoteFixture.initialize), RemoteFixture.reply(status: 202),
            .init(status: 200, headers: ["content-type": "application/json"], body: response),
        ])
        let checker = HealthChecker(timeout: 0.15, httpClient: MCPHTTPClient(transport: fixture.transport))
        let server = Server(id: "remote", name: "remote", transport: .http, command: nil, args: [], env: [],
                            url: "https://mcp.example/mcp", enabledIn: [:], origin: .configFile(.cursor),
                            fingerprint: "test", isBundled: false, definitionSource: "{}")
        let start = Date()
        let outcome = checker.check(server)
        #expect(outcome.health.status == .failed)
        #expect(outcome.health.currentMessage?.contains("time limit") == true)
        #expect(outcome.health.tools.isEmpty)
        #expect(outcome.tokenWeight == nil)
        #expect(Date().timeIntervalSince(start) < 2)
    }

    @Test func validatesEndpointsHeadersAndResponseBounds() throws {
        for endpoint in ["http://mcp.example/mcp", "https://user:password@mcp.example/mcp", "https://mcp.example/mcp#fragment"] {
            #expect(throws: RemoteHTTPError.self) { try RemoteHTTPTransport.endpoint(endpoint) }
        }
        #expect(try RemoteHTTPTransport.endpoint("http://127.0.0.1:12345/mcp").host == "127.0.0.1")
        for headers in [["Host": "different.example"], ["Authorization": "secret\r\ninjected: true"], ["MCP-Session-Id": "injected"]] {
            #expect(throws: RemoteHTTPError.self) { try MCPHTTPClient.validate(headers) }
        }
        let oversized = RemoteFixture([RemoteFixture.reply(String(repeating: "x", count: 4 * 1024 * 1024 + 1))])
        #expect(throws: RemoteHTTPError.self) { try MCPHTTPClient(transport: oversized.transport).listTools(url: "https://mcp.example/mcp") }
        let wrongID = RemoteFixture([RemoteFixture.reply(#"{"jsonrpc":"2.0","id":99,"result":{}}"#)])
        #expect(throws: RemoteHTTPError.self) { try MCPHTTPClient(transport: wrongID.transport).listTools(url: "https://mcp.example/mcp") }
        #expect(RemoteHTTPTransport.document(String(repeating: "[", count: 129) + String(repeating: "]", count: 129)) == nil)
        #expect(RemoteHTTPTransport.document(#"{"result":{"tools":[]},"result":{"tools":[{"name":"hidden"}]}}"#) == nil)
        #expect(RemoteHTTPTransport.document(#"{"result":{"tools":[],"tools":[{"name":"hidden"}]}}"#) == nil)
    }

    @Test func parsesSSECompleteEventsOnly() {
        #expect(MCPHTTPClient.sseMessages(Data("data: first\ndata: second\n\n: heartbeat\n\ndata: incomplete".utf8)) == ["first\nsecond"])
        #expect(MCPHTTPClient.sseMessages(Data("data: one\r\rdata: two\r\r".utf8)) == ["one", "two"])
    }

    @Test func resolvesOriginalClientHeadersWithoutCrossingIPC() throws {
        let environment = ["TOKEN": "fixture-token", "VERSION": "2026"]
        let json = #"{"url":"https://mcp.example","headers":{"Authorization":"Bearer ${env:TOKEN}","X-Version":"${VERSION}"}}"#
        #expect(try RemoteHTTPHeaders.resolve(definitionSource: json, environment: environment) == ["Authorization": "Bearer fixture-token", "X-Version": "2026"])
        let toml = """
        [mcp_servers.remote]
        url = "https://mcp.example/mcp"
        bearer_token_env_var = "TOKEN"
        [mcp_servers.remote.http_headers]
        X-Version = "2026"
        [mcp_servers.remote.env_http_headers]
        X-Token = "TOKEN"
        """
        #expect(try RemoteHTTPHeaders.resolve(definitionSource: toml, environment: environment) == ["Authorization": "Bearer fixture-token", "X-Version": "2026", "X-Token": "fixture-token"])
        #expect(throws: RemoteHTTPError.self) { try RemoteHTTPHeaders.resolve(definitionSource: json, environment: [:]) }
        let literalToken = #"{"headers":{"Authorization":"Bearer ${env:TOKEN}"}}"#
        #expect(try RemoteHTTPHeaders.resolve(definitionSource: literalToken, environment: ["TOKEN": "prefix-${OTHER}", "OTHER": "must-not-expand"]) == ["Authorization": "Bearer prefix-${OTHER}"])
        let inline = #"remote = { url="https://mcp.example", http_headers={X-Version="2026"}, bearer_token_env_var="TOKEN" }"#
        #expect(try RemoteHTTPHeaders.resolve(definitionSource: inline, environment: environment) == ["Authorization": "Bearer fixture-token", "X-Version": "2026"])
        #expect(try RemoteHTTPHeaders.resolve(definitionSource: json, environment: ["VERSION": "2026"], omittingAuthorization: true) == ["X-Version": "2026"])
    }
}

@Suite("Remote OAuth — native PKCE and secure storage")
struct RemoteOAuthTests {
    private let redirect = "app.kytto.oauth://callback"
    private var configuration: RemoteOAuthConfiguration {
        .init(resourceURL: "https://mcp.example/mcp", resource: nil, issuer: "https://auth.example/tenant",
              authorizationEndpoint: "https://auth.example/authorize", tokenEndpoint: "https://auth.example/token",
              registrationEndpoint: nil, scopes: ["read"], authorizationResponseIssuerSupported: true)
    }
    private func callback(_ pending: RemoteOAuthPendingAuthorization, issuer: String = "https://auth.example/tenant", state: String? = nil, code: String = "code+with space") -> URL {
        let request = URLComponents(url: pending.authorizationURL, resolvingAgainstBaseURL: false)!
        let originalState = request.queryItems!.first { $0.name == "state" }!.value!
        var url = URLComponents(string: redirect)!
        url.queryItems = [.init(name: "state", value: state ?? originalState), .init(name: "iss", value: issuer), .init(name: "code", value: code)]
        return url.url!
    }

    @Test func discoveryUsesChallengeScopesAndValidatesIssuer() throws {
        let resource = #"{"resource":"https://mcp.example/mcp","authorization_servers":["https://auth.example/tenant"],"scopes_supported":["wide"]}"#
        let auth = #"{"issuer":"https://auth.example/tenant","authorization_endpoint":"https://auth.example/authorize","token_endpoint":"https://auth.example/token","code_challenge_methods_supported":["S256"],"authorization_response_iss_parameter_supported":true}"#
        let fixture = RemoteFixture([RemoteFixture.reply(resource), RemoteFixture.reply(status: 404), RemoteFixture.reply(auth)])
        let config = try RemoteOAuthClient(transport: fixture.transport).discover(resourceURL: "https://mcp.example/mcp", challenge: #"Bearer resource_metadata="https://mcp.example/meta", scope="read""#)
        #expect(config.scopes == ["read"])
        #expect(config.issuer == "https://auth.example/tenant")
        #expect(fixture.requests.map { $0.url!.path } == ["/meta", "/.well-known/oauth-authorization-server/tenant", "/.well-known/openid-configuration/tenant"])
        #expect(fixture.requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == nil })
        let bad = RemoteFixture([RemoteFixture.reply(resource), RemoteFixture.reply(auth.replacingOccurrences(of: #""issuer":"https://auth.example/tenant""#, with: #""issuer":"https://attacker.example""#))])
        #expect(throws: RemoteOAuthError.self) { try RemoteOAuthClient(transport: bad.transport).discover(resourceURL: "https://mcp.example/mcp", challenge: #"Bearer resource_metadata="https://mcp.example/meta""#) }
    }

    @Test func rootMetadataForTheOriginCoversAPathEndpoint() throws {
        // Path-inserted metadata answers 401 from auth middleware; the root
        // document describes the origin, which RFC 9728 allows.
        let resource = #"{"resource":"https://mcp.example","authorization_servers":["https://auth.example"]}"#
        let auth = #"{"issuer":"https://auth.example","authorization_endpoint":"https://auth.example/authorize","token_endpoint":"https://auth.example/token","code_challenge_methods_supported":["S256"]}"#
        let fixture = RemoteFixture([RemoteFixture.reply(status: 401), RemoteFixture.reply(resource), RemoteFixture.reply(auth)])
        let config = try RemoteOAuthClient(transport: fixture.transport).discover(resourceURL: "https://mcp.example/mcp/", challenge: "Bearer")
        #expect(config.resourceURL == "https://mcp.example/mcp")
        #expect(config.resource == "https://mcp.example")
        #expect(fixture.requests.map { $0.url!.path } == ["/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource", "/.well-known/oauth-authorization-server"])
        let pending = try RemoteOAuthClient(transport: fixture.transport).prepareAuthorization(configuration: config, clientID: "c", redirectURI: redirect)
        let query = URLComponents(url: pending.authorizationURL, resolvingAgainstBaseURL: false)!.queryItems!
        #expect(query.first { $0.name == "resource" }?.value == "https://mcp.example")
        #expect(!RemoteOAuthClient.resource("https://mcp.example/other", covers: URL(string: "https://mcp.example/mcp")!))
        #expect(!RemoteOAuthClient.resource("https://mcp.example/mc", covers: URL(string: "https://mcp.example/mcp")!))
        #expect(!RemoteOAuthClient.resource("https://other.example", covers: URL(string: "https://mcp.example/mcp")!))
    }

    @Test func acceptsGrantedScopesAndStringExpiry() throws {
        let fixture = RemoteFixture([
            RemoteFixture.reply(#"{"access_token":"a","token_type":"bearer","expires_in":"3600","scope":"read default"}"#),
        ])
        let client = RemoteOAuthClient(transport: fixture.transport)
        let pending = try client.prepareAuthorization(configuration: configuration, clientID: "public-client", redirectURI: redirect)
        let credential = try client.exchange(pending: pending, callbackURL: callback(pending))
        #expect(credential.scopes == ["default", "read"])
        #expect(credential.expiresAt != nil)
    }

    @Test func pkceBindsCallbackIssuerResourceAndRefreshTokens() throws {
        let fixture = RemoteFixture([
            RemoteFixture.reply(#"{"access_token":"access-fixture","token_type":"Bearer","expires_in":120,"refresh_token":"refresh-fixture","scope":"read"}"#),
            RemoteFixture.reply(#"{"access_token":"rotated-access","token_type":"Bearer","expires_in":3600,"refresh_token":"rotated-refresh","scope":"read"}"#),
        ])
        let client = RemoteOAuthClient(transport: fixture.transport)
        let pending = try client.prepareAuthorization(configuration: configuration, clientID: "public-client", redirectURI: redirect)
        let query = URLComponents(url: pending.authorizationURL, resolvingAgainstBaseURL: false)!.queryItems!
        #expect(query.first { $0.name == "code_challenge_method" }?.value == "S256")
        #expect(query.first { $0.name == "resource" }?.value == configuration.resourceURL)
        let credential = try client.exchange(pending: pending, callbackURL: callback(pending, code: "code space"))
        let body = String(decoding: fixture.requests[0].httpBody!, as: UTF8.self)
        #expect(body.contains("resource=https%3A%2F%2Fmcp.example%2Fmcp"))
        #expect(body.contains("code=code%20space"))
        let verifier = body.components(separatedBy: "&").first { $0.hasPrefix("code_verifier=") }!.dropFirst("code_verifier=".count)
        let hash = Data(SHA256.hash(data: Data(verifier.utf8))).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        #expect(query.first { $0.name == "code_challenge" }?.value == hash)
        #expect(credential.authorizationHeaders["Authorization"] == "Bearer access-fixture")
        #expect(throws: RemoteOAuthError.self) { try client.exchange(pending: pending, callbackURL: callback(pending)) }
        let renewed = try client.refresh(credential)
        #expect(renewed.authorizationHeaders["Authorization"] == "Bearer rotated-access")
        #expect(String(decoding: fixture.requests[1].httpBody!, as: UTF8.self).contains("refresh_token=refresh-fixture"))
        let secrets = InMemorySecretStore()
        let vault = RemoteOAuthCredentials(store: secrets)
        try vault.store(renewed)
        #expect(try vault.load(resourceURL: configuration.resourceURL)?.authorizationHeaders == renewed.authorizationHeaders)
        #expect(try vault.load(resourceURL: "https://mcp.example/other") == nil)
        #expect(secrets.storedIdentifiers().allSatisfy { !$0.contains("mcp.example") && !$0.contains("access") })
        try vault.delete(resourceURL: configuration.resourceURL)
        #expect(try vault.load(resourceURL: configuration.resourceURL) == nil)
    }

    @Test func rejectsCallbackTamperingBeforeAnyTokenRequest() throws {
        let fixture = RemoteFixture([])
        let client = RemoteOAuthClient(transport: fixture.transport)
        let mismatched = try client.prepareAuthorization(configuration: configuration, clientID: "client", redirectURI: redirect)
        #expect(throws: RemoteOAuthError.self) { try client.exchange(pending: mismatched, callbackURL: callback(mismatched, issuer: "https://attacker.example")) }
        let state = try client.prepareAuthorization(configuration: configuration, clientID: "client", redirectURI: redirect)
        #expect(throws: RemoteOAuthError.self) { try client.exchange(pending: state, callbackURL: callback(state, state: "wrong")) }
        let origin = try client.prepareAuthorization(configuration: configuration, clientID: "client", redirectURI: redirect)
        #expect(throws: RemoteOAuthError.self) { try client.exchange(pending: origin, callbackURL: URL(string: callback(origin).absoluteString.replacingOccurrences(of: "://callback", with: "://other"))!) }
        #expect(fixture.requests.isEmpty)
        #expect(throws: RemoteOAuthError.self) { try client.prepareAuthorization(configuration: configuration, clientID: "client", redirectURI: "https://external.example/callback") }
    }

    @Test func refusesCrossOriginResourceHintAndHandlesQuotedChallenges() throws {
        let client = RemoteOAuthClient(transport: RemoteFixture([]).transport)
        #expect(throws: RemoteOAuthError.self) { try client.discover(resourceURL: "https://mcp.example/mcp", challenge: #"Bearer resource_metadata="https://different.example/meta""#) }
        let params = RemoteOAuthClient.challengeParameters(#"Basic realm="basic", Bearer resource_metadata="https://mcp.example/meta?a=b,c=d", scope="read write""#)
        #expect(params["scope"] == "read write")
        #expect(params["resource_metadata"] == "https://mcp.example/meta?a=b,c=d")
    }
}

private final class LoopbackRemoteServer {
    let process = Process()
    let url: String

    init(mode: String) throws {
        let script = Bundle.module.resourceURL!.appending(path: "Fixtures/fake_remote_mcp_server.py").path
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = ["python3", script, mode]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let portLine = output.fileHandleForReading.availableData
        guard let text = String(data: portLine, encoding: .utf8), let port = Int(text.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            process.terminate()
            throw RemoteHTTPError.unavailable
        }
        url = "http://127.0.0.1:\(port)/mcp"
    }

    deinit { if process.isRunning { process.terminate(); process.waitUntilExit() } }
}

@Suite("Remote HTTP — real loopback fixture")
struct RemoteLoopbackTests {
    @Test func realJSONAndOpenSSECompleteWithoutWaitingForEOF() throws {
        for mode in ["json", "sse-open"] {
            let server = try LoopbackRemoteServer(mode: mode)
            let start = Date()
            let listing = try MCPHTTPClient().listTools(url: server.url, timeout: 1)
            #expect(listing.tools.map(\.name) == ["fixture_read"])
            #expect(Date().timeIntervalSince(start) < 1.5)
        }
    }

    @Test func realTransportRejectsRedirectOversizeAndTimeout() throws {
        for mode in ["redirect", "oversized", "slow"] {
            let server = try LoopbackRemoteServer(mode: mode)
            do {
                _ = try MCPHTTPClient().listTools(url: server.url, timeout: 0.3)
                Issue.record("Expected \(mode) to fail")
            } catch let error as RemoteHTTPError {
                switch (mode, error) {
                case ("redirect", .httpStatus(302)), ("oversized", .responseTooLarge), ("slow", .timedOut): break
                default: Issue.record("Unexpected sanitized failure for \(mode): \(error)")
                }
            }
        }
    }
}
