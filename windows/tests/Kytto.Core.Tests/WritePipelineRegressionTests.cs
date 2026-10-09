using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>
/// The write pipeline's promise (§6): an edit changes what it says it changes and
/// nothing else. Each test here is a way that promise was found broken.
/// </summary>
public sealed class WritePipelineRegressionTests
{
    private const string CursorPath = @".cursor\mcp.json";
    private const string CodexPath = @".codex\config.toml";

    private static AuthoringResult Save(ToggleHarness harness, string name, Func<ServerDraft, ServerDraft>? change = null)
    {
        var server = harness.Server(name);
        var draft = ServerDraft.Editing(server);
        if (change is not null) draft = change(draft);
        return harness.Authoring().Update(
            draft.MergingSecrets(server), server.Name, server, harness.Discover().Servers);
    }

    // MARK: - Held copies (D1)

    [Fact]
    public void EditingASwitchedOffServerInAPresenceOnlyClientKeepsItOff()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), CursorPath);
        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);
        Assert.Null(JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "github"));

        var result = Save(harness, "github", draft => draft with { Command = "bunx" });

        // Nothing in the file: the edit went to the held copy.
        Assert.Empty(result.Changed);
        Assert.Equal([ClientId.Cursor], result.ParkedUpdated);
        Assert.Null(JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "github"));
        Assert.Equal(Enablement.Disabled, harness.Server("github").EnabledIn[ClientId.Cursor]);

        // And switching it on brings back the edit, not the old bytes.
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.Cursor);
        var github = JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "github");
        Assert.Equal("bunx", github?["command"]?.StringValue);
        Assert.Equal("ghp_exampletokenvalue", github?.ValueAt("env", "GITHUB_PERSONAL_ACCESS_TOKEN")?.StringValue);
    }

    [Fact]
    public void ADefinitionPutBackByHandOutranksAStaleHeldCopy()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), CursorPath);
        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);
        Assert.NotNull(harness.ParkStore.Parked(ClientId.Cursor, "github"));

        // Someone puts it back by hand, with their own change.
        var edited = Fixture.CursorMcp.Text().Replace("\"npx\"", "\"pnpx\"", StringComparison.Ordinal);
        harness.Write(edited, CursorPath);

        var github = harness.Server("github");
        Assert.Equal(Enablement.Enabled, github.EnabledIn[ClientId.Cursor]);
        Assert.Equal("pnpx", github.DefinitionsByClient[ClientId.Cursor].Command);

        // Switching it "on" again must not restore the held bytes over the file.
        harness.Toggles.SetEnabled(true, github, ClientId.Cursor);
        Assert.Equal(edited, harness.Text(CursorPath));
    }

    // MARK: - Symbolic links (D4)

    /// <summary>
    /// A config kept in a dotfiles folder and linked into place is written where it
    /// lives; the link stays a link.
    /// </summary>
    /// <remarks>
    /// Creating a file symlink needs Developer Mode or an elevated run. Without
    /// either this test has nothing to exercise and passes without asserting.
    /// </remarks>
    [Fact]
    public void ASymlinkedConfigIsWrittenThroughAndStaysALink()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @"dotfiles\cursor-mcp.json");
        var real = Path.Combine(harness.Home, "dotfiles", "cursor-mcp.json");
        var link = Path.Combine(harness.Home, CursorPath);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Null(JsonDocument.Parse(File.ReadAllText(real)).ValueAt("mcpServers", "github"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(link)!, "*.tmp"));

        // Rolling back a file that did not exist removes what was created, not the link.
        var dangling = Path.Combine(harness.Home, @".codeium\windsurf\mcp_config.json");
        var missing = Path.Combine(harness.Home, "dotfiles", "not-yet.json");
        Directory.CreateDirectory(Path.GetDirectoryName(dangling)!);
        File.CreateSymbolicLink(dangling, missing);
        AtomicWriter.Write("{}"u8.ToArray(), dangling, throughLinks: true);
        Assert.True(File.Exists(missing));
        new AtomicConfigFileWriter().Delete(dangling);
        Assert.False(File.Exists(missing));
        Assert.NotNull(new FileInfo(dangling).LinkTarget);
    }

    [Fact]
    public void KyttosOwnStorageIsNeverWrittenThroughALink()
    {
        // Without a link to follow, the default and the config path agree.
        var path = Path.Combine(Path.GetTempPath(), "kytto-tests", Guid.NewGuid().ToString("N"), "file.json");
        Assert.Equal(path, AtomicWriter.ResolvingLinks(path));
    }

    // MARK: - Restore (D2)

    [Fact]
    public void WhatARestoreBringsBackIsReadFromTheBackup()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), CursorPath);
        harness.Discover();
        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);

        // The backup is of the file as it was: github still in it.
        var backup = harness.Backups.List(ClientId.Cursor).First();
        Assert.Equal(["github", "figma"], harness.Backups.ServerNames(backup, harness.Resolver()));
    }

    [Fact]
    public void ADenyListBackupNamesNoServers()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(Fixture.ClaudeCodeSettings.Text(), @".claude\settings.json");
        harness.Discover();
        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);

        var backup = Assert.Single(harness.Backups.List(ClientId.ClaudeCode));
        Assert.EndsWith("settings.json", backup.OriginalPath, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Backups.ServerNames(backup, harness.Resolver()));
    }

    // MARK: - Splice only what the draft describes (D3)

    [Fact]
    public void ASaveWithNoChangesWritesNoBytesAndKeepsKeysKyttoDoesNotModel()
    {
        using var harness = new ToggleHarness();
        const string original = """
            {
              // Cursor's own file, with keys Kytto has never modelled.
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "cwd": "C:\\work",
                  "envFile": ".env",
                  "headers": { "Authorization": "Bearer keep-me" },
                  "env": { "TOKEN": "t", "RETRIES": 5 }
                }
              }
            }
            """;
        harness.Write(original, CursorPath);

        var result = Save(harness, "github");
        Assert.Empty(result.Changed);
        Assert.Equal(original, harness.Text(CursorPath));

        Save(harness, "github", draft => draft with { Command = "bunx" });
        var github = JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "github")!;
        Assert.Equal("bunx", github["command"]?.StringValue);
        Assert.Equal("C:\\work", github["cwd"]?.StringValue);
        Assert.Equal(".env", github["envFile"]?.StringValue);
        Assert.Equal("Bearer keep-me", github.ValueAt("headers", "Authorization")?.StringValue);
        // A value that is not a string is kept, not turned into "".
        Assert.Equal(5, github.ValueAt("env", "RETRIES")?.NumberValue);
        Assert.Contains("// Cursor's own file", harness.Text(CursorPath), StringComparison.Ordinal);
        // Only the command changed.
        Assert.Equal(original.Replace("\"npx\"", "\"bunx\"", StringComparison.Ordinal), harness.Text(CursorPath));
    }

    [Fact]
    public void EnvironmentChangesAreKeyByKey()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), CursorPath);

        Save(harness, "github", draft => draft with
        {
            Env = [new EnvEntry("GITHUB_PERSONAL_ACCESS_TOKEN", null), new EnvEntry("GITHUB_HOST", "ghe.example")],
        });

        var env = JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "github", "env")!;
        Assert.Equal(["GITHUB_PERSONAL_ACCESS_TOKEN", "GITHUB_HOST"], env.Keys);
        Assert.Equal("ghp_exampletokenvalue", env["GITHUB_PERSONAL_ACCESS_TOKEN"]?.StringValue);
    }

    [Fact]
    public void ACodexEditKeepsEveryKeyTheDraftDoesNotDescribe()
    {
        using var harness = new ToggleHarness();
        const string original = """
            [mcp_servers.github]
            command = "npx"
            args = ["-y", "@modelcontextprotocol/server-github"]
            startup_timeout_sec = 30 # generous
            enabled_tools = ["search"]
            bearer_token_env_var = "GH_TOKEN"
            enabled = false

            [mcp_servers.github.http_headers]
            X-Trace = "1"

            [mcp_servers.github.env]
            TOKEN = 't'
            """;
        harness.Write(original, CodexPath);

        var result = Save(harness, "github");
        Assert.Empty(result.Changed);
        Assert.Equal(original, harness.Text(CodexPath));

        Save(harness, "github", draft => draft with { Args = ["-y", "@modelcontextprotocol/server-github@1.2.3"] });
        var text = harness.Text(CodexPath);
        Assert.Equal(
            original.Replace(
                "\"@modelcontextprotocol/server-github\"]",
                "\"@modelcontextprotocol/server-github@1.2.3\"]",
                StringComparison.Ordinal),
            text);
        // Still switched off.
        Assert.False(TomlDocument.Parse(text).Table("mcp_servers", "github")?.Value("enabled")?.BoolValue);
    }

    [Fact]
    public void AnInlineEnvironmentSpelledDifferentlyIsNotAChange()
    {
        using var harness = new ToggleHarness();
        const string original = """
            [mcp_servers.github]
            command = "npx"
            env = {K='v',  L = "w"}
            """;
        harness.Write(original, CodexPath);

        Assert.Empty(Save(harness, "github").Changed);
        Assert.Equal(original, harness.Text(CodexPath));

        Save(harness, "github", draft => draft with { Env = [new EnvEntry("K", null), new EnvEntry("M", "x")] });
        Assert.Contains("env = { K = 'v', M = \"x\" }", harness.Text(CodexPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ADottedEnvironmentKeyInCodexIsRefusedRatherThanGuessed()
    {
        using var harness = new ToggleHarness();
        const string original = """
            [mcp_servers.github]
            command = "npx"
            env.TOKEN = "t"
            """;
        harness.Write(original, CodexPath);

        Assert.Throws<TomlEditException>(() => Save(harness, "github", draft => draft with { Command = "bunx" }));
        Assert.Equal(original, harness.Text(CodexPath));
    }

    [Fact]
    public void ARenameRewritesTheKeyAndNothingElse()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), CursorPath);
        const string codex = """
            [mcp_servers.figma]
            url = "https://mcp.figma.com/sse"
            startup_timeout_sec = 30

            [mcp_servers.figma.http_headers]
            X-Trace = "1"
            """;
        harness.Write(codex, CodexPath);

        Save(harness, "figma", draft => draft with { Name = "figma-design" });

        Assert.Equal(
            Fixture.CursorMcp.Text().Replace("\"figma\":", "\"figma-design\":", StringComparison.Ordinal),
            harness.Text(CursorPath));
        Assert.Equal(
            codex.Replace("[mcp_servers.figma", "[mcp_servers.figma-design", StringComparison.Ordinal),
            harness.Text(CodexPath));
    }

    // MARK: - Codex tables that do not sit together (D5)

    private const string Interleaved = """
        [mcp_servers.a]
        command = "a"

        [mcp_servers.b]
        command = "b"

        [mcp_servers.a.env]
        K = "v"
        """;

    [Fact]
    public void DeletingAServerLeavesATableBetweenItsOwnAlone()
    {
        using var harness = new ToggleHarness();
        harness.Write(Interleaved, CodexPath);

        harness.Authoring().Delete(harness.Server("a"));

        var document = TomlDocument.Parse(harness.Text(CodexPath));
        Assert.Equal(["b"], document.ServerNames("mcp_servers"));
        Assert.Equal("b", document.Table("mcp_servers", "b")?.Value("command")?.StringValue);
        Assert.Null(document.Table("mcp_servers", "a", "env"));
    }

    [Fact]
    public void EditingAServerLeavesATableBetweenItsOwnAlone()
    {
        using var harness = new ToggleHarness();
        harness.Write(Interleaved, CodexPath);

        Save(harness, "a", draft => draft with { Command = "a2" });

        Assert.Equal(Interleaved.Replace("command = \"a\"", "command = \"a2\"", StringComparison.Ordinal),
            harness.Text(CodexPath));

        // The wholesale replacement path takes the same care.
        var replaced = TomlDocument.Parse(Interleaved).SettingServer(
            "a", "mcp_servers", "[mcp_servers.a]\ncommand = \"z\"");
        Assert.Equal("b", TomlDocument.Parse(replaced).Table("mcp_servers", "b")?.Value("command")?.StringValue);
        Assert.Null(TomlDocument.Parse(replaced).Table("mcp_servers", "a", "env"));
    }

    // MARK: - The file's own spelling (D7)

    [Fact]
    public void EveryWriteUsesTheSpellingEachFileHas()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text().Replace("\"github\":", "\"GitHub\":", StringComparison.Ordinal), CursorPath);
        harness.Write("""
            {
              "mcpServers": {
                "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] }
              }
            }
            """, ".claude.json");

        // One row, two spellings.
        var server = harness.Server(harness.Discover().Servers.Single(row => row.Id == "github").Name);

        // Switching off in Cursor removes Cursor's `GitHub`.
        harness.Toggles.SetEnabled(false, server, ClientId.Cursor);
        Assert.Null(JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "GitHub"));

        // And switching on puts it back under that spelling, not the row's.
        harness.Toggles.SetEnabled(true, harness.Discover().Servers.Single(row => row.Id == "github"), ClientId.Cursor);
        Assert.Equal(["figma", "GitHub"], JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers")?.Keys);

        // An edit changes both copies and adds no twin.
        var row = harness.Discover().Servers.Single(candidate => candidate.Id == "github");
        harness.Authoring().Update(
            ServerDraft.Editing(row).MergingSecrets(row) with { Command = "bunx" },
            row.Name, row, harness.Discover().Servers);
        Assert.Equal(["figma", "GitHub"], JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers")?.Keys);
        Assert.Equal(["github"], JsonDocument.Parse(harness.Text(".claude.json")).ValueAt("mcpServers")?.Keys);
        Assert.Equal("bunx", JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers", "GitHub", "command")?.StringValue);

        // Delete leaves no copy behind.
        harness.Authoring().Delete(harness.Discover().Servers.Single(candidate => candidate.Id == "github"));
        Assert.Equal(["figma"], JsonDocument.Parse(harness.Text(CursorPath)).ValueAt("mcpServers")?.Keys);
        Assert.Empty(JsonDocument.Parse(harness.Text(".claude.json")).ValueAt("mcpServers")?.Keys ?? []);
    }

    // MARK: - Duplicate keys (D11)

    [Fact]
    public void AKeyWrittenTwiceIsRefusedRatherThanEditedInTheCopyNobodyReads()
    {
        using var harness = new ToggleHarness();
        const string original = """
            {
              "mcpServers": {
                "a": { "command": "old" },
                "a": { "command": "new" }
              }
            }
            """;
        harness.Write(original, CursorPath);
        var document = JsonDocument.Parse(original);

        var error = Assert.Throws<JsonEditException>(() =>
            document.SettingMember("a", ["mcpServers"], "{}"));
        Assert.Equal(JsonEditErrorKind.DuplicateKey, error.Kind);
        Assert.Throws<JsonEditException>(() => document.RemovingMember("a", ["mcpServers"]));
        Assert.Throws<JsonEditException>(() => document.ServerKey("a", "mcpServers"));
        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKeysAnsweringToOneIdentityAreRefused()
    {
        var document = JsonDocument.Parse("""{"mcpServers":{"GitHub":{},"github":{}}}""");
        Assert.Equal(
            JsonEditErrorKind.DuplicateKey,
            Assert.Throws<JsonEditException>(() => document.ServerKey("github", "mcpServers")).Kind);

        var toml = TomlDocument.Parse("""
            [mcp_servers.GitHub]
            command = "a"

            [mcp_servers.github]
            command = "b"
            """);
        Assert.Equal(
            TomlEditErrorKind.AmbiguousValue,
            Assert.Throws<TomlEditException>(() => toml.ServerName("github", "mcp_servers")).Kind);
    }

    // MARK: - Line endings (D10)

    [Fact]
    public void JsonEditsIntoACrlfFileInsertCrlf()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text().ReplaceLineEndings("\r\n"), CursorPath);

        Save(harness, "figma", draft => draft with { Env = [new EnvEntry("A", "1"), new EnvEntry("B", "2")] });
        Save(harness, "github", draft => draft with { Args = ["-y", "a-very-long-package-name-that-will-not-fit-on-one-line@1.0.0", "--flag"] });

        var text = harness.Text(CursorPath);
        Assert.DoesNotMatch("(?<!\r)\n", text);
    }
}
