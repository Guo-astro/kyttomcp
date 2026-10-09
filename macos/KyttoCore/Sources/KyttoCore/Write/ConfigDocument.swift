import Foundation

/// What the write pipeline needs to know about a config file, whatever it is
/// spelled in.
///
/// Deliberately tiny. The interesting operations — set this key, remove that
/// table — differ enough between JSON and TOML that pretending they are the same
/// call would produce a worst-of-both abstraction. What genuinely is the same is
/// the §6 pipeline around them: read, check nobody else moved it, transform,
/// check the result still parses, write atomically. That is all this covers.
public protocol ConfigDocument: Sendable {
    /// What a client that has never configured MCP starts from.
    static var emptySource: String { get }
    /// For the message when an edit produces something unparseable.
    static var formatName: String { get }

    static func parse(_ text: String) throws -> Self

    var sourceText: String { get }
}

extension JSONDocument: ConfigDocument {
    /// An object, because every JSON client's config is one and a bare `{}` is a
    /// valid starting point for splicing a servers key into.
    public static var emptySource: String { "{}\n" }
    public static var formatName: String { "JSON" }
}

extension TOMLDocument: ConfigDocument {
    /// Nothing. An empty TOML file is a valid TOML file holding no tables, and
    /// unlike JSON there is no punctuation to seed.
    public static var emptySource: String { "" }
    public static var formatName: String { "TOML" }
}

// MARK: - Finding a server by identity

extension JSONDocument {
    /// The key a server goes by in this file's server map, or nil if it is not
    /// there.
    ///
    /// The matrix merges rows by identity, so a server called `GitHub` in one
    /// client and `github` in another is one row — and every edit to the second
    /// has to use the spelling that file actually contains, or it silently
    /// changes nothing (or adds a twin). Two keys answering to the same identity
    /// is refused rather than resolved: clients read the last, and guessing is
    /// not a splice (§6.3).
    func serverKey(for identity: String, under serversKey: String) throws -> String? {
        let matches = (value(at: [serversKey])?.keys ?? []).filter { Server.identity(for: $0) == identity }
        guard matches.count <= 1 else { throw JSONEditError.duplicateKey([serversKey, matches[0]]) }
        return matches.first
    }
}

extension TOMLDocument {
    /// The name a server goes by in this file — see `JSONDocument.serverKey`.
    /// Two tables answering to one identity are refused, as in JSON.
    func serverName(for identity: String, under serversKey: String) throws -> String? {
        let matches = serverNames(under: serversKey).filter { Server.identity(for: $0) == identity }
        guard matches.count <= 1 else { throw TOMLEditError.ambiguousValue([serversKey, matches[0]]) }
        return matches.first
    }
}
