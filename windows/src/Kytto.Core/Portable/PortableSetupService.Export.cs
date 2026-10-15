using System.Text.RegularExpressions;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;
using Kytto.Core.Toml;

namespace Kytto.Core.Portable;

public sealed partial class PortableSetupService
{
    // MARK: - Export

    /// <summary>
    /// Writes the setup to a file the user chose. Never looks in the secret store.
    /// </summary>
    /// <remarks>
    /// Only writable, built-in client definitions and profiles are exported: not
    /// custom or workspace sources, not installed plugins, not extension bundles —
    /// those are installed software or somebody else's file, not this user's setup.
    /// </remarks>
    public PortableSetupSummary Export(
        string file,
        IReadOnlyList<Server> servers,
        bool includeReviewedContracts = false)
    {
        lock (MutationCoordinator.Gate)
        {
            RefuseProtectedExportLocation(file);

            var requirements = new List<PortableRequirement>();
            var exported = new List<PortableServer>();
            var omitted = 0;
            var unportable = 0;

            foreach (var server in servers.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
            {
                var copies = new Dictionary<ClientKey, ClientDefinition>(server.DefinitionsByClient);
                if (copies.Count == 0 && server.Origin is Origin.ConfigFile configFile)
                {
                    copies[configFile.Client] = ClientDefinition.From(server);
                }

                foreach (var (key, definition) in copies.OrderBy(pair => pair.Key.Raw(), StringComparer.Ordinal))
                {
                    var editable = key.BuiltIn is { } builtIn
                        ? _descriptors.FirstOrDefault(descriptor => descriptor.Id == builtIn)?.EditableServerMap
                        : null;
                    if (key.BuiltIn is not { } clientId ||
                        editable is null ||
                        definition.IsBundled ||
                        definition.IsReadOnly ||
                        definition.Origin is not Origin.ConfigFile ||
                        server.EnabledIn.GetValueOrDefault(key, Enablement.Absent) == Enablement.Absent)
                    {
                        omitted++;
                        continue;
                    }

                    var local = new List<PortableRequirement>();
                    var candidate = Describe(server, clientId, definition, editable, local);
                    if (candidate is null || !PortableSetupValidator.IsPortable(candidate, local))
                    {
                        unportable++;
                        continue;
                    }
                    exported.Add(candidate);
                    requirements.AddRange(local);
                }
            }

            var names = exported.Select(server => Server.Identity(server.Name)).ToHashSet(StringComparer.Ordinal);
            var profiles = new List<PortableProfile>();
            foreach (var profile in ReadProfiles())
            {
                var item = new PortableProfile(
                    profile.Name,
                    profile.ServerIDs.Where(names.Contains).ToArray(),
                    profile.TokenBudget);
                if (IsPortableProfile(item)) profiles.Add(item);
                else unportable++;
            }

            var contracts = new List<PortableContractReference>();
            if (includeReviewedContracts)
            {
                IReadOnlyDictionary<string, Health.ServerMetadata> metadata;
                try
                {
                    metadata = MetadataStore.ReadStrict(_paths);
                }
                catch (InvalidDataException error)
                {
                    throw PortableSetupException.StoreUnreadable("measurements", error);
                }

                foreach (var (id, entry) in metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!names.Contains(id) || entry.ContractTools is not { } tools) continue;
                    var reference = new PortableContractReference(id, DigestsOf(tools));
                    if (reference.ToolDigests.Count <= 2048 &&
                        reference.ToolDigests.Keys.All(tool => PortableSetupValidator.ValidText(tool)))
                    {
                        contracts.Add(reference);
                    }
                }
            }

            var setup = new PortableSetup(
                PortableSetup.FormatName,
                PortableSetup.CurrentVersion,
                exported,
                profiles,
                requirements,
                contracts);
            PortableSetupValidator.Validate(setup);

            var data = PortableSetupSerializer.Write(setup);
            if (data.Length > MaximumFileBytes) throw PortableSetupException.Invalid("file is too large");
            AtomicWriter.Write(data, file);
            return Summary(setup, omitted, unportable);
        }
    }

    private static bool IsPortableProfile(PortableProfile profile) =>
        PortableSetupValidator.ValidText(profile.Name, 320) &&
        new System.Globalization.StringInfo(profile.Name).LengthInTextElements <= 80 &&
        profile.Name == profile.Name.Trim();

    /// <summary>
    /// One client's copy of a server, as the format spells it, with every value that
    /// is not provably public turned into a required input.
    /// </summary>
    /// <returns>Null when the copy has nothing to export (no command, no URL).</returns>
    private static PortableServer? Describe(
        Server server,
        ClientId clientId,
        ClientDefinition definition,
        ConfigSource.ServerMap editable,
        List<PortableRequirement> requirements)
    {
        var label = $"{server.Name} ({clientId.Raw()})";

        PortableValue Required(string what, PortableRequirementKind kind)
        {
            var id = Guid.NewGuid().ToString("D");
            requirements.Add(new PortableRequirement(id, $"{label}: {what}", kind));
            return PortableValue.Required(id);
        }

        var isRemote = definition.Transport != Transport.Stdio;
        var source = definition.DefinitionSource;

        // Every URL is a required input: credentials hide in userinfo, query,
        // fragments and path segments alike.
        if (isRemote)
        {
            if (string.IsNullOrEmpty(definition.Url)) return null;

            var headers = new Dictionary<string, PortableValue>(StringComparer.Ordinal);
            foreach (var name in HeaderNames(source, editable.Format))
            {
                headers[name] = Required($"header {name}", PortableRequirementKind.Secret);
            }
            var environment = new Dictionary<string, PortableValue>(StringComparer.Ordinal);
            foreach (var entry in definition.Env)
            {
                environment[entry.Key] = Required($"environment {entry.Key}", PortableRequirementKind.Secret);
            }
            return new PortableServer(
                server.Name, clientId, WasEnabled(server, clientId), definition.Transport,
                Command: null, Args: [], environment, headers,
                Url: Required("remote URL", PortableRequirementKind.Secret),
                WorkingDirectory: null);
        }

        if (string.IsNullOrEmpty(definition.Command)) return null;

        // Only exact, known launcher names are public. A generic argument may itself
        // be a bearer token even when no flag says "token".
        var launcher = SafeLauncher(definition.Command);
        var command = launcher is not null
            ? PortableValue.FromLiteral(launcher)
            : Required("command", PortableRequirementKind.Path);

        // A package stays readable only when the launcher is: the importer decides it
        // from the file, which does not say what an unspoken command was.
        var packageIndex = launcher is null
            ? null
            : PortablePackages.SafePackageIndex(launcher, definition.Args, PortableSetupValidator.SafeArguments);
        var args = definition.Args
            .Select((argument, index) =>
                PortableSetupValidator.SafeArguments.Contains(argument) || index == packageIndex
                    ? PortableValue.FromLiteral(argument)
                    : Required(
                        $"argument {index + 1}",
                        IsPath(argument) ? PortableRequirementKind.Path : PortableRequirementKind.Value))
            .ToArray();

        var env = new Dictionary<string, PortableValue>(StringComparer.Ordinal);
        foreach (var entry in definition.Env)
        {
            env[entry.Key] = Required($"environment {entry.Key}", PortableRequirementKind.Secret);
        }

        var cwd = WorkingDirectory(source, editable.Format);
        return new PortableServer(
            server.Name, clientId, WasEnabled(server, clientId), definition.Transport,
            command, args, env, new Dictionary<string, PortableValue>(StringComparer.Ordinal),
            Url: null,
            WorkingDirectory: cwd is null ? null : Required("working directory", PortableRequirementKind.Path));
    }

    private static bool WasEnabled(Server server, ClientId clientId) =>
        server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) == Enablement.Enabled;

    /// <summary>
    /// The launcher as the format spells it, or null when it is not one the format allows.
    /// </summary>
    /// <remarks>
    /// <c>npx.cmd</c> is how a Windows config says <c>npx</c>, and the file has to be
    /// readable on a Mac, so the bare name is what travels. A path, or a name with
    /// anything else around it, is not a launcher and becomes an input.
    /// </remarks>
    private static string? SafeLauncher(string command)
    {
        if (PortableSetupValidator.SafeCommands.Contains(command)) return command;

        foreach (var suffix in (string[])[".cmd", ".exe"])
        {
            if (!command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var stem = command[..^suffix.Length].ToLowerInvariant();
            if (PortableSetupValidator.SafeCommands.Contains(stem)) return stem;
        }
        return null;
    }

    private static readonly Regex DriveRooted = new(
        @"\A([A-Za-z]:[\\/]|%[A-Za-z_][A-Za-z0-9_]*%[\\/])", RegexOptions.CultureInvariant);

    /// <summary>Whether an argument is a file-system path rather than another kind of value.</summary>
    private static bool IsPath(string value) =>
        value.StartsWith('/') || value.StartsWith("~/", StringComparison.Ordinal) ||
        value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal) ||
        value.StartsWith(@"~\", StringComparison.Ordinal) || value.StartsWith(@".\", StringComparison.Ordinal) ||
        value.StartsWith(@"..\", StringComparison.Ordinal) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
        DriveRooted.IsMatch(value);

    // MARK: - What a definition's own text says

    /// <summary>
    /// The header names a definition carries, in whatever shape its client spells them.
    /// Values are never read.
    /// </summary>
    private static IReadOnlyList<string> HeaderNames(string text, ConfigFormat format)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] tables = ["headers", "http_headers", "env_http_headers"];

        if (format == ConfigFormat.Json)
        {
            try
            {
                var root = JsonDocument.Parse(text).Root;
                foreach (var key in tables) names.UnionWith(root[key]?.Keys ?? []);
                if (root["bearer_token_env_var"]?.StringValue is not null) NormalizeAuthorization(names);
            }
            catch (JsonParseException)
            {
                // A definition that does not parse names no headers; its values are
                // not exported either way.
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }

        if (TomlFields(text) is { } fields)
        {
            foreach (var key in tables.Skip(1))
            {
                if (fields.Pairs.FirstOrDefault(pair => pair.Name == key)?.Value.InlinePairs is { } inline)
                {
                    names.UnionWith(inline.Select(pair => pair.Name).OfType<string>());
                }
                // `http_headers.X-Key = "…"` on the server's own table.
                names.UnionWith(fields.Pairs
                    .Where(pair => pair.Key.Count == 2 && pair.Key[0] == key)
                    .Select(pair => pair.Key[1]));
                if (fields.Table is { } table)
                {
                    foreach (var child in fields.Document.Tables.Where(candidate =>
                                 candidate.Path.SequenceEqual([.. table.Path, key])))
                    {
                        names.UnionWith(child.Pairs.Select(pair => pair.Name).OfType<string>());
                    }
                }
            }
            if (fields.Pairs.FirstOrDefault(pair => pair.Name == "bearer_token_env_var")?.Value.StringValue is not null)
            {
                NormalizeAuthorization(names);
            }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A bearer token travels as an Authorization header, whatever the client calls it.</summary>
    private static void NormalizeAuthorization(HashSet<string> names)
    {
        names.RemoveWhere(name => string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase));
        names.Add("Authorization");
    }

    private static string? WorkingDirectory(string text, ConfigFormat format)
    {
        if (format == ConfigFormat.Json)
        {
            try
            {
                return JsonDocument.Parse(text).Root["cwd"]?.StringValue;
            }
            catch (JsonParseException)
            {
                return null;
            }
        }
        return TomlFields(text)?.Pairs.FirstOrDefault(pair => pair.Name == "cwd")?.Value.StringValue;
    }

    private sealed record TomlDefinition(TomlDocument Document, TomlTable? Table, IReadOnlyList<TomlPair> Pairs);

    /// <summary>
    /// Discovery retains a complete table block, or the single <c>name = {…}</c> pair
    /// for an inline Codex server. Both need the same whitelist.
    /// </summary>
    private static TomlDefinition? TomlFields(string text)
    {
        TomlDocument document;
        try
        {
            document = TomlDocument.Parse(text);
        }
        catch (TomlParseException)
        {
            return null;
        }

        if (document.Tables.FirstOrDefault(table => table.Value("url") is not null || table.Value("command") is not null)
            is { } own)
        {
            return new TomlDefinition(document, own, own.Pairs);
        }
        foreach (var table in document.Tables)
        {
            foreach (var pair in table.Pairs)
            {
                if (pair.Value.InlinePairs is { } fields &&
                    fields.Any(field => field.Name is "url" or "command"))
                {
                    return new TomlDefinition(document, null, fields);
                }
            }
        }
        return null;
    }

    // MARK: - Where an export may land

    /// <summary>
    /// A save dialog's choice is the user's, but an export must still not turn into
    /// an unbacked replacement of a live config or one of Kytto's own stores.
    /// </summary>
    /// <remarks>
    /// Compared by what each path finally names — links, junctions, mount points,
    /// short names and letter case followed through — so an alias cannot do what the
    /// file itself is refused. A name carrying an alternate data stream is refused
    /// outright: it addresses a stream of some other file.
    /// </remarks>
    private void RefuseProtectedExportLocation(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Contains(':') || file.AsSpan(Math.Min(2, file.Length)).Contains(':'))
        {
            throw PortableSetupException.ProtectedLocation("a file name with a colon addresses a stream of some other file");
        }

        // `\\localhost\C$\…` is a local file under a name no resolution turns back into
        // its drive path, so any share of this computer is refused outright.
        if (FinalPath.IsShareOnThisComputer(file))
        {
            throw PortableSetupException.ProtectedLocation(
                "a share on this computer can name a live config under another spelling — choose a folder by its own path");
        }

        var guarded = new List<string> { _paths.Root };
        foreach (var descriptor in _descriptors)
        {
            guarded.AddRange(descriptor.Sources.SelectMany(source => source.Path.ResolveAll(_home)));
            if (descriptor.EditableServerMap is { } map)
            {
                guarded.Add(_resolver.ResolveServerMap(map.File, descriptor.Id));
                if (map.Enablement is EnablementStrategy.DenyList deny)
                {
                    guarded.AddRange(deny.File.ResolveAll(_home));
                }
            }
        }
        guarded.AddRange(_additionalProtectedPaths);

        if (guarded.Any(path => FinalPath.IsSameOrUnder(file, path)))
        {
            throw PortableSetupException.ProtectedLocation(
                "it must be outside client configuration and Kytto's own data folders, however it is spelled");
        }
    }
}
