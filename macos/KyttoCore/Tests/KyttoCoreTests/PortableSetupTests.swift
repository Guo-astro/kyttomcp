import Foundation
import Testing
@testable import KyttoCore

private extension ToggleHarness {
    var portable: PortableSetupService {
        PortableSetupService(home: home.root, writer: ConfigWriter(backups: backups), parkStore: parkStore, ledger: ledger)
    }
    var portableFile: URL { home.root.appending(path: "setup.kytto.json") }
}

private func inputValues(_ summary: PortableSetupSummary) -> [String: String] {
    Dictionary(uniqueKeysWithValues: summary.requirements.map { requirement in
        let value: String
        if requirement.kind == .path { value = "/tmp/mapped-portable-path" }
        else if requirement.label.hasSuffix("remote URL") { value = "https://example.test/mcp" }
        else if requirement.label.contains("argument") { value = "@modelcontextprotocol/server-memory" }
        else { value = "replacement-value" }
        return (requirement.id, value)
    })
}

@Suite("Portable setup — safe export and import")
struct PortableSetupTests {
    @Test("export never copies env, URL, header, argument, path or unknown-field values")
    func excludesSecretValues() throws {
        let source = try ToggleHarness()
        defer { source.cleanUp() }
        try source.home.write(#"""
        {"mcpServers": {
          "local": {"command": "/Users/private-user/private-command", "args": ["--token=argument-secret", "-y", "/private/path"], "env": {"API_TOKEN": "env-secret"}, "opaque": {"token": "unknown-secret"}, "cwd": "/Users/private-user/private-project"},
          "remote": {"type":"http", "url":"https://url-user:url-password@example.test/path-secret?token=query-secret#fragment-secret", "headers":{"Authorization":"Bearer header-secret"}}
        }}
        """#, to: ".cursor/mcp.json")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        let text = try String(contentsOf: source.portableFile, encoding: .utf8)
        for secret in ["private-user", "private-command", "argument-secret", "env-secret", "unknown-secret", "url-user", "url-password", "path-secret", "query-secret", "fragment-secret", "header-secret", "/private/path", "private-project"] {
            #expect(!text.contains(secret), "export leaked \(secret)")
        }
        #expect(summary.serverNames == ["local", "remote"])
        #expect(summary.requirements.count == 7)
        #expect(text.contains("API_TOKEN"))
        #expect(text.contains("Authorization"))
        #expect(!text.contains("opaque"))
        let mode = try FileManager.default.attributesOfItem(atPath: source.portableFile.path)[.posixPermissions] as? NSNumber
        #expect(mode?.intValue == 0o600)
    }

    @Test("an absolute npx path exports, and one unexportable copy does not sink the rest")
    func absoluteRunnerAndPartialExport() throws {
        let source = try ToggleHarness()
        defer { source.cleanUp() }
        try source.home.write(#"""
        {"mcpServers": {
          "memory": {"command": "/opt/homebrew/bin/npx", "args": ["-y", "@modelcontextprotocol/server-memory"]},
          "brave search": {"command": "npx", "args": ["-y", "pkg"]}
        }}
        """#, to: ".cursor/mcp.json")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        #expect(summary.serverNames == ["memory"])
        #expect(summary.warnings.contains { $0.contains("1 ") })
        let text = try String(contentsOf: source.portableFile, encoding: .utf8)
        #expect(!text.contains("/opt/homebrew"))
    }

    @Test("labels say where a value goes, whatever the file claims")
    func labelsComeFromStructure() throws {
        let source = try ToggleHarness()
        defer { source.cleanUp() }
        try source.home.write(#"{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"KEY":"v"}}}}"#, to: ".cursor/mcp.json")
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        var text = try String(contentsOf: source.portableFile, encoding: .utf8)
        text = text.replacingOccurrences(of: "memory (cursor): environment KEY", with: "Your GitHub personal access token")
        try text.write(to: source.portableFile, atomically: true, encoding: .utf8)
        let summary = try source.portable.inspect(file: source.portableFile)
        #expect(summary.requirements.first?.label == "memory (cursor): environment KEY — for npx -y @modelcontextprotocol/server-memory")
    }

    @Test("round trip keeps disabled presence copies parked and imports profiles without applying them")
    func disabledRoundTrip() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"KEY":"original-secret"}}}}"#, to: ".cursor/mcp.json")
        _ = try ProfileStore(paths: source.paths).create(name: "Writing", serverIDs: ["memory"], tokenBudget: 12_000)
        let service = destination.portable
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        #expect(summary.requirements.count == 1, "the recognized registry package stays portable; only env needs input")
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor, .codex], values: inputValues(summary))
        #expect(!FileManager.default.fileExists(atPath: destination.paths.root.path))
        #expect(preview.enabled == false)
        let result = try service.apply(previewID: preview.id)
        #expect(result.changedClients == [.codex])
        #expect(!FileManager.default.fileExists(atPath: destination.home.root.appending(path: ".cursor/mcp.json").path))
        let parked = try #require(destination.parkStore.parked(clientID: .cursor, serverName: "memory"))
        let definition = try JSONDocument.parse(parked.sourceText)
        #expect(definition.root["command"]?.stringValue == "npx")
        #expect(definition.root["args"]?.elements?.last?.stringValue == "@modelcontextprotocol/server-memory")
        #expect(definition.root["env"]?["KEY"]?.stringValue == "replacement-value")
        let toml = try TOMLDocument.parse(destination.text(".codex/config.toml"))
        #expect(toml.table(at: ["mcp_servers", "memory"])?.value("enabled")?.boolValue == false)
        let profiles = ProfileStore(paths: destination.paths).all()
        #expect(profiles.count == 1)
        #expect(profiles.first?.serverIDs == ["memory"])
        #expect(profiles.first?.tokenBudget == 12_000)
        #expect(destination.discover().servers.first(where: { $0.name == "memory" })?.enabledIn[.cursor] == .disabled)
        #expect(throws: PortableSetupError.stalePreview) { try service.apply(previewID: preview.id) }
    }

    @Test("a server imported switched off arrives off in Antigravity and OpenCode, in their own shapes")
    func flagClientsImport() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"KEY":"original-secret"}}}}"#, to: ".cursor/mcp.json")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.antigravity, .openCode], values: inputValues(summary))
        let result = try service.apply(previewID: preview.id)
        #expect(Set(result.changedClients) == [.antigravity, .openCode])
        #expect(destination.parkStore.all().isEmpty)

        let antigravity = try #require(JSONDocument.parse(destination.text(".gemini/config/mcp_config.json")).value(at: ["mcpServers", "memory"]))
        #expect(antigravity["disabled"]?.boolValue == true)
        #expect(antigravity["env"]?["KEY"]?.stringValue == "replacement-value")

        let openCode = try #require(JSONDocument.parse(destination.text(".config/opencode/opencode.json")).value(at: ["mcp", "memory"]))
        #expect(openCode["enabled"]?.boolValue == false)
        #expect(openCode["type"]?.stringValue == "local")
        #expect(openCode["command"]?.elements?.first?.stringValue == "npx")
        #expect(openCode["environment"]?["KEY"]?.stringValue == "replacement-value")

        let memory = try #require(destination.discover().servers.first { $0.name == "memory" })
        #expect(memory.enabledIn[.antigravity] == .disabled)
        #expect(memory.enabledIn[.openCode] == .disabled)
    }

    @Test("explicit enabled import preserves JSONC, TOML comments, unknown fields and backups")
    func preservation() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"npx","args":["-y"]}}}"#, to: ".cursor/mcp.json")
        let json = "{\n  // keep this exactly\n  \"preferences\": {\"opaque\": 42},\n  \"mcpServers\": {\"existing\": {\"command\": \"node\", \"unknown\": true}}\n}\n"
        let toml = "# keep this comment\nmodel = \"example\"\n\n[projects.\"/keep/path\"]\ntrust_level = \"trusted\"\n"
        try destination.home.write(json, to: ".cursor/mcp.json")
        try destination.home.write(toml, to: ".codex/config.toml")
        let service = destination.portable
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor, .codex], enableImportedServers: true)
        let result = try service.apply(previewID: preview.id)
        #expect(result.backupIDs.count == 2)
        let jsonAfter = try destination.text(".cursor/mcp.json")
        #expect(jsonAfter.contains("// keep this exactly"))
        #expect(jsonAfter.contains("\"preferences\": {\"opaque\": 42}"))
        #expect(jsonAfter.contains("\"existing\": {\"command\": \"node\", \"unknown\": true}"))
        let tomlAfter = try destination.text(".codex/config.toml")
        #expect(tomlAfter.hasPrefix(toml))
        #expect(try TOMLDocument.parse(tomlAfter).table(at: ["mcp_servers", "new"])?.value("enabled") == nil)
    }

    @Test("Claude Code import creates a real disabled definition and keeps existing deny settings")
    func denyList() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"npx"}}}"#, to: ".cursor/mcp.json")
        try destination.home.write(#"{"deniedMcpServers":[{"serverName":"existing"}],"unrelated":true}"#, to: ".claude/settings.json")
        let service = destination.portable
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        let preview = try service.preview(file: source.portableFile, targetClients: [.claudeCode])
        _ = try service.apply(previewID: preview.id)
        let config = try JSONDocument.parse(destination.text(".claude.json"))
        #expect(config.root["mcpServers"]?["new"]?["command"]?.stringValue == "npx")
        let settings = try JSONDocument.parse(destination.text(".claude/settings.json"))
        #expect(settings.root["unrelated"]?.boolValue == true)
        #expect(settings.root["deniedMcpServers"]?.elements?.count == 2)
        #expect(destination.discover().servers.first(where: { $0.name == "new" })?.enabledIn[.claudeCode] == .disabled)
    }

    @Test("remote headers use each destination's own format and URL is supplied natively")
    func remoteRoundTrip() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write("[mcp_servers.remote]\nurl = \"https://private.test/mcp\"\n\n[mcp_servers.remote.http_headers]\nAuthorization = \"Bearer original-secret\"\n", to: ".codex/config.toml")
        let service = destination.portable
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        #expect(summary.requirements.count == 2)
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor, .codex], values: inputValues(summary))
        _ = try service.apply(previewID: preview.id)
        let parked = try #require(destination.parkStore.parked(clientID: .cursor, serverName: "remote"))
        #expect(try JSONDocument.parse(parked.sourceText).root["headers"]?["Authorization"]?.stringValue == "replacement-value")
        let toml = try TOMLDocument.parse(destination.text(".codex/config.toml"))
        #expect(toml.table(at: ["mcp_servers", "remote", "http_headers"])?.value("Authorization")?.stringValue == "replacement-value")
        #expect(toml.table(at: ["mcp_servers", "remote"])?.value("url")?.stringValue == "https://example.test/mcp")
    }

    @Test("Codex inline/static/environment header shapes and bearer references become required inputs")
    func alternateHeaderShapes() throws {
        let source = try ToggleHarness()
        defer { source.cleanUp() }
        try source.home.write("""
        [mcp_servers]
        inline = { url = "https://inline.test/mcp", http_headers = { Authorization = "inline-secret" }, env_http_headers = { "X-Api-Key" = "API_KEY" } }
        local = { command = "node", cwd = "/private/project", args = ["/private/server.js"] }

        [mcp_servers.remote]
        url = "https://remote.test/mcp"
        bearer_token_env_var = "BEARER_VALUE"
        http_headers = { "X-Static" = "table-inline-secret" }

        [mcp_servers.remote.env_http_headers]
        "X-Env" = "ENV_VALUE"
        """, to: ".codex/config.toml")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        #expect(summary.requirements.contains { $0.label.contains("inline") && $0.label.hasSuffix("header Authorization") })
        #expect(summary.requirements.contains { $0.label.contains("inline") && $0.label.hasSuffix("header X-Api-Key") })
        #expect(summary.requirements.contains { $0.label.contains("remote") && $0.label.hasSuffix("header Authorization") })
        #expect(summary.requirements.contains { $0.label.hasSuffix("header X-Static") })
        #expect(summary.requirements.contains { $0.label.hasSuffix("header X-Env") })
        #expect(summary.requirements.contains { $0.label.contains("local") && $0.label.contains("working directory") })
        let text = try String(contentsOf: source.portableFile, encoding: .utf8)
        for privateValue in ["inline-secret", "table-inline-secret", "ENV_VALUE", "BEARER_VALUE", "/private/project", "/private/server.js"] {
            #expect(!text.contains(privateValue))
        }
    }

    @Test("imported reviewed contracts stay pending and cannot establish a trusted baseline")
    func contractReferenceIsUntrusted() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"npx"}}}"#, to: ".cursor/mcp.json")
        let tools = [ToolSummary(name: "tool", description: "description-secret", inputSchemaJSON: #"{"default":"schema-secret"}"#)]
        let health = HealthResult(status: .passed, checkedAt: .timestamp(), toolCount: 1, tools: tools,
                                  message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil)
        try MetadataStore(paths: source.paths).record(health: health, tokenWeight: nil, for: "new")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers, includeReviewedContracts: true)
        #expect(summary.pendingContractCount == 1)
        let text = try String(contentsOf: source.portableFile, encoding: .utf8)
        #expect(!text.contains("description-secret"))
        #expect(!text.contains("schema-secret"))
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor])
        _ = try service.apply(previewID: preview.id)
        #expect(try service.pendingContractReferences().count == 1)
        #expect(MetadataStore(paths: destination.paths).metadata(for: "new") == nil)
        #expect(try service.pendingContractSummary().first?.matching == nil)
        #expect(throws: PortableSetupError.localContractNotChecked) { try service.reviewPendingContract(serverID: "new") }
        #expect(try service.pendingContractReferences().count == 1)
    }

    @Test("pending contract matching ignores token counts, schema key order and tool order but preserves array semantics")
    func stableContractMatching() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"npx"}}}"#, to: ".cursor/mcp.json")
        let before = [
            ToolSummary(name: "first", description: "Read", inputSchemaJSON: #"{"properties":{"mode":{"enum":["a","b"]}},"type":"object"}"#, tokenCount: 12),
            ToolSummary(name: "second", description: nil, tokenCount: 20),
        ]
        func health(_ tools: [ToolSummary], status: HealthResult.Status = .passed, offset: TimeInterval) -> HealthResult {
            HealthResult(status: status, checkedAt: Date.timestamp().addingTimeInterval(offset), toolCount: tools.count, tools: tools,
                         message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil)
        }
        try MetadataStore(paths: source.paths).record(health: health(before, offset: 0), tokenWeight: nil, for: "new")
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers, includeReviewedContracts: true)
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor])
        _ = try service.apply(previewID: preview.id)
        let reordered = [
            ToolSummary(name: "second", description: nil, tokenCount: 900),
            ToolSummary(name: "first", description: "Read", inputSchemaJSON: #"{ "type":"object", "properties": {"mode":{"enum":["a", "b"]}} }"#, tokenCount: 600),
        ]
        let metadata = MetadataStore(paths: destination.paths)
        try metadata.record(health: health(reordered, offset: 1), tokenWeight: nil, for: "new")
        let matching = try #require(service.pendingContractSummary().first)
        #expect(matching.serverID == "new")
        #expect(matching.toolCount == 2)
        #expect(matching.matching == true)
        let changed = [
            ToolSummary(name: "second", description: nil),
            ToolSummary(name: "first", description: "Read", inputSchemaJSON: #"{"type":"object","properties":{"mode":{"enum":["b","a"]}}}"#),
        ]
        try metadata.record(health: health(changed, offset: 2), tokenWeight: nil, for: "new")
        #expect(try service.pendingContractSummary().first?.matching == false)
        try metadata.record(health: health([], status: .failed, offset: 3), tokenWeight: nil, for: "new")
        #expect(try service.pendingContractSummary().first?.matching == nil)
        #expect(throws: PortableSetupError.localContractNotChecked) { try service.reviewPendingContract(serverID: "new") }
        try metadata.record(health: health(changed, offset: 4), tokenWeight: nil, for: "new")
        let baselineBefore = metadata.metadata(for: "new")?.contractTools
        try service.reviewPendingContract(serverID: "new")
        #expect(try service.pendingContractSummary().isEmpty)
        #expect(metadata.metadata(for: "new")?.contractTools == baselineBefore, "review never imports trust or changes the local baseline")
        #expect(metadata.metadata(for: "new")?.contractChanges?.isEmpty == false)
    }

    @Test("malformed pending references report a safe error and survive attempted review")
    func malformedPendingContractStore() throws {
        let destination = try ToggleHarness()
        defer { destination.cleanUp() }
        let url = destination.paths.root.appending(path: "pending-contract-imports.json")
        let malformed = #"[{"serverID":"private-secret","toolDigests":{"tool":"invalid-digest-secret"}}]"#
        try AtomicWriter.write(malformed, to: url)
        let service = destination.portable
        #expect(throws: PortableSetupError.pendingContractsUnreadable) { try service.pendingContractSummary() }
        #expect(throws: PortableSetupError.pendingContractsUnreadable) { try service.reviewPendingContract(serverID: "private-secret") }
        #expect(try String(contentsOf: url, encoding: .utf8) == malformed)
        #expect(!PortableSetupError.pendingContractsUnreadable.localizedDescription.contains("private-secret"))
    }

    @Test("cached passing health before import cannot review a contract; legacy pending arrays also require a new check")
    func freshCheckAfterImport() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"npx"}}}"#, to: ".cursor/mcp.json")
        let tools = [ToolSummary(name: "read", description: "Read")]
        func health(at checkedAt: Date) -> HealthResult {
            HealthResult(status: .passed, checkedAt: checkedAt, toolCount: 1, tools: tools,
                         message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil)
        }
        let old = Date.timestamp().addingTimeInterval(-60)
        try MetadataStore(paths: source.paths).record(health: health(at: old), tokenWeight: nil, for: "new")
        let metadata = MetadataStore(paths: destination.paths)
        try metadata.record(health: health(at: old), tokenWeight: nil, for: "new")
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers, includeReviewedContracts: true)
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor])
        _ = try service.apply(previewID: preview.id)
        #expect(try service.pendingContractSummary().first?.matching == nil)
        #expect(throws: PortableSetupError.localContractNotChecked) { try service.reviewPendingContract(serverID: "new") }
        try metadata.record(health: health(at: .timestamp().addingTimeInterval(1)), tokenWeight: nil, for: "new")
        #expect(try service.pendingContractSummary().first?.matching == true)

        // The previous on-disk representation had no locally trusted import
        // date. Its first read sets an in-memory gate shared by service values.
        let legacyDestination = try ToggleHarness()
        defer { legacyDestination.cleanUp() }
        let exported = try JSONDecoder().decode(PortableSetup.self, from: Data(contentsOf: source.portableFile))
        let pendingURL = legacyDestination.paths.root.appending(path: "pending-contract-imports.json")
        try AtomicWriter.write(try JSONEncoder().encode(exported.reviewedContracts), to: pendingURL)
        let legacyMetadata = MetadataStore(paths: legacyDestination.paths)
        try legacyMetadata.record(health: health(at: old), tokenWeight: nil, for: "new")
        #expect(try legacyDestination.portable.pendingContractSummary().first?.matching == nil)
        #expect(throws: PortableSetupError.localContractNotChecked) { try legacyDestination.portable.reviewPendingContract(serverID: "new") }
        try legacyMetadata.record(health: health(at: .timestamp().addingTimeInterval(1)), tokenWeight: nil, for: "new")
        #expect(try legacyDestination.portable.pendingContractSummary().first?.matching == true)
        try legacyDestination.portable.reviewPendingContract(serverID: "new")
        #expect(try legacyDestination.portable.pendingContractSummary().isEmpty)
    }

    @Test("bundled and custom-source definitions never become portable writable configurations")
    func excludesBundledAndCustom() throws {
        let source = try ToggleHarness()
        defer { source.cleanUp() }
        let custom = ClientID.custom(UUID())
        let server = Server(id: "bundled", name: "bundled", transport: .stdio, command: "npx", args: [], env: [], url: nil,
                            enabledIn: [.cursor: .enabled, custom: .enabled], origin: .configFile(.cursor), fingerprint: "unused", isBundled: true,
                            definitionSource: #"{"command":"npx"}"#)
        var mixed = server
        mixed.definitionsByClient = [.cursor: ClientDefinition(from: server), custom: ClientDefinition(transport: .stdio, command: "node", args: [], env: [], url: nil,
                                                                                                      origin: .configFile(custom), isBundled: false, definitionSource: "{}")]
        let summary = try source.portable.export(to: source.portableFile, servers: [mixed])
        #expect(summary.serverNames.isEmpty)
        #expect(summary.requirements.isEmpty)
    }
}

@Suite("Portable setup — untrusted files and transaction safety")
struct PortableSetupSafetyTests {
    private func source() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(#"{"mcpServers":{"new":{"command":"npx"}}}"#, to: ".cursor/mcp.json")
        _ = try harness.portable.export(to: harness.portableFile, servers: harness.discover().servers)
        return harness
    }

    @Test("strict version, duplicate-key, unknown-field and size validation")
    func strictValidation() throws {
        let source = try source()
        defer { source.cleanUp() }
        let good = try String(contentsOf: source.portableFile, encoding: .utf8)
        let mutations = [
            good.replacingOccurrences(of: "\"version\" : 1", with: "\"version\" : 99"),
            good.replacingOccurrences(of: "\"format\" :", with: "\"version\":1,\"format\" :"),
            good.replacingOccurrences(of: "\"format\" :", with: "\"unexpected\":true,\"format\" :"),
            good.replacingOccurrences(of: "\"literal\" : \"npx\"", with: "\"literal\" : \"sh\""),
            String(repeating: " ", count: PortableSetupService.maximumFileBytes + 1),
        ]
        for text in mutations {
            try AtomicWriter.write(text, to: source.portableFile)
            #expect(throws: PortableSetupError.self) { try source.portable.inspect(file: source.portableFile) }
        }
    }

    @Test("export cannot overwrite a live client configuration, recovery store or symlink alias")
    func protectedExportDestinations() throws {
        let source = try source()
        defer { source.cleanUp() }
        let config = source.home.root.appending(path: ".cursor/mcp.json")
        let before = try Data(contentsOf: config)
        let servers = source.discover().servers
        #expect(throws: PortableSetupError.self) { try source.portable.export(to: config, servers: servers) }
        #expect(try Data(contentsOf: config) == before)
        #expect(throws: PortableSetupError.self) { try source.portable.export(to: source.paths.profiles, servers: servers) }
        let alias = source.home.root.appending(path: "alias.json")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: config)
        #expect(throws: PortableSetupError.self) { try source.portable.export(to: alias, servers: servers) }
        #expect(try Data(contentsOf: config) == before)
    }

    @Test("every required input is validated before touching any destination")
    func missingInputs() throws {
        let source = try ToggleHarness()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try source.home.write(#"{"mcpServers":{"new":{"command":"/private/tool","env":{"TOKEN":"secret"}}}}"#, to: ".cursor/mcp.json")
        let summary = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        #expect(throws: PortableSetupError.self) { try destination.portable.preview(file: source.portableFile, targetClients: [.cursor]) }
        var values = inputValues(summary)
        let path = try #require(summary.requirements.first(where: { $0.kind == .path }))
        values[path.id] = "./relative"
        #expect(throws: PortableSetupError.self) { try destination.portable.preview(file: source.portableFile, targetClients: [.cursor], values: values) }
        #expect(!FileManager.default.fileExists(atPath: destination.paths.root.path))
    }

    @Test("existing parked definitions and case-insensitive in-file names refuse import")
    func conflicts() throws {
        let source = try source()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        try destination.home.write(#"{"mcpServers":{"NEW":{"command":"keep-me"}}}"#, to: ".cursor/mcp.json")
        let before = try destination.text(".cursor/mcp.json")
        #expect(throws: PortableSetupError.self) { try destination.portable.preview(file: source.portableFile, targetClients: [.cursor]) }
        #expect(try destination.text(".cursor/mcp.json") == before)
        try destination.parkStore.park(clientID: .codex, serverName: "new", sourceText: "{}")
        #expect(throws: PortableSetupError.self) { try destination.portable.preview(file: source.portableFile, targetClients: [.codex]) }
    }

    @Test("external changes invalidate preview, including a target that would only receive a parked definition")
    func staleDestination() throws {
        let source = try source()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.cursor])
        try destination.home.write(#"{"mcpServers":{"new":{"command":"external"}}}"#, to: ".cursor/mcp.json")
        #expect(throws: PortableSetupError.stalePreview) { try service.apply(previewID: preview.id) }
        #expect(destination.parkStore.all().isEmpty)
        #expect(try destination.text(".cursor/mcp.json").contains("external"))
    }

    @Test("source file change and profile save invalidate native plans")
    func staleSourceAndProfile() throws {
        let source = try source()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.codex])
        try AtomicWriter.write("{}", to: source.portableFile)
        #expect(throws: PortableSetupError.stalePreview) { try service.apply(previewID: preview.id) }
        _ = try source.portable.export(to: source.portableFile, servers: source.discover().servers)
        let next = try service.preview(file: source.portableFile, targetClients: [.codex])
        _ = try ProfileStore(paths: destination.paths).create(name: "Other", serverIDs: [])
        #expect(throws: PortableSetupError.stalePreview) { try service.apply(previewID: next.id) }
        #expect(!FileManager.default.fileExists(atPath: destination.home.root.appending(path: ".codex/config.toml").path))
    }

    @Test("a later destination failure restores earlier config and deny-list writes")
    func rollback() throws {
        let source = try source()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        let service = destination.portable
        let preview = try service.preview(file: source.portableFile, targetClients: [.claudeCode, .codex])
        // A non-directory parent makes the later Codex commit fail without
        // relying on permissions, installed helpers or any real client files.
        try destination.home.write("not a directory", to: ".codex")
        #expect(throws: (any Error).self) { try service.apply(previewID: preview.id) }
        #expect(!FileManager.default.fileExists(atPath: destination.home.root.appending(path: ".claude.json").path))
        #expect(!FileManager.default.fileExists(atPath: destination.home.root.appending(path: ".claude/settings.json").path))
        #expect(destination.parkStore.all().isEmpty)
    }

    @Test("concurrent preview commits cannot silently replace each other's imported setup")
    func concurrentApply() async throws {
        let source = try source()
        let destination = try ToggleHarness()
        defer { source.cleanUp(); destination.cleanUp() }
        let first = destination.portable
        let second = destination.portable
        let a = try first.preview(file: source.portableFile, targetClients: [.codex])
        let b = try second.preview(file: source.portableFile, targetClients: [.codex])
        let successes = await withTaskGroup(of: Bool.self, returning: Int.self) { group in
            group.addTask { (try? first.apply(previewID: a.id)) != nil }
            group.addTask { (try? second.apply(previewID: b.id)) != nil }
            var count = 0
            for await success in group { if success { count += 1 } }
            return count
        }
        #expect(successes == 1)
        #expect(destination.discover().servers.filter { $0.name == "new" }.count == 1)
    }
}
