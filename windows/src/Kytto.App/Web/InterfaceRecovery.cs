using Microsoft.Web.WebView2.Core;

namespace Kytto.App.Web;

/// <summary>Why the interface cannot be trusted to be working.</summary>
internal enum InterfaceFailureKind
{
    /// <summary>The bundled page did not load: a navigation error, or an HTTP error status.</summary>
    NavigationFailed,

    /// <summary>The page's renderer process ended. A new navigation starts a new one.</summary>
    RendererExited,

    /// <summary>The renderer stopped responding.</summary>
    RendererUnresponsive,

    /// <summary>
    /// The WebView2 browser process ended. Every object tied to it, the control
    /// included, is unusable from then on.
    /// </summary>
    BrowserProcessExited,

    /// <summary>A fatal failure WebView2 describes as unknown. Treated like a lost browser process.</summary>
    OtherFatal,

    /// <summary>
    /// A helper process failed — GPU, utility, sandbox helper, a plug-in, or a
    /// frame's renderer. WebView2 restarts those by itself and the page keeps
    /// working, so nothing is shown.
    /// </summary>
    Auxiliary,
}

/// <summary>What "Reload interface" has to do for the failure at hand.</summary>
internal enum RecoveryAction
{
    /// <summary>Navigate the existing control to the bundled origin again.</summary>
    ReloadPage,

    /// <summary>Throw the control away and build a new one, with its bridge and delegates.</summary>
    RecreateWebView,
}

/// <summary>How a main-frame navigation ended, as far as recovery cares.</summary>
internal enum NavigationOutcome
{
    Succeeded,

    /// <summary>
    /// Expected, and says nothing about the interface's health: the shell refused a
    /// navigation, or a newer one replaced it.
    /// </summary>
    Cancelled,

    Failed,
}

/// <summary>What the native recovery panel shows.</summary>
/// <param name="Kind">The most serious failure seen since the interface last worked.</param>
/// <param name="Action">What the next "Reload interface" will do.</param>
/// <param name="Reloading">True while a recovery attempt is in flight.</param>
/// <param name="FailedAttempts">How many recovery attempts have already failed.</param>
internal sealed record RecoveryPanelState(
    InterfaceFailureKind Kind,
    RecoveryAction Action,
    bool Reloading,
    int FailedAttempts);

/// <summary>
/// What the recovery logic needs from the window. Everything WPF- and
/// WebView2-specific is behind it, which is what lets the logic be tested.
/// </summary>
internal interface IInterfaceRecoveryHost
{
    /// <summary>
    /// Shows the single native recovery panel, or refreshes it when it is already
    /// up. Never a second panel.
    /// </summary>
    void ShowPanel(RecoveryPanelState state);

    void HidePanel();

    /// <summary>
    /// Navigates the existing control to the bundled app origin — never to the last
    /// URL it happened to hold. May throw when the control is no longer usable.
    /// </summary>
    void ReloadInterface();

    /// <summary>
    /// Disposes the control and builds a new one with its message handlers,
    /// navigation policy, bridge script and router subscription, then starts the
    /// navigation to the bundled origin. May throw. Either method that can throw
    /// logs its own cause first, so the machine can stay free of the log.
    /// </summary>
    Task RecreateInterfaceAsync();
}

/// <summary>
/// The recovery state machine for a web view that failed to load or lost a process
/// (§7.14).
/// </summary>
/// <remarks>
/// <para>
/// It exists so the shell can say "the interface is down" without the interface:
/// the panel is native and independent of the page and of IPC. This class decides
/// when that panel appears and what "Reload interface" does; the window supplies
/// the hands.
/// </para>
/// <para>
/// Three rules carry the design. <b>One panel:</b> repeated callbacks, from a
/// crash that reports twice or a reload that fails again, refresh the panel and
/// never add one. <b>No false alarms:</b> a cancelled navigation — the shell
/// refusing an external link, or a reload replacing a pending load — says nothing
/// about the page, so it never covers a working app. <b>No replay:</b> recovery
/// only ever navigates to the bundled origin; nothing here knows about a command,
/// let alone re-sends one.
/// </para>
/// <para>
/// A failed reload escalates to rebuilding the control, because a renderer that
/// will not come back is more often a symptom of a dead browser process than a
/// bad page.
/// </para>
/// </remarks>
internal sealed class InterfaceRecovery(IInterfaceRecoveryHost host, TimeSpan? loadTimeout = null)
{
    private readonly TimeSpan _loadTimeout = loadTimeout ?? TimeSpan.FromSeconds(15);

    private InterfaceFailureKind? _kind;
    private RecoveryAction _action = RecoveryAction.ReloadPage;
    private int _failedAttempts;
    private bool _recovering;
    private TaskCompletionSource<bool>? _load;

    /// <summary>True while the native panel is (meant to be) up.</summary>
    internal bool PanelVisible => _kind is not null;

    /// <summary>What the next "Reload interface" will do.</summary>
    internal RecoveryAction NextAction => _action;

    internal bool Recovering => _recovering;

    // MARK: - Mapping WebView2 onto the machine

    /// <summary>
    /// Which recovery a <c>ProcessFailed</c> kind calls for.
    /// </summary>
    /// <remarks>
    /// Only the two process kinds that end the page and the browser, plus the one
    /// WebView2 cannot describe, are failures the user must hear about. A GPU,
    /// utility, sandbox-helper or plug-in process is restarted by WebView2 on its
    /// own, and a frame's renderer is not ours — the app has no frames — so none of
    /// those may cover a page that is still working.
    /// </remarks>
    internal static InterfaceFailureKind Classify(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited => InterfaceFailureKind.BrowserProcessExited,
        CoreWebView2ProcessFailedKind.RenderProcessExited => InterfaceFailureKind.RendererExited,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => InterfaceFailureKind.RendererUnresponsive,
        CoreWebView2ProcessFailedKind.UnknownProcessExited => InterfaceFailureKind.OtherFatal,
        _ => InterfaceFailureKind.Auxiliary,
    };

    /// <summary>
    /// How a finished navigation reads. An aborted or cancelled one is expected —
    /// the shell cancels every navigation that is not the bundled app — and an HTTP
    /// error status is a failed load even though WebView2 calls it a success.
    /// </summary>
    internal static NavigationOutcome Outcome(bool isSuccess, bool cancelled, int httpStatus)
    {
        if (cancelled) return NavigationOutcome.Cancelled;
        if (!isSuccess) return NavigationOutcome.Failed;
        return httpStatus >= 400 ? NavigationOutcome.Failed : NavigationOutcome.Succeeded;
    }

    /// <summary>What a failure of each kind needs.</summary>
    internal static RecoveryAction ActionFor(InterfaceFailureKind kind) => kind switch
    {
        InterfaceFailureKind.BrowserProcessExited or InterfaceFailureKind.OtherFatal =>
            RecoveryAction.RecreateWebView,
        _ => RecoveryAction.ReloadPage,
    };

    private static int Severity(InterfaceFailureKind kind) => kind switch
    {
        InterfaceFailureKind.NavigationFailed => 1,
        InterfaceFailureKind.RendererExited => 2,
        InterfaceFailureKind.RendererUnresponsive => 2,
        InterfaceFailureKind.OtherFatal => 3,
        InterfaceFailureKind.BrowserProcessExited => 4,
        _ => 0,
    };

    // MARK: - Events from the web view

    /// <summary>A <c>ProcessFailed</c> callback. Safe to call repeatedly.</summary>
    internal void ReportProcessFailure(InterfaceFailureKind kind)
    {
        if (kind == InterfaceFailureKind.Auxiliary) return;

        // A failure while a recovery attempt is waiting for its page is that
        // attempt failing, not a second problem.
        _load?.TrySetResult(false);
        Raise(kind);
    }

    /// <summary>A finished main-frame navigation.</summary>
    internal void ReportNavigation(NavigationOutcome outcome)
    {
        switch (outcome)
        {
            case NavigationOutcome.Cancelled:
                // Never a failure, not even mid-recovery: reloading replaces the
                // navigation that was pending, and that one reports "cancelled".
                return;

            case NavigationOutcome.Succeeded:
                // Only a recovery attempt waits for it. A page that loaded while
                // nothing was wrong is nothing to report, and one that loaded while
                // the panel is up without us asking is not trusted to stay up.
                _load?.TrySetResult(true);
                return;

            case NavigationOutcome.Failed:
                if (_load is not null) _load.TrySetResult(false);
                else Raise(InterfaceFailureKind.NavigationFailed);
                return;
        }
    }

    private void Raise(InterfaceFailureKind kind)
    {
        var action = ActionFor(kind);
        if (_kind is not { } current)
        {
            _kind = kind;
            _action = action;
            _failedAttempts = 0;
            host.ShowPanel(State());
            return;
        }

        // Already showing. Only a worse failure changes anything, and even then it
        // refreshes the one panel rather than adding another.
        var kindChanged = Severity(kind) > Severity(current);
        var actionChanged = action == RecoveryAction.RecreateWebView && _action != RecoveryAction.RecreateWebView;
        if (kindChanged) _kind = kind;
        if (actionChanged) _action = action;
        if (kindChanged || actionChanged) host.ShowPanel(State());
    }

    // MARK: - "Reload interface"

    /// <summary>
    /// Runs one recovery attempt: reload or rebuild, wait for the bundled page to
    /// load, then hide the panel. A second request while one is running is ignored.
    /// </summary>
    internal async Task RecoverAsync()
    {
        if (_kind is null || _recovering) return;
        _recovering = true;

        var load = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _load = load;
        host.ShowPanel(State());

        var loaded = false;
        try
        {
            if (_action == RecoveryAction.RecreateWebView) await host.RecreateInterfaceAsync();
            else host.ReloadInterface();

            using var timeout = new CancellationTokenSource();
            var finished = await Task.WhenAny(load.Task, Task.Delay(_loadTimeout, timeout.Token));
            loaded = finished == load.Task && load.Task.Result;
            timeout.Cancel();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A control that cannot be navigated or rebuilt is the failure this
            // exists to survive; the panel stays, with the next attempt escalated.
            // The host has already logged what it knows about the cause.
        }
        finally
        {
            _load = null;
            _recovering = false;
        }

        if (loaded)
        {
            _kind = null;
            _action = RecoveryAction.ReloadPage;
            _failedAttempts = 0;
            host.HidePanel();
            return;
        }

        // Reloading did not bring the page back; a new control is the next thing
        // worth trying. A failed rebuild stays a rebuild.
        _failedAttempts++;
        _action = RecoveryAction.RecreateWebView;
        host.ShowPanel(State());
    }

    private RecoveryPanelState State() =>
        new(_kind ?? InterfaceFailureKind.NavigationFailed, _action, _recovering, _failedAttempts);
}
