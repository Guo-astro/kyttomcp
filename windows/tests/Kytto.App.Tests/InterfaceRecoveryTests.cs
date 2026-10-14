using Kytto.App.Web;
using Microsoft.Web.WebView2.Core;

namespace Kytto.App.Tests;

/// <summary>
/// When the native recovery panel appears, what "Reload interface" does for each
/// kind of failure, and what must never cover a working app (§7.14).
/// </summary>
public sealed class InterfaceRecoveryTests
{
    /// <summary>A window that records what recovery asked of it.</summary>
    private sealed class FakeHost : IInterfaceRecoveryHost
    {
        public List<RecoveryPanelState> Shown { get; } = [];
        public int Hidden { get; private set; }
        public int Reloads { get; private set; }
        public int Rebuilds { get; private set; }

        /// <summary>What the page does when a navigation starts: report how it ended.</summary>
        public Action<InterfaceRecovery>? AfterReload { get; set; }

        public Action<InterfaceRecovery>? AfterRebuild { get; set; }
        public Exception? ReloadFailure { get; set; }
        public Exception? RebuildFailure { get; set; }
        public InterfaceRecovery? Recovery { get; set; }

        public void ShowPanel(RecoveryPanelState state) => Shown.Add(state);

        public void HidePanel() => Hidden++;

        public void ReloadInterface()
        {
            Reloads++;
            if (ReloadFailure is not null) throw ReloadFailure;
            AfterReload?.Invoke(Recovery!);
        }

        public Task RecreateInterfaceAsync()
        {
            Rebuilds++;
            if (RebuildFailure is not null) throw RebuildFailure;
            AfterRebuild?.Invoke(Recovery!);
            return Task.CompletedTask;
        }
    }

    private static (InterfaceRecovery Recovery, FakeHost Host) Create(TimeSpan? timeout = null)
    {
        var host = new FakeHost();
        var recovery = new InterfaceRecovery(host, timeout ?? TimeSpan.FromSeconds(5));
        host.Recovery = recovery;
        host.AfterReload = r => r.ReportNavigation(NavigationOutcome.Succeeded);
        host.AfterRebuild = r => r.ReportNavigation(NavigationOutcome.Succeeded);
        return (recovery, host);
    }

    // MARK: - Mapping WebView2 onto the machine

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, InterfaceFailureKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, InterfaceFailureKind.RendererExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, InterfaceFailureKind.RendererUnresponsive)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited, InterfaceFailureKind.OtherFatal)]
    // A helper process is restarted by WebView2 itself and the page keeps working.
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, InterfaceFailureKind.Auxiliary)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, InterfaceFailureKind.Auxiliary)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, InterfaceFailureKind.Auxiliary)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, InterfaceFailureKind.Auxiliary)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, InterfaceFailureKind.Auxiliary)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, InterfaceFailureKind.Auxiliary)]
    public void EveryProcessFailureKindMapsToOneRecoveryKind(
        CoreWebView2ProcessFailedKind webView,
        InterfaceFailureKind expected) =>
        Assert.Equal(expected, InterfaceRecovery.Classify(webView));

    [Theory]
    [InlineData(InterfaceFailureKind.NavigationFailed, RecoveryAction.ReloadPage)]
    [InlineData(InterfaceFailureKind.RendererExited, RecoveryAction.ReloadPage)]
    [InlineData(InterfaceFailureKind.RendererUnresponsive, RecoveryAction.ReloadPage)]
    [InlineData(InterfaceFailureKind.BrowserProcessExited, RecoveryAction.RecreateWebView)]
    [InlineData(InterfaceFailureKind.OtherFatal, RecoveryAction.RecreateWebView)]
    public void ARendererIsReloadedAndALostBrowserProcessMeansANewControl(
        InterfaceFailureKind kind,
        RecoveryAction expected) =>
        Assert.Equal(expected, InterfaceRecovery.ActionFor(kind));

    [Theory]
    [InlineData(true, false, 200, NavigationOutcome.Succeeded)]
    [InlineData(true, false, 0, NavigationOutcome.Succeeded)]
    // WebView2 calls a 404 from the shell's own file handler a success; it is not.
    [InlineData(true, false, 404, NavigationOutcome.Failed)]
    [InlineData(true, false, 500, NavigationOutcome.Failed)]
    [InlineData(false, false, 0, NavigationOutcome.Failed)]
    // An aborted navigation — the shell refusing an address, a reload replacing a
    // pending load — is expected, whatever else it reports.
    [InlineData(false, true, 0, NavigationOutcome.Cancelled)]
    [InlineData(false, true, 200, NavigationOutcome.Cancelled)]
    public void ANavigationOutcomeSeparatesCancelledFromFailed(
        bool isSuccess,
        bool cancelled,
        int httpStatus,
        NavigationOutcome expected) =>
        Assert.Equal(expected, InterfaceRecovery.Outcome(isSuccess, cancelled, httpStatus));

    // MARK: - When the panel appears

    [Fact]
    public void ARendererThatExitedShowsTheNativePanelOnce()
    {
        var (recovery, host) = Create();

        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        Assert.True(recovery.PanelVisible);
        var shown = Assert.Single(host.Shown);
        Assert.Equal(InterfaceFailureKind.RendererExited, shown.Kind);
        Assert.Equal(RecoveryAction.ReloadPage, shown.Action);
        Assert.False(shown.Reloading);
    }

    [Fact]
    public void RepeatedFailureCallbacksNeverStackAPanel()
    {
        var (recovery, host) = Create();

        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererUnresponsive);
        recovery.ReportProcessFailure(InterfaceFailureKind.NavigationFailed);
        recovery.ReportNavigation(NavigationOutcome.Failed);
        recovery.ReportNavigation(NavigationOutcome.Failed);

        Assert.True(recovery.PanelVisible);
        Assert.Single(host.Shown);
    }

    [Fact]
    public void AWorseFailureRefreshesTheSamePanelAndEscalatesTheAction()
    {
        var (recovery, host) = Create();

        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);
        recovery.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);
        // And a lesser one afterwards changes nothing.
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        Assert.Equal(2, host.Shown.Count);
        Assert.Equal(InterfaceFailureKind.BrowserProcessExited, host.Shown[^1].Kind);
        Assert.Equal(RecoveryAction.RecreateWebView, recovery.NextAction);
        Assert.Equal(0, host.Hidden);
    }

    [Fact]
    public void AHelperProcessFailureDoesNotCoverAWorkingPage()
    {
        var (recovery, host) = Create();

        recovery.ReportProcessFailure(InterfaceFailureKind.Auxiliary);

        Assert.False(recovery.PanelVisible);
        Assert.Empty(host.Shown);
    }

    [Fact]
    public void ACancelledNavigationNeverCoversAWorkingApp()
    {
        var (recovery, host) = Create();

        // What the shell's own refusal of an external link looks like to the page.
        recovery.ReportNavigation(NavigationOutcome.Cancelled);
        recovery.ReportNavigation(NavigationOutcome.Cancelled);
        // A page that loaded while nothing was wrong is nothing to report.
        recovery.ReportNavigation(NavigationOutcome.Succeeded);

        Assert.False(recovery.PanelVisible);
        Assert.Empty(host.Shown);
        Assert.Equal(0, host.Hidden);
    }

    [Fact]
    public void ARealNavigationErrorShowsTheRecoveryPanel()
    {
        var (recovery, host) = Create();

        recovery.ReportNavigation(NavigationOutcome.Failed);

        Assert.True(recovery.PanelVisible);
        var shown = Assert.Single(host.Shown);
        Assert.Equal(InterfaceFailureKind.NavigationFailed, shown.Kind);
        Assert.Equal(RecoveryAction.ReloadPage, shown.Action);
    }

    [Fact]
    public void ACancelledNavigationBetweenRealFailuresIsStillIgnored()
    {
        var (recovery, host) = Create();

        recovery.ReportNavigation(NavigationOutcome.Failed);
        recovery.ReportNavigation(NavigationOutcome.Cancelled);

        Assert.Single(host.Shown);
        Assert.True(recovery.PanelVisible);
    }

    // MARK: - Reload interface

    [Fact]
    public async Task ReloadingARendererNavigatesTheExistingControlAndThenHidesThePanel()
    {
        var (recovery, host) = Create();
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        await recovery.RecoverAsync();

        Assert.Equal(1, host.Reloads);
        Assert.Equal(0, host.Rebuilds);
        Assert.Equal(1, host.Hidden);
        Assert.False(recovery.PanelVisible);
        // The panel told the user a reload was running while it did.
        Assert.Contains(host.Shown, state => state.Reloading);
    }

    [Fact]
    public async Task ALostBrowserProcessRebuildsTheControlInsteadOfReloadingIt()
    {
        var (recovery, host) = Create();
        recovery.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);

        await recovery.RecoverAsync();

        Assert.Equal(0, host.Reloads);
        Assert.Equal(1, host.Rebuilds);
        Assert.False(recovery.PanelVisible);
        Assert.Equal(1, host.Hidden);
    }

    [Fact]
    public async Task ARecoveredInterfaceGoesBackToTheCheapestRecoveryForTheNextFailure()
    {
        var (recovery, host) = Create();
        recovery.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);
        await recovery.RecoverAsync();

        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);
        await recovery.RecoverAsync();

        Assert.Equal(1, host.Rebuilds);
        Assert.Equal(1, host.Reloads);
    }

    [Fact]
    public async Task AReloadThatFailsToLoadKeepsThePanelAndEscalatesToARebuild()
    {
        var (recovery, host) = Create();
        host.AfterReload = r => r.ReportNavigation(NavigationOutcome.Failed);
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        await recovery.RecoverAsync();

        Assert.True(recovery.PanelVisible);
        Assert.Equal(0, host.Hidden);
        Assert.Equal(RecoveryAction.RecreateWebView, recovery.NextAction);
        var last = host.Shown[^1];
        Assert.False(last.Reloading);
        Assert.Equal(1, last.FailedAttempts);

        // The next attempt rebuilds, and this time the page comes back.
        await recovery.RecoverAsync();
        Assert.Equal(1, host.Reloads);
        Assert.Equal(1, host.Rebuilds);
        Assert.False(recovery.PanelVisible);
    }

    [Fact]
    public async Task AControlThatCannotBeNavigatedKeepsThePanelAndEscalates()
    {
        var (recovery, host) = Create();
        host.ReloadFailure = new InvalidOperationException("the web view is gone");
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        await recovery.RecoverAsync();

        Assert.True(recovery.PanelVisible);
        Assert.Equal(RecoveryAction.RecreateWebView, recovery.NextAction);
        Assert.Equal(0, host.Hidden);
    }

    [Fact]
    public async Task AFailedRebuildStaysARebuildAndKeepsThePanel()
    {
        var (recovery, host) = Create();
        host.RebuildFailure = new InvalidOperationException("the runtime is gone");
        recovery.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);

        await recovery.RecoverAsync();
        await recovery.RecoverAsync();

        Assert.Equal(2, host.Rebuilds);
        Assert.True(recovery.PanelVisible);
        Assert.Equal(2, host.Shown.Last().FailedAttempts);
        Assert.Equal(RecoveryAction.RecreateWebView, recovery.NextAction);
    }

    [Fact]
    public async Task APageThatNeverFinishesLoadingTimesOutInsteadOfHangingThePanel()
    {
        var (recovery, host) = Create(TimeSpan.FromMilliseconds(50));
        host.AfterReload = _ => { };
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererUnresponsive);

        await recovery.RecoverAsync();

        Assert.True(recovery.PanelVisible);
        Assert.False(recovery.Recovering);
        Assert.Equal(RecoveryAction.RecreateWebView, recovery.NextAction);
    }

    [Fact]
    public async Task TheNavigationThatAReloadReplacesIsNotCountedAgainstTheReload()
    {
        var (recovery, host) = Create();
        host.AfterReload = r =>
        {
            // The pending navigation reports "cancelled" before the new one finishes.
            r.ReportNavigation(NavigationOutcome.Cancelled);
            r.ReportNavigation(NavigationOutcome.Succeeded);
        };
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        await recovery.RecoverAsync();

        Assert.False(recovery.PanelVisible);
    }

    [Fact]
    public async Task AFailureWhileReloadingFailsThatAttemptWithoutAddingAPanel()
    {
        var (recovery, host) = Create();
        host.AfterReload = r => r.ReportProcessFailure(InterfaceFailureKind.RendererExited);
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        await recovery.RecoverAsync();

        Assert.True(recovery.PanelVisible);
        Assert.Equal(0, host.Hidden);
        Assert.Equal(1, host.Shown.Last().FailedAttempts);
    }

    [Fact]
    public async Task ASecondReloadRequestWhileOneIsRunningIsIgnored()
    {
        var (recovery, host) = Create();
        host.AfterReload = _ => { };
        recovery.ReportProcessFailure(InterfaceFailureKind.RendererExited);

        var first = recovery.RecoverAsync();
        Assert.True(recovery.Recovering);
        await recovery.RecoverAsync();
        Assert.Equal(1, host.Reloads);

        recovery.ReportNavigation(NavigationOutcome.Succeeded);
        await first;

        Assert.Equal(1, host.Reloads);
        Assert.False(recovery.PanelVisible);
    }

    [Fact]
    public async Task ReloadingWithNothingWrongDoesNothing()
    {
        var (recovery, host) = Create();

        await recovery.RecoverAsync();

        Assert.Equal(0, host.Reloads);
        Assert.Equal(0, host.Rebuilds);
        Assert.Empty(host.Shown);
    }

    // MARK: - What recovery can and cannot do

    [Fact]
    public void RecoveryCanOnlyReloadRebuildAndShowOrHideThePanel()
    {
        // It must not be able to send a command: no config write, no health check,
        // nothing the page had in flight. The window offers it nothing of the kind.
        var members = typeof(IInterfaceRecoveryHost).GetMethods().Select(method => method.Name).Order();

        Assert.Equal(
            new[] { "HidePanel", "RecreateInterfaceAsync", "ReloadInterface", "ShowPanel" },
            members);
    }

    [Fact]
    public void ReloadAndRebuildGoOnlyToTheBundledAppOrigin()
    {
        Assert.Equal("https://app/index.html", WebHost.AppEntryUri.AbsoluteUri);
        Assert.Equal(WebHost.TrustedOrigin.Host, WebHost.AppEntryUri.Host);
        Assert.Equal("", WebHost.AppEntryUri.Query);
        Assert.Equal("", WebHost.AppEntryUri.Fragment);
    }

    // MARK: - What the panel says

    [Theory]
    [InlineData(InterfaceFailureKind.NavigationFailed)]
    [InlineData(InterfaceFailureKind.RendererExited)]
    [InlineData(InterfaceFailureKind.RendererUnresponsive)]
    [InlineData(InterfaceFailureKind.BrowserProcessExited)]
    [InlineData(InterfaceFailureKind.OtherFatal)]
    public void EachFailureHasAFixedSentenceThatQuotesNothing(InterfaceFailureKind kind)
    {
        var reason = InterfaceRecoveryPanel.ReasonFor(kind);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain("\\", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelSaysWhenARecoveryIsRunningAndWhenItFailed()
    {
        Assert.Null(InterfaceRecoveryPanel.StatusFor(
            new RecoveryPanelState(InterfaceFailureKind.RendererExited, RecoveryAction.ReloadPage, false, 0)));
        Assert.Equal(
            "Reloading the interface…",
            InterfaceRecoveryPanel.StatusFor(
                new RecoveryPanelState(InterfaceFailureKind.RendererExited, RecoveryAction.ReloadPage, true, 0)));
        Assert.Equal(
            "Rebuilding the interface…",
            InterfaceRecoveryPanel.StatusFor(
                new RecoveryPanelState(InterfaceFailureKind.BrowserProcessExited, RecoveryAction.RecreateWebView, true, 0)));
        Assert.Contains(
            "did not bring the interface back",
            InterfaceRecoveryPanel.StatusFor(
                new RecoveryPanelState(InterfaceFailureKind.RendererExited, RecoveryAction.RecreateWebView, false, 1)),
            StringComparison.Ordinal);
    }
}
