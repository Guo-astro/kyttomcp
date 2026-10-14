using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kytto.App;

/// <summary>
/// The native dialogs of a remote sign-in: consent, a public client ID, and a window
/// that waits for the browser and can be cancelled (§7.3).
/// </summary>
/// <remarks>
/// <para>
/// Native on purpose. What the person agrees to — which provider, which scopes — is
/// shown by the shell from validated metadata, never rendered by the web layer, and no
/// token, code or endpoint is ever passed through anything the page can read.
/// </para>
/// <para>
/// Everything shown came from a server. It is clipped and stripped of control
/// characters before it is put in front of anyone, and it is only ever text.
/// </para>
/// </remarks>
internal sealed class RemoteSignInController : IRemoteSignInUi
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } main ? main : null;

    public bool ConfirmSignIn(string serverName, string issuer, IReadOnlyList<string> scopes)
    {
        var requested = scopes.Count == 0
            ? "No scopes advertised"
            : Display(string.Join(", ", scopes), 400);
        var message =
            $"Server: {Display(serverName, 120)}\n" +
            $"Authorization provider: {Display(issuer, 200)}\n" +
            $"Requested scopes: {requested}\n\n" +
            "Kytto will open your browser to sign in, and keep the resulting authorization in " +
            "Windows Credential Manager for Remote Doctor checks. Your other MCP clients keep " +
            "their own sign-ins; Kytto does not change any client configuration.";
        const string title = "Sign in to remote MCP server";
        var result = Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    public string? PromptClientId(string redirectUri)
    {
        var field = new TextBox { MinWidth = 380, Margin = new Thickness(0, 8, 0, 0), MaxLength = 512 };
        var confirm = new Button { Content = "Sign in", IsDefault = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        var window = Dialog(
            "OAuth client ID required",
            new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This provider does not register clients on its own. Enter the ID of a public " +
                       $"client you registered for the redirect {redirectUri} (a loopback redirect on " +
                       "this computer; any port). No client secret is accepted.",
            },
            field,
            confirm,
            cancel);
        string? entered = null;
        confirm.Click += (_, _) =>
        {
            entered = field.Text.Trim();
            window.DialogResult = true;
        };
        field.Loaded += (_, _) => Keyboard.Focus(field);
        return window.ShowDialog() == true && !string.IsNullOrEmpty(entered) ? entered : null;
    }

    public IRemoteSignInWait ShowWaiting(string issuer)
    {
        var cancellation = new CancellationTokenSource();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        var window = Dialog(
            "Waiting for sign-in",
            new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Finish signing in with {Display(issuer, 200)} in your browser. " +
                       "Kytto is listening on this computer only, and stops when you finish or cancel.",
            },
            null,
            null,
            cancel);
        window.ShowInTaskbar = false;
        cancel.Click += (_, _) => cancellation.Cancel();
        var waiting = new Waiting(window, cancellation);
        // Closing the window with its X is giving up, too — unless the flow itself
        // is what closed it.
        window.Closed += (_, _) => waiting.WindowClosed();
        window.Show();
        return waiting;
    }

    private sealed class Waiting(Window window, CancellationTokenSource cancellation) : IRemoteSignInWait
    {
        private bool _closed;

        public CancellationToken Cancelled => cancellation.Token;

        internal void WindowClosed()
        {
            if (!_closed && !cancellation.IsCancellationRequested) cancellation.Cancel();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // Already closing.
            }
            cancellation.Dispose();
        }
    }

    private static Window Dialog(string title, UIElement body, UIElement? input, Button? confirm, Button cancel)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        if (confirm is not null) buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(body);
        if (input is not null) content.Children.Add(input);
        content.Children.Add(buttons);

        return new Window
        {
            Title = title,
            Content = content,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = Owner,
            ShowInTaskbar = false,
        };
    }

    /// <summary>Text from a server, made safe to show: no control characters, bounded.</summary>
    internal static string Display(string text, int limit)
    {
        var clean = new StringBuilder(Math.Min(text.Length, limit + 1));
        foreach (var rune in text.EnumerateRunes())
        {
            if (clean.Length >= limit)
            {
                clean.Append('…');
                break;
            }
            // Control and format characters (bidi overrides included) are how a long
            // string is made to read as something it is not.
            var category = Rune.GetUnicodeCategory(rune);
            clean.Append(category is System.Globalization.UnicodeCategory.Control or
                System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.LineSeparator or
                System.Globalization.UnicodeCategory.ParagraphSeparator
                ? ' '
                : rune.ToString());
        }
        return clean.ToString();
    }
}
