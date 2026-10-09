namespace Kytto.Core;

/// <summary>A file or directory tree whose changes can affect discovery.</summary>
public sealed record ConfigWatchTarget(string Path, bool IncludeSubdirectories)
{
    public static ConfigWatchTarget File(string path) => new(path, false);

    public static ConfigWatchTarget Tree(string path) => new(path, true);
}

/// <summary>
/// Notices when a watched config or installed-package tree changes (§6.4).
/// </summary>
/// <remarks>
/// <para>
/// File targets are watched through their parent directory, because a config is
/// replaced by a rename — every atomic write, including Kytto's own, is a new file
/// moved over the old one. The watcher filter still names that one file, so activity
/// elsewhere in a broad parent such as the user profile cannot refresh the app.
/// </para>
/// <para>
/// Coalesced, because one save produces several notifications and a client app
/// rewriting its own config produces a burst of them. The callback is for
/// re-reading state, and re-reading twice for one change is waste the user pays
/// for in a stutter.
/// </para>
/// <para>
/// A directory that is not there — a client not configured yet, or a folder
/// deleted and created again — is watched for from the nearest ancestor that is,
/// filtered to the one name on the way down. Its appearance is a change like any
/// other; the owner re-arms on it and the new watcher finds the directory.
/// </para>
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();
    private readonly Action _onChange;
    private readonly TimeSpan _quietPeriod;
    private Timer? _timer;
    private bool _pending;
    private bool _disposed;

    /// <param name="startPending">
    /// Report a change once the quiet period passes even if nothing else happens:
    /// what a re-armed watcher is told when the one it replaces had a change
    /// waiting, so that change is reported rather than dropped with the old timer.
    /// </param>
    public ConfigWatcher(
        IEnumerable<ConfigWatchTarget> targets,
        Action onChange,
        TimeSpan? quietPeriod = null,
        bool startPending = false)
    {
        _onChange = onChange;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(300);

        var watched = new HashSet<(string Directory, string Filter, bool Subdirectories)>();
        foreach (var target in targets)
        {
            var fullPath = Path.GetFullPath(target.Path);
            var directory = target.IncludeSubdirectories
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) continue;

            // The directory itself: watched from its parent, so deleting it and
            // creating it again is noticed — a watcher on a directory that is gone
            // reports nothing more.
            WatchForDirectory(directory, watched);

            if (!Directory.Exists(directory)) continue;
            Watch(
                directory,
                target.IncludeSubdirectories ? "*" : Path.GetFileName(fullPath),
                target.IncludeSubdirectories,
                // Size and last-write cover an edit in place; file name covers the
                // rename an atomic write ends with.
                NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                watched);
        }

        if (startPending) Schedule();
    }

    /// <summary>Whether a change has been seen and not yet reported.</summary>
    public bool HasPendingChange
    {
        get
        {
            lock (_lock) return _pending;
        }
    }

    /// <summary>
    /// Watches the nearest existing ancestor of <paramref name="directory"/> for the
    /// next name on the way down to it.
    /// </summary>
    private void WatchForDirectory(string directory, HashSet<(string, string, bool)> watched)
    {
        var child = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(child);
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            child = parent;
            parent = Path.GetDirectoryName(child);
        }
        if (string.IsNullOrEmpty(parent)) return;
        Watch(parent, Path.GetFileName(child), false, NotifyFilters.DirectoryName, watched);
    }

    private void Watch(
        string directory,
        string filter,
        bool subdirectories,
        NotifyFilters notify,
        HashSet<(string, string, bool)> watched)
    {
        if (!watched.Add((directory.ToUpperInvariant(), filter.ToUpperInvariant(), subdirectories))) return;
        try
        {
            var watcher = new FileSystemWatcher(directory, filter)
            {
                NotifyFilter = notify,
                IncludeSubdirectories = subdirectories,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            // A watcher that overflows its buffer stops reporting; re-reading
            // everything is exactly the right response to "something happened and I
            // lost track of what".
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be watched is not a reason to fail the others,
            // or the app. The user can still refresh by hand.
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs args) => Schedule();

    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending = true;
            _timer?.Dispose();
            _timer = new Timer(_ => Fire(), null, _quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_lock)
        {
            if (_disposed || !_pending) return;
            _pending = false;
        }
        _onChange();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
