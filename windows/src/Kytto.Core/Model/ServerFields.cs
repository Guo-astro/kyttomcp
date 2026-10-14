using Kytto.Core.Json;
using Kytto.Core.Toml;

namespace Kytto.Core.Model;

/// <summary>
/// One client's server entry, reduced to the handful of things Kytto models.
/// </summary>
/// <remarks>
/// <para>
/// §4: never assume two clients accept the same shape. The fields overlap heavily
/// in practice, but the differences that exist — VS Code's <c>servers</c> key,
/// Cursor's <c>envFile</c>/<c>headers</c>, extensions' <c>${__dirname}</c> args,
/// Codex spelling the whole thing in TOML — are why this normalizes on read
/// instead of passing raw dictionaries around.
/// </para>
/// <para>
/// Reading is the only thing that varies per format. Everything after it — how a
/// transport is inferred, what a fingerprint is made of — happens once, here, so a
/// second spelling of a config file cannot quietly grow a second set of rules.
/// </para>
/// </remarks>
public sealed class ServerFields
{
    public string? Command { get; init; }
    public string? Url { get; init; }

    /// <summary><c>type</c> as the file states it, if it does.</summary>
    public string? DeclaredType { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<EnvEntry> Env { get; init; } = [];

    /// <summary><c>cwd</c> exactly as the definition spells it, if it does.</summary>
    /// <remarks>
    /// Raw on purpose. Whether a relative or placeholder-bearing directory can be
    /// resolved depends on a context only the owning client — or a registered plugin
    /// root — supplies, so only an absolute one becomes a <see cref="Server"/> property
    /// here and the rest is left to the code that has that context (§4).
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <param name="sourceText">
    /// The definition exactly as it appears in the file it came from, which is what
    /// gets parked and what gets copied verbatim into another client of the same
    /// format.
    /// </param>
    public Server MakeServer(
        string name,
        Origin origin,
        bool isBundled,
        string sourceText,
        bool isReadOnly = false) => new()
    {
        Id = Server.Identity(name),
        Name = name,
        Transport = ResolveTransport(),
        Command = Command,
        Args = Args,
        Env = Env,
        Url = Url,
        EnabledIn = [],
        Origin = origin,
        Fingerprint = Server.MakeFingerprint(Command, Args, Url),
        IsBundled = isBundled,
        IsReadOnly = isReadOnly,
        DefinitionSource = sourceText,
        Provenance = ServerProvenance.Infer(
            ResolveTransport(), Command, Args, Url),
        // A relative cwd needs client context, and an ordinary config cannot supply
        // one. Plugin discovery resolves it from its registered root instead.
        WorkingDirectory = AbsoluteDirectory(WorkingDirectory),
    };

    /// <summary>
    /// A drive-letter or UNC directory, normalised lexically; anything else is null.
    /// </summary>
    /// <remarks>
    /// <c>/work/demo</c> and <c>\work</c> are rooted but not absolute on Windows — they
    /// mean "on the current drive", which is Kytto's, not the client's — so they are
    /// refused here rather than resolved against the wrong place.
    /// </remarks>
    internal static string? AbsoluteDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return null;
        if (!Path.IsPathFullyQualified(value)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>type</c> when the client states it, otherwise inferred. Clients disagree
    /// on whether <c>type</c> is required, and older entries predate it entirely.
    /// </summary>
    internal Transport ResolveTransport()
    {
        if (Transports.FromRaw(DeclaredType) is { } declared) return declared;
        if (Url is not { } url) return Transport.Stdio;
        if (Command is not null) return Transport.Stdio;
        // A bare URL is HTTP unless the endpoint says otherwise; `/sse` is the
        // convention the older transport used.
        return url.EndsWith("/sse", StringComparison.Ordinal) ? Transport.Sse : Transport.Http;
    }

    // MARK: - JSON

    public static ServerFields FromJson(JsonNode node) => new()
    {
        Command = node["command"]?.StringValue,
        Url = node["url"]?.StringValue,
        DeclaredType = node["type"]?.StringValue,
        WorkingDirectory = node["cwd"]?.StringValue,
        Args = node["args"]?.Elements?
            .Select(element => element.StringValue)
            .OfType<string>()
            .ToArray() ?? [],
        Env = (node["env"]?.Members ?? [])
            .Select(member => new EnvEntry(member.Key, member.Value.StringValue))
            .ToArray(),
    };

    // MARK: - TOML

    /// <param name="envTable">
    /// The server's <c>[….env]</c> sub-table, when it has one. TOML lets the same
    /// data be written either as a sub-table or as an inline <c>env = { … }</c>,
    /// and a config that has been edited by hand may hold either.
    /// </param>
    public static ServerFields FromToml(TomlTable table, TomlTable? envTable)
    {
        IReadOnlyList<EnvEntry> env = [];
        if (envTable is not null)
        {
            env = envTable.Pairs
                .Where(pair => pair.Name is not null)
                .Select(pair => new EnvEntry(pair.Name!, pair.Value.StringValue))
                .ToArray();
        }
        else if (table.Value("env")?.InlinePairs is { } inline)
        {
            env = inline
                .Where(pair => pair.Name is not null)
                .Select(pair => new EnvEntry(pair.Name!, pair.Value.StringValue))
                .ToArray();
        }

        return new ServerFields
        {
            Command = table.Value("command")?.StringValue,
            Url = table.Value("url")?.StringValue,
            DeclaredType = table.Value("type")?.StringValue,
            WorkingDirectory = table.Value("cwd")?.StringValue,
            Args = table.Value("args")?.Elements?
                .Select(element => element.StringValue)
                .OfType<string>()
                .ToArray() ?? [],
            Env = env,
        };
    }

    /// <summary>A server written as <c>name = { … }</c> under its map table.</summary>
    public static ServerFields FromTomlInline(IReadOnlyList<TomlPair> pairs)
    {
        // Reuse the table reader: an inline table has the same key/value pairs and
        // the same nested `env = { … }` spelling, only no header of its own.
        var table = new TomlTable([], null, pairs, new TomlSpan(0, 0), false);
        return FromToml(table, envTable: null);
    }
}
