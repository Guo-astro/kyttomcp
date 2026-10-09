import Foundation

public enum ContractChangeKind: String, Codable, Sendable {
    case toolAdded
    case toolRemoved
    case descriptionChanged
    case inputSchemaChanged
    case annotationsChanged
}

public enum ContractChangeSeverity: String, Codable, Sendable {
    case info
    case warning
    case breaking
}

public struct ContractChange: Codable, Equatable, Sendable {
    public let kind: ContractChangeKind
    public let severity: ContractChangeSeverity
    public let toolName: String
    public let summary: String

    public init(
        kind: ContractChangeKind,
        severity: ContractChangeSeverity,
        toolName: String,
        summary: String
    ) {
        self.kind = kind
        self.severity = severity
        self.toolName = toolName
        self.summary = summary
    }
}

/// Compares the model-visible contract, not the server package version.
/// Descriptions and annotations matter because clients give them to the model.
public enum ContractGuard {
    public static func changes(from old: [ToolSummary], to new: [ToolSummary]) -> [ContractChange] {
        // A server may list one name twice (or several nameless tools, which all
        // become "(unnamed)"). That is broken input, not a reason to crash; clients
        // resolve it last-wins, so this does too.
        let before = Dictionary(old.map { ($0.name, $0) }, uniquingKeysWith: { _, last in last })
        let after = Dictionary(new.map { ($0.name, $0) }, uniquingKeysWith: { _, last in last })
        var result: [ContractChange] = []

        let beforeNames = Set(before.keys)
        let afterNames = Set(after.keys)

        for name in beforeNames.subtracting(afterNames).sorted() {
            result.append(ContractChange(
                kind: .toolRemoved,
                severity: .breaking,
                toolName: name,
                summary: "Tool removed"
            ))
        }
        for name in afterNames.subtracting(beforeNames).sorted() {
            result.append(ContractChange(
                kind: .toolAdded,
                severity: .info,
                toolName: name,
                summary: "New tool added"
            ))
        }
        for name in beforeNames.intersection(afterNames).sorted() {
            guard let lhs = before[name], let rhs = after[name] else { continue }
            if lhs.description != rhs.description {
                result.append(ContractChange(
                    kind: .descriptionChanged,
                    severity: .warning,
                    toolName: name,
                    summary: "Model-facing description changed"
                ))
            }
            if !sameSchema(lhs.inputSchemaJSON, rhs.inputSchemaJSON) {
                result.append(ContractChange(
                    kind: .inputSchemaChanged,
                    severity: .breaking,
                    toolName: name,
                    summary: "Input schema changed"
                ))
            }
            if lhs.annotations != rhs.annotations {
                result.append(ContractChange(
                    kind: .annotationsChanged,
                    severity: .warning,
                    toolName: name,
                    summary: "Safety annotations changed"
                ))
            }
        }
        return result
    }

    /// Whether two input schemas say the same thing.
    ///
    /// The stored text is the raw slice the server sent, so a server that only
    /// re-serializes its schema — keys in another order, other whitespace — would
    /// otherwise read as a breaking change, and false alarms teach people to
    /// acknowledge without reading. Object keys are sorted; array order is kept,
    /// because `enum`, `oneOf` or `prefixItems` can mean something else reordered.
    /// If either side does not parse, the raw text decides, so a change is never
    /// hidden by the canonicalization.
    static func sameSchema(_ lhs: String?, _ rhs: String?) -> Bool {
        if lhs == rhs { return true }
        guard let lhs, let rhs,
              let left = canonical(lhs),
              let right = canonical(rhs)
        else { return false }
        return left == right
    }

    private static func canonical(_ json: String) -> Data? {
        guard let object = try? JSONSerialization.jsonObject(
            with: Data(json.utf8),
            options: [.fragmentsAllowed]
        ) else { return nil }
        return try? JSONSerialization.data(
            withJSONObject: object,
            options: [.sortedKeys, .fragmentsAllowed, .withoutEscapingSlashes]
        )
    }
}
