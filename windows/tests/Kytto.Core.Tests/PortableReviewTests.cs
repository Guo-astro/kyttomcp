using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Portable;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// Cases an independent review of portable setup found that the first round of tests
/// did not cover (§7.13).
/// </summary>
public sealed class PortableReviewTests
{
    private static ToggleHarness ExportedSource()
    {
        var harness = new ToggleHarness();
        harness.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        harness.ExportTo();
        return harness;
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData(".")]
    [InlineData("::1")]
    public void ExportRefusesAnyShareOfThisComputerEvenWhenNothingExistsThere(string host)
    {
        using var source = ExportedSource();
        var spelling = host == "::1"
            ? @"\\0--1.ipv6-literal.net\share\setup.json"
            : @"\\" + host + @"\share\setup.json";

        Assert.Equal(
            PortableSetupFailure.ProtectedLocation,
            Assert.Throws<PortableSetupException>(() =>
                source.Portable().Export(spelling, source.Discover().Servers)).Failure);
    }

    [Fact]
    public void ExportRefusesTheMachineNameAndTheExtendedUncSpelling()
    {
        using var source = ExportedSource();
        var servers = source.Discover().Servers;

        foreach (var spelling in new[]
                 {
                     @"\\" + Environment.MachineName + @"\share\setup.json",
                     @"\\?\UNC\localhost\share\setup.json",
                 })
        {
            Assert.Equal(
                PortableSetupFailure.ProtectedLocation,
                Assert.Throws<PortableSetupException>(() => source.Portable().Export(spelling, servers)).Failure);
        }
    }

    [Fact]
    public void ExportCannotOverwriteALiveConfigurationThroughTheLocalAdministrativeShare()
    {
        using var source = ExportedSource();
        source.Write("""{"mcpServers":{"keep":{"command":"node"}}}""", ".cursor/mcp.json");
        var config = Path.Combine(source.Home, ".cursor", "mcp.json");
        var before = File.ReadAllBytes(config);
        var drive = config[0];

        foreach (var host in new[] { "localhost", "127.0.0.1", Environment.MachineName })
        {
            var alias = $@"\\{host}\{drive}$" + config[2..];
            // The share exists only for an administrator whose server service runs; the
            // refusal does not depend on it, which the test above covers.
            if (!File.Exists(alias)) continue;

            Assert.Throws<PortableSetupException>(() => source.Portable().Export(alias, source.Discover().Servers));
            Assert.Equal(before, File.ReadAllBytes(config));
        }
    }

    // MARK: - What an import does to files it does not own

    /// <summary>
    /// The new text is the old text with one run of characters added somewhere: nothing
    /// before it, after it or inside what was kept moved, changed or went missing.
    /// </summary>
    private static string InsertedRun(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
               before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }
        Assert.True(prefix + suffix == before.Length, $"original text was altered, not just extended:\n--- before\n{before}\n--- after\n{after}");
        return after[prefix..^suffix];
    }

    public static TheoryData<string, string, string> Destinations => new()
    {
        // client folder file, existing text
        { ".cursor/mcp.json", "{\r\n  // keep me\r\n  \"theme\": \"dark\",\r\n  \"mcpServers\": {\r\n    \"old\": { \"command\": \"node\", \"x-unknown\": [1, 2,], }, // trailing\r\n  },\r\n}\r\n", "cursor" },
        { ".cursor/mcp.json", "\uFEFF{\"mcpServers\":{}}", "cursor" },
        { ".cursor/mcp.json", "{}", "cursor" },
        { @"AppData\Roaming\Code\User\mcp.json", "// vs code\n{\n\t\"inputs\": [],\n\t\"servers\": {\n\t\t// first\n\t\t\"old\": {\"command\": \"node\"}\n\t}\n}\n", "vsCode" },
        { @"AppData\Roaming\Claude\claude_desktop_config.json", "{\"preferences\":{\"a\":1},\"mcpServers\":{\"old\":{\"command\":\"node\"}}}", "claudeDesktop" },
        { ".claude.json", "{\"numStartups\": 3, \"projects\": {\"C:\\\\x\": {\"mcpServers\": {}}}, \"mcpServers\": {\"old\": {\"command\": \"node\"}}}", "claudeCode" },
        { ".codex/config.toml", "# top\r\nmodel = \"x\"\r\n\r\n[mcp_servers.old]\r\ncommand = \"node\" # note\r\n\r\n[projects.'C:\\keep']\r\ntrust_level = \"trusted\"\r\n", "codex" },
        { ".codex/config.toml", "", "codex" },
    };

    [Theory]
    [MemberData(nameof(Destinations))]
    public void AnImportOnlyAddsToAFileAndNeverRewritesWhatWasThere(string file, string existing, string clientRaw)
    {
        foreach (var enable in new[] { false, true })
        {
            using var source = new ToggleHarness();
            using var destination = new ToggleHarness();
            source.Write(
                """{"mcpServers":{"new":{"command":"npx","args":["-y","pkg"],"env":{"K":"v"}},"é.日本\"q\"/b":{"command":"uvx","args":["tool"]}}}""",
                ".cursor/mcp.json");
            var summary = source.ExportTo();
            destination.Write(existing, file);
            var denyFile = ".claude/settings.json";
            const string DenyBefore = "{\n  // keep\n  \"permissions\": {\"allow\": []},\n  \"deniedMcpServers\": [{\"serverName\": \"other\"}]\n}\n";
            if (clientRaw == "claudeCode") destination.Write(DenyBefore, denyFile);

            var client = ClientIds.FromRaw(clientRaw)!.Value;
            var service = destination.Portable();
            var preview = service.Preview(
                source.PortableFile(), [client], PortableTestSupport.Inputs(summary), enableImportedServers: enable);
            service.Apply(preview.Id);

            var after = destination.Text(file);
            if (enable || clientRaw is "claudeCode" or "codex")
            {
                var run = InsertedRun(existing, after);
                Assert.Contains("new", run, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(existing, after);
            }

            if (clientRaw == "claudeCode" && !enable)
            {
                var deny = destination.Text(denyFile);
                var run = InsertedRun(DenyBefore, deny);
                Assert.Contains("serverName", run, StringComparison.Ordinal);
                Assert.Contains("\"new\"", run, StringComparison.Ordinal);
            }

            var imported = destination.Discover().Servers.Where(server => server.Name is "new" or "é.日本\"q\"/b").ToArray();
            Assert.Equal(2, imported.Length);
            foreach (var server in imported)
            {
                Assert.Equal(enable ? Enablement.Enabled : Enablement.Disabled, server.EnabledIn[client]);
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe", @"C:\Users\Some One\My Server\main.js", @"D:\Projects\ünï cödé\work dir")]
    [InlineData(@"\\fileserver\tools\node.exe", @"C:\a""b'c\main.js", @"C:\café\日本語")]
    public void PathsWithSpacesQuotesAndNonAsciiLettersSurviveIntoEveryFormat(string command, string script, string cwd)
    {
        using var source = new ToggleHarness();
        source.Write(
            """{"mcpServers":{"local":{"command":"C:\\Users\\a\\node.exe","args":["C:\\Users\\a\\main.js","-y"],"cwd":"C:\\Users\\a"}}}""",
            ".cursor/mcp.json");
        var summary = source.ExportTo();
        var inputs = summary.Requirements.ToDictionary(
            requirement => requirement.Id,
            requirement => requirement.Label switch
            {
                var label when label.EndsWith("command", StringComparison.Ordinal) => command,
                var label when label.EndsWith("argument 1", StringComparison.Ordinal) => script,
                _ => cwd,
            });

        foreach (var client in new[] { ClientId.Cursor, ClientId.ClaudeCode, ClientId.Codex, ClientId.VsCode, ClientId.ClaudeDesktop })
        {
            using var destination = new ToggleHarness();
            var service = destination.Portable();
            var preview = service.Preview(source.PortableFile(), [client], inputs, enableImportedServers: true);
            service.Apply(preview.Id);

            var found = destination.Discover().Servers.Single(server => server.Name == "local");
            Assert.Equal(command, found.Command);
            Assert.Equal(script, found.Args[0]);
            Assert.Equal(cwd, found.WorkingDirectory);
        }
    }

    [Theory]
    [InlineData("tab\there", "codex")]
    [InlineData("ctl\u0001x\u007fy\u0085z\u2028w", "codex")]
    [InlineData("quote\"back\\slash'single", "codex")]
    [InlineData("new\nline\r\nsecond", "codex")]
    [InlineData("ctl\u0001x\u007fy\u0085z\u2028w", "cursor")]
    [InlineData("new\nline\r\nsecond", "claudeCode")]
    [InlineData("emoji😀 and é́ and ﻿ bom", "codex")]
    public void TypedValuesRoundTripExactlyOrAreRefusedWithoutEchoingThem(string typed, string clientRaw)
    {
        using var source = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"node","env":{"K":"v"}}}}""", ".cursor/mcp.json");
        var summary = source.ExportTo();
        using var destination = new ToggleHarness();
        var client = ClientIds.FromRaw(clientRaw)!.Value;
        var service = destination.Portable();
        var inputs = summary.Requirements.ToDictionary(requirement => requirement.Id, _ => typed);

        try
        {
            var preview = service.Preview(source.PortableFile(), [client], inputs, enableImportedServers: true);
            service.Apply(preview.Id);
        }
        catch (Exception error)
        {
            Assert.DoesNotContain(typed.Trim(), error.Message, StringComparison.Ordinal);
            Assert.False(destination.Exists(client == ClientId.Codex ? ".codex/config.toml" : ".cursor/mcp.json"));
            return;
        }

        var found = destination.Discover().Servers.Single(server => server.Name == "new");
        Assert.Equal(typed, found.Env.Single(entry => entry.Key == "K").Value);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("claudeCode")]
    [InlineData("vsCode")]
    public void ARemoteServerWithHeadersAndAnAwkwardNameImportsWithTheValuesTheUserTyped(string clientRaw)
    {
        using var source = new ToggleHarness();
        source.Write(
            """{"mcpServers":{"a.b\"c]d":{"type":"http","url":"https://example.test/mcp","headers":{"Authorization":"Bearer x","X-Api-Key":"k"}}}}""",
            ".cursor/mcp.json");
        var summary = source.ExportTo();
        using var destination = new ToggleHarness();
        var client = ClientIds.FromRaw(clientRaw)!.Value;
        var inputs = summary.Requirements.ToDictionary(
            requirement => requirement.Id,
            requirement => requirement.Label.EndsWith("remote URL", StringComparison.Ordinal)
                ? "https://remote.test/mcp?a=1&b=\"2\""
                : "typed \"value\" with \\ and 'quotes'");
        var service = destination.Portable();

        var preview = service.Preview(source.PortableFile(), [client], inputs, enableImportedServers: true);
        service.Apply(preview.Id);

        var found = destination.Discover().Servers.Single(server => server.Name == "a.b\"c]d");
        Assert.Equal("https://remote.test/mcp?a=1&b=\"2\"", found.Url);
        Assert.Equal(Enablement.Enabled, found.EnabledIn[client]);
        var written = destination.Text(client switch
        {
            ClientId.Codex => ".codex/config.toml",
            ClientId.ClaudeCode => ".claude.json",
            ClientId.VsCode => @"AppData\Roaming\Code\User\mcp.json",
            _ => ".cursor/mcp.json",
        });
        Assert.Contains("typed", written, StringComparison.Ordinal);
    }

    // MARK: - The shared gate

    [Fact]
    public async Task ImportsRunningBesideTogglesProfilesMeasurementsAndExportsNeitherDeadlockNorLoseAnything()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            """{"mcpServers":{"a0":{"command":"node"},"a1":{"command":"node"},"a2":{"command":"node"}}}""",
            ".cursor/mcp.json");
        harness.Write("# keep\n[mcp_servers.t0]\ncommand = \"node\"\n", ".codex/config.toml");
        var service = harness.Portable();
        var profiles = new Kytto.Core.Profiles.ProfileStore(harness.Paths);
        var metadata = new MetadataStore(harness.Paths);
        var succeeded = new System.Collections.Concurrent.ConcurrentBag<int>();
        var unexpected = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void Guard(Action work)
        {
            try
            {
                work();
            }
            catch (Exception error) when (
                error is PortableSetupException or ConfigWriteException or ToggleException or
                         Kytto.Core.Profiles.ProfileException or ConfigTransactionException)
            {
                // A refusal is an answer; a hang or any other failure is the finding.
            }
            catch (Exception error)
            {
                unexpected.Enqueue(error);
            }
        }

        var tasks = new List<Task>
        {
            Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    var index = i;
                    Guard(() =>
                    {
                        var file = Path.Combine(harness.Home, $"setup{index}.json");
                        File.WriteAllBytes(file, PortableSetupSerializer.Write(new PortableSetup(
                            PortableSetup.FormatName,
                            PortableSetup.CurrentVersion,
                            [new PortableServer(
                                $"imp{index}", ClientId.Cursor, true, Transport.Stdio,
                                PortableValue.FromLiteral("node"), [], new Dictionary<string, PortableValue>(),
                                new Dictionary<string, PortableValue>(), null, null)],
                            [new PortableProfile($"Prof{index}", [$"imp{index}"], null)],
                            [], [])));
                        var preview = service.Preview(file, [ClientId.Codex, ClientId.Cursor, ClientId.ClaudeCode]);
                        service.Apply(preview.Id);
                        succeeded.Add(index);
                    });
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    Guard(() =>
                    {
                        var server = harness.Server("a1");
                        harness.Toggles.SetEnabled(false, server, ClientId.Cursor);
                        harness.Toggles.SetEnabled(true, harness.Server("a1"), ClientId.Cursor);
                    });
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 60; i++)
                {
                    var n = i;
                    Guard(() => profiles.Create($"Side{n}", ["a0"]));
                    Guard(() => profiles.All());
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 60; i++)
                {
                    Guard(() => metadata.Record(
                        "a0",
                        new HealthResult(HealthStatus.Passed, Timestamp.Now, 0, [], null, null, 0.1, null, null),
                        null));
                    Guard(() => harness.ParkStore.Park(ClientId.VsCode, $"p{i}", "{}"));
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    Guard(() => service.PendingContractSummary());
                    Guard(() => service.Export(
                        Path.Combine(harness.Home, "out", "export.json"), harness.Discover().Servers));
                }
            }),
        };

        var all = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)));
        Assert.True(finished == all, "a deadlock: the operations did not finish");
        await all;
        Assert.True(unexpected.IsEmpty, string.Join("\n", unexpected.Select(error => error.ToString())));

        // Nothing that was reported as imported went missing, and none arrived switched on.
        var servers = harness.Discover().Servers;
        var profileNames = new Kytto.Core.Profiles.ProfileStore(harness.Paths).AllStrict().Select(p => p.Name).ToHashSet();
        Assert.NotEmpty(succeeded);
        foreach (var index in succeeded)
        {
            var imported = servers.Single(server => server.Name == $"imp{index}");
            Assert.Equal(Enablement.Disabled, imported.EnabledIn[ClientId.Cursor]);
            Assert.Equal(Enablement.Disabled, imported.EnabledIn[ClientId.Codex]);
            Assert.Contains($"Prof{index}", profileNames);
        }
    }

    [Fact]
    public void AShareOfAnotherComputerIsNotMistakenForThisOne()
    {
        Assert.False(FinalPath.IsShareOnThisComputer(@"\\another-computer-that-is-not-this-one\share\setup.json"));
        Assert.False(FinalPath.IsShareOnThisComputer(@"C:\Users\someone\setup.json"));
        Assert.False(FinalPath.IsShareOnThisComputer(@"\\10.255.255.1\share\setup.json"));
    }
}
