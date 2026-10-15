import Foundation
import KyttoCore
import Testing
@testable import KyttoMCP

struct PortableIntegrationTests {
    @Test("unreadable imported fingerprints cannot partially acknowledge a local alert")
    @MainActor
    func pendingReadFailurePreservesBaseline() throws {
        let home = FileManager.default.temporaryDirectory.appending(path: "KyttoPendingNative-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: home) }
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(at: config.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"mcpServers":{"memory":{"command":"node"}}}"#.utf8).write(to: config)
        let paths = KyttoPaths(home: home)
        let metadata = MetadataStore(paths: paths)
        for description in ["before", "after"] {
            let health = HealthResult(status: .passed, checkedAt: .timestamp(), toolCount: 1,
                                      tools: [ToolSummary(name: "read", description: description)],
                                      message: nil, stderr: nil, durationSeconds: 0,
                                      serverName: nil, serverVersion: nil)
            try metadata.record(health: health, tokenWeight: nil, for: "memory")
        }
        try Data("malformed".utf8).write(to: paths.root.appending(path: "pending-contract-imports.json"))
        let model = AppModel(home: home)
        let baseline = metadata.all()["memory"]?.contractTools
        #expect(throws: PortableSetupError.self) { try model.acknowledgeContract(serverID: "memory") }
        #expect(metadata.all()["memory"]?.contractTools == baseline)
        #expect(metadata.all()["memory"]?.contractChanges?.isEmpty == false)
    }

    @Test("native preview retains inputs and imports disabled without changing the source")
    @MainActor
    func nativePortableRoundTrip() throws {
        let root = FileManager.default.temporaryDirectory.appending(path: "KyttoPortableNative-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let sourceHome = root.appending(path: "source")
        let destinationHome = root.appending(path: "destination")
        let sourceConfig = sourceHome.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(at: sourceConfig.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: destinationHome, withIntermediateDirectories: true)
        let original = Data(#"{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"FIXTURE_TOKEN":"fixture-secret"}}}}"#.utf8)
        try original.write(to: sourceConfig)
        let source = AppModel(home: sourceHome)
        let file = root.appending(path: "portable.json")
        let summary = try source.exportPortableSetup(to: file, includeReviewedContracts: false)
        #expect(summary.serverNames == ["memory"])
        #expect(!(try String(contentsOf: file, encoding: .utf8)).contains("fixture-secret"))
        let destination = AppModel(home: destinationHome)
        let inspected = try destination.inspectPortableSetup(file: file)
        let values = Dictionary(uniqueKeysWithValues: inspected.requirements.map { ($0.id, $0.kind == .path ? "/Users/example/project" : "replacement-fixture") })
        let preview = try destination.previewPortableSetup(file: file, targetClients: [.cursor], values: values)
        #expect(!preview.enabled)
        #expect(destination.current().servers.isEmpty)
        try destination.applyPortableSetup(previewID: preview.id)
        let imported = try #require(destination.current().servers.first { $0.name == "memory" })
        #expect(imported.enabledIn[.cursor] == .disabled)
        #expect(try Data(contentsOf: sourceConfig) == original)
        #expect(throws: PortableSetupError.self) { try destination.applyPortableSetup(previewID: preview.id) }
    }
}
