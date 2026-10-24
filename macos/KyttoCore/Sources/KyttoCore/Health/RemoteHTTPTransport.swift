import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Bounded, credential-free HTTP used only after the user's explicit request (§6, §7.3).
public struct RemoteHTTPTransport: Sendable {
    struct Reply: Sendable {
        let status: Int
        let headers: [String: String]
        let body: Data
        func header(_ name: String) -> String? { headers[name.lowercased()] }
    }

    typealias Sender = @Sendable (URLRequest, Date, Int, Int?) throws -> Reply
    private let sender: Sender

    public init() {
        sender = { request, deadline, limit, responseID in
            let transfer = RemoteHTTPTransfer(limit: limit, responseID: responseID)
            return try transfer.perform(request, deadline: deadline)
        }
    }

    init(sender: @escaping Sender) { self.sender = sender }

    /// A `responseID` meaning "return as soon as the first complete SSE event
    /// has arrived", for probing a stream that never ends on its own.
    static let firstEventOnly = -1

    func send(_ request: URLRequest, deadline: Date, limit: Int, responseID: Int? = nil) throws -> Reply {
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        let reply = try sender(request, deadline, limit, responseID)
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        guard reply.body.count <= limit else { throw RemoteHTTPError.responseTooLarge }
        return reply
    }

    /// Limit nesting before invoking the lossless parser on untrusted remote JSON.
    static func document(_ text: String) -> JSONDocument? {
        var depth = 0
        var quoted = false
        var escaped = false
        for byte in text.utf8 {
            if quoted {
                if escaped { escaped = false }
                else if byte == 0x5C { escaped = true }
                else if byte == 0x22 { quoted = false }
            } else if byte == 0x22 { quoted = true }
            else if byte == 0x7B || byte == 0x5B {
                depth += 1
                if depth > 128 { return nil }
            } else if byte == 0x7D || byte == 0x5D { depth -= 1 }
        }
        guard let document = try? JSONDocument.parse(text) else { return nil }
        func unambiguous(_ node: JSONNode) -> Bool {
            if let members = node.members {
                guard Set(members.map(\.key)).count == members.count else { return false }
                return members.allSatisfy { unambiguous($0.value) }
            }
            return node.elements?.allSatisfy(unambiguous) ?? true
        }
        return unambiguous(document.root) ? document : nil
    }

    static func endpoint(_ text: String, allowLoopbackHTTP: Bool = true) throws -> URL {
        guard text.utf8.count <= 8192, let url = URL(string: text),
              let host = url.host, !host.isEmpty, url.user == nil, url.password == nil,
              url.fragment == nil,
              url.scheme?.lowercased() == "https" ||
                (allowLoopbackHTTP && url.scheme?.lowercased() == "http" &&
                 ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host.lowercased()))
        else { throw RemoteHTTPError.invalidEndpoint }
        return url
    }
}

public enum RemoteHTTPError: Error, LocalizedError, Sendable {
    case invalidEndpoint, invalidHeaders, unresolvedCredential, timedOut, unavailable, responseTooLarge
    /// The endpoint speaks the deprecated HTTP+SSE transport, which Kytto does
    /// not inspect — not a broken server.
    case legacySSE
    case httpStatus(Int)
    case authorizationRequired(challenge: String?)
    case protocolFailure(String)

    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint: "Use an HTTPS endpoint, or HTTP on the loopback interface, without embedded credentials or a fragment."
        case .invalidHeaders: "The configured HTTP headers are invalid or override a transport header."
        case .unresolvedCredential: "A configured HTTP credential variable could not be resolved. Supply it in the launching environment or sign in explicitly."
        case .timedOut: "The remote server did not finish the check within the time limit."
        case .unavailable: "The remote server could not be reached. Check its address, network connection and TLS certificate."
        case .responseTooLarge: "The remote response exceeded Kytto's inspection size limit."
        case .legacySSE: "This endpoint uses the legacy HTTP+SSE transport, which Kytto does not inspect. It is deprecated; the server's Streamable HTTP endpoint can be checked instead."
        case .httpStatus(let status): "The remote server returned HTTP \(status)."
        case .authorizationRequired: "The remote server requires authorization. Sign in or check its configured credentials and permissions."
        case .protocolFailure(let detail): "The remote server did not speak MCP as expected: \(detail)"
        }
    }
}

private final class RemoteHTTPTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let condition = NSCondition()
    private var limit: Int
    private static let errorBodyLimit = 16 * 1024
    private let responseID: Int?
    private var reply: RemoteHTTPTransport.Reply?
    private var bytes = Data()
    private var failure: RemoteHTTPError?
    private var completed = false

    init(limit: Int, responseID: Int?) { self.limit = limit; self.responseID = responseID }

    func perform(_ original: URLRequest, deadline: Date) throws -> RemoteHTTPTransport.Reply {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForResource = max(0.05, deadline.timeIntervalSinceNow)
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        var request = original
        request.timeoutInterval = max(0.05, deadline.timeIntervalSinceNow)
        let task = session.dataTask(with: request)
        task.resume()
        condition.lock()
        while !completed && failure == nil {
            if !condition.wait(until: deadline) { failure = .timedOut }
        }
        let error = failure
        let result = reply.map { RemoteHTTPTransport.Reply(status: $0.status, headers: $0.headers, body: bytes) }
        condition.unlock()
        task.cancel()
        session.invalidateAndCancel()
        if let error { throw error }
        guard let result else { throw RemoteHTTPError.unavailable }
        return result
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        // Never forward bearer tokens, signed URLs or metadata requests to another destination.
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else {
            // A 401 that also offers Basic or Negotiate must still arrive as a
            // 401, so its Bearer challenge can be read; Kytto offers no
            // credential for those schemes (credential storage is off).
            completionHandler(.rejectProtectionSpace, nil)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        condition.lock()
        defer { condition.unlock() }
        guard let http = response as? HTTPURLResponse else {
            failure = .unavailable; condition.broadcast(); completionHandler(.cancel); return
        }
        var headers: [String: String] = [:]
        for (key, value) in http.allHeaderFields { headers[String(describing: key).lowercased()] = String(describing: value) }
        reply = .init(status: http.statusCode, headers: headers, body: Data())
        if (400..<500).contains(http.statusCode) {
            // A short error body says which era the server speaks (a modern
            // JSON-RPC error code); read a little of it and no more.
            limit = min(limit, Self.errorBodyLimit)
            completionHandler(.allow)
        } else if http.statusCode >= 300 {
            completed = true; condition.broadcast(); completionHandler(.cancel)
        } else if response.expectedContentLength > Int64(limit) {
            failure = .responseTooLarge; condition.broadcast(); completionHandler(.cancel)
        } else { completionHandler(.allow) }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        condition.lock()
        defer { condition.unlock() }
        guard !completed, failure == nil else { return }
        guard bytes.count <= limit - data.count else {
            if let status = reply?.status, status >= 400 {
                completed = true
            } else {
                failure = .responseTooLarge
            }
            condition.broadcast(); dataTask.cancel(); return
        }
        bytes.append(data)
        if responseID == RemoteHTTPTransport.firstEventOnly,
           String(decoding: bytes, as: UTF8.self).replacingOccurrences(of: "\r\n", with: "\n").contains("\n\n") {
            completed = true; condition.broadcast(); dataTask.cancel(); return
        }
        if let responseID, reply?.header("content-type")?.lowercased().hasPrefix("text/event-stream") == true,
           MCPHTTPClient.sseMessages(bytes).contains(where: { message in
               guard let document = RemoteHTTPTransport.document(message) else { return false }
               return document.root["id"]?.numberValue == Double(responseID) &&
                   (document.root["result"] != nil || document.root["error"] != nil)
           }) {
            // A server may keep SSE open after the answer. Do not wait for EOF.
            completed = true; condition.broadcast(); dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        condition.lock()
        defer { condition.unlock() }
        guard !completed, failure == nil else { return }
        if let error {
            failure = (error as NSError).code == NSURLErrorTimedOut ? .timedOut : .unavailable
        } else { completed = true }
        condition.broadcast()
    }
}
