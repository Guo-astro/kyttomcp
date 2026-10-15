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

/// Bounded excerpts of the model-visible fields that changed. These explain a
/// review; they do not certify that a server implements its stated safeguards.
public struct ContractFieldChange: Codable, Equatable, Sendable {
    public let path: String
    public let before: String?
    public let after: String?
    public let explanation: String?

    public init(path: String, before: String?, after: String?, explanation: String? = nil) {
        self.path = path
        self.before = before
        self.after = after
        self.explanation = explanation
    }
}

public struct ContractChange: Codable, Equatable, Sendable {
    public let kind: ContractChangeKind
    public let severity: ContractChangeSeverity
    public let toolName: String
    public let summary: String
    public let details: [ContractFieldChange]?

    public init(
        kind: ContractChangeKind,
        severity: ContractChangeSeverity,
        toolName: String,
        summary: String,
        details: [ContractFieldChange]? = nil
    ) {
        self.kind = kind
        self.severity = severity
        self.toolName = toolName
        self.summary = summary
        self.details = details
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
                    summary: "Model-facing description changed",
                    details: [ContractFieldChange(path: "description", before: excerpt(lhs.description), after: excerpt(rhs.description))]
                ))
            }
            if !sameSchema(lhs.inputSchemaJSON, rhs.inputSchemaJSON) {
                result.append(ContractChange(
                    kind: .inputSchemaChanged,
                    severity: .breaking,
                    toolName: name,
                    summary: "Input schema changed — review compatibility and safeguards",
                    details: schemaDetails(lhs.inputSchemaJSON, rhs.inputSchemaJSON)
                ))
            }
            if lhs.annotations != rhs.annotations {
                result.append(ContractChange(
                    kind: .annotationsChanged,
                    severity: .warning,
                    toolName: name,
                    summary: "Safety annotations changed",
                    details: annotationDetails(lhs.annotations, rhs.annotations)
                ))
            }
        }
        return result
    }

    private static func excerpt(_ value: String?) -> String? {
        value.map {
            let scalars = $0.unicodeScalars.prefix(801)
            return scalars.count > 800
                ? String(String.UnicodeScalarView(scalars.prefix(800))) + "… [truncated]"
                : $0
        }
    }

    private static func display(_ value: Any?) -> String? {
        guard let value else { return nil }
        guard let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .fragmentsAllowed, .withoutEscapingSlashes]),
              let text = String(data: data, encoding: .utf8) else { return nil }
        return excerpt(text)
    }

    private static func annotationDetails(_ before: ToolAnnotations?, _ after: ToolAnnotations?) -> [ContractFieldChange] {
        let fields: [(String, Bool?, Bool?)] = [
            ("readOnlyHint", before?.readOnlyHint, after?.readOnlyHint),
            ("destructiveHint", before?.destructiveHint, after?.destructiveHint),
            ("idempotentHint", before?.idempotentHint, after?.idempotentHint),
            ("openWorldHint", before?.openWorldHint, after?.openWorldHint),
        ]
        var result = fields.compactMap { name, lhs, rhs -> ContractFieldChange? in
            guard lhs != rhs else { return nil }
            return ContractFieldChange(path: "annotations." + name, before: lhs.map(String.init), after: rhs.map(String.init), explanation: "An annotation is a hint to the client, not an enforced permission.")
        }
        if result.isEmpty {
            result.append(ContractFieldChange(path: "annotations", before: before == nil ? nil : "{}", after: after == nil ? nil : "{}"))
        }
        return result
    }

    private static func schemaDetails(_ before: String?, _ after: String?) -> [ContractFieldChange] {
        // Parsing is bounded independently of rendering. Large or malformed
        // contracts remain changes, with a raw excerpt instead of a deep walk.
        guard let before, let after, before.utf8.count <= 1_000_000, after.utf8.count <= 1_000_000,
              let lhs = try? JSONSerialization.jsonObject(with: Data(before.utf8), options: [.fragmentsAllowed]),
              let rhs = try? JSONSerialization.jsonObject(with: Data(after.utf8), options: [.fragmentsAllowed]) else {
            return [ContractFieldChange(path: "inputSchema", before: excerpt(before), after: excerpt(after), explanation: "Inspect the original schema; a structured comparison is unavailable.")]
        }
        var result: [ContractFieldChange] = []
        var omitted = false
        var visited = 0
        func visit(_ lhs: Any?, _ rhs: Any?, _ path: String, _ depth: Int) {
            visited += 1
            if result.count >= 40 || visited > 500 { omitted = true; return }
            let leftBytes = lhs.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
            let rightBytes = rhs.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
            guard leftBytes != rightBytes else { return }
            if depth < 12, let left = lhs as? [String: Any], let right = rhs as? [String: Any] {
                for key in Set(left.keys).union(right.keys).sorted() {
                    if key.utf8.count > 256 || path.utf8.count > 1_024 {
                        let oldField = left[key].flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
                        let newField = right[key].flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
                        if oldField == newField { continue }
                        if result.count >= 40 { omitted = true; break }
                        result.append(ContractFieldChange(
                            path: "inputSchema/[long field path]", before: display(left[key]), after: display(right[key]),
                            explanation: "The schema field name or path is too long to display. Inspect the complete schema."
                        ))
                        continue
                    }
                    // JSON Pointer escaping makes names containing '/' or '~'
                    // unambiguous. Arrays remain whole so their order is visible.
                    let escaped = key.replacingOccurrences(of: "~", with: "~0").replacingOccurrences(of: "/", with: "~1")
                    visit(left[key], right[key], path + "/" + escaped, depth + 1)
                }
                return
            }
            let lower = path.lowercased()
            let guardRelated = ["dry_run", "dryrun", "confirm", "approval"].contains { lower.contains($0) }
                || (lower.hasSuffix("/required") && ((display(lhs) ?? "") + (display(rhs) ?? "")).lowercased().contains("confirm"))
            let explanation = guardRelated
                ? "A confirmation or dry-run field changed. This may alter how the model requests a guarded action; verify the server's actual enforcement."
                : (path.hasSuffix("/required") ? "Required arguments changed; existing calls may need different inputs." : nil)
            result.append(ContractFieldChange(path: path, before: display(lhs), after: display(rhs), explanation: explanation))
        }
        visit(lhs, rhs, "inputSchema", 0)
        if omitted {
            result.append(ContractFieldChange(path: "inputSchema", before: nil, after: nil, explanation: "Additional changes omitted. Inspect the complete schema before acknowledging."))
        }
        if result.isEmpty {
            result.append(ContractFieldChange(path: "inputSchema", before: excerpt(before), after: excerpt(after)))
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

    static func canonical(_ json: String) -> Data? {
        guard json.utf8.count <= 1_000_000 else { return nil }
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
