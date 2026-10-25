using System.IO;
using System.Security.Cryptography;
using System.Text;
using Kytto.App.Ipc;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Secrets;

namespace Kytto.App;

/// <summary>What a person is asked, and shown, while Kytto signs in to a remote server.</summary>
/// <remarks>
/// An interface so the flow is testable without a window; the WPF implementation is
/// <see cref="RemoteSignInController"/>. Every method runs on the UI thread and every
/// value it is given is text from a server, to be shown as text.
/// </remarks>
internal interface IRemoteSignInUi
{
    /// <summary>
    /// Names the server, the address the authorization will be sent to, the provider and
    /// the scopes, and asks whether to continue.
    /// </summary>
    bool ConfirmSignIn(string serverName, string resource, string issuer, IReadOnlyList<string> scopes);

    /// <summary>Asks for a public client ID, for a provider that does not register clients itself.</summary>
    string? PromptClientId(string redirectUri);

    /// <summary>Shows that Kytto is waiting for the browser, with a way to give up.</summary>
    IRemoteSignInWait ShowWaiting(string issuer);
}

internal interface IRemoteSignInWait : IDisposable
{
    /// <summary>Cancelled when the person gives up waiting.</summary>
    CancellationToken Cancelled { get; }
}

/// <summary>
/// Remote Doctor's native half: explicit OAuth sign-in, Kytto's own saved
/// authorization, and keeping a slow check from overwriting what changed under it
/// (§7.3, §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this never does.</b> It does not run at launch, does not change any client
/// configuration, and does not sign any other MCP client in: a sign-in here authorizes
/// Kytto's own checks only, and is stored under Kytto's own prefix in the credential
/// store. Nothing secret, and no endpoint, ever reaches the web layer.
/// </para>
/// <para>
/// <b>Generations.</b> Each resource has a counter. Signing in, refreshing and
/// forgetting all go through <c>_remoteLock</c>, and Forget raises the counter
/// <em>before</em> it deletes — so a refresh that was already in flight finds the
/// counter changed and does not store what it fetched, and a new sign-in is never
/// undone by an older refresh finishing after it.
/// </para>
/// </remarks>
internal sealed partial class AppModel
{
    private readonly object _remoteLock = new();
    private readonly Dictionary<string, long> _remoteGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<RemoteOAuthCredential>> _remoteRefreshes = new(StringComparer.Ordinal);
    private ISecretStore? _remoteStore;
    private int _signInActive;

    /// <summary>Tests only: where Kytto's remote sign-ins are kept.</summary>
    internal ISecretStore? RemoteSecretStoreOverride { get; set; }

    /// <summary>Tests only: the checker, so a check can run against a scripted transport.</summary>
    internal HealthChecker? HealthCheckerOverride { get; set; }

    /// <summary>Tests only: the OAuth client, so a sign-in can run against a local fake.</summary>
    internal RemoteOAuthClient? RemoteOAuthClientOverride { get; set; }

    /// <summary>The prompts for a sign-in. Null means the real windows.</summary>
    internal IRemoteSignInUi? RemoteSignInUi { get; set; }

    /// <summary>
    /// Kytto's own store for remote sign-ins, apart from the secrets screen's and the
    /// Gateway's, so forgetting one can never touch another.
    /// </summary>
    /// <remarks>
    /// An in-memory secret store means the model is running under the isolated test
    /// harness; that isolation extends here instead of reaching the real Credential
    /// Manager (§6).
    /// </remarks>
    private ISecretStore RemoteStore => RemoteSecretStoreOverride ?? (_remoteStore ??=
        _secretStore is InMemorySecretStore
            ? new InMemorySecretStore()
            : new WindowsCredentialStore("Kytto.RemoteOAuth"));

    private RemoteOAuthCredentials RemoteVault => new(RemoteStore);

    // MARK: - Checking

    /// <summary>Runs one check with Kytto's saved sign-in, if there is one, and records it.</summary>
    /// <remarks>
    /// Nothing is renewed except here, because a check was asked for. A result for a
    /// server that was deleted, renamed, edited or re-signed-in meanwhile is returned
    /// but not recorded: the name alone is not what made it stale, so the definition
    /// and the authorization generation are compared as well.
    /// </remarks>
    private async Task<HealthResult> RecordHealthAsync(Server server)
    {
        var checker = HealthCheckerOverride ?? new HealthChecker();
        var token = DefinitionToken(server);

        string? resource = null;
        var generation = 0L;
        RemoteOAuthCredential? credential = null;
        var refreshed = false;
        if (server.Transport == Transport.Http && server.Url is { } url)
        {
            resource = TryCanonicalResource(url);
        }

        if (resource is not null)
        {
            generation = RemoteGeneration(resource);
            try
            {
                credential = RemoteVault.Load(resource);
                if (credential is not null && credential.RequiresRefresh(DateTimeOffset.UtcNow))
                {
                    credential = await RefreshedCredentialAsync(credential, resource, generation).ConfigureAwait(true);
                    refreshed = true;
                }
            }
            catch (Exception error) when (error is RemoteOAuthException or SecretsException)
            {
                if (RemoteGeneration(resource) != generation) throw new AuthorizationChangedException();
                var unreadable = AuthorizationUnusable();
                if (StillCurrent(server.Id, token, resource, generation))
                {
                    _metadata.Record(server.Id, unreadable, null);
                    Reload();
                }
                return unreadable;
            }
        }

        var checkedServer = DirectDefinitionForCheck(server);
        var (health, weight) = await checker.CheckAsync(checkedServer, credential?.AuthorizationHeaders).ConfigureAwait(true);

        // A token revoked early, or one issued without an expiry, shows up only as the
        // server refusing it. One refresh and one retry, not a loop.
        if (health.Status == HealthStatus.NeedsAuthorization && !refreshed && resource is not null &&
            credential is { CanRefresh: true } saved)
        {
            RemoteOAuthCredential? renewed = null;
            try
            {
                renewed = await RefreshedCredentialAsync(saved, resource, generation).ConfigureAwait(true);
            }
            catch (Exception error) when (error is RemoteOAuthException or SecretsException)
            {
                // The refusal stands as the result.
            }
            if (renewed is not null)
            {
                (health, weight) = await checker.CheckAsync(checkedServer, renewed.AuthorizationHeaders).ConfigureAwait(true);
            }
        }

        // The check can take ten seconds and the window stays usable meanwhile. A
        // server deleted, renamed or edited in that time — or signed in or out — must
        // not get this result recorded against it.
        if (!StillCurrent(server.Id, token, resource, generation)) return health;
        _metadata.Record(server.Id, health, weight);
        Reload();
        return health;
    }

    /// <summary>One refresh per resource at a time.</summary>
    /// <remarks>
    /// Refresh tokens rotate, so two checks of the same remote refreshing in parallel
    /// would spend the same token twice — and an authorization server that detects the
    /// replay revokes the whole grant. A later caller waits for the running refresh.
    /// </remarks>
    private async Task<RemoteOAuthCredential> RefreshedCredentialAsync(
        RemoteOAuthCredential saved,
        string resource,
        long generation)
    {
        Task<RemoteOAuthCredential> refresh;
        var started = false;
        lock (_remoteLock)
        {
            if (!_remoteRefreshes.TryGetValue(resource, out refresh!))
            {
                var client = RemoteOAuthClientOverride ?? new RemoteOAuthClient();
                refresh = client.RefreshAsync(saved);
                _remoteRefreshes[resource] = refresh;
                started = true;
            }
        }

        RemoteOAuthCredential renewed;
        try
        {
            renewed = await refresh.ConfigureAwait(true);
        }
        finally
        {
            if (started)
            {
                lock (_remoteLock) _remoteRefreshes.Remove(resource);
            }
        }

        lock (_remoteLock)
        {
            // A refresh that lost a race with Forget or a new sign-in must not put its
            // tokens back.
            if (RemoteGeneration(resource) != generation) throw new AuthorizationChangedException();
            if (started) RemoteVault.Store(renewed);
        }
        return renewed;
    }

    /// <summary>The server a Gateway route stands for, so a check measures the server rather than the helper.</summary>
    /// <remarks>
    /// Checked through the helper, <c>tools/list</c> comes back already masked: hidden
    /// tools would vanish from the tool picker (and saving it unchanged would drop the
    /// mask), Contract Guard would see tools removed, and every check would log a client
    /// session in Live Activity that no client started.
    /// </remarks>
    private Server DirectDefinitionForCheck(Server server)
    {
        if (server.Transport != Transport.Stdio || server.Command is not { } command ||
            !Path.GetFileNameWithoutExtension(command).Equals("kytto-mcp-proxy", StringComparison.OrdinalIgnoreCase))
        {
            return server;
        }
        var flag = server.Args.ToList().IndexOf("--route");
        if (flag < 0 || flag + 1 >= server.Args.Count) return server;
        try
        {
            var launch = _gatewayRoutes.Resolve(server.Args[flag + 1], _gatewaySecretStore);
            return new Server
            {
                Id = server.Id,
                Name = server.Name,
                Transport = Transport.Stdio,
                Command = launch.Route.Command,
                Args = launch.Route.Arguments,
                Env = launch.Environment
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new EnvEntry(pair.Key, pair.Value))
                    .ToArray(),
                Url = null,
                EnabledIn = server.EnabledIn,
                Origin = server.Origin,
                Fingerprint = server.Fingerprint,
                IsBundled = server.IsBundled,
                IsReadOnly = server.IsReadOnly,
                DefinitionSource = server.DefinitionSource,
                DefinitionsByClient = server.DefinitionsByClient,
            };
        }
        catch (Exception error) when (error is Kytto.Core.Gateway.GatewayRouteException or SecretsException or
                                              IOException or UnauthorizedAccessException)
        {
            // An unresolvable route is checked as configured, which reports the problem.
            return server;
        }
    }

    /// <summary>The saved credential's headers and scopes, for a step-up sign-in.</summary>
    private (IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Scopes)? RemoteAuthorizationContext(string url)
    {
        if (TryCanonicalResource(url) is not { } resource) return null;
        try
        {
            return RemoteVault.Load(resource) is { } credential
                ? (credential.AuthorizationHeaders, credential.Scopes)
                : null;
        }
        catch (Exception error) when (error is RemoteOAuthException or SecretsException)
        {
            return null;
        }
    }

    private static HealthResult AuthorizationUnusable() => new(
        HealthStatus.NeedsAuthorization,
        Timestamp.Now,
        ToolCount: null,
        Tools: [],
        Message: null,
        Stderr: null,
        DurationSeconds: 0,
        ServerName: null,
        ServerVersion: null)
    {
        Failure = HealthFailureReason.Other(
            "Kytto's saved authorization could not be refreshed or read. Sign in again."),
        EnvironmentSource = HealthChecker.RemoteEnvironmentSource,
    };

    private bool StillCurrent(string serverId, string token, string? resource, long generation)
    {
        var latest = _current.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
        return latest is not null &&
               DefinitionToken(latest) == token &&
               (resource is null || RemoteGeneration(resource) == generation);
    }

    /// <summary>A fingerprint of everything that decides what a check talks to.</summary>
    /// <remarks>
    /// Compared, never shown, and never leaves the process: it covers environment
    /// values and headers, which is exactly why it is a hash. The name alone is not
    /// enough — a server renamed, edited and given the old name back is not the server
    /// a slow check started against.
    /// </remarks>
    internal static string DefinitionToken(Server server)
    {
        var text = new StringBuilder();
        void Add(params string?[] parts)
        {
            foreach (var part in parts) text.Append(part ?? "\u0000").Append('\u001f');
            text.Append('\u001e');
        }

        Add(server.Id, server.Transport.Raw(), server.Command, server.Url, server.DefinitionSource);
        Add([.. server.Args]);
        foreach (var entry in server.Env) Add(entry.Key, entry.Value);
        foreach (var (name, value) in server.Placeholders.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Add(name, value);
        foreach (var (client, copy) in server.DefinitionsByClient.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            Add(client.ToString(), copy.Transport.Raw(), copy.Command, copy.Url, copy.DefinitionSource);
            Add([.. copy.Args]);
            foreach (var entry in copy.Env) Add(entry.Key, entry.Value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // MARK: - Authorization state

    private long RemoteGeneration(string resource)
    {
        lock (_remoteLock) return _remoteGenerations.GetValueOrDefault(resource);
    }

    private static string? TryCanonicalResource(string url)
    {
        try
        {
            return RemoteOAuthClient.CanonicalResource(url);
        }
        catch (RemoteHttpException)
        {
            return null;
        }
    }

    private string RemoteResource(string serverId)
    {
        var server = ServerById(serverId);
        if (server.Transport != Transport.Http || server.Url is not { } url)
        {
            throw new BadArgumentException("No Streamable HTTP server was selected.");
        }
        return RemoteOAuthClient.CanonicalResource(url);
    }

    /// <summary>Deletes only Kytto's own saved sign-in for this server's exact resource.</summary>
    /// <remarks>
    /// The generation is raised first, under the same lock the writers hold, and the
    /// delete happens inside it: nothing in flight can store after the increment, and
    /// nothing can store between the increment and the delete.
    /// </remarks>
    internal void ForgetRemoteAuthorization(string serverId)
    {
        var resource = RemoteResource(serverId);
        lock (_remoteLock)
        {
            _remoteGenerations[resource] = _remoteGenerations.GetValueOrDefault(resource) + 1;
            RemoteVault.Delete(resource);
        }
    }

    private void SaveRemoteAuthorization(RemoteOAuthCredential credential, string serverId, string definitionToken)
    {
        lock (_remoteLock)
        {
            var latest = _current.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
            if (latest is null || DefinitionToken(latest) != definitionToken)
            {
                throw new InvalidOperationException(
                    "The server changed while you were signing in. Nothing was saved; sign in again.");
            }
            var resource = latest.Url is { } url ? TryCanonicalResource(url) : null;
            if (resource is null || resource != credential.Configuration.ResourceUrl)
            {
                throw new RemoteOAuthException(RemoteOAuthError.ResourceMismatch);
            }
            // A new sign-in wins over any refresh that started before it.
            _remoteGenerations[resource] = _remoteGenerations.GetValueOrDefault(resource) + 1;
            RemoteVault.Store(credential);
        }
    }

    // MARK: - Signing in

    /// <summary>
    /// An explicit sign-in: discovery, consent, a public client, the system browser, a
    /// loopback callback, token exchange, secure storage, then a check.
    /// </summary>
    /// <remarks>
    /// One at a time. Declining the consent or the client-ID prompt changes nothing and
    /// is not an error; giving up on the browser is.
    /// </remarks>
    internal async Task SignInRemoteAsync(string serverId, IRemoteSignInUi ui, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _signInActive, 1, 0) != 0)
        {
            throw new BadArgumentException("A sign-in is already open.");
        }

        var signedIn = false;
        try
        {
            var server = ServerById(serverId);
            if (server.Transport != Transport.Http || server.Url is not { } url)
            {
                throw new BadArgumentException("No Streamable HTTP server was selected.");
            }
            var token = DefinitionToken(server);
            var client = RemoteOAuthClientOverride ?? new RemoteOAuthClient();

            // Signing in again with a credential on file is how a server that answered
            // 403 insufficient_scope gets the scope it asked for.
            var saved = RemoteAuthorizationContext(url);
            var configuration = await client.DiscoverAsync(
                url,
                cancellationToken: cancellationToken,
                authorization: saved?.Headers,
                previousScopes: saved?.Scopes).ConfigureAwait(true);
            if (!ui.ConfirmSignIn(server.Name, configuration.ResourceUrl, configuration.Issuer, configuration.Scopes)) return;

            // The listener exists from here until this method ends, however it ends.
            using var listener = OAuthLoopbackListener.Start();

            string clientId;
            if (configuration.RegistrationEndpoint is not null)
            {
                clientId = await client.RegisterAsync(configuration, listener.RedirectUri, cancellationToken).ConfigureAwait(true);
            }
            else if (ui.PromptClientId(listener.RedirectUri) is { } entered && entered.Trim() is { Length: > 0 } trimmed)
            {
                clientId = trimmed;
            }
            else
            {
                return;
            }

            var pending = client.PrepareAuthorization(configuration, clientId, listener.RedirectUri);
            // Only a validated HTTPS page is ever handed to the browser.
            if (!AuthorizationSignal.IsValidAuthorizationURL(pending.AuthorizationUrl.AbsoluteUri) ||
                !string.IsNullOrEmpty(pending.AuthorizationUrl.UserInfo))
            {
                throw new RemoteOAuthException(RemoteOAuthError.Discovery);
            }

            Uri callback;
            using (var waiting = ui.ShowWaiting(configuration.Issuer))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waiting.Cancelled))
            {
                // Waiting starts before the browser does, so the listener already knows
                // the state when a provider that is signed in redirects straight back.
                var waitingForCallback = listener.WaitForCallbackAsync(
                    pending.State, RemoteOAuthPendingAuthorization.Lifetime, linked.Token);
                _authorizationPageLauncher.Open(pending.AuthorizationUrl.AbsoluteUri);
                try
                {
                    callback = await waitingForCallback.ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new BadArgumentException("Sign-in was cancelled or could not complete.");
                }
            }

            var credential = await client.ExchangeAsync(pending, callback, cancellationToken).ConfigureAwait(true);
            SaveRemoteAuthorization(credential, serverId, token);
            signedIn = true;
        }
        finally
        {
            Interlocked.Exchange(ref _signInActive, 0);
        }

        // As on macOS: a successful sign-in is followed by a check, so the person sees
        // what the new authorization does.
        if (signedIn) _ = await CheckHealthAsync(serverId).ConfigureAwait(true);
    }
}

/// <summary>A check found that its authorization changed while it ran.</summary>
internal sealed class AuthorizationChangedException()
    : InvalidOperationException("Authorization changed while the check was running. Check the server again.");
