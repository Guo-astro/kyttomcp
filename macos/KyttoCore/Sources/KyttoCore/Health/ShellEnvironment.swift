import Foundation

/// The PATH the user's terminal has, which is not the one this app was given.
///
/// An app launched from Finder inherits a minimal environment: no nvm, no
/// Homebrew, no `~/.local/bin`. Almost every MCP server is `npx …` or `uvx …`,
/// so without this, health checks fail on nearly every server with "command not
/// found" — and the user, whose terminal runs the same command fine, concludes
/// the app is broken.
///
/// Asking the login shell is the only reliable answer: PATH is assembled by
/// whatever the user put in `.zshrc`, `.zprofile`, or a version manager's hook,
/// and none of that is readable any other way.
public struct ShellEnvironment: Sendable {
    /// How long to wait for the shell. A misbehaving rc file can hang forever,
    /// and a health check that never starts is worse than one that runs with a
    /// merely-good PATH.
    private static let probeTimeout: TimeInterval = 5

    /// Locations worth trying when the shell cannot be asked at all.
    private static let fallbackPath = [
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/usr/sbin",
        "/sbin",
    ]

    public let path: String
    /// True when the login shell answered. False means `path` is the fallback,
    /// which the UI says out loud when a command is not found.
    public let resolvedFromLoginShell: Bool

    private init(path: String, resolvedFromLoginShell: Bool) {
        self.path = path
        self.resolvedFromLoginShell = resolvedFromLoginShell
    }

    /// Resolved once per launch. The user's PATH does not change under us often
    /// enough to justify paying for a shell start on every health check.
    public static let current: ShellEnvironment = resolve()

    static func resolve(
        shellPath: String? = ProcessInfo.processInfo.environment["SHELL"],
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> ShellEnvironment {
        guard let shell = shellPath, FileManager.default.isExecutableFile(atPath: shell) else {
            return ShellEnvironment(path: fallback(home: home), resolvedFromLoginShell: false)
        }

        // `-i` matters: most people set PATH in `.zshrc`, which a non-interactive
        // shell never reads.
        guard let output = runCapturing(shell, ["-ilc", "command printf '%s\\n\(endMarker)\\n' \"$PATH\""]),
              !output.isEmpty
        else {
            return ShellEnvironment(path: fallback(home: home), resolvedFromLoginShell: false)
        }

        // An rc file that prints a banner would otherwise end up inside PATH.
        let candidate = output
            .split(separator: "\n")
            .last { $0.contains("/") }
            .map(String.init) ?? output

        let entries = candidate
            .split(separator: ":")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && $0.hasPrefix("/") }

        guard !entries.isEmpty else {
            return ShellEnvironment(path: fallback(home: home), resolvedFromLoginShell: false)
        }
        return ShellEnvironment(path: entries.joined(separator: ":"), resolvedFromLoginShell: true)
    }

    private static func fallback(home: URL) -> String {
        ([home.appending(path: ".local/bin").path] + fallbackPath).joined(separator: ":")
    }

    /// Printed after PATH, so the answer is complete when it appears. Waiting
    /// for end-of-file instead would wait on any daemon the rc files started
    /// (gitstatusd, an update check sent to the background), which inherits
    /// the pipe and keeps it open long after the shell itself is done.
    private static let endMarker = "__KYTTO_PATH_END__"

    private static func runCapturing(_ executable: String, _ arguments: [String]) -> String? {
        let process = Process()
        process.executableURL = URL(filePath: executable)
        process.arguments = arguments
        // A pristine environment, so a PATH we inherited cannot be echoed back
        // at us and mistaken for the shell's own answer.
        process.environment = ["HOME": FileManager.default.homeDirectoryForCurrentUser.path]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        // Never the caller's stdin: inside the gateway helper that is the MCP
        // client's pipe, and an rc file that reads input would eat its messages.
        process.standardInput = FileHandle.nullDevice

        do {
            try process.run()
        } catch {
            return nil
        }

        let output = CapturedOutput()
        let finished = DispatchSemaphore(value: 0)
        let handle = pipe.fileHandleForReading
        let marker = Data(endMarker.utf8)
        // Read on a background thread so a shell that never exits cannot pin us.
        Thread.detachNewThread {
            while true {
                let chunk = handle.availableData
                if output.append(chunk, endsWith: marker) || chunk.isEmpty { break }
            }
            finished.signal()
        }

        _ = finished.wait(timeout: .now() + probeTimeout)
        if process.isRunning { process.terminate() }

        let captured = output.snapshot
        guard let text = String(data: captured, encoding: .utf8),
              let range = text.range(of: endMarker) else { return nil }
        return String(text[..<range.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The environment an MCP server should be launched with: this process's own,
    /// with PATH replaced and the server's own variables layered on top.
    public func environment(adding extra: [String: String] = [:]) -> [String: String] {
        var result = ProcessInfo.processInfo.environment
        result["PATH"] = path
        for (key, value) in extra { result[key] = value }
        return result
    }
}

/// Bytes a reader thread appends while the caller may already be reading them.
private final class CapturedOutput: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()

    /// Appends, and says whether `marker` has now arrived.
    func append(_ chunk: Data, endsWith marker: Data) -> Bool {
        lock.withLock {
            data.append(chunk)
            return data.range(of: marker) != nil
        }
    }

    var snapshot: Data { lock.withLock { data } }
}
