import Foundation
import Testing
@testable import KyttoCore

/// Contract Guard against the key-reorder fixture from the rugsnare corpus.
///
/// `Fixtures/rugsnare-corpus-05/` holds the `tools/list` responses of two servers
/// that expose the same two tools, the second with every object's keys in reverse
/// order. From `corpus/05-key-reorder` in Paraphern/rugsnare, Apache-2.0 (licence
/// text beside the fixtures, notice in THIRD_PARTY_NOTICES.md). It is the regression
/// fixture for issue #12: a differ that compares the raw schema text reads this
/// pair as breaking, a canonicalizing one reads it as clean.
@Suite("Contract Guard corpus")
struct ContractGuardCorpusTests {
    @Test("reversing every key order is not a contract change, in either direction")
    func keyReorderIsClean() throws {
        let v1 = try tools(in: "v1-tools-list.json")
        let v2 = try tools(in: "v2-tools-list.json")

        #expect(v1.map(\.name) == ["create_note", "search_notes"])
        #expect(v1.map(\.name) == v2.map(\.name))
        // The two responses must differ as text, or this test proves nothing.
        #expect(v1.compactMap(\.inputSchemaJSON) != v2.compactMap(\.inputSchemaJSON))

        #expect(ContractGuard.changes(from: v1, to: v2).isEmpty)
        #expect(ContractGuard.changes(from: v2, to: v1).isEmpty)
    }

    @Test("a real change inside the reordered fixture is still caught")
    func realChangeStillFires() throws {
        let v1 = try tools(in: "v1-tools-list.json")
        let retyped = try tools(
            text: try fixtureText("v2-tools-list.json").replacingOccurrences(
                of: #""description":"Max results","type":"number""#,
                with: #""description":"Max results","type":"string""#
            )
        )

        let result = ContractGuard.changes(from: v1, to: retyped)
        #expect(result.count == 1)
        #expect(result.first?.toolName == "search_notes")
        #expect(result.first?.kind == .inputSchemaChanged)
        #expect(result.first?.severity == .breaking)
    }

    /// Reads tools the way `MCPStdioClient` does: each schema is the verbatim slice
    /// of the server's response, not a re-serialization.
    private func tools(in name: String) throws -> [ToolSummary] {
        try tools(text: fixtureText(name))
    }

    private func tools(text: String) throws -> [ToolSummary] {
        let document = try JSONDocument.parse(text)
        let nodes = try #require(document.value(at: ["result", "tools"])?.elements)
        return nodes.map { tool in
            ToolSummary(
                name: tool.lastValue("name")?.stringValue ?? "(unnamed)",
                description: tool.lastValue("description")?.stringValue,
                inputSchemaJSON: tool.lastValue("inputSchema").map { document.slice($0.span) }
            )
        }
    }

    private func fixtureText(_ name: String) throws -> String {
        let base = try #require(Bundle.module.resourceURL)
        let url = base.appending(path: "Fixtures").appending(path: "rugsnare-corpus-05").appending(path: name)
        return try String(contentsOf: url, encoding: .utf8)
    }
}
