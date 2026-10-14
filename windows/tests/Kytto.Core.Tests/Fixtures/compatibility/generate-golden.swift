import Foundation
@testable import KyttoCore

let output = URL(filePath: "/tmp/kytto-handoff-golden-fixtures")
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
let home = FileManager.default.temporaryDirectory.appending(path: "KyttoHandoffGolden-\(UUID().uuidString)")
defer { try? FileManager.default.removeItem(at: home) }
let config = home.appending(path: ".cursor/mcp.json")
try FileManager.default.createDirectory(at: config.deletingLastPathComponent(), withIntermediateDirectories: true)
let configText = #"{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory","--root","/Users/example/workspace"],"env":{"FIXTURE_TOKEN":"fixture-secret-do-not-export"},"cwd":"/Users/example/workspace"},"remote":{"type":"http","url":"https://example.test/mcp?token=fixture-url-secret","headers":{"Authorization":"Bearer fixture-header-secret"}}}}"#
try Data(configText.utf8).write(to: config)
let paths = KyttoPaths(home: home)
let ledger = DigestLedger()
let discovery = Discovery(home: home, locator: StubAppLocator(), ledger: ledger).run()
let profileStore = ProfileStore(paths: paths)
_ = try profileStore.create(name: "Research", serverIDs: ["memory", "remote"], tokenBudget: 12_000)
let tools: [ToolSummary] = [
    ToolSummary(name: "minimal", description: nil),
    ToolSummary(name: "search", description: "Find / files & <notes> — čučoriedka 😀", inputSchemaJSON: #"{"required":["query"],"properties":{"query":{"type":"string","default":"a/b"}},"type":"object"}"#, annotations: ToolAnnotations(readOnlyHint: true, destructiveHint: false), tokenCount: 999),
    ToolSummary(name: "search", description: "Find / files & <notes> — čučoriedka 😀", inputSchemaJSON: #"{ "type": "object", "properties": {"query": {"default":"a/b", "type":"string"}}, "required": ["query"] }"#, annotations: ToolAnnotations(readOnlyHint: true, destructiveHint: false), tokenCount: 1),
    ToolSummary(name: "numbers", description: "nulls, numeric defaults, array ordering", inputSchemaJSON: #"{"properties":{"amount":{"default":1.0},"scale":{"default":0.001},"choices":{"enum":["b","a",null]}},"type":"object"}"#, annotations: ToolAnnotations(idempotentHint: false, openWorldHint: true)),
    ToolSummary(name: "malformed", description: "Conservative raw schema fallback", inputSchemaJSON: "{broken"),
]
let health = HealthResult(status: .passed, checkedAt: .timestamp(), toolCount: 1, tools: [tools[1]], message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil)
try MetadataStore(paths: paths).record(health: health, tokenWeight: nil, for: "memory")
let service = PortableSetupService(home: home, writer: ConfigWriter(backups: BackupStore(paths: paths)), parkStore: ParkStore(paths: paths), ledger: ledger)
let exportFile = output.appending(path: "portable-v1-macos-export.json")
let summary = try service.export(to: exportFile, servers: discovery.servers, includeReviewedContracts: true)
_ = try service.inspect(file: exportFile)
let exported = try String(contentsOf: exportFile, encoding: .utf8)
precondition(!exported.contains("fixture-secret-do-not-export"))
precondition(!exported.contains("fixture-url-secret"))
precondition(!exported.contains("fixture-header-secret"))
precondition(!exported.contains("/Users/example/workspace"))
struct Vector: Encodable {
    let label: String
    let tool: ToolSummary
    let expectedDigest: String
    let expectedCanonicalSchema: String?
}
var vectors: [Vector] = []
for (index,tool) in tools.enumerated() {
    vectors.append(Vector(label: "vector-\(index+1)", tool: tool, expectedDigest: try PortableSetupService.contractDigest(tool), expectedCanonicalSchema: tool.inputSchemaJSON.flatMap(ContractGuard.canonical).map { String(decoding: $0, as: UTF8.self) }))
}
precondition(vectors[1].expectedDigest == vectors[2].expectedDigest)
let encoder = JSONEncoder()
encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
try encoder.encode(vectors).write(to: output.appending(path: "contract-digest-v1-vectors.json"))
print("Generated and validated real-core portable v1 export, \(summary.serverNames.count) servers, \(summary.profileNames.count) profile, and \(vectors.count) digest vectors.")
