using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using static Kytto.Core.Tests.Support.PluginFixtures;

namespace Kytto.Core.Tests;

/// <summary>
/// Only plugins a client has registered and switched on are servers (§4). Everything
/// else under a plugin cache — stale versions, uninstalled leftovers, test fixtures —
/// is just a directory.
/// </summary>
public class PluginDiscoveryTests
{
    private const string Wrong = """{"mcpServers":{"wrong":{"command":"never-run"}}}""";

    // MARK: - Claude Code

    [Fact]
    public void ClaudeReadsTheRegisteredActiveVersionAndIgnoresFixturesStaleAndDisabledCaches()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\2");
        harness.RegisterClaude("demo@example", Entry("user", root, "2"));
        harness.EnableClaudePlugins("demo@example", true);
        harness.Write(
            """{"name":"demo","displayName":"Demo tools","version":"2"}""",
            $@"{ClaudeCache}\demo\2\.claude-plugin\plugin.json");
        const string Config =
            """{"mcpServers":{"inspector":{"command":"${CLAUDE_PLUGIN_ROOT}/bin/inspect","args":["${CLAUDE_PLUGIN_ROOT}/config with spaces.json"],"env":{"ROOT":"${CLAUDE_PLUGIN_ROOT}"},"cwd":"."}}}""";
        harness.Write(Config, $@"{ClaudeCache}\demo\2\.mcp.json");
        foreach (var path in new[]
        {
            $@"{ClaudeCache}\demo\1\.mcp.json",
            $@"{ClaudeCache}\demo\2\tests\fixtures\.mcp.json",
            $@"{ClaudeCache}\uninstalled\1\.mcp.json",
        })
        {
            harness.Write(Wrong, path);
        }
        var before = Snapshot(harness.Home);

        var result = harness.Discover();

        Assert.Equal(["plugin:demo:inspector"], result.Servers.Select(server => server.Name));
        var server = Assert.Single(result.Servers);
        Assert.True(server.IsBundled);
        Assert.True(server.IsPlugin);
        Assert.Equal(new Origin.Plugin(ClientId.ClaudeCode, "demo@example", "Demo tools", "2"), server.Origin);
        Assert.Equal("plugin", server.Origin.Kind);
        Assert.Equal(new ServerSource("plugin", "Demo tools", "2", ReadOnly: true), server.Origin.Source);

        // Expanded once, when it runs; what the plugin wrote is what is shown.
        Assert.Equal(root + "/bin/inspect", server.Runnable.Command);
        Assert.Equal([root + "/config with spaces.json"], server.Runnable.Args);
        Assert.Equal(root, server.RunnableEnvironment.First(entry => entry.Key == "ROOT").Value);
        Assert.Equal(root, server.RunnableEnvironment.First(entry => entry.Key == "CLAUDE_PLUGIN_ROOT").Value);
        Assert.Contains(server.RunnableEnvironment, entry => entry.Key == "CLAUDE_PLUGIN_DATA");
        Assert.Contains("${CLAUDE_PLUGIN_ROOT}", server.CommandSummary, StringComparison.Ordinal);
        Assert.False(server.HasRelativePath);
        Assert.Equal(root, server.WorkingDirectory);

        var copy = server.DefinitionsByClient[ClientId.ClaudeCode];
        Assert.Equal(root, copy.WorkingDirectory);
        Assert.Equal(root, copy.Placeholders["CLAUDE_PLUGIN_ROOT"]);
        Assert.True(copy.IsPlugin);

        // Read-only means read-only: nothing under the profile changed, the data
        // directory was only named, and no config write could target the plugin.
        Assert.Equal(Config, File.ReadAllText(Path.Combine(root, ".mcp.json")));
        Assert.Equal(before, Snapshot(harness.Home));
        Assert.False(Directory.Exists(harness.Full(@".claude\plugins\data")));
        Assert.Null(harness.Resolver().WriteTarget(Path.Combine(root, ".mcp.json"), ClientId.ClaudeCode));
        Assert.Null(harness.Resolver().WriteTarget(Path.Combine(root, ".mcp.json"), ClientId.Codex));
    }

    [Fact]
    public void ADisabledPluginAndAnAmbiguousRegistrationAreNeverShownAsActive()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        var entry = Entry("user", root, "1");
        harness.RegisterClaude("demo@example", entry);
        harness.EnableClaudePlugins("demo@example", false);
        harness.Write("""{"mcpServers":{"ignored":{"command":"never-run"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");

        Assert.Empty(harness.Discover().Servers);

        harness.EnableClaudePlugins("demo@example", true);
        harness.RegisterClaude("demo@example", entry, entry);
        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void ARegisteredPluginWithNoEnabledSettingIsActiveUnlessItsManifestSaysOtherwise()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");

        Assert.Equal(["plugin:demo:tool"], harness.Discover().Servers.Select(server => server.Name));

        harness.Write("""{"name":"demo","defaultEnabled":false}""", $@"{ClaudeCache}\demo\1\.claude-plugin\plugin.json");
        Assert.Empty(harness.Discover().Servers);

        // Said on in settings, it is on whatever the manifest's default is.
        harness.EnableClaudePlugins("demo@example", true);
        Assert.Single(harness.Discover().Servers);
    }

    [Fact]
    public void ClaudeMergesDeclaredComponentsInOrderWithoutReadingUndeclaredFiles()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write(
            """{"name":"demo","mcpServers":["./config/extra.json",{"tool":{"command":"inline-last"}},"./../outside.json"]}""",
            $@"{ClaudeCache}\demo\1\.claude-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"tool":{"command":"default-first"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write(
            """{"mcpServers":{"tool":{"command":"declared-second"},"extra":{"command":"extra"}}}""",
            $@"{ClaudeCache}\demo\1\config\extra.json");
        harness.Write("""{"mcpServers":{"escape":{"command":"never"}}}""", $@"{ClaudeCache}\demo\outside.json");
        harness.Write("""{"mcpServers":{"undeclared":{"command":"never"}}}""", $@"{ClaudeCache}\demo\1\other\.mcp.json");

        var result = harness.Discover();

        Assert.Equal(["plugin:demo:extra", "plugin:demo:tool"], result.Servers.Select(server => server.Name));
        Assert.Equal("inline-last", result.Servers.Single(server => server.Name == "plugin:demo:tool").Command);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("external MCP", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnparsableSettingsFileSkipsPluginDiscoveryRatherThanGuessingEnablement()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write("{ not json", @".claude\settings.json");

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rather than guessing", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnsupportedRegistryFormatIsReportedAndNothingIsScanned()
    {
        using var harness = new ToggleHarness();
        harness.Write("""{"version":1,"plugins":{}}""", @".claude\plugins\installed_plugins.json");
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unsupported format", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPluginsThatClaimTheSameServerNameKeepTheFirstAndSayWhy()
    {
        using var harness = new ToggleHarness();
        var first = harness.Full($@"{ClaudeCache}\demo\1");
        var second = harness.Full(@".claude\plugins\cache\other\demo\1");
        harness.RegisterClaude(new Dictionary<string, Dictionary<string, object?>[]>
        {
            ["demo@example"] = [Entry("user", first, "1")],
            ["demo@other"] = [Entry("user", second, "1")],
        });
        harness.Write("""{"mcpServers":{"tool":{"command":"first"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write("""{"mcpServers":{"tool":{"command":"second"}}}""", @".claude\plugins\cache\other\demo\1\.mcp.json");

        var result = harness.Discover();

        var server = Assert.Single(result.Servers);
        Assert.Equal("first", server.Command);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Multiple sources", StringComparison.Ordinal));
    }

    [Fact]
    public void AJunctionCannotStandInForAPluginRootOrAComponentPath()
    {
        using var harness = new ToggleHarness();
        var real = harness.Full(@"real-plugin");
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", @"real-plugin\.mcp.json");
        var junction = harness.Full($@"{ClaudeCache}\linked\1");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        MakeJunction(junction, real);
        harness.RegisterClaude("linked@example", Entry("user", junction, "1"));

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing or ambiguous", StringComparison.Ordinal));

        // And inside a real plugin: a declared path through a junction is not followed.
        var plugin = harness.Full($@"{ClaudeCache}\demo\1");
        harness.Write("""{"mcpServers":{"escape":{"command":"never"}}}""", @"outside\.mcp.json");
        harness.Write("{}", $@"{ClaudeCache}\demo\1\placeholder.txt");
        MakeJunction(Path.Combine(plugin, "out"), harness.Full("outside"));
        Assert.Null(PluginInstallations.ContainedFile("./out/.mcp.json", plugin));
    }

    // MARK: - Project and local scope

    [Fact]
    public void ProjectPluginsRespectProjectAndLocalSettingsPrecedence()
    {
        using var harness = new ToggleHarness();
        var project = harness.Full(@"projects\demo");
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("project", root, "1", project));
        harness.EnableClaudePlugins("demo@example", false);
        harness.Write(
            JsonSerializer.Serialize(new { enabledPlugins = new Dictionary<string, bool> { ["demo@example"] = true } }),
            @"projects\demo\.claude\settings.json");
        harness.Write(
            """{"mcpServers":{"tool":{"command":"node","args":["${CLAUDE_PROJECT_DIR}/server.js"]}}}""",
            $@"{ClaudeCache}\demo\1\.mcp.json");

        var result = harness.Discover();

        var client = Assert.Single(result.Clients, candidate => candidate.ScopeLabel == project);
        var server = Assert.Single(result.Servers);
        Assert.Equal(Kytto.Core.Settings.ConfigurationScope.Workspace, client.ConfigurationScope);
        Assert.True(client.IsReadOnly);
        Assert.Equal(Discovery.ProjectClientID(project), client.Id);
        Assert.Equal(Enablement.Absent, server.EnabledIn[ClientId.ClaudeCode]);
        Assert.Equal(Enablement.Enabled, server.EnabledIn[client.Id]);
        Assert.Equal([project + "/server.js"], server.Runnable.Args);
        Assert.Equal(1, client.ServerCount);

        // Local settings are the most specific of the three and switch it off again.
        harness.Write(
            JsonSerializer.Serialize(new { enabledPlugins = new Dictionary<string, bool> { ["demo@example"] = false } }),
            @"projects\demo\.claude\settings.local.json");
        var off = harness.Discover();
        Assert.Empty(off.Servers);
        Assert.DoesNotContain(off.Clients, candidate => candidate.ScopeLabel == project);
    }

    [Fact]
    public void AUserPluginIsInEffectInAProjectUnlessThatProjectSwitchesItOff()
    {
        using var harness = new ToggleHarness();
        var project = harness.Full(@"projects\demo");
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.EnableClaudePlugins("demo@example", true);
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write(
            JsonSerializer.Serialize(new
            {
                projects = new Dictionary<string, object>
                {
                    [project] = new { mcpServers = new { local = new { command = "node" } } },
                },
            }),
            ".claude.json");

        var result = harness.Discover();
        var workspace = Assert.Single(result.Clients, candidate => candidate.ScopeLabel == project);
        var plugin = result.Servers.Single(server => server.Name == "plugin:demo:tool");
        Assert.Equal(Enablement.Enabled, plugin.EnabledIn[ClientId.ClaudeCode]);
        Assert.Equal(Enablement.Enabled, plugin.EnabledIn[workspace.Id]);

        harness.Write(
            JsonSerializer.Serialize(new { enabledPlugins = new Dictionary<string, bool> { ["demo@example"] = false } }),
            @"projects\demo\.claude\settings.json");
        var off = harness.Discover().Servers.Single(server => server.Name == "plugin:demo:tool");
        Assert.Equal(Enablement.Enabled, off.EnabledIn[ClientId.ClaudeCode]);
        Assert.Equal(Enablement.Absent, off.EnabledIn[workspace.Id]);
    }

    [Fact]
    public void OneProjectSpelledTwoWaysIsOneScopeWithTheIdItsConfigGaveIt()
    {
        using var harness = new ToggleHarness();
        // Spaces in the path, a lower-case drive letter and forward slashes in the shared
        // config, and the plugin registry's own spelling with a trailing separator: one
        // project to Windows, three strings to a program that compares them.
        var directory = harness.Full(@"Work Space\Demo App");
        Directory.CreateDirectory(directory);
        var configSpelling = char.ToLowerInvariant(directory[0]) + directory[1..].Replace('\\', '/');
        var registrySpelling = char.ToUpperInvariant(directory[0]) + directory[1..] + @"\";
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("project", root, "1", registrySpelling));
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write(
            JsonSerializer.Serialize(new
            {
                projects = new Dictionary<string, object>
                {
                    [configSpelling] = new { mcpServers = new { local = new { command = "node" } } },
                },
            }),
            ".claude.json");

        var result = harness.Discover();

        var workspace = Assert.Single(result.Clients, candidate =>
            candidate.ConfigurationScope == Kytto.Core.Settings.ConfigurationScope.Workspace);
        Assert.Equal(Discovery.ProjectClientID(configSpelling), workspace.Id);
        Assert.Equal(configSpelling, workspace.ScopeLabel);
        Assert.Equal(2, workspace.ServerCount);
        Assert.Equal(Enablement.Enabled, result.Servers.Single(s => s.Name == "local").EnabledIn[workspace.Id]);
        Assert.Equal(Enablement.Enabled, result.Servers.Single(s => s.Name == "plugin:demo:tool").EnabledIn[workspace.Id]);
    }

    [Fact]
    public void AProjectOnlyAPluginNamesGetsTheIdItsExactPathDerives()
    {
        using var harness = new ToggleHarness();
        var project = harness.Full(@"Work Space\Plugin Only");
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("local", root, "1", project));
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");

        var result = harness.Discover();

        var workspace = Assert.Single(result.Clients, candidate => candidate.ScopeLabel == project);
        Assert.Equal(Discovery.ProjectClientID(project), workspace.Id);
        Assert.StartsWith("Claude Code · ", workspace.DisplayName, StringComparison.Ordinal);
        Assert.Equal(Enablement.Enabled, result.Servers.Single().EnabledIn[workspace.Id]);
    }

    [Fact]
    public void ARegistrationThatNamesNoUsableProjectCreatesNoScope()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example",
            Entry("project", root, "1", "relative\\project"),
            Entry("local", root, "1", @"\rooted\only"),
            Entry("project", root, "1", @"\\?\C:\device\path"));
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");

        var result = harness.Discover();

        Assert.DoesNotContain(result.Clients, candidate =>
            candidate.ConfigurationScope == Kytto.Core.Settings.ConfigurationScope.Workspace);
        Assert.Empty(result.Servers);
    }

    // MARK: - Codex

    [Fact]
    public void CodexNeedsAnEnabledRegistrationAndASinglyIdentifiedPackageVersion()
    {
        using var harness = new ToggleHarness();
        const string Base = @".codex\plugins\cache\example\demo";
        harness.Write("""{"name":"demo","version":"2"}""", $@"{Base}\2\.codex-plugin\plugin.json");
        harness.Write(
            """{"mcpServers":{"tool":{"command":"./bin/server","cwd":".","env":{"ROOT":"${CODEX_PLUGIN_ROOT}"}}}}""",
            $@"{Base}\2\.mcp.json");
        harness.Write("""{"mcpServers":{"fixture":{"command":"never"}}}""", $@"{Base}\2\tests\.mcp.json");
        harness.Write("""{"mcpServers":{"disabled":{"command":"never"}}}""", @".codex\plugins\cache\example\disabled\1\.mcp.json");

        // A package in the cache nobody registered is not a server.
        Assert.Empty(harness.Discover().Servers);

        harness.Write(
            "[plugins.\"demo@example\"]\nenabled = true\n[plugins.\"disabled@example\"]\nenabled = false\n",
            @".codex\config.toml");
        var result = harness.Discover();

        var server = Assert.Single(result.Servers);
        var root = harness.Full($@"{Base}\2");
        Assert.Equal("tool", server.Name);
        Assert.Equal(new Origin.Plugin(ClientId.Codex, "demo@example", "demo", "2"), server.Origin);
        Assert.Equal(root, server.WorkingDirectory);
        Assert.Equal(root, server.RunnableEnvironment.First(entry => entry.Key == "ROOT").Value);
        Assert.Equal(Enablement.Enabled, server.EnabledIn[ClientId.Codex]);
        Assert.True(server.IsBundled);

        // A second cached version and no registered one: the active one is not known.
        harness.Write("""{"name":"demo","version":"1"}""", $@"{Base}\1\.codex-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"stale":{"command":"never"}}}""", $@"{Base}\1\.mcp.json");
        var ambiguous = harness.Discover();
        Assert.Empty(ambiguous.Servers);
        Assert.Contains(ambiguous.Diagnostics, d => d.Message.Contains("several cached versions", StringComparison.Ordinal));

        // A version the registration names settles it.
        harness.Write(
            "[plugins.\"demo@example\"]\nenabled = true\nversion = \"2\"\n",
            @".codex\config.toml");
        Assert.Equal(["tool"], harness.Discover().Servers.Select(s => s.Name));
    }

    [Fact]
    public void ACodexPluginIdThatCouldEscapeTheCacheIsNeverFollowed()
    {
        using var harness = new ToggleHarness();
        harness.Write("""{"name":"x","version":"1"}""", @".codex\plugins\cache\x\x\1\.codex-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"evil":{"command":"never"}}}""", @".codex\plugins\cache\x\x\1\.mcp.json");
        harness.Write(
            "[plugins.\"..@x\"]\nenabled = true\n[plugins.\"x@..\"]\nenabled = true\n" +
            "[plugins.\"x\\\\y@x\"]\nenabled = true\n[plugins.\"nomarketplace\"]\nenabled = true\n",
            @".codex\config.toml");

        Assert.Empty(harness.Discover().Servers);
    }

    [Fact]
    public void ACodexPluginRegisteredTwiceIsUncertainAndSkipped()
    {
        using var harness = new ToggleHarness();
        harness.Write("""{"name":"demo","version":"1"}""", @".codex\plugins\cache\example\demo\1\.codex-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"tool":{"command":"node"}}}""", @".codex\plugins\cache\example\demo\1\.mcp.json");
        harness.Write(
            "[plugins.\"demo@example\"]\nenabled = true\n[plugins.\"demo@example\"]\nenabled = false\n",
            @".codex\config.toml");

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void AConfiguredCodexServerOutranksAPluginServerOfTheSameName()
    {
        using var harness = new ToggleHarness();
        harness.Write("""{"name":"demo","version":"1"}""", @".codex\plugins\cache\example\demo\1\.codex-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"tool":{"command":"from-plugin"}}}""", @".codex\plugins\cache\example\demo\1\.mcp.json");
        harness.Write(
            "[mcp_servers.tool]\ncommand = \"from-config\"\n\n[plugins.\"demo@example\"]\nenabled = true\n",
            @".codex\config.toml");

        var result = harness.Discover();

        var server = Assert.Single(result.Servers);
        Assert.Equal("from-config", server.Command);
        Assert.False(server.IsBundled);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Multiple sources", StringComparison.Ordinal));
    }

    // MARK: - Working directory

    [Theory]
    [InlineData(".", "ROOT")]
    [InlineData("bin", @"ROOT\bin")]
    [InlineData("./bin/../lib", @"ROOT\lib")]
    [InlineData("${CLAUDE_PLUGIN_ROOT}/work dir", @"ROOT\work dir")]
    [InlineData("${CLAUDE_PLUGIN_DATA}/work", @"DATA\work")]
    [InlineData(@"C:\absolute\elsewhere", @"C:\absolute\elsewhere")]
    [InlineData(@"c:/absolute/forward", @"c:\absolute\forward")]
    [InlineData(@"\\server\share\data", @"\\server\share\data")]
    public void APluginsWorkingDirectoryIsResolvedInItsOwnContext(string declared, string expected)
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write(
            JsonSerializer.Serialize(new { mcpServers = new { tool = new { command = "node", cwd = declared } } }),
            $@"{ClaudeCache}\demo\1\.mcp.json");

        var server = Assert.Single(harness.Discover().Servers);

        var data = harness.Full(@".claude\plugins\data\demo-example");
        Assert.Equal(
            expected.Replace("ROOT", root, StringComparison.Ordinal).Replace("DATA", data, StringComparison.Ordinal),
            server.WorkingDirectory);
    }

    [Theory]
    [InlineData("../outside", "escapes its installation")]
    [InlineData("a/../../outside", "escapes its installation")]
    [InlineData("${NOT_A_KNOWN_VARIABLE}/x", "substitution")]
    [InlineData("/rooted-on-the-current-drive", "substitution")]
    [InlineData(@"\rooted", "substitution")]
    [InlineData("C:drive-relative", "substitution")]
    [InlineData("stream:name", "substitution")]
    public void AWorkingDirectoryThatCannotBeEstablishedSkipsTheServerAndSaysSo(string declared, string reason)
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write(
            JsonSerializer.Serialize(new { mcpServers = new { tool = new { command = "node", cwd = declared } } }),
            $@"{ClaudeCache}\demo\1\.mcp.json");

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ARelativeWorkingDirectoryNeverFollowsAJunctionOutOfThePlugin()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write(
            """{"mcpServers":{"tool":{"command":"node","cwd":"linked"}}}""",
            $@"{ClaudeCache}\demo\1\.mcp.json");
        Directory.CreateDirectory(harness.Full("elsewhere"));
        MakeJunction(Path.Combine(root, "linked"), harness.Full("elsewhere"));

        var result = harness.Discover();

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("escapes its installation", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyAnAbsoluteWindowsCwdInAnOrdinaryConfigIsKept()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["absolute"] = new { command = "node", cwd = @"C:\Work Space\app\" },
                    ["unc"] = new { command = "node", cwd = @"\\server\share\app" },
                    ["relative"] = new { command = "node", cwd = "app" },
                    ["posix"] = new { command = "node", cwd = "/home/me/app" },
                    ["unexpanded"] = new { command = "node", cwd = "${workspaceFolder}" },
                    ["none"] = new { command = "node" },
                },
            }),
            @".cursor\mcp.json");

        var servers = harness.Discover().Servers.ToDictionary(server => server.Name);

        Assert.Equal(@"C:\Work Space\app", servers["absolute"].WorkingDirectory);
        Assert.Equal(@"\\server\share\app", servers["unc"].WorkingDirectory);
        Assert.Null(servers["relative"].WorkingDirectory);
        Assert.Null(servers["posix"].WorkingDirectory);
        Assert.Null(servers["unexpanded"].WorkingDirectory);
        Assert.Null(servers["none"].WorkingDirectory);
    }

    // MARK: - Substitution

    [Fact]
    public void ASubstitutedValueIsNeverReinterpretedAsAnotherPlaceholder()
    {
        var document = Kytto.Core.Json.JsonDocument.Parse(
            """{"command":"${ROOT}/server","env":{"VALUE":"${ROOT}"}}""");
        var server = ServerFields.FromJson(document.Root).MakeServer(
            "test", new Origin.ConfigFile(ClientId.Cursor), isBundled: false, sourceText: "{}");
        server.Placeholders["ROOT"] = "/temp/${OTHER}";
        server.Placeholders["OTHER"] = "wrong";

        Assert.Equal("/temp/${OTHER}/server", server.Runnable.Command);
        Assert.Equal("/temp/${OTHER}", server.RunnableEnvironment.First().Value);
        // And an unknown token is left exactly as written.
        Assert.Equal("${UNKNOWN}", server.ExpandingKnownPlaceholders("${UNKNOWN}"));
    }

    [Fact]
    public void ALaunchVariableNeverShadowsAConfiguredOneThatDiffersOnlyInCase()
    {
        var server = new Server
        {
            Id = "t", Name = "t", Transport = Transport.Stdio, Command = "node", Args = [],
            Env = [new EnvEntry("claude_plugin_root", "mine")], Url = null, EnabledIn = [],
            Origin = new Origin.ConfigFile(ClientId.Cursor), Fingerprint = "0", IsBundled = false,
            DefinitionSource = "{}",
            LaunchEnvironment = { ["CLAUDE_PLUGIN_ROOT"] = @"C:\plugin", ["CLAUDE_PLUGIN_DATA"] = @"C:\data" },
        };

        var environment = server.RunnableEnvironment;

        Assert.Equal(2, environment.Count);
        Assert.Equal("mine", environment.Single(entry => entry.Key == "claude_plugin_root").Value);
        Assert.Equal(@"C:\data", environment.Single(entry => entry.Key == "CLAUDE_PLUGIN_DATA").Value);
    }

    // MARK: - Containment and paths

    [Theory]
    [InlineData("./../outside.json")]
    [InlineData("../outside.json")]
    [InlineData("./a/../../outside.json")]
    [InlineData("/absolute.json")]
    [InlineData(@"C:\absolute.json")]
    [InlineData(@"\\server\share\file.json")]
    [InlineData("./config.json:stream")]
    [InlineData("./nul.json")]
    [InlineData("./CON.json")]
    [InlineData("./com1.json")]
    [InlineData("./LPT9.json")]
    [InlineData("./config.json.")]
    [InlineData("./config.json ")]
    [InlineData("./config.txt")]
    [InlineData("./")]
    [InlineData("./.")]
    [InlineData("./a//b.json")]
    [InlineData("config/extra.json")]
    public void AComponentPathMustBeADeclaredPlainFileInsideThePlugin(string declared)
    {
        using var harness = new ToggleHarness();
        var root = harness.Full("plugin");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        File.WriteAllText(Path.Combine(root, "extra.json"), "{}");
        File.WriteAllText(harness.Full("outside.json"), "{}");

        Assert.Null(PluginInstallations.ContainedFile(declared, root));
    }

    [Theory]
    [InlineData("./config/extra.json")]
    [InlineData(@".\config\extra.json")]
    [InlineData("./config\\extra.json")]
    public void ADeclaredFileInsideThePluginIsAccepted(string declared)
    {
        using var harness = new ToggleHarness();
        var root = harness.Full("plugin with spaces");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "extra.json"), "{}");

        Assert.Equal(
            Path.Combine(root, "config", "extra.json"),
            PluginInstallations.ContainedFile(declared, root));
    }

    [Theory]
    [InlineData(@"C:\Users\you\app", true)]
    [InlineData(@"c:/Users/you/app", true)]
    [InlineData(@"C:\Work Space\app", true)]
    [InlineData(@"\\server\share\app", true)]
    [InlineData("//server/share/app", true)]
    [InlineData("/work/demo", false)]
    [InlineData(@"\rooted", false)]
    [InlineData("C:drive-relative", false)]
    [InlineData("relative/path", false)]
    [InlineData(@"C:\a\..\b", false)]
    [InlineData(@"\\?\C:\app", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("", false)]
    public void OnlyAPlainDriveOrUncPathIsAnAbsoluteRegistration(string path, bool expected) =>
        Assert.Equal(expected, PluginInstallations.IsAbsoluteWindowsPath(path));

    [Fact]
    public void TwoSpellingsOfOneProjectCompareEqualAndDifferentProjectsDoNot()
    {
        Assert.True(PluginInstallations.SameProject(@"C:\Users\You\App", "c:/users/you/app/"));
        Assert.True(PluginInstallations.SameProject(@"\\SERVER\Share\App\", "//server/share/app"));
        Assert.True(PluginInstallations.SameProject(@"C:\", "c:/"));
        Assert.False(PluginInstallations.SameProject(@"C:\Users\You\App", @"C:\Users\You\App2"));
        Assert.False(PluginInstallations.SameProject(@"C:\Users\You\App", @"D:\Users\You\App"));
        Assert.False(PluginInstallations.SameProject(@"\\server\share\app", @"C:\server\share\app"));
    }

    [Fact]
    public void ADisplayStringFromAFileCarriesNoControlOrFormattingCharacters()
    {
        Assert.Equal("Demo tools", PluginInstallations.Display("  Demo\u0000 tools\u202E "));
        Assert.Null(PluginInstallations.Display("\u202E\n"));
        Assert.Equal(81, PluginInstallations.Display(new string('x', 200))!.Length);
    }

    // MARK: - Nothing is written, spawned or watched in the cache

    [Fact]
    public void WatchingCoversTheRegistrationFilesAndNotTheClaudePluginCache()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));

        var targets = new Discovery(harness.Home, new AllInstalledLocator()).WatchTargets();

        Assert.Contains(targets, target =>
            !target.IncludeSubdirectories
            && string.Equals(target.Path, harness.Full(@".claude\plugins\installed_plugins.json"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target =>
            !target.IncludeSubdirectories
            && string.Equals(target.Path, Path.Combine(root, ".mcp.json"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target =>
            target.IncludeSubdirectories
            && string.Equals(target.Path, harness.Full(@".codex\plugins\cache"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, target =>
            target.IncludeSubdirectories
            && target.Path.Contains(@".claude\plugins", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscoveryAcrossBothClientsCreatesAndChangesNothing()
    {
        using var harness = new ToggleHarness();
        var root = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", root, "1"));
        harness.Write("""{"mcpServers":{"tool":{"command":"node","cwd":"."}}}""", $@"{ClaudeCache}\demo\1\.mcp.json");
        harness.Write("""{"name":"demo","version":"1"}""", @".codex\plugins\cache\example\demo\1\.codex-plugin\plugin.json");
        harness.Write("""{"mcpServers":{"tool2":{"command":"node","cwd":"."}}}""", @".codex\plugins\cache\example\demo\1\.mcp.json");
        harness.EnableCodexPlugin("demo@example", true);
        var before = Snapshot(harness.Home);
        var bytes = before
            .Where(path => File.Exists(harness.Full(path)))
            .ToDictionary(path => path, path => File.ReadAllBytes(harness.Full(path)));

        Assert.Equal(2, harness.Discover().Servers.Count);

        Assert.Equal(before, Snapshot(harness.Home));
        foreach (var (path, content) in bytes) Assert.Equal(content, File.ReadAllBytes(harness.Full(path)));
    }
}
