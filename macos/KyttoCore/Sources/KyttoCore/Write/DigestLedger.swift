import Foundation

/// What Kytto believes is currently on disk, per file.
///
/// Discovery records a digest for every file it reads; every write updates it.
/// Before writing, `ConfigWriter` checks the file still matches — if it does not,
/// someone edited it behind Kytto's back and the change is refused rather than
/// merged (§6.4).
///
/// Deliberately in memory only. Persisting it would mean a stale record from a
/// previous launch could wave through a write over changes made while Kytto was
/// closed. Starting each session by reading the truth is both simpler and safer.
public final class DigestLedger: @unchecked Sendable {
    private var digests: [String: String] = [:]
    /// Paths last recorded as absent, so `adopt` can carry an absence across as
    /// faithfully as a digest. Bounded by the files discovery looks for.
    private var absent: Set<String> = []
    private var changes = 0
    private let lock = NSLock()

    public init() {}

    public func record(_ digest: String?, for url: URL) {
        lock.lock()
        defer { lock.unlock() }
        let path = url.standardizedFileURL.path
        if let digest {
            if digests.updateValue(digest, forKey: path) != digest { changes += 1 }
            absent.remove(path)
        } else {
            if digests.removeValue(forKey: path) != nil { changes += 1 }
            absent.insert(path)
        }
    }

    /// Increases whenever what this ledger believes about any file changes.
    ///
    /// A discovery pass that ran off the main actor compares this before and
    /// after: a write that landed in between recorded a newer digest, and the
    /// pass's older reading must not replace it.
    public var revision: Int {
        lock.lock()
        defer { lock.unlock() }
        return changes
    }

    /// Takes over everything another ledger recorded, absences included.
    ///
    /// Used to apply a background discovery pass all at once, together with
    /// the state it read, so the digest guard never knows about a file version
    /// the window has not been shown (§6.4).
    public func adopt(_ other: DigestLedger) {
        guard other !== self else { return }
        other.lock.lock()
        let recorded = other.digests
        let missing = other.absent
        other.lock.unlock()

        lock.lock()
        defer { lock.unlock() }
        for (path, digest) in recorded where digests.updateValue(digest, forKey: path) != digest {
            changes += 1
        }
        for path in missing where digests.removeValue(forKey: path) != nil {
            changes += 1
        }
        absent.formUnion(missing)
        absent.subtract(recorded.keys)
    }

    public func digest(for url: URL) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return digests[url.standardizedFileURL.path]
    }

    /// True when the file on disk is not what Kytto last saw.
    public func hasDrifted(_ url: URL) -> Bool {
        ConfigWriter.digest(of: url) != digest(for: url)
    }

    public func forgetAll() {
        lock.lock()
        defer { lock.unlock() }
        if !digests.isEmpty { changes += 1 }
        digests.removeAll()
        absent.removeAll()
    }
}
