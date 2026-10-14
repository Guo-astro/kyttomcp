using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kytto.App.Web;

namespace Kytto.App;

/// <summary>
/// The native panel shown when the web interface failed to load or lost a process
/// (§7.14): Reload interface, Report a bug…, Report on GitHub….
/// </summary>
/// <remarks>
/// It holds no logic about <em>when</em> to appear — <see cref="InterfaceRecovery"/>
/// decides that — and needs neither the page nor IPC, which is the point: it has to
/// work when both are gone. Its words never include an exception, a path or a
/// configuration value; each failure kind has a fixed sentence.
/// </remarks>
public partial class InterfaceRecoveryPanel : UserControl
{
    public InterfaceRecoveryPanel() => InitializeComponent();

    /// <summary>"Reload interface" was chosen.</summary>
    internal event Action? ReloadRequested;

    /// <summary>One of the two fixed report links was chosen.</summary>
    internal event Action<SupportLink>? ReportRequested;

    /// <summary>What a failure of each kind says, in words that quote nothing.</summary>
    internal static string ReasonFor(InterfaceFailureKind kind) => kind switch
    {
        InterfaceFailureKind.RendererExited => "The interface’s display process ended unexpectedly.",
        InterfaceFailureKind.RendererUnresponsive => "The interface stopped responding.",
        InterfaceFailureKind.BrowserProcessExited or InterfaceFailureKind.OtherFatal =>
            "The Microsoft Edge WebView2 component Kytto uses stopped unexpectedly.",
        _ => "The interface files could not be loaded.",
    };

    /// <summary>The line under the message while a recovery runs or after one failed.</summary>
    internal static string? StatusFor(RecoveryPanelState state)
    {
        if (state.Reloading)
        {
            return state.Action == RecoveryAction.RecreateWebView
                ? "Rebuilding the interface…"
                : "Reloading the interface…";
        }

        return state.FailedAttempts > 0
            ? "That did not bring the interface back. Kytto will rebuild it this time; if it fails again, quit and reopen Kytto."
            : null;
    }

    internal void Present(RecoveryPanelState state)
    {
        Message.Text = $"{ReasonFor(state.Kind)} Reload the interface. If the problem continues, quit and reopen Kytto or report a bug.";

        var status = StatusFor(state);
        Status.Text = status ?? "";
        Status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;

        // One attempt at a time: a second click while a reload is running would
        // only start a second navigation.
        ReloadButton.IsEnabled = !state.Reloading;
    }

    /// <summary>Says something the reporter needs when their browser would not open.</summary>
    internal void ShowNotice(string text)
    {
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
    }

    /// <summary>The same light/dark decision the window makes for its backdrop.</summary>
    internal void ApplyAppearance(bool dark) =>
        Foreground = dark ? Brushes.White : Brushes.Black;

    /// <summary>Gives the keyboard to the primary action, so Enter reloads.</summary>
    internal void FocusReload() => ReloadButton.Focus();

    private void OnReload(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke();

    private void OnReportBug(object sender, RoutedEventArgs e) => ReportRequested?.Invoke(SupportLinks.BugReport);

    private void OnReportGitHub(object sender, RoutedEventArgs e) => ReportRequested?.Invoke(SupportLinks.GitHubIssue);

    /// <summary>
    /// The page normally declares the drag region; with the page gone, this strip
    /// is what keeps the window movable.
    /// </summary>
    private void OnDragStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Window.GetWindow(this) is not { } window) return;

        if (e.ClickCount == 2 && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the move began.
        }
    }
}
