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
    /// <summary>Names the provider and the scopes and asks whether to continue.</summary>
    bool ConfirmSignIn(string serverName, string issuer, IReadOnlyList<string> scopes);

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
        IReadOnlyDictionary<string, string>? headers = null;
        if (server.Transport == Transport.Http && server.Url is { } url)
        {
            resource = TryCanonicalResource(url);
        }

        if (resource is not null)
        {
            generation = RemoteGeneration(resource);
            try
            {
                headers = await SavedAuthorizationHeadersAsync(resource, generation).ConfigureAwait(true);
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

        var (health, weight) = await checker.CheckAsync(server, headers).ConfigureAwait(true);

        // The check can take ten seconds and the window stays usable meanwhile. A
        // server deleted, renamed or edited in that time — or signed in or out — must
        // not get this result recorded against it.
        if (!StillCurrent(server.Id, token, resource, generation)) return health;
        _metadata.Record(server.Id, health, weight);
        Reload();
        return health;
    }

    private async Task<IReadOnlyDictionary<string, string>?> SavedAuthorizationHeadersAsync(string resource, long generation)
    {
        var credential = RemoteVault.Load(resource);
        if (credential is null) return null;

        if (credential.RequiresRefresh(DateTimeOffset.UtcNow))
        {
            var client = RemoteOAuthClientOverride ?? new RemoteOAuthClient();
            var renewed = await client.RefreshAsync(credential).ConfigureAwait(true);
            lock (_remoteLock)
            {
                // A refresh that lost a race with Forget or a new sign-in must not
                // put its tokens back.
                if (RemoteGeneration(resource) != generation) throw new AuthorizationChangedException();
                RemoteVault.Store(renewed);
            }
            credential = renewed;
        }
        return credential.AuthorizationHeaders;
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

            var configuration = await client.DiscoverAsync(url, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (!ui.ConfirmSignIn(server.Name, configuration.Issuer, configuration.Scopes)) return;

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
                _authorizationPageLauncher.Open(pending.AuthorizationUrl.AbsoluteUri);
                try
                {
                    callback = await listener
                        .WaitForCallbackAsync(pending.State, RemoteOAuthPendingAuthorization.Lifetime, linked.Token)
                        .ConfigureAwait(true);
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
