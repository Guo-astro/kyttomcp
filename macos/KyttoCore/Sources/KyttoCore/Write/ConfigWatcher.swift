import Foundation

/// Notices when a client's configuration changes underneath Kytto (§6.4).
///
/// Watches the *directories*, and the config files in them. Every safe writer —
/// Kytto included — replaces a config by renaming a new file over the old one,
/// which shows up on the directory and leaves any watch on the original file
/// pointing at an inode nobody can reach. But not every writer is safe: an
/// editor that truncates and rewrites in place changes no directory entry at
/// all, and without a watch on the file itself the matrix would go stale and
/// the next toggle would fail with "changed on disk". So both are watched, and
/// file watches are re-armed whenever one fires, because the inode it was on
/// may be gone.
///
/// On any event Kytto re-reads and compares digests, so it learns what actually
/// changed rather than trusting the event; a watch that fires for nothing costs
/// one debounced re-read.
///
/// All state lives on one serial queue. `watch` is called from the main actor
/// after every reload while events arrive on the queue, and re-arming must
/// neither race the event handlers nor swallow an event that was still waiting
/// out its debounce — the one an external edit made while discovery was running.
public final class ConfigWatcher: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.kytto.config-watcher")
    private let queueKey = DispatchSpecificKey<Void>()
    private let onChange: @Sendable () -> Void
    private let debounce: DispatchTimeInterval

    /// The file system has to hold one descriptor per watch. Kytto's own configs
    /// are a handful; this only bounds a directory with an unusual number of
    /// JSON files in it.
    static let maximumFileWatches = 128
    /// What a config file is called. Watching every file in the home folder
    /// would be waking up for shell history.
    static let configExtensions: Set<String> = ["json", "jsonc", "toml"]

    // Owned by `queue`.
    private var requestedDirectories: [URL] = []
    private var requestedFiles: [URL] = []
    private var directorySources: [String: (source: DispatchSourceFileSystemObject, identity: FileIdentity)] = [:]
    private var fileSources: [String: (source: DispatchSourceFileSystemObject, identity: FileIdentity)] = [:]
    private var pending: DispatchWorkItem?

    private struct FileIdentity: Equatable {
        let device: dev_t
        let inode: ino_t
    }

    /// - Parameter debounce: editors and client apps often touch a file several
    ///   times in quick succession. Coalescing avoids re-reading everything four
    ///   times for one save.
    public init(debounce: DispatchTimeInterval = .milliseconds(250), onChange: @escaping @Sendable () -> Void) {
        self.debounce = debounce
        self.onChange = onChange
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        for entry in directorySources.values { entry.source.cancel() }
        for entry in fileSources.values { entry.source.cancel() }
        pending?.cancel()
    }

    /// Starts watching, or re-aims a watch already running.
    ///
    /// When a requested directory does not exist yet, its nearest existing
    /// ancestor is watched; creating the missing directory then triggers
    /// discovery, which re-arms this watcher on the exact new path. Watches that
    /// are still wanted are kept rather than recreated, and an event waiting out
    /// its debounce is still delivered.
    ///
    /// - Parameter files: config files to watch for in-place edits, in addition
    ///   to the config-looking files found in `directories`.
    public func watch(directories urls: [URL], files: [URL] = []) {
        onQueue {
            requestedDirectories = urls
            requestedFiles = files
            arm()
        }
    }

    public func stop() {
        onQueue {
            pending?.cancel()
            pending = nil
            requestedDirectories = []
            requestedFiles = []
            for entry in directorySources.values { entry.source.cancel() }
            for entry in fileSources.values { entry.source.cancel() }
            directorySources.removeAll()
            fileSources.removeAll()
        }
    }

    private func onQueue(_ work: () -> Void) {
        if DispatchQueue.getSpecific(key: queueKey) != nil {
            work()
        } else {
            queue.sync(execute: work)
        }
    }

    // MARK: - Arming (on `queue`)

    private func arm() {
        dispatchPrecondition(condition: .onQueue(queue))

        let directories = Set(requestedDirectories.compactMap(Self.nearestExistingDirectory).map(\.path))
        for (path, entry) in directorySources
        where !directories.contains(path) || Self.identity(of: path) != entry.identity {
            // A directory deleted and created again is a new inode; the old
            // watch would never fire again.
            entry.source.cancel()
            directorySources.removeValue(forKey: path)
        }
        for path in directories.sorted() where directorySources[path] == nil {
            if let source = makeSource(path: path, events: [.write, .rename, .delete]) {
                directorySources[path] = source
            }
        }

        let files = wantedFiles()
        for (path, entry) in fileSources where !files.contains(path) || Self.identity(of: path) != entry.identity {
            // Gone, no longer wanted, or replaced by a rename: the watch is on
            // an inode the path no longer names.
            entry.source.cancel()
            fileSources.removeValue(forKey: path)
        }
        for path in files.sorted() where fileSources[path] == nil {
            if let source = makeSource(path: path, events: [.write, .extend, .attrib, .delete, .rename, .revoke]) {
                fileSources[path] = source
            }
        }
    }

    /// Requested files that exist, followed through a symbolic link to the file
    /// that actually changes, plus the config-looking files in each requested
    /// directory.
    private func wantedFiles() -> Set<String> {
        var result: Set<String> = []
        for url in requestedFiles {
            let path = url.resolvingSymlinksInPath().path
            if Self.isRegularFile(path) { result.insert(path) }
        }
        for directory in requestedDirectories {
            let directory = directory.standardizedFileURL
            guard let names = try? FileManager.default.contentsOfDirectory(atPath: directory.path) else { continue }
            for name in names.sorted() {
                guard Self.configExtensions.contains((name as NSString).pathExtension.lowercased()) else { continue }
                let path = directory.appending(path: name).resolvingSymlinksInPath().path
                if Self.isRegularFile(path) { result.insert(path) }
            }
        }
        guard result.count > Self.maximumFileWatches else { return result }
        // Explicitly requested files first, then the rest in a stable order.
        let explicit = requestedFiles.map { $0.resolvingSymlinksInPath().path }.filter(result.contains)
        let rest = result.subtracting(explicit).sorted()
        return Set((explicit + rest).prefix(Self.maximumFileWatches))
    }

    private func makeSource(
        path: String,
        events: DispatchSource.FileSystemEvent
    ) -> (source: DispatchSourceFileSystemObject, identity: FileIdentity)? {
        let descriptor = open(path, O_EVTONLY)
        guard descriptor >= 0 else { return nil }
        var info = stat()
        guard fstat(descriptor, &info) == 0 else {
            close(descriptor)
            return nil
        }

        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            eventMask: events,
            queue: queue
        )
        source.setEventHandler { [weak self] in self?.scheduleNotification() }
        source.setCancelHandler { close(descriptor) }
        source.resume()
        return (source, FileIdentity(device: info.st_dev, inode: info.st_ino))
    }

    private func scheduleNotification() {
        dispatchPrecondition(condition: .onQueue(queue))
        pending?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.pending = nil
            // Before reporting, so an edit landing while the app re-reads is
            // caught by a watch on the file as it now is.
            self.arm()
            self.onChange()
        }
        pending = work
        queue.asyncAfter(deadline: .now() + debounce, execute: work)
    }

    // MARK: - Paths

    private static func nearestExistingDirectory(_ requested: URL) -> URL? {
        var candidate = requested.standardizedFileURL
        var isDirectory: ObjCBool = false
        while !FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory) || !isDirectory.boolValue {
            let parent = candidate.deletingLastPathComponent()
            guard parent != candidate else { return nil }
            candidate = parent
        }
        return candidate
    }

    private static func isRegularFile(_ path: String) -> Bool {
        var info = stat()
        return stat(path, &info) == 0 && (info.st_mode & S_IFMT) == S_IFREG
    }

    private static func identity(of path: String) -> FileIdentity? {
        var info = stat()
        guard stat(path, &info) == 0 else { return nil }
        return FileIdentity(device: info.st_dev, inode: info.st_ino)
    }
}
