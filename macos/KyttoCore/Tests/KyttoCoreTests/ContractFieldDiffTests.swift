import Foundation
import Testing
@testable import KyttoCore

@Suite("Readable contract changes")
struct ContractFieldDiffTests {
    private func diff(_ before: String, _ after: String) -> ContractChange? {
        ContractGuard.changes(
            from: [ToolSummary(name: "write", description: nil, inputSchemaJSON: before)],
            to: [ToolSummary(name: "write", description: nil, inputSchemaJSON: after)]
        ).first
    }

    @Test func guardDefaultAndRequiredAreExplained() throws {
        let change = try #require(diff(
            #"{"properties":{"dry_run":{"type":"boolean","default":true},"confirm":{"type":"boolean"}},"required":["confirm"]}"#,
            #"{"properties":{"dry_run":{"type":"boolean","default":false},"confirm":{"type":"boolean"}},"required":[]}"#
        ))
        let fields = try #require(change.details)
        let dryRun = try #require(fields.first { $0.path == "inputSchema/properties/dry_run/default" })
        #expect(dryRun.before == "true")
        #expect(dryRun.after == "false")
        #expect(dryRun.explanation?.contains("actual enforcement") == true)
        #expect(fields.first { $0.path == "inputSchema/required" }?.explanation != nil)
        #expect(change.severity == .breaking)
    }

    @Test func longEqualPrefixesDoNotHideChanges() throws {
        let text = String(repeating: "x", count: 900)
        let change = try #require(diff("{\"description\":\"\(text)a\"}", "{\"description\":\"\(text)b\"}"))
        #expect(change.details?.count == 1)
        #expect(change.details?.first?.before?.contains("truncated") == true)
    }

    @Test func boundsManyChangesAndEscapesNames() throws {
        let before = Dictionary(uniqueKeysWithValues: (0..<100).map { ("a/\($0)~", 0) })
        let after = before.mapValues { $0 + 1 }
        let lhs = String(decoding: try JSONSerialization.data(withJSONObject: before), as: UTF8.self)
        let rhs = String(decoding: try JSONSerialization.data(withJSONObject: after), as: UTF8.self)
        let fields = try #require(diff(lhs, rhs)?.details)
        #expect(fields.count <= 41)
        #expect(fields.first?.path.contains("~1") == true)
        #expect(fields.last?.explanation?.contains("omitted") == true)
    }

    @Test func oldMetadataDecodesWithoutDetails() throws {
        let bytes = Data(#"{"kind":"inputSchemaChanged","severity":"breaking","toolName":"write","summary":"Input schema changed"}"#.utf8)
        let old = try JSONDecoder().decode(ContractChange.self, from: bytes)
        #expect(old.details == nil)
    }

    @Test func malformedSchemaStillHasReviewableExcerpt() throws {
        let change = try #require(diff("{", "{\"new\":"))
        #expect(change.details?.first?.before == "{")
        #expect(change.details?.first?.after == "{\"new\":")
    }

    @Test func combiningMarksAndLongKeysCannotBypassDisplayBounds() throws {
        let cluster = "a" + String(repeating: "\u{0301}", count: 30_000)
        let changes = ContractGuard.changes(from: [ToolSummary(name: "write", description: cluster)], to: [ToolSummary(name: "write", description: "changed")])
        #expect(try #require(changes.first?.details?.first?.before).unicodeScalars.count < 900)
        let longKey = String(repeating: "x", count: 30_000)
        let lhs = String(decoding: try JSONSerialization.data(withJSONObject: [longKey: 0]), as: UTF8.self)
        let rhs = String(decoding: try JSONSerialization.data(withJSONObject: [longKey: 1]), as: UTF8.self)
        let field = try #require(diff(lhs, rhs)?.details?.first)
        #expect(field.path.utf8.count < 1_100)
        #expect(field.explanation?.contains("too long") == true)
        let unchangedLong = "{\"\(longKey)\":0,\"short\":1}"
        let changedShort = "{\"\(longKey)\":0,\"short\":2}"
        #expect(try #require(diff(unchangedLong, changedShort)?.details).map(\.path) == ["inputSchema/short"])
    }
}
