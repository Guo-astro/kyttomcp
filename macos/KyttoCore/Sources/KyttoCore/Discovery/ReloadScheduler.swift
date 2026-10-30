import Foundation

/// Runs re-reads of the clients' configs one at a time and folds together the
/// requests that arrive while one is running (§6.4).
///
/// Claude Code rewrites `~/.claude.json` after nearly every message, so the
/// watcher asks for a re-read far more often than anything changes. A request
/// made while a pass is running is answered by exactly one further pass that
/// starts after the running one ends, however many requests arrive in that
/// time — never by the pass that was already reading, which may have read the
/// file before the change being reported.
@MainActor
public final class ReloadScheduler {
    public struct Outcome: Equatable, Sendable {
        /// Increases with every pass, so a caller can tell whether it has
        /// already acted on the pass it waited for.
        public let pass: Int
        /// What the pass reported: whether anything worth showing changed.
        public let changed: Bool

        public init(pass: Int, changed: Bool) {
            self.pass = pass
            self.changed = changed
        }
    }

    private let perform: @MainActor () async -> Bool
    private var running: Task<Outcome, Never>?
    private var queued: Task<Outcome, Never>?
    private var passes = 0

    /// - Parameter perform: one pass. Returns whether it changed anything.
    public init(perform: @escaping @MainActor () async -> Bool) {
        self.perform = perform
    }

    /// Whether a pass is running now.
    public var isRunning: Bool { running != nil }

    /// Waits for a pass that started no earlier than this call.
    @discardableResult
    public func request() async -> Outcome {
        // Not started yet, so it will read whatever prompted this request.
        if let queued { return await queued.value }
        guard let running else { return await start().value }

        let next = Task { @MainActor [weak self] () -> Outcome in
            _ = await running.value
            guard let self else { return Outcome(pass: 0, changed: false) }
            self.queued = nil
            return await self.start().value
        }
        queued = next
        return await next.value
    }

    private func start() -> Task<Outcome, Never> {
        passes += 1
        let pass = passes
        let task = Task { @MainActor [weak self, perform] () -> Outcome in
            let changed = await perform()
            // Cleared before the task completes, so the queued pass waiting on
            // it always finds the lane free.
            if let self, self.passes == pass { self.running = nil }
            return Outcome(pass: pass, changed: changed)
        }
        running = task
        return task
    }
}
