using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core.Portable;

public sealed partial class PortableSetupService
{
    // MARK: - Pending contract references

    /// <summary>
    /// What an imported fingerprint is: evidence to compare against, with the moment
    /// this machine received it. Never a baseline (§7.13).
    /// </summary>
    private sealed record PendingImport(PortableContractReference Reference, DateTimeOffset ImportedAt);

    /// <summary>
    /// The earlier on-disk shape had no import time. Its first sighting in this
    /// process becomes that time, so such a reference also needs a check made after
    /// it was first seen. Guarded by <see cref="MutationCoordinator"/>.
    /// </summary>
    private static readonly Dictionary<string, (string Digest, DateTimeOffset Since)> LegacyPendingDates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonWriterOptions PendingWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The references as imported. Pending fingerprints are never consumed by
    /// <see cref="MetadataStore"/>: a fresh real check and an explicit review are what
    /// establish local trust.
    /// </summary>
    public IReadOnlyList<PortableContractReference> PendingContractReferences()
    {
        lock (MutationCoordinator.Gate)
        {
            return PendingImports().Select(entry => entry.Reference).ToArray();
        }
    }

    /// <summary>
    /// Reads the pending store, or refuses with <see cref="PortableSetupFailure.PendingContractsUnreadable"/>.
    /// The file is never deleted, repaired or rewritten on the way: a store this
    /// cannot read is one a person may want to look at.
    /// </summary>
    private List<PendingImport> PendingImports()
    {
        lock (MutationCoordinator.Gate)
        {
            if (!File.Exists(PendingFile)) return [];
            try
            {
                var data = ReadBounded(PendingFile);
                using var document = System.Text.Json.JsonDocument.Parse(
                    data, new JsonDocumentOptions { MaxDepth = 64 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw PortableSetupException.PendingContractsUnreadable();

                var nodes = document.RootElement.EnumerateArray().ToArray();
                if (nodes.Length > 1000) throw PortableSetupException.PendingContractsUnreadable();

                var isLegacy = nodes.Length > 0 &&
                    nodes[0].ValueKind == JsonValueKind.Object &&
                    nodes[0].TryGetProperty("serverID", out _);

                var imports = new List<PendingImport>();
                foreach (var node in nodes)
                {
                    JsonElement reference;
                    double importedMilliseconds = 0;
                    if (isLegacy)
                    {
                        reference = node;
                    }
                    else
                    {
                        var entry = ExactMembers(node, "reference", "importedAt");
                        reference = entry["reference"];
                        if (entry["importedAt"].ValueKind != JsonValueKind.Number ||
                            !entry["importedAt"].TryGetDouble(out importedMilliseconds))
                        {
                            throw PortableSetupException.PendingContractsUnreadable();
                        }
                    }

                    var fields = ExactMembers(reference, "serverID", "toolDigests");
                    if (fields["serverID"].ValueKind != JsonValueKind.String ||
                        fields["toolDigests"].ValueKind != JsonValueKind.Object)
                    {
                        throw PortableSetupException.PendingContractsUnreadable();
                    }
                    var digests = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var property in fields["toolDigests"].EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !digests.TryAdd(property.Name, property.Value.GetString()!))
                        {
                            throw PortableSetupException.PendingContractsUnreadable();
                        }
                    }

                    var importedAt = isLegacy ? LegacySince(data) : FromMilliseconds(importedMilliseconds);
                    imports.Add(new PendingImport(
                        new PortableContractReference(fields["serverID"].GetString()!, digests), importedAt));
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                var now = _clock();
                foreach (var entry in imports)
                {
                    var reference = entry.Reference;
                    if (!PortableSetupValidator.ValidText(reference.ServerID) ||
                        reference.ServerID != Server.Identity(reference.ServerID) ||
                        !ids.Add(reference.ServerID) ||
                        reference.ToolDigests.Count > 2048 ||
                        !reference.ToolDigests.All(pair =>
                            PortableSetupValidator.ValidText(pair.Key) && PortableSetupValidator.IsSha256(pair.Value)) ||
                        entry.ImportedAt > now.AddSeconds(60))
                    {
                        throw PortableSetupException.PendingContractsUnreadable();
                    }
                }
                return imports;
            }
            catch (Exception error) when (error is not PortableSetupException)
            {
                throw PortableSetupException.PendingContractsUnreadable();
            }
        }
    }

    private static Dictionary<string, JsonElement> ExactMembers(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) throw PortableSetupException.PendingContractsUnreadable();
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!members.TryAdd(property.Name, property.Value)) throw PortableSetupException.PendingContractsUnreadable();
        }
        if (members.Count != keys.Length || !keys.All(members.ContainsKey))
        {
            throw PortableSetupException.PendingContractsUnreadable();
        }
        return members;
    }

    /// <summary>A time that cannot be represented is a store that cannot be trusted.</summary>
    private static DateTimeOffset FromMilliseconds(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 253_402_300_799_000)
        {
            throw PortableSetupException.PendingContractsUnreadable();
        }
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(milliseconds));
    }

    private DateTimeOffset LegacySince(byte[] data)
    {
        var key = Path.GetFullPath(PendingFile);
        var digest = Digest.Of(data);
        if (LegacyPendingDates.TryGetValue(key, out var previous) && previous.Digest == digest)
        {
            return previous.Since;
        }

        var since = _clock();
        if (LegacyPendingDates.Count >= 64) LegacyPendingDates.Clear();
        LegacyPendingDates[key] = (digest, since);
        return since;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumFileBytes) throw PortableSetupException.PendingContractsUnreadable();
        }
        return buffer.ToArray();
    }

    private void WritePending(IReadOnlyList<PendingImport> imports)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, PendingWriterOptions))
        {
            writer.WriteStartArray();
            foreach (var entry in imports.OrderBy(item => item.Reference.ServerID, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("importedAt", entry.ImportedAt.ToUnixTimeMilliseconds());
                writer.WriteStartObject("reference");
                writer.WriteString("serverID", entry.Reference.ServerID);
                writer.WriteStartObject("toolDigests");
                foreach (var (tool, digest) in entry.Reference.ToolDigests.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(tool, digest);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        var data = buffer.ToArray();
        if (imports.Count > 1000 || data.Length > MaximumFileBytes)
        {
            throw PortableSetupException.Invalid("too many pending contract references");
        }

        KyttoStorage.EnsurePrivateRoot(_paths);
        // Before the write, as the other stores do: the writer keeps the destination's
        // descriptor, so a loose one would otherwise outlive every rewrite.
        KyttoStorage.TightenOwnFile(PendingFile);
        AtomicWriter.Write(data, PendingFile);
    }

    private void RecordPendingReferences(IReadOnlyList<PortableContractReference> incoming)
    {
        var imports = PendingImports();
        var incomingIds = incoming.Select(reference => reference.ServerID).ToHashSet(StringComparer.Ordinal);
        imports.RemoveAll(entry => incomingIds.Contains(entry.Reference.ServerID));

        // The time is this machine's, taken now. A date inside the file would let the
        // file decide that an old check already counts.
        var importedAt = _clock();
        imports.AddRange(incoming.Select(reference => new PendingImport(reference, importedAt)));
        WritePending(imports);
    }

    // MARK: - Comparing and reviewing

    /// <summary>
    /// Each pending reference with whether the latest check agrees with it.
    /// </summary>
    /// <remarks>
    /// <c>Matching</c> stays null until a check that <em>passed</em> and finished
    /// strictly after the reference arrived here: a cached result from before the
    /// import says nothing about it. Token counts, schema key order and the order of
    /// tools never change the answer; the order inside an array does.
    /// </remarks>
    public IReadOnlyList<PortablePendingContractSummary> PendingContractSummary()
    {
        lock (MutationCoordinator.Gate)
        {
            var imports = PendingImports();
            if (imports.Count == 0) return [];

            var metadata = LocalContractMetadata();
            return imports
                .OrderBy(entry => entry.Reference.ServerID, StringComparer.Ordinal)
                .Select(entry =>
                {
                    var reference = entry.Reference;
                    bool? matching = null;
                    if (FreshPassingCheck(metadata, entry) is { } health)
                    {
                        matching = SameDigests(DigestsOf(health.Tools), reference.ToolDigests);
                    }
                    return new PortablePendingContractSummary(
                        reference.ServerID, reference.ToolDigests.Count, matching);
                })
                .ToArray();
        }
    }

    /// <summary>Removes review evidence after explicit review of the observed local contract.</summary>
    /// <remarks>
    /// Never copies the imported hashes or tool definitions anywhere: the local
    /// baseline is whatever the local checks established.
    /// </remarks>
    public void ReviewPendingContract(string serverId) => ReviewPending(serverId, () => { });

    /// <summary>
    /// Reviews the pending reference for a server and runs <paramref name="acknowledgeBaseline"/>
    /// — the ordinary acknowledgement of a local Contract Guard alert — as one step
    /// that either completes or changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point. Both stores are read and the fresh-check requirement
    /// is satisfied <em>before</em> either is written, so an unreadable pending store
    /// or a stale check stops the acknowledgement with the local alert untouched. The
    /// pending reference is then removed first and the baseline moved second, because
    /// the pending store can be put back byte for byte and the baseline cannot; if
    /// the baseline step refuses, the reference is restored.
    /// </para>
    /// </remarks>
    public void ReviewPending(string serverId, Action acknowledgeBaseline)
    {
        lock (MutationCoordinator.Gate)
        {
            var imports = PendingImports();
            var id = Server.Identity(serverId);
            var pending = imports.FirstOrDefault(entry => entry.Reference.ServerID == id);
            if (pending is null)
            {
                acknowledgeBaseline();
                return;
            }

            if (FreshPassingCheck(LocalContractMetadata(), pending) is null)
            {
                throw PortableSetupException.LocalContractNotChecked();
            }

            var original = File.ReadAllBytes(PendingFile);
            imports.Remove(pending);
            WritePending(imports);
            try
            {
                acknowledgeBaseline();
            }
            catch (Exception error)
            {
                try
                {
                    AtomicWriter.Write(original, PendingFile);
                }
                catch (Exception restore) when (
                    restore is IOException or UnauthorizedAccessException)
                {
                    throw PortableSetupException.RollbackFailed("pending contract references", error);
                }
                throw;
            }
        }
    }

    private IReadOnlyDictionary<string, ServerMetadata> LocalContractMetadata()
    {
        try
        {
            return MetadataStore.ReadStrict(_paths);
        }
        catch (InvalidDataException)
        {
            throw PortableSetupException.LocalContractNotChecked();
        }
    }

    private static HealthResult? FreshPassingCheck(
        IReadOnlyDictionary<string, ServerMetadata> metadata,
        PendingImport pending) =>
        metadata.TryGetValue(pending.Reference.ServerID, out var local) &&
        local.Health is { Status: HealthStatus.Passed } health &&
        health.CheckedAt > pending.ImportedAt
            ? health
            : null;

    /// <summary>
    /// Matches how Contract Guard treats a repeated tool name — the last one wins — and
    /// ignores the order tools arrive in, which is not part of a contract.
    /// </summary>
    private static Dictionary<string, string> DigestsOf(IEnumerable<ToolSummary> tools)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools) hashes[tool.Name] = ContractDigest.Of(tool);
        return hashes;
    }

    private static bool SameDigests(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var other) && other == pair.Value);
}
