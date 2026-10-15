import AppKit
import AuthenticationServices
import Foundation
import KyttoCore

/// OAuth and all tokens remain native. The web sends only a discovered server
/// id; endpoints come from validated protected-resource and issuer metadata.
@MainActor
final class RemoteSignInController: NSObject, ASWebAuthenticationPresentationContextProviding {
    private var session: ASWebAuthenticationSession?
    private var signingIn = false
    private var continuation: CheckedContinuation<URL, Error>?
    private let redirectURI = "app.kytto.oauth://callback"

    func signIn(serverID: String, model: AppModel) async throws {
        guard !signingIn else { throw CommandError.badArgument("A sign-in is already open.") }
        signingIn = true
        defer { signingIn = false }
        let resource = try model.remoteServerURL(serverID: serverID)
        let client = RemoteOAuthClient()
        let configuration = try await Task.detached(priority: .userInitiated) {
            try client.discover(resourceURL: resource)
        }.value
        let consent = NSAlert()
        consent.messageText = "Sign in to remote MCP server"
        consent.informativeText = "Authorization provider: \(configuration.issuer)\nRequested scopes: \(configuration.scopes.isEmpty ? "No scopes advertised" : configuration.scopes.joined(separator: ", "))\n\nKytto stores its authorization in Keychain for Remote Doctor checks. Your other MCP clients keep their own sign-ins."
        consent.addButton(withTitle: "Continue")
        consent.addButton(withTitle: "Cancel")
        guard consent.runModal() == .alertFirstButtonReturn else { return }
        let clientID: String
        if configuration.registrationEndpoint != nil {
            let redirect = redirectURI
            clientID = try await Task.detached(priority: .userInitiated) {
                try client.register(configuration: configuration, redirectURI: redirect)
            }.value
        } else {
            let prompt = NSAlert()
            prompt.messageText = "OAuth client ID required"
            prompt.informativeText = "This provider requires a public client registered for callback \(redirectURI). Enter its client ID. No client secret is accepted."
            let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 380, height: 24))
            prompt.accessoryView = field
            prompt.addButton(withTitle: "Sign in")
            prompt.addButton(withTitle: "Cancel")
            guard prompt.runModal() == .alertFirstButtonReturn else { return }
            clientID = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let pending = try client.prepareAuthorization(configuration: configuration, clientID: clientID, redirectURI: redirectURI)
        let callback = try await authenticate(url: pending.authorizationURL)
        let credential = try await Task.detached(priority: .userInitiated) {
            try client.exchange(pending: pending, callbackURL: callback)
        }.value
        try model.saveRemoteAuthorization(credential, serverID: serverID)
        _ = try await model.checkHealth(serverID: serverID)
    }

    private func authenticate(url: URL) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: "app.kytto.oauth") { callback, _ in
                Task { @MainActor in
                    self.complete(callback: callback)
                }
            }
            session.presentationContextProvider = self
            session.prefersEphemeralWebBrowserSession = true
            self.session = session
            if !session.start() {
                complete(callback: nil)
            }
        }
    }

    private func complete(callback: URL?) {
        guard let continuation else { return }
        self.continuation = nil
        session = nil
        if let callback { continuation.resume(returning: callback) }
        else { continuation.resume(throwing: CommandError.badArgument("Sign-in was cancelled or could not complete.")) }
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        NSApplication.shared.keyWindow ?? NSApplication.shared.windows.first ?? NSWindow()
    }
}
