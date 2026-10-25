using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Library;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>TOML strictness Codex shares: what Kytto refuses to read, it can never write.</summary>
public class TomlStrictnessTests
{
    [Theory]
    [InlineData("[mcp_servers.a]\ncommand = \"x\"\n[mcp_servers.a]\nargs = []\n")]
    [InlineData("[mcp_servers]\nfoo.command = \"x\"\n[mcp_servers.foo]\nargs = []\n")]
    [InlineData("[a]\nk = 1\nk = 2\n")]
    public void ATableOrKeyDefinedTwiceIsRefused(string text) =>
        Assert.Throws<TomlParseException>(() => TomlDocument.Parse(text));

    [Theory]
    [InlineData("[[profiles]]\nname = \"a\"\n[[profiles]]\nname = \"b\"\n")]
    [InlineData("[mcp_servers.a.env]\nK = \"v\"\n[mcp_servers.a]\ncommand = \"x\"\n")]
    [InlineData("[mcp_servers.a]\ncommand = \"x\"\n[mcp_servers.b]\ncommand = \"y\"\n[mcp_servers.a.env]\nK = \"v\"\n")]
    [InlineData("[a]\nb.c = 1\nb.d = 2\n")]
    public void LegalRepetitionStillParses(string text) => TomlDocument.Parse(text);

    [Fact]
    public void AMultilineStringMayEndInQuotesBeforeItsDelimiter()
    {
        var document = TomlDocument.Parse("note = \"\"\"say \"hi\"\"\"\"\nnext = 1\n");

        Assert.Equal("say \"hi\"", document.Tables[0].Value("note")?.StringValue);
        Assert.NotNull(document.Tables[0].Value("next"));
    }

    [Fact]
    public void DeleteIsEscapedWhenAStringIsWritten() =>
        Assert.Equal("\"a\\u007Fb\"", TomlText.String("a\u007Fb"));
}

/// <summary>JSONC commas and the parser's recursion bound.</summary>
public class JsonCommaAndDepthTests
{
    [Fact]
    public void RemovingTheLastMemberAfterACommentedLineLeavesNoTrailingComma()
    {
        const string text = "{\n  \"a\": {}, // primary\n  \"b\": {}\n}";

        var result = JsonDocument.Parse(text).RemovingMember("b", []);

        Assert.DoesNotContain("{}, // primary", result, StringComparison.Ordinal);
        Assert.Equal(["a"], JsonDocument.Parse(result).Root.Keys);
        // A strict reader must still read the file once comments are stripped.
        var stripped = string.Join("\n", result.Split('\n').Select(line => line.Split("//")[0]));
        System.Text.Json.JsonDocument.Parse(stripped).Dispose();
    }

    [Fact]
    public void RemovingAMemberWhoseCommaFollowsABlockComment()
    {
        var result = JsonDocument.Parse("""{ "a": 1 /* x */, "b": 2 }""").RemovingMember("a", []);

        Assert.Equal(["b"], JsonDocument.Parse(result).Root.Keys);
        Assert.DoesNotContain("{ ,", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running out of stack ends a .NET process with no handler, so a server reply
    /// nested thousands deep has to be a parse error.
    /// </summary>
    [Fact]
    public void NestingBeyondTheBoundIsAParseErrorNotACrash()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);
        var error = Assert.Throws<JsonParseException>(() => JsonDocument.Parse(deep));
        Assert.Equal(JsonParseErrorKind.NestingTooDeep, error.Kind);

        JsonDocument.Parse(new string('[', 128) + new string(']', 128));
    }
}

/// <summary>Backup retention counts each original file on its own.</summary>
public class BackupRetentionPerFileTests
{
    [Fact]
    public void OneFilesBackupsCannotPushOutAnothersFromTheSameClient()
    {
        using var harness = new ToggleHarness();
        harness.Write("{}", ".claude.json");
        harness.Write("{}", @".claude\settings.json");
        var main = Path.Combine(harness.Home, ".claude.json");
        var deny = Path.Combine(harness.Home, ".claude", "settings.json");
        var store = new BackupStore(
            new Kytto.Core.Settings.KyttoPaths(Path.Combine(harness.Home, "retention")),
            retentionPerClient: 2);

        store.BackUp(main, ClientId.ClaudeCode, "main");
        for (var round = 0; round < 5; round++) store.BackUp(deny, ClientId.ClaudeCode, "deny");

        var kept = store.List(ClientId.ClaudeCode);
        Assert.Single(kept, backup => Path.GetFullPath(backup.OriginalPath) == Path.GetFullPath(main));
        Assert.Equal(2, kept.Count(backup => Path.GetFullPath(backup.OriginalPath) == Path.GetFullPath(deny)));
    }
}

/// <summary>Copies a client cannot carry, and the deny list's spelling.</summary>
public class TogglePortabilityTests
{
    [Fact]
    public void LaunchAndAuthSettingsThatRenderingWouldDropAreNamed()
    {
        const string toml = """
            [mcp_servers.app]
            command = "node"
            args = ["dist/index.js"]
            cwd = "C:\\Users\\example\\app"

            [mcp_servers.app.http_headers]
            X-Key = "value"
            """;
        Assert.Equal(["http_headers", "cwd"], ToggleService.UnportableFields(toml, ConfigFormat.Toml));
        Assert.Equal(
            ["bearer_token_env_var"],
            ToggleService.UnportableFields(
                """remote = { url = "https://x", bearer_token_env_var = "TOKEN" }""", ConfigFormat.Toml));
        Assert.Equal(
            ["headers"],
            ToggleService.UnportableFields(
                """{"type":"http","url":"https://x","headers":{"Authorization":"Bearer t"}}""", ConfigFormat.Json));
        Assert.Empty(ToggleService.UnportableFields("""{"command":"npx","args":["-y","pkg"],"env":{"A":"b"}}""", ConfigFormat.Json));
        Assert.Empty(ToggleService.UnportableFields("[mcp_servers.cwd]\ncommand = \"x\"", ConfigFormat.Toml));
    }

    [Fact]
    public void ACodexServerWithAWorkingDirectoryIsNotCopiedIntoAJsonClient()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            [mcp_servers.app]
            command = "node"
            args = ["index.js"]
            cwd = "C:\\work\\app"
            """, @".codex\config.toml");
        harness.Write("""{"mcpServers": {}}""", @".cursor\mcp.json");
        var before = harness.Text(@".cursor\mcp.json");

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, harness.Server("app"), ClientId.Cursor));

        Assert.Contains("cwd", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void AJsonServerWithHeadersIsNotCopiedIntoCodex()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            """{"mcpServers": {"remote": {"type": "http", "url": "https://mcp.example/mcp", "headers": {"Authorization": "Bearer t"}}}}""",
            @".cursor\mcp.json");
        harness.Write("", @".codex\config.toml");

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, harness.Server("remote"), ClientId.Codex));

        Assert.Contains("headers", error.Message, StringComparison.Ordinal);
        Assert.Equal("", harness.Text(@".codex\config.toml"));
    }

    [Fact]
    public void AGatewayDefinitionIsNeverCopiedToAnotherClient()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            """{"mcpServers": {"routed": {"command": "C:\\Kytto\\kytto-mcp-proxy.exe", "args": ["--route", "3f6c1d0e-8b8a-4c7e-9a51-1d2f3a4b5c6d"]}}}""",
            @".cursor\mcp.json");
        harness.Write("{}", ".claude.json");

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, harness.Server("routed"), ClientId.ClaudeCode));

        Assert.Contains("Gateway", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row takes its name from whichever client listed it first; Claude Code
    /// matches its deny list against its own key.
    /// </summary>
    [Fact]
    public void AClaudeCodeDenyEntryUsesTheSpellingOfItsOwnConfig()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            """{"mcpServers": {"GitHub": {"command": "npx"}}}""",
            $@"{ToggleHarness.ClaudeSupport}\claude_desktop_config.json");
        harness.Write("""{"mcpServers": {"github": {"command": "npx"}}}""", ".claude.json");
        harness.Write("{}", @".claude\settings.json");
        var server = harness.Server("GitHub");
        Assert.Equal("GitHub", server.Name);

        harness.Toggles.SetEnabled(false, server, ClientId.ClaudeCode);

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        Assert.Equal(
            ["github"],
            settings.Root["deniedMcpServers"]?.Elements?.Select(entry => entry["serverName"]?.StringValue));
    }
}

/// <summary>Entry points the library suggests have to start from any directory.</summary>
public class LibraryEntryPointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kytto-library-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SuggestedEntryPointsAreAbsolute()
    {
        Directory.CreateDirectory(Path.Combine(_root, "py"));
        Directory.CreateDirectory(Path.Combine(_root, "node", "dist"));
        File.WriteAllText(Path.Combine(_root, "py", "server.py"), "print('x')\n");
        File.WriteAllText(Path.Combine(_root, "node", "package.json"), """{"name":"pkg","main":"dist/index.js"}""");
        File.WriteAllText(Path.Combine(_root, "package.json"), """{"name":"root-pkg","main":"index.js"}""");

        var result = new McpLibraryScanner().Scan(_root);

        var args = result.Candidates.SelectMany(candidate => candidate.NativeServer?.Args ?? []).ToArray();
        Assert.Contains(Path.Combine(_root, "py", "server.py"), args);
        Assert.Contains(Path.Combine(_root, "node", "dist", "index.js"), args);
        Assert.Contains(Path.Combine(_root, "index.js"), args);
        Assert.All(args, arg => Assert.True(Path.IsPathFullyQualified(arg), arg));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
