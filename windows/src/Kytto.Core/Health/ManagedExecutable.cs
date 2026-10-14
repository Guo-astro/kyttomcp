using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Kytto.Core.Health;

/// <param name="IsBatch">
/// The target is a <c>.cmd</c> or <c>.bat</c> file, which Windows cannot start
/// directly: <see cref="FileName"/> is then the command interpreter and the
/// arguments have to be written for it (<see cref="ManagedExecutable.Apply"/>).
/// </param>
public sealed record ResolvedExecutable(
    string Path,
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    bool IsBatch = false);

/// <summary>
/// Resolves a command against the environment without asking a shell to
/// reinterpret the real server arguments. Batch shims use cmd.exe explicitly.
/// </summary>
/// <remarks>
/// <para>
/// <c>npx</c> is <c>npx.cmd</c>, so almost every server goes through the batch path.
/// A batch file cannot be started directly, and handing it to <c>cmd /c</c> is where
/// arguments stop being literal: cmd re-parses the whole line, so a space, an
/// ampersand or a quote in an argument means something to it that it does not to the
/// server. <see cref="BatchCommandLine"/> quotes what is safe to quote and refuses
/// what cannot be passed through exactly, instead of starting the server with an
/// argument it never asked for (§6).
/// </para>
/// </remarks>
public static class ManagedExecutable
{
    /// <param name="workingDirectory">
    /// The directory a relative command is resolved against, when the definition's
    /// owner established one. Without it a relative command is resolved against this
    /// process's own directory, which is only right for the gateway — it is started by
    /// the client, from the client's directory.
    /// </param>
    public static ResolvedExecutable Resolve(
        string command,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory = null)
    {
        var path = Find(command, environment, workingDirectory);
        if (!IsScript(path)) return new ResolvedExecutable(path, path, []);

        return new ResolvedExecutable(
            path,
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ["/d", "/s", "/c"],
            IsBatch: true);
    }

    public static ProcessStartInfo StartInfo(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory = null)
    {
        var resolved = Resolve(command, environment, workingDirectory);
        var start = new ProcessStartInfo
        {
            FileName = resolved.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Apply(start, resolved, arguments);
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        return start;
    }

    /// <summary>Puts the arguments on <paramref name="start"/> in the form its target needs.</summary>
    /// <exception cref="ProcessException">
    /// An argument cannot reach a batch file unchanged.
    /// </exception>
    public static void Apply(
        ProcessStartInfo start,
        ResolvedExecutable resolved,
        IReadOnlyList<string> arguments)
    {
        if (resolved.IsBatch)
        {
            // One raw command line, because cmd's own quoting rules apply to it and
            // .NET's argument list quotes for a different parser.
            start.Arguments = BatchCommandLine(resolved.Path, arguments);
            return;
        }
        foreach (var argument in resolved.PrefixArguments) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    /// <summary>
    /// The command line that runs <paramref name="script"/> with <paramref name="arguments"/>
    /// through <c>cmd.exe</c>, each argument arriving at the batch file as one literal
    /// argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole payload sits inside one extra pair of quotes with <c>/s</c>, which is
    /// what makes cmd strip exactly those and nothing else. Without it a command line
    /// that starts with a quote and holds more than two is "fixed" by cmd's legacy rule,
    /// and <c>C:\Program Files\nodejs\npx.cmd</c> plus one argument with a space stops
    /// working — the case that matters most, because that is where Node lives.
    /// </para>
    /// <para>
    /// An argument is quoted whenever it holds anything cmd treats specially, and inside
    /// quotes <c>&amp; | &lt; &gt; ^ ( )</c> are literal. What quoting cannot protect is
    /// refused: a double quote ends the quoted span, <c>%</c> is expanded by cmd even
    /// inside one, and a line break ends the command. There is no escape for those that
    /// holds for every batch file, so a definition using them is not started rather
    /// than started with a different argument.
    /// </para>
    /// </remarks>
    public static string BatchCommandLine(string script, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { QuoteForBatch(script, alwaysQuote: true) };
        foreach (var argument in arguments) parts.Add(QuoteForBatch(argument, alwaysQuote: false));
        return "/d /s /c \"" + string.Join(' ', parts) + "\"";
    }

    private static readonly Regex PlainArgument = new(
        @"^[\p{L}\p{N}_\-+.:/\\@#~$]+$", RegexOptions.CultureInvariant);

    private static string QuoteForBatch(string value, bool alwaysQuote)
    {
        if (value.IndexOfAny(['"', '%', '\r', '\n', '\0']) >= 0)
        {
            throw ProcessException.UnsafeArgument();
        }
        if (!alwaysQuote && value.Length > 0 && PlainArgument.IsMatch(value)) return value;

        // Backslashes directly before the closing quote would escape it for the
        // program that parses the line afterwards, so they are doubled.
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    private static string Find(
        string command,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory)
    {
        var extensions = (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        if (command.Contains(System.IO.Path.DirectorySeparatorChar) ||
            command.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            var full = workingDirectory is not null && !System.IO.Path.IsPathRooted(command)
                ? System.IO.Path.GetFullPath(command, workingDirectory)
                : System.IO.Path.GetFullPath(command);

            // A path that names no extension is what a plugin writes for a program it
            // ships as `tool.cmd` or `tool.exe`; PATH lookup already finds those that
            // way, and a path deserves the same courtesy.
            if (!System.IO.Path.HasExtension(full))
            {
                foreach (var extension in extensions)
                {
                    if (File.Exists(full + extension)) return full + extension;
                }
            }
            if (File.Exists(full)) return full;
            throw ProcessException.ExecutableMissing(command, full);
        }

        foreach (var directory in PathOf(environment)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim('"');
            if (trimmed.Length == 0) continue;

            if (System.IO.Path.HasExtension(command))
            {
                var exact = SafeCombine(trimmed, command);
                if (exact is not null && File.Exists(exact)) return exact;
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = SafeCombine(trimmed, command + extension);
                if (candidate is not null && File.Exists(candidate)) return candidate;
            }
        }
        throw ProcessException.CommandNotFound(command, PathOf(environment));
    }

    private static string? SafeCombine(string directory, string name)
    {
        try { return System.IO.Path.Combine(directory, name); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsScript(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(IReadOnlyDictionary<string, string> environment) =>
        environment.GetValueOrDefault("PATH")
        ?? environment.GetValueOrDefault("Path")
        ?? "";
}
