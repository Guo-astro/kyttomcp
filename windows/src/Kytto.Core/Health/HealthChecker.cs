using Kytto.Core.Model;

namespace Kytto.Core.Health;

/// <summary>Runs a server and asks it what it can do (§7.3).</summary>
/// <remarks>
/// Never on launch and never in the background: §6 is firm that spawning arbitrary
/// commands is something the user asks for, one server at a time.
/// </remarks>
public sealed class HealthChecker(
    ShellEnvironment? shell = null,
    TimeSpan? timeout = null,
    McpStdioClient? client = null,
    McpHttpClient? httpClient = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ShellEnvironment _shell = shell ?? ShellEnvironment.Current;
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly McpStdioClient _client = client ?? new McpStdioClient();
    private readonly McpHttpClient _httpClient = httpClient ?? new McpHttpClient();

    /// <summary>Checks one server; a remote one with Kytto's own sign-in, if it has one.</summary>
    /// <param name="httpHeaders">
    /// Headers Kytto itself holds for a Streamable HTTP server — its OAuth token. They
    /// replace the definition's headers of the same name, case-insensitively, and nothing
    /// else.
    /// </param>
    public (HealthResult Health, TokenWeight? TokenWeight) Check(
        Server server,
        IReadOnlyDictionary<string, string>? httpHeaders = null) =>
        server.Transport == Transport.Http
            ? CheckRemoteAsync(server, httpHeaders, CancellationToken.None).GetAwaiter().GetResult()
            : CheckLocal(server);

    /// <summary>The same check without holding a thread for a remote one.</summary>
    public Task<(HealthResult Health, TokenWeight? TokenWeight)> CheckAsync(
        Server server,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        CancellationToken cancellationToken = default) =>
        server.Transport == Transport.Http
            ? CheckRemoteAsync(server, httpHeaders, cancellationToken)
            : Task.Run(() => CheckLocal(server), cancellationToken);

    private (HealthResult Health, TokenWeight? TokenWeight) CheckLocal(Server server)
    {
        var started = Timestamp.Now;

        if (server.Transport != Transport.Stdio)
        {
            var reason = HealthFailureReason.RemoteTransport(server.Transport.Raw());
            // Remote servers are not spawned, so there is nothing to run. Saying
            // "not checked yet" would be a lie; saying why is not.
            return (new HealthResult(
                Status: HealthStatus.Unsupported,
                CheckedAt: started,
                ToolCount: null,
                Tools: [],
                Message: reason.CurrentMessage(),
                Stderr: null,
                DurationSeconds: 0,
                ServerName: null,
                ServerVersion: null)
                {
                    Failure = reason,
                }, null);
        }

        // Placeholders the owning client would expand — `${__dirname}` and friends
        // in a Claude Desktop extension — are resolved here. Running the literal
        // text would fail every extension for a reason that has nothing to do with
        // whether the server works.
        var (command, arguments) = server.Runnable;
        if (string.IsNullOrEmpty(command))
        {
            return (Failure(HealthFailureReason.NoCommand(), null, started), null);
        }
        // A relative command is only meaningful from the directory its owner runs it
        // in. When something established one — a plugin root, an absolute `cwd` — it is
        // resolved there; otherwise Kytto's own directory would answer a different
        // question than the client's does.
        if (server.HasRelativeCommand && server.WorkingDirectory is null)
        {
            return (Failure(
                HealthFailureReason.RelativeCommand(command),
                null,
                started,
                HealthStatus.Unsupported), null);
        }

        // Case-insensitive, like the environment it is merged into: a plugin's
        // CLAUDE_PLUGIN_ROOT must not sit beside a configured claude_plugin_root.
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in server.RunnableEnvironment)
        {
            if (entry.Value is { } value) extra.TryAdd(entry.Key, value);
        }

        try
        {
            var (listing, stderr) = _client.ListTools(
                command, arguments, _shell.With(extra), _timeout, server.WorkingDirectory);

            return (new HealthResult(
                Status: HealthStatus.Passed,
                CheckedAt: started,
                ToolCount: listing.Tools.Count,
                Tools: listing.Tools,
                Message: null,
                // Servers commonly log to stderr while working perfectly. Keeping
                // it on success too means the detail view can show warnings that
                // are not failures.
                Stderr: stderr.Length == 0 ? null : stderr,
                DurationSeconds: (Timestamp.Now - started).TotalSeconds,
                ServerName: listing.ServerName,
                ServerVersion: listing.ServerVersion)
                {
                    ProtocolVersion = listing.NegotiatedProtocolVersion,
                    CapabilityNames = listing.CapabilityNames,
                    PromptCount = listing.PromptCount,
                    ResourceCount = listing.ResourceCount,
                    InspectionNotes = listing.InspectionNotes,
                    ResolvedCommand = listing.ResolvedCommand,
                    EnvironmentSource = _shell.SourceDisplay,
                },
                TokenWeight.Measuring(listing.RawToolsJson));
        }
        catch (HealthFailure failure)
        {
            var reason = ReasonFor(failure.Underlying);
            var stderr = failure.Stderr.Length == 0 ? null : failure.Stderr;
            string? authorizationURL = null;
            var status = HealthStatus.Failed;
            if (AuthorizationSignal.IsFailedHandshake(reason) &&
                AuthorizationSignal.TryDetect(stderr, out authorizationURL))
            {
                reason = HealthFailureReason.AwaitingAuthorization();
                status = HealthStatus.NeedsAuthorization;
            }
            return (Failure(reason, stderr, started, status, authorizationURL) with
                {
                    ResolvedCommand = failure.ResolvedCommand,
                    EnvironmentSource = _shell.SourceDisplay,
                }, null);
        }
        catch (Exception error)
        {
            return (Failure(ReasonFor(error), null, started), null);
        }
    }

    /// <summary>Streamable HTTP: the handshake and the tool list, never a tool call (§7.3).</summary>
    /// <remarks>
    /// One deadline spans the network, the parsing and the token count. Nothing the
    /// server said is kept when it fails — not a body, not a challenge, not a JSON-RPC
    /// message — because any of them can echo a credential or a signed URL (§6).
    /// </remarks>
    private async Task<(HealthResult Health, TokenWeight? TokenWeight)> CheckRemoteAsync(
        Server server,
        IReadOnlyDictionary<string, string>? overrides,
        CancellationToken cancellationToken)
    {
        var started = Timestamp.Now;
        var clock = DateTimeOffset.UtcNow;
        var budget = _timeout < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50)
            : _timeout > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60)
            : _timeout;
        var deadline = clock + budget;

        try
        {
            var url = server.Url ?? throw new RemoteHttpException(RemoteHttpFailure.InvalidEndpoint);
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in server.Env)
            {
                if (entry.Value is { } value) extra.TryAdd(entry.Key, value);
            }
            var configured = RemoteHttpHeaders.Resolve(
                server.DefinitionSource,
                _shell.With(extra),
                omitAuthorization: overrides?.Keys.Any(name =>
                    string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)) == true);
            var headers = RemoteHttpHeaders.Merge(configured, overrides);

            var listing = await _httpClient
                .ListToolsAsync(url, headers, deadline - DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            var weight = TokenWeight.Measuring(listing.RawToolsJson, deadline);

            return (new HealthResult(
                Status: HealthStatus.Passed,
                CheckedAt: started,
                ToolCount: listing.Tools.Count,
                Tools: listing.Tools,
                Message: null,
                Stderr: null,
                DurationSeconds: (Timestamp.Now - started).TotalSeconds,
                ServerName: listing.ServerName,
                ServerVersion: listing.ServerVersion)
                {
                    ProtocolVersion = listing.NegotiatedProtocolVersion,
                    CapabilityNames = listing.CapabilityNames,
                    PromptCount = listing.PromptCount,
                    ResourceCount = listing.ResourceCount,
                    InspectionNotes = listing.InspectionNotes,
                    EnvironmentSource = RemoteEnvironmentSource,
                },
                weight);
        }
        catch (RemoteHttpException failure)
        {
            var status = failure.Failure == RemoteHttpFailure.AuthorizationRequired
                ? HealthStatus.NeedsAuthorization
                : HealthStatus.Failed;
            return (RemoteFailure(
                HealthFailureReason.RemoteHttp(failure.Failure, failure.Status, failure.Detail),
                started,
                status), null);
        }
        catch (TimeoutException)
        {
            return (RemoteFailure(HealthFailureReason.RemoteHttp(RemoteHttpFailure.TimedOut), started), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (RemoteFailure(HealthFailureReason.RemoteHttp(RemoteHttpFailure.TimedOut), started), null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Deliberately not the exception's own text: see the remarks above.
            return (RemoteFailure(HealthFailureReason.Other("The remote check could not be completed."), started), null);
        }
    }

    /// <summary>What the detail view shows as the "environment" of a remote check.</summary>
    internal const string RemoteEnvironmentSource = "Streamable HTTP";

    private static HealthResult RemoteFailure(
        HealthFailureReason reason,
        DateTimeOffset started,
        HealthStatus status = HealthStatus.Failed) =>
        Failure(reason, null, started, status) with { EnvironmentSource = RemoteEnvironmentSource };

    private static HealthResult Failure(
        HealthFailureReason reason,
        string? stderr,
        DateTimeOffset started,
        HealthStatus status = HealthStatus.Failed,
        string? authorizationURL = null) => new(
        Status: status,
        CheckedAt: started,
        ToolCount: null,
        Tools: [],
        Message: reason.CurrentMessage(),
        Stderr: stderr,
        DurationSeconds: (Timestamp.Now - started).TotalSeconds,
        ServerName: null,
        ServerVersion: null)
        {
            Failure = reason,
            AuthorizationURL = authorizationURL,
        };

    private HealthFailureReason ReasonFor(Exception error)
    {
        if (error is ProcessException process)
        {
            return process.Failure.Reason == "commandNotFound"
                ? process.Failure with { SearchedPreferredPATH = _shell.PreferredPathAvailable }
                : process.Failure;
        }
        if (error is McpException mcp) return mcp.Failure;
        if (error is IOException or ObjectDisposedException) return HealthFailureReason.InputClosed();
        return HealthFailureReason.Other(error.Message);
    }
}
