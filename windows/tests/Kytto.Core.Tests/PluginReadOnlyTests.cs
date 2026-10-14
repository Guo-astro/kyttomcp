using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using static Kytto.Core.Tests.Support.PluginFixtures;

namespace Kytto.Core.Tests;

/// <summary>
/// A plugin's server is not edited, copied or toggled — natively, so a caller that
/// skips the disabled button gets the same answer (§4). A Claude Desktop extension is a
/// different thing and stays under its owning client's control.
/// </summary>
public class PluginReadOnlyTests
{
    private const string ExtensionManifestPath =
        $@"{ToggleHarness.ClaudeSupport}\Claude Extensions\ant.dir.test.osascript\manifest.json";

    private const string ExtensionSettingsPath =
        $@"{ToggleHarness.ClaudeSupport}\Claude Extensions Settings\ant.dir.test.osascript.json";

    /// <summary>
    /// A Claude Code plugin, a Codex plugin that shares its name with an ordinary
    /// Cursor entry, and a Claude Desktop extension — three kinds of "bundled".
    /// </summary>
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();

        var claudeRoot = harness.Full($@"{ClaudeCache}\demo\1");
        harness.RegisterClaude("demo@example", Entry("user", claudeRoot, "1"));
        harness.EnableClaudePlugins("demo@example", true);
        harness.Write(
            """{"mcpServers":{"tool":{"command":"node","args":["${CLAUDE_PLUGIN_ROOT}/server.js"]}}}""",
            $@"{ClaudeCache}\demo\1\.mcp.json");

        harness.Write("""{"name":"codexdemo","version":"1"}""", @".codex\plugins\cache\example\codexdemo\1\.codex-plugin\plugin.json");
        harness.Write(
            """{"mcpServers":{"shared":{"command":"codex-plugin-command","args":["--plugin"]},"only-codex":{"command":"node"}}}""",
            @".codex\plugins\cache\example\codexdemo\1\.mcp.json");
        harness.EnableCodexPlugin("codexdemo@example", true);

        harness.Write(
            """{"mcpServers":{"shared":{"command":"node","args":["cursor.js"]},"plain":{"command":"node"}}}""",
            @".cursor\mcp.json");

        harness.Write(Fixture.ExtensionManifest.Text(), ExtensionManifestPath);
        harness.Write(Fixture.ExtensionSettingsDisabled.Text(), ExtensionSettingsPath);
        return harness;
    }

    private static string[] Everything(ToggleHarness harness) =>
        Snapshot(harness.Home)
            .Where(path => File.Exists(harness.Full(path)))
            .Select(path => path + "\0" + Convert.ToHexString(File.ReadAllBytes(harness.Full(path))))
            .ToArray();

    // MARK: - Toggle

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePluginsOwnerCannotSwitchItOnOrOff(bool enabled)
    {
        using var harness = MakeHarness();
        var plugin = harness.Server("plugin:demo:tool");
        var before = Everything(harness);

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(enabled, plugin, ClientId.ClaudeCode));

        Assert.Contains("installed plugin", error.Message, StringComparison.Ordinal);
        Assert.Contains("turn the plugin off in the client", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Everything(harness));
    }

    [Theory]
    [InlineData(ClientId.ClaudeDesktop)]
    [InlineData(ClientId.Cursor)]
    [InlineData(ClientId.VsCode)]
    [InlineData(ClientId.Codex)]
    public void ThePluginCannotBeCopiedIntoAnotherClientWhetherOrNotThatClientHasTheName(ClientId other)
    {
        using var harness = MakeHarness();
        var plugin = harness.Server("plugin:demo:tool");
        var before = Everything(harness);

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, plugin, other));

        Assert.Contains("install the plugin there", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Everything(harness));
        Assert.False(harness.Exists(@".vscode\mcp.json"));
    }

    [Fact]
    public void ACodexPluginCannotBeSwitchedInCodexOrCopiedAnywhereElse()
    {
        using var harness = MakeHarness();
        var plugin = harness.Server("only-codex");
        Assert.True(plugin.IsPlugin);
        Assert.Equal(Enablement.Enabled, plugin.EnabledIn[ClientId.Codex]);
        var before = Everything(harness);

        Assert.Contains("installed plugin", Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(false, plugin, ClientId.Codex)).Message, StringComparison.Ordinal);
        Assert.Contains("install the plugin there", Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, plugin, ClientId.Cursor)).Message, StringComparison.Ordinal);
        Assert.Contains("install the plugin there", Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, plugin, ClientId.ClaudeCode)).Message, StringComparison.Ordinal);

        Assert.Equal(before, Everything(harness));
    }

    [Fact]
    public void ANameThatIsAPluginsInOneClientAndAConfigEntryInAnotherStaysControllableWhereItIsConfig()
    {
        using var harness = MakeHarness();
        var shared = harness.Server("shared");
        Assert.False(shared.IsBundled);
        Assert.True(shared.HoldsPluginCopy(ClientId.Codex));
        Assert.False(shared.HoldsPluginCopy(ClientId.Cursor));
        var codexBefore = harness.Text(@".codex\config.toml");

        // Cursor's own entry switches off and back on exactly as before.
        harness.Toggles.SetEnabled(false, shared, ClientId.Cursor);
        Assert.Equal(Enablement.Disabled, harness.Server("shared").EnabledIn[ClientId.Cursor]);
        harness.Toggles.SetEnabled(true, harness.Server("shared"), ClientId.Cursor);
        Assert.Equal(Enablement.Enabled, harness.Server("shared").EnabledIn[ClientId.Cursor]);
        Assert.Equal(
            "node",
            JsonDocument.Parse(harness.Text(@".cursor\mcp.json")).ValueAt("mcpServers", "shared", "command")?.StringValue);

        // Codex's copy is the plugin's, and is refused.
        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(false, harness.Server("shared"), ClientId.Codex));
        Assert.Contains("installed plugin", error.Message, StringComparison.Ordinal);
        Assert.Equal(codexBefore, harness.Text(@".codex\config.toml"));
    }

    [Fact]
    public void AnExtensionIsStillSwitchedByItsOwningClientAndStillNotCopied()
    {
        using var harness = MakeHarness();
        var extension = harness.Server("Control your Mac");
        Assert.True(extension.IsBundled);
        Assert.False(extension.IsPlugin);
        Assert.Equal("extension", extension.Origin.Kind);
        Assert.Equal(Enablement.Disabled, extension.EnabledIn[ClientId.ClaudeDesktop]);

        // The owner: the same boolean in its own settings file, as before plugins existed.
        harness.Toggles.SetEnabled(true, extension, ClientId.ClaudeDesktop);
        Assert.True(JsonDocument.Parse(harness.Text(ExtensionSettingsPath)).Root["isEnabled"]?.BoolValue);
        harness.Toggles.SetEnabled(false, harness.Server("Control your Mac"), ClientId.ClaudeDesktop);
        Assert.False(JsonDocument.Parse(harness.Text(ExtensionSettingsPath)).Root["isEnabled"]?.BoolValue);

        // Anywhere else: still an installed bundle, with the extension's own wording.
        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, harness.Server("Control your Mac"), ClientId.Cursor));
        Assert.Contains("cannot be copied", error.Message, StringComparison.Ordinal);
    }

    // MARK: - Authoring

    [Fact]
    public void ThePluginIsNotEditableDeletableUnifiableOrArgumentPatchableAndNothingIsWritten()
    {
        using var harness = MakeHarness();
        var plugin = harness.Server("plugin:demo:tool");
        var all = harness.Discover().Servers;
        var before = Everything(harness);
        var authoring = harness.Authoring();

        void Refused(Action action)
        {
            var error = Assert.Throws<AuthoringException>(action);
            Assert.Contains("installed plugin", error.Message, StringComparison.Ordinal);
        }

        Refused(() => authoring.Update(ServerDraft.Editing(plugin), plugin.Name, plugin, all));
        Refused(() => authoring.Delete(plugin));
        Refused(() => authoring.RemoveFrom(plugin, ClientId.ClaudeCode));
        Refused(() => authoring.Unify(ServerDraft.Editing(plugin), plugin, [ClientId.ClaudeCode, ClientId.Cursor]));
        Refused(() => authoring.ReplaceArgument(
            plugin,
            new Dictionary<ClientId, ArgumentEdit>
            {
                [ClientId.ClaudeCode] = new(0, "${CLAUDE_PLUGIN_ROOT}/server.js", "other.js"),
            }));

        Assert.Equal(before, Everything(harness));
    }

    [Fact]
    public void ACodexPluginIsNotRemovableByNameFromCodex()
    {
        using var harness = MakeHarness();
        var plugin = harness.Server("only-codex");
        var before = Everything(harness);

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().RemoveFrom(plugin, ClientId.Codex));

        Assert.Contains("installed plugin", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Everything(harness));
    }

    [Fact]
    public void EditingAndDeletingASharedNameTouchesTheConfigEntryAndNeverShadowsThePlugin()
    {
        using var harness = MakeHarness();
        var shared = harness.Server("shared");
        var codexBefore = harness.Text(@".codex\config.toml");

        var draft = ServerDraft.Editing(shared) with { Args = ["changed.js"] };
        harness.Authoring().Update(draft, "shared", shared, harness.Discover().Servers);

        Assert.Equal(
            "changed.js",
            JsonDocument.Parse(harness.Text(@".cursor\mcp.json")).ValueAt("mcpServers", "shared", "args")?.Elements?[0].StringValue);
        // No `[mcp_servers.shared]` was written beside the plugin to shadow it.
        Assert.Equal(codexBefore, harness.Text(@".codex\config.toml"));

        harness.Authoring().Delete(harness.Server("shared"));
        Assert.Null(JsonDocument.Parse(harness.Text(@".cursor\mcp.json")).ValueAt("mcpServers", "shared"));
        Assert.Equal(codexBefore, harness.Text(@".codex\config.toml"));

        // What is left is the plugin's, still shown, still read-only.
        var remaining = harness.Server("shared");
        Assert.True(remaining.IsPlugin);
        Assert.Equal(Enablement.Absent, remaining.EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void ANewServerCannotTakeAPluginsNameInTheClientThatProvidesIt()
    {
        using var harness = MakeHarness();
        var draft = new ServerDraft
        {
            Name = "only-codex",
            Transport = Transport.Stdio,
            Command = "node",
        };
        var codexBefore = harness.Text(@".codex\config.toml");

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Create(draft, [ClientId.Codex], harness.Discover().Servers));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal(codexBefore, harness.Text(@".codex\config.toml"));
    }

    // MARK: - Drift and comparison

    [Fact]
    public void ThePluginTakesPartInAComparisonButIsNeverASourceOrATarget()
    {
        using var harness = MakeHarness();
        var shared = harness.Server("shared");

        var drift = ServerDrift.Detect(shared);

        Assert.NotNull(drift);
        Assert.Contains(ClientId.Codex, drift.UnwritableClientIDs.Select(id => id.BuiltIn!.Value));
        Assert.All(
            drift.Variants.Where(variant => variant.ClientIDs.Contains((ClientKey)ClientId.Codex)),
            variant => Assert.False(variant.CanBeSource));
        Assert.Contains(drift.Variants, variant => variant.CanBeSource);
    }
}
