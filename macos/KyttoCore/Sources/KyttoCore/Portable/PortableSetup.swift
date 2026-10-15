import Foundation

/// The portable format is a whitelist, never a copy of a client's raw config.
/// Values Kytto cannot prove safe to share become native-only required inputs.
public struct PortableSetup: Codable, Equatable, Sendable {
    public static let currentVersion = 1
    public let format: String
    public let version: Int
    public let servers: [PortableServer]
    public let profiles: [PortableProfile]
    public let requirements: [PortableRequirement]
    public let reviewedContracts: [PortableContractReference]
}

public struct PortableValue: Codable, Equatable, Sendable {
    public let literal: String?
    public let requirementID: String?

    static func literal(_ value: String) -> Self { Self(literal: value, requirementID: nil) }
    static func required(_ id: String) -> Self { Self(literal: nil, requirementID: id) }
}

public struct PortableServer: Codable, Equatable, Sendable {
    public let name: String
    public let sourceClient: ClientID
    public let wasEnabled: Bool
    public let transport: Transport
    public let command: PortableValue?
    public let args: [PortableValue]
    public let environment: [String: PortableValue]
    public let headers: [String: PortableValue]
    public let url: PortableValue?
    public let workingDirectory: PortableValue?
}

public struct PortableProfile: Codable, Equatable, Sendable {
    public let name: String
    public let serverIDs: [String]
    public let tokenBudget: Int?
}

public struct PortableRequirement: Codable, Equatable, Identifiable, Sendable {
    public enum Kind: String, Codable, Sendable { case secret, path, value }
    public let id: String
    public let label: String
    public let kind: Kind
    public var isSecret: Bool { kind != .path }
}

/// Hashes preserve a comparison reference without exporting descriptions,
/// schema defaults or other model-facing text that can contain credentials.
/// A reference imported from a file is *pending*, never a trusted baseline.
public struct PortableContractReference: Codable, Equatable, Sendable {
    public let serverID: String
    public let toolDigests: [String: String]
}

/// Imported fingerprints are review evidence, not local trust. A match exists
/// only after an actual, currently successful check on this Mac.
public struct PortablePendingContractSummary: Codable, Equatable, Sendable {
    public let serverID: String
    public let toolCount: Int
    public let matching: Bool?
}

/// Safe to show in the web layer. No command, argument, URL or secret values.
public struct PortableSetupSummary: Codable, Sendable {
    public let serverNames: [String]
    public let profileNames: [String]
    public let requirements: [PortableRequirement]
    public let pendingContractCount: Int
    public let warnings: [String]
}

public struct PortableImportPreview: Codable, Sendable {
    public let id: String
    public let serverNames: [String]
    public let profileNames: [String]
    public let targetClients: [ClientID]
    public let pathDisplays: [String]
    public let pendingContractCount: Int
    public let warnings: [String]
    public let enabled: Bool
}

public struct PortableImportResult: Codable, Sendable {
    public let serverNames: [String]
    public let importedProfiles: [String]
    public let changedClients: [ClientID]
    public let backupIDs: [String]
    public let pendingContractCount: Int
}

public enum PortableSetupError: Error, LocalizedError, Equatable {
    case invalid(String)
    case missingInput(String)
    case conflict(String)
    case stalePreview
    case rollbackFailed(String)
    case pendingContractsUnreadable
    case localContractNotChecked

    public var errorDescription: String? {
        switch self {
        case .invalid(let detail): "This is not a supported portable setup: \(detail)"
        case .missingInput(let label): "Provide \(label) in the native import dialog."
        case .conflict(let name): "\(name) already exists. The import left it untouched."
        case .stalePreview: "The setup or destination changed after the preview. Preview the import again."
        case .rollbackFailed(let detail): "The import failed and could not completely restore its previous files: \(detail)"
        case .pendingContractsUnreadable: "Kytto could not read its pending imported contract references, so it left them untouched."
        case .localContractNotChecked: "Run a successful local check before reviewing this imported contract reference."
        }
    }
}
