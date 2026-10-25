using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kytto.Core.Doctor;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Tokenizer;
using static Kytto.Core.Tests.ScriptedTransport;

namespace Kytto.Core.Tests;

/// <summary>Remote Doctor over Streamable HTTP: protocol, headers, limits and the deadline (§7.3).</summary>
public class RemoteDoctorTests
{
    private const string Endpoint = "https://mcp.example/mcp";

    private static HealthChecker Checker(ScriptedTransport transport, double seconds = 10) =>
        new(timeout: TimeSpan.FromSeconds(seconds), httpClient: new McpHttpClient(transport));

    // MARK: - Protocol

    [Fact]
    public async Task PaginatesJsonAndSseWithSessionHeaders()
    {
        const string first =
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read","description":"Read","inputSchema": {"type":"object"},"annotations":{"readOnlyHint":true}}],"nextCursor":"two"}}""";
        const string second =
            """{"jsonrpc":"2.0","id":3,"result":{"tools":[{"name":"write","inputSchema":{"type":"object"}}]}}""";
        var transport = new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize, headers: new Dictionary<string, string> { ["mcp-session-id"] = "session-secret" }),
            Reply(status: 202),
            Reply(first),
            Reply(
                ": heartbeat\r\n\r\nevent: message\r\ndata: " + second + "\r\n\r\n",
                headers: new Dictionary<string, string> { ["content-type"] = "text/event-stream" }),
            Reply(status: 204));

        var listing = await new McpHttpClient(transport).ListToolsAsync(
            Endpoint,
            new Dictionary<string, string> { ["Authorization"] = "Bearer fixture-secret" });

        Assert.Equal(["read", "write"], listing.Tools.Select(tool => tool.Name));
        Assert.Equal("""{"type":"object"}""", listing.Tools[0].InputSchemaJSON);
        Assert.True(listing.Tools[0].Annotations?.ReadOnlyHint);
        Assert.Equal("remote-fixture", listing.ServerName);
        Assert.Equal("2025-11-25", listing.NegotiatedProtocolVersion);

        var requests = transport.Requests;
        Assert.Equal(6, requests.Count);
        // The modern probe carries the 2026-07-28 metadata and no session.
        Assert.Contains(new KeyValuePair<string, string>("MCP-Protocol-Version", "2026-07-28"), requests[0].Headers);
        Assert.Contains(new KeyValuePair<string, string>("Mcp-Method", "server/discover"), requests[0].Headers);
        Assert.DoesNotContain(requests[1].Headers, header => header.Key == "MCP-Session-Id");
        Assert.DoesNotContain(requests[1].Headers, header => header.Key == "MCP-Protocol-Version");
        foreach (var request in requests.Skip(2))
        {
            Assert.Contains(new KeyValuePair<string, string>("MCP-Session-Id", "session-secret"), request.Headers);
            Assert.Contains(new KeyValuePair<string, string>("MCP-Protocol-Version", "2025-11-25"), request.Headers);
            Assert.DoesNotContain(request.Headers, header => header.Key == "Mcp-Method");
            Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer fixture-secret"), request.Headers);
        }
        Assert.Equal(HttpMethod.Delete, requests[^1].Method);
        Assert.Contains("two", Encoding.UTF8.GetString(requests[4].Body!), StringComparison.Ordinal);
        Assert.Equal(
            ["server/discover", "initialize", "notifications/initialized", "tools/list", "tools/list"],
            requests.Take(5).Select(request => JsonDocument.Parse(request.Body!).RootElement.GetProperty("method").GetString()));
    }

    [Fact]
    public async Task ARepeatedCursorFailsInsteadOfReportingPartialToolsAsPassed()
    {
        var transport = new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize),
            Reply(status: 202),
            Reply("""{"jsonrpc":"2.0","id":2,"result":{"tools":[],"nextCursor":"same"}}"""),
            Reply("""{"jsonrpc":"2.0","id":3,"result":{"tools":[],"nextCursor":"same"}}"""));

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(transport).ListToolsAsync(Endpoint));
        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);

        var (health, weight) = Checker(new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize), Reply(status: 202),
            Reply("""{"jsonrpc":"2.0","id":2,"result":{"tools":[],"nextCursor":"same"}}"""),
            Reply("""{"jsonrpc":"2.0","id":3,"result":{"tools":[],"nextCursor":"same"}}"""),
            Reply(status: 204))).Check(RemoteTestServers.Http(Endpoint));
        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Empty(health.Tools);
        Assert.Null(weight);
    }

    [Fact]
    public async Task APageLimitAndAToolLimitStopTheWalk()
    {
        var replies = new List<RemoteHttpReply> { LegacyRejection, Reply(Initialize), Reply(status: 202) };
        for (var page = 0; page < 60; page++)
        {
            replies.Add(Reply(Rpc(2 + page, "{\"tools\":[],\"nextCursor\":\"c" + page + "\"}")));
        }
        await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(new ScriptedTransport([.. replies])).ListToolsAsync(Endpoint));

        var tools = string.Join(",", Enumerable.Range(0, 10_001).Select(index => "{\"name\":\"t" + index + "\",\"inputSchema\":{}}"));
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize), Reply(status: 202),
            Reply(Rpc(2, "{\"tools\":[" + tools + "]}")))).ListToolsAsync(Endpoint));
        Assert.Equal(RemoteHttpFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task OnlyAMatchingJsonRpcIdIsAnAnswer()
    {
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(new ScriptedTransport(
            Reply("""{"jsonrpc":"2.0","id":99,"result":{}}"""))).ListToolsAsync(Endpoint));
        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);

        // A server's own notification and request in the stream are skipped, not answered.
        var transport = new ScriptedTransport(
            LegacyRejection,
            Reply(
                """data: {"jsonrpc":"2.0","method":"notifications/message","params":{}}""" + "\n\n" +
                """data: {"jsonrpc":"2.0","id":7,"method":"ping"}""" + "\n\n" +
                "data: " + Initialize + "\n\n",
                headers: new Dictionary<string, string> { ["content-type"] = "text/event-stream; charset=utf-8" }),
            Reply(status: 202),
            Reply(EmptyTools));
        var listing = await new McpHttpClient(transport).ListToolsAsync(Endpoint);
        Assert.Empty(listing.Tools);
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task AJsonRpcErrorFailsWithoutEchoingItsText()
    {
        var transport = new ScriptedTransport(LegacyRejection, Reply(
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"bad Authorization: Bearer leaked-secret https://x/?sig=abc"}}"""));

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(transport).ListToolsAsync(Endpoint));

        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);
        Assert.DoesNotContain("leaked-secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(204, "")]
    [InlineData(202, "")]
    [InlineData(200, """{"jsonrpc":"2.0"}""")]
    public async Task ANotificationIsAcceptedWithAny2xx(int status, string body)
    {
        var transport = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply(body, status), Reply(EmptyTools), Reply(status: 204));
        Assert.Empty((await new McpHttpClient(transport).ListToolsAsync(Endpoint)).Tools);
    }

    [Fact]
    public async Task ANotificationRefusedWithAnErrorStatusFailsTheCheck()
    {
        var transport = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply("", 400));
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));
        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);
    }

    [Fact]
    public async Task AnUnsupportedProtocolVersionOrMissingCapabilitiesFail()
    {
        foreach (var result in new[]
        {
            """{"protocolVersion":"1999-01-01","capabilities":{}}""",
            """{"protocolVersion":"2025-11-25"}""",
            """{"protocolVersion":"2025-11-25","capabilities":[]}""",
            // The modern revision has no handshake, so answering one with it is wrong.
            """{"protocolVersion":"2026-07-28","capabilities":{}}""",
        })
        {
            var transport = new ScriptedTransport(LegacyRejection, Reply($$"""{"jsonrpc":"2.0","id":1,"result":{{result}}}"""));
            await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));
        }
    }

    [Fact]
    public async Task AToolWithoutANameOrSchemaIsNotAValidDefinition()
    {
        var transport = new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize), Reply(status: 202),
            Reply("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"description":"nameless","inputSchema":{}}]}}"""));
        await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));
    }

    [Fact]
    public async Task ToolsCallIsNeverSent()
    {
        var transport = new ScriptedTransport(
            LegacyRejection,
            Reply(Initialize), Reply(status: 202),
            Reply("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"delete_everything","inputSchema":{}}]}}"""),
            Reply(status: 204));
        await new McpHttpClient(transport).ListToolsAsync(Endpoint);

        Assert.DoesNotContain(
            transport.Requests,
            request => request.Body is { } body && Encoding.UTF8.GetString(body).Contains("tools/call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheModernRevisionIsSpokenWithoutAHandshake()
    {
        const string discover =
            """{"jsonrpc":"2.0","id":1,"result":{"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{},"logging":{}},"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-remote","version":"3"}}}}""";
        const string tools = """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read","inputSchema":{"type":"object"}}]}}""";
        var transport = new ScriptedTransport(
            Reply(discover, headers: new Dictionary<string, string> { ["mcp-session-id"] = "ignored" }),
            Reply(tools));

        var listing = await new McpHttpClient(transport).ListToolsAsync(Endpoint);

        Assert.Equal("2026-07-28", listing.NegotiatedProtocolVersion);
        Assert.Equal("modern-remote", listing.ServerName);
        Assert.Equal(["read"], listing.Tools.Select(tool => tool.Name));
        Assert.Equal([DeprecatedFeature.Logging], listing.DeprecatedFeatures);
        // Two requests, no DELETE: a modern server has no session to end.
        Assert.Equal(2, transport.Requests.Count);
        var list = transport.Requests[1];
        Assert.Contains(new KeyValuePair<string, string>("Mcp-Method", "tools/list"), list.Headers);
        Assert.Contains(new KeyValuePair<string, string>("MCP-Protocol-Version", "2026-07-28"), list.Headers);
        Assert.DoesNotContain(list.Headers, header => header.Key == "MCP-Session-Id");
        Assert.Contains("io.modelcontextprotocol/protocolVersion", Encoding.UTF8.GetString(list.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModernVersionRefusalIsNotMistakenForAHandshakeServer()
    {
        const string refusal =
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32022,"message":"Unsupported","data":{"supported":["2027-01-01"],"requested":"2026-07-28"}}}""";
        var transport = new ScriptedTransport(Reply(refusal, 400));

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));

        Assert.Equal(RemoteHttpFailure.Protocol, failure.Failure);
        Assert.Contains("2027-01-01", failure.Message, StringComparison.Ordinal);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(307)]
    public async Task AForbiddenRateLimitedOrRedirectedProbeIsNotRetriedAsAHandshake(int status)
    {
        var transport = new ScriptedTransport(Reply("", status));

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));

        Assert.Equal(RemoteHttpFailure.HttpStatus, failure.Failure);
        Assert.Equal(status, failure.Status);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public void ALegacySseEndpointIsUnsupportedNotFailed()
    {
        var transport = new ScriptedTransport(
            Reply(status: 405),
            Reply(status: 405),
            Reply(
                "event: endpoint\ndata: /messages?session=1\n\n",
                headers: new Dictionary<string, string> { ["content-type"] = "text/event-stream" }));
        var server = RemoteTestServers.Http("https://mcp.example/events");

        var (health, weight) = Checker(transport).Check(server);

        Assert.Equal(HealthStatus.Unsupported, health.Status);
        Assert.Null(weight);
        Assert.Equal("remoteTransport", health.Failure?.Reason);
        Assert.Equal("sse", health.Failure?.Transport);
        Assert.Equal(HttpMethod.Get, transport.Requests[^1].Method);
        Assert.Contains(new KeyValuePair<string, string>("Accept", "text/event-stream"), transport.Requests[^1].Headers);

        server.Health = health;
        Assert.Contains(McpDoctor.Analyze(server).Findings, finding => finding.Code == "deprecated-http-sse");
    }

    [Fact]
    public void AnEndpointThatRefusesTheHandshakeAndIsNoEventStreamStaysFailed()
    {
        var transport = new ScriptedTransport(Reply(status: 404), Reply(status: 404), Reply(status: 404));

        var health = Checker(transport).Check(RemoteTestServers.Http(Endpoint)).Health;

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("404", health.CurrentMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainHttpToAnAddressOffThisMachineIsUnsupportedNotFailed()
    {
        var health = Checker(new ScriptedTransport()).Check(RemoteTestServers.Http("http://192.168.1.20:8080/mcp")).Health;

        Assert.Equal(HealthStatus.Unsupported, health.Status);
    }

    // MARK: - Authorization states

    [Fact]
    public void AuthorizationIsToldApartFromForbiddenAndNothingSensitiveIsKept()
    {
        HealthResult Check(int status, string? challenge)
        {
            var headers = challenge is null
                ? null
                : new Dictionary<string, string> { ["www-authenticate"] = challenge };
            var transport = new ScriptedTransport(Reply("sensitive echoed body", status, headers));
            return Checker(transport).Check(RemoteTestServers.Http("https://mcp.example/mcp?token=hidden-query")).Health;
        }

        var needsAuth = Check(401, """Bearer resource_metadata="https://mcp.example/meta?token=challenge-secret" """);
        Assert.Equal(HealthStatus.NeedsAuthorization, needsAuth.Status);
        Assert.Null(needsAuth.AuthorizationURL);
        var encoded = JsonSerializer.Serialize(needsAuth);
        Assert.DoesNotContain("hidden-query", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("challenge-secret", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("echoed body", encoded, StringComparison.Ordinal);

        var forbidden = Check(403, null);
        Assert.Equal(HealthStatus.Failed, forbidden.Status);
        Assert.Contains("403", forbidden.CurrentMessage, StringComparison.Ordinal);
        Assert.Equal(
            HealthStatus.NeedsAuthorization,
            Check(403, """Bearer error="insufficient_scope",scope="write" """).Status);
    }

    [Fact]
    public void FailureMessagesNeverCarryTheUrlOrTheServersWords()
    {
        foreach (var status in new[] { 404, 500, 502 })
        {
            var transport = new ScriptedTransport(
                Reply("token=abc123 body", status), Reply("token=abc123 body", status), Reply("token=abc123 body", status));
            var health = Checker(transport).Check(RemoteTestServers.Http("https://mcp.example/mcp?sig=secretsig")).Health;
            Assert.Equal(HealthStatus.Failed, health.Status);
            Assert.DoesNotContain("secretsig", health.CurrentMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", health.CurrentMessage, StringComparison.Ordinal);
            Assert.Contains(status.ToString(), health.CurrentMessage, StringComparison.Ordinal);
        }
    }

    // MARK: - Headers

    [Fact]
    public void ACheckMergesTheOAuthOverrideOverTheConfiguredAuthorizationOnly()
    {
        var transport = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply(status: 202), Reply(EmptyTools), Reply(status: 204));
        var server = RemoteTestServers.Http(
            Endpoint,
            """{"headers":{"authorization":"Bearer ${MISSING}","X-Version":"2026"}}""");

        var outcome = Checker(transport).Check(
            server,
            new Dictionary<string, string> { ["Authorization"] = "Bearer oauth-fixture" });

        Assert.Equal(HealthStatus.Passed, outcome.Health.Status);
        Assert.Equal("Streamable HTTP", outcome.Health.EnvironmentSource);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("Bearer oauth-fixture", request.Headers.Single(header => header.Key == "Authorization").Value);
            Assert.Equal("2026", request.Headers.Single(header => header.Key == "X-Version").Value);
        });
        Assert.DoesNotContain(
            transport.Requests.SelectMany(request => request.Headers),
            header => header.Key.Equals("authorization", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDoctorUsesTheRemoteOutcomeAndLegacySseIsSaidPlainly()
    {
        var server = RemoteTestServers.Http(Endpoint);
        Assert.Contains(McpDoctor.Analyze(server).Findings, finding => finding.Code == "not-checked");

        var transport = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply(status: 202), Reply(EmptyTools), Reply(status: 204));
        server.Health = Checker(transport).Check(server).Health;
        Assert.Empty(McpDoctor.Analyze(server).Findings);

        server.Health = new HealthResult(HealthStatus.NeedsAuthorization, Timestamp.Now, null, [], null, null, 0, null, null);
        Assert.Contains(McpDoctor.Analyze(server).Findings, finding => finding.Code == "needs-authorization");

        server.Health = Checker(new ScriptedTransport(Reply("", 500))).Check(server).Health;
        Assert.Contains(McpDoctor.Analyze(server).Findings, finding => finding.Code == "remote-check-failed");

        var legacy = RemoteTestServers.Http(Endpoint);
        var sse = new Server
        {
            Id = "sse", Name = "sse", Transport = Transport.Sse, Command = null, Args = [], Env = [],
            Url = Endpoint, EnabledIn = [], Origin = legacy.Origin, Fingerprint = "x", IsBundled = false,
            DefinitionSource = "{}",
        };
        var (health, weight) = Checker(new ScriptedTransport()).Check(sse);
        Assert.Equal(HealthStatus.Unsupported, health.Status);
        Assert.Null(weight);
        Assert.Contains("legacy", health.Message, StringComparison.OrdinalIgnoreCase);
        var deprecated = Assert.Single(McpDoctor.Analyze(sse).Findings, finding => finding.Code == "deprecated-http-sse");
        Assert.Equal(DoctorSeverity.Warning, deprecated.Severity);
    }

    [Fact]
    public void OriginalClientHeadersResolveNativelyAndOnlyOnce()
    {
        var environment = new Dictionary<string, string> { ["TOKEN"] = "fixture-token", ["VERSION"] = "2026" };
        const string json =
            """{"url":"https://mcp.example","headers":{"Authorization":"Bearer ${env:TOKEN}","X-Version":"${VERSION}"}}""";
        Assert.Equal(
            new Dictionary<string, string> { ["Authorization"] = "Bearer fixture-token", ["X-Version"] = "2026" },
            RemoteHttpHeaders.Resolve(json, environment));

        const string toml = """
            [mcp_servers.remote]
            url = "https://mcp.example/mcp"
            bearer_token_env_var = "TOKEN"
            [mcp_servers.remote.http_headers]
            X-Version = "2026"
            [mcp_servers.remote.env_http_headers]
            X-Token = "TOKEN"
            """;
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer fixture-token",
                ["X-Version"] = "2026",
                ["X-Token"] = "fixture-token",
            },
            RemoteHttpHeaders.Resolve(toml, environment));

        Assert.Throws<RemoteHttpException>(() => RemoteHttpHeaders.Resolve(json, new Dictionary<string, string>()));

        // A credential is literal data: placeholder-looking text inside it is not
        // a request for another variable.
        const string literalToken = """{"headers":{"Authorization":"Bearer ${env:TOKEN}"}}""";
        Assert.Equal(
            "Bearer prefix-${OTHER}",
            RemoteHttpHeaders.Resolve(
                literalToken,
                new Dictionary<string, string> { ["TOKEN"] = "prefix-${OTHER}", ["OTHER"] = "must-not-expand" })["Authorization"]);
        const string viaEnvTable = """{"env_http_headers":{"X-Key":"KEY"},"bearer_token_env_var":"TOKEN"}""";
        var resolved = RemoteHttpHeaders.Resolve(
            viaEnvTable,
            new Dictionary<string, string> { ["KEY"] = "${OTHER}", ["TOKEN"] = "${OTHER}", ["OTHER"] = "must-not-expand" });
        Assert.Equal("${OTHER}", resolved["X-Key"]);
        Assert.Equal("Bearer ${OTHER}", resolved["Authorization"]);

        const string inline = """remote = { url="https://mcp.example", http_headers={X-Version="2026"}, bearer_token_env_var="TOKEN" }""";
        Assert.Equal(
            new Dictionary<string, string> { ["Authorization"] = "Bearer fixture-token", ["X-Version"] = "2026" },
            RemoteHttpHeaders.Resolve(inline, environment));

        Assert.Equal(
            new Dictionary<string, string> { ["X-Version"] = "2026" },
            RemoteHttpHeaders.Resolve(json, new Dictionary<string, string> { ["VERSION"] = "2026" }, omitAuthorization: true));

        Assert.Equal(
            "Bearer d",
            RemoteHttpHeaders.Resolve("""{"headers":{"Authorization":"Bearer ${NOPE:-d}"}}""", new Dictionary<string, string>())["Authorization"]);
    }

    [Fact]
    public void OAuthReplacesOnlyAuthorizationWhateverItsCase()
    {
        var configured = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["authorization"] = "Bearer configured",
            ["X-Tenant"] = "acme",
        };

        var merged = RemoteHttpHeaders.Merge(configured, new Dictionary<string, string> { ["Authorization"] = "Bearer oauth" });

        Assert.Equal(2, merged.Count);
        Assert.Equal("Bearer oauth", merged["authorization"]);
        Assert.Equal("acme", merged["X-Tenant"]);
    }

    [Theory]
    [InlineData("Host", "different.example")]
    [InlineData("host", "different.example")]
    [InlineData("Authorization", "secret\r\ninjected: true")]
    [InlineData("Authorization", "secret\ninjected")]
    [InlineData("MCP-Session-Id", "injected")]
    [InlineData("mcp-protocol-version", "1")]
    [InlineData("Content-Length", "1")]
    [InlineData("Cookie", "a=b")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Accept-Encoding", "gzip")]
    [InlineData("X Bad", "name has a space")]
    [InlineData("", "empty name")]
    public void TransportHeadersAndInjectionAreRefused(string name, string value)
    {
        Assert.Throws<RemoteHttpException>(
            () => RemoteHttpHeaders.Validate(new Dictionary<string, string> { [name] = value }));
    }

    [Fact]
    public void HeaderCountAndDuplicatesAreBounded()
    {
        Assert.Throws<RemoteHttpException>(() => RemoteHttpHeaders.Validate(
            Enumerable.Range(0, 65).ToDictionary(index => $"X-{index}", _ => "v")));
        Assert.Throws<RemoteHttpException>(() => RemoteHttpHeaders.Validate(
            new Dictionary<string, string> { ["x"] = new string('a', 16_385) }));
        Assert.Throws<RemoteHttpException>(() => RemoteHttpHeaders.Validate(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["X-A"] = "1", ["x-a"] = "2" }));
    }

    [Theory]
    [InlineData("http://mcp.example/mcp")]
    [InlineData("https://user:password@mcp.example/mcp")]
    [InlineData("https://mcp.example/mcp#fragment")]
    [InlineData("https://mcp.example/mcp#")]
    [InlineData("ftp://mcp.example/mcp")]
    [InlineData("https://mcp.example/a b")]
    [InlineData("https://mcp.example\\@evil.example/mcp")]
    [InlineData("http://127.0.0.1.example/mcp")]
    [InlineData("/relative/mcp")]
    [InlineData("")]
    public void UnsafeEndpointsAreRefused(string endpoint)
    {
        Assert.Throws<RemoteHttpException>(() => RemoteHttpEndpoint.Validate(endpoint));
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345/mcp")]
    [InlineData("http://localhost:12345/mcp")]
    [InlineData("http://[::1]:12345/mcp")]
    [InlineData("https://mcp.example/mcp?workspace=a")]
    public void LoopbackHttpAndHttpsAreAllowed(string endpoint)
    {
        Assert.NotNull(RemoteHttpEndpoint.Validate(endpoint));
    }

    [Fact]
    public void AnOversizedUrlIsRefused()
    {
        Assert.Throws<RemoteHttpException>(() => RemoteHttpEndpoint.Validate("https://mcp.example/" + new string('a', 9_000)));
    }

    // MARK: - Untrusted JSON

    [Fact]
    public void RemoteJsonRefusesDeepDuplicateAndMalformedInput()
    {
        Assert.Null(RemoteJson.TryParse(new string('[', 129) + new string(']', 129)));
        Assert.NotNull(RemoteJson.TryParse(new string('[', 128) + new string(']', 128)));
        Assert.Null(RemoteJson.TryParse("""{"result":{"tools":[]},"result":{"tools":[{"name":"hidden"}]}}"""));
        Assert.Null(RemoteJson.TryParse("""{"result":{"tools":[],"tools":[{"name":"hidden"}]}}"""));
        Assert.Null(RemoteJson.TryParse("""{"a":1,}"""));
        Assert.Null(RemoteJson.TryParse("""{"a":/* c */1}"""));
        Assert.Null(RemoteJson.TryParse("{"));
        Assert.Null(RemoteJson.TryParse([0x7b, 0x22, 0x61, 0x22, 0x3a, 0xff, 0x7d]));
        Assert.NotNull(RemoteJson.TryParse("""{"a":{"b":[1,2,{"c":null}]}}"""));
    }

    [Fact]
    public async Task ADuplicatedKeyInAResponseIsRefusedNotResolved()
    {
        var transport = new ScriptedTransport(LegacyRejection, Reply(
            """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25","capabilities":{},"capabilities":{"tools":{}}}}"""));
        await Assert.ThrowsAsync<RemoteHttpException>(() => new McpHttpClient(transport).ListToolsAsync(Endpoint));
    }

    [Fact]
    public async Task AnOversizedResponseFailsAndTheTotalIsBounded()
    {
        var oversized = new ScriptedTransport(Reply(new string('x', 4 * 1024 * 1024 + 1)));
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(oversized).ListToolsAsync(Endpoint));
        Assert.Equal(RemoteHttpFailure.ResponseTooLarge, failure.Failure);

        // Each page is under the per-response limit; together they pass 8 MiB.
        var filler = new string('d', 3 * 1024 * 1024);
        var page = (int id, bool more) =>
            Reply(Rpc(id, "{\"tools\":[{\"name\":\"t" + id + "\",\"description\":\"" + filler + "\",\"inputSchema\":{}}]" +
                (more ? ",\"nextCursor\":\"c" + id + "\"" : "") + "}"));
        var total = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply(status: 202), page(2, true), page(3, true), page(4, true), page(5, false));
        var overTotal = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient(total).ListToolsAsync(Endpoint, timeout: TimeSpan.FromSeconds(30)));
        Assert.Equal(RemoteHttpFailure.ResponseTooLarge, overTotal.Failure);
    }

    [Fact]
    public void ServerSentEventsAreCompleteEventsOnly()
    {
        Assert.Equal(
            ["first\nsecond"],
            ServerSentEventReader.Messages("data: first\ndata: second\n\n: heartbeat\n\ndata: incomplete"u8));
        Assert.Equal(["one", "two"], ServerSentEventReader.Messages("data: one\r\rdata: two\r\r"u8));
        Assert.Equal(["a", "b"], ServerSentEventReader.Messages("data:a\r\n\r\nevent: x\r\nid: 9\r\ndata: b\r\n\r\n"u8));
        Assert.Equal(["bom"], ServerSentEventReader.Messages([0xEF, 0xBB, 0xBF, .. "data: bom\n\n"u8]));
        Assert.Empty(ServerSentEventReader.Messages(": only a heartbeat\n\n"u8));
    }

    [Fact]
    public void AnEventStreamIsReadAsItArrivesAndStopsAtTheMatchingAnswer()
    {
        var reader = new ServerSentEventReader(responseId: 2);
        Assert.False(reader.Feed("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n"u8));
        Assert.False(reader.Feed("data: {\"jsonrpc\":\"2.0\",\"id\":2,"u8));
        Assert.False(reader.Feed("\"result\":{}}\n"u8));
        Assert.True(reader.Feed("\n"u8));
    }

    // MARK: - Deadline

    [Fact]
    public void AHostileDescriptionCannotOutliveTheDeadlineOrReturnPartialWeight()
    {
        // Load the vocabulary before the timed check: this isolates hostile input
        // from the one-time cost of reading the bundled table.
        _ = BpeTokenizer.Cl100kBase;
        var huge = new string('a', 3_400_000);
        var response = Rpc(2, "{\"tools\":[{\"name\":\"huge\",\"description\":\"" + huge + "\",\"inputSchema\":{\"type\":\"object\"}}]}");
        var transport = new ScriptedTransport(LegacyRejection, Reply(Initialize), Reply(status: 202), Reply(response), Reply(status: 204));
        var checker = Checker(transport, seconds: 0.25);

        var watch = Stopwatch.StartNew();
        var (health, weight) = checker.Check(RemoteTestServers.Http(Endpoint));
        watch.Stop();

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("time limit", health.CurrentMessage, StringComparison.Ordinal);
        Assert.Empty(health.Tools);
        Assert.Null(weight);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public void TheTokenizerStopsAtItsDeadlineAndNeverReturnsAPartialCount()
    {
        var tokenizer = BpeTokenizer.Cl100kBase!;
        Assert.Throws<TimeoutException>(() => tokenizer.CountTokens(new string('a', 3_000_000), DateTimeOffset.UtcNow.AddMilliseconds(50)));
        Assert.Throws<TimeoutException>(() => tokenizer.CountTokens("x", DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Equal(tokenizer.CountTokens("hello world"), tokenizer.CountTokens("hello world", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    // MARK: - The real transport, against a loopback server

    [Fact]
    public async Task RealJsonAndAnOpenEventStreamCompleteWithoutWaitingForEof()
    {
        var streaming = true;
        using var server = new FakeRemoteMcp(intercept: (request, method) =>
        {
            if (!streaming || method != "tools/list") return null;
            var message = """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"fixture_read","inputSchema":{"type":"object"}}]}}""";
            return new LoopbackResponse(
                200,
                ": heartbeat\n\ndata: " + message + "\n\n",
                "text/event-stream",
                HoldOpen: TimeSpan.FromSeconds(3));
        });

        var watch = Stopwatch.StartNew();
        var listing = await new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromSeconds(5));
        watch.Stop();

        Assert.Equal(["fixture_read"], listing.Tools.Select(tool => tool.Name));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"waited for the stream to end: {watch.Elapsed}");
    }

    [Fact]
    public async Task TheRealTransportPaginatesKeepsSessionAndHeadersAndNeverSendsToolsCallOrCookies()
    {
        using var server = new FakeRemoteMcp(pages: 3);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer loopback-secret",
            ["X-Tenant"] = "acme",
        };

        var listing = await new McpHttpClient().ListToolsAsync(server.Url, headers);

        Assert.Equal(["fixture_read_0", "fixture_read_1", "fixture_read_2"], listing.Tools.Select(tool => tool.Name));
        Assert.Equal(
            ["server/discover", "initialize", "notifications/initialized", "tools/list", "tools/list", "tools/list"],
            server.Methods);
        Assert.DoesNotContain("tools/call", server.Methods);

        var posts = server.Requests.Where(request => request.Method == "POST").ToArray();
        Assert.All(posts, request =>
        {
            Assert.Equal("Bearer loopback-secret", request.Header("Authorization"));
            Assert.Equal("acme", request.Header("X-Tenant"));
            Assert.Contains("application/json", request.Header("Accept"), StringComparison.Ordinal);
            // A cookie the server set must never be played back.
            Assert.Null(request.Header("Cookie"));
        });
        Assert.Equal("2026-07-28", posts[0].Header("MCP-Protocol-Version"));
        Assert.Equal("server/discover", posts[0].Header("Mcp-Method"));
        Assert.Null(posts[0].Header("MCP-Session-Id"));
        Assert.Null(posts[1].Header("MCP-Session-Id"));
        Assert.All(posts.Skip(2), request =>
        {
            Assert.Equal("session-fixture", request.Header("MCP-Session-Id"));
            Assert.Equal("2025-11-25", request.Header("MCP-Protocol-Version"));
            Assert.Null(request.Header("Mcp-Method"));
        });

        var delete = Assert.Single(server.Requests, request => request.Method == "DELETE");
        Assert.Equal("session-fixture", delete.Header("MCP-Session-Id"));
        Assert.Equal("Bearer loopback-secret", delete.Header("Authorization"));
    }

    [Fact]
    public async Task ARedirectIsNeverFollowed()
    {
        var destinationHits = 0;
        using var server = new FakeRemoteMcp(intercept: (request, _) =>
        {
            if (request.Path == "/destination")
            {
                Interlocked.Increment(ref destinationHits);
                return new LoopbackResponse(200, "{}");
            }
            return new LoopbackResponse(302, Headers: new Dictionary<string, string> { ["Location"] = "/destination" });
        });

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient().ListToolsAsync(server.Url, new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));

        Assert.Equal(RemoteHttpFailure.HttpStatus, failure.Failure);
        Assert.Equal(302, failure.Status);
        Assert.Equal(0, destinationHits);
    }

    [Fact]
    public async Task ADeclaredOversizedBodyAndAStreamedOneBothFail()
    {
        using var declared = new FakeRemoteMcp(intercept: (_, _) =>
            new LoopbackResponse(200, "x", DeclaredLength: 8 * 1024 * 1024));
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient().ListToolsAsync(declared.Url, timeout: TimeSpan.FromSeconds(3)));
        Assert.Equal(RemoteHttpFailure.ResponseTooLarge, failure.Failure);

        using var streamed = new FakeRemoteMcp(intercept: (_, _) =>
            new LoopbackResponse(200, new string('x', 4 * 1024 * 1024 + 10), HoldOpen: TimeSpan.FromSeconds(2)));
        var second = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient().ListToolsAsync(streamed.Url, timeout: TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteHttpFailure.ResponseTooLarge, second.Failure);
    }

    [Fact]
    public async Task ASlowServerTimesOutAtTheDeadline()
    {
        using var server = new FakeRemoteMcp(intercept: (_, _) =>
            new LoopbackResponse(200, "{}", DelayBeforeReply: TimeSpan.FromSeconds(30)));

        var watch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromMilliseconds(400)));
        watch.Stop();

        Assert.Equal(RemoteHttpFailure.TimedOut, failure.Failure);
        // The server would take 30 s: finishing well inside that proves the 400 ms deadline
        // held, with room for a machine that is busy building something else.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task TheSessionCleanupNeverTakesMoreThanItsBudget()
    {
        using var server = new FakeRemoteMcp(deleteDelay: TimeSpan.FromSeconds(30));

        var watch = Stopwatch.StartNew();
        var listing = await new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromSeconds(10));
        watch.Stop();

        Assert.Single(listing.Tools);
        Assert.Contains(server.Requests, request => request.Method == "DELETE");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"cleanup held the check: {watch.Elapsed}");
    }

    [Fact]
    public async Task CancellingTheCheckStopsItPromptly()
    {
        using var server = new FakeRemoteMcp(intercept: (_, method) =>
            method == "tools/list" ? new LoopbackResponse(200, "{}", DelayBeforeReply: TimeSpan.FromSeconds(5)) : null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellation.Token));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task AnUnreachableServerSaysSoWithoutTheUrl()
    {
        int port;
        using (var probe = new LoopbackHttpServer(_ => new LoopbackResponse())) port = probe.Port;

        var failure = await Assert.ThrowsAsync<RemoteHttpException>(
            () => new McpHttpClient().ListToolsAsync($"http://127.0.0.1:{port}/mcp?sig=urlsecret", timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(RemoteHttpFailure.Unavailable, failure.Failure);
        Assert.DoesNotContain("urlsecret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndToEndCheckOverLoopbackRecordsAWeightAndNoSecrets()
    {
        using var server = new FakeRemoteMcp();
        var definition = """{"headers":{"X-Api-Key":"${LOOPBACK_KEY}"}}""";
        var env = new[] { new EnvEntry("LOOPBACK_KEY", "fake-key-value") };

        var (health, weight) = new HealthChecker().Check(RemoteTestServers.Http(server.Url, definition, env));

        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(1, health.ToolCount);
        Assert.NotNull(weight);
        Assert.Equal("fake-key-value", server.Requests[0].Header("X-Api-Key"));
        Assert.DoesNotContain("fake-key-value", JsonSerializer.Serialize(health), StringComparison.Ordinal);
    }

    // MARK: - The Python fixture, as the macOS port ran it

    [Fact]
    public async Task ThePortedPythonFixtureJsonAndOpenSseBehaveTheSame()
    {
        foreach (var mode in new[] { "json", "sse-open" })
        {
            using var server = PythonLoopback.Start(mode);
            var watch = Stopwatch.StartNew();
            // The open stream outlives the fixture's own 10 s hold, so a client that
            // waited for EOF could not pass inside 6 s however slow the machine is.
            var listing = await new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromSeconds(8));
            Assert.Equal(["fixture_read"], listing.Tools.Select(tool => tool.Name));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"{mode}: {watch.Elapsed}");
        }
    }

    [Fact]
    public async Task ThePortedPythonFixtureRejectsRedirectOversizeAndTimeout()
    {
        foreach (var (mode, expected) in new[]
        {
            ("redirect", RemoteHttpFailure.HttpStatus),
            ("oversized", RemoteHttpFailure.ResponseTooLarge),
            ("slow", RemoteHttpFailure.TimedOut),
        })
        {
            using var server = PythonLoopback.Start(mode);
            var failure = await Assert.ThrowsAsync<RemoteHttpException>(
                () => new McpHttpClient().ListToolsAsync(server.Url, timeout: TimeSpan.FromMilliseconds(800)));
            Assert.Equal(expected, failure.Failure);
        }
    }

    private sealed class PythonLoopback : IDisposable
    {
        private readonly Process _process;

        private PythonLoopback(Process process, string url)
        {
            _process = process;
            Url = url;
        }

        public string Url { get; }

        public static PythonLoopback Start(string mode)
        {
            var python = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory =>
                {
                    try { return Path.Combine(directory.Trim('"'), "python.exe"); }
                    catch (ArgumentException) { return null; }
                })
                .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
            Assert.True(python is not null, "These tests need python.exe on PATH.");

            var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_remote_mcp_server.py");
            var process = Process.Start(new ProcessStartInfo(python!)
            {
                ArgumentList = { script, mode },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var port = int.Parse(process.StandardOutput.ReadLine()!.Trim());
            return new PythonLoopback(process, $"http://127.0.0.1:{port}/mcp");
        }

        public void Dispose()
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}
