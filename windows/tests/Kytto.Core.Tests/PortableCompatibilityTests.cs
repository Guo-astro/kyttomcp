using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Portable;
using Kytto.Core.Profiles;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>
/// The same file, moving in both directions between the macOS and Windows apps
/// (§7.13). The macOS fixture is real output of the compiled macOS core; the Windows
/// fixture is this app's export of the same inputs.
/// </summary>
public sealed class PortableCompatibilityTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "compatibility", name);

    [Fact]
    public void TheMacOsExportInspectsAndImportsDisabledOnWindows()
    {
        var macExport = Fixture("portable-v1-macos-export.json");
        using var destination = new ToggleHarness();
        var service = destination.Portable();

        var summary = service.Inspect(macExport);
        Assert.Equal(["memory", "remote"], summary.ServerNames);
        Assert.Equal(["Research"], summary.ProfileNames);
        Assert.Equal(6, summary.Requirements.Count);
        Assert.Equal(1, summary.PendingContractCount);

        var values = summary.Requirements.ToDictionary(
            requirement => requirement.Id,
            requirement => requirement.Label switch
            {
                var label when label.Contains(": argument 3", StringComparison.Ordinal) => "--root",
                var label when label.Contains(": remote URL", StringComparison.Ordinal) => "https://example.test/mcp",
                _ when requirement.Kind == PortableRequirementKind.Path => @"C:\Users\example\workspace",
                _ => "replacement-value",
            });
        var preview = service.Preview(macExport, [ClientId.Cursor, ClientId.Codex], values);
        Assert.False(preview.Enabled);

        service.Apply(preview.Id);

        var servers = destination.Discover().Servers;
        foreach (var name in new[] { "memory", "remote" })
        {
            var server = servers.First(candidate => candidate.Name == name);
            Assert.Equal(Enablement.Disabled, server.EnabledIn[ClientId.Cursor]);
            Assert.Equal(Enablement.Disabled, server.EnabledIn[ClientId.Codex]);
        }

        var parked = Kytto.Core.Json.JsonDocument.Parse(destination.ParkStore.Parked(ClientId.Cursor, "memory")!.SourceText).Root;
        Assert.Equal("npx", parked["command"]?.StringValue);
        Assert.Equal(
            ["-y", "@modelcontextprotocol/server-memory", "--root", @"C:\Users\example\workspace"],
            parked["args"]!.Elements!.Select(node => node.StringValue!));
        Assert.Equal(@"C:\Users\example\workspace", parked["cwd"]?.StringValue);
        Assert.Equal("replacement-value", parked["env"]?["FIXTURE_TOKEN"]?.StringValue);

        var toml = TomlDocument.Parse(destination.Text(".codex/config.toml"));
        Assert.Equal("https://example.test/mcp", toml.Table("mcp_servers", "remote")?.Value("url")?.StringValue);
        Assert.Equal(
            "replacement-value",
            toml.Table("mcp_servers", "remote", "http_headers")?.Value("Authorization")?.StringValue);
        Assert.Equal(
            @"C:\Users\example\workspace",
            toml.Table("mcp_servers", "memory")?.Value("cwd")?.StringValue);

        var profile = Assert.Single(new ProfileStore(destination.Paths).All());
        Assert.Equal("Research", profile.Name);
        Assert.Equal(["memory", "remote"], profile.ServerIDs);
        Assert.Equal(12_000, profile.TokenBudget);

        // The imported reference is pending evidence: nothing was checked here.
        var pending = Assert.Single(service.PendingContractSummary());
        Assert.Equal("memory", pending.ServerID);
        Assert.Null(pending.Matching);
    }

    // MARK: - The Windows export

    /// <summary>
    /// The inputs the macOS golden fixture was generated from, with this platform's
    /// spelling of the working directory.
    /// </summary>
    private static PortableSetupSummary ExportTheGoldenInputs(ToggleHarness source, string target)
    {
        source.Write("""
            {"mcpServers":{
              "memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory","--root","C:\\Users\\example\\workspace"],"env":{"FIXTURE_TOKEN":"fixture-secret-do-not-export"},"cwd":"C:\\Users\\example\\workspace"},
              "remote":{"type":"http","url":"https://example.test/mcp?token=fixture-url-secret","headers":{"Authorization":"Bearer fixture-header-secret"}}
            }}
            """, ".cursor/mcp.json");
        new ProfileStore(source.Paths).Create("Research", ["memory", "remote"], 12_000);
        // Vector 2 of the digest vectors: the reviewed tool the macOS export carries.
        var tool = new ToolSummary(
            "search",
            "Find / files & <notes> — čučoriedka 😀",
            """{"required":["query"],"properties":{"query":{"type":"string","default":"a/b"}},"type":"object"}""",
            new ToolAnnotations(ReadOnlyHint: true, DestructiveHint: false),
            999);
        new MetadataStore(source.Paths).Record(
            "memory",
            new HealthResult(HealthStatus.Passed, Timestamp.Now, 1, [tool], null, null, 0, null, null),
            null);
        return source.Portable().Export(target, source.Discover().Servers, includeReviewedContracts: true);
    }

    /// <summary>Requirement ids are opaque; what matters is which input is which.</summary>
    private static string Normalised(string json)
    {
        using var document = JsonDocument.Parse(json);
        var ids = document.RootElement.GetProperty("requirements").EnumerateArray()
            .Select(requirement => requirement.GetProperty("id").GetString()!)
            .ToArray();
        var text = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var index = 0; index < ids.Length; index++)
        {
            text = text.Replace(ids[index], $"requirement-{index + 1}", StringComparison.Ordinal);
        }
        return text;
    }

    [Fact]
    public void AWindowsExportOfTheGoldenInputsIsTheFixtureUpToOpaqueIds()
    {
        using var source = new ToggleHarness();
        var target = source.PortableFile();
        ExportTheGoldenInputs(source, target);
        var produced = File.ReadAllBytes(target);

        // `KYTTO_WRITE_WINDOWS_FIXTURE=<path>` regenerates the checked-in fixture,
        // the way generate-golden.swift regenerates the macOS one.
        if (Environment.GetEnvironmentVariable("KYTTO_WRITE_WINDOWS_FIXTURE") is { Length: > 0 } output)
        {
            File.WriteAllBytes(output, produced);
        }

        var fixture = File.ReadAllText(Fixture("portable-v1-windows-export.json"));
        Assert.Equal(Normalised(fixture), Normalised(System.Text.Encoding.UTF8.GetString(produced)));
        var text = System.Text.Encoding.UTF8.GetString(produced);
        foreach (var secret in new[]
                 {
                     "fixture-secret-do-not-export", "fixture-url-secret", "fixture-header-secret",
                     @"C:\\Users\\example\\workspace",
                 })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWindowsExportCarriesTheSameContractDigestTheMacOsExportDoes()
    {
        var mac = PortableSetupSerializer.Read(File.ReadAllBytes(Fixture("portable-v1-macos-export.json")));
        var windows = PortableSetupSerializer.Read(File.ReadAllBytes(Fixture("portable-v1-windows-export.json")));

        Assert.Equal(
            Assert.Single(mac.ReviewedContracts).ToolDigests,
            Assert.Single(windows.ReviewedContracts).ToolDigests);
        Assert.Equal(
            mac.Profiles.Select(profile => (profile.Name, profile.ServerIDs.AsEnumerable(), profile.TokenBudget)),
            windows.Profiles.Select(profile => (profile.Name, profile.ServerIDs.AsEnumerable(), profile.TokenBudget)));
        Assert.Equal(
            mac.Servers.Select(server => (server.Name, server.Transport, server.WasEnabled)),
            windows.Servers.Select(server => (server.Name, server.Transport, server.WasEnabled)));
        Assert.Equal(
            mac.Requirements.Select(requirement => (requirement.Label, requirement.Kind)).Order(),
            windows.Requirements.Select(requirement => (requirement.Label, requirement.Kind)).Order());
    }

    /// <summary>
    /// An independent reading of the rules the macOS validator applies, written
    /// against the raw JSON so it shares nothing with this app's own validator.
    /// </summary>
    [Fact]
    public void TheWindowsFixtureSatisfiesTheMacOsValidatorRules()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Fixture("portable-v1-windows-export.json")));
        var root = document.RootElement;
        Assert.Equal(
            ["format", "profiles", "requirements", "reviewedContracts", "servers", "version"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("kytto-portable-setup", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var ids = root.GetProperty("requirements").EnumerateArray().Select(requirement =>
        {
            Assert.Equal(
                ["id", "kind", "label"],
                requirement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.True(Guid.TryParseExact(requirement.GetProperty("id").GetString(), "D", out _));
            Assert.Contains(requirement.GetProperty("kind").GetString(), new[] { "secret", "path", "value" });
            return requirement.GetProperty("id").GetString()!;
        }).ToHashSet();

        string[] safeCommands = ["npx", "npm", "pnpm", "yarn", "bunx", "bun", "uvx", "uv", "pipx", "node", "python", "python3", "docker"];
        var used = new HashSet<string>();
        void Value(JsonElement element)
        {
            var keys = element.EnumerateObject().Select(property => property.Name).ToArray();
            // Exactly one of the two.
            Assert.Single(keys);
            if (keys[0] == "requirementID")
            {
                Assert.Contains(element.GetProperty("requirementID").GetString()!, ids);
                Assert.True(used.Add(element.GetProperty("requirementID").GetString()!));
            }
            else
            {
                Assert.Equal("literal", keys[0]);
            }
        }

        foreach (var server in root.GetProperty("servers").EnumerateArray())
        {
            Assert.All(
                server.EnumerateObject().Select(property => property.Name),
                name => Assert.Contains(
                    name,
                    new[] { "name", "sourceClient", "wasEnabled", "transport", "command", "args", "environment", "headers", "url", "workingDirectory" }));
            foreach (var required in new[] { "name", "sourceClient", "wasEnabled", "transport", "args", "environment", "headers" })
            {
                Assert.True(server.TryGetProperty(required, out _), $"{required} is required");
            }
            if (server.TryGetProperty("command", out var command))
            {
                Value(command);
                if (command.TryGetProperty("literal", out var literal)) Assert.Contains(literal.GetString(), safeCommands);
            }
            if (server.TryGetProperty("url", out var url))
            {
                Value(url);
                Assert.True(url.TryGetProperty("requirementID", out _), "every URL is a required input");
            }
            if (server.TryGetProperty("workingDirectory", out var cwd))
            {
                Value(cwd);
                Assert.True(cwd.TryGetProperty("requirementID", out _), "a working directory is never public");
            }
            foreach (var argument in server.GetProperty("args").EnumerateArray()) Value(argument);
            foreach (var property in server.GetProperty("environment").EnumerateObject()) Value(property.Value);
            foreach (var property in server.GetProperty("headers").EnumerateObject()) Value(property.Value);
        }
        Assert.Equal(ids, used);

        foreach (var contract in root.GetProperty("reviewedContracts").EnumerateArray())
        {
            Assert.All(
                contract.GetProperty("toolDigests").EnumerateObject(),
                digest => Assert.Matches("^[0-9a-f]{64}$", digest.Value.GetString()!));
        }
    }

    [Fact]
    public void TheWindowsFixtureImportsOnWindowsToo()
    {
        var fixture = Fixture("portable-v1-windows-export.json");
        using var destination = new ToggleHarness();
        var service = destination.Portable();
        var summary = service.Inspect(fixture);

        var preview = service.Preview(fixture, [ClientId.VsCode], PortableTestSupport.Inputs(summary));
        service.Apply(preview.Id);

        Assert.All(
            destination.Discover().Servers.Where(server => server.Name is "memory" or "remote"),
            server => Assert.Equal(Enablement.Disabled, server.EnabledIn[ClientId.VsCode]));
    }
}
