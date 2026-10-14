using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using Forms = System.Windows.Forms;

namespace Kytto.App.Tests;

/// <summary>
/// The native places the two report links live: Settings → Support, the save
/// failure, and the tray's Help submenu (§7.14).
/// </summary>
public sealed class SupportUiTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-support-ui-tests", Guid.NewGuid().ToString("N"));

    private sealed class RecordingLauncher : ISupportLinkLauncher
    {
        public List<Uri> Opened { get; } = [];
        public bool Succeeds { get; set; } = true;

        public bool Open(Uri url)
        {
            Opened.Add(url);
            return Succeeds;
        }
    }

    private sealed class FailingSettingsWriter : ISettingsFileWriter
    {
        public void Write(string text, string path) => throw new IOException("Injected settings write failure.");
    }

    private sealed class NoLoginItem : ILoginItem
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    private AppModel NewModel(KyttoPaths paths, SettingsStore? settings = null) => new(
        paths,
        _root,
        new NoInstalledApps(),
        new InMemorySecretStore(),
        watchConfigs: false,
        settingsStore: settings,
        loginItem: new NoLoginItem());

    /// <summary>WPF windows need a single-threaded apartment of their own.</summary>
    private static void RunSta(Action body)
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI test thread did not finish.");
        if (failure is not null) throw failure;
    }

    // MARK: - Settings → Support

    [Fact]
    public void SettingsOffersBothReportLinksWithWhoCanSeeThem()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        RunSta(() =>
        {
            using var model = NewModel(paths);
            var window = new SettingsWindow(model, new RecordingLauncher());

            Assert.Equal("Report a bug…", window.BugReportButton.Content);
            Assert.Equal("Private · No account", window.BugReportAudience.Text);
            Assert.Equal("Report on GitHub…", window.GitHubReportButton.Content);
            Assert.Equal("Public · GitHub account", window.GitHubReportAudience.Text);
            // The sentence that says nothing is attached is the one the app keeps.
            Assert.Equal(SupportLinks.AttachmentNote, window.SupportNote.Text);
            Assert.Equal(Visibility.Collapsed, window.SupportError.Visibility);
            // The save-failure shortcut is for a failed save only.
            Assert.Equal(Visibility.Collapsed, window.SaveFailureReportButton.Visibility);
        });
    }

    [Fact]
    public void SettingsLinksOpenTheFixedAddressesAndSaveNothing()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        RunSta(() =>
        {
            using var model = NewModel(paths);
            var launcher = new RecordingLauncher();
            var window = new SettingsWindow(model, launcher);

            window.BugReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.GitHubReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(
                [new Uri("https://kytto.jakubhecht.sk/#report-bug"),
                 new Uri("https://github.com/heyitsjakub/KyttoMCP/issues/new/choose")],
                launcher.Opened);
            Assert.Equal(Visibility.Collapsed, window.SupportError.Visibility);
            // Reading the support section never changes a setting.
            Assert.False(File.Exists(paths.SettingsFile));
        });
    }

    [Fact]
    public void ABrowserThatWillNotOpenLeavesTheReporterTheAddress()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        RunSta(() =>
        {
            using var model = NewModel(paths);
            var window = new SettingsWindow(model, new RecordingLauncher { Succeeds = false });

            window.BugReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Visible, window.SupportError.Visibility);
            Assert.Contains("https://kytto.jakubhecht.sk/#report-bug", window.SupportError.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AFailedSaveOffersThePrivateReportBesideTheError()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        RunSta(() =>
        {
            var store = new SettingsStore(paths, new FailingSettingsWriter());
            using var model = NewModel(paths, store);
            var launcher = new RecordingLauncher();
            var window = new SettingsWindow(model, launcher);
            // A save that changes nothing writes nothing, so give it something to write.
            window.TrayBox.IsChecked = window.TrayBox.IsChecked != true;

            Save(window);

            Assert.Equal(Visibility.Visible, window.CustomSourceError.Visibility);
            Assert.Contains("settings write failure", window.CustomSourceError.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, window.SaveFailureReportButton.Visibility);

            window.SaveFailureReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([SupportLinks.BugReport.Url], launcher.Opened);
        });
    }

    [Fact]
    public void ASuccessfulSaveOffersNoReport()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        RunSta(() =>
        {
            using var model = NewModel(paths);
            var window = new SettingsWindow(model, new RecordingLauncher());

            // Saving closes the dialog with a result; outside ShowDialog that throws,
            // which is after the part under test, so only the buttons are checked.
            try
            {
                Save(window);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Equal(Visibility.Collapsed, window.SaveFailureReportButton.Visibility);
        });
    }

    private static void Save(SettingsWindow window)
    {
        try
        {
            typeof(SettingsWindow)
                .GetMethod("OnSave", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    // MARK: - Tray

    [Fact]
    public void TheTraysHelpSubmenuListsBothLinksInOrder()
    {
        var chosen = new List<SupportLink>();

        using var help = TrayController.BuildHelpMenu(chosen.Add);

        Assert.Equal("Help", help.Text);
        Assert.Equal(
            ["Report a bug…", "Report on GitHub…"],
            help.DropDownItems.Cast<Forms.ToolStripItem>().Select(item => item.Text));
        Assert.Equal(
            ["Private · No account", "Public · GitHub account"],
            help.DropDownItems.Cast<Forms.ToolStripItem>().Select(item => item.ToolTipText));
    }

    [Fact]
    public void ChoosingATrayEntryOpensExactlyThatFixedLink()
    {
        var chosen = new List<SupportLink>();
        using var help = TrayController.BuildHelpMenu(chosen.Add);

        foreach (Forms.ToolStripMenuItem item in help.DropDownItems) item.PerformClick();

        Assert.Equal([SupportLinks.BugReport, SupportLinks.GitHubIssue], chosen);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
