using System.Diagnostics;
using System.Text.Json;
using Kytto.Core.Clients;

namespace Kytto.Core.Tests.Support;

/// <summary>
/// Writes the files a client leaves behind when it has registered plugins, into a
/// throwaway profile (§4). Every path lives under <see cref="ToggleHarness.Home"/>.
/// </summary>
public static class PluginFixtures
{
    public const string ClaudeCache = @".claude\plugins\cache\example";

    /// <summary>The full path of a profile-relative one.</summary>
    public static string Full(this ToggleHarness harness, string relative) =>
        Path.Combine(harness.Home, relative);

    /// <summary>One entry of <c>installed_plugins.json</c>, as Claude Code writes it.</summary>
    public static Dictionary<string, object?> Entry(
        string scope, string installPath, string? version = null, string? projectPath = null)
    {
        var entry = new Dictionary<string, object?> { ["scope"] = scope, ["installPath"] = installPath };
        if (version is not null) entry["version"] = version;
        if (projectPath is not null) entry["projectPath"] = projectPath;
        return entry;
    }

    public static void RegisterClaude(
        this ToggleHarness harness,
        string id,
        params Dictionary<string, object?>[] entries) =>
        harness.RegisterClaude(new Dictionary<string, Dictionary<string, object?>[]> { [id] = entries });

    public static void RegisterClaude(
        this ToggleHarness harness,
        Dictionary<string, Dictionary<string, object?>[]> plugins) =>
        harness.Write(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["version"] = 2, ["plugins"] = plugins }),
            @".claude\plugins\installed_plugins.json");

    /// <summary>The user's <c>enabledPlugins</c>, in <c>~\.claude\settings.json</c>.</summary>
    public static void EnableClaudePlugins(this ToggleHarness harness, string id, bool enabled) =>
        harness.Write(
            JsonSerializer.Serialize(new { enabledPlugins = new Dictionary<string, bool> { [id] = enabled } }),
            @".claude\settings.json");

    public static void EnableCodexPlugin(this ToggleHarness harness, string id, bool enabled, string extra = "") =>
        harness.Write(
            $"[plugins.\"{id}\"]\nenabled = {(enabled ? "true" : "false")}\n{extra}",
            @".codex\config.toml");

    /// <summary>Every file under a directory, relative, so "nothing was created" can be asserted.</summary>
    public static string[] Snapshot(string root) =>
        !Directory.Exists(root)
            ? []
            : Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>A directory junction, which needs no privilege and behaves like a symlink to the reader.</summary>
    public static void MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction was not created");
    }
}
