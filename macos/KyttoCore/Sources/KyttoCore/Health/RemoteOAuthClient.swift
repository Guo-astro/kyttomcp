import Foundation
import CryptoKit
import Security
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Validated metadata retained natively; never send endpoints or credentials to web state (§6).
public struct RemoteOAuthConfiguration: Codable, Equatable, Sendable {
    /// The configured endpoint, canonicalised. Names the Keychain entry.
    public let resourceURL: String
    /// The protected resource the metadata described (RFC 9728), sent as the
    /// RFC 8707 `resource` parameter. It may be the endpoint or a parent of it
    /// on the same origin. Absent in records saved before it was kept.
    public let resource: String?
    public let issuer: String
    public let authorizationEndpoint: String
    public let tokenEndpoint: String
    public let registrationEndpoint: String?
    public let scopes: [String]
    public let authorizationResponseIssuerSupported: Bool

    var resourceIndicator: String { resource ?? resourceURL }
}

public enum RemoteOAuthError: Error, LocalizedError, Sendable {
    case discovery, issuerMismatch, resourceMismatch, pkceUnsupported, registrationUnavailable
    case invalidClient, invalidRedirect, invalidCallback, expiredAuthorization, authorizationRejected
    case invalidToken, refreshUnavailable, credentialStore

    public var errorDescription: String? {
        switch self {
        case .discovery: "The server did not provide usable OAuth discovery metadata. Check its OAuth support or configured authorization headers."
        case .issuerMismatch: "OAuth issuer validation failed. The sign-in was stopped."
        case .resourceMismatch: "OAuth metadata describes a different MCP resource. The sign-in was stopped."
        case .pkceUnsupported: "The authorization server does not advertise the required S256 PKCE protection."
        case .registrationUnavailable: "This authorization server requires a pre-registered client ID."
        case .invalidClient: "The OAuth client registration is not a usable public client."
        case .invalidRedirect: "The OAuth callback must be an application callback or a loopback redirect."
        case .invalidCallback: "The OAuth callback did not match this sign-in request."
        case .expiredAuthorization: "The sign-in request expired or was already used. Start it again."
        case .authorizationRejected: "The authorization server declined the sign-in request."
        case .invalidToken: "The authorization server returned an unusable token response."
        case .refreshUnavailable: "The saved authorization cannot be refreshed. Sign in again."
        case .credentialStore: "The authorization could not be saved securely."
        }
    }
}

/// A single-use, short-lived PKCE transaction; not Codable and never persisted or passed to IPC.
public final class RemoteOAuthPendingAuthorization: @unchecked Sendable {
    public let authorizationURL: URL
    public let expiresAt: Date
    fileprivate let configuration: RemoteOAuthConfiguration
    fileprivate let clientID: String
    fileprivate let redirectURI: String
    fileprivate let verifier: String
    fileprivate let state: String
    private let lock = NSLock()
    private var used = false

    fileprivate init(url: URL, configuration: RemoteOAuthConfiguration, clientID: String,
                     redirectURI: String, verifier: String, state: String) {
        authorizationURL = url; expiresAt = Date().addingTimeInterval(600)
        self.configuration = configuration; self.clientID = clientID
        self.redirectURI = redirectURI; self.verifier = verifier; self.state = state
    }

    fileprivate func consume() throws {
        lock.lock(); defer { lock.unlock() }
        guard !used, Date() < expiresAt else { throw RemoteOAuthError.expiredAuthorization }
        used = true
    }
}

/// Secret-bearing native record. Store only through SecretStoring; never attach to UI DTOs or logs.
public struct RemoteOAuthCredential: Codable, Sendable {
    public let configuration: RemoteOAuthConfiguration
    public let clientID: String
    public let scopes: [String]
    public let expiresAt: Date?
    private let accessToken: String
    fileprivate let refreshToken: String?

    public var authorizationHeaders: [String: String] { ["Authorization": "Bearer " + accessToken] }
    public var requiresRefresh: Bool { expiresAt.map { $0.timeIntervalSinceNow < 60 } ?? false }
    public var canRefresh: Bool { refreshToken?.isEmpty == false }

    fileprivate init(configuration: RemoteOAuthConfiguration, clientID: String, scopes: [String],
                     expiresAt: Date?, accessToken: String, refreshToken: String?) {
        self.configuration = configuration; self.clientID = clientID; self.scopes = scopes
        self.expiresAt = expiresAt; self.accessToken = accessToken; self.refreshToken = refreshToken
    }
}

public struct RemoteOAuthCredentials: Sendable {
    private let secrets: any SecretStoring
    public init(store: any SecretStoring) { secrets = store }

    public func load(resourceURL: String) throws -> RemoteOAuthCredential? {
        let canonical = try RemoteOAuthClient.canonicalResource(resourceURL)
        guard let value = try secrets.value(for: identifier(canonical)) else { return nil }
        guard let credential = try? JSONDecoder().decode(RemoteOAuthCredential.self, from: Data(value.utf8)),
              credential.configuration.resourceURL == canonical else { throw RemoteOAuthError.credentialStore }
        return credential
    }

    public func store(_ credential: RemoteOAuthCredential) throws {
        let data = try JSONEncoder().encode(credential)
        try secrets.store(String(decoding: data, as: UTF8.self), for: identifier(credential.configuration.resourceURL))
    }

    public func delete(resourceURL: String) throws {
        try secrets.delete(for: identifier(try RemoteOAuthClient.canonicalResource(resourceURL)))
    }

    private func identifier(_ resource: String) -> String {
        "oauth.resource." + SHA256.hash(data: Data(resource.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}

/// OAuth 2.1 assistance for explicit Remote Doctor sign-in (§7.3).
/// Spec: MCP 2026-07-28 authorization + RFC 9728/8414/9207/8707. No automatic browser launches.
public struct RemoteOAuthClient: Sendable {
    private let transport: RemoteHTTPTransport
    public init(transport: RemoteHTTPTransport = .init()) { self.transport = transport }

    /// The configured endpoint in one spelling: lowercase scheme and host, no
    /// default port, no trailing slash. The query stays, because two servers
    /// on one path told apart by a query are two sign-ins.
    public static func canonicalResource(_ text: String) throws -> String {
        let url = try RemoteHTTPTransport.endpoint(text)
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { throw RemoteOAuthError.discovery }
        components.scheme = components.scheme?.lowercased()
        components.host = components.host?.lowercased()
        if components.port == defaultPort(components.scheme) { components.port = nil }
        while components.path.hasSuffix("/") { components.path.removeLast() }
        guard let result = components.string else { throw RemoteOAuthError.discovery }
        return result
    }

    private static func defaultPort(_ scheme: String?) -> Int? {
        switch scheme?.lowercased() { case "https": 443; case "http": 80; default: nil }
    }

    /// Whether protected-resource metadata may speak for the endpoint: same
    /// origin, and a path that is the endpoint's or a parent of it on a segment
    /// boundary. Root metadata describes the origin (RFC 9728 §3.3), so an exact
    /// match would refuse every server found through the root fallback.
    static func resource(_ described: String, covers endpoint: URL) -> Bool {
        guard let url = try? RemoteHTTPTransport.endpoint(described), sameOrigin(url, endpoint) else { return false }
        if let query = url.query, query != endpoint.query { return false }
        var parent = url.path
        while parent.hasSuffix("/") { parent.removeLast() }
        var path = endpoint.path
        while path.hasSuffix("/") { path.removeLast() }
        return parent.isEmpty || path == parent || path.hasPrefix(parent + "/")
    }

    /// - Parameters:
    ///   - authorization: the saved credential's headers, when re-signing in.
    ///     The probe then carries them, so a server that needs more scope says
    ///     which in a 403 `insufficient_scope` challenge (step-up).
    ///   - previousScopes: what the saved credential was granted. A step-up asks
    ///     for these and the challenged ones together, so it gains scope rather
    ///     than trading one for another.
    public func discover(resourceURL: String, challenge: String? = nil,
                         authorization: [String: String]? = nil, previousScopes: [String] = []) throws -> RemoteOAuthConfiguration {
        let canonical = try Self.canonicalResource(resourceURL)
        let resource = try RemoteHTTPTransport.endpoint(canonical)
        let deadline = Date().addingTimeInterval(10)
        var challenge = challenge
        if challenge == nil, let authorization {
            var request = URLRequest(url: resource)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
            request.setValue(MCPStdioClient.modernProtocolVersion, forHTTPHeaderField: "MCP-Protocol-Version")
            request.setValue("tools/list", forHTTPHeaderField: "Mcp-Method")
            for (key, value) in authorization { request.setValue(value, forHTTPHeaderField: key) }
            request.httpBody = Data(MCPStdioClient.request(id: 1, method: "tools/list", params: [:], modern: true).utf8)
            if let reply = try? transport.send(request, deadline: deadline, limit: 256 * 1024, responseID: 1),
               [401, 403].contains(reply.status) {
                challenge = reply.header("www-authenticate")
            }
        }
        if challenge == nil {
            var request = URLRequest(url: resource)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
            request.httpBody = Data("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"\(MCPHTTPClient.preferredProtocolVersion)\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Kytto\",\"version\":\"1.0\"}}}".utf8)
            let reply = try transport.send(request, deadline: deadline, limit: 256 * 1024, responseID: 1)
            challenge = reply.header("www-authenticate")
        }
        let parameters = Self.challengeParameters(challenge)
        var candidates: [URL] = []
        if let hinted = parameters["resource_metadata"] {
            let url = try RemoteHTTPTransport.endpoint(hinted, allowLoopbackHTTP: false)
            // A resource metadata URI can be cross-origin in the spec. This initial
            // implementation requires the resource's own origin, preventing SSRF via challenges.
            guard Self.sameOrigin(url, resource) else { throw RemoteOAuthError.resourceMismatch }
            candidates = [url]
        } else {
            candidates = Self.wellKnown(resource, suffix: "oauth-protected-resource", includeAppend: false)
            if !resource.path.isEmpty && resource.path != "/" {
                var root = URLComponents(url: resource, resolvingAgainstBaseURL: false)!
                root.path = "/.well-known/oauth-protected-resource"; root.query = nil
                candidates.append(root.url!)
            }
        }
        let metadata = try firstMetadata(candidates, deadline: deadline)
        guard let describedResource = metadata.root["resource"]?.stringValue,
              Self.resource(describedResource, covers: resource) else { throw RemoteOAuthError.resourceMismatch }
        guard let issuers = metadata.root["authorization_servers"]?.elements?.compactMap(\.stringValue),
              let issuer = issuers.first, issuers.count <= 16 else { throw RemoteOAuthError.discovery }
        let issuerURL = try RemoteHTTPTransport.endpoint(issuer, allowLoopbackHTTP: false)
        guard issuerURL.query == nil else { throw RemoteOAuthError.discovery }
        var discoveryURLs = Self.wellKnown(issuerURL, suffix: "oauth-authorization-server", includeAppend: false)
        discoveryURLs += Self.wellKnown(issuerURL, suffix: "openid-configuration", includeAppend: true)
        let authorization = try firstMetadata(discoveryURLs, deadline: deadline)
        guard authorization.root["issuer"]?.stringValue == issuer else { throw RemoteOAuthError.issuerMismatch }
        guard authorization.root["code_challenge_methods_supported"]?.elements?.contains(where: { $0.stringValue == "S256" }) == true else {
            throw RemoteOAuthError.pkceUnsupported
        }
        if let modes = authorization.root["response_types_supported"]?.elements,
           !modes.contains(where: { $0.stringValue == "code" }) { throw RemoteOAuthError.discovery }
        func endpoint(_ key: String) throws -> String {
            guard let value = authorization.root[key]?.stringValue else { throw RemoteOAuthError.discovery }
            _ = try RemoteHTTPTransport.endpoint(value, allowLoopbackHTTP: false)
            return value
        }
        var scopes: [String]
        if let required = parameters["scope"] { scopes = try Self.scopes(required) }
        else {
            scopes = metadata.root["scopes_supported"]?.elements?.compactMap(\.stringValue) ?? []
            _ = try Self.scopes(scopes.joined(separator: " "))
        }
        if !previousScopes.isEmpty, !scopes.isEmpty {
            scopes = try Self.scopes((scopes + previousScopes).joined(separator: " "))
        }
        let registration = authorization.root["registration_endpoint"]?.stringValue
        if let registration { _ = try RemoteHTTPTransport.endpoint(registration, allowLoopbackHTTP: false) }
        return RemoteOAuthConfiguration(
            resourceURL: canonical, resource: describedResource, issuer: issuer,
            authorizationEndpoint: try endpoint("authorization_endpoint"), tokenEndpoint: try endpoint("token_endpoint"),
            registrationEndpoint: registration, scopes: scopes,
            authorizationResponseIssuerSupported: authorization.root["authorization_response_iss_parameter_supported"]?.boolValue == true
        )
    }

    /// Optional compatibility fallback; called only as part of an explicit sign-in.
    public func register(configuration: RemoteOAuthConfiguration, redirectURI: String) throws -> String {
        try Self.validateRedirect(redirectURI)
        guard let endpoint = configuration.registrationEndpoint else { throw RemoteOAuthError.registrationUnavailable }
        var request = URLRequest(url: try RemoteHTTPTransport.endpoint(endpoint, allowLoopbackHTTP: false))
        request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "client_name": "Kytto MCP", "redirect_uris": [redirectURI],
            // Required in registration requests since MCP 2026-07-28 (SEP-837).
            "application_type": "native",
            "token_endpoint_auth_method": "none", "grant_types": ["authorization_code", "refresh_token"],
            "response_types": ["code"],
        ])
        let reply = try transport.send(request, deadline: Date().addingTimeInterval(10), limit: 256 * 1024)
        guard (200..<300).contains(reply.status), let document = Self.document(reply.body),
              let clientID = document.root["client_id"]?.stringValue, Self.validClient(clientID),
              document.root["client_secret"]?.stringValue?.isEmpty != false,
              document.root["token_endpoint_auth_method"]?.stringValue.map({ $0 == "none" }) != false else {
            throw RemoteOAuthError.invalidClient
        }
        if let redirects = document.root["redirect_uris"]?.elements?.compactMap(\.stringValue), !redirects.contains(redirectURI) {
            throw RemoteOAuthError.invalidClient
        }
        return clientID
    }

    public func prepareAuthorization(configuration: RemoteOAuthConfiguration, clientID: String, redirectURI: String) throws -> RemoteOAuthPendingAuthorization {
        guard Self.validClient(clientID) else { throw RemoteOAuthError.invalidClient }
        try Self.validateRedirect(redirectURI)
        let verifier = try Self.random()
        let state = try Self.random()
        let challenge = Self.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
        let endpoint = try RemoteHTTPTransport.endpoint(configuration.authorizationEndpoint, allowLoopbackHTTP: false)
        var components = URLComponents(url: endpoint, resolvingAgainstBaseURL: false)!
        let reserved: Set<String> = ["response_type", "client_id", "redirect_uri", "state", "code_challenge", "code_challenge_method", "resource", "scope"]
        guard !(components.queryItems ?? []).contains(where: { reserved.contains($0.name) }) else { throw RemoteOAuthError.discovery }
        var items = components.queryItems ?? []
        items += [URLQueryItem(name: "response_type", value: "code"), .init(name: "client_id", value: clientID),
                  .init(name: "redirect_uri", value: redirectURI), .init(name: "state", value: state),
                  .init(name: "code_challenge", value: challenge), .init(name: "code_challenge_method", value: "S256"),
                  .init(name: "resource", value: configuration.resourceIndicator)]
        if !configuration.scopes.isEmpty { items.append(.init(name: "scope", value: configuration.scopes.joined(separator: " "))) }
        components.queryItems = items
        guard let url = components.url else { throw RemoteOAuthError.discovery }
        return RemoteOAuthPendingAuthorization(url: url, configuration: configuration, clientID: clientID,
                                               redirectURI: redirectURI, verifier: verifier, state: state)
    }

    public func exchange(pending: RemoteOAuthPendingAuthorization, callbackURL: URL) throws -> RemoteOAuthCredential {
        try pending.consume()
        guard callbackURL.absoluteString.utf8.count <= 16_384,
              var actual = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false), actual.fragment == nil,
              let expected = URLComponents(string: pending.redirectURI) else { throw RemoteOAuthError.invalidCallback }
        let query = actual.percentEncodedQuery ?? ""
        actual.query = nil
        guard actual.string == expected.string else { throw RemoteOAuthError.invalidCallback }
        var parameters: [String: String] = [:]
        for item in query.split(separator: "&", omittingEmptySubsequences: false) {
            let parts = item.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2,
                  let key = String(parts[0]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  let value = String(parts[1]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  parameters[key] == nil else { throw RemoteOAuthError.invalidCallback }
            parameters[key] = value
        }
        guard parameters["state"] == pending.state else { throw RemoteOAuthError.invalidCallback }
        if let issuer = parameters["iss"] {
            guard issuer == pending.configuration.issuer else { throw RemoteOAuthError.issuerMismatch }
        } else if pending.configuration.authorizationResponseIssuerSupported { throw RemoteOAuthError.issuerMismatch }
        guard parameters["error"] == nil else { throw RemoteOAuthError.authorizationRejected }
        guard let code = parameters["code"], !code.isEmpty, code.utf8.count <= 8192 else { throw RemoteOAuthError.invalidCallback }
        return try tokens(configuration: pending.configuration, clientID: pending.clientID, fields: [
            "grant_type": "authorization_code", "code": code, "redirect_uri": pending.redirectURI,
            "code_verifier": pending.verifier,
        ], previous: nil)
    }

    public func refresh(_ credential: RemoteOAuthCredential) throws -> RemoteOAuthCredential {
        guard let refresh = credential.refreshToken, !refresh.isEmpty else { throw RemoteOAuthError.refreshUnavailable }
        return try tokens(configuration: credential.configuration, clientID: credential.clientID,
                          fields: ["grant_type": "refresh_token", "refresh_token": refresh], previous: credential)
    }

    private func tokens(configuration: RemoteOAuthConfiguration, clientID: String, fields: [String: String], previous: RemoteOAuthCredential?) throws -> RemoteOAuthCredential {
        var fields = fields
        fields["client_id"] = clientID; fields["resource"] = configuration.resourceIndicator
        var request = URLRequest(url: try RemoteHTTPTransport.endpoint(configuration.tokenEndpoint, allowLoopbackHTTP: false))
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = Data(Self.form(fields).utf8)
        let reply = try transport.send(request, deadline: Date().addingTimeInterval(10), limit: 256 * 1024)
        guard (200..<300).contains(reply.status), let document = Self.document(reply.body),
              let token = document.root["access_token"]?.stringValue, !token.isEmpty, token.utf8.count <= 64 * 1024,
              token.utf8.allSatisfy({ $0 >= 0x21 && $0 <= 0x7E }),
              document.root["token_type"]?.stringValue?.lowercased() == "bearer" else { throw RemoteOAuthError.invalidToken }
        // The authorization server may grant other scopes than requested
        // (RFC 6749 §3.3) — its default when none were asked for, or a
        // normalised spelling. What it granted is what is recorded.
        let granted: [String]
        if let scope = document.root["scope"]?.stringValue { granted = try Self.scopes(scope) }
        else { granted = previous?.scopes ?? configuration.scopes }
        var expiry: Date?
        if let raw = document.root["expires_in"], !raw.isNull {
            // Some servers send it as a string.
            guard let seconds = raw.numberValue ?? raw.stringValue.flatMap(Double.init),
                  seconds.isFinite, seconds > 0, seconds <= 366 * 24 * 3600 else { throw RemoteOAuthError.invalidToken }
            expiry = Date().addingTimeInterval(seconds)
        }
        let refresh = document.root["refresh_token"]?.stringValue ?? previous?.refreshToken
        if let refresh, refresh.isEmpty || refresh.utf8.count > 64 * 1024 || refresh.contains("\r") || refresh.contains("\n") { throw RemoteOAuthError.invalidToken }
        return RemoteOAuthCredential(configuration: configuration, clientID: clientID, scopes: granted,
                                     expiresAt: expiry, accessToken: token, refreshToken: refresh)
    }

    /// The first candidate that answers with a JSON object. Clients must try
    /// the well-known locations in order, so a 401 from auth middleware, a
    /// redirect (never followed) or an HTML catch-all page moves on to the next
    /// one rather than ending discovery.
    private func firstMetadata(_ candidates: [URL], deadline: Date) throws -> JSONDocument {
        for candidate in candidates {
            var request = URLRequest(url: candidate)
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            let reply = try transport.send(request, deadline: deadline, limit: 256 * 1024)
            if reply.status == 200, let document = Self.document(reply.body) { return document }
        }
        throw RemoteOAuthError.discovery
    }

    private static func document(_ data: Data) -> JSONDocument? {
        guard let text = String(data: data, encoding: .utf8), let document = RemoteHTTPTransport.document(text), document.root.members != nil else { return nil }
        return document
    }

    private static func wellKnown(_ url: URL, suffix: String, includeAppend: Bool) -> [URL] {
        var base = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        // RFC 8414 §3.1: a trailing slash is removed before insertion.
        var path = base.percentEncodedPath
        while path.hasSuffix("/") { path.removeLast() }
        base.query = nil; base.percentEncodedPath = "/.well-known/" + suffix + path
        var result = [base.url!]
        if includeAppend && !path.isEmpty {
            base.percentEncodedPath = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                .withLeadingSlash + "/.well-known/" + suffix
            result.append(base.url!)
        }
        return result
    }

    private static func sameOrigin(_ a: URL, _ b: URL) -> Bool {
        a.scheme?.lowercased() == b.scheme?.lowercased() && a.host?.lowercased() == b.host?.lowercased()
            && (a.port ?? defaultPort(a.scheme)) == (b.port ?? defaultPort(b.scheme))
    }

    private static func validClient(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 8192 && value.utf8.allSatisfy { $0 >= 0x21 && $0 <= 0x7E }
    }

    private static func validateRedirect(_ text: String) throws {
        guard let url = URL(string: text), let scheme = url.scheme, !scheme.isEmpty,
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
              (scheme != "http" && scheme != "https" && scheme.contains(".")) ||
                (scheme == "http" && ["127.0.0.1", "::1", "[::1]"].contains(url.host ?? "") && url.port != nil) else {
            throw RemoteOAuthError.invalidRedirect
        }
    }

    private static func random() throws -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw RemoteOAuthError.invalidCallback }
        return base64URL(Data(bytes))
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    private static func scopes(_ text: String) throws -> [String] {
        guard text.utf8.count <= 8192,
              text.utf8.allSatisfy({ $0 == 0x20 || $0 == 0x21 || (0x23...0x5B).contains($0) || (0x5D...0x7E).contains($0) }) else { throw RemoteOAuthError.discovery }
        return Array(Set(text.split(separator: " ").map(String.init))).sorted()
    }

    private static func form(_ fields: [String: String]) -> String {
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return fields.sorted(by: { $0.key < $1.key }).map {
            ($0.key.addingPercentEncoding(withAllowedCharacters: allowed) ?? "") + "=" + ($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
        }.joined(separator: "&")
    }

    /// Bounded RFC 6750 Bearer challenge parser with quoted comma/escape handling.
    static func challengeParameters(_ header: String?) -> [String: String] {
        guard let header, header.utf8.count <= 16_384,
              let bearer = header.range(of: "Bearer ", options: [.caseInsensitive]),
              bearer.lowerBound == header.startIndex || header[header.index(before: bearer.lowerBound)] == " " || header[header.index(before: bearer.lowerBound)] == "," else { return [:] }
        var remaining = header[bearer.upperBound...]
        var parameters: [String: String] = [:]
        while !remaining.isEmpty {
            remaining = remaining.drop(while: { $0 == " " || $0 == "," })
            let key = remaining.prefix(while: { $0.isLetter || $0.isNumber || $0 == "_" })
            remaining = remaining.dropFirst(key.count).drop(while: { $0 == " " })
            guard !key.isEmpty, remaining.first == "=" else { break }
            remaining = remaining.dropFirst().drop(while: { $0 == " " })
            var value = ""
            if remaining.first == "\"" {
                remaining = remaining.dropFirst()
                var closed = false
                while let next = remaining.first {
                    remaining = remaining.dropFirst()
                    if next == "\"" { closed = true; break }
                    if next == "\\", let escaped = remaining.first { value.append(escaped); remaining = remaining.dropFirst() }
                    else { value.append(next) }
                }
                guard closed else { return [:] }
            } else {
                let unquoted = remaining.prefix(while: { $0 != "," && $0 != " " })
                value = String(unquoted); remaining = remaining.dropFirst(unquoted.count)
            }
            guard parameters[String(key).lowercased()] == nil else { return [:] }
            parameters[String(key).lowercased()] = value
        }
        return parameters
    }
}

private extension String {
    var withLeadingSlash: String { hasPrefix("/") ? self : "/" + self }
}
