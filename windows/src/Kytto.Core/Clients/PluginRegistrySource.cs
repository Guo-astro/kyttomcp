namespace Kytto.Core.Clients;

/// <summary>
/// Where a client records which plugins are installed and switched on (§4).
/// </summary>
/// <remarks>
/// <para>
/// Client-specific registration locations belong beside the client registry, like
/// every other path: the files named here move between client releases and a fix has
/// to stay a one-file change.
/// </para>
/// <para>
/// Claude Code's project and local scopes are not part of the source. They come from
/// registrations naming a project path, which discovery turns into read-only
/// workspace scopes — nothing here looks for a project on disk.
/// </para>
/// </remarks>
public abstract record PluginRegistrySource
{
    /// <summary>The file that says a plugin is registered at all.</summary>
    public abstract PlatformPath RegistrationFile { get; }

    /// <summary>
    /// Claude Code: <c>installed_plugins.json</c> (format version 2) lists each
    /// installation with its scope, and <c>enabledPlugins</c> in the settings files
    /// says whether it is switched on.
    /// </summary>
    public sealed record ClaudeCode(PlatformPath Installed, PlatformPath Settings) : PluginRegistrySource
    {
        public override PlatformPath RegistrationFile => Installed;
    }

    /// <summary>
    /// Codex: <c>[plugins."name@marketplace"]</c> with <c>enabled = true</c> in the
    /// client's own config, and the package under the plugin cache.
    /// </summary>
    public sealed record Codex(PlatformPath Config, PlatformPath Cache) : PluginRegistrySource
    {
        public override PlatformPath RegistrationFile => Config;
    }

    public static ClaudeCode ClaudeCodeDefault { get; } = new(
        Installed: new PlatformPath(
            Darwin: "~/.claude/plugins/installed_plugins.json",
            Windows: @"%USERPROFILE%\.claude\plugins\installed_plugins.json"),
        Settings: new PlatformPath(
            Darwin: "~/.claude/settings.json",
            Windows: @"%USERPROFILE%\.claude\settings.json"));
}
