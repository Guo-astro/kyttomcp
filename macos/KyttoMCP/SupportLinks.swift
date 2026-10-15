import Foundation

/// Fixed support destinations (§7.14). Opening a report never attaches app data.
enum SupportLinks {
    static let bugReport = URL(string: "https://kytto.jakubhecht.sk/#report-bug")!
    static let githubIssue = URL(string: "https://github.com/heyitsjakub/KyttoMCP/issues/new/choose")!
}
