import Foundation

/// Read preserved transport credentials natively; values never enter IPC (§6).
public enum RemoteHTTPHeaders {
    public static func resolve(definitionSource: String, environment: [String: String], omittingAuthorization: Bool = false) throws -> [String: String] {
        var headers: [String: String] = [:]
        var bearerVariable: String?
        var environmentHeaders: [String: String] = [:]
        if let document = try? JSONDocument.parse(definitionSource) {
            for key in ["headers", "http_headers"] {
                if let value = document.root[key], value.members == nil { throw RemoteHTTPError.invalidHeaders }
                for member in document.root[key]?.members ?? [] {
                    guard let value = member.value.stringValue else { throw RemoteHTTPError.invalidHeaders }
                    headers[member.key] = value
                }
            }
            for member in document.root["env_http_headers"]?.members ?? [] {
                guard let value = member.value.stringValue else { throw RemoteHTTPError.invalidHeaders }
                environmentHeaders[member.key] = value
            }
            bearerVariable = document.root["bearer_token_env_var"]?.stringValue
        } else if let document = try? TOMLDocument.parse(definitionSource) {
            let table = document.tables.first { $0.value("url") != nil }
            let inline = document.tables.flatMap(\.pairs).compactMap { $0.value.inlinePairs }
                .first { $0.contains { $0.name == "url" } } ?? []
            func value(_ key: String) -> TOMLValue? {
                table?.value(key) ?? inline.first { $0.name == key }?.value
            }
            bearerVariable = value("bearer_token_env_var")?.stringValue
            for pair in value("http_headers")?.inlinePairs ?? [] {
                if let name = pair.name, let value = pair.value.stringValue { headers[name] = value }
                else { throw RemoteHTTPError.invalidHeaders }
            }
            for pair in value("env_http_headers")?.inlinePairs ?? [] {
                if let name = pair.name, let value = pair.value.stringValue { environmentHeaders[name] = value }
                else { throw RemoteHTTPError.invalidHeaders }
            }
            for child in document.tables where table.map({ child.path == $0.path + ["http_headers"] }) == true {
                for pair in child.pairs {
                    if let name = pair.name, let value = pair.value.stringValue { headers[name] = value }
                    else { throw RemoteHTTPError.invalidHeaders }
                }
            }
            for child in document.tables where table.map({ child.path == $0.path + ["env_http_headers"] }) == true {
                for pair in child.pairs {
                    if let name = pair.name, let value = pair.value.stringValue { environmentHeaders[name] = value }
                    else { throw RemoteHTTPError.invalidHeaders }
                }
            }
        }
        if omittingAuthorization {
            headers = headers.filter { $0.key.lowercased() != "authorization" }
            environmentHeaders = environmentHeaders.filter { $0.key.lowercased() != "authorization" }
            bearerVariable = nil
        }
        for (name, variable) in environmentHeaders {
            guard let value = environment[variable], !value.isEmpty else { throw RemoteHTTPError.unresolvedCredential }
            headers[name] = value
        }
        if let bearerVariable {
            guard let value = environment[bearerVariable], !value.isEmpty else { throw RemoteHTTPError.unresolvedCredential }
            headers = headers.filter { $0.key.lowercased() != "authorization" }
            headers["Authorization"] = "Bearer " + value
        }
        for (key, value) in headers {
            headers[key] = try expand(value, environment: environment)
        }
        try MCPHTTPClient.validate(headers)
        return headers
    }

    private static func expand(_ value: String, environment: [String: String]) throws -> String {
        var result = ""
        var remaining = value[...]
        while let open = remaining.range(of: "${") {
            result += remaining[..<open.lowerBound]
            guard let close = remaining[open.upperBound...].firstIndex(of: "}") else {
                throw RemoteHTTPError.unresolvedCredential
            }
            var key = String(remaining[open.upperBound..<close])
            if key.hasPrefix("env:") { key = String(key.dropFirst(4)) }
            guard let replacement = environment[key], !replacement.isEmpty else {
                throw RemoteHTTPError.unresolvedCredential
            }
            // A credential is literal data. Never interpret placeholder-looking
            // text inside it as a request for another environment secret (§6).
            result += replacement
            remaining = remaining[remaining.index(after: close)...]
        }
        result += remaining
        return result
    }
}
