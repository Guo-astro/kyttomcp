using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Kytto.Core.Health;
using Kytto.Core.Secrets;
using static Kytto.Core.Tests.ScriptedTransport;

namespace Kytto.Core.Tests;

/// <summary>Native OAuth for remote servers: discovery, PKCE, callback, tokens, storage (§7.3, §6).</summary>
public class RemoteOAuthTests
{
    private const string Redirect = "http://127.0.0.1:41999/oauth/callback";
    private const string Resource = "https://mcp.example/mcp";

    private const string ResourceMetadata =
        """{"resource":"https://mcp.example/mcp","authorization_servers":["https://auth.example/tenant"],"scopes_supported":["wide"]}""";

    private const string ServerMetadata =
        """{"issuer":"https://auth.example/tenant","authorization_endpoint":"https://auth.example/authorize","token_endpoint":"https://auth.example/token","code_challenge_methods_supported":["S256"],"authorization_response_iss_parameter_supported":true}""";

    private static RemoteOAuthConfiguration Configuration(
        bool issuerParameter = true,
        string? registration = null,
        string[]? scopes = null) => new(
        Resource,
        "https://auth.example/tenant",
        "https://auth.example/authorize",
        "https://auth.example/token",
        registration,
        scopes ?? ["read"],
        issuerParameter);

    private static Uri Callback(
        RemoteOAuthPendingAuthorization pending,
        string issuer = "https://auth.example/tenant",
        string? state = null,
        string code = "code with space",
        string path = "/oauth/callback",
        int port = 41999,
        bool includeIssuer = true)
    {
        var originalState = Query(pending.AuthorizationUrl)["state"];
        var query = $"state={Uri.EscapeDataString(state ?? originalState)}&code={Uri.EscapeDataString(code)}";
        if (includeIssuer) query += $"&iss={Uri.EscapeDataString(issuer)}";
        return new Uri($"http://127.0.0.1:{port}{path}?{query}");
    }

    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    private const string Tokens =
        """{"access_token":"access-fixture","token_type":"Bearer","expires_in":120,"refresh_token":"refresh-fixture","scope":"read"}""";

    // MARK: - Discovery

    [Fact]
    public async Task DiscoveryUsesChallengeScopesAndValidatesTheIssuer()
    {
        var transport = new ScriptedTransport(Reply(ResourceMetadata), Reply(status: 404), Reply(ServerMetadata));
        var config = await new RemoteOAuthClient(transport).DiscoverAsync(
            Resource,
            """Bearer resource_metadata="https://mcp.example/meta", scope="read" """);

        Assert.Equal(["read"], config.Scopes);
        Assert.Equal("https://auth.example/tenant", config.Issuer);
        Assert.True(config.AuthorizationResponseIssuerSupported);
        Assert.Equal(
            ["/meta", "/.well-known/oauth-authorization-server/tenant", "/.well-known/openid-configuration/tenant"],
            transport.Requests.Select(request => request.Url.AbsolutePath));
        // Nothing the user configured for the endpoint is ever sent to discovery.
        Assert.All(transport.Requests, request => Assert.DoesNotContain(request.Headers, h => h.Key == "Authorization"));
    }

    [Fact]
    public async Task WithoutAChallengeTheEndpointIsAskedOnceWithNoCredentialsAndTheWellKnownPathIsTried()
    {
        var transport = new ScriptedTransport(
            Reply("", 401, new Dictionary<string, string> { ["www-authenticate"] = "Bearer realm=\"mcp\"" }),
            Reply(status: 404),
            Reply(ResourceMetadata),
            Reply(ServerMetadata));

        var config = await new RemoteOAuthClient(transport).DiscoverAsync(Resource);

        Assert.Equal(["wide"], config.Scopes);
        Assert.Equal(HttpMethod.Post, transport.Requests[0].Method);
        Assert.DoesNotContain(transport.Requests[0].Headers, header => header.Key == "Authorization");
        Assert.Equal(
            ["/mcp", "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource", "/.well-known/oauth-authorization-server/tenant"],
            transport.Requests.Select(request => request.Url.AbsolutePath));
    }

    [Fact]
    public async Task TheFirstAdvertisedIssuerIsTheOneUsed()
    {
        var metadata = ResourceMetadata.Replace(
            "\"https://auth.example/tenant\"",
            "\"https://auth.example/tenant\",\"https://other.example\"",
            StringComparison.Ordinal);
        var config = await new RemoteOAuthClient(new ScriptedTransport(Reply(metadata), Reply(ServerMetadata)))
            .DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta\"");
        Assert.Equal("https://auth.example/tenant", config.Issuer);
    }

    [Theory]
    [InlineData("issuer", RemoteOAuthError.IssuerMismatch)]
    [InlineData("pkce", RemoteOAuthError.PkceUnsupported)]
    [InlineData("code", RemoteOAuthError.Discovery)]
    [InlineData("insecure-token", RemoteOAuthError.Discovery)]
    [InlineData("missing-authorization", RemoteOAuthError.Discovery)]
    public async Task ServerMetadataThatIsNotRightStopsTheSignIn(string flaw, RemoteOAuthError expected)
    {
        var metadata = flaw switch
        {
            "issuer" => ServerMetadata.Replace("\"issuer\":\"https://auth.example/tenant\"", "\"issuer\":\"https://attacker.example\"", StringComparison.Ordinal),
            "pkce" => ServerMetadata.Replace("S256", "plain", StringComparison.Ordinal),
            "code" => ServerMetadata.Replace("\"code_challenge_methods", "\"response_types_supported\":[\"token\"],\"code_challenge_methods", StringComparison.Ordinal),
            "insecure-token" => ServerMetadata.Replace("https://auth.example/token", "http://auth.example/token", StringComparison.Ordinal),
            _ => ServerMetadata.Replace("\"authorization_endpoint\":\"https://auth.example/authorize\",", "", StringComparison.Ordinal),
        };
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(new ScriptedTransport(Reply(ResourceMetadata), Reply(metadata)))
                .DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta\""));
        Assert.Equal(expected, failure.Error);
    }

    /// <summary>
    /// Path-inserted metadata answers 401 from auth middleware; the root document
    /// describes the origin, which RFC 9728 allows, and is what the token is for.
    /// </summary>
    [Fact]
    public async Task RootMetadataForTheOriginCoversAPathEndpoint()
    {
        const string resource = """{"resource":"https://mcp.example","authorization_servers":["https://auth.example"]}""";
        const string auth = """{"issuer":"https://auth.example","authorization_endpoint":"https://auth.example/authorize","token_endpoint":"https://auth.example/token","code_challenge_methods_supported":["S256"]}""";
        var transport = new ScriptedTransport(Reply(status: 401), Reply(resource), Reply(auth));
        var client = new RemoteOAuthClient(transport);

        var config = await client.DiscoverAsync("https://mcp.example/mcp/", "Bearer");

        Assert.Equal("https://mcp.example/mcp", config.ResourceUrl);
        Assert.Equal("https://mcp.example", config.Resource);
        Assert.Equal(
            ["/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource", "/.well-known/oauth-authorization-server"],
            transport.Requests.Select(request => request.Url.AbsolutePath));
        var pending = client.PrepareAuthorization(config, "c", Redirect);
        Assert.Equal("https://mcp.example", Query(pending.AuthorizationUrl)["resource"]);

        var endpoint = new Uri("https://mcp.example/mcp");
        Assert.False(RemoteOAuthClient.ResourceCovers("https://mcp.example/other", endpoint));
        Assert.False(RemoteOAuthClient.ResourceCovers("https://mcp.example/mc", endpoint));
        Assert.False(RemoteOAuthClient.ResourceCovers("https://other.example", endpoint));
        Assert.True(RemoteOAuthClient.ResourceCovers("https://mcp.example/mcp/", endpoint));
    }

    [Theory]
    [InlineData("https://MCP.Example:443/mcp/", "https://mcp.example/mcp")]
    [InlineData("https://mcp.example/", "https://mcp.example")]
    [InlineData("https://mcp.example:8443/a/b//", "https://mcp.example:8443/a/b")]
    [InlineData("https://mcp.example/mcp?workspace=a", "https://mcp.example/mcp?workspace=a")]
    public void TheCanonicalEndpointDropsTheDefaultPortAndTrailingSlashAndKeepsTheQuery(string text, string expected) =>
        Assert.Equal(expected, RemoteOAuthClient.CanonicalResource(text));

    [Fact]
    public async Task ADiscoveryUrlAnsweringWithAnErrorPageMovesOnToTheNextCandidate()
    {
        var transport = new ScriptedTransport(
            Reply("<html>sign in</html>", 200, new Dictionary<string, string> { ["content-type"] = "text/html" }),
            Reply(ResourceMetadata),
            Reply(ServerMetadata));

        var config = await new RemoteOAuthClient(transport).DiscoverAsync(Resource, "Bearer");

        Assert.Equal("https://auth.example/tenant", config.Issuer);
    }

    [Fact]
    public void TheWellKnownPathDropsATrailingSlashBeforeInsertion() =>
        Assert.Equal(
            "/.well-known/oauth-authorization-server/tenant",
            RemoteOAuthClient.WellKnown(new Uri("https://auth.example/tenant/"), "oauth-authorization-server", includeAppend: false)[0].AbsolutePath);

    /// <summary>
    /// Signing in again probes with the saved token, so a server that wants more scope
    /// says which, and the new request keeps what was already granted.
    /// </summary>
    [Fact]
    public async Task ASignInAgainProbesWithTheSavedTokenAndAsksForTheUnionOfScopes()
    {
        var transport = new ScriptedTransport(
            Reply("", 403, new Dictionary<string, string>
            {
                ["www-authenticate"] = """Bearer error="insufficient_scope", scope="write", resource_metadata="https://mcp.example/meta" """,
            }),
            Reply(ResourceMetadata),
            Reply(ServerMetadata));

        var config = await new RemoteOAuthClient(transport).DiscoverAsync(
            Resource,
            authorization: new Dictionary<string, string> { ["Authorization"] = "Bearer saved" },
            previousScopes: ["read"]);

        Assert.Equal(["read", "write"], config.Scopes);
        var probe = transport.Requests[0];
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer saved"), probe.Headers);
        Assert.Contains(new KeyValuePair<string, string>("Mcp-Method", "tools/list"), probe.Headers);
        Assert.Contains(new KeyValuePair<string, string>("MCP-Protocol-Version", "2026-07-28"), probe.Headers);
        // Discovery itself still carries nothing the user configured.
        Assert.All(transport.Requests.Skip(1), request => Assert.DoesNotContain(request.Headers, h => h.Key == "Authorization"));
    }

    [Fact]
    public async Task AResourceDescribedAsSomethingElseIsRefused()
    {
        var metadata = ResourceMetadata.Replace("https://mcp.example/mcp", "https://mcp.example/other", StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(new ScriptedTransport(Reply(metadata)))
                .DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta\""));
        Assert.Equal(RemoteOAuthError.ResourceMismatch, failure.Error);
    }

    [Fact]
    public async Task ACrossOriginMetadataHintIsNeverFollowed()
    {
        var transport = new ScriptedTransport();
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(transport).DiscoverAsync(
                Resource, "Bearer resource_metadata=\"https://different.example/meta\""));
        Assert.Equal(RemoteOAuthError.ResourceMismatch, failure.Error);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("http://auth.example")]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("https://auth.example/?x=1")]
    [InlineData("https://user:pw@auth.example")]
    // A server's word must not aim Kytto at this machine or a private address.
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://app.localhost")]
    [InlineData("https://[::1]")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.20.1.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[fd00::1]")]
    public async Task AnUnsafeIssuerIsRefusedBeforeItIsContacted(string issuer)
    {
        var metadata = ResourceMetadata.Replace("https://auth.example/tenant", issuer, StringComparison.Ordinal);
        var transport = new ScriptedTransport(Reply(metadata));
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(transport).DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta\""));
        Assert.Equal(RemoteOAuthError.Discovery, failure.Error);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("authorization_endpoint")]
    [InlineData("token_endpoint")]
    [InlineData("registration_endpoint")]
    public async Task AnEndpointAimedAtAPrivateAddressIsRefused(string field)
    {
        var metadata = ServerMetadata.Replace(
            "\"code_challenge_methods_supported\"",
            $"\"{field}\":\"https://10.1.2.3/x\",\"code_challenge_methods_supported\"",
            StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(new ScriptedTransport(Reply(ResourceMetadata), Reply(metadata)))
                .DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta\""));
        Assert.Equal(RemoteOAuthError.Discovery, failure.Error);
    }

    [Fact]
    public async Task TheResourceItselfMayLiveOnThePrivateNetwork()
    {
        // The resource is the user's own address; only a server's word is held to the rule.
        var metadata = ResourceMetadata.Replace("https://mcp.example/mcp", "https://10.0.0.8/mcp", StringComparison.Ordinal);
        var config = await new RemoteOAuthClient(new ScriptedTransport(Reply(metadata), Reply(ServerMetadata)))
            .DiscoverAsync("https://10.0.0.8/mcp", "Bearer resource_metadata=\"https://10.0.0.8/meta\"");
        Assert.Equal("https://10.0.0.8/mcp", config.ResourceUrl);
    }

    [Fact]
    public async Task AResourceWithAQueryIsNotSentToAnIssuer()
    {
        var transport = new ScriptedTransport();
        await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(transport).DiscoverAsync("https://mcp.example/mcp?token=secret-in-url"));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ADiscoveryFailureDoesNotCarryTheServersWordsOrTheUrl()
    {
        var transport = new ScriptedTransport(Reply("{\"error\":\"leaked-secret\"}", 500));
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(transport).DiscoverAsync(Resource, "Bearer resource_metadata=\"https://mcp.example/meta?sig=urlsecret\""));
        Assert.DoesNotContain("leaked-secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("urlsecret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChallengesAreParsedWithQuotedCommasAndEscapesAndRefusedWhenAmbiguous()
    {
        var parameters = RemoteOAuthClient.ChallengeParameters(
            """Basic realm="basic", Bearer resource_metadata="https://mcp.example/meta?a=b,c=d", scope="read write" """);
        Assert.Equal("read write", parameters["scope"]);
        Assert.Equal("https://mcp.example/meta?a=b,c=d", parameters["resource_metadata"]);

        Assert.Equal("a\"b", RemoteOAuthClient.ChallengeParameters("""Bearer error_description="a\"b" """)["error_description"]);
        Assert.Empty(RemoteOAuthClient.ChallengeParameters("""Bearer scope="a", scope="b" """));
        Assert.Empty(RemoteOAuthClient.ChallengeParameters("""Bearer scope="unterminated"""[..^1]));
        Assert.Empty(RemoteOAuthClient.ChallengeParameters(null));
        Assert.Empty(RemoteOAuthClient.ChallengeParameters("Basic realm=x"));
    }

    [Theory]
    [InlineData("https://MCP.example/mcp", "https://mcp.example/mcp")]
    [InlineData("https://mcp.example/", "https://mcp.example")]
    [InlineData("https://mcp.example", "https://mcp.example")]
    [InlineData("https://mcp.example:443/mcp", "https://mcp.example/mcp")]
    [InlineData("https://mcp.example:8443/mcp", "https://mcp.example:8443/mcp")]
    public void TheCanonicalResourceIsTheIdentityOfASignIn(string given, string canonical)
    {
        Assert.Equal(canonical, RemoteOAuthClient.CanonicalResource(given));
    }

    // MARK: - Registration

    [Fact]
    public async Task PublicRegistrationReturnsAClientIdAndRefusesSecretsAndOtherRedirects()
    {
        var config = Configuration(registration: "https://auth.example/register");

        var ok = new ScriptedTransport(Reply(
            $$"""{"client_id":"public-client","token_endpoint_auth_method":"none","redirect_uris":["{{Redirect}}"]}""", 201));
        Assert.Equal("public-client", await new RemoteOAuthClient(ok).RegisterAsync(config, Redirect));
        var body = Encoding.UTF8.GetString(ok.Requests[0].Body!);
        Assert.Contains("\"token_endpoint_auth_method\":\"none\"", body, StringComparison.Ordinal);
        Assert.Contains(Redirect, body, StringComparison.Ordinal);

        foreach (var reply in new[]
        {
            """{"client_id":"c","client_secret":"shh"}""",
            """{"client_id":"c","token_endpoint_auth_method":"client_secret_basic"}""",
            """{"client_id":"c","redirect_uris":["http://127.0.0.1:1/other"]}""",
            """{"client_id":""}""",
            """{"nothing":true}""",
        })
        {
            var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
                new RemoteOAuthClient(new ScriptedTransport(Reply(reply, 201))).RegisterAsync(config, Redirect));
            Assert.Equal(RemoteOAuthError.InvalidClient, failure.Error);
        }

        var none = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            new RemoteOAuthClient(new ScriptedTransport()).RegisterAsync(Configuration(), Redirect));
        Assert.Equal(RemoteOAuthError.RegistrationUnavailable, none.Error);
    }

    [Theory]
    [InlineData("https://external.example/callback")]
    [InlineData("http://localhost:41999/oauth/callback")]
    [InlineData("http://127.0.0.1/oauth/callback")]
    [InlineData("http://127.0.0.1:41999/oauth/callback?x=1")]
    [InlineData("http://127.0.0.1:41999/oauth/callback#f")]
    [InlineData("app.kytto.oauth://callback")]
    [InlineData("http://192.168.1.5:41999/oauth/callback")]
    public void OnlyALoopbackRedirectWithAPortIsAcceptable(string redirect)
    {
        var failure = Assert.Throws<RemoteOAuthException>(() =>
            new RemoteOAuthClient(new ScriptedTransport()).PrepareAuthorization(Configuration(), "client", redirect));
        Assert.Equal(RemoteOAuthError.InvalidRedirect, failure.Error);
    }

    // MARK: - PKCE, callback and tokens

    [Fact]
    public async Task PkceBindsTheCallbackIssuerResourceAndRefreshTokens()
    {
        var transport = new ScriptedTransport(
            Reply(Tokens),
            Reply("""{"access_token":"rotated-access","token_type":"bearer","expires_in":3600,"refresh_token":"rotated-refresh","scope":"read"}"""));
        var client = new RemoteOAuthClient(transport);

        var pending = client.PrepareAuthorization(Configuration(), "public-client", Redirect);
        var query = Query(pending.AuthorizationUrl);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Resource, query["resource"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("public-client", query["client_id"]);
        Assert.Equal(Redirect, query["redirect_uri"]);
        Assert.Equal("read", query["scope"]);
        Assert.Equal("https", pending.AuthorizationUrl.Scheme);
        // 32 random bytes each, URL-safe.
        Assert.Equal(43, query["state"].Length);

        var credential = await client.ExchangeAsync(pending, Callback(pending, code: "code space"));

        var body = Encoding.UTF8.GetString(transport.Requests[0].Body!);
        Assert.Equal("application/x-www-form-urlencoded", transport.Requests[0].ContentType);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example%2Fmcp", body, StringComparison.Ordinal);
        Assert.Contains("code=code%20space", body, StringComparison.Ordinal);
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString(Redirect)}", body, StringComparison.Ordinal);
        var verifier = body.Split('&').Single(item => item.StartsWith("code_verifier=", StringComparison.Ordinal))["code_verifier=".Length..];
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(expected, query["code_challenge"]);
        Assert.Equal("Bearer access-fixture", credential.AuthorizationHeaders["Authorization"]);

        // The transaction is spent: the same callback cannot be played twice.
        var replay = await Assert.ThrowsAsync<RemoteOAuthException>(() => client.ExchangeAsync(pending, Callback(pending)));
        Assert.Equal(RemoteOAuthError.ExpiredAuthorization, replay.Error);
        Assert.Single(transport.Requests);

        var renewed = await client.RefreshAsync(credential);
        Assert.Equal("Bearer rotated-access", renewed.AuthorizationHeaders["Authorization"]);
        Assert.Contains("refresh_token=refresh-fixture", Encoding.UTF8.GetString(transport.Requests[1].Body!), StringComparison.Ordinal);

        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(renewed);
        Assert.Equal(
            renewed.AuthorizationHeaders["Authorization"],
            vault.Load(Resource)!.AuthorizationHeaders["Authorization"]);
        Assert.Null(vault.Load("https://mcp.example/other"));
        Assert.All(secrets.StoredIdentifiers(), identifier =>
        {
            Assert.DoesNotContain("mcp.example", identifier, StringComparison.Ordinal);
            Assert.DoesNotContain("access", identifier, StringComparison.Ordinal);
        });
        vault.Delete(Resource);
        Assert.Null(vault.Load(Resource));
        Assert.Empty(secrets.StoredIdentifiers());
    }

    [Fact]
    public async Task TamperingIsRejectedBeforeAnyTokenRequest()
    {
        var transport = new ScriptedTransport();
        var client = new RemoteOAuthClient(transport);

        async Task Rejects(Func<RemoteOAuthPendingAuthorization, Uri> tamper, RemoteOAuthError expected)
        {
            var pending = client.PrepareAuthorization(Configuration(), "client", Redirect);
            var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() => client.ExchangeAsync(pending, tamper(pending)));
            Assert.Equal(expected, failure.Error);
        }

        await Rejects(pending => Callback(pending, issuer: "https://attacker.example"), RemoteOAuthError.IssuerMismatch);
        await Rejects(pending => Callback(pending, includeIssuer: false), RemoteOAuthError.IssuerMismatch);
        await Rejects(pending => Callback(pending, state: "wrong"), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => Callback(pending, path: "/other"), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => Callback(pending, port: 42000), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => new Uri(Callback(pending).AbsoluteUri.Replace("127.0.0.1", "localhost", StringComparison.Ordinal)), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => new Uri(Callback(pending).AbsoluteUri + "#fragment"), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => new Uri(Callback(pending).AbsoluteUri + "&state=again"), RemoteOAuthError.InvalidCallback);
        await Rejects(pending => new Uri(Callback(pending).AbsoluteUri + "&bare"), RemoteOAuthError.InvalidCallback);
        await Rejects(
            pending => new Uri($"http://127.0.0.1:41999/oauth/callback?state={Query(pending.AuthorizationUrl)["state"]}&iss={Uri.EscapeDataString("https://auth.example/tenant")}&error=access_denied"),
            RemoteOAuthError.AuthorizationRejected);
        await Rejects(
            pending => new Uri($"http://127.0.0.1:41999/oauth/callback?state={Query(pending.AuthorizationUrl)["state"]}&iss={Uri.EscapeDataString("https://auth.example/tenant")}"),
            RemoteOAuthError.InvalidCallback);

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task AnExpiredTransactionCannotBeExchanged()
    {
        var transport = new ScriptedTransport(Reply(Tokens));
        var client = new RemoteOAuthClient(transport);
        var stale = new RemoteOAuthPendingAuthorization(
            new Uri("https://auth.example/authorize"), Configuration(), "client", Redirect, "verifier", "state",
            DateTimeOffset.UtcNow - RemoteOAuthPendingAuthorization.Lifetime - TimeSpan.FromSeconds(1));

        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() =>
            client.ExchangeAsync(stale, new Uri($"{Redirect}?state=state&code=c&iss={Uri.EscapeDataString("https://auth.example/tenant")}")));

        Assert.Equal(RemoteOAuthError.ExpiredAuthorization, failure.Error);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task SimultaneousExchangesOfOneTransactionLetExactlyOneThrough()
    {
        var transport = new ScriptedTransport(Reply(Tokens), Reply(Tokens), Reply(Tokens), Reply(Tokens));
        var client = new RemoteOAuthClient(transport);
        var pending = client.PrepareAuthorization(Configuration(), "client", Redirect);
        var callback = Callback(pending);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await client.ExchangeAsync(pending, callback);
                return RemoteOAuthError.Discovery;
            }
            catch (RemoteOAuthException failure)
            {
                return failure.Error;
            }
        }));

        Assert.Single(outcomes, outcome => outcome == RemoteOAuthError.Discovery);
        Assert.Equal(3, outcomes.Count(outcome => outcome == RemoteOAuthError.ExpiredAuthorization));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task TwoTransactionsNeverShareAStateOrAVerifier()
    {
        var client = new RemoteOAuthClient(new ScriptedTransport());
        var first = client.PrepareAuthorization(Configuration(), "client", Redirect);
        var second = client.PrepareAuthorization(Configuration(), "client", Redirect);
        Assert.NotEqual(Query(first.AuthorizationUrl)["state"], Query(second.AuthorizationUrl)["state"]);
        Assert.NotEqual(Query(first.AuthorizationUrl)["code_challenge"], Query(second.AuthorizationUrl)["code_challenge"]);

        // A callback for one is not a callback for the other.
        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() => client.ExchangeAsync(second, Callback(first)));
        Assert.Equal(RemoteOAuthError.InvalidCallback, failure.Error);
    }

    [Theory]
    [InlineData("""{"access_token":"a","token_type":"mac"}""")]
    [InlineData("""{"access_token":"","token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"has space","token_type":"Bearer"}""")]
    [InlineData("""{"token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","expires_in":-5}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","expires_in":"soon"}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","expires_in":999999999}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","refresh_token":"bad token"}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","access_token":"b"}""")]
    public async Task UnusableTokenResponsesAreRefused(string response)
    {
        var client = new RemoteOAuthClient(new ScriptedTransport(Reply(response)));
        var pending = client.PrepareAuthorization(Configuration(), "client", Redirect);

        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() => client.ExchangeAsync(pending, Callback(pending)));

        Assert.Equal(RemoteOAuthError.InvalidToken, failure.Error);
        Assert.DoesNotContain("has space", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 6749 §3.3: the server may grant other scopes than asked for. What it granted
    /// is what is recorded; an expiry may arrive as text, and null means none.
    /// </summary>
    [Fact]
    public async Task GrantedScopesAreRecordedAndAStringExpiryIsRead()
    {
        var client = new RemoteOAuthClient(new ScriptedTransport(
            Reply("""{"access_token":"a","token_type":"bearer","expires_in":"3600","scope":"read default"}"""),
            Reply("""{"access_token":"b","token_type":"Bearer","expires_in":null}""")));

        var pending = client.PrepareAuthorization(Configuration(), "public-client", Redirect);
        var credential = await client.ExchangeAsync(pending, Callback(pending));
        Assert.Equal(["default", "read"], credential.Scopes);
        Assert.NotNull(credential.ExpiresAt);

        var second = client.PrepareAuthorization(Configuration(), "public-client", Redirect);
        Assert.Null((await client.ExchangeAsync(second, Callback(second))).ExpiresAt);
    }

    [Fact]
    public async Task ARegistrationNamesANativeApplication()
    {
        var transport = new ScriptedTransport(Reply("""{"client_id":"registered"}""", 201));

        await new RemoteOAuthClient(transport).RegisterAsync(Configuration(registration: "https://auth.example/register"), Redirect);

        Assert.Contains("\"application_type\":\"native\"", Encoding.UTF8.GetString(transport.Requests[0].Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedTokenIsRefusedNotTruncated()
    {
        var response = "{\"access_token\":\"" + new string('a', 17 * 1024) + "\",\"token_type\":\"Bearer\"}";
        var client = new RemoteOAuthClient(new ScriptedTransport(Reply(response)));
        var pending = client.PrepareAuthorization(Configuration(), "client", Redirect);

        await Assert.ThrowsAsync<RemoteOAuthException>(() => client.ExchangeAsync(pending, Callback(pending)));
    }

    [Fact]
    public async Task AGrantWithNoScopeAdvertisedIsAcceptedAndRefreshKeepsTheOldRefreshTokenWhenNoneIsReturned()
    {
        var transport = new ScriptedTransport(
            Reply("""{"access_token":"first","token_type":"Bearer","refresh_token":"keep-me","scope":"anything"}"""),
            Reply("""{"access_token":"second","token_type":"Bearer"}"""),
            Reply("""{"error":"invalid_grant"}""", 400));
        var client = new RemoteOAuthClient(transport);
        var pending = client.PrepareAuthorization(Configuration(scopes: []), "client", Redirect);
        var credential = await client.ExchangeAsync(pending, Callback(pending));
        Assert.DoesNotContain("scope=", pending.AuthorizationUrl.Query, StringComparison.Ordinal);

        var renewed = await client.RefreshAsync(credential);
        Assert.Equal("Bearer second", renewed.AuthorizationHeaders["Authorization"]);
        Assert.Contains("refresh_token=keep-me", Encoding.UTF8.GetString(transport.Requests[1].Body!), StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<RemoteOAuthException>(() => client.RefreshAsync(renewed));
        Assert.Equal(RemoteOAuthError.RefreshUnavailable, failure.Error);
    }

    [Fact]
    public void ACredentialNeverPrintsItsTokens()
    {
        var credential = Credential("super-secret-access", "super-secret-refresh");

        Assert.DoesNotContain("super-secret", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", Configuration().ToString(), StringComparison.Ordinal);
        Assert.False(credential.RequiresRefresh(DateTimeOffset.UtcNow));
        // An hour of life left, refreshed once it is within the last minute.
        Assert.False(credential.RequiresRefresh(DateTimeOffset.UtcNow.AddMinutes(58)));
        Assert.True(credential.RequiresRefresh(DateTimeOffset.UtcNow.AddMinutes(59.5)));
    }

    // MARK: - Secure storage

    private static RemoteOAuthCredential Credential(string access, string refresh, string resource = Resource) =>
        new(
            Configuration() with { ResourceUrl = resource },
            "public-client",
            ["read"],
            DateTimeOffset.UtcNow.AddHours(1),
            access,
            refresh);

    [Fact]
    public void ARecordFarPastAnItemLimitIsSplitCheckedAndReassembled()
    {
        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        var access = new string('a', 9_000);
        var refresh = new string('r', 7_000);

        vault.Store(Credential(access, refresh));

        // Every stored item fits a Credential Manager blob (2,560 bytes, stored as UTF-16).
        var identifiers = secrets.StoredIdentifiers().ToArray();
        Assert.True(identifiers.Length > 10);
        Assert.All(identifiers, identifier => Assert.True(secrets.Value(identifier)!.Length * 2 <= 2_560, identifier));
        // And none of them is the token, in the clear or in a name.
        Assert.All(identifiers, identifier =>
        {
            Assert.DoesNotContain("aaaa", identifier, StringComparison.Ordinal);
            Assert.DoesNotContain("aaaaaaaa", secrets.Value(identifier), StringComparison.Ordinal);
        });
        Assert.Equal("Bearer " + access, vault.Load(Resource)!.AuthorizationHeaders["Authorization"]);
    }

    [Fact]
    public void ARecordTooLargeToStoreSafelyIsRefusedAndChangesNothing()
    {
        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(Credential("small", "small"));
        var before = secrets.StoredIdentifiers().OrderBy(name => name).ToArray();

        var failure = Assert.Throws<RemoteOAuthException>(() => vault.Store(Credential(new string('a', 60_000), "r")));

        Assert.Equal(RemoteOAuthError.CredentialStore, failure.Error);
        Assert.Equal(before, secrets.StoredIdentifiers().OrderBy(name => name).ToArray());
        Assert.Equal("Bearer small", vault.Load(Resource)!.AuthorizationHeaders["Authorization"]);
    }

    [Fact]
    public void ATamperedOrMissingPieceIsRefusedNeverPartlyUsed()
    {
        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(Credential(new string('a', 3_000), "refresh"));
        var piece = secrets.StoredIdentifiers().First(name => name.Count(c => c == '.') >= 4 || name.EndsWith(".1", StringComparison.Ordinal));

        var original = secrets.Value(piece)!;
        secrets.Store(original[..^2] + "AA", piece);
        Assert.Equal(RemoteOAuthError.CredentialStore, Assert.Throws<RemoteOAuthException>(() => vault.Load(Resource)).Error);

        secrets.Delete(piece);
        Assert.Equal(RemoteOAuthError.CredentialStore, Assert.Throws<RemoteOAuthException>(() => vault.Load(Resource)).Error);

        var manifest = secrets.StoredIdentifiers().Single(name => name.Count(c => c == '.') == 2);
        secrets.Store("garbage", manifest);
        Assert.Equal(RemoteOAuthError.CredentialStore, Assert.Throws<RemoteOAuthException>(() => vault.Load(Resource)).Error);
    }

    [Fact]
    public void AnInterruptedWriteLeavesThePreviousRecordWholeAndNoLeftovers()
    {
        var secrets = new FailingStore(new InMemorySecretStore());
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(Credential(new string('o', 4_000), "old-refresh"));
        var before = secrets.Inner.StoredIdentifiers().OrderBy(name => name).ToArray();

        secrets.FailAfter(3);
        Assert.Throws<RemoteOAuthException>(() => vault.Store(Credential(new string('n', 4_000), "new-refresh")));
        secrets.FailAfter(null);

        Assert.Equal(before, secrets.Inner.StoredIdentifiers().OrderBy(name => name).ToArray());
        Assert.Equal("Bearer " + new string('o', 4_000), vault.Load(Resource)!.AuthorizationHeaders["Authorization"]);

        // A later successful write replaces it and sweeps the old pieces.
        vault.Store(Credential("replacement", "r"));
        Assert.Equal(2, secrets.Inner.StoredIdentifiers().Count);
        Assert.Equal("Bearer replacement", vault.Load(Resource)!.AuthorizationHeaders["Authorization"]);
    }

    [Fact]
    public void DeleteRemovesEveryPieceIncludingLeftovers()
    {
        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(Credential(new string('a', 5_000), "r"));
        var identity = secrets.StoredIdentifiers().Single(name => name.Count(c => c == '.') == 2);
        secrets.Store("stray", identity + ".deadbeefdeadbeef.0");
        vault.Store(Credential("other", "r", "https://mcp.example/other"));

        vault.Delete(Resource);

        Assert.Null(vault.Load(Resource));
        Assert.DoesNotContain(secrets.StoredIdentifiers(), name => name.StartsWith(identity, StringComparison.Ordinal));
        // Another resource's record is not touched.
        Assert.NotNull(vault.Load("https://mcp.example/other"));
    }

    [Fact]
    public void ARecordThatClaimsAnotherResourceIsRefused()
    {
        var secrets = new InMemorySecretStore();
        var vault = new RemoteOAuthCredentials(secrets);
        vault.Store(Credential("a", "r", "https://mcp.example/a"));
        // Move the record under another resource's name: it must not be honoured there.
        var from = RemoteOAuthCredentials.Identity("https://mcp.example/a");
        var to = RemoteOAuthCredentials.Identity("https://mcp.example/b");
        foreach (var name in secrets.StoredIdentifiers().ToArray())
        {
            secrets.Store(secrets.Value(name)!, to + name[from.Length..]);
        }

        Assert.Equal(RemoteOAuthError.CredentialStore, Assert.Throws<RemoteOAuthException>(() => vault.Load("https://mcp.example/b")).Error);
    }

    private sealed class FailingStore(InMemorySecretStore inner) : ISecretStore
    {
        private int? _remaining;

        public InMemorySecretStore Inner { get; } = inner;

        public void FailAfter(int? writes) => _remaining = writes;

        public void Store(string value, string id)
        {
            if (_remaining is { } remaining)
            {
                if (remaining <= 0) throw new SecretsException("Credential Manager refused to store the value (error 1312).");
                _remaining = remaining - 1;
            }
            Inner.Store(value, id);
        }

        public string? Value(string id) => Inner.Value(id);
        public void Delete(string id) => Inner.Delete(id);
        public IReadOnlySet<string> StoredIdentifiers() => Inner.StoredIdentifiers();
    }

    // MARK: - The loopback callback

    private static async Task<string> Raw(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[16 * 1024];
        var read = await stream.ReadAsync(buffer);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string Get(int port, string target, string? host = null) =>
        $"GET {target} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\nConnection: close\r\n\r\n";

    [Fact]
    public async Task TheListenerIsBoundToLoopbackOnlyAndCompletesOnTheFirstValidCallback()
    {
        using var listener = OAuthLoopbackListener.Start();
        Assert.StartsWith("http://127.0.0.1:", listener.RedirectUri, StringComparison.Ordinal);
        Assert.EndsWith("/oauth/callback", listener.RedirectUri, StringComparison.Ordinal);
        RemoteOAuthClient.ValidateRedirect(listener.RedirectUri);

        var waiting = listener.WaitForCallbackAsync("the-state", TimeSpan.FromSeconds(10), CancellationToken.None);

        // Noise that is not the sign-in changes nothing and does not end the wait.
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=wrong")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=the-state&state=the-state")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 404", await Raw(listener.Port, Get(listener.Port, "/oauth/other?state=the-state")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 404", await Raw(listener.Port, Get(listener.Port, "/favicon.ico")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=the-state", host: "attacker.example")), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=the-state", host: "localhost:" + listener.Port)), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 405", await Raw(listener.Port, $"POST /oauth/callback?state=the-state HTTP/1.1\r\nHost: 127.0.0.1:{listener.Port}\r\nContent-Length: 0\r\n\r\n"), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, "garbage\r\n\r\n"), StringComparison.Ordinal);
        Assert.False(waiting.IsCompleted);

        var reply = await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=the-state&code=abc&iss=https%3A%2F%2Fauth.example"));
        Assert.StartsWith("HTTP/1.1 200", reply, StringComparison.Ordinal);
        // The page shown back reflects nothing from the request, and says how it may be framed and cached.
        Assert.DoesNotContain("abc", reply, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store", reply, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", reply, StringComparison.Ordinal);

        var callback = await waiting;
        Assert.Equal(listener.Port, callback.Port);
        Assert.Equal("/oauth/callback", callback.AbsolutePath);
        Assert.Contains("code=abc", callback.Query, StringComparison.Ordinal);

        // After it is spent the port is closed: a replay finds nothing to talk to.
        await Task.Delay(100);
        await Assert.ThrowsAnyAsync<Exception>(() => Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=the-state&code=again")));
    }

    [Fact]
    public async Task TheListenerGivesUpOnTimeoutAndOnCancelAndClosesItsPort()
    {
        using (var listener = OAuthLoopbackListener.Start())
        {
            var failure = await Assert.ThrowsAsync<RemoteOAuthException>(
                () => listener.WaitForCallbackAsync("s", TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.Equal(RemoteOAuthError.ExpiredAuthorization, failure.Error);
        }

        int port;
        using (var cancelled = OAuthLoopbackListener.Start())
        {
            port = cancelled.Port;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled.WaitForCallbackAsync("s", TimeSpan.FromSeconds(30), cancellation.Token));
        }
        await Assert.ThrowsAnyAsync<SocketException>(() => Raw(port, Get(port, "/oauth/callback?state=s")));
    }

    [Fact]
    public async Task ConcurrentValidCallbacksCompleteExactlyOnce()
    {
        using var listener = OAuthLoopbackListener.Start();
        var waiting = listener.WaitForCallbackAsync("race", TimeSpan.FromSeconds(10), CancellationToken.None);

        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                return (await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=race&code=x")))[..12];
            }
            catch (Exception)
            {
                return "refused";
            }
        }));

        Assert.NotNull(await waiting);
        Assert.Single(replies, reply => reply.StartsWith("HTTP/1.1 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOversizedRequestIsNotBuffered()
    {
        using var listener = OAuthLoopbackListener.Start();
        _ = listener.WaitForCallbackAsync("s", TimeSpan.FromSeconds(10), CancellationToken.None);

        // Either the refusal is read, or the connection is dropped on the unread rest:
        // what must not happen is that the request is buffered and honoured.
        try
        {
            var reply = await Raw(listener.Port, "GET /oauth/callback?state=" + new string('a', 20_000) + " HTTP/1.1\r\nHost: 127.0.0.1\r\n");
            Assert.StartsWith("HTTP/1.1 431", reply, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or SocketException)
        {
        }
        Assert.StartsWith("HTTP/1.1 400", await Raw(listener.Port, Get(listener.Port, "/oauth/callback?state=nope")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidCallbackIsKeptWhenTheBrowserResetsTheConnectionBeforeReadingTheReply()
    {
        // The request that reached the listener was still the one valid, single-use
        // callback. Losing it because the reply could not be written would leave the
        // sign-in waiting for its whole lifetime for a callback that cannot come again.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var listener = OAuthLoopbackListener.Start();
            var waiting = listener.WaitForCallbackAsync("s", TimeSpan.FromSeconds(10), CancellationToken.None);
            using (var client = new TcpClient { LingerState = new LingerOption(true, 0) })
            {
                await client.ConnectAsync("127.0.0.1", listener.Port);
                await client.GetStream().WriteAsync(
                    Encoding.ASCII.GetBytes(Get(listener.Port, "/oauth/callback?state=s&code=kept")));
            } // a zero linger makes closing send a reset instead of a graceful close

            var callback = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("code=kept", callback.Query, StringComparison.Ordinal);
        }
    }
}
