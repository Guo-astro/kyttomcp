import Foundation
import Testing
@testable import KyttoCore

/// What the watcher's re-read relies on to stay quiet when Claude Code rewrites
/// its file without touching any server, while never missing an edit that did.
@Suite("Discovery — re-reads that change nothing")
struct DiscoveryNoOpTests {
    private static func discovery(_ home: FakeHome, ledger: DigestLedger) -> Discovery {
        Discovery(home: home.root, locator: StubAppLocator(executables: ["claude"]), ledger: ledger)
    }

    @Test func identicalBytesGiveAnIdenticalResult() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.claudeCode.text, to: ".claude.json")
        try home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")

        let ledger = DigestLedger()
        let first = Self.discovery(home, ledger: ledger).run()
        let revision = ledger.revision
        let second = Self.discovery(home, ledger: ledger).run()

        #expect(first == second)
        #expect(ledger.revision == revision)
    }

    @Test func bookkeepingOutsideTheServersChangesNothingButTheDigest() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.claudeCode.text, to: ".claude.json")
        let url = home.root.appending(path: ".claude.json")

        let ledger = DigestLedger()
        let before = Self.discovery(home, ledger: ledger).run()
        let oldDigest = ledger.digest(for: url)

        // What Claude Code does after a message.
        try home.write(
            Fixture.claudeCode.text.replacingOccurrences(of: #""numStartups": 560"#, with: #""numStartups": 561"#),
            to: ".claude.json"
        )
        let after = Self.discovery(home, ledger: ledger).run()

        #expect(before == after)
        // The guard has to see the new bytes, or the next toggle is refused.
        #expect(ledger.digest(for: url) != oldDigest)
        #expect(ledger.digest(for: url) == ConfigWriter.digest(of: url))
    }

    @Test func anEditToAServerIsAChange() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.claudeCode.text, to: ".claude.json")

        let ledger = DigestLedger()
        let before = Self.discovery(home, ledger: ledger).run()
        try home.write(
            Fixture.claudeCode.text.replacingOccurrences(of: "xcodebuildmcp@latest", with: "xcodebuildmcp@2.0.0"),
            to: ".claude.json"
        )
        let after = Self.discovery(home, ledger: ledger).run()

        #expect(before != after)
    }

    @Test func aHealthResultIsAChange() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.claudeCode.text, to: ".claude.json")

        let ledger = DigestLedger()
        let before = Self.discovery(home, ledger: ledger).run()
        let health = HealthResult(
            status: .passed, checkedAt: Date(timeIntervalSince1970: 1_000), toolCount: 0, tools: [],
            message: nil, stderr: nil, durationSeconds: 1, serverName: nil, serverVersion: nil
        )
        let measured = Discovery(
            home: home.root, locator: StubAppLocator(executables: ["claude"]), ledger: ledger,
            metadata: ["context7": ServerMetadata(health: health)]
        ).run()

        #expect(before != measured)
    }
}

@Suite("DigestLedger — background passes")
struct DigestLedgerAdoptionTests {
    private let a = URL(filePath: "/Users/example/a.json")
    private let b = URL(filePath: "/Users/example/b.json")

    @Test func revisionMovesOnlyWhenABeliefChanges() {
        let ledger = DigestLedger()
        let start = ledger.revision
        ledger.record("one", for: a)
        #expect(ledger.revision == start + 1)
        ledger.record("one", for: a)
        #expect(ledger.revision == start + 1)
        ledger.record(nil, for: b)
        #expect(ledger.revision == start + 1)
        ledger.record(nil, for: a)
        #expect(ledger.revision == start + 2)
    }

    @Test func adoptionCarriesDigestsAndAbsences() {
        let ledger = DigestLedger()
        ledger.record("old-a", for: a)
        ledger.record("old-b", for: b)

        let pass = DigestLedger()
        pass.record("new-a", for: a)
        pass.record(nil, for: b)

        ledger.adopt(pass)
        #expect(ledger.digest(for: a) == "new-a")
        #expect(ledger.digest(for: b) == nil)
        // The adopted ledger is not changed by being adopted.
        #expect(pass.digest(for: a) == "new-a")
    }

    @Test func adoptingWhatIsAlreadyKnownIsNotAChange() {
        let ledger = DigestLedger()
        ledger.record("same", for: a)
        let pass = DigestLedger()
        pass.record("same", for: a)
        pass.record(nil, for: b)

        let revision = ledger.revision
        ledger.adopt(pass)
        #expect(ledger.revision == revision)
    }
}

@MainActor
@Suite("ReloadScheduler — coalescing")
struct ReloadSchedulerTests {
    /// A pass that waits until the test lets it finish.
    @MainActor
    private final class Gate {
        var started = 0
        var finished = 0
        private var waiting: [CheckedContinuation<Void, Never>] = []

        func pass() async -> Bool {
            started += 1
            await withCheckedContinuation { waiting.append($0) }
            finished += 1
            return true
        }

        func release() {
            let pending = waiting
            waiting.removeAll()
            pending.forEach { $0.resume() }
        }

        func waitForStart(_ count: Int) async {
            while started < count { await Task.yield() }
        }
    }

    @Test func requestsDuringAPassShareExactlyOneMore() async {
        let gate = Gate()
        let scheduler = ReloadScheduler { await gate.pass() }

        async let first = scheduler.request()
        await gate.waitForStart(1)

        // Three watcher events while the first pass is still reading.
        async let second = scheduler.request()
        async let third = scheduler.request()
        async let fourth = scheduler.request()
        for _ in 0..<20 { await Task.yield() }
        #expect(gate.started == 1)

        gate.release()
        await gate.waitForStart(2)
        gate.release()

        let outcomes = await [first, second, third, fourth]
        #expect(gate.started == 2)
        #expect(outcomes[0].pass == 1)
        #expect(outcomes[1...].allSatisfy { $0.pass == 2 })
        #expect(!scheduler.isRunning)
    }

    @Test func aRequestAfterAPassEndsStartsANewOne() async {
        let gate = Gate()
        let scheduler = ReloadScheduler { await gate.pass() }

        async let first = scheduler.request()
        await gate.waitForStart(1)
        gate.release()
        let one = await first

        async let second = scheduler.request()
        await gate.waitForStart(2)
        gate.release()
        let two = await second

        #expect(one.pass == 1 && two.pass == 2)
        #expect(gate.finished == 2)
    }

    @Test func theOutcomeReportsWhetherThePassChangedAnything() async {
        var answers = [false, true]
        let scheduler = ReloadScheduler { answers.removeFirst() }
        #expect(await scheduler.request() == .init(pass: 1, changed: false))
        #expect(await scheduler.request() == .init(pass: 2, changed: true))
    }
}
