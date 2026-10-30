import Foundation
import Testing
@testable import KyttoCore

extension ToggleHarness {
    var toolSwitches: ToolSwitchService {
        ToolSwitchService(home: home.root, writer: ConfigWriter(backups: backups), ledger: ledger)
    }
}

private let githubTools = ["get_issue", "create_issue", "delete_branch", "list_repos"]

@Suite("Tool switches — rules")
struct ToolFilterTests {

    @Test("globs, exact rules and allow lists decide in that order")
    func evaluation() {
        let filter = ToolFilter(prefix: "github_", rules: [
            .init(pattern: "github_delete_*", keepsOn: false),
            .init(pattern: "github_delete_tag", keepsOn: true),
        ])
        #expect(filter.isOff("delete_branch"))
        #expect(!filter.isOff("delete_tag"))
        #expect(!filter.isOff("get_issue"))
        #expect(filter.blockingPattern(for: "delete_branch") == "github_delete_*")

        let allow = ToolFilter(rules: [.init(pattern: "b", keepsOn: false)], allowList: ["a", "b"])
        #expect(allow.offTools(among: ["a", "b", "c"]) == ["b", "c"])
    }

    @Test("names become identifiers the way the clients spell them")
    func sanitizing() {
        #expect(ToolFilter.sanitized("my.server name") == "my_server_name")
        #expect(ToolFilter.glob("mcp__git*__get_?", matches: "mcp__github__get_x"))
        #expect(!ToolFilter.glob("mcp__git*__get_?", matches: "mcp__github__get_xy"))
    }
}

@Suite("Tool switches — writing")
struct ToolSwitchTests {

    @Test("Codex: disabled_tools on the server's table, gone again when empty")
    func codex() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        let before = try harness.text(".codex/config.toml")

        let result = try harness.toolSwitches.setDisabledTools(["delete_branch"], among: githubTools, server: try harness.server("filesystem"), in: .codex)
        #expect(result.backupID != nil)
        let table = try #require(TOMLDocument.parse(harness.text(".codex/config.toml")).table(at: ["mcp_servers", "filesystem"]))
        #expect(table.value("disabled_tools")?.elements?.compactMap(\.stringValue) == ["delete_branch"])
        #expect(try harness.server("filesystem").toolFilters[.codex]?.isOff("delete_branch") == true)

        _ = try harness.toolSwitches.setDisabledTools([], among: githubTools, server: try harness.server("filesystem"), in: .codex)
        #expect(try harness.text(".codex/config.toml") == before)
    }

    @Test("Codex: an allow list is edited instead of growing a deny list")
    func codexAllowList() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.codexConfig.text.replacingOccurrences(of: "startup_timeout_sec = 20", with: "startup_timeout_sec = 20\nenabled_tools = [\"get_issue\", \"create_issue\"]"), to: ".codex/config.toml")

        #expect(try harness.server("filesystem").toolFilters[.codex]?.offTools(among: githubTools) == ["delete_branch", "list_repos"])
        _ = try harness.toolSwitches.setDisabledTools(["create_issue", "delete_branch"], among: githubTools, server: try harness.server("filesystem"), in: .codex)
        let table = try #require(TOMLDocument.parse(harness.text(".codex/config.toml")).table(at: ["mcp_servers", "filesystem"]))
        #expect(table.value("enabled_tools")?.elements?.compactMap(\.stringValue) == ["get_issue", "list_repos"])
        #expect(table.value("disabled_tools") == nil)
    }

    @Test("Antigravity: disabledTools on the entry, everything else kept")
    func antigravity() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let config = """
        {
          "mcpServers": {
            "github": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-github"],
              "disabledTools": ["delete_branch"]
            }
          }
        }
        """
        try harness.home.write(config, to: ".gemini/config/mcp_config.json")

        _ = try harness.toolSwitches.setDisabledTools(["delete_branch", "create_issue"], among: githubTools, server: try harness.server("github"), in: .antigravity)
        let entry = try #require(JSONDocument.parse(harness.text(".gemini/config/mcp_config.json")).value(at: ["mcpServers", "github"]))
        #expect(entry["disabledTools"]?.elements?.compactMap(\.stringValue) == ["delete_branch", "create_issue"])

        // A no-op save writes nothing.
        let unchanged = try harness.toolSwitches.setDisabledTools(["delete_branch", "create_issue"], among: githubTools, server: try harness.server("github"), in: .antigravity)
        #expect(unchanged.backupID == nil)
        #expect(!unchanged.requiresRestart)

        _ = try harness.toolSwitches.setDisabledTools([], among: githubTools, server: try harness.server("github"), in: .antigravity)
        #expect(try JSONDocument.parse(harness.text(".gemini/config/mcp_config.json")).value(at: ["mcpServers", "github", "disabledTools"]) == nil)
    }

    @Test("OpenCode: `<server>_<tool>: false` in the tools map, and a glob is not overridden")
    func openCode() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let config = """
        {
          // keep
          "mcp": {
            "github": { "type": "local", "command": ["npx", "-y", "@modelcontextprotocol/server-github"] }
          },
          "tools": { "github_delete_*": false, "bash": true }
        }
        """
        try harness.home.write(config, to: ".config/opencode/opencode.json")

        let server = try harness.server("github")
        #expect(server.toolFilters[.openCode]?.offTools(among: githubTools) == ["delete_branch"])

        _ = try harness.toolSwitches.setDisabledTools(["delete_branch", "create_issue"], among: githubTools, server: server, in: .openCode)
        let text = try harness.text(".config/opencode/opencode.json")
        #expect(text.contains("// keep"))
        let tools = try #require(JSONDocument.parse(text).root["tools"])
        #expect(tools["github_create_issue"]?.boolValue == false)
        #expect(tools["bash"]?.boolValue == true)
        #expect(tools["github_delete_branch"] == nil)

        #expect(throws: ToolSwitchError.blockedByRule(tool: "delete_branch", rule: "github_delete_*")) {
            try harness.toolSwitches.setDisabledTools([], among: githubTools, server: try harness.server("github"), in: .openCode)
        }
        #expect(try harness.text(".config/opencode/opencode.json") == text)
    }

    @Test("Claude Code: one mcp__server__tool deny rule per tool in settings.json")
    func claudeCode() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")
        let configBefore = try harness.text(".claude.json")

        let server = try harness.server("XcodeBuildMCP")
        _ = try harness.toolSwitches.setDisabledTools(["build_sim"], among: ["build_sim", "list_sims"], server: server, in: .claudeCode)
        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        #expect(settings.value(at: ["permissions", "deny"])?.elements?.compactMap(\.stringValue) == ["mcp__XcodeBuildMCP__build_sim"])
        #expect(settings.root["deniedMcpServers"]?.elements?.count == 2)
        #expect(try harness.text(".claude.json") == configBefore)
        #expect(try harness.server("XcodeBuildMCP").toolFilters[.claudeCode]?.isOff("build_sim") == true)

        _ = try harness.toolSwitches.setDisabledTools([], among: ["build_sim", "list_sims"], server: try harness.server("XcodeBuildMCP"), in: .claudeCode)
        #expect(try JSONDocument.parse(harness.text(".claude/settings.json")).value(at: ["permissions", "deny"])?.elements?.isEmpty == true)
    }

    @Test("a client without a tool setting says to use Gateway")
    func unsupported() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let server = try #require(harness.discover().servers.first { $0.enabledIn[.cursor] == .enabled })
        #expect(throws: ToolSwitchError.notSupported(client: "Cursor")) {
            try harness.toolSwitches.setDisabledTools(["x"], among: ["x"], server: server, in: .cursor)
        }
    }

    @Test("switched-off tools leave the client's tool count")
    func budget() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.codexConfig.text.replacingOccurrences(of: "startup_timeout_sec = 20", with: "startup_timeout_sec = 20\ndisabled_tools = [\"delete_branch\"]"), to: ".codex/config.toml")
        var server = try harness.server("filesystem")
        server.health = HealthResult(
            status: .passed, checkedAt: Date(), toolCount: 4,
            tools: githubTools.map { ToolSummary(name: $0, description: nil) },
            message: nil, stderr: nil, durationSeconds: 0.1, serverName: nil, serverVersion: nil
        )
        let budget = ToolBudget.evaluate(clientID: .codex, limit: nil, servers: [server], routes: [])
        #expect(budget.toolCount == 3)
        #expect(budget.maskedToolCount == 1)
    }
}
