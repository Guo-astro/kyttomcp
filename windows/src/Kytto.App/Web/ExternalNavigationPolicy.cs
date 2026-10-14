using Kytto.App.Ipc;

namespace Kytto.App.Web;

/// <summary>What the shell does with an address the page asked to navigate to.</summary>
internal enum NavigationDecision
{
    /// <summary>It is the bundled app's own origin; the page may go there.</summary>
    Allow,

    /// <summary>One of the two fixed support links: open it in the system browser.</summary>
    OpenInBrowser,

    /// <summary>Anything else. Nothing happens.</summary>
    Block,
}

/// <summary>
/// The one decision about where the web view may go (§3, §7.14).
/// </summary>
/// <remarks>
/// <para>
/// The page carries a privileged bridge, so it may only ever show the bundled
/// app. Every other address is refused — with exactly one exception: the two fixed
/// bug-report links, which are handed to the system browser and never rendered in
/// the app's own web view. That exception is deliberately not "any http or https
/// URL": a page that has been compromised or is merely broken must not be able to
/// open an address of its choosing, so the allow-list is the two literals in
/// <see cref="SupportLinks"/>.
/// </para>
/// <para>
/// A browser only opens when the user did the navigating. A script calling
/// <c>window.open</c> is not a click.
/// </para>
/// </remarks>
internal static class ExternalNavigationPolicy
{
    internal static NavigationDecision Decide(string? address, Uri trustedOrigin, bool userInitiated)
    {
        if (address is null) return NavigationDecision.Block;
        if (CommandRouter.IsSameOrigin(address, trustedOrigin)) return NavigationDecision.Allow;
        return userInitiated && SupportLinks.IsSupportLink(address)
            ? NavigationDecision.OpenInBrowser
            : NavigationDecision.Block;
    }

    /// <summary>A pop-up or <c>target="_blank"</c> link: never shown in-app.</summary>
    /// <remarks>
    /// The app has no browser surface, so even its own origin is not offered a new
    /// window. Only the fixed support links get through, and then to the browser.
    /// </remarks>
    internal static NavigationDecision DecideNewWindow(string? address, Uri trustedOrigin, bool userInitiated) =>
        Decide(address, trustedOrigin, userInitiated) == NavigationDecision.OpenInBrowser
            ? NavigationDecision.OpenInBrowser
            : NavigationDecision.Block;

    /// <summary>A sub-frame: it may load the app's own origin and nothing else.</summary>
    internal static NavigationDecision DecideFrame(string? address, Uri trustedOrigin) =>
        Decide(address, trustedOrigin, userInitiated: false);
}
