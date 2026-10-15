import Foundation
import Testing
@testable import KyttoCore

private func pluginJSON(_ object: Any) throws -> String {
    String(decoding: try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
}

private func registerClaude(_ home: FakeHome, id: String = "demo@example", entries: [[String: Any]]) throws {
    try home.write(try pluginJSON(["version": 2, "plugins": [id: entries]]), to: ".claude/plugins/installed_plugins.json")
}

@Suite("Registered plugin discovery")
struct PluginDiscoveryTests {
    @Test("Claude reads the registered active version and ignores fixtures, stale and disabled caches")
    func activeClaudeRegistration() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let root = home.root.appending(path: ".claude/plugins/cache/example/demo/2")
        try registerClaude(home, entries: [["scope": "user", "installPath": root.path, "version": "2"]])
        try home.write(#"{"enabledPlugins":{"demo@example":true}}"#, to: ".claude/settings.json")
        try home.write(#"{"name":"demo","displayName":"Demo tools","version":"2"}"#, to: ".claude/plugins/cache/example/demo/2/.claude-plugin/plugin.json")
        let config = #"{"mcpServers":{"inspector":{"command":"${CLAUDE_PLUGIN_ROOT}/bin/inspect","args":["${CLAUDE_PLUGIN_ROOT}/config with spaces.json"],"env":{"ROOT":"${CLAUDE_PLUGIN_ROOT}"},"cwd":"."}}}"#
        try home.write(config, to: ".claude/plugins/cache/example/demo/2/.mcp.json")
        for path in [".claude/plugins/cache/example/demo/1/.mcp.json", ".claude/plugins/cache/example/demo/2/tests/fixtures/.mcp.json", ".claude/plugins/cache/example/uninstalled/1/.mcp.json"] {
            try home.write(#"{"mcpServers":{"wrong":{"command":"never-run"}}}"#, to: path)
        }
        let result = Discovery(home: home.root, locator: StubAppLocator()).run()
        #expect(result.servers.map(\.name) == ["plugin:demo:inspector"])
        let server = try #require(result.servers.first)
        #expect(server.isBundled)
        #expect(server.origin == .plugin(clientID: .claudeCode, pluginID: "demo@example", displayName: "Demo tools", version: "2"))
        #expect(server.origin.provenance.kind == "plugin")
        #expect(server.origin.provenance.readOnly)
        #expect(server.runnable.command == root.appending(path: "bin/inspect").path)
        #expect(server.runnable.args == [root.appending(path: "config with spaces.json").path])
        #expect(server.runnableEnvironment.first(where: { $0.key == "ROOT" })?.value == root.path)
        #expect(server.workingDirectory == root.path)
        #expect(server.definitionsByClient[.claudeCode]?.workingDirectory == root.path)
        #expect(server.definitionsByClient[.claudeCode]?.placeholders == server.placeholders)
        #expect(server.commandSummary.contains("${CLAUDE_PLUGIN_ROOT}"))
        #expect(try String(contentsOf: root.appending(path: ".mcp.json"), encoding: .utf8) == config)
        #expect(!FileManager.default.fileExists(atPath: home.root.appending(path: ".claude/plugins/data").path))
        let resolver = ClientPathResolver(home: home.root)
        #expect(resolver.writeTarget(matching: root.appending(path: ".mcp.json"), for: ClientRegistry.descriptor(for: .claudeCode)) == nil)
    }

    @Test("disabled plugins and ambiguous active registrations are never shown as active")
    func inactiveClaudeRegistration() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let root = home.root.appending(path: ".claude/plugins/cache/example/demo/1")
        let entry: [String: Any] = ["scope": "user", "installPath": root.path, "version": "1"]
        try registerClaude(home, entries: [entry])
        try home.write(#"{"enabledPlugins":{"demo@example":false}}"#, to: ".claude/settings.json")
        try home.write(#"{"mcpServers":{"ignored":{"command":"never-run"}}}"#, to: ".claude/plugins/cache/example/demo/1/.mcp.json")
        #expect(Discovery(home: home.root, locator: StubAppLocator()).run().servers.isEmpty)
        try home.write(#"{"enabledPlugins":{"demo@example":true}}"#, to: ".claude/settings.json")
        try registerClaude(home, entries: [entry, entry])
        let result = Discovery(home: home.root, locator: StubAppLocator()).run()
        #expect(result.servers.isEmpty)
        #expect(result.diagnostics.contains { $0.message.contains("ambiguous") })
    }

    @Test("Claude merges declared MCP components in order without reading undeclared files")
    func declaredComponents() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let relativeRoot = ".claude/plugins/cache/example/demo/1"
        let root = home.root.appending(path: relativeRoot)
        try registerClaude(home, entries: [["scope": "user", "installPath": root.path, "version": "1"]])
        try home.write(#"{"name":"demo","mcpServers":["./config/extra.json",{"tool":{"command":"inline-last"}},"./../outside.json"]}"#, to: relativeRoot + "/.claude-plugin/plugin.json")
        try home.write(#"{"mcpServers":{"tool":{"command":"default-first"}}}"#, to: relativeRoot + "/.mcp.json")
        try home.write(#"{"mcpServers":{"tool":{"command":"declared-second"},"extra":{"command":"extra"}}}"#, to: relativeRoot + "/config/extra.json")
        try home.write(#"{"mcpServers":{"escape":{"command":"never"}}}"#, to: ".claude/plugins/cache/example/demo/outside.json")
        let result = Discovery(home: home.root, locator: StubAppLocator()).run()
        #expect(result.servers.count == 2)
        #expect(result.servers.first { $0.name == "plugin:demo:tool" }?.command == "inline-last")
        #expect(result.diagnostics.contains { $0.message.contains("external MCP") })
    }

    @Test("registered project plugins respect project and local settings precedence")
    func projectRegistration() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let project = home.root.appending(path: "projects/demo").path
        let root = home.root.appending(path: ".claude/plugins/cache/example/demo/1")
        try registerClaude(home, entries: [["scope": "project", "projectPath": project, "installPath": root.path, "version": "1"]])
        try home.write(#"{"enabledPlugins":{"demo@example":false}}"#, to: ".claude/settings.json")
        try home.write(#"{"enabledPlugins":{"demo@example":true}}"#, to: "projects/demo/.claude/settings.json")
        try home.write(#"{"mcpServers":{"tool":{"command":"node","args":["${CLAUDE_PROJECT_DIR}/server.js"]}}}"#, to: ".claude/plugins/cache/example/demo/1/.mcp.json")
        let result = Discovery(home: home.root, locator: StubAppLocator()).run()
        let client = try #require(result.clients.first { $0.scopeLabel == project })
        let server = try #require(result.servers.first)
        #expect(client.configurationScope == .workspace)
        #expect(client.isReadOnly)
        #expect(server.enabledIn[.claudeCode] == .absent)
        #expect(server.enabledIn[client.id] == .enabled)
        #expect(server.runnable.args == [project + "/server.js"])
        try home.write(#"{"enabledPlugins":{"demo@example":false}}"#, to: "projects/demo/.claude/settings.local.json")
        #expect(Discovery(home: home.root, locator: StubAppLocator()).run().servers.isEmpty)
    }

    @Test("Codex requires enabled registration and a single identified package version")
    func codexRegistration() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("[plugins.\"demo@example\"]\nenabled = true\n[plugins.\"disabled@example\"]\nenabled = false\n", to: ".codex/config.toml")
        let base = ".codex/plugins/cache/example/demo"
        try home.write(#"{"name":"demo","version":"2"}"#, to: base + "/2/.codex-plugin/plugin.json")
        try home.write(#"{"mcpServers":{"tool":{"command":"./bin/server","cwd":".","env":{"ROOT":"${CODEX_PLUGIN_ROOT}"}}}}"#, to: base + "/2/.mcp.json")
        try home.write(#"{"mcpServers":{"fixture":{"command":"never"}}}"#, to: base + "/2/tests/.mcp.json")
        try home.write(#"{"mcpServers":{"disabled":{"command":"never"}}}"#, to: ".codex/plugins/cache/example/disabled/1/.mcp.json")
        let result = Discovery(home: home.root, locator: StubAppLocator()).run()
        #expect(result.servers.map(\.name) == ["tool"])
        #expect(result.servers.first?.workingDirectory == home.root.appending(path: base + "/2").path)
        try home.write(#"{"name":"demo","version":"1"}"#, to: base + "/1/.codex-plugin/plugin.json")
        try home.write(#"{"mcpServers":{"stale":{"command":"never"}}}"#, to: base + "/1/.mcp.json")
        let ambiguous = Discovery(home: home.root, locator: StubAppLocator()).run()
        #expect(ambiguous.servers.isEmpty)
        #expect(ambiguous.diagnostics.contains { $0.message.contains("several cached versions") })
    }

    @Test("MCP component symlinks cannot escape the registered plugin root")
    func containedComponents() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.makeDirectory("plugin")
        try home.write("{}", to: "outside.json")
        let root = home.root.appending(path: "plugin")
        try FileManager.default.createSymbolicLink(at: root.appending(path: "escape.json"), withDestinationURL: home.root.appending(path: "outside.json"))
        #expect(PluginInstallations.containedFile("./escape.json", root: root) == nil)
        #expect(PluginInstallations.containedFile("./../outside.json", root: root) == nil)
    }

    @Test("a substituted value is never reinterpreted as another placeholder")
    func singlePassSubstitution() throws {
        let document = try JSONDocument.parse(#"{"command":"${ROOT}/server","env":{"VALUE":"${ROOT}"}}"#)
        var server = ServerFields(node: document.root).makeServer(name: "test", origin: .configFile(.cursor), isBundled: false, sourceText: "{}")
        server.placeholders = ["ROOT": "/temp/${OTHER}", "OTHER": "wrong"]
        #expect(server.runnable.command == "/temp/${OTHER}/server")
        #expect(server.runnableEnvironment.first?.value == "/temp/${OTHER}")
    }

    @Test("health checks use the plugin directory and expanded environment with literal argv")
    func pluginLaunchContext() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let root = home.root.appending(path: "plugin with spaces")
        let script = #"""
        import json, os, sys
        assert os.path.realpath(os.getcwd()) == os.path.realpath(os.environ['EXPECTED_ROOT'])
        assert sys.argv[1] == 'literal $(touch should-not-exist); value'
        for line in sys.stdin:
            request = json.loads(line)
            if request.get('method') == 'initialize':
                result = {'protocolVersion': '2026-07-28', 'capabilities': {}, 'serverInfo': {'name': 'context-ok', 'version': '1'}}
            elif request.get('method') == 'tools/list':
                result = {'tools': []}
            else:
                continue
            print(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}), flush=True)
        """#
        try home.write(script, to: "plugin with spaces/context.py")
        let document = try JSONDocument.parse(#"{"command":"python3","args":["./context.py","literal $(touch should-not-exist); value"],"env":{"EXPECTED_ROOT":"${CLAUDE_PLUGIN_ROOT}"}}"#)
        var server = ServerFields(node: document.root).makeServer(name: "test", origin: .plugin(clientID: .claudeCode, pluginID: "demo@example", displayName: "Demo", version: "1"), isBundled: true, sourceText: "{}")
        server.workingDirectory = root.path
        server.placeholders = ["CLAUDE_PLUGIN_ROOT": root.path]
        let shell = ShellEnvironment.resolve(shellPath: nil)
        let (health, _) = HealthChecker(shell: shell, timeout: 5).check(server)
        #expect(health.status == .passed, Comment(rawValue: health.currentMessage ?? health.stderr ?? "no message"))
        #expect(health.serverName == "context-ok")
        #expect(!FileManager.default.fileExists(atPath: root.appending(path: "should-not-exist").path))
    }

    @Test("relative executables require explicit native context and missing cwd never runs")
    func executableContext() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("#!/bin/sh\nexit 0\n", to: "plugin/bin/server")
        let root = home.root.appending(path: "plugin")
        let executable = root.appending(path: "bin/server")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        #expect(try ManagedExecutable.resolve("./bin/server", path: "", workingDirectory: root.path) == executable.path)
        #expect(throws: ProcessError.relativeCommand("./bin/server")) {
            try ManagedExecutable.resolve("./bin/server", path: "")
        }
        #expect(throws: ProcessError.invalidWorkingDirectory(root.appending(path: "missing").path)) {
            try MCPStdioClient().listTools(command: executable.path, arguments: [], environment: [:], timeout: 1, workingDirectory: root.appending(path: "missing").path)
        }
    }
}
