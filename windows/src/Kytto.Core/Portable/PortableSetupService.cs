using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Settings;
using Kytto.Core.Toml;

namespace Kytto.Core.Portable;

/// <summary>
/// File-based import and export of a setup (§7.13). Resolved inputs and config bytes
/// stay native; callers can pass only the opaque preview id across IPC.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Export</strong> writes a whitelist, never a copy of a config, and never
/// looks in the secret store. <strong>Import</strong> is two steps: a preview that
/// reads and validates everything and keeps a plan natively, and an apply that
/// re-checks that nothing moved and commits the plan once. Nothing is ever started
/// and an imported server is always switched off.
/// </para>
/// <para>
/// Apply changes four kinds of thing — client configs, the park store, the profile
/// store and the pending contract references — and either all of it lands or none of
/// it does. Kytto's own stores go first, because they can be put back byte for byte;
/// the client configs go last, as one <see cref="ConfigWriter"/> transaction with its
/// own guarded rollback. A failure anywhere puts the stores back, and a store that
/// cannot be put back is reported rather than hidden.
/// </para>
/// </remarks>
public sealed partial class PortableSetupService
{
    public const int MaximumFileBytes = 2_000_000;

    /// <summary>How long a preview can be applied. Inputs are kept natively that long.</summary>
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Plans retained at once; a fifth evicts the oldest.</summary>
    public const int MaximumPlans = 4;

    private readonly string _home;
    private readonly ConfigWriter _writer;
    private readonly ParkStore _parkStore;
    private readonly DigestLedger _ledger;
    private readonly KyttoPaths _paths;
    private readonly ProfileStore _profiles;
    private readonly IReadOnlyList<ClientDescriptor> _descriptors;
    private readonly ClientPathResolver _resolver;
    private readonly IReadOnlyList<string> _additionalProtectedPaths;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Plan> _plans = new(StringComparer.Ordinal);

    public PortableSetupService(
        string home,
        ConfigWriter writer,
        ParkStore parkStore,
        DigestLedger ledger,
        KyttoPaths paths,
        ProfileStore? profileStore = null,
        IReadOnlyList<ClientDescriptor>? descriptors = null,
        IReadOnlyDictionary<string, string>? pathOverrides = null,
        IReadOnlyList<string>? additionalProtectedPaths = null,
        Func<DateTimeOffset>? clock = null)
    {
        _home = home;
        _writer = writer;
        _parkStore = parkStore;
        _ledger = ledger;
        _paths = paths;
        _profiles = profileStore ?? new ProfileStore(paths);
        _descriptors = descriptors ?? ClientRegistry.All;
        _resolver = new ClientPathResolver(home, pathOverrides);
        _additionalProtectedPaths = additionalProtectedPaths ?? [];
        _clock = clock ?? (static () => Timestamp.Now);
    }

    // MARK: - Plans

    private sealed record Snapshot(string Path, byte[]? Data, string? Digest, string Display);

    private sealed record FileEdit(Snapshot Snapshot, ClientId ClientId, ConfigFormat Format, string Updated);

    private sealed record ParkEdit(ClientId ClientId, string Name, string Text);

    private sealed record Plan(
        PortableImportPreview Preview,
        Snapshot Source,
        IReadOnlyList<FileEdit> Edits,
        IReadOnlyList<ParkEdit> Parks,
        IReadOnlyList<PortableProfile> Profiles,
        IReadOnlyList<PortableContractReference> Contracts,
        IReadOnlyList<Snapshot> Owned,
        DateTimeOffset CreatedAt);

    private string PendingFile => Path.Combine(_paths.Root, "pending-contract-imports.json");

    /// <summary>Releases a plan and the replacement values it holds.</summary>
    public void Discard(string previewId)
    {
        lock (MutationCoordinator.Gate)
        {
            _plans.Remove(previewId);
        }
    }

    /// <summary>Drops every plan. A change of settings means they were prepared for a world that is gone.</summary>
    public void DiscardAll()
    {
        lock (MutationCoordinator.Gate)
        {
            _plans.Clear();
        }
    }

    internal int RetainedPlanCount
    {
        get
        {
            lock (MutationCoordinator.Gate)
            {
                return _plans.Count;
            }
        }
    }

    private void PurgeExpired()
    {
        var now = _clock();
        foreach (var id in _plans.Where(pair => now - pair.Value.CreatedAt >= PlanLifetime)
                     .Select(pair => pair.Key).ToArray())
        {
            _plans.Remove(id);
        }
    }

    // MARK: - Inspect

    public PortableSetupSummary Inspect(string file) => Summary(ReadSetup(file).Setup);

    // MARK: - Preview

    /// <summary>
    /// Validates the file, the inputs and the destinations, and keeps a plan.
    /// </summary>
    /// <param name="values">
    /// Requirement id → what the user typed natively. Target paths come exclusively
    /// from the registry and the current overrides, never from the imported file.
    /// </param>
    /// <param name="enableImportedServers">
    /// Core-level switch for tests and tooling: copies that were enabled when
    /// exported stay enabled. The application never sets it, and no IPC command
    /// carries it — an import from the app always arrives switched off.
    /// </param>
    public PortableImportPreview Preview(
        string file,
        IReadOnlyList<ClientId> targetClients,
        IReadOnlyDictionary<string, string>? values = null,
        bool enableImportedServers = false)
    {
        values ??= new Dictionary<string, string>();
        lock (MutationCoordinator.Gate)
        {
            PurgeExpired();
            var (setup, source) = ReadSetup(file);

            if ((targetClients.Count == 0 && setup.Servers.Count > 0) ||
                targetClients.Distinct().Count() != targetClients.Count)
            {
                throw PortableSetupException.Invalid("choose distinct writable target clients");
            }

            var known = setup.Requirements.Select(requirement => requirement.Id).ToHashSet(StringComparer.Ordinal);
            if (!values.Keys.All(known.Contains))
            {
                throw PortableSetupException.Invalid("unknown required input");
            }
            foreach (var requirement in setup.Requirements)
            {
                if (!values.TryGetValue(requirement.Id, out var value) || value.Length == 0 ||
                    Encoding.UTF8.GetByteCount(value) > 16_384 || value.Contains('\0'))
                {
                    throw PortableSetupException.MissingInput(requirement.Label);
                }
                // A relative path would silently change meaning on another machine,
                // and a drive-relative one (`C:tool`) on another working directory.
                if (requirement.Kind == PortableRequirementKind.Path && !IsAbsolutePath(value))
                {
                    throw PortableSetupException.Invalid("mapped paths must be absolute");
                }
            }

            string Resolve(PortableValue entry) =>
                entry.Literal ?? values.GetValueOrDefault(entry.RequirementID ?? "") ?? "";

            // Kytto's own stores are read strictly first: "no such profile" and "the
            // profile file is damaged" must not look the same to a collision check.
            VerifyOwnedStoresReadable();
            var existingProfiles = ReadProfiles();
            foreach (var profile in setup.Profiles)
            {
                if (existingProfiles.Any(existing =>
                        string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw PortableSetupException.Conflict($"Profile {profile.Name}");
                }
            }

            var grouped = setup.Servers
                .GroupBy(server => Server.Identity(server.Name), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var edits = new List<FileEdit>();
            var parks = new List<ParkEdit>();
            var observed = new List<Snapshot>();
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var clientId in targetClients.OrderBy(client => client.Raw(), StringComparer.Ordinal))
            {
                var map = _descriptors.FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap
                    ?? throw AuthoringException.NoSourceForClient(clientId);
                var path = _resolver.ResolveServerMap(map.File, clientId);
                var display = _resolver.DisplayServerMap(map.File, clientId);
                var snapshot = TakeSnapshot(path, display);
                observed.Add(snapshot);
                if (!destinations.Add(FinalPath.Resolve(path)))
                {
                    throw PortableSetupException.Invalid("two selected clients resolve to the same destination");
                }

                var original = ReadText(snapshot, map.Format);
                var text = original;
                var denied = new List<string>();
                foreach (var identity in grouped.Keys.Order(StringComparer.Ordinal))
                {
                    var copies = grouped[identity];
                    var copy = copies.FirstOrDefault(candidate => candidate.SourceClient == clientId) ?? copies[0];

                    if (_parkStore.Parked(clientId, copy.Name) is not null)
                    {
                        throw PortableSetupException.Conflict($"{copy.Name} in {clientId.Raw()} (parked)");
                    }
                    if (Exists(text, map, identity, display))
                    {
                        throw PortableSetupException.Conflict($"{copy.Name} in {clientId.Raw()}");
                    }

                    var draft = new ServerDraft
                    {
                        Name = copy.Name,
                        Transport = copy.Transport,
                        Command = copy.Command is { } command ? Resolve(command) : "",
                        Args = copy.Args.Select(Resolve).ToArray(),
                        Env = copy.Environment.Keys
                            .Order(StringComparer.Ordinal)
                            .Select(key => new EnvEntry(key, Resolve(copy.Environment[key])))
                            .ToArray(),
                        Url = copy.Url is { } url ? Resolve(url) : "",
                    };
                    if (draft.Validate().Count > 0)
                    {
                        throw PortableSetupException.Invalid($"invalid definition for {copy.Name}");
                    }

                    var enabled = enableImportedServers && copy.WasEnabled;
                    try
                    {
                        switch (map.Enablement)
                        {
                            case EnablementStrategy.Presence when !enabled:
                                // No off switch: the definition is held by Kytto, exactly as a
                                // toggle would hold it, and never written into the file.
                                parks.Add(new ParkEdit(
                                    clientId,
                                    copy.Name,
                                    RenderJson(copy, draft, Resolve, JsonDocument.Parse(text), map.ServersKey)));
                                break;
                            case EnablementStrategy.Presence:
                            case EnablementStrategy.InlineFlag:
                            case EnablementStrategy.DenyList:
                                text = map.Format == ConfigFormat.Json
                                    ? InsertJson(text, copy, draft, Resolve, map.ServersKey)
                                    : InsertToml(text, copy, draft, Resolve, map.ServersKey, enabled);
                                if (map.Enablement is EnablementStrategy.DenyList && !enabled)
                                {
                                    denied.Add(copy.Name);
                                }
                                break;
                            default:
                                throw AuthoringException.NoSourceForClient(clientId);
                        }
                    }
                    catch (Exception error) when (error is JsonEditException or TomlEditException)
                    {
                        throw ConfigWriteException.Unreadable(display, error.Message);
                    }
                }

                if (text != original)
                {
                    edits.Add(new FileEdit(snapshot, clientId, map.Format, text));
                }

                if (map.Enablement is EnablementStrategy.DenyList deny && denied.Count > 0)
                {
                    var denyPath = _resolver.Resolve(deny.File);
                    if (!destinations.Add(FinalPath.Resolve(denyPath)))
                    {
                        throw PortableSetupException.Invalid("overlapping configuration destinations");
                    }
                    var denySnapshot = TakeSnapshot(denyPath, deny.File.DisplayString());
                    edits.Add(new FileEdit(
                        denySnapshot,
                        clientId,
                        ConfigFormat.Json,
                        DenyText(ReadText(denySnapshot, ConfigFormat.Json), deny, denied, denySnapshot.Display)));
                }
            }

            // Kytto's own stores are part of the plan too, so another window's save
            // between preview and commit cannot be overwritten (§6.4).
            var owned = new List<Snapshot>
            {
                TakeSnapshot(_paths.ParkFile, "Parked definitions"),
                TakeSnapshot(_paths.ProfilesFile, "Profiles"),
                TakeSnapshot(PendingFile, "Pending contract references"),
            };
            owned.AddRange(observed.Where(snapshot =>
                !edits.Any(edit => SamePath(edit.Snapshot.Path, snapshot.Path))));

            var id = Guid.NewGuid().ToString("D");
            var warnings = Summary(setup).Warnings.ToList();
            warnings.Add(enableImportedServers
                ? "Only copies enabled in the exported setup will be enabled; disabled copies remain disabled."
                : "All imported servers start switched off. Review their commands before enabling them.");
            var preview = new PortableImportPreview(
                id,
                grouped.Keys.Order(StringComparer.Ordinal).ToArray(),
                setup.Profiles.Select(profile => profile.Name).ToArray(),
                targetClients.ToArray(),
                edits.Select(edit => edit.Snapshot.Display).ToArray(),
                setup.ReviewedContracts.Count,
                warnings,
                enableImportedServers);

            // Bound how long and how many plans hold typed values in memory.
            while (_plans.Count >= MaximumPlans)
            {
                _plans.Remove(_plans.OrderBy(pair => pair.Value.CreatedAt).First().Key);
            }
            _plans[id] = new Plan(
                preview, source, edits, parks, setup.Profiles, setup.ReviewedContracts, owned, _clock());
            return preview;
        }
    }

    // MARK: - Apply

    /// <summary>
    /// Commits a preview, exactly once and within ten minutes. Starts no server and
    /// stores nothing in the credential store.
    /// </summary>
    public PortableImportResult Apply(string previewId)
    {
        lock (MutationCoordinator.Gate)
        {
            PurgeExpired();
            if (!_plans.Remove(previewId, out var plan) || _clock() - plan.CreatedAt >= PlanLifetime)
            {
                throw PortableSetupException.StalePreview();
            }

            // Everything the plan was built from must still be what it was: the setup
            // file, every config it would write or only read, and Kytto's own stores.
            foreach (var snapshot in new[] { plan.Source }
                         .Concat(plan.Edits.Select(edit => edit.Snapshot))
                         .Concat(plan.Owned))
            {
                if (!Unchanged(snapshot)) throw PortableSetupException.StalePreview();
            }

            // Re-read at the last moment: a profile named the same since the preview
            // is a collision the preview could not have seen.
            VerifyOwnedStoresReadable();
            var existing = ReadProfiles();
            foreach (var profile in plan.Profiles)
            {
                if (existing.Any(candidate =>
                        string.Equals(candidate.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw PortableSetupException.Conflict($"Profile {profile.Name}");
                }
            }

            var owned = plan.Owned.Where(snapshot =>
                SamePath(snapshot.Path, _paths.ParkFile) ||
                SamePath(snapshot.Path, _paths.ProfilesFile) ||
                SamePath(snapshot.Path, PendingFile)).ToArray();

            IReadOnlyList<WriteReceipt> receipts = [];
            IReadOnlyList<PreparedConfigWrite> prepared = [];
            IReadOnlyList<Profile> imported = [];
            try
            {
                foreach (var park in plan.Parks)
                {
                    _parkStore.Park(park.ClientId, park.Name, park.Text);
                }

                imported = _profiles.ImportProfiles(plan.Profiles
                    .Select(profile => (profile.Name, profile.ServerIDs, profile.TokenBudget))
                    .ToArray());

                if (plan.Contracts.Count > 0) RecordPendingReferences(plan.Contracts);

                // Last, and all together: one transaction with a digest check, backups
                // and a guarded rollback of its own. Everything above can be undone
                // from the snapshots if this refuses.
                prepared = plan.Edits.Select(edit => Prepare(edit)).ToArray();
                receipts = _writer.Commit(prepared);
            }
            catch (Exception error)
            {
                var failures = RestoreOwned(owned);
                if (error is ConfigTransactionException transaction)
                {
                    failures.AddRange(transaction.Paths);
                }
                if (failures.Count > 0)
                {
                    throw PortableSetupException.RollbackFailed(
                        string.Join(", ", failures.Distinct(StringComparer.OrdinalIgnoreCase)), error);
                }
                throw;
            }

            for (var index = 0; index < prepared.Count; index++)
            {
                _ledger.Record(prepared[index].Path, receipts[index].Digest);
            }

            return new PortableImportResult(
                plan.Preview.ServerNames,
                imported.Select(profile => profile.Name).ToArray(),
                plan.Edits
                    .Where((_, index) => receipts[index].DidWrite)
                    .Select(edit => edit.ClientId)
                    .Distinct()
                    .OrderBy(client => client.Raw(), StringComparer.Ordinal)
                    .ToArray(),
                receipts.Select(receipt => receipt.BackupID).OfType<string>().ToArray(),
                plan.Contracts.Count);
        }
    }

    private PreparedConfigWrite Prepare(FileEdit edit) => edit.Format == ConfigFormat.Json
        ? _writer.Prepare<JsonDocument>(
            edit.Snapshot.Path, edit.ClientId, edit.Snapshot.Display, edit.Snapshot.Digest, _ => edit.Updated)
        : _writer.Prepare<TomlDocument>(
            edit.Snapshot.Path, edit.ClientId, edit.Snapshot.Display, edit.Snapshot.Digest, _ => edit.Updated);

    /// <summary>
    /// Puts Kytto's own stores back to what the preview saw. Returns what it could not.
    /// </summary>
    private static List<string> RestoreOwned(IEnumerable<Snapshot> owned)
    {
        var failures = new List<string>();
        foreach (var snapshot in owned)
        {
            try
            {
                if (snapshot.Data is { } data)
                {
                    if (Digest.OfFile(snapshot.Path) != snapshot.Digest) AtomicWriter.Write(data, snapshot.Path);
                }
                else if (File.Exists(snapshot.Path))
                {
                    File.Delete(snapshot.Path);
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add(snapshot.Display);
            }
        }
        return failures;
    }

    // MARK: - Reading

    private (PortableSetup Setup, Snapshot Snapshot) ReadSetup(string file)
    {
        byte[] data;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumFileBytes) throw PortableSetupException.Invalid("file is too large");
            }
            data = buffer.ToArray();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw PortableSetupException.Invalid("the file could not be read");
        }

        var setup = PortableSetupSerializer.Read(data);
        PortableSetupValidator.Validate(setup);
        return (setup, new Snapshot(file, data, Digest.Of(data), "Portable setup file"));
    }

    private static Snapshot TakeSnapshot(string path, string display)
    {
        try
        {
            if (Directory.Exists(path)) throw ConfigWriteException.Unreadable(display, "it is a folder");
            if (!File.Exists(path)) return new Snapshot(path, null, null, display);
            var data = File.ReadAllBytes(path);
            return new Snapshot(path, data, Digest.Of(data), display);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ConfigWriteException.Unreadable(display, error.Message);
        }
    }

    private static string ReadText(Snapshot snapshot, ConfigFormat format)
    {
        string text;
        if (snapshot.Data is null)
        {
            return format == ConfigFormat.Json ? JsonDocument.EmptySource : TomlDocument.EmptySource;
        }
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(snapshot.Data);
        }
        catch (DecoderFallbackException)
        {
            throw ConfigWriteException.Unreadable(snapshot.Display, "not valid UTF-8");
        }

        try
        {
            if (format == ConfigFormat.Json) _ = JsonDocument.Parse(text);
            else _ = TomlDocument.Parse(text);
        }
        catch (Exception error) when (error is JsonParseException or TomlParseException)
        {
            throw ConfigWriteException.Unreadable(snapshot.Display, error.Message);
        }
        return text;
    }

    private static bool Unchanged(Snapshot snapshot)
    {
        var current = Digest.OfFile(snapshot.Path);
        if (snapshot.Digest is null)
        {
            // "No file" has to mean no file — an unreadable one is not absent.
            return current is null && !File.Exists(snapshot.Path) && !Directory.Exists(snapshot.Path);
        }
        return current == snapshot.Digest;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsolutePath(string value) =>
        Path.IsPathFullyQualified(value) &&
        !value.StartsWith(@"\\.\", StringComparison.Ordinal) &&
        !value.StartsWith(@"\\?\", StringComparison.Ordinal);

    private void VerifyOwnedStoresReadable()
    {
        try
        {
            _parkStore.VerifyReadable();
        }
        catch (InvalidDataException error)
        {
            throw PortableSetupException.StoreUnreadable("parked definitions", error);
        }
        if (File.Exists(PendingFile)) _ = PendingImports();
    }

    private IReadOnlyList<Profile> ReadProfiles()
    {
        try
        {
            return _profiles.AllStrict();
        }
        catch (ProfileException error)
        {
            throw PortableSetupException.StoreUnreadable("profiles", error);
        }
    }

    private static bool Exists(string text, ConfigSource.ServerMap source, string identity, string display)
    {
        try
        {
            return source.Format == ConfigFormat.Json
                ? JsonDocument.Parse(text).ServerKey(identity, source.ServersKey) is not null
                : TomlDocument.Parse(text).ServerName(identity, source.ServersKey) is not null;
        }
        catch (Exception error) when (error is JsonEditException or TomlEditException)
        {
            // Two entries answering to one name: which one a client reads is a guess,
            // and adding a third is not an answer.
            throw ConfigWriteException.Unreadable(display, error.Message);
        }
    }

    // MARK: - Summaries

    private static PortableSetupSummary Summary(PortableSetup setup, int omitted = 0, int unportable = 0)
    {
        var warnings = new List<string>
        {
            "Environment/header values, arbitrary arguments, URLs and local paths require native input. " +
            "Unknown fields and bundled plugins are excluded.",
        };
        if (omitted > 0) warnings.Add($"{omitted} bundled or read-only definitions were excluded.");
        if (unportable > 0)
        {
            warnings.Add($"{unportable} definitions could not be described portably and were excluded.");
        }
        if (setup.ReviewedContracts.Count > 0)
        {
            warnings.Add(
                "Reviewed contracts travel as fingerprints only and remain pending review after import.");
        }
        var groups = setup.Servers.GroupBy(server => Server.Identity(server.Name), StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Count() > 1))
        {
            warnings.Add(
                "Each destination uses its original client's copy when available; otherwise the first exported copy is used.");
        }
        return new PortableSetupSummary(
            groups.Select(group => group.Key).Order(StringComparer.Ordinal).ToArray(),
            setup.Profiles.Select(profile => profile.Name).ToArray(),
            setup.Requirements,
            setup.ReviewedContracts.Count,
            warnings);
    }
}
