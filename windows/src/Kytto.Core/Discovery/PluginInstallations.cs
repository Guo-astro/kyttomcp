using System.Globalization;
using System.Text.RegularExpressions;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Toml;

namespace Kytto.Core;

/// <summary>One installed plugin that its client has registered and switched on.</summary>
internal sealed record PluginInstallation(
    ClientId ClientId,
    string PluginId,
    /// <summary>The installation directory: absolute, lexically normalised, no trailing separator.</summary>
    string Root,
    JsonDocument? Manifest,
    string? Version,
    /// <summary>The project this evaluation is for, as registered; null for the global scope.</summary>
    string? ProjectPath)
{
    public string Name =>
        PluginInstallations.Display(Manifest?.Root["name"]?.StringValue)
        ?? PluginInstallations.Display(PluginId.Split('@')[0])
        ?? "plugin";

    public string DisplayName =>
        PluginInstallations.Display(Manifest?.Root["displayName"]?.StringValue)
        ?? PluginInstallations.Display(Manifest?.Root["interface"]?["displayName"]?.StringValue)
        ?? Name;
}

internal sealed class PluginInstallationResult
{
    public List<PluginInstallation> Packages { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>One plugin id as <c>installed_plugins.json</c> lists it.</summary>
internal sealed record ClaudePluginRegistration(string Id, IReadOnlyList<ClaudePluginEntry> Entries);

internal sealed record ClaudePluginEntry(
    string? Scope,
    string? InstallPath,
    string? ProjectPath,
    string? Version);

/// <summary>
/// The parsed installed-plugin registry of Claude Code, read once per discovery pass.
/// </summary>
internal sealed class ClaudePluginRegistry
{
    public IReadOnlyList<ClaudePluginRegistration> Plugins { get; init; } = [];

    /// <summary>
    /// Project paths that a project- or local-scope installation names, exactly as the
    /// registry spells them. They are explicit registrations: nothing is looked for on
    /// disk to find more (§4).
    /// </summary>
    public IReadOnlyList<string> ProjectPaths { get; init; } = [];
}

/// <summary>
/// Reads which plugins a client has registered and switched on, and where they are
/// installed (§4).
/// </summary>
/// <remarks>
/// Strictly read-only: no network, no process, no directory creation, and no walk of
/// the plugin cache. A plugin is active because a registration says so, never because
/// a directory exists — stale versions, uninstalled leftovers and nested test fixtures
/// are all just directories.
/// </remarks>
internal static class PluginInstallations
{
    /// <summary>Largest plugin or settings file read; anything bigger is not a config.</summary>
    internal const long MaxFileBytes = 2 * 1024 * 1024;

    // MARK: - Claude Code

    /// <summary>
    /// Parses <c>installed_plugins.json</c>. Null when the file is absent or unusable;
    /// the reason, when there is one worth saying, goes to <paramref name="warnings"/>.
    /// </summary>
    public static ClaudePluginRegistry? LoadClaudeRegistry(
        string installedPath,
        Func<string, string?> text,
        ICollection<string> warnings)
    {
        if (text(installedPath) is not { } source) return null;

        JsonDocument? registry = null;
        try
        {
            registry = JsonDocument.Parse(source);
        }
        catch (JsonParseException)
        {
            // Reported below, with the same sentence as an unsupported version.
        }
        if (registry is null
            || registry.Root["version"]?.NumberValue != 2
            || registry.Root["plugins"]?.Members is not { } plugins)
        {
            warnings.Add(
                "Claude Code plugin registrations are unreadable or use an unsupported " +
                "format. Cached packages were not scanned.");
            return null;
        }

        var registrations = plugins
            .OrderBy(member => member.Key, StringComparer.Ordinal)
            .Select(member => new ClaudePluginRegistration(
                member.Key,
                (member.Value.Elements ?? []).Select(entry => new ClaudePluginEntry(
                    entry["scope"]?.StringValue,
                    entry["installPath"]?.StringValue,
                    entry["projectPath"]?.StringValue,
                    entry["version"]?.StringValue)).ToArray()))
            .ToArray();

        var projects = registrations
            .SelectMany(registration => registration.Entries)
            .Where(entry => entry.Scope is "project" or "local"
                && entry.ProjectPath is not null
                && IsAbsoluteWindowsPath(entry.ProjectPath))
            .Select(entry => entry.ProjectPath!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ClaudePluginRegistry { Plugins = registrations, ProjectPaths = projects };
    }

    /// <summary>
    /// The plugins in effect for one scope: the user's, or a project's with that
    /// project's settings layered over them.
    /// </summary>
    /// <param name="projectPath">Null for the global Claude Code scope.</param>
    public static PluginInstallationResult ReadClaudeCode(
        ClaudePluginRegistry registry,
        string settingsPath,
        string? projectPath,
        Func<string, string?> text)
    {
        var result = new PluginInstallationResult();

        // Precedence, lowest first: user, then project, then local. A later file that
        // says nothing about a plugin leaves the earlier answer standing.
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        var settingsFiles = new List<string> { settingsPath };
        if (projectPath is not null)
        {
            settingsFiles.Add(Path.Combine(projectPath, ".claude", "settings.json"));
            settingsFiles.Add(Path.Combine(projectPath, ".claude", "settings.local.json"));
        }
        foreach (var file in settingsFiles)
        {
            if (text(file) is not { } source) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(source);
            }
            catch (JsonParseException)
            {
                result.Warnings.Add(
                    "Claude Code plugin settings could not be read. Plugin discovery was " +
                    "skipped rather than guessing enablement.");
                return result;
            }
            foreach (var entry in document.Root["enabledPlugins"]?.Members ?? [])
            {
                if (entry.Value.BoolValue is { } value) enabled[entry.Key] = value;
            }
        }

        foreach (var plugin in registry.Plugins)
        {
            if (enabled.TryGetValue(plugin.Id, out var isEnabled) && !isEnabled) continue;

            var matching = plugin.Entries.Where(entry =>
                entry.Scope == "user"
                || (projectPath is not null
                    && entry.Scope is "project" or "local"
                    && entry.ProjectPath is not null
                    && SameProject(entry.ProjectPath, projectPath))).ToArray();

            // The most specific installation wins. More than one at that scope is
            // ambiguous: timestamps and version sorting cannot prove which copy the
            // client will load.
            if (matching.Length == 0) continue;
            var highest = matching.Max(Priority);
            var candidates = matching.Where(entry => Priority(entry) == highest).ToArray();
            string? root = null;
            if (candidates.Length == 1
                && candidates[0].InstallPath is { } installPath
                && IsAbsoluteWindowsPath(installPath))
            {
                root = PlainDirectory(installPath);
            }
            if (root is null)
            {
                result.Warnings.Add(
                    $"Claude Code plugin \"{Display(plugin.Id) ?? "?"}\" has a missing or " +
                    "ambiguous registered installation; its cached copies were not scanned.");
                continue;
            }

            var manifestSource = text(Path.Combine(root, ".claude-plugin", "plugin.json"));
            JsonDocument? manifest = null;
            if (manifestSource is not null)
            {
                try
                {
                    manifest = JsonDocument.Parse(manifestSource);
                }
                catch (JsonParseException)
                {
                    result.Warnings.Add(
                        "An installed Claude Code plugin has an unreadable manifest and was skipped.");
                    continue;
                }
            }

            // A plugin that says it is off unless asked, and was not asked.
            if (!enabled.ContainsKey(plugin.Id)
                && manifest?.Root["defaultEnabled"]?.BoolValue == false)
            {
                continue;
            }

            result.Packages.Add(new PluginInstallation(
                ClientId.ClaudeCode,
                plugin.Id,
                root,
                manifest,
                candidates[0].Version ?? manifest?.Root["version"]?.StringValue,
                projectPath));
        }
        return result;

        static int Priority(ClaudePluginEntry entry) => entry.Scope switch
        {
            "user" => 0,
            "project" => 1,
            "local" => 2,
            _ => -1,
        };
    }

    // MARK: - Codex

    /// <summary>
    /// Plugins Codex's own config registers with <c>enabled = true</c>, found in the
    /// cache at their registered version or the only one installed.
    /// </summary>
    public static PluginInstallationResult ReadCodex(
        string configPath,
        string cachePath,
        Func<string, string?> text)
    {
        var result = new PluginInstallationResult();
        // An unreadable Codex config is already reported by the server map that shares
        // it; saying so twice would be noise, and it means nothing is registered.
        if (text(configPath) is not { } source) return result;
        TomlDocument document;
        try
        {
            document = TomlDocument.Parse(source);
        }
        catch (TomlParseException)
        {
            return result;
        }

        var registered = document.Tables
            .Where(table => table.Path.Count == 2 && table.Path[0] == "plugins" && !table.IsArrayElement)
            .GroupBy(table => table.Path[1], StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in registered)
        {
            var id = group.Key;
            if (group.Count() > 1)
            {
                // Two tables for one plugin: which `enabled` wins is the client's
                // business, and guessing would show a plugin that may be off.
                result.Warnings.Add(
                    $"Codex plugin \"{Display(id) ?? "?"}\" is registered more than once in the " +
                    "config. Kytto skipped it because the effective registration is uncertain.");
                continue;
            }
            var table = group.Single();
            if (table.Value("enabled")?.BoolValue != true) continue;

            var parts = id.Split('@');
            if (parts.Length != 2 || !parts.All(SafeComponent)) continue;
            var packageRoot = PlainDirectory(Path.Combine(cachePath, parts[1], parts[0]));
            if (packageRoot is null) continue;

            IReadOnlyList<string> versions;
            try
            {
                // One level, by name: the cache is never walked.
                versions = Directory.EnumerateDirectories(packageRoot)
                    .Where(directory => !IsReparsePoint(directory))
                    .Where(directory => File.Exists(Path.Combine(directory, ".codex-plugin", "plugin.json")))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (versions.Count == 0) continue;

            // A cache is not an installation registry. Only a version the registration
            // names, or the one version there is, establishes which package is active
            // without asking the client to execute anything.
            var chosen = versions.Count == 1
                ? versions[0]
                : ChooseRegisteredVersion(table.Value("version")?.StringValue, versions, text);
            if (chosen is null)
            {
                result.Warnings.Add(
                    $"Enabled Codex plugin \"{Display(id) ?? "?"}\" has several cached versions " +
                    "or an unreadable manifest. Kytto skipped it because the active version " +
                    "is uncertain.");
                continue;
            }

            JsonDocument? manifest = null;
            try
            {
                if (text(Path.Combine(chosen, ".codex-plugin", "plugin.json")) is { } manifestSource)
                {
                    manifest = JsonDocument.Parse(manifestSource);
                }
            }
            catch (JsonParseException)
            {
                // Handled with the missing case just below.
            }
            if (manifest is null)
            {
                result.Warnings.Add(
                    $"Enabled Codex plugin \"{Display(id) ?? "?"}\" has several cached versions " +
                    "or an unreadable manifest. Kytto skipped it because the active version " +
                    "is uncertain.");
                continue;
            }

            result.Packages.Add(new PluginInstallation(
                ClientId.Codex,
                id,
                Path.TrimEndingDirectorySeparator(chosen),
                manifest,
                manifest.Root["version"]?.StringValue ?? Path.GetFileName(chosen),
                ProjectPath: null));
        }
        return result;
    }

    private static string? ChooseRegisteredVersion(
        string? registeredVersion,
        IReadOnlyList<string> versions,
        Func<string, string?> text)
    {
        if (string.IsNullOrWhiteSpace(registeredVersion)) return null;
        var matches = versions.Where(directory =>
        {
            if (string.Equals(Path.GetFileName(directory), registeredVersion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            try
            {
                return text(Path.Combine(directory, ".codex-plugin", "plugin.json")) is { } source
                    && JsonDocument.Parse(source).Root["version"]?.StringValue == registeredVersion;
            }
            catch (JsonParseException)
            {
                return false;
            }
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    // MARK: - Components

    /// <summary>
    /// A file the plugin names, if it really is inside the plugin and an ordinary file.
    /// </summary>
    /// <remarks>
    /// Declared paths only, spelled <c>./relative.json</c>. No <c>..</c>, no drive or
    /// stream syntax, no reserved device names and no reparse point anywhere on the way
    /// down — a junction or symlink inside a plugin must not be able to point the reader
    /// at a file the plugin does not own.
    /// </remarks>
    internal static string? ContainedFile(string declared, string root)
    {
        if (!declared.StartsWith("./", StringComparison.Ordinal)
            && !declared.StartsWith(@".\", StringComparison.Ordinal))
        {
            return null;
        }
        if (!declared.EndsWith(".json", StringComparison.Ordinal)) return null;

        var segments = declared[2..].Split('/', '\\');
        if (segments.Length == 0 || !segments.All(SafeComponent)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!IsInside(full, root)) return null;

        var current = root;
        foreach (var segment in segments[..^1])
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) || IsReparsePoint(current)) return null;
        }
        if (!File.Exists(full) || IsReparsePoint(full)) return null;
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes) return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return full;
    }

    /// <summary>
    /// A directory strictly inside <paramref name="root"/>, or the root itself.
    /// </summary>
    internal static bool IsInside(string path, string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalized, parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Paths

    /// <summary>A directory that exists and is not itself a symlink or junction.</summary>
    internal static string? PlainDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new DirectoryInfo(full);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or PathTooLongException)
        {
            // Not being able to tell is not a reason to follow it.
            return true;
        }
    }

    /// <summary>
    /// A drive-letter or UNC path, written plainly. Rooted-only (<c>\work</c>) and
    /// drive-relative (<c>C:work</c>) spellings are not absolute on Windows, and device
    /// paths (<c>\\?\</c>, <c>\\.\</c>) are not something a registration should name.
    /// </summary>
    internal static bool IsAbsoluteWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return false;
        }
        if (!Path.IsPathFullyQualified(path)) return false;
        return !path.Split('/', '\\').Contains("..", StringComparer.Ordinal);
    }

    /// <summary>
    /// What two spellings of one project path have in common, for comparison only.
    /// </summary>
    /// <remarks>
    /// Claude Code writes <c>C:/Users/you/app</c> in one file and <c>c:\Users\you\app\</c>
    /// in another, and Windows paths are case-insensitive, so the project those name is
    /// one project. The result identifies nothing on the wire — the id of a scope is
    /// still derived from the exact path it was first spelled with.
    /// </remarks>
    internal static string ProjectKey(string path)
    {
        var normalized = path.Replace('/', '\\');
        var trimmed = normalized.TrimEnd('\\');
        // "C:\" must stay a root, and "\\server\share" must stay a UNC path.
        if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += '\\';
        return trimmed.ToUpperInvariant();
    }

    internal static bool SameProject(string left, string right) =>
        string.Equals(ProjectKey(left), ProjectKey(right), StringComparison.Ordinal);

    private static readonly Regex ReservedDeviceName = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// One path component that cannot be anything but an ordinary name.
    /// </summary>
    internal static bool SafeComponent(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or "..") return false;
        if (value.Any(char.IsControl) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        // Windows drops a trailing dot or space when it opens a name, so `a.json.` and
        // `a.json` are one file.
        if (value.EndsWith('.') || value.EndsWith(' ')) return false;
        return !ReservedDeviceName.IsMatch(value);
    }

    /// <summary>
    /// Text a file supplied, made fit to show: no control or formatting characters (a
    /// right-to-left override reorders what a person reads) and bounded in length.
    /// </summary>
    internal static string? Display(string? value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(character =>
            !char.IsControl(character)
            && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }
}
