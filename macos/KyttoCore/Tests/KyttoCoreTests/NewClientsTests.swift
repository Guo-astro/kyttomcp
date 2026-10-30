import Foundation
import Testing
@testable import KyttoCore

/// Antigravity, Devin Desktop and OpenCode through the real pipeline.
///
/// The three share Codex's kind of off switch — a flag on the entry — but in
/// JSON, and two of them spell a definition differently from the first five
/// clients. What these protect is that a copy between clients means the same
/// thing on both sides: an endpoint under the key the target reads, no `type`
/// where it is rejected, and no on/off state carried from the source client.
private let antigravityPath = ".gemini/config/mcp_config.json"
private let devinPath = ".config/devin/mcp_config.json"
private let openCodePath = ".config/opencode/opencode.json"
private let cursorPath = ".cursor/mcp.json"

private let antigravityConfig = """
{
  "mcpServers": {
    "sqlite": {
      "command": "node",
      "args": ["/opt/sqlite-mcp.js"],
      "env": { "DB_PATH": "/var/data/app.db" },
      "disabledTools": ["drop_table"]
    },
    "docs": {
      "serverUrl": "https://docs.example.com/mcp",
      "headers": { "Authorization": "Bearer abc" },
      "timeoutSeconds": 600
    },
    "paused": {
      "command": "uvx",
      "args": ["mcp-server-time"],
      "disabled": true
    }
  }
}
"""

private let openCodeConfig = """
{
  "$schema": "https://opencode.ai/config.json",
  // Personal settings.
  "theme": "opencode",
  "mcp": {
    "github": {
      "type": "local",
      "command": ["npx", "-y", "@modelcontextprotocol/server-github"],
      "environment": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_examplevalue1234567890" },
      "timeout": 20000,
    },
    "context": {
      "type": "remote",
      "url": "https://mcp.example.com/mcp",
      "enabled": false
    }
  },
  "tools": { "github_delete_*": false }
}
"""

private let cursorConfig = """
{
  "mcpServers": {
    "fetch": {
      "command": "uvx",
      "args": ["mcp-server-fetch"],
      "env": { "FETCH_LIMIT": "10" }
    },
    "remote": {
      "type": "http",
      "url": "https://remote.example.com/mcp",
      "headers": { "X-Team": "kytto" }
    },
    "legacy": {
      "url": "https://old.example.com/sse"
    }
  }
}
"""

@Suite("New clients — Antigravity")
struct AntigravityTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(antigravityConfig, to: antigravityPath)
        try harness.home.write(cursorConfig, to: cursorPath)
        return harness
    }

    @Test("servers are found, `serverUrl` is the endpoint and `disabled` decides what is on")
    func discovery() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let result = harness.discover()
        let client = try #require(result.clients.first { $0.id == .antigravity })
        #expect(client.configExists)
        #expect(client.serverCount == 3)

        let docs = try harness.server("docs")
        #expect(docs.transport == .http)
        #expect(docs.url == "https://docs.example.com/mcp")
        #expect(docs.enabledIn[.antigravity] == .enabled)
        #expect(try harness.server("paused").enabledIn[.antigravity] == .disabled)
        #expect(try harness.server("sqlite").env.first?.key == "DB_PATH")
    }

    @Test("switching off writes disabled: true and keeps everything else")
    func disableAndEnable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let result = try harness.toggles.setEnabled(false, server: try harness.server("sqlite"), in: .antigravity)
        #expect(result.wasParked == false)
        #expect(harness.parkStore.all().isEmpty)
        let off = try JSONDocument.parse(harness.text(antigravityPath))
        #expect(off.value(at: ["mcpServers", "sqlite", "disabled"])?.boolValue == true)
        #expect(off.value(at: ["mcpServers", "sqlite", "disabledTools"])?.elements?.count == 1)
        #expect(try harness.server("sqlite").enabledIn[.antigravity] == .disabled)

        // Back on, the flag goes away, which is how the entry looked before.
        _ = try harness.toggles.setEnabled(true, server: try harness.server("sqlite"), in: .antigravity)
        #expect(try harness.text(antigravityPath) == antigravityConfig)

        // And the server that was already off comes on the same way.
        _ = try harness.toggles.setEnabled(true, server: try harness.server("paused"), in: .antigravity)
        #expect(try JSONDocument.parse(harness.text(antigravityPath)).value(at: ["mcpServers", "paused", "disabled"]) == nil)
    }

    @Test("a switch to the state a server is already in writes nothing")
    func noOp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let result = try harness.toggles.setEnabled(false, server: try harness.server("paused"), in: .antigravity)
        #expect(result.backupID == nil)
        #expect(try harness.text(antigravityPath) == antigravityConfig)
    }

    @Test("a Cursor server arrives with serverUrl, no type, and its headers")
    func copyFromCursor() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(true, server: try harness.server("remote"), in: .antigravity)
        let entry = try #require(JSONDocument.parse(harness.text(antigravityPath)).value(at: ["mcpServers", "remote"]))
        #expect(entry["serverUrl"]?.stringValue == "https://remote.example.com/mcp")
        #expect(entry["url"] == nil)
        #expect(entry["type"] == nil)
        #expect(entry["headers"]?["X-Team"]?.stringValue == "kytto")
        #expect(try harness.server("remote").enabledIn[.antigravity] == .enabled)
    }

    @Test("an Antigravity server arrives in Cursor without Antigravity's own state")
    func copyToCursor() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(true, server: try harness.server("docs"), in: .cursor)
        _ = try harness.toggles.setEnabled(true, server: try harness.server("paused"), in: .cursor)
        let document = try JSONDocument.parse(harness.text(cursorPath))
        let docs = try #require(document.value(at: ["mcpServers", "docs"]))
        #expect(docs["url"]?.stringValue == "https://docs.example.com/mcp")
        #expect(docs["serverUrl"] == nil)
        #expect(docs["type"]?.stringValue == "http")
        #expect(docs["headers"] != nil)
        #expect(document.value(at: ["mcpServers", "paused", "disabled"]) == nil)
        #expect(try harness.server("paused").enabledIn[.cursor] == .enabled)
    }

    @Test("the legacy SSE transport is refused rather than written")
    func sseRefused() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let legacy = try harness.server("legacy")
        #expect(legacy.transport == .sse)
        #expect(throws: ToggleError.definitionNotPortable("legacy", fields: ["the legacy HTTP+SSE transport"])) {
            try harness.toggles.setEnabled(true, server: legacy, in: .antigravity)
        }
        let draft = ServerDraft(name: "old", transport: .sse, url: "https://old.example.com/sse")
        #expect(throws: AuthoringError.sseNotAccepted(client: "Antigravity")) {
            try harness.authoring.create(draft, in: [.antigravity], existing: harness.discover().servers)
        }
        #expect(try harness.text(antigravityPath) == antigravityConfig)
    }

    @Test("adding and editing a remote server keeps the endpoint key the entry uses")
    func authoring() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let draft = ServerDraft(name: "search", transport: .http, url: "https://search.example.com/mcp")
        _ = try harness.authoring.create(draft, in: [.antigravity], existing: harness.discover().servers)
        let added = try #require(JSONDocument.parse(harness.text(antigravityPath)).value(at: ["mcpServers", "search"]))
        #expect(added["serverUrl"]?.stringValue == "https://search.example.com/mcp")
        #expect(added["type"] == nil)

        // An entry written by hand with `url` stays on `url`.
        let text = try harness.text(antigravityPath)
        try harness.home.write(text.replacingOccurrences(of: #""serverUrl": "https://docs"#, with: #""url": "https://docs"#), to: antigravityPath)
        let docs = try harness.server("docs")
        var edit = ServerDraft(editing: docs)
        edit.url = "https://docs.example.com/v2/mcp"
        _ = try harness.authoring.update(edit, originalName: "docs", server: docs, existing: harness.discover().servers)
        let edited = try #require(JSONDocument.parse(harness.text(antigravityPath)).value(at: ["mcpServers", "docs"]))
        #expect(edited["url"]?.stringValue == "https://docs.example.com/v2/mcp")
        #expect(edited["serverUrl"] == nil)
        #expect(edited["timeoutSeconds"] != nil)
    }
}

@Suite("New clients — Devin Desktop")
struct DevinDesktopTests {

    @Test("only the legacy transport is stated, as `transport`")
    func transports() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(cursorConfig, to: cursorPath)
        try harness.home.write(#"{ "mcpServers": { "events": { "url": "https://events.example.com/mcp", "transport": "sse" } } }"#, to: devinPath)

        #expect(try harness.server("events").transport == .sse)

        _ = try harness.toggles.setEnabled(true, server: try harness.server("remote"), in: .devinDesktop)
        _ = try harness.toggles.setEnabled(true, server: try harness.server("legacy"), in: .devinDesktop)
        let document = try JSONDocument.parse(harness.text(devinPath))
        let remote = try #require(document.value(at: ["mcpServers", "remote"]))
        #expect(remote["url"]?.stringValue == "https://remote.example.com/mcp")
        #expect(remote["type"] == nil)
        #expect(remote["transport"] == nil)
        #expect(document.value(at: ["mcpServers", "legacy", "transport"])?.stringValue == "sse")

        _ = try harness.toggles.setEnabled(false, server: try harness.server("events"), in: .devinDesktop)
        #expect(try harness.server("events").enabledIn[.devinDesktop] == .disabled)
        #expect(try JSONDocument.parse(harness.text(devinPath)).value(at: ["mcpServers", "events", "disabled"])?.boolValue == true)
    }

    @Test("the first server creates the file")
    func firstServer() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(cursorConfig, to: cursorPath)

        #expect(harness.discover().clients.first { $0.id == .devinDesktop }?.configExists == false)
        _ = try harness.toggles.setEnabled(true, server: try harness.server("fetch"), in: .devinDesktop)
        let entry = try #require(JSONDocument.parse(harness.text(devinPath)).value(at: ["mcpServers", "fetch"]))
        #expect(entry["command"]?.stringValue == "uvx")
        #expect(entry["env"]?["FETCH_LIMIT"]?.stringValue == "10")
    }
}

@Suite("New clients — OpenCode")
struct OpenCodeTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(openCodeConfig, to: openCodePath)
        try harness.home.write(cursorConfig, to: cursorPath)
        return harness
    }

    @Test("the argv array, `environment` and `enabled` are read")
    func discovery() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let client = try #require(harness.discover().clients.first { $0.id == .openCode })
        #expect(client.serverCount == 2)

        let github = try harness.server("github")
        #expect(github.transport == .stdio)
        #expect(github.command == "npx")
        #expect(github.args == ["-y", "@modelcontextprotocol/server-github"])
        #expect(github.env.first?.key == "GITHUB_PERSONAL_ACCESS_TOKEN")
        #expect(github.enabledIn[.openCode] == .enabled)

        let context = try harness.server("context")
        #expect(context.transport == .http)
        #expect(context.url == "https://mcp.example.com/mcp")
        #expect(context.enabledIn[.openCode] == .disabled)
    }

    @Test("switching writes `enabled`, and the rest of the JSONC file survives")
    func toggle() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(false, server: try harness.server("github"), in: .openCode)
        let text = try harness.text(openCodePath)
        #expect(text.contains("// Personal settings."))
        #expect(text.contains(#""tools": { "github_delete_*": false }"#))
        #expect(try JSONDocument.parse(text).value(at: ["mcp", "github", "enabled"])?.boolValue == false)
        #expect(try harness.server("github").enabledIn[.openCode] == .disabled)

        _ = try harness.toggles.setEnabled(true, server: try harness.server("context"), in: .openCode)
        #expect(try JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "context", "enabled"])?.boolValue == true)
        #expect(try harness.server("context").enabledIn[.openCode] == .enabled)
    }

    @Test("a Cursor server is rendered in OpenCode's own shape")
    func copyFromCursor() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(true, server: try harness.server("fetch"), in: .openCode)
        let entry = try #require(JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "fetch"]))
        #expect(entry["type"]?.stringValue == "local")
        #expect(entry["command"]?.elements?.compactMap(\.stringValue) == ["uvx", "mcp-server-fetch"])
        #expect(entry["environment"]?["FETCH_LIMIT"]?.stringValue == "10")
        #expect(entry["args"] == nil)
        #expect(entry["env"] == nil)
        #expect(try harness.server("fetch").enabledIn[.openCode] == .enabled)
    }

    @Test("a definition with headers is refused rather than copied without them")
    func headersNotPortable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        #expect(throws: ToggleError.definitionNotPortable("remote", fields: ["headers"])) {
            try harness.toggles.setEnabled(true, server: try harness.server("remote"), in: .openCode)
        }
        #expect(try harness.text(openCodePath) == openCodeConfig)
    }

    @Test("an OpenCode server arrives in Cursor as command and args")
    func copyToCursor() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(true, server: try harness.server("github"), in: .cursor)
        let entry = try #require(JSONDocument.parse(harness.text(cursorPath)).value(at: ["mcpServers", "github"]))
        #expect(entry["command"]?.stringValue == "npx")
        #expect(entry["args"]?.elements?.count == 2)
        #expect(entry["env"]?["GITHUB_PERSONAL_ACCESS_TOKEN"] != nil)
        #expect(entry["type"] == nil)
        #expect(entry["timeout"] == nil)
    }

    @Test("adding, editing and repairing an argument keep OpenCode's keys")
    func authoring() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let draft = ServerDraft(name: "time", command: "uvx", args: ["mcp-server-time"], env: [EnvEntry(key: "TZ", value: "UTC")])
        _ = try harness.authoring.create(draft, in: [.openCode], existing: harness.discover().servers)
        let added = try #require(JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "time"]))
        #expect(added["type"]?.stringValue == "local")
        #expect(added["command"]?.elements?.compactMap(\.stringValue) == ["uvx", "mcp-server-time"])
        #expect(added["environment"]?["TZ"]?.stringValue == "UTC")

        // An unchanged save writes nothing.
        let before = try harness.text(openCodePath)
        let github = try harness.server("github")
        let unchanged = try harness.authoring.update(ServerDraft(editing: github), originalName: "github", server: github, existing: harness.discover().servers)
        #expect(unchanged.changed.isEmpty)
        #expect(try harness.text(openCodePath) == before)

        var edit = ServerDraft(editing: github)
        edit.args = ["-y", "@modelcontextprotocol/server-github@1.2.3"]
        _ = try harness.authoring.update(edit, originalName: "github", server: github, existing: harness.discover().servers)
        let edited = try #require(JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "github"]))
        #expect(edited["command"]?.elements?.compactMap(\.stringValue) == ["npx", "-y", "@modelcontextprotocol/server-github@1.2.3"])
        #expect(edited["timeout"] != nil)
        #expect(edited["environment"]?["GITHUB_PERSONAL_ACCESS_TOKEN"]?.stringValue == "ghp_examplevalue1234567890")

        let pinned = try harness.server("github")
        _ = try harness.authoring.replaceArgument(of: pinned, edits: [
            .openCode: .init(index: 1, expected: "@modelcontextprotocol/server-github@1.2.3", replacement: "@modelcontextprotocol/server-github@1.2.4"),
        ])
        #expect(try harness.server("github").args == ["-y", "@modelcontextprotocol/server-github@1.2.4"])
    }

    @Test("a remote entry never gets `environment`, and a transport change leaves no keys of the other kind")
    func transportKeys() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let remote = ServerDraft(name: "hosted", transport: .http, env: [EnvEntry(key: "TOKEN", value: "x")], url: "https://hosted.example.com/mcp")
        #expect(try JSONDocument.parse(remote.definition(baseIndent: "", unit: "  ", schema: .openCode)).root["environment"] == nil)

        let github = try harness.server("github")
        var toRemote = ServerDraft(editing: github)
        toRemote.transport = .http
        toRemote.url = "https://github.example.com/mcp"
        _ = try harness.authoring.update(toRemote, originalName: "github", server: github, existing: harness.discover().servers)
        let remoteEntry = try #require(JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "github"]))
        #expect(remoteEntry["type"]?.stringValue == "remote")
        #expect(remoteEntry["command"] == nil)
        #expect(remoteEntry["environment"] == nil)
        #expect(remoteEntry["timeout"] != nil)

        let context = try harness.server("context")
        var toLocal = ServerDraft(editing: context)
        toLocal.transport = .stdio
        toLocal.command = "npx"
        toLocal.args = ["-y", "context-mcp"]
        _ = try harness.authoring.update(toLocal, originalName: "context", server: context, existing: harness.discover().servers)
        let localEntry = try #require(JSONDocument.parse(harness.text(openCodePath)).value(at: ["mcp", "context"]))
        #expect(localEntry["type"]?.stringValue == "local")
        #expect(localEntry["url"] == nil)
        #expect(localEntry["command"]?.elements?.count == 3)
        #expect(localEntry["enabled"]?.boolValue == false)
    }

    @Test("rotating a secret changes the value under `environment`")
    func rotateSecret() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let servers = harness.discover().servers
        let secrets = harness.secrets()
        let record = try #require(secrets.scan(servers: servers).first { $0.key == "GITHUB_PERSONAL_ACCESS_TOKEN" })
        _ = try secrets.rotate(secretID: record.id, to: "ghp_rotatedvalue0987654321", servers: servers, alsoStoreInSecretStore: false)
        #expect(try harness.server("github").env.first?.value == "ghp_rotatedvalue0987654321")
        #expect(try harness.text(openCodePath).contains("// Personal settings."))
    }

    @Test("a remote draft renders as `type: remote`")
    func schemaRendering() {
        let draft = ServerDraft(name: "x", transport: .http, url: "https://x.example.com/mcp")
        let text = draft.definition(baseIndent: "", unit: "  ", schema: .openCode)
        let node = try? JSONDocument.parse(text).root
        #expect(node?["type"]?.stringValue == "remote")
        #expect(node?["url"]?.stringValue == "https://x.example.com/mcp")
    }
}

@Suite("New clients — remote checks")
struct NewClientRemoteCheckTests {

    @Test("OpenCode's `{env:VAR}` in a header is resolved for a remote check")
    func openCodePlaceholder() throws {
        let headers = try RemoteHTTPHeaders.resolve(
            definitionSource: #"{"type":"remote","url":"https://x.example.com/mcp","headers":{"Authorization":"Bearer {env:API_KEY}","X-Plain":"literal"}}"#,
            environment: ["API_KEY": "secret-value"]
        )
        #expect(headers["Authorization"] == "Bearer secret-value")
        #expect(headers["X-Plain"] == "literal")
    }
}
