using Kytto.Core;

namespace Kytto.Core.Tests;

public sealed class ConfigWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-watcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactConfigIgnoresActivityElsewhereInItsParent()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, ".claude.json");
        using var changed = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(
            [ConfigWatchTarget.File(config)],
            changed.Set,
            TimeSpan.FromMilliseconds(40));

        var unrelated = Path.Combine(_root, "projects", "session.log");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "ordinary activity");

        Assert.False(changed.Wait(TimeSpan.FromMilliseconds(250)));

        File.WriteAllText(config, "{}");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void InstalledPackageTreesRemainRecursive()
    {
        Directory.CreateDirectory(_root);
        using var changed = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(
            [ConfigWatchTarget.Tree(_root)],
            changed.Set,
            TimeSpan.FromMilliseconds(40));

        var manifest = Path.Combine(_root, "plugin", "nested", ".mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");

        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// A client that has never been configured has no directory yet; creating it is
    /// a change worth hearing about, so the owner re-arms and finds the file.
    /// </summary>
    [Fact]
    public void ADirectoryThatAppearsLaterIsNoticed()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, ".cursor", "nested", "mcp.json");
        using var changed = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(
            [ConfigWatchTarget.File(config)],
            changed.Set,
            TimeSpan.FromMilliseconds(40));

        Directory.CreateDirectory(Path.Combine(_root, ".cursor"));
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ARecreatedDirectoryIsWatchedAgain()
    {
        var directory = Path.Combine(_root, ".cursor");
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "mcp.json");
        using var changed = new ManualResetEventSlim();

        using (new ConfigWatcher([ConfigWatchTarget.File(config)], changed.Set, TimeSpan.FromMilliseconds(40)))
        {
            Directory.Delete(directory);
            Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "deleting the directory went unnoticed");
        }

        // The owner re-arms on that change, while the directory is gone.
        changed.Reset();
        using var rearmed = new ConfigWatcher([ConfigWatchTarget.File(config)], changed.Set, TimeSpan.FromMilliseconds(40));
        Directory.CreateDirectory(directory);
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "recreating the directory went unnoticed");

        // And once re-armed again, edits inside it are seen.
        changed.Reset();
        using var again = new ConfigWatcher([ConfigWatchTarget.File(config)], changed.Set, TimeSpan.FromMilliseconds(40));
        Thread.Sleep(100);
        changed.Reset();
        File.WriteAllText(config, "{}");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "an edit after recreation went unnoticed");
    }

    /// <summary>
    /// Re-arming used to dispose the debounce timer, so a change waiting out its
    /// quiet period at that moment was never reported.
    /// </summary>
    [Fact]
    public void AChangeWaitingWhenTheWatcherIsReplacedIsStillReported()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "mcp.json");
        using var first = new ManualResetEventSlim();
        var old = new ConfigWatcher([ConfigWatchTarget.File(config)], first.Set, TimeSpan.FromSeconds(30));

        File.WriteAllText(config, "{}");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!old.HasPendingChange && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(old.HasPendingChange);

        var pending = old.HasPendingChange;
        old.Dispose();
        Assert.False(first.IsSet);

        using var second = new ManualResetEventSlim();
        using var replacement = new ConfigWatcher(
            [ConfigWatchTarget.File(config)], second.Set, TimeSpan.FromMilliseconds(40), startPending: pending);
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(replacement.HasPendingChange);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A delayed watcher handle is not worth hiding the assertion result.
        }
    }
}
