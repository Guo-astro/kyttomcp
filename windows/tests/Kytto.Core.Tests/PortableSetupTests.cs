using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Portable;
using Kytto.Core.Profiles;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>
/// Portable setup, end to end, against throwaway profiles (§7.13). Nothing here
/// touches a real config, credential or data folder.
/// </summary>
public sealed class PortableSetupTests
{
    [Fact]
    public void ExportNeverCopiesEnvUrlHeaderArgumentPathOrUnknownFieldValues()
    {
        using var source = new ToggleHarness();
        source.Write("""
            {"mcpServers": {
              "local": {"command": "C:\\Users\\private-user\\private-command.exe", "args": ["--token=argument-secret", "-y", "C:\\private\\path"], "env": {"API_TOKEN": "env-secret"}, "opaque": {"token": "unknown-secret"}, "cwd": "C:\\Users\\private-user\\private-project"},
              "remote": {"type":"http", "url":"https://url-user:url-password@example.test/path-secret?token=query-secret#fragment-secret", "headers":{"Authorization":"Bearer header-secret"}}
            }}
            """, ".cursor/mcp.json");

        var summary = source.ExportTo();
        var text = File.ReadAllText(source.PortableFile());

        foreach (var secret in new[]
                 {
                     "private-user", "private-command", "argument-secret", "env-secret", "unknown-secret",
                     "url-user", "url-password", "path-secret", "query-secret", "fragment-secret",
                     "header-secret", @"C:\\private\\path", "private-project",
                 })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
        Assert.Equal(["local", "remote"], summary.ServerNames);
        Assert.Equal(7, summary.Requirements.Count);
        Assert.Contains("API_TOKEN", text, StringComparison.Ordinal);
        Assert.Contains("Authorization", text, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripKeepsDisabledPresenceCopiesParkedAndImportsProfilesWithoutApplyingThem()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write(
            """{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"KEY":"original-secret"}}}}""",
            ".cursor/mcp.json");
        new ProfileStore(source.Paths).Create("Writing", ["memory"], 12_000);
        var service = destination.Portable();

        var summary = source.ExportTo();
        Assert.Single(summary.Requirements);

        var preview = service.Preview(
            source.PortableFile(), [ClientId.Cursor, ClientId.Codex], PortableTestSupport.Inputs(summary));
        Assert.False(Directory.Exists(destination.Paths.Root), "a preview creates nothing");
        Assert.False(preview.Enabled);

        var result = service.Apply(preview.Id);
        Assert.Equal([ClientId.Codex], result.ChangedClients);
        Assert.False(destination.Exists(".cursor/mcp.json"), "a presence client's file is not touched");

        var parked = destination.ParkStore.Parked(ClientId.Cursor, "memory");
        Assert.NotNull(parked);
        var definition = JsonDocument.Parse(parked.SourceText).Root;
        Assert.Equal("npx", definition["command"]?.StringValue);
        Assert.Equal("@modelcontextprotocol/server-memory", definition["args"]?.Elements?[^1].StringValue);
        Assert.Equal("replacement-value", definition["env"]?["KEY"]?.StringValue);

        var toml = TomlDocument.Parse(destination.Text(".codex/config.toml"));
        Assert.Equal(false, toml.Table("mcp_servers", "memory")?.Value("enabled")?.BoolValue);

        var profile = Assert.Single(new ProfileStore(destination.Paths).All());
        Assert.Equal(["memory"], profile.ServerIDs);
        Assert.Equal(12_000, profile.TokenBudget);
        Assert.Equal(
            Enablement.Disabled,
            destination.Discover().Servers.First(server => server.Name == "memory").EnabledIn[ClientId.Cursor]);

        // A preview is single-use.
        Assert.Equal(
            PortableSetupFailure.StalePreview,
            Assert.Throws<PortableSetupException>(() => service.Apply(preview.Id)).Failure);
    }

    public static TheoryData<ClientId, string> EveryEnablementStrategy => new()
    {
        { ClientId.ClaudeDesktop, "presence, parked" },
        { ClientId.Cursor, "presence, parked" },
        { ClientId.VsCode, "presence, parked" },
        { ClientId.ClaudeCode, "deny list" },
        { ClientId.Codex, "inline flag" },
    };

    [Theory]
    [MemberData(nameof(EveryEnablementStrategy))]
    public void AnImportedServerIsDisabledWhicheverWayTheClientSwitchesThingsOff(ClientId client, string how)
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx","args":["-y"]}}}""", ".cursor/mcp.json");
        var summary = source.ExportTo();
        var service = destination.Portable();

        var preview = service.Preview(source.PortableFile(), [client], PortableTestSupport.Inputs(summary));
        service.Apply(preview.Id);

        var imported = destination.Discover().Servers.First(server => server.Name == "new");
        Assert.True(
            Enablement.Disabled == imported.EnabledIn[client],
            $"{client} ({how}) must read as disabled after an import");
        Assert.NotNull(imported.Command);
    }

    [Fact]
    public void ExplicitEnabledImportPreservesJsoncTomlCommentsUnknownFieldsAndTakesBackups()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx","args":["-y"]}}}""", ".cursor/mcp.json");
        const string Json =
            "{\n  // keep this exactly\n  \"preferences\": {\"opaque\": 42},\n  \"mcpServers\": {\"existing\": {\"command\": \"node\", \"unknown\": true}}\n}\n";
        const string Toml =
            "# keep this comment\nmodel = \"example\"\n\n[projects.\"/keep/path\"]\ntrust_level = \"trusted\"\n";
        destination.Write(Json, ".cursor/mcp.json");
        destination.Write(Toml, ".codex/config.toml");
        var service = destination.Portable();
        source.ExportTo();

        var preview = service.Preview(
            source.PortableFile(), [ClientId.Cursor, ClientId.Codex], enableImportedServers: true);
        var result = service.Apply(preview.Id);

        Assert.Equal(2, result.BackupIDs.Count);
        var json = destination.Text(".cursor/mcp.json");
        Assert.Contains("// keep this exactly", json, StringComparison.Ordinal);
        Assert.Contains("\"preferences\": {\"opaque\": 42}", json, StringComparison.Ordinal);
        Assert.Contains("\"existing\": {\"command\": \"node\", \"unknown\": true}", json, StringComparison.Ordinal);
        var toml = destination.Text(".codex/config.toml");
        Assert.StartsWith(Toml, toml, StringComparison.Ordinal);
        Assert.Null(TomlDocument.Parse(toml).Table("mcp_servers", "new")?.Value("enabled"));
    }

    [Fact]
    public void AnImportKeepsParkedCopiesJsonCommentsAndUnknownKeysOfEveryOtherServer()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        destination.Write("""{"mcpServers":{"kept":{"command":"node"}}}""", ".cursor/mcp.json");
        // An existing parked copy for another server, in another client.
        destination.ParkStore.Park(ClientId.Cursor, "elsewhere", """{ "command": "parked-bytes" }""");
        var parkedBefore = File.ReadAllBytes(destination.Paths.ParkFile);
        var service = destination.Portable();
        source.ExportTo();

        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor]);
        service.Apply(preview.Id);

        Assert.Equal("""{ "command": "parked-bytes" }""", destination.ParkStore.Parked(ClientId.Cursor, "elsewhere")?.SourceText);
        Assert.NotNull(destination.ParkStore.Parked(ClientId.Cursor, "new"));
        Assert.Contains("\"kept\":{\"command\":\"node\"}", destination.Text(".cursor/mcp.json"), StringComparison.Ordinal);
        Assert.NotEqual(parkedBefore, File.ReadAllBytes(destination.Paths.ParkFile));
    }

    [Fact]
    public void ClaudeCodeImportCreatesARealDisabledDefinitionAndKeepsExistingDenySettings()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        destination.Write("""{"deniedMcpServers":[{"serverName":"existing"}],"unrelated":true}""", ".claude/settings.json");
        var service = destination.Portable();
        source.ExportTo();

        var preview = service.Preview(source.PortableFile(), [ClientId.ClaudeCode]);
        Assert.Equal(2, preview.PathDisplays.Count);
        service.Apply(preview.Id);

        var config = JsonDocument.Parse(destination.Text(".claude.json"));
        Assert.Equal("npx", config.Root["mcpServers"]?["new"]?["command"]?.StringValue);
        var settings = JsonDocument.Parse(destination.Text(".claude/settings.json"));
        Assert.Equal(true, settings.Root["unrelated"]?.BoolValue);
        Assert.Equal(2, settings.Root["deniedMcpServers"]?.Elements?.Count);
        Assert.Equal(
            Enablement.Disabled,
            destination.Discover().Servers.First(server => server.Name == "new").EnabledIn[ClientId.ClaudeCode]);
    }

    [Fact]
    public void RemoteHeadersUseEachDestinationsOwnFormatAndTheUrlIsSuppliedNatively()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write(
            "[mcp_servers.remote]\nurl = \"https://private.test/mcp\"\n\n[mcp_servers.remote.http_headers]\nAuthorization = \"Bearer original-secret\"\n",
            ".codex/config.toml");
        var service = destination.Portable();
        var summary = source.ExportTo();
        Assert.Equal(2, summary.Requirements.Count);
        var text = File.ReadAllText(source.PortableFile());
        Assert.DoesNotContain("original-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private.test", text, StringComparison.Ordinal);

        var preview = service.Preview(
            source.PortableFile(), [ClientId.Cursor, ClientId.Codex], PortableTestSupport.Inputs(summary));
        service.Apply(preview.Id);

        var parked = destination.ParkStore.Parked(ClientId.Cursor, "remote");
        Assert.Equal(
            "replacement-value",
            JsonDocument.Parse(parked!.SourceText).Root["headers"]?["Authorization"]?.StringValue);
        var toml = TomlDocument.Parse(destination.Text(".codex/config.toml"));
        Assert.Equal(
            "replacement-value",
            toml.Table("mcp_servers", "remote", "http_headers")?.Value("Authorization")?.StringValue);
        Assert.Equal("https://example.test/mcp", toml.Table("mcp_servers", "remote")?.Value("url")?.StringValue);
    }

    [Fact]
    public void CodexInlineStaticAndEnvironmentHeaderShapesAndBearerReferencesBecomeRequiredInputs()
    {
        using var source = new ToggleHarness();
        source.Write("""
            [mcp_servers]
            inline = { url = "https://inline.test/mcp", http_headers = { Authorization = "inline-secret" }, env_http_headers = { "X-Api-Key" = "API_KEY" } }
            local = { command = "node", cwd = "/private/project", args = ["/private/server.js"] }

            [mcp_servers.remote]
            url = "https://remote.test/mcp"
            bearer_token_env_var = "BEARER_VALUE"
            http_headers = { "X-Static" = "table-inline-secret" }

            [mcp_servers.remote.env_http_headers]
            "X-Env" = "ENV_VALUE"
            """, ".codex/config.toml");

        var summary = source.ExportTo();

        bool Has(string server, string suffix) => summary.Requirements.Any(requirement =>
            requirement.Label.StartsWith(server, StringComparison.Ordinal) &&
            requirement.Label.EndsWith(suffix, StringComparison.Ordinal));
        Assert.True(Has("inline", "header Authorization"));
        Assert.True(Has("inline", "header X-Api-Key"));
        Assert.True(Has("remote", "header Authorization"));
        Assert.True(Has("remote", "header X-Static"));
        Assert.True(Has("remote", "header X-Env"));
        Assert.True(Has("local", "working directory"));
        var text = File.ReadAllText(source.PortableFile());
        foreach (var value in new[]
                 {
                     "inline-secret", "table-inline-secret", "ENV_VALUE", "BEARER_VALUE",
                     "/private/project", "/private/server.js",
                 })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BundledExtensionAndCustomSourceDefinitionsNeverBecomePortableWritableConfigurations()
    {
        using var source = new ToggleHarness();
        var custom = ClientKey.Custom(Guid.NewGuid());
        var bundled = new Server
        {
            Id = "bundled",
            Name = "bundled",
            Transport = Transport.Stdio,
            Command = "npx",
            Args = [],
            Env = [],
            Url = null,
            EnabledIn = new Dictionary<ClientKey, Enablement>
            {
                [ClientId.Cursor] = Enablement.Enabled,
                [custom] = Enablement.Enabled,
            },
            Origin = new Origin.ConfigFile(ClientId.Cursor),
            Fingerprint = "unused",
            IsBundled = true,
            DefinitionSource = """{"command":"npx"}""",
        };
        bundled.DefinitionsByClient[ClientId.Cursor] = ClientDefinition.From(bundled);
        bundled.DefinitionsByClient[custom] = new ClientDefinition(
            Transport.Stdio, "node", [], [], null, new Origin.ConfigFile(custom), false, "{}");
        var extension = new Server
        {
            Id = "extension",
            Name = "extension",
            Transport = Transport.Stdio,
            Command = "node",
            Args = [],
            Env = [],
            Url = null,
            EnabledIn = new Dictionary<ClientKey, Enablement> { [ClientId.ClaudeDesktop] = Enablement.Enabled },
            Origin = new Origin.ClaudeDesktopExtension("bundle"),
            Fingerprint = "unused",
            IsBundled = true,
            DefinitionSource = "{}",
        };

        var summary = source.Portable().Export(source.PortableFile(), [bundled, extension]);

        Assert.Empty(summary.ServerNames);
        Assert.Empty(summary.Requirements);
    }

    [Fact]
    public void WindowsLauncherSpellingsTravelAsTheirPortableNamesAndPathsAreInputs()
    {
        using var source = new ToggleHarness();
        source.Write("""
            {"mcpServers":{
              "shim":{"command":"npx.cmd","args":["-y","@modelcontextprotocol/server-memory"]},
              "full":{"command":"C:\\Program Files\\nodejs\\npx.cmd","args":["-y","@modelcontextprotocol/server-memory"]},
              "wrapped":{"command":"cmd","args":["/c","npx","-y","@modelcontextprotocol/server-memory"]}
            }}
            """, ".cursor/mcp.json");

        var summary = source.ExportTo();
        var setup = PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile()));

        var shim = setup.Servers.Single(server => server.Name == "shim");
        Assert.Equal("npx", shim.Command?.Literal);
        Assert.Equal("@modelcontextprotocol/server-memory", shim.Args[1].Literal);

        // A path to a launcher is not a launcher the format allows, so the package
        // that follows it cannot be vouched for either — and the export still validates.
        var full = setup.Servers.Single(server => server.Name == "full");
        Assert.Null(full.Command?.Literal);
        Assert.Equal("-y", full.Args[0].Literal);
        Assert.Null(full.Args[1].Literal);

        var wrapped = setup.Servers.Single(server => server.Name == "wrapped");
        Assert.Null(wrapped.Command?.Literal);
        Assert.DoesNotContain(
            wrapped.Args, argument => argument.Literal is "npx" or "@modelcontextprotocol/server-memory");
        Assert.Equal(3, summary.ServerNames.Count);
    }

    [Fact]
    public void ADefinitionThatCannotBeDescribedPortablyIsLeftOutInsteadOfFailingTheWholeExport()
    {
        using var source = new ToggleHarness();
        source.Write("""
            {"mcpServers":{
              "good":{"command":"npx","args":["-y"]},
              "has space":{"command":"npx"},
              "bad-env":{"command":"npx","env":{"NOT-A-NAME":"x"}},
              "empty":{}
            }}
            """, ".cursor/mcp.json");

        var summary = source.ExportTo();

        Assert.Equal(["good"], summary.ServerNames);
        Assert.Contains(summary.Warnings, warning => warning.Contains("could not be described portably", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportCoversProfilesAndFiltersThemToWhatWasExported()
    {
        using var source = new ToggleHarness();
        source.Write("""{"mcpServers":{"memory":{"command":"npx"}}}""", ".cursor/mcp.json");
        new ProfileStore(source.Paths).Create("Mixed", ["memory", "not-exported"], 5_000);
        new ProfileStore(source.Paths).Create("Empty", []);

        var summary = source.ExportTo();
        var setup = PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile()));

        Assert.Equal(["Empty", "Mixed"], summary.ProfileNames);
        Assert.Equal(["memory"], setup.Profiles.Single(profile => profile.Name == "Mixed").ServerIDs);
        Assert.Equal(5_000, setup.Profiles.Single(profile => profile.Name == "Mixed").TokenBudget);
        Assert.Empty(setup.Profiles.Single(profile => profile.Name == "Empty").ServerIDs);
    }
}
