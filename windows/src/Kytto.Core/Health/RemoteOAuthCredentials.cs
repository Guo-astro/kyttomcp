using System.Security.Cryptography;
using System.Text;
using Kytto.Core.Secrets;

namespace Kytto.Core.Health;

/// <summary>
/// Where Kytto's own remote sign-ins are kept: one secure-store item per resource,
/// split deterministically into small checked pieces (§6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why pieces.</b> A Windows Credential Manager item holds at most 2,560 bytes, and
/// <c>WindowsCredentialStore</c> writes UTF-16, so about 1,280 characters. An OAuth
/// record — endpoints, scopes, an access token and a refresh token — is routinely
/// larger. Truncating it would store a token that fails later for no visible reason,
/// and a plaintext fallback would defeat the point of the store. So the record is
/// base64-encoded, cut into pieces of 1,000 characters, and written as separate items
/// under a manifest that carries a SHA-256 of the whole. A record that does not
/// reassemble to that hash is refused, never partly used.
/// </para>
/// <para>
/// <b>Why a generation in the item names.</b> New pieces are written under a fresh
/// random name and the manifest is switched last, so an interrupted write leaves the
/// previous, complete record in place; the old pieces are removed afterwards.
/// </para>
/// <para>
/// <b>Identity.</b> <c>oauth.resource.</c> plus the SHA-256 of the exact canonical
/// resource, so the item name names nothing and one resource can never read
/// another's tokens. A record whose own resource is not the one asked for is
/// refused.
/// </para>
/// <para>
/// Callers serialize access (<c>AppModel</c> holds a lock around load-modify-store);
/// this type keeps no state of its own.
/// </para>
/// </remarks>
public sealed class RemoteOAuthCredentials(ISecretStore store)
{
    private const string Prefix = "oauth.resource.";
    private const string ManifestTag = "kytto-oauth-1";
    private const int PieceCharacters = 1_000;
    private const int MaxRecordBytes = 45 * 1024;

    public RemoteOAuthCredential? Load(string resourceUrl)
    {
        var canonical = RemoteOAuthClient.CanonicalResource(resourceUrl);
        var identity = Identity(canonical);
        if (store.Value(identity) is not { } manifestText) return null;

        var manifest = Manifest.Parse(manifestText);
        var pieces = new StringBuilder();
        for (var index = 0; index < manifest.Count; index++)
        {
            pieces.Append(store.Value(PieceName(identity, manifest.Generation, index)) ?? throw Corrupt());
        }

        byte[] record;
        try
        {
            record = Convert.FromBase64String(pieces.ToString());
        }
        catch (FormatException error)
        {
            throw new RemoteOAuthException(RemoteOAuthError.CredentialStore, error);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(record), Convert.FromHexString(manifest.Hash)))
        {
            throw Corrupt();
        }

        var credential = RemoteOAuthCredential.Deserialize(Encoding.UTF8.GetString(record));
        return credential.Configuration.ResourceUrl == canonical ? credential : throw Corrupt();
    }

    /// <summary>Saves a record, or fails with a clear error and changes nothing.</summary>
    public void Store(RemoteOAuthCredential credential)
    {
        var canonical = RemoteOAuthClient.CanonicalResource(credential.Configuration.ResourceUrl);
        var identity = Identity(canonical);
        var record = Encoding.UTF8.GetBytes(credential.Serialize());
        if (record.Length > MaxRecordBytes) throw new RemoteOAuthException(RemoteOAuthError.CredentialStore);

        var encoded = Convert.ToBase64String(record);
        var count = (encoded.Length + PieceCharacters - 1) / PieceCharacters;
        var generation = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        Manifest? previous = null;
        try
        {
            previous = store.Value(identity) is { } text ? Manifest.TryParse(text) : null;
        }
        catch (SecretsException)
        {
            // An unreadable old manifest is replaced below; its pieces are swept up.
        }

        var written = new List<string>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var name = PieceName(identity, generation, index);
                store.Store(encoded.Substring(index * PieceCharacters, Math.Min(PieceCharacters, encoded.Length - index * PieceCharacters)), name);
                written.Add(name);
            }
            store.Store(
                string.Join(' ', ManifestTag, generation, count, Convert.ToHexString(SHA256.HashData(record)).ToLowerInvariant()),
                identity);
        }
        catch (Exception error) when (error is SecretsException or IOException)
        {
            // The previous record, if any, is untouched: the manifest is switched last.
            foreach (var name in written) TryDelete(name);
            throw new RemoteOAuthException(RemoteOAuthError.CredentialStore, error);
        }

        if (previous is { } old && old.Generation != generation) DeletePieces(identity, old);
        SweepOrphans(identity, generation);
    }

    /// <summary>Removes the record and every piece of it, including leftovers.</summary>
    public void Delete(string resourceUrl)
    {
        var identity = Identity(RemoteOAuthClient.CanonicalResource(resourceUrl));
        try
        {
            if (store.Value(identity) is { } text && Manifest.TryParse(text) is { } manifest) DeletePieces(identity, manifest);
            store.Delete(identity);
            SweepOrphans(identity, keepGeneration: null);
        }
        catch (SecretsException error)
        {
            throw new RemoteOAuthException(RemoteOAuthError.CredentialStore, error);
        }
    }

    // MARK: - Names

    internal static string Identity(string canonicalResource) =>
        Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalResource))).ToLowerInvariant();

    private static string PieceName(string identity, string generation, int index) =>
        $"{identity}.{generation}.{index}";

    private void DeletePieces(string identity, Manifest manifest)
    {
        for (var index = 0; index < manifest.Count; index++) TryDelete(PieceName(identity, manifest.Generation, index));
    }

    /// <summary>Pieces an interrupted write left behind, found by name and removed.</summary>
    private void SweepOrphans(string identity, string? keepGeneration)
    {
        foreach (var name in store.StoredIdentifiers())
        {
            if (!name.StartsWith(identity + ".", StringComparison.Ordinal)) continue;
            if (keepGeneration is not null && name.StartsWith($"{identity}.{keepGeneration}.", StringComparison.Ordinal)) continue;
            TryDelete(name);
        }
    }

    private void TryDelete(string name)
    {
        try
        {
            store.Delete(name);
        }
        catch (SecretsException)
        {
            // Cleanup of pieces nothing points at; a leftover is harmless and swept next time.
        }
    }

    private static RemoteOAuthException Corrupt() => new(RemoteOAuthError.CredentialStore);

    private sealed record Manifest(string Generation, int Count, string Hash)
    {
        internal static Manifest Parse(string text) => TryParse(text) ?? throw Corrupt();

        internal static Manifest? TryParse(string text)
        {
            var parts = text.Split(' ');
            if (parts.Length != 4 || parts[0] != ManifestTag ||
                parts[1].Length != 16 || !parts[1].All(Uri.IsHexDigit) ||
                !int.TryParse(parts[2], out var count) || count is < 1 or > 64 ||
                parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit))
            {
                return null;
            }
            return new Manifest(parts[1], count, parts[3]);
        }
    }
}
