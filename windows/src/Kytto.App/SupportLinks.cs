using System.ComponentModel;
using System.Diagnostics;

namespace Kytto.App;

/// <summary>One fixed support destination and the words that describe it.</summary>
/// <param name="Label">What the link is called wherever it appears.</param>
/// <param name="Audience">Who can see the report and whether it needs an account.</param>
/// <param name="Url">Always one of the two fixed addresses; nothing is ever appended.</param>
internal sealed record SupportLink(string Label, string Audience, Uri Url);

/// <summary>
/// The two places a bug can be reported, and the only two addresses the app ever
/// opens for that purpose (§7.14).
/// </summary>
/// <remarks>
/// <para>
/// The addresses are constants with no query and no fragment but the form's own
/// anchor. Nothing about the failure — its text, a path, a log line, a
/// configuration value, the version, the machine — is ever added to them, and
/// nothing is attached or submitted by the app: the reporter writes the report in
/// their own browser. Both web (<c>web/js/support.js</c>) and native surfaces
/// spell these the same way; a test pins that they agree.
/// </para>
/// <para>
/// The same list is what the page's external-navigation policy accepts
/// (<see cref="Web.ExternalNavigationPolicy"/>), so a link the page renders can
/// open exactly these and nothing broader.
/// </para>
/// </remarks>
internal static class SupportLinks
{
    /// <summary>The private form on the project's site: no account needed.</summary>
    internal const string BugReportAddress = "https://kytto.jakubhecht.sk/#report-bug";

    /// <summary>The public issue chooser: visible to everyone, needs a GitHub account.</summary>
    internal const string GitHubIssueAddress = "https://github.com/heyitsjakub/KyttoMCP/issues/new/choose";

    internal static readonly SupportLink BugReport =
        new("Report a bug…", "Private · No account", new Uri(BugReportAddress));

    internal static readonly SupportLink GitHubIssue =
        new("Report on GitHub…", "Public · GitHub account", new Uri(GitHubIssueAddress));

    internal static IReadOnlyList<SupportLink> All { get; } = [BugReport, GitHubIssue];

    /// <summary>The sentence every native surface puts beside the links.</summary>
    internal const string AttachmentNote =
        "Opens in your browser. No logs or configuration are attached automatically.";

    /// <summary>
    /// True only for exactly one of the fixed addresses — same scheme, host, path,
    /// query and fragment. A different fragment, a query string or user info makes
    /// it something else.
    /// </summary>
    internal static bool IsSupportLink(Uri? candidate) =>
        candidate is { IsAbsoluteUri: true } &&
        All.Any(link => string.Equals(
            candidate.AbsoluteUri, link.Url.AbsoluteUri, StringComparison.Ordinal));

    internal static bool IsSupportLink(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && IsSupportLink(uri);

    /// <summary>What a surface says when the default browser could not be started.</summary>
    /// <remarks>
    /// Spelled with the fixed address so the reporter can still get there by hand.
    /// </remarks>
    internal static string FallbackMessage(SupportLink link) =>
        $"Kytto could not open your browser. Open {link.Url.AbsoluteUri} yourself to report this.";
}

/// <summary>Hands a fixed support address to the user's default browser.</summary>
internal interface ISupportLinkLauncher
{
    /// <returns>False when the address is not a support link or the browser did not start.</returns>
    bool Open(Uri url);
}

/// <summary>The system browser, never a WebView with a privileged bridge.</summary>
internal sealed class SystemBrowserSupportLauncher : ISupportLinkLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    internal static SystemBrowserSupportLauncher Shared { get; } = new();

    internal SystemBrowserSupportLauncher(Action<ProcessStartInfo>? start = null) =>
        _start = start ?? (info => { Process.Start(info); });

    public bool Open(Uri url)
    {
        // Re-checked here as well as at every call site: whatever asks, nothing but
        // a fixed address is ever handed to the shell.
        if (!SupportLinks.IsSupportLink(url)) return false;

        try
        {
            _start(new ProcessStartInfo
            {
                FileName = url.AbsoluteUri,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception error) when (
            error is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
