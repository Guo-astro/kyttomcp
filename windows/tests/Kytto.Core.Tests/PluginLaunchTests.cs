using System.Diagnostics;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// A plugin server is started the way its client would start it: in its own directory,
/// with its own variables, and with every argument arriving exactly as written — which
/// on Windows means through the PATH + PATHEXT resolution and, for a batch shim,
/// through <c>cmd.exe</c>'s quoting (§4, §6).
/// </summary>
public class PluginLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kytto-plugin-launch", Guid.NewGuid().ToString("N"));

    public PluginLaunchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string? Python =>
        Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory =>
            {
                try { return Path.Combine(directory.Trim('"'), "python.exe"); }
                catch (ArgumentException) { return null; }
            })
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

    /// <summary>
    /// A stand-in MCP server that fails — loudly, before answering — unless it was
    /// started in the directory, with the variable and the argv the test expects.
    /// </summary>
    private const string ServerScript = """
        import json, os, sys
        here = os.path.dirname(os.path.abspath(__file__))
        expected = json.load(open(os.path.join(here, 'expected.json'), encoding='utf-8'))
        assert os.path.realpath(os.getcwd()) == os.path.realpath(expected['cwd']), 'cwd: ' + os.getcwd()
        assert os.environ.get('CLAUDE_PLUGIN_ROOT') == expected['root'], 'root: ' + str(os.environ.get('CLAUDE_PLUGIN_ROOT'))
        assert os.environ.get('EXPECTED_ROOT') == expected['root'], 'expanded env: ' + str(os.environ.get('EXPECTED_ROOT'))
        assert sys.argv[1:] == expected['argv'], 'argv: ' + repr(sys.argv[1:])
        for line in sys.stdin:
            request = json.loads(line)
            if request.get('method') == 'initialize':
                result = {'protocolVersion': '2026-07-28', 'capabilities': {}, 'serverInfo': {'name': 'context-ok', 'version': '1'}}
            elif request.get('method') == 'tools/list':
                result = {'tools': []}
            else:
                continue
            print(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}), flush=True)
        """;

    private string Plugin(string name = "plugin with spaces") => Path.Combine(_directory, name);

    private void Install(string root, IReadOnlyList<string> expectedArgv, string? cwd = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "server.py"), ServerScript);
        File.WriteAllText(
            Path.Combine(root, "expected.json"),
            JsonSerializer.Serialize(new { cwd = cwd ?? root, root, argv = expectedArgv }));
    }

    private static Server PluginServer(
        string root, string command, IReadOnlyList<string> args, string? workingDirectory)
    {
        var definition = JsonSerializer.Serialize(new
        {
            command,
            args,
            env = new Dictionary<string, string> { ["EXPECTED_ROOT"] = "${CLAUDE_PLUGIN_ROOT}" },
        });
        var server = ServerFields.FromJson(Kytto.Core.Json.JsonDocument.Parse(definition).Root).MakeServer(
            "plugin:demo:tool",
            new Origin.Plugin(ClientId.ClaudeCode, "demo@example", "Demo", "1"),
            isBundled: true,
            sourceText: definition);
        server.WorkingDirectory = workingDirectory;
        server.Placeholders["CLAUDE_PLUGIN_ROOT"] = root;
        server.LaunchEnvironment["CLAUDE_PLUGIN_ROOT"] = root;
        return server;
    }

    private static HealthResult Check(Server server) =>
        new HealthChecker(timeout: TimeSpan.FromSeconds(15)).Check(server).Health;

    private static string Describe(HealthResult health) =>
        health.CurrentMessage + " | " + health.Stderr;

    // MARK: - A program, started directly

    [Fact]
    public void AnExecutableStartsInThePluginDirectoryWithItsExpandedEnvironmentAndLiteralArguments()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin();
        string[] argv =
        [
            "plain",
            "literal $(touch should-not-exist); value",
            "say \"hi\" & more",
            "%PATH% ^ | < > ( )",
            "",
            @"C:\dir with space\",
            "héllo wörld",
        ];
        Install(root, argv);
        var server = PluginServer(root, Python!, ["server.py", .. argv], root);

        var health = Check(server);

        Assert.True(health.Status == HealthStatus.Passed, Describe(health));
        Assert.Equal("context-ok", health.ServerName);
        Assert.False(File.Exists(Path.Combine(root, "should-not-exist")));
    }

    [Fact]
    public void ARelativeCommandResolvesInThePluginDirectoryOnlyWhenOneWasEstablished()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin();
        Install(root, []);
        File.WriteAllText(Path.Combine(root, "bin", "run.cmd"), "@echo off\r\n\"" + Python + "\" \"%~dp0..\\server.py\" %*\r\n");

        var withDirectory = Check(PluginServer(root, "./bin/run", [], root));
        Assert.True(withDirectory.Status == HealthStatus.Passed, Describe(withDirectory));

        // Without one, resolving it from Kytto's own directory would answer a different
        // question than the client's does.
        var without = Check(PluginServer(root, "./bin/run", [], workingDirectory: null));
        Assert.Equal(HealthStatus.Unsupported, without.Status);
        Assert.Equal("relativeCommand", without.Failure?.Reason);
    }

    [Fact]
    public void ACommandThroughAPluginRootPlaceholderIsNotMistakenForARelativeOne()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin();
        Install(root, []);
        File.WriteAllText(Path.Combine(root, "bin", "tool.cmd"), "@echo off\r\n\"" + Python + "\" \"%~dp0..\\server.py\" %*\r\n");
        var server = PluginServer(root, "${CLAUDE_PLUGIN_ROOT}/bin/tool", [], workingDirectory: null);

        Assert.False(server.HasRelativeCommand);
        Assert.False(server.HasRelativePath);
        // No directory was established, but the command is absolute once its root is
        // known, so there is nothing to resolve: the shim runs, and the server's own
        // cwd assertion is what notices the difference.
        var health = Check(server);
        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("cwd", health.Stderr, StringComparison.Ordinal);
    }

    // MARK: - A batch shim, through cmd.exe

    [Fact]
    public void ABatchShimInADirectoryFullOfCmdMetacharactersReceivesEveryArgumentLiterally()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin("plugin (x86) & co ^ 1");
        string[] argv =
        [
            "plain",
            "two words",
            "x&y",
            "p|q",
            "<>",
            "a^b",
            "(paren)",
            "semi;colon,comma=eq",
            @"trailing backslash\",
            @"C:\dir with space\",
            "literal $(touch should-not-exist); value",
            "''single'",
            @"back\\slash",
            "--flag=value",
            "@scope/pkg",
            "héllo wörld",
            "",
        ];
        Install(root, argv);
        File.WriteAllText(
            Path.Combine(root, "bin", "tool.cmd"),
            "@echo off\r\n\"" + Python + "\" \"%~dp0..\\server.py\" %*\r\n");
        // Written without an extension, as a plugin that ships `tool.cmd` writes it, and
        // through the root placeholder, as every plugin command is.
        var server = PluginServer(root, "${CLAUDE_PLUGIN_ROOT}/bin/tool", argv, root);

        var health = Check(server);

        Assert.True(health.Status == HealthStatus.Passed, Describe(health));
        Assert.Contains("tool.cmd", health.ResolvedCommand ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "should-not-exist")));
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("%PATH%")]
    [InlineData("100%")]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    public void AnArgumentABatchFileCannotReceiveUnchangedIsRefusedWithoutStartingAnything(string argument)
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin();
        Install(root, [argument]);
        File.WriteAllText(
            Path.Combine(root, "bin", "tool.cmd"),
            "@echo off\r\necho started> \"%~dp0..\\started.txt\"\r\n\"" + Python + "\" \"%~dp0..\\server.py\" %*\r\n");
        var server = PluginServer(root, @"${CLAUDE_PLUGIN_ROOT}\bin\tool.cmd", [argument], root);

        var health = Check(server);

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Equal("unsafeArgument", health.Failure?.Reason);
        Assert.False(File.Exists(Path.Combine(root, "started.txt")), "the shim ran with a different argument");
    }

    [Fact]
    public void ABatchLineIsOneQuotedPayloadSoCmdStripsOnlyTheOuterPair()
    {
        Assert.Equal(
            "/d /s /c \"\"C:\\Program Files\\nodejs\\npx.cmd\" -y @scope/pkg \"two words\"\"",
            ManagedExecutable.BatchCommandLine(
                @"C:\Program Files\nodejs\npx.cmd", ["-y", "@scope/pkg", "two words"]));
        Assert.Equal(
            "/d /s /c \"\"C:\\x\\a (1) & b.cmd\"\"",
            ManagedExecutable.BatchCommandLine(@"C:\x\a (1) & b.cmd", []));
        // Empty, delimiter-bearing and backslash-terminated arguments.
        Assert.Equal(
            "/d /s /c \"\"C:\\t.cmd\" \"\" \"a=b\" \"d e\\\\\" \"x&y\" C:\\plain\\path\"",
            ManagedExecutable.BatchCommandLine(@"C:\t.cmd", ["", "a=b", @"d e\", "x&y", @"C:\plain\path"]));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("%TEMP%")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void AnUnquotableArgumentIsAnErrorNotASilentChange(string argument)
    {
        var error = Assert.Throws<ProcessException>(() =>
            ManagedExecutable.BatchCommandLine(@"C:\t.cmd", [argument]));
        Assert.Equal("unsafeArgument", error.Failure.Reason);
    }

    [Fact]
    public void AnExecutableKeepsTheArgumentListAndOnlyABatchFileGetsARawCommandLine()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = _directory,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        };
        File.WriteAllText(Path.Combine(_directory, "shim.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(_directory, "tool.exe"), "");

        var batch = ManagedExecutable.StartInfo("shim", ["a b", "c"], environment);
        Assert.Empty(batch.ArgumentList);
        Assert.StartsWith("/d /s /c \"", batch.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"a b\" c", batch.Arguments, StringComparison.Ordinal);

        var program = ManagedExecutable.StartInfo("tool", ["a b", "c"], environment);
        Assert.Equal(Path.Combine(_directory, "tool.exe"), program.FileName, ignoreCase: true);
        Assert.Equal(["a b", "c"], program.ArgumentList);
        Assert.Equal("", program.Arguments);
    }

    // MARK: - Resolution

    [Fact]
    public void APathWithNoExtensionIsResolvedThroughPathextLikeACommandOnPathIs()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        };
        Directory.CreateDirectory(Path.Combine(_directory, "bin"));
        File.WriteAllText(Path.Combine(_directory, "bin", "tool.cmd"), "@echo off\r\n");

        // PATHEXT spells its extensions in capitals; the file system does not mind.
        var expected = Path.Combine(_directory, "bin", "tool.cmd");
        var absolute = ManagedExecutable.Resolve(Path.Combine(_directory, "bin", "tool"), environment);
        Assert.Equal(expected, absolute.Path, ignoreCase: true);
        Assert.True(absolute.IsBatch);

        var relative = ManagedExecutable.Resolve("./bin/tool", environment, _directory);
        Assert.Equal(expected, relative.Path, ignoreCase: true);

        var missing = Assert.Throws<ProcessException>(() =>
            ManagedExecutable.Resolve("./bin/absent", environment, _directory));
        Assert.Equal("executableMissing", missing.Failure.Reason);
    }

    // MARK: - A directory that is not there is not replaced by another

    [Fact]
    public void AMissingWorkingDirectoryNeverRunsTheServerSomewhereElse()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        var root = Plugin();
        Install(root, []);
        var marker = Path.Combine(root, "ran.txt");
        File.WriteAllText(
            Path.Combine(root, "marker.py"),
            "open(r'" + marker + "', 'w').write('ran')\n");
        var definition = JsonSerializer.Serialize(new { command = Python, args = new[] { Path.Combine(root, "marker.py") } });
        var server = ServerFields.FromJson(Kytto.Core.Json.JsonDocument.Parse(definition).Root).MakeServer(
            "t", new Origin.Plugin(ClientId.Codex, "x@y", "x", "1"), isBundled: true, sourceText: definition);
        server.WorkingDirectory = Path.Combine(root, "missing");

        var health = Check(server);

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Equal("invalidWorkingDirectory", health.Failure?.Reason);
        Assert.DoesNotContain(root, health.CurrentMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(marker));

        // A relative directory is not an established one either.
        server.WorkingDirectory = "relative";
        Assert.Equal("invalidWorkingDirectory", Check(server).Failure?.Reason);
    }

    // MARK: - What counts as relative

    [Theory]
    [InlineData(@"\\server\share\tool.exe", false)]
    [InlineData(@"C:\Program Files\tool.exe", false)]
    [InlineData("c:/tools/tool.exe", false)]
    [InlineData(@".\tool.exe", true)]
    [InlineData("./tool", true)]
    [InlineData(@"..\tool.exe", true)]
    [InlineData(@"bin\tool.exe", true)]
    [InlineData("node", false)]
    [InlineData("${UNKNOWN}/tool", true)]
    [InlineData("${CLAUDE_PLUGIN_ROOT}/bin/tool", false)]
    public void RelativenessIsJudgedOnTheCommandThatWouldRun(string command, bool relative)
    {
        var server = new Server
        {
            Id = "t", Name = "t", Transport = Transport.Stdio, Command = command, Args = [],
            Env = [], Url = null, EnabledIn = [], Origin = new Origin.ConfigFile(ClientId.Cursor),
            Fingerprint = "0", IsBundled = false, DefinitionSource = "{}",
            Placeholders = { ["CLAUDE_PLUGIN_ROOT"] = @"C:\plugin" },
        };

        Assert.Equal(relative, server.HasRelativeCommand);
        Assert.Equal(relative, server.HasRelativePath);
    }
}
