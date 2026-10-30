import Foundation

/// What one discovery pass has read and parsed, keyed by file.
///
/// Claude Code keeps its global servers, every project scope and its history in
/// one `~/.claude.json`, which on a long-used machine runs to megabytes. The
/// project list, the global map and each project's map all come from that one
/// file, and reading and parsing it once per source made a pass proportional to
/// the number of projects times the size of the history. Within one pass every
/// source now sees the same bytes, which is also what the digest ledger records
/// for it.
///
/// Lives for one pass only: `Discovery.run()` empties it when it finishes, so a
/// later pass never sees a file as it was.
final class DiscoveryReadCache: @unchecked Sendable {
    struct File {
        let data: Data
        let digest: String
        /// Nil when the bytes are not UTF-8.
        let text: String?
    }

    private enum Parsed<Document> {
        case document(Document, text: String)
        case failure(any Error, text: String)
    }

    private let lock = NSLock()
    private var files: [String: File?] = [:]
    private var json: [String: Parsed<JSONDocument>] = [:]
    private var toml: [String: Parsed<TOMLDocument>] = [:]

    /// How often each file was actually read from disk and parsed. For tests,
    /// which are the only way to prove the work happens once.
    private(set) var diskReads: [String: Int] = [:]
    private(set) var jsonParses: [String: Int] = [:]
    private(set) var tomlParses: [String: Int] = [:]

    private static func key(_ url: URL) -> String { url.standardizedFileURL.path }

    /// The file's bytes as text with their digest, or nil if it does not exist.
    func file(at url: URL) -> File? {
        let key = Self.key(url)
        lock.lock()
        if let cached = files[key] {
            lock.unlock()
            return cached
        }
        lock.unlock()

        let file = (try? Data(contentsOf: url)).map {
            File(data: $0, digest: ConfigWriter.digest(of: $0), text: String(data: $0, encoding: .utf8))
        }

        lock.lock()
        defer { lock.unlock() }
        diskReads[key, default: 0] += 1
        // A concurrent reader of the same file wins; both read the same pass.
        if let cached = files[key] { return cached }
        files[key] = .some(file)
        return file
    }

    /// The raw bytes, for callers that parse with something other than Kytto's
    /// own span-keeping parsers.
    func data(at url: URL) -> Data? {
        file(at: url)?.data
    }

    /// `text` is what `file(at:)` returned for `url`. A different text is
    /// parsed on its own rather than being answered with another file's tree.
    func jsonDocument(at url: URL, text: String) throws -> JSONDocument {
        try parse(url, text, in: \.json, count: \.jsonParses, JSONDocument.parse)
    }

    func tomlDocument(at url: URL, text: String) throws -> TOMLDocument {
        try parse(url, text, in: \.toml, count: \.tomlParses, TOMLDocument.parse)
    }

    func removeAll() {
        lock.lock()
        defer { lock.unlock() }
        files.removeAll()
        json.removeAll()
        toml.removeAll()
    }

    private func parse<Document>(
        _ url: URL,
        _ text: String,
        in store: ReferenceWritableKeyPath<DiscoveryReadCache, [String: Parsed<Document>]>,
        count: ReferenceWritableKeyPath<DiscoveryReadCache, [String: Int]>,
        _ parser: (String) throws -> Document
    ) throws -> Document {
        let key = Self.key(url)
        lock.lock()
        let cached = self[keyPath: store][key]
        lock.unlock()

        switch cached {
        case .document(let document, let parsedText) where parsedText == text:
            return document
        case .failure(let error, let parsedText) where parsedText == text:
            throw error
        default:
            break
        }

        let outcome: Parsed<Document>
        do {
            outcome = .document(try parser(text), text: text)
        } catch {
            outcome = .failure(error, text: text)
        }

        lock.lock()
        self[keyPath: count][key, default: 0] += 1
        self[keyPath: store][key] = outcome
        lock.unlock()

        switch outcome {
        case .document(let document, _): return document
        case .failure(let error, _): throw error
        }
    }
}
