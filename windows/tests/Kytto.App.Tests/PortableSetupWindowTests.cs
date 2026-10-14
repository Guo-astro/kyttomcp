using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Kytto.Core.Clients;
using Kytto.Core.Portable;

namespace Kytto.App.Tests;

/// <summary>
/// The native window that collects secrets, URLs and private paths (§7.13). It is the
/// one place typed values live, so what it does with them — mask them, keep them
/// across a refusal, drop them on close — is tested here rather than assumed.
/// </summary>
/// <remarks>
/// The windows are built and driven without being shown. WPF refuses to show any
/// window once an application in the process has shut down, which an earlier native
/// smoke test legitimately does; everything under test here is a property of the
/// controls, not of being on screen.
/// </remarks>
public sealed class PortableSetupWindowTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The native window test did not exit.");
        if (failure is not null) throw failure;
    }

    private static void Press(Window window, string label) =>
        Descendants<Button>(window).Single(button => (string)button.Content == label)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static readonly string SecretId = Guid.NewGuid().ToString("D");
    private static readonly string PathId = Guid.NewGuid().ToString("D");

    private static PortableSetupSummary Summary() => new(
        ["memory"],
        [],
        [
            new PortableRequirement(SecretId, "memory (cursor): environment API_KEY", PortableRequirementKind.Secret),
            new PortableRequirement(PathId, "memory (cursor): working directory", PortableRequirementKind.Path),
        ],
        0,
        []);

    [Fact]
    public void ARefusedInputLeavesTheWindowOpenWithEverythingAsTyped()
    {
        var attempts = new List<PortableImportSelection>();
        var closedAfterRefusal = true;
        var acceptedAfterRefusal = true;
        var secretAfterRefusal = "";
        var pathAfterRefusal = "";
        var errorShown = "";
        var closedAfterSuccess = false;
        var acceptedAfterSuccess = false;

        OnStaThread(() =>
        {
            var closed = false;
            var window = new PortableImportWindow(
                null,
                Summary(),
                [(ClientId.Cursor, "Cursor"), (ClientId.Codex, "Codex")],
                selection =>
                {
                    attempts.Add(selection);
                    return attempts.Count == 1
                        ? "Provide memory (cursor): working directory in the native import dialog."
                        : null;
                });
            window.Closed += (_, _) => closed = true;

            var secret = Descendants<PasswordBox>(window).Single();
            var path = Descendants<TextBox>(window).Single();
            var boxes = Descendants<CheckBox>(window).ToArray();
            Assert.Equal(["Cursor", "Codex"], boxes.Select(box => box.Content));
            // The first destination is chosen for the person, the rest are not.
            Assert.Equal([true, false], boxes.Select(box => box.IsChecked == true));

            secret.Password = "typed-secret-value";
            Press(window, "Preview");

            closedAfterRefusal = closed;
            acceptedAfterRefusal = window.Accepted;
            secretAfterRefusal = secret.Password;
            pathAfterRefusal = path.Text;
            errorShown = Descendants<TextBlock>(window)
                .First(block => block.Visibility == Visibility.Visible &&
                                block.Text.StartsWith("Provide", StringComparison.Ordinal))
                .Text;

            path.Text = @"  C:\mapped\project  ";
            Press(window, "Preview");
            closedAfterSuccess = closed;
            acceptedAfterSuccess = window.Accepted;
        });

        Assert.False(closedAfterRefusal, "a refused input must not close the window");
        Assert.False(acceptedAfterRefusal);
        Assert.Equal("typed-secret-value", secretAfterRefusal);
        Assert.Equal("", pathAfterRefusal);
        Assert.Contains("working directory", errorShown, StringComparison.Ordinal);
        Assert.True(closedAfterSuccess);
        Assert.True(acceptedAfterSuccess);

        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal([ClientId.Cursor], attempt.Targets));
        Assert.Equal("typed-secret-value", attempts[0].Values[SecretId]);
        Assert.Equal("", attempts[0].Values[PathId]);
        // The same typed secret is still what the second attempt carries, and a path
        // loses only the whitespace around it.
        Assert.Equal("typed-secret-value", attempts[1].Values[SecretId]);
        Assert.Equal(@"C:\mapped\project", attempts[1].Values[PathId]);
    }

    [Fact]
    public void OnlyPathsAreShownAndEverythingElseIsAMaskedField()
    {
        var kinds = new List<string>();
        OnStaThread(() =>
        {
            var window = new PortableImportWindow(null, Summary(), [(ClientId.Cursor, "Cursor")], _ => null);
            kinds.AddRange(Descendants<PasswordBox>(window).Select(_ => "masked"));
            kinds.AddRange(Descendants<TextBox>(window).Select(_ => "visible"));
        });

        // One secret requirement, one path requirement: one of each, never two visible.
        Assert.Equal(["masked", "visible"], kinds);
    }

    [Fact]
    public void ClosingWithoutPreviewSubmitsNothingAndClearsWhatWasTyped()
    {
        var submitted = false;
        var accepted = true;
        var secretAfterClose = "not cleared";
        var pathAfterClose = "not cleared";

        OnStaThread(() =>
        {
            var window = new PortableImportWindow(
                null, Summary(), [(ClientId.Cursor, "Cursor")], _ => { submitted = true; return null; });
            var secret = Descendants<PasswordBox>(window).Single();
            var path = Descendants<TextBox>(window).Single();
            secret.Password = "typed";
            path.Text = @"C:\private";

            // Cancel is what Escape and the close box do.
            window.Close();

            accepted = window.Accepted;
            secretAfterClose = secret.Password;
            pathAfterClose = path.Text;
        });

        Assert.False(submitted);
        Assert.False(accepted);
        Assert.Equal("", secretAfterClose);
        Assert.Equal("", pathAfterClose);
    }

    [Fact]
    public void CancelAndEscapeAreTheCancelButtonAndTheDefaultIsPreview()
    {
        OnStaThread(() =>
        {
            var window = new PortableImportWindow(null, Summary(), [(ClientId.Cursor, "Cursor")], _ => null);
            var buttons = Descendants<Button>(window).ToDictionary(button => (string)button.Content);

            Assert.True(buttons["Cancel"].IsCancel);
            Assert.True(buttons["Preview"].IsDefault);
            Assert.False(buttons["Preview"].IsCancel);
        });
    }

    [Fact]
    public void TheExportWindowOffersTheContractOptionUncheckedAndOnlyChooseAccepts()
    {
        bool includeByDefault = true;
        bool acceptedBeforeChoose = true;
        bool includeAfterChoice = false;
        bool acceptedAfterChoose = false;
        bool cancelAccepts = true;

        OnStaThread(() =>
        {
            var window = new PortableExportWindow(null);
            includeByDefault = window.IncludeReviewedContracts;
            acceptedBeforeChoose = window.Accepted;
            Descendants<CheckBox>(window).Single().IsChecked = true;
            Press(window, "Choose file…");
            includeAfterChoice = window.IncludeReviewedContracts;
            acceptedAfterChoose = window.Accepted;

            var cancelled = new PortableExportWindow(null);
            cancelled.Close();
            cancelAccepts = cancelled.Accepted;
        });

        Assert.False(includeByDefault);
        Assert.False(acceptedBeforeChoose);
        Assert.True(includeAfterChoice);
        Assert.True(acceptedAfterChoose);
        Assert.False(cancelAccepts);
    }
}
