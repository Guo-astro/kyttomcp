using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;

namespace Kytto.Core;

/// <summary>One MCP server a plugin declares, before any launch context is attached.</summary>
internal sealed record PluginDefinition(string Key, ServerFields Fields, string SourceText);

/// <summary>
/// Turns a registered plugin installation into the servers it declares (§4).
/// </summary>
/// <remarks>
/// <para>
/// Reads the package's root <c>.mcp.json</c> and the MCP components its own manifest
/// names, nothing else: nested fixtures, payloads and other versions are never
/// opened. A component the manifest names but that is not a plain file inside the
/// installation is reported and skipped, not followed.
/// </para>
/// <para>
/// The native launch context — plugin root, data directory, project directory,
/// working directory — is attached here and stays on the <see cref="Server"/>. None of
/// it is a property the web layer can see. Placeholders are expanded once, when the
/// server is run, by <see cref="Server.Runnable"/>; the model keeps what the plugin
/// wrote.
/// </para>
/// </remarks>
internal sealed class PluginServerReader(
    string home,
    Func<string, string?> text,
    Action<string> warn)
{
    private readonly Dictionary<string, IReadOnlyList<PluginDefinition>> _definitions =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Server> Read(PluginInstallation installation)
    {
        var servers = new List<Server>();
        foreach (var definition in DefinitionsOf(installation))
        {
            var name = installation.ClientId == ClientId.ClaudeCode
                ? $"plugin:{installation.Name}:{definition.Key}"
                : definition.Key;
            var server = definition.Fields.MakeServer(
                name,
                new Origin.Plugin(
                    installation.ClientId,
                    installation.PluginId,
                    installation.DisplayName,
                    PluginInstallations.Display(installation.Version, 40)),
                isBundled: true,
                sourceText: definition.SourceText);

            var rootVariable = installation.ClientId == ClientId.ClaudeCode
                ? "CLAUDE_PLUGIN_ROOT"
                : "CODEX_PLUGIN_ROOT";
            server.Placeholders[rootVariable] = installation.Root;
            server.LaunchEnvironment[rootVariable] = installation.Root;

            if (installation.ClientId == ClientId.ClaudeCode)
            {
                // Named, never created: Claude Code makes the directory when it runs the
                // plugin, and discovery writes nothing.
                var data = Path.Combine(home, ".claude", "plugins", "data", DataDirectoryName(installation.PluginId));
                server.Placeholders["CLAUDE_PLUGIN_DATA"] = data;
                server.LaunchEnvironment["CLAUDE_PLUGIN_DATA"] = data;
                if (installation.ProjectPath is { } project
                    && ServerFields.AbsoluteDirectory(project) is { } projectDirectory)
                {
                    server.Placeholders["CLAUDE_PROJECT_DIR"] = projectDirectory;
                }
            }

            if (!TryEstablishWorkingDirectory(server, definition.Fields.WorkingDirectory, installation.Root))
            {
                continue;
            }
            servers.Add(server);
        }
        return servers;
    }

    /// <summary>
    /// A registered plugin root supplies the missing relative launch context. The
    /// directory it yields is kept native and is never copied anywhere.
    /// </summary>
    private bool TryEstablishWorkingDirectory(Server server, string? declared, string root)
    {
        // Resolved once, from the known placeholders, and never again: a value that
        // came out of a placeholder is a path, not another template.
        var cwd = server.ExpandingKnownPlaceholders(declared ?? ".");

        if (Path.IsPathFullyQualified(cwd))
        {
            // An absolute directory is the plugin's own choice and is allowed.
            if (ServerFields.AbsoluteDirectory(cwd) is { } absolute)
            {
                server.WorkingDirectory = absolute;
                return true;
            }
        }
        else if (!cwd.Contains("${", StringComparison.Ordinal) && !cwd.Contains(':')
            && !cwd.StartsWith('\\') && !cwd.StartsWith('/')
            && !cwd.Any(char.IsControl))
        {
            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(root, cwd));
            }
            catch (Exception error) when (
                error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warn("A plugin's working directory is not a usable path. Kytto skipped that server.");
                return false;
            }
            if (PluginInstallations.IsInside(resolved, root) && HasNoReparsePointBelow(resolved, root))
            {
                server.WorkingDirectory = Path.TrimEndingDirectorySeparator(resolved);
                return true;
            }
            warn("A plugin's relative working directory escapes its installation. Kytto skipped that server.");
            return false;
        }

        warn("A plugin's working directory needs a substitution Kytto cannot establish. Kytto skipped that server.");
        return false;
    }

    /// <summary>
    /// A directory that exists and sits behind a junction or symlink would make a
    /// lexically contained path point somewhere else.
    /// </summary>
    private static bool HasNoReparsePointBelow(string path, string root)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (current.Length > stop.Length && Path.GetDirectoryName(current) is { } parent)
        {
            try
            {
                if (Directory.Exists(current)
                    && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            current = parent;
        }
        return true;
    }

    /// <summary>Claude Code's per-plugin data directory name for a plugin id.</summary>
    private static string DataDirectoryName(string pluginId) => new(pluginId.Select(character =>
        character is < (char)128 && (char.IsLetterOrDigit(character) || character is '_' or '-')
            ? character
            : '-').ToArray());

    // MARK: - Declared components

    private IReadOnlyList<PluginDefinition> DefinitionsOf(PluginInstallation installation)
    {
        if (_definitions.TryGetValue(installation.Root, out var known)) return known;
        var found = new Dictionary<string, PluginDefinition>(StringComparer.Ordinal);

        void ReadMap(JsonNode? node, JsonDocument document)
        {
            foreach (var member in node?.Members ?? [])
            {
                if (member.Value.Members is null) continue;
                found[member.Key] = new PluginDefinition(
                    member.Key,
                    ServerFields.FromJson(member.Value),
                    document.Slice(member.Value.Span));
            }
        }

        void ReadFile(string path)
        {
            if (text(path) is not { } source) return;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(source);
            }
            catch (JsonParseException)
            {
                warn("An active plugin's declared MCP configuration could not be parsed and was skipped.");
                return;
            }
            ReadMap(document.Root["mcpServers"] ?? document.Root, document);
        }

        // The root .mcp.json first, then what the manifest declares, in order — a later
        // definition of the same name replaces an earlier one, as in the client.
        if (PluginInstallations.ContainedFile("./.mcp.json", installation.Root) is { } defaults)
        {
            ReadFile(defaults);
        }
        if (installation.Manifest is { } manifest && manifest.Root["mcpServers"] is { } components)
        {
            foreach (var shape in components.Elements ?? [components])
            {
                if (shape.StringValue is { } declared)
                {
                    if (PluginInstallations.ContainedFile(declared, installation.Root) is not { } file)
                    {
                        warn(
                            "A plugin declares an unsupported or external MCP component. Kytto " +
                            "reads only JSON files contained in the registered plugin directory.");
                        continue;
                    }
                    ReadFile(file);
                }
                else if (shape.Members is not null)
                {
                    ReadMap(shape["mcpServers"] ?? shape, manifest);
                }
            }
        }

        var ordered = found.Values.OrderBy(definition => definition.Key, StringComparer.Ordinal).ToArray();
        _definitions[installation.Root] = ordered;
        return ordered;
    }
}
