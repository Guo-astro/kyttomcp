import Foundation
import Testing
@testable import KyttoCore

/// Regressions found by the write-pipeline audit. Each test names the way a
/// user's config used to be damaged, and pins the behaviour that replaced it
/// (§6.3, §6.4, §7.12).
@Suite("Write pipeline — audit regressions")
struct WritePipelineRegressionTests {
    private let cursor = ".cursor/mcp.json"
    private let claudeDesktop = "Library/Application Support/Claude/claude_desktop_config.json"
    private let codex = ".codex/config.toml"

    private func parkedSecrets(_ harness: ToggleHarness) -> SecretsService {
        SecretsService(
            home: harness.home.root,
            writer: ConfigWriter(backups: harness.backups),
            ledger: harness.ledger,
            store: InMemorySecretStore(),
            parkStore: harness.parkStore
        )
    }

    // MARK: 1 — editing a switched-off copy

    @Test("editing a server switched off in a presence client keeps it off and edits the held copy")
    func editParkedStaysOff() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let shared = #"{"mcpServers": {"github": {"command": "npx", "args": ["-y", "old"]}}}"#
        try harness.home.write(shared, to: cursor)
        try harness.home.write(shared, to: ".claude.json")
        _ = try harness.toggles.setEnabled(false, server: harness.server("github"), in: .cursor)
        let cursorBefore = try harness.text(cursor)

        let server = try harness.server("github")
        var draft = ServerDraft(editing: server)
        draft.args = ["-y", "new"]
        let result = try harness.authoring.update(
            draft, originalName: "github", server: server, existing: harness.discover().servers
        )

        #expect(result.parkedUpdated == [.cursor])
        #expect(!result.changed.contains(.cursor))
        #expect(try harness.text(cursor) == cursorBefore, "the switched-off copy must not reappear in the file")
        #expect(try harness.server("github").enabledIn[.cursor] == .disabled)
        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "github")?.sourceText.contains("new") == true)

        // Switching it back on brings the edit, not the old bytes.
        _ = try harness.toggles.setEnabled(true, server: harness.server("github"), in: .cursor)
        #expect(try harness.text(cursor).contains(#""new""#))
        #expect(try !harness.text(cursor).contains(#""old""#))
    }

    @Test("a definition in the file outranks a stale parked copy")
    func fileWinsOverStalePark() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"github": {"command": "edited-by-hand"}}}"#, to: cursor)
        try harness.parkStore.park(clientID: .cursor, serverName: "github", sourceText: #"{"command": "old"}"#)

        let server = try harness.server("github")
        #expect(server.enabledIn[.cursor] == .enabled)
        #expect(server.command == "edited-by-hand")

        // Switching "on" must not restore the held bytes over the file.
        let before = try harness.text(cursor)
        _ = try harness.toggles.setEnabled(true, server: server, in: .cursor)
        #expect(try harness.text(cursor) == before)
    }

    // MARK: 2 — restore and parked copies

    @Test("a backup's server names come from the backup, not from the model parks feed")
    func restoreDecidesFromTheBackup() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"gh": {"command": "x"}}}"#, to: cursor)
        _ = harness.discover()
        // Adding figma takes a backup of the file without it.
        _ = try harness.authoring.create(
            ServerDraft(name: "figma", transport: .sse, url: "https://figma.example/sse"),
            in: [.cursor], existing: harness.discover().servers
        )
        _ = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)

        // Discovery draws the parked copy as Cursor's definition…
        #expect(try harness.server("figma").definitionsByClient[.cursor] != nil)
        // …but the oldest backup does not define figma, so restoring it must not
        // count figma as restored and drop its only copy.
        let oldest = try #require(harness.backups.list(clientID: .cursor).last)
        let names = try #require(harness.backups.serverNames(in: oldest, home: harness.home.root))
        #expect(names == ["gh"])
    }

    @Test("a backup of a file that is not a server map names no servers")
    func denyListBackupNamesNothing() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")
        _ = try harness.toggles.setEnabled(false, server: harness.server("context7"), in: .claudeCode)
        let backup = try #require(harness.backups.list(clientID: .claudeCode).first)
        #expect(harness.backups.serverNames(in: backup, home: harness.home.root) == nil)
    }

    // MARK: 3 — edits splice, they do not rebuild

    @Test("saving an unchanged JSON server writes nothing, and an edit keeps headers, cwd and envFile")
    func jsonEditKeepsUnknownKeys() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = """
        {
          "mcpServers": {
            "remote": {
              "type": "http",
              "url": "https://x.example/mcp",
              "headers": { "Authorization": "Bearer abc" }
            },
            "local": {
              "command": "node",
              "args": ["a.js"],
              "cwd": "/Users/example/p",
              "envFile": ".env",
              "env": { "PORT": 8080, "API_KEY": "s" }
            }
          }
        }
        """
        try harness.home.write(original, to: cursor)

        for name in ["remote", "local"] {
            let server = try harness.server(name)
            let draft = ServerDraft(editing: server).mergingSecrets(from: server)
            let result = try harness.authoring.update(
                draft, originalName: name, server: server, existing: harness.discover().servers
            )
            #expect(result.changed.isEmpty, "a Save with no changes must not touch \(name)")
        }
        #expect(try harness.text(cursor) == original)

        let local = try harness.server("local")
        var draft = ServerDraft(editing: local).mergingSecrets(from: local)
        draft.args = ["b.js"]
        _ = try harness.authoring.update(draft, originalName: "local", server: local, existing: harness.discover().servers)
        let after = try harness.text(cursor)
        #expect(after == original.replacingOccurrences(of: #"["a.js"]"#, with: #"["b.js"]"#))

        let remote = try harness.server("remote")
        var remoteDraft = ServerDraft(editing: remote)
        remoteDraft.url = "https://y.example/mcp"
        _ = try harness.authoring.update(remoteDraft, originalName: "remote", server: remote, existing: harness.discover().servers)
        #expect(try harness.text(cursor).contains(#""headers": { "Authorization": "Bearer abc" }"#))
        #expect(try harness.text(cursor).contains("https://y.example/mcp"))
    }

    @Test("a Codex edit keeps timeouts, tool lists, headers and enabled = false")
    func tomlEditKeepsUnknownKeys() throws {
        try inlineEnvSurvivesANoOpSave()
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = """
        [mcp_servers.gh]
        command = "npx"
        args = ["-y", "gh"]
        startup_timeout_sec = 60
        enabled_tools = ["search"]
        enabled = false # off for now

        [mcp_servers.gh.env]
        TOKEN = "t"

        [mcp_servers.remote]
        url = "https://x.example/mcp"
        bearer_token_env_var = "X_TOKEN"
        http_headers = { "X-Org" = "acme" }
        """
        try harness.home.write(original, to: codex)

        let gh = try harness.server("gh")
        _ = try harness.authoring.update(
            ServerDraft(editing: gh).mergingSecrets(from: gh),
            originalName: "gh", server: gh, existing: harness.discover().servers
        )
        #expect(try harness.text(codex) == original)

        var draft = ServerDraft(editing: gh).mergingSecrets(from: gh)
        draft.args = ["-y", "gh@2"]
        draft.env.append(EnvEntry(key: "EXTRA", value: "1"))
        _ = try harness.authoring.update(draft, originalName: "gh", server: gh, existing: harness.discover().servers)
        let expected = original
            .replacingOccurrences(of: #"args = ["-y", "gh"]"#, with: #"args = ["-y", "gh@2"]"#)
            .replacingOccurrences(of: "TOKEN = \"t\"\n", with: "TOKEN = \"t\"\nEXTRA = \"1\"\n")
        #expect(try harness.text(codex) == expected)

        let remote = try harness.server("remote")
        var remoteDraft = ServerDraft(editing: remote)
        remoteDraft.url = "https://y.example/mcp"
        _ = try harness.authoring.update(remoteDraft, originalName: "remote", server: remote, existing: harness.discover().servers)
        #expect(try harness.text(codex).contains(#"bearer_token_env_var = "X_TOKEN""#))
        #expect(try harness.text(codex).contains(#"http_headers = { "X-Org" = "acme" }"#))
    }

    @Test("renaming a JSON server changes its key and nothing else")
    func renameIsTheKeyOnly() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = #"{"mcpServers": {"a": {"command": "x", "cwd": "/Users/example"}, "b": {"command": "y"}}}"#
        try harness.home.write(original, to: cursor)
        let server = try harness.server("a")
        var draft = ServerDraft(editing: server)
        draft.name = "renamed"
        _ = try harness.authoring.update(draft, originalName: "a", server: server, existing: harness.discover().servers)
        #expect(try harness.text(cursor) == original.replacingOccurrences(of: #""a":"#, with: #""renamed":"#))
    }

    // MARK: 4 — symbolic links

    @Test("a symlinked config stays a link, and the file it names keeps its mode")
    func symlinkedConfig() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"a": {"command": "x"}, "b": {"command": "y"}}}"#, to: "dotfiles/cursor-mcp.json")
        let target = harness.home.root.appending(path: "dotfiles/cursor-mcp.json")
        chmod(target.path, 0o600)
        try harness.home.makeDirectory(".cursor")
        let link = harness.home.root.appending(path: cursor)
        try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: "../dotfiles/cursor-mcp.json")

        _ = try harness.toggles.setEnabled(false, server: harness.server("b"), in: .cursor)

        let linkAttributes = try FileManager.default.attributesOfItem(atPath: link.path)
        #expect(linkAttributes[.type] as? FileAttributeType == .typeSymbolicLink)
        let targetAttributes = try FileManager.default.attributesOfItem(atPath: target.path)
        #expect((targetAttributes[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        #expect(try !String(contentsOf: target, encoding: .utf8).contains(#""b""#))
        // The ledger and the next write still key on the path the client reads.
        _ = try harness.toggles.setEnabled(true, server: harness.server("b"), in: .cursor)
        #expect(try String(contentsOf: target, encoding: .utf8).contains(#""b""#))
    }

    // MARK: 5 — TOML tables that do not sit together

    @Test("editing or deleting a TOML server leaves a server between its tables alone")
    func tomlNonContiguousTables() throws {
        let original = """
        [mcp_servers.a]
        command = "a"

        [mcp_servers.b]
        command = "b"

        [mcp_servers.a.env]
        K = "v"
        """
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(original, to: codex)

        let a = try harness.server("a")
        var draft = ServerDraft(editing: a).mergingSecrets(from: a)
        draft.args = ["1"]
        _ = try harness.authoring.update(draft, originalName: "a", server: a, existing: harness.discover().servers)
        #expect(Set(harness.discover().servers.map(\.name)) == ["a", "b"])
        #expect(try harness.server("a").args == ["1"])

        _ = try harness.authoring.delete(harness.server("a"))
        #expect(try harness.text(codex) == "[mcp_servers.b]\ncommand = \"b\"\n")

        let document = try TOMLDocument.parse(original)
        let replaced = try document.settingServer("a", under: "mcp_servers", to: "[mcp_servers.a]\ncommand = \"z\"")
        #expect(try TOMLDocument.parse(replaced).serverNames(under: "mcp_servers").sorted() == ["a", "b"])
    }

    // MARK: 6 — rotating a secret held in a parked copy

    @Test("rotating a secret in a switched-off copy updates the copy and leaves the file alone")
    func rotateParkedSecret() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"GITHUB_TOKEN": "ghp_old"}}, "other": {"command": "x"}}}"#,
            to: cursor
        )
        _ = try harness.toggles.setEnabled(false, server: harness.server("github"), in: .cursor)
        let fileBefore = try harness.text(cursor)

        let service = parkedSecrets(harness)
        let servers = harness.discover().servers
        let record = try #require(service.scan(servers: servers).first)
        _ = try service.rotate(secretID: record.id, to: "ghp_new", servers: servers, alsoStoreInSecretStore: false)

        #expect(try harness.text(cursor) == fileBefore)
        let parked = try #require(harness.parkStore.parked(clientID: .cursor, serverName: "github"))
        #expect(parked.sourceText == #"{"command": "npx", "env": {"GITHUB_TOKEN": "ghp_new"}}"#)
        #expect(try harness.server("github").enabledIn[.cursor] == .disabled)
    }

    @Test("rotation without the park store refuses a parked copy instead of writing it into the file")
    func rotateParkedWithoutStoreRefuses() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"github": {"command": "npx", "env": {"GITHUB_TOKEN": "ghp_old"}}}}"#, to: cursor)
        _ = try harness.toggles.setEnabled(false, server: harness.server("github"), in: .cursor)
        let fileBefore = try harness.text(cursor)
        let service = harness.secrets()
        let servers = harness.discover().servers
        let record = try #require(service.scan(servers: servers).first)
        #expect(throws: (any Error).self) {
            _ = try service.rotate(secretID: record.id, to: "ghp_new", servers: servers, alsoStoreInSecretStore: false)
        }
        #expect(try harness.text(cursor) == fileBefore)
    }

    // MARK: 7 — the file's own spelling

    @Test("a row named GitHub edits Cursor's github under Cursor's spelling")
    func caseMismatchedNames() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"GitHub": {"command": "npx", "env": {"GITHUB_TOKEN": "ghp_old"}}}}"#, to: claudeDesktop)
        try harness.home.write(#"{"mcpServers": {"github": {"command": "npx", "env": {"GITHUB_TOKEN": "ghp_old"}}}}"#, to: cursor)

        // Rotate: one key per file, not a twin.
        let service = parkedSecrets(harness)
        var servers = harness.discover().servers
        let record = try #require(service.scan(servers: servers).first)
        _ = try service.rotate(secretID: record.id, to: "ghp_new", servers: servers, alsoStoreInSecretStore: false)
        #expect(try harness.text(cursor) == #"{"mcpServers": {"github": {"command": "npx", "env": {"GITHUB_TOKEN": "ghp_new"}}}}"#)

        // Edit: spliced under "github", no "GitHub" added.
        let row = try harness.server("GitHub")
        var draft = ServerDraft(editing: row).mergingSecrets(from: row)
        draft.args = ["z"]
        _ = try harness.authoring.update(draft, originalName: row.name, server: row, existing: harness.discover().servers)
        #expect(try !harness.text(cursor).contains("GitHub"))
        #expect(try harness.text(cursor).contains(#""args": ["z"]"#))

        // Switch off: actually removes Cursor's copy.
        let result = try harness.toggles.setEnabled(false, server: harness.server("GitHub"), in: .cursor)
        #expect(result.wasParked)
        #expect(try !harness.text(cursor).contains("github"))
        _ = try harness.toggles.setEnabled(true, server: harness.server("GitHub"), in: .cursor)
        // Back under the spelling it was held under, not the row's.
        #expect(try harness.text(cursor).contains(#""github""#))
        #expect(try !harness.text(cursor).contains(#""GitHub""#))

        // Delete: gone from both files.
        servers = harness.discover().servers
        _ = try harness.authoring.delete(try #require(servers.first { $0.id == "github" }))
        #expect(try !harness.text(cursor).lowercased().contains("github\":"))
        #expect(try !harness.text(claudeDesktop).lowercased().contains("github\":"))
    }

    private func inlineEnvSurvivesANoOpSave() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = """
        [mcp_servers.gh]
        command = "npx"
        env = {K='v',  L = "w"}  # keep
        """
        try harness.home.write(original, to: codex)
        let gh = try harness.server("gh")
        _ = try harness.authoring.update(
            ServerDraft(editing: gh).mergingSecrets(from: gh),
            originalName: "gh", server: gh, existing: harness.discover().servers
        )
        #expect(try harness.text(codex) == original, "same values in another spelling are not a change")
    }

    @Test("two Codex tables answering to one name are refused, not guessed")
    func tomlDuplicateIdentityRefused() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = """
        [mcp_servers.GitHub]
        command = "a"

        [mcp_servers.github]
        command = "b"
        """
        try harness.home.write(original, to: codex)
        let row = try #require(harness.discover().servers.first { $0.id == "github" })
        var draft = ServerDraft(editing: row).mergingSecrets(from: row)
        draft.args = ["z"]
        #expect(throws: (any Error).self) {
            try harness.authoring.update(draft, originalName: row.name, server: row, existing: harness.discover().servers)
        }
        #expect(try harness.text(codex) == original)
    }

    // MARK: 8 — a failed switch-off

    @Test("a switch-off whose write fails leaves no park behind")
    func failedSwitchOffRestoresPark() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"a": {"command": "x"}, "b": {"command": "y"}}}"#, to: cursor)
        _ = harness.discover()
        try FileManager.default.createDirectory(at: harness.paths.backups, withIntermediateDirectories: true)
        chmod(harness.paths.backups.path, 0o500)
        defer { chmod(harness.paths.backups.path, 0o700) }

        #expect(throws: (any Error).self) {
            _ = try harness.toggles.setEnabled(false, server: harness.server("b"), in: .cursor)
        }
        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "b") == nil)
        #expect(try harness.server("b").enabledIn[.cursor] == .enabled)
    }

    // MARK: 10 — restore guard, BOM, line endings

    @Test("a restore over a file changed since Kytto read it is refused")
    func restoreChecksTheLedger() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {"a": {"command": "x"}, "b": {"command": "y"}}}"#, to: cursor)
        _ = try harness.toggles.setEnabled(false, server: harness.server("b"), in: .cursor)
        let backup = try #require(harness.backups.list(clientID: .cursor).first)
        try harness.home.write(#"{"mcpServers": {"hand": {"command": "edit"}}}"#, to: cursor)

        #expect(throws: ConfigWriteError.self) {
            try harness.backups.restore(backup, home: harness.home.root, ledger: harness.ledger)
        }
        #expect(try harness.text(cursor).contains("hand"))

        _ = harness.discover()
        try harness.backups.restore(backup, home: harness.home.root, ledger: harness.ledger)
        #expect(try harness.text(cursor).contains(#""b""#))
        #expect(!harness.ledger.hasDrifted(harness.home.root.appending(path: cursor)))
    }

    @Test("a byte-order mark survives an edit, a switch-off and a restore")
    func byteOrderMarkSurvives() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let url = harness.home.root.appending(path: cursor)
        try harness.home.makeDirectory(".cursor")
        let bom: [UInt8] = [0xEF, 0xBB, 0xBF]
        try Data(bom + Array(#"{"mcpServers": {"a": {"command": "x"}, "b": {"command": "y"}}}"#.utf8)).write(to: url)

        _ = try harness.toggles.setEnabled(false, server: harness.server("a"), in: .cursor)
        #expect(Array(try Data(contentsOf: url).prefix(3)) == bom)
        _ = try harness.toggles.setEnabled(true, server: harness.server("a"), in: .cursor)
        #expect(Array(try Data(contentsOf: url).prefix(3)) == bom)

        let backup = try #require(harness.backups.list(clientID: .cursor).last)
        _ = harness.discover()
        try harness.backups.restore(backup, home: harness.home.root, ledger: harness.ledger)
        #expect(Array(try Data(contentsOf: url).prefix(3)) == bom)
    }

    @Test("an edit to a CRLF file inserts CRLF, and a mixed file is left as it is")
    func lineEndingsMatchTheFile() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let crlf = "{\r\n  \"mcpServers\": {\r\n    \"a\": {\r\n      \"command\": \"x\"\r\n    }\r\n  }\r\n}\r\n"
        try harness.home.write(crlf, to: cursor)
        _ = try harness.authoring.create(
            ServerDraft(name: "b", command: "y", env: [EnvEntry(key: "K", value: "v")]),
            in: [.cursor], existing: harness.discover().servers
        )
        let bytes = Array(try Data(contentsOf: harness.home.root.appending(path: cursor)))
        for (index, byte) in bytes.enumerated() where byte == 0x0A {
            #expect(index > 0 && bytes[index - 1] == 0x0D)
        }

        #expect(ConfigWriter.matchingLineEndings(of: "a\r\nb\nc", in: "a\r\nb\nc\nd") == "a\r\nb\nc\nd")
        #expect(ConfigWriter.matchingLineEndings(of: "a\r\nb\r\n", in: "a\r\nx\nb\r\n") == "a\r\nx\r\nb\r\n")
    }

    // MARK: 11 — duplicate keys

    @Test("a server spelled twice in one map is refused, not guessed at")
    func duplicateKeysRefused() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let original = #"{"mcpServers": {"a": {"command": "old"}, "a": {"command": "new"}}}"#
        try harness.home.write(original, to: cursor)
        #expect(throws: JSONEditError.self) {
            _ = try harness.toggles.setEnabled(false, server: harness.server("a"), in: .cursor)
        }
        #expect(try harness.text(cursor) == original)
        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "a") == nil)

        let document = try JSONDocument.parse(original)
        #expect(throws: JSONEditError.duplicateKey(["mcpServers", "a"])) {
            _ = try document.settingMember("a", at: ["mcpServers"], to: "{}")
        }
        #expect(JSONEditError.duplicateKey(["mcpServers", "a"]).errorDescription?.contains("more than once") == true)
    }

    // MARK: C — restricting permissions

    @Test("restricting a file that does not exist is an error, not a success")
    func restrictPermissionsWithoutFileThrows() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        #expect(throws: SecretsError.self) {
            try harness.secrets().restrictPermissions(clientID: .cursor)
        }

        try harness.home.write(#"{"mcpServers": {}}"#, to: cursor)
        let url = harness.home.root.appending(path: cursor)
        chmod(url.path, 0o644)
        #expect(try harness.secrets().restrictPermissions(clientID: .cursor))
        let mode = (try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber)?.intValue
        #expect(mode == 0o600)
    }
}

// MARK: - 9, A — the watcher

@Suite("ConfigWatcher", .serialized)
struct ConfigWatcherTests {
    private final class Counter: @unchecked Sendable {
        private let lock = NSLock()
        private var value = 0
        func increment() { lock.withLock { value += 1 } }
        var count: Int { lock.withLock { value } }
    }

    private func waitFor(_ counter: Counter, atLeast target: Int, seconds: Double = 3) async -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if counter.count >= target { return true }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return counter.count >= target
    }

    @Test("an edit in place, which changes no directory entry, is noticed")
    func inPlaceEdit() async throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(#"{"mcpServers": {}}"#, to: ".cursor/mcp.json")
        let url = home.root.appending(path: ".cursor/mcp.json")

        let counter = Counter()
        let watcher = ConfigWatcher(debounce: .milliseconds(50)) { counter.increment() }
        watcher.watch(directories: [url.deletingLastPathComponent()])
        defer { watcher.stop() }

        // Truncate and rewrite through the same inode, as some editors do.
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: 0)
        try handle.write(contentsOf: Data(#"{"mcpServers": {"a": {"command": "x"}}}"#.utf8))
        try handle.close()
        #expect(await waitFor(counter, atLeast: 1))

        // And again after an atomic replace moved the file to a new inode.
        try home.write(#"{"mcpServers": {"b": {}}}"#, to: ".cursor/mcp.json")
        #expect(await waitFor(counter, atLeast: 2))
        let afterReplace = counter.count
        try? await Task.sleep(for: .milliseconds(200))
        let handle2 = try FileHandle(forWritingTo: url)
        try handle2.seekToEnd()
        try handle2.write(contentsOf: Data(" ".utf8))
        try handle2.close()
        #expect(await waitFor(counter, atLeast: afterReplace + 1))
    }

    @Test("a watched directory deleted and created again is watched again")
    func recreatedDirectoryIsRewatched() async throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("{}", to: ".codex/seed.json")
        let directory = home.root.appending(path: ".codex")

        let counter = Counter()
        let watcher = ConfigWatcher(debounce: .milliseconds(50)) { counter.increment() }
        watcher.watch(directories: [directory])
        defer { watcher.stop() }

        try FileManager.default.removeItem(at: directory)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? await Task.sleep(for: .milliseconds(200))
        watcher.watch(directories: [directory])   // as a reload does
        let before = counter.count
        try home.write("model = \"x\"\n", to: ".codex/config.toml")
        #expect(await waitFor(counter, atLeast: before + 1))
    }

    @Test("re-arming does not swallow an event still waiting out its debounce")
    func rearmKeepsPendingEvent() async throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("{}", to: ".cursor/mcp.json")
        let directory = home.root.appending(path: ".cursor")

        let counter = Counter()
        let watcher = ConfigWatcher(debounce: .milliseconds(300)) { counter.increment() }
        watcher.watch(directories: [directory])
        defer { watcher.stop() }

        try home.write(#"{"a": 1}"#, to: ".cursor/mcp.json")
        try? await Task.sleep(for: .milliseconds(60))
        // What the app does after every reload.
        watcher.watch(directories: [directory])
        watcher.watch(directories: [directory])
        #expect(await waitFor(counter, atLeast: 1))
    }
}
