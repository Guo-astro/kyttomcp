import Foundation
import Testing
@testable import KyttoCore

/// `~/.claude.json` carries Claude Code's history and runs to megabytes. One
/// discovery pass used to read and parse it once for the project list, once for
/// the global map and once more per project scope.
@Suite("Discovery — every file read and parsed once per pass")
struct DiscoveryReadOnceTests {
    private static let projects = [
        "/Users/example/one", "/Users/example/two", "/Users/example/three",
    ]

    /// A global server, three project scopes with servers each, and a long
    /// tail of non-MCP bookkeeping like the history Claude Code writes.
    private static func claudeJSON(history: Int = 200, globalCommand: String = "npx") -> String {
        let projects = Self.projects.enumerated().map { index, path in
            #""\#(path)": {"mcpServers": {"scoped-\#(index)": {"command": "node", "args": ["s\#(index).js"]}}, "history": []}"#
        }.joined(separator: ",\n")
        let entries = (0..<history).map { #"{"display": "message \#($0)", "pastedContents": {}}"# }
            .joined(separator: ",")
        return """
        {
          "numStartups": \(history),
          "mcpServers": {"global": {"command": "\(globalCommand)", "args": ["-y", "pkg"]}},
          "projects": {
        \(projects)
          },
          "history": [\(entries)]
        }
        """
    }

    private static func discovery(_ home: FakeHome, ledger: DigestLedger? = nil) -> Discovery {
        Discovery(
            home: home.root,
            locator: StubAppLocator(executables: ["claude", "codex"]),
            ledger: ledger
        )
    }

    @Test func claudeJSONIsReadAndParsedOnceForEveryScope() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Self.claudeJSON(), to: ".claude.json")
        try home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")

        let ledger = DigestLedger()
        let discovery = Self.discovery(home, ledger: ledger)
        let result = discovery.run()

        // Every scope is still there…
        let names = Set(result.servers.map(\.name))
        #expect(names.isSuperset(of: ["global", "scoped-0", "scoped-1", "scoped-2"]))
        #expect(result.clients.filter { $0.configurationScope == .workspace }.count == Self.projects.count)

        // …from one read and one parse of the shared file.
        let path = home.root.appending(path: ".claude.json").standardizedFileURL.path
        #expect(discovery.files.diskReads[path] == 1)
        #expect(discovery.files.jsonParses[path] == 1)
        // The deny-list file is shared by the global map and the plugin
        // settings of every scope.
        let settings = home.root.appending(path: ".claude/settings.json").standardizedFileURL.path
        #expect(discovery.files.diskReads[settings] == 1)
        #expect(discovery.files.jsonParses[settings] == 1)

        // The digest guard still knows exactly what was read.
        let bytes = try Data(contentsOf: home.root.appending(path: ".claude.json"))
        #expect(ledger.digest(for: home.root.appending(path: ".claude.json")) == ConfigWriter.digest(of: bytes))
    }

    @Test func codexConfigIsParsedOnceForServersAndPlugins() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.codexConfig.text, to: ".codex/config.toml")

        let discovery = Self.discovery(home)
        let result = discovery.run()
        #expect(!result.servers.isEmpty)

        let path = home.root.appending(path: ".codex/config.toml").standardizedFileURL.path
        #expect(discovery.files.diskReads[path] == 1)
        #expect(discovery.files.tomlParses[path] == 1)
    }

    @Test func aSecondRunReadsTheDiskAgain() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Self.claudeJSON(), to: ".claude.json")

        let ledger = DigestLedger()
        let discovery = Self.discovery(home, ledger: ledger)
        _ = discovery.run()

        try home.write(Self.claudeJSON(globalCommand: "uvx"), to: ".claude.json")
        let second = discovery.run()

        #expect(second.servers.first { $0.name == "global" }?.command == "uvx")
        let url = home.root.appending(path: ".claude.json")
        #expect(ledger.digest(for: url) == ConfigWriter.digest(of: url))
    }

    @Test func aParseErrorIsReportedForEveryScopeAsBefore() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(#"{"mcpServers": {"broken": "#, to: ".claude.json")

        let discovery = Self.discovery(home)
        let result = discovery.run()
        let path = home.root.appending(path: ".claude.json").standardizedFileURL.path
        #expect(discovery.files.jsonParses[path] == 1)
        #expect(result.diagnostics.contains { $0.clientID == .claudeCode && $0.severity == .error })
    }
}
