#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Kytto.App.Web;

namespace Kytto.App.Tests;

/// <summary>
/// The real window and the real WebView2: recovery panel, reload, rebuild, and the
/// external-navigation policy (§7.14). Debug only, like the other native UI tests —
/// it uses the isolated <c>KYTTO_TEST_HOME</c> / <c>KYTTO_TEST_DATA_ROOT</c> names that
/// exist only in Debug builds, never the developer's own profile.
/// </summary>
/// <remarks>
/// Each scenario owns its window and dispatcher and creates no
/// <c>System.Windows.Application</c>, so it can run beside the others and the
/// native smoke test (which is ordered last, see <see cref="NativeUiSmokeLastOrderer"/>).
/// The only process this ever kills is the WebView2 browser process of its own
/// window, whose id comes from <c>CoreWebView2.BrowserProcessId</c> and whose user
/// data folder is under this test's temporary directory.
/// </remarks>
public sealed class WebView2RecoveryIntegrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-recovery-tests", Guid.NewGuid().ToString("N"));

    private sealed class RecordingLauncher : ISupportLinkLauncher
    {
        public List<Uri> Opened { get; } = [];

        public bool Open(Uri url)
        {
            Opened.Add(url);
            return true;
        }
    }

    // MARK: - Harness

    private void Scenario(Func<MainWindow, RecordingLauncher, Task> body, int seconds = 90)
    {
        Directory.CreateDirectory(_home);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var oldHome = Environment.GetEnvironmentVariable("KYTTO_TEST_HOME");
            var oldData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", _home);
                Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", Path.Combine(_home, "KyttoData"));

                var launcher = new RecordingLauncher();
                var window = new MainWindow { SupportLinkLauncher = launcher };
                var dispatcher = Dispatcher.CurrentDispatcher;
                window.Show();

                _ = dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await body(window, launcher);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        window.RequestQuit();
                        dispatcher.InvokeShutdown();
                    }
                });

                Dispatcher.Run();
            }
            catch (Exception error)
            {
                failure ??= error;
            }
            finally
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", oldHome);
                Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", oldData);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds + 15)), "The recovery scenario did not finish.");
        if (failure is not null) throw failure;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition()) return;
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The control is mid-rebuild; ask again.
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for: {what}.");
    }

    private static Task WaitUntil(Func<bool> condition, string what, int seconds = 30) =>
        WaitUntil(() => Task.FromResult(condition()), what, seconds);

    private static async Task<string?> Eval(MainWindow window, string script) =>
        JsonSerializer.Deserialize<string?>(await window.Web.CoreWebView2.ExecuteScriptAsync(script));

    /// <summary>The page has rendered something, so its script ran and its bridge answered.</summary>
    private static Task PageReady(MainWindow window) =>
        WaitUntil(async () =>
            window.Web.CoreWebView2 is not null &&
            await Eval(window, "String(document.querySelector('#app')?.children.length ?? 0)") is { } count &&
            count != "0",
            "the page to render");

    private static int PanelCount(MainWindow window) =>
        window.Layout.Children.OfType<InterfaceRecoveryPanel>().Count();

    private static void Click(MainWindow window, System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // MARK: - Scenarios

    [Fact]
    public void ARendererFailureShowsOneNativePanelAndReloadBringsThePageBack()
    {
        Scenario(async (window, _) =>
        {
            await PageReady(window);
            Assert.Equal(Visibility.Collapsed, window.Recovery.Visibility);
            Assert.Equal(Visibility.Visible, window.Web.Visibility);
            var control = window.Web;

            // The window draws its own WPF content (the panel) in software: hardware
            // rendering painted nothing over the glass and backdrop when checked by eye.
            var target = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(window)).CompositionTarget;
            Assert.Equal(System.Windows.Interop.RenderMode.SoftwareOnly, target.RenderMode);

            // What WebHost reports when the page's renderer ends — twice, as it can.
            window.RecoveryState!.ReportProcessFailure(InterfaceFailureKind.RendererExited);
            window.RecoveryState.ReportProcessFailure(InterfaceFailureKind.RendererExited);

            Assert.Equal(Visibility.Visible, window.Recovery.Visibility);
            // The web view is an HwndHost and would paint over the panel.
            Assert.Equal(Visibility.Hidden, window.Web.Visibility);
            Assert.Equal(1, PanelCount(window));

            Click(window, window.Recovery.ReloadButton);
            await WaitUntil(() => !window.RecoveryState.PanelVisible, "the reload to finish");

            Assert.Equal(Visibility.Collapsed, window.Recovery.Visibility);
            Assert.Equal(Visibility.Visible, window.Web.Visibility);
            // The same control: a renderer failure never needs a new one.
            Assert.Same(control, window.Web);
            await PageReady(window);
            Assert.StartsWith("https://app/", window.Web.CoreWebView2.Source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ALostBrowserProcessRebuildsTheControlWithItsBridgeAndNavigationPolicy()
    {
        Scenario(async (window, launcher) =>
        {
            await PageReady(window);
            var before = window.Web;

            window.RecoveryState!.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);
            Assert.Equal(RecoveryAction.RecreateWebView, window.RecoveryState.NextAction);
            Assert.Equal(Visibility.Visible, window.Recovery.Visibility);

            Click(window, window.Recovery.ReloadButton);
            await WaitUntil(() => !window.RecoveryState.PanelVisible, "the rebuild to finish");

            // A new control, in the same place in the layout, with no panel stacked.
            Assert.NotSame(before, window.Web);
            Assert.Equal(1, PanelCount(window));
            Assert.Equal(Visibility.Visible, window.Web.Visibility);
            Assert.Equal(Visibility.Collapsed, window.Recovery.Visibility);
            await PageReady(window);

            // The bridge: the rebuilt page can still talk to native code.
            await Eval(window, "window.__reply = null; webkit.messageHandlers.kytto.postMessage({ command: 'app.info', payload: null }).then(r => { window.__reply = r; }); 'sent'");
            await WaitUntil(async () => await Eval(window, "window.__reply") is not null, "the bridge to answer");
            Assert.Contains("\"ok\":true", (await Eval(window, "window.__reply"))!, StringComparison.Ordinal);

            // The navigation policy: an address that is not the app is refused, and the
            // refusal is not mistaken for a failed interface.
            window.Web.CoreWebView2.Navigate("https://example.invalid/");
            await Task.Delay(1500);
            Assert.StartsWith("https://app/", window.Web.CoreWebView2.Source, StringComparison.Ordinal);
            Assert.False(window.RecoveryState.PanelVisible);
            Assert.Empty(launcher.Opened);
        });
    }

    [Fact]
    public void ARefusedNavigationNeverCoversAWorkingPage()
    {
        Scenario(async (window, launcher) =>
        {
            await PageReady(window);

            foreach (var address in new[] { "https://example.invalid/", "http://example.invalid/", "https://kytto.jakubhecht.sk/?x=1" })
            {
                window.Web.CoreWebView2.Navigate(address);
                await Task.Delay(1200);
            }

            Assert.False(window.RecoveryState!.PanelVisible);
            Assert.Equal(Visibility.Collapsed, window.Recovery.Visibility);
            Assert.StartsWith("https://app/", window.Web.CoreWebView2.Source, StringComparison.Ordinal);
            // A script navigation is not a click, and none of these is a fixed link.
            Assert.Empty(launcher.Opened);
        });
    }

    [Fact]
    public void AUsersClickOnASupportLinkOpensTheBrowserAndNothingBroaderDoes()
    {
        Scenario(async (window, launcher) =>
        {
            await PageReady(window);
            var app = window.Web.CoreWebView2.Source;

            // Four real links, laid over the page where a click can find them: the two
            // fixed ones (one asking for a new tab, one navigating in place) and two
            // that are neither.
            await Eval(window, """
                (() => {
                  const make = (id, href, top, blank) => {
                    const a = document.createElement('a');
                    a.id = id; a.href = href; a.textContent = id;
                    if (blank) { a.target = '_blank'; a.rel = 'noopener noreferrer'; }
                    a.style.cssText = `position:fixed;left:20px;top:${top}px;width:240px;height:30px;z-index:99999;background:#ccc;color:#000`;
                    document.body.append(a);
                  };
                  make('bug', 'https://kytto.jakubhecht.sk/#report-bug', 40, true);
                  make('gh', 'https://github.com/heyitsjakub/KyttoMCP/issues/new/choose', 90, false);
                  make('evil-blank', 'https://example.invalid/', 140, true);
                  make('evil-same', 'https://example.invalid/', 190, false);
                  make('extra', 'https://kytto.jakubhecht.sk/#report-bug&error=boom', 240, true);
                  return 'ok';
                })()
                """);

            foreach (var top in new[] { 55, 105, 155, 205, 255 })
            {
                await ClickAt(window, 60, top);
                await Task.Delay(1200);
            }

            Assert.Equal(
                [SupportLinks.BugReport.Url, SupportLinks.GitHubIssue.Url],
                launcher.Opened);
            // Cancelled and refused navigations left the app exactly where it was.
            Assert.False(window.RecoveryState!.PanelVisible);
            Assert.Equal(Visibility.Collapsed, window.Recovery.Visibility);
            Assert.Equal(app, window.Web.CoreWebView2.Source);
        });
    }

    /// <summary>A trusted click, the way a user's would arrive, through the renderer's own input.</summary>
    private static async Task ClickAt(MainWindow window, int x, int y)
    {
        var core = window.Web.CoreWebView2;
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", $$"""{"type":"mouseMoved","x":{{x}},"y":{{y}}}""");
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", $$"""{"type":"mousePressed","x":{{x}},"y":{{y}},"button":"left","clickCount":1}""");
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent", $$"""{"type":"mouseReleased","x":{{x}},"y":{{y}},"button":"left","clickCount":1}""");
    }

    [Fact]
    public void ARenderProcessThatReallyCrashesShowsThePanelAndReloadRecovers()
    {
        Scenario(async (window, launcher) =>
        {
            await PageReady(window);

            // A real renderer crash, asked for through the DevTools protocol.
            _ = window.Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}")
                .ContinueWith(task => task.Exception, TaskScheduler.Default);
            await WaitUntil(() => window.RecoveryState!.PanelVisible, "the crash to be reported");

            Assert.Equal(Visibility.Visible, window.Recovery.Visibility);
            Assert.Equal(1, PanelCount(window));

            Click(window, window.Recovery.ReloadButton);
            await WaitUntil(() => !window.RecoveryState!.PanelVisible, "the reload to finish");
            await PageReady(window);
            Assert.Equal(Visibility.Visible, window.Web.Visibility);
        });
    }

    [Fact]
    public void ABrowserProcessThatReallyDiesIsRebuiltAfterTheUserAsksForIt()
    {
        Scenario(async (window, _) =>
        {
            await PageReady(window);
            var before = window.Web;
            var pid = (int)window.Web.CoreWebView2.BrowserProcessId;

            // Only this window's own WebView2 browser process, by the id it reported.
            using (var browser = Process.GetProcessById(pid))
            {
                Assert.Equal("msedgewebview2", browser.ProcessName, ignoreCase: true);
                browser.Kill();
            }
            await WaitUntil(() => window.RecoveryState!.PanelVisible, "the loss to be reported");
            Assert.Equal(RecoveryAction.RecreateWebView, window.RecoveryState!.NextAction);
            Assert.Equal(1, PanelCount(window));

            Click(window, window.Recovery.ReloadButton);
            await WaitUntil(() => !window.RecoveryState.PanelVisible, "the rebuild to finish");

            Assert.NotSame(before, window.Web);
            await PageReady(window);
            Assert.Equal(Visibility.Visible, window.Web.Visibility);
        });
    }

    [Fact]
    public void TheReportButtonsOnThePanelOpenTheFixedLinksWithoutTheWebView()
    {
        Scenario(async (window, launcher) =>
        {
            await PageReady(window);
            window.RecoveryState!.ReportProcessFailure(InterfaceFailureKind.BrowserProcessExited);

            Click(window, window.Recovery.BugReportButton);
            Click(window, window.Recovery.GitHubButton);

            Assert.Equal([SupportLinks.BugReport.Url, SupportLinks.GitHubIssue.Url], launcher.Opened);
            // Reporting is not recovering: the panel is still up and nothing reloaded.
            Assert.True(window.RecoveryState.PanelVisible);
            Assert.Equal(Visibility.Visible, window.Recovery.Visibility);
            await Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // WebView2 can hold its profile a moment after the window closed.
        }
    }
}
#endif
