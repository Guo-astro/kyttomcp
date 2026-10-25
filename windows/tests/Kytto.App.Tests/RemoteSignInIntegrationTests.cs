using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

/// <summary>
/// Remote sign-in through the real <c>AppModel</c> and the real IPC handlers, against a
/// scripted "world" of one MCP server and one OAuth provider. Only the loopback
/// callback is real sockets: the "browser" is a fake that makes the request a browser
/// would. No real network, provider or credential store is involved.
/// </summary>
public sealed class RemoteSignInIntegrationTests : IDisposable
{
    private const string Url = "https://mcp.example/mcp";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-remote-signin-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeWorld _world = new();
    private readonly FakeUi _ui = new();
    private readonly InMemorySecretStore _store = new();
    private string ConfigPath => Path.Combine(_home, ".cursor", "mcp.json");

    private AppModel NewModel(string definition = """{ "url": "https://mcp.example/mcp" }""")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, $$"""{ "mcpServers": { "remote": {{definition}} } }""");
        var browser = new FakeBrowser();
        var model = new AppModel(
            new KyttoPaths(Path.Combine(_home, "KyttoData")),
            _home,
            new AllInstalledApps(),
            new InMemorySecretStore(),
            authorizationPageLauncher: browser,
            watchConfigs: false)
        {
            RemoteSecretStoreOverride = _store,
            RemoteOAuthClientOverride = new RemoteOAuthClient(_world),
            HealthCheckerOverride = new HealthChecker(httpClient: new McpHttpClient(_world)),
            RemoteSignInUi = _ui,
        };
        _browser = browser;
        return model;
    }

    private FakeBrowser _browser = null!;

    private static bool NeedsAuthorization(AppModel model) =>
        model.ServerById("remote").Health?.Status == HealthStatus.NeedsAuthorization;

    // MARK: - The whole flow

    [Fact]
    public async Task AnExplicitSignInStoresAnAuthorizationSecurelyAndTheNextCheckPasses()
    {
        using var model = NewModel();
        var before = File.ReadAllBytes(ConfigPath);

        var unauthorized = await model.CheckHealthAsync("remote");
        Assert.Equal(HealthStatus.NeedsAuthorization, unauthorized.Status);
        Assert.Empty(_store.StoredIdentifiers());

        await model.SignInRemoteAsync("remote", _ui);

        // The person was told who they were signing in with, from validated metadata.
        Assert.Equal("remote", _ui.ShownServer);
        // The label is the configuration's own word; the address is what the token is for.
        Assert.Equal(Url, _ui.ShownResource);
        Assert.Equal("https://auth.example/tenant", _ui.ShownIssuer);
        Assert.Equal(["read"], _ui.ShownScopes);

        // One browser page, HTTPS, PKCE S256, bound to the exact resource and a loopback redirect.
        var page = Assert.Single(_browser.Opened);
        Assert.StartsWith("https://auth.example/authorize?", page.AbsoluteUri, StringComparison.Ordinal);
        var query = _browser.Query(page);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Url, query["resource"]);
        Assert.Equal("code", query["response_type"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);

        // Saved under Kytto's own prefix as checked pieces; no token or URL in any name.
        Assert.NotEmpty(_store.StoredIdentifiers());
        Assert.All(_store.StoredIdentifiers(), name =>
        {
            Assert.StartsWith("oauth.resource.", name, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp.example", name, StringComparison.Ordinal);
        });
        Assert.All(_store.StoredIdentifiers(), name =>
            Assert.DoesNotContain("access-1", _store.Value(name), StringComparison.Ordinal));

        // The sign-in ended with a check that used it.
        var health = model.ServerById("remote").Health!;
        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(["remote_tool"], health.Tools.Select(tool => tool.Name));
        Assert.Contains("Bearer access-1", _world.AuthorizationHeadersSeenByTheServer);

        // Nothing about it reaches the web layer.
        var state = JsonSerializer.Serialize(model.ToStateDto(), Envelope.Json);
        foreach (var secret in new[] { "access-1", "refresh-1", "authcode", "code_verifier" })
        {
            Assert.DoesNotContain(secret, state, StringComparison.Ordinal);
        }

        // And no client configuration was touched, or backed up.
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(model.Backups.List(ClientId.Cursor));

        // The loopback port is gone with the sign-in.
        var redirect = new Uri(query["redirect_uri"]);
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => new HttpClient().GetAsync(redirect + "?state=x"));
    }

    [Fact]
    public async Task NothingRunsUntilTheUserAsks()
    {
        using var model = NewModel();

        // Discovery of configs, reloads, state building and DTOs: no request, no browser, no prompt.
        model.Reload();
        _ = model.ToStateDto();
        _ = model.DoctorReports();

        Assert.Empty(_world.Requests);
        Assert.Empty(_browser.Opened);
        Assert.Empty(_store.StoredIdentifiers());
        Assert.Equal(0, _ui.Prompts);
    }

    [Fact]
    public async Task DecliningTheConsentChangesNothingAndOpensNoBrowser()
    {
        using var model = NewModel();
        _ui.Confirm = false;

        await model.SignInRemoteAsync("remote", _ui);

        Assert.Empty(_browser.Opened);
        Assert.Empty(_store.StoredIdentifiers());
        Assert.Null(model.ServerById("remote").Health);
        // The single-flight guard is released, so the person can try again.
        _ui.Confirm = true;
        await model.SignInRemoteAsync("remote", _ui);
        Assert.Single(_browser.Opened);
    }

    [Fact]
    public async Task AProviderWithoutRegistrationAsksForAPublicClientIdAndDecliningStopsQuietly()
    {
        using var model = NewModel();
        _world.RegistrationEndpoint = null;
        _ui.ClientId = null;

        await model.SignInRemoteAsync("remote", _ui);
        Assert.Equal(1, _ui.ClientPrompts);
        Assert.StartsWith("http://127.0.0.1:", _ui.PromptedRedirect, StringComparison.Ordinal);
        Assert.Empty(_browser.Opened);
        Assert.Empty(_store.StoredIdentifiers());

        _ui.ClientId = "  entered-public-client  ";
        await model.SignInRemoteAsync("remote", _ui);
        Assert.Equal("entered-public-client", _browser.Query(Assert.Single(_browser.Opened))["client_id"]);
        Assert.Equal(0, _world.Registrations);
    }

    [Fact]
    public async Task AProviderThatRegistersClientsIsNeverAskedForAClientId()
    {
        using var model = NewModel();

        await model.SignInRemoteAsync("remote", _ui);

        Assert.Equal(0, _ui.ClientPrompts);
        Assert.Equal(1, _world.Registrations);
        Assert.Equal("registered-client", _browser.Query(Assert.Single(_browser.Opened))["client_id"]);
    }

    [Fact]
    public async Task AClientSecretIsNeverAcceptedFromARegistration()
    {
        using var model = NewModel();
        _world.RegistrationReply = """{"client_id":"c","client_secret":"shh"}""";

        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() => model.SignInRemoteAsync("remote", _ui));

        Assert.Equal(RemoteOAuthError.InvalidClient, failure.Error);
        Assert.Empty(_browser.Opened);
        Assert.Empty(_store.StoredIdentifiers());
    }

    // MARK: - Concurrency

    [Fact]
    public async Task OnlyOneSignInRunsAtATimeAndTheGuardIsReleasedAfterwards()
    {
        using var model = NewModel();
        var release = new TaskCompletionSource();
        _browser.WaitBeforeCallback = release.Task;

        var first = model.SignInRemoteAsync("remote", _ui);
        await _browser.OpenedFirst;

        var second = await Assert.ThrowsAsync<BadArgumentException>(() => model.SignInRemoteAsync("remote", _ui));
        Assert.Equal("A sign-in is already open.", second.Message);

        release.SetResult();
        await first;
        Assert.Single(_browser.Opened);

        // After it ends, the next one is allowed.
        _browser.WaitBeforeCallback = null;
        await model.SignInRemoteAsync("remote", _ui);
        Assert.Equal(2, _browser.Opened.Count);
    }

    [Fact]
    public async Task ASimultaneousSecondCommandThroughTheRouterIsRefusedToo()
    {
        using var model = NewModel();
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);
        var release = new TaskCompletionSource();
        _browser.WaitBeforeCallback = release.Task;

        var first = router.InvokeForTests("health.signInRemote", """{ "serverID": "remote" }""");
        await _browser.OpenedFirst;
        var second = await router.InvokeForTests("health.signInRemote", """{ "serverID": "remote" }""");
        release.SetResult();
        var completed = await first;

        Assert.Contains("\"ok\":false", second, StringComparison.Ordinal);
        Assert.Contains("A sign-in is already open.", second, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", completed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallbackDeliveredTwiceOrWithAWrongStateCompletesTheSignInExactlyOnce()
    {
        using var model = NewModel();
        _browser.SendStrayCallbacks = true;

        await model.SignInRemoteAsync("remote", _ui);

        Assert.Equal(1, _world.CodeExchanges);
        Assert.Equal(HealthStatus.Passed, model.ServerById("remote").Health!.Status);
    }

    [Fact]
    public async Task GivingUpOnTheBrowserEndsTheSignInClosesTheListenerAndStoresNothing()
    {
        using var model = NewModel();
        _ui.CancelWhileWaiting = true;
        _browser.SuppressCallback = true;

        var failure = await Assert.ThrowsAsync<BadArgumentException>(() => model.SignInRemoteAsync("remote", _ui));

        Assert.Contains("cancelled", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_store.StoredIdentifiers());
        var redirect = new Uri(_browser.Query(Assert.Single(_browser.Opened))["redirect_uri"]);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new HttpClient().GetAsync(redirect + "?state=x"));
        // And the next sign-in is allowed.
        _ui.CancelWhileWaiting = false;
        _browser.SuppressCallback = false;
        await model.SignInRemoteAsync("remote", _ui);
        Assert.Equal(HealthStatus.Passed, model.ServerById("remote").Health!.Status);
    }

    [Fact]
    public async Task SigningInToAServerThatIsNotStreamableHttpIsRefused()
    {
        using var model = NewModel("""{ "command": "node", "args": ["server.js"] }""");

        var failure = await Assert.ThrowsAsync<BadArgumentException>(() => model.SignInRemoteAsync("remote", _ui));

        Assert.Contains("Streamable HTTP", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_world.Requests);
    }

    // MARK: - Refresh, forget and races

    /// <summary>
    /// Signs in with a token that has just over a minute to live: not due when the
    /// sign-in's own check runs, due a couple of seconds later. Refresh is judged on
    /// the real clock, so the test waits rather than faking one.
    /// </summary>
    private async Task SignedIn(AppModel model, bool dueSoon = false)
    {
        if (dueSoon) _world.ExpiresIn = 62;
        await model.SignInRemoteAsync("remote", _ui);
        if (dueSoon) await Task.Delay(2500);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefreshedOnlyByAnExplicitCheckAndRotationIsKept()
    {
        using var model = NewModel();
        await SignedIn(model, dueSoon: true);
        Assert.Equal(0, _world.Refreshes);

        // Nothing renews it in the background.
        model.Reload();
        _ = model.ToStateDto();
        Assert.Equal(0, _world.Refreshes);

        var health = await model.CheckHealthAsync("remote");

        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(1, _world.Refreshes);
        Assert.Equal("refresh-1", _world.LastRefreshTokenPresented);
        Assert.Contains("Bearer access-2", _world.AuthorizationHeadersSeenByTheServer);

        // A rotated refresh token replaced the saved one: the next refresh presents it.
        await Task.Delay(2500);
        await model.CheckHealthAsync("remote");
        Assert.Equal(2, _world.Refreshes);
        Assert.Equal("refresh-2", _world.LastRefreshTokenPresented);
    }

    [Fact]
    public async Task ARefreshThatTheProviderRefusesBecomesAPlainSignInAgainState()
    {
        using var model = NewModel();
        await SignedIn(model, dueSoon: true);
        _world.RefuseRefresh = true;

        var health = await model.CheckHealthAsync("remote");

        Assert.Equal(HealthStatus.NeedsAuthorization, health.Status);
        Assert.Contains("Sign in again", health.CurrentMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-", health.CurrentMessage, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.NeedsAuthorization, model.ServerById("remote").Health!.Status);
    }

    [Fact]
    public async Task ForgetDeletesOnlyKyttosSavedSignInAndTheNextCheckNeedsAuthorizationAgain()
    {
        using var model = NewModel();
        await SignedIn(model);
        Assert.NotEmpty(_store.StoredIdentifiers());

        model.ForgetRemoteAuthorization("remote");

        Assert.Empty(_store.StoredIdentifiers());
        var health = await model.CheckHealthAsync("remote");
        Assert.Equal(HealthStatus.NeedsAuthorization, health.Status);
        // Forgetting again is not an error.
        model.ForgetRemoteAuthorization("remote");
    }

    [Fact]
    public async Task ForgetDuringAnInFlightRefreshCannotBeUndoneByIt()
    {
        using var model = NewModel();
        await SignedIn(model, dueSoon: true);
        _world.HoldRefresh();

        var check = model.CheckHealthAsync("remote");
        await _world.RefreshStarted;

        model.ForgetRemoteAuthorization("remote");
        _world.ReleaseRefresh();
        await Assert.ThrowsAsync<AuthorizationChangedException>(() => check);

        // The refresh finished after the forget and stored nothing.
        Assert.Empty(_store.StoredIdentifiers());
        Assert.Equal(HealthStatus.NeedsAuthorization, (await model.CheckHealthAsync("remote")).Status);
    }

    [Fact]
    public async Task ANewSignInIsNotLostToAnOlderRefreshFinishingAfterIt()
    {
        using var model = NewModel();
        await SignedIn(model, dueSoon: true);
        _world.HoldRefresh();

        var check = model.CheckHealthAsync("remote");
        await _world.RefreshStarted;

        // The person signs in again while the old refresh is still out.
        var newSignIn = model.SignInRemoteAsync("remote", _ui);
        await newSignIn;
        var current = new RemoteOAuthCredentials(_store).Load(Url)!;
        var signedInWith = current.AuthorizationHeaders["Authorization"];

        _world.ReleaseRefresh();
        await Assert.ThrowsAsync<AuthorizationChangedException>(() => check);

        Assert.Equal(
            signedInWith,
            new RemoteOAuthCredentials(_store).Load(Url)!.AuthorizationHeaders["Authorization"]);
    }

    [Fact]
    public async Task AUnreadableSavedAuthorizationReadsAsSignInAgainNotACrash()
    {
        using var model = NewModel();
        await SignedIn(model);
        var piece = _store.StoredIdentifiers().First(name => name.Count(c => c == '.') >= 4);
        _store.Store("not-the-piece-that-was-written", piece);

        var health = await model.CheckHealthAsync("remote");

        Assert.Equal(HealthStatus.NeedsAuthorization, health.Status);
        Assert.Contains("Sign in again", health.CurrentMessage, StringComparison.Ordinal);
    }

    // MARK: - Stale results

    [Fact]
    public async Task ACheckThatFinishesAfterTheServerWasEditedIsNotRecordedAgainstIt()
    {
        using var model = NewModel();
        _world.HoldMcp();

        var check = model.CheckHealthAsync("remote");
        await _world.McpStarted;

        // Same name, different definition: the name alone is not what made it stale.
        File.WriteAllText(ConfigPath, """{ "mcpServers": { "remote": { "url": "https://mcp.example/other" } } }""");
        model.Reload();
        _world.ReleaseMcp();
        await check;

        Assert.Null(model.ServerById("remote").Health);
    }

    [Fact]
    public async Task ACheckThatFinishesAfterTheServerWasRemovedGetsNothingBack()
    {
        using var model = NewModel();
        _world.HoldMcp();

        var check = model.CheckHealthAsync("remote");
        await _world.McpStarted;
        File.WriteAllText(ConfigPath, """{ "mcpServers": { } }""");
        model.Reload();
        _world.ReleaseMcp();
        await check;

        Assert.Empty(model.Current().Servers);
        Assert.Null(new MetadataStore(new KyttoPaths(Path.Combine(_home, "KyttoData"))).For("remote"));
    }

    [Fact]
    public async Task ACheckStartedBeforeASignInCompletesIsNotRecordedAfterIt()
    {
        using var model = NewModel();
        _world.HoldMcp();

        var check = model.CheckHealthAsync("remote");
        await _world.McpStarted;
        // The held request is the check's first, sent without any authorization.
        // The person signs in meanwhile, which also checks, and passes.
        await model.SignInRemoteAsync("remote", _ui);
        _world.ReleaseMcp();
        await check;

        // The recorded health is the sign-in's own check (passed), not the stale 401.
        Assert.Equal(HealthStatus.Passed, model.ServerById("remote").Health!.Status);
    }

    // MARK: - IPC

    [Fact]
    public async Task TheCommandsAreRegisteredAndReportedAsCapabilities()
    {
        using var model = NewModel();
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        using var info = JsonDocument.Parse(await router.InvokeForTests("app.info"));
        var capabilities = info.RootElement.GetProperty("data").GetProperty("capabilities")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("health.http", capabilities);
        Assert.Contains("health.oauth", capabilities);

        var signIn = await router.InvokeForTests("health.signInRemote", """{ "serverID": "remote" }""");
        using var reply = JsonDocument.Parse(signIn);
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(reply.RootElement.GetProperty("data").TryGetProperty("servers", out _));
        Assert.DoesNotContain("access-1", signIn, StringComparison.Ordinal);

        var forget = await router.InvokeForTests("health.forgetRemoteAuthorization", """{ "serverID": "remote" }""");
        Assert.Equal("""{"ok":true,"data":{}}""", forget);
        Assert.Empty(_store.StoredIdentifiers());

        var unknown = await router.InvokeForTests("health.forgetRemoteAuthorization", """{ "serverID": "nope" }""");
        Assert.Contains("\"ok\":false", unknown, StringComparison.Ordinal);
        var missing = await router.InvokeForTests("health.signInRemote", "{}");
        Assert.Contains("\"ok\":false", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAllCoversStdioAndHttpAndSkipsLegacySse()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """
            { "mcpServers": {
              "remote": { "url": "https://mcp.example/mcp" },
              "legacy": { "url": "https://mcp.example/sse", "type": "sse" },
              "local": { "command": "kytto-definitely-missing-command" }
            } }
            """);
        using var model = new AppModel(
            new KyttoPaths(Path.Combine(_home, "KyttoData")), _home, new AllInstalledApps(),
            new InMemorySecretStore(), watchConfigs: false)
        {
            RemoteSecretStoreOverride = _store,
            HealthCheckerOverride = new HealthChecker(httpClient: new McpHttpClient(_world)),
        };
        _world.AllowAnonymous = true;
        var names = new List<string>();

        await model.CheckAllHealthAsync((name, _, _) => names.Add(name));

        Assert.Contains("remote", names);
        Assert.Contains("local", names);
        Assert.DoesNotContain("legacy", names);
        Assert.Equal(HealthStatus.Passed, model.ServerById("remote").Health!.Status);
        Assert.Null(model.ServerById("legacy").Health);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // MARK: - The fake world

    private sealed class AllInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => true;
        public bool PackageExists(string familyName) => true;
        public bool ExecutableExists(string name, string home) => true;
    }

    private sealed class FakeUi : IRemoteSignInUi
    {
        public bool Confirm { get; set; } = true;
        public string? ClientId { get; set; } = "entered-client";
        public bool CancelWhileWaiting { get; set; }
        public string? ShownServer { get; private set; }
        public string? ShownResource { get; private set; }
        public string? ShownIssuer { get; private set; }
        public IReadOnlyList<string>? ShownScopes { get; private set; }
        public string? PromptedRedirect { get; private set; }
        public int Prompts { get; private set; }
        public int ClientPrompts { get; private set; }

        public bool ConfirmSignIn(string serverName, string resource, string issuer, IReadOnlyList<string> scopes)
        {
            Prompts++;
            (ShownServer, ShownResource, ShownIssuer, ShownScopes) = (serverName, resource, issuer, scopes);
            return Confirm;
        }

        public string? PromptClientId(string redirectUri)
        {
            Prompts++;
            ClientPrompts++;
            PromptedRedirect = redirectUri;
            return ClientId;
        }

        public IRemoteSignInWait ShowWaiting(string issuer)
        {
            var source = new CancellationTokenSource();
            if (CancelWhileWaiting) source.CancelAfter(TimeSpan.FromMilliseconds(100));
            return new Wait(source);
        }

        private sealed class Wait(CancellationTokenSource source) : IRemoteSignInWait
        {
            public CancellationToken Cancelled => source.Token;
            public void Dispose() => source.Dispose();
        }
    }

    /// <summary>What the system browser does: follow the page, and make the loopback request.</summary>
    private sealed class FakeBrowser : IAuthorizationPageLauncher
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Uri> _opened = [];

        public IReadOnlyList<Uri> Opened
        {
            get
            {
                lock (_opened) return _opened.ToArray();
            }
        }

        public Task OpenedFirst => _first.Task;
        public Task? WaitBeforeCallback { get; set; }
        public bool SuppressCallback { get; set; }
        public bool SendStrayCallbacks { get; set; }

        public Dictionary<string, string> Query(Uri url) =>
            url.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2))
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

        public void Open(string url)
        {
            var page = new Uri(url);
            lock (_opened) _opened.Add(page);
            _first.TrySetResult();
            if (SuppressCallback) return;
            var wait = WaitBeforeCallback;
            _ = Task.Run(async () =>
            {
                if (wait is not null) await wait;
                var query = Query(page);
                var redirect = query["redirect_uri"];
                using var client = new HttpClient();
                if (SendStrayCallbacks)
                {
                    // A stray page in another tab, and then the real thing twice.
                    await Try(client, $"{redirect}?state=attacker&code=stolen");
                }
                var real = $"{redirect}?state={Uri.EscapeDataString(query["state"])}&code=authcode&iss={Uri.EscapeDataString("https://auth.example/tenant")}";
                await Try(client, real);
                if (SendStrayCallbacks) await Try(client, real);
            });
        }

        private static async Task Try(HttpClient client, string target)
        {
            try
            {
                using var _ = await client.GetAsync(target);
            }
            catch (HttpRequestException)
            {
                // The listener had already closed.
            }
        }
    }

    /// <summary>One MCP server and one OAuth provider, answered in memory.</summary>
    private sealed class FakeWorld : IRemoteHttpTransport
    {
        private readonly object _lock = new();
        private readonly List<string> _requests = [];
        private readonly HashSet<string> _issued = [];
        private readonly List<string> _authorizationSeen = [];
        private int _accessCounter;
        private int _refreshCounter;
        private TaskCompletionSource? _refreshGate;
        private TaskCompletionSource? _mcpGate;
        private TaskCompletionSource? _held;
        private readonly TaskCompletionSource _refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _mcpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? RegistrationEndpoint { get; set; } = "https://auth.example/register";
        public string RegistrationReply { get; set; } =
            """{"client_id":"registered-client","token_endpoint_auth_method":"none"}""";
        public int ExpiresIn { get; set; } = 3600;
        public bool RefuseRefresh { get; set; }
        public bool AllowAnonymous { get; set; }
        public int Registrations { get; private set; }
        public int CodeExchanges { get; private set; }
        public int Refreshes => _refreshCounter;
        public string? LastRefreshTokenPresented { get; private set; }

        public Task RefreshStarted => _refreshStarted.Task;
        public Task McpStarted => _mcpStarted.Task;

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_lock) return _requests.ToArray();
            }
        }

        public IReadOnlyList<string> AuthorizationHeadersSeenByTheServer
        {
            get
            {
                lock (_lock) return _authorizationSeen.ToArray();
            }
        }

        public void HoldRefresh() => _refreshGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRefresh() => _refreshGate?.TrySetResult();
        public void HoldMcp() => _held = _mcpGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseMcp() => _held?.TrySetResult();

        public async Task<RemoteHttpReply> SendAsync(
            RemoteHttpRequest request,
            DateTimeOffset deadline,
            int limit,
            int? responseId,
            CancellationToken cancellationToken)
        {
            lock (_lock) _requests.Add($"{request.Method} {request.Url.AbsoluteUri}");
            var path = request.Url.AbsolutePath;
            var host = request.Url.Host;

            if (host == "mcp.example" && path == "/mcp") return await Mcp(request);
            if (host == "mcp.example" && path.StartsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal))
            {
                return Json("""{"resource":"https://mcp.example/mcp","authorization_servers":["https://auth.example/tenant"],"scopes_supported":["read"]}""");
            }
            if (host == "auth.example" && path.StartsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal))
            {
                var registration = RegistrationEndpoint is null ? "" : $",\"registration_endpoint\":\"{RegistrationEndpoint}\"";
                return Json(
                    "{\"issuer\":\"https://auth.example/tenant\",\"authorization_endpoint\":\"https://auth.example/authorize\"," +
                    "\"token_endpoint\":\"https://auth.example/token\",\"code_challenge_methods_supported\":[\"S256\"]," +
                    "\"authorization_response_iss_parameter_supported\":true" + registration + "}");
            }
            if (host == "auth.example" && path == "/register")
            {
                Registrations++;
                return Json(RegistrationReply, 201);
            }
            if (host == "auth.example" && path == "/token") return await Token(request);
            return new RemoteHttpReply(404, new Dictionary<string, string>(), []);
        }

        private async Task<RemoteHttpReply> Token(RemoteHttpRequest request)
        {
            var form = Encoding.UTF8.GetString(request.Body!).Split('&')
                .Select(item => item.Split('=', 2))
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

            if (form["grant_type"] == "authorization_code")
            {
                CodeExchanges++;
                Assert.Equal("authcode", form["code"]);
                // PKCE: the verifier presented hashes to the challenge in the page.
                Assert.True(form["code_verifier"].Length >= 43);
                Assert.Equal(Url, form["resource"]);
            }
            else
            {
                Interlocked.Increment(ref _refreshCounter);
                LastRefreshTokenPresented = form["refresh_token"];
                _refreshStarted.TrySetResult();
                if (_refreshGate is { } gate) await gate.Task;
                if (RefuseRefresh) return Json("""{"error":"invalid_grant"}""", 400);
            }

            var index = Interlocked.Increment(ref _accessCounter);
            var access = $"access-{index}";
            lock (_lock) _issued.Add(access);
            return Json(
                $"{{\"access_token\":\"{access}\",\"token_type\":\"Bearer\",\"expires_in\":{ExpiresIn}," +
                $"\"refresh_token\":\"refresh-{index}\",\"scope\":\"read\"}}");
        }

        private async Task<RemoteHttpReply> Mcp(RemoteHttpRequest request)
        {
            var authorization = request.Headers.FirstOrDefault(header => header.Key == "Authorization").Value;
            lock (_lock)
            {
                if (authorization is not null) _authorizationSeen.Add(authorization);
            }

            var authorized = authorization is not null &&
                             authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
                             _issued.Contains(authorization["Bearer ".Length..]);
            // The first request after HoldMcp is held, so that "a check is in flight" is real.
            if (Interlocked.Exchange(ref _mcpGate, null) is { } mcpGate)
            {
                _mcpStarted.TrySetResult();
                await mcpGate.Task;
            }

            if (request.Method == HttpMethod.Delete) return new RemoteHttpReply(204, new Dictionary<string, string>(), []);
            if (!authorized && !AllowAnonymous)
            {
                return new RemoteHttpReply(
                    401,
                    new Dictionary<string, string>
                    {
                        ["www-authenticate"] =
                            "Bearer resource_metadata=\"https://mcp.example/.well-known/oauth-protected-resource/mcp\"",
                    },
                    []);
            }

            using var message = JsonDocument.Parse(request.Body!);
            var root = message.RootElement;
            var method = root.GetProperty("method").GetString();
            Assert.NotEqual("tools/call", method);
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            return method switch
            {
                "notifications/initialized" => new RemoteHttpReply(202, new Dictionary<string, string>(), []),
                "initialize" => Json(Rpc(id, """{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"}}""")),
                "tools/list" => Json(Rpc(id, """{"tools":[{"name":"remote_tool","description":"A tool.","inputSchema":{"type":"object"}}]}""")),
                _ => new RemoteHttpReply(400, new Dictionary<string, string>(), []),
            };
        }

        private static string Rpc(int id, string result) =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}";

        private static RemoteHttpReply Json(string body, int status = 200) => new(
            status,
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            Encoding.UTF8.GetBytes(body));
    }
}
