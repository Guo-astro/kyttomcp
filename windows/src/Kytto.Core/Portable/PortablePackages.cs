using System.Text.RegularExpressions;

namespace Kytto.Core.Portable;

/// <summary>
/// Which one argument of a launcher command line may stay in a portable setup as
/// public text: a registry package identifier (§7.13).
/// </summary>
/// <remarks>
/// <para>
/// A generic argument may itself be a bearer token, even when no flag says so, so
/// only a flag from a short list or the package a known runner downloads is ever
/// literal. "The package a known runner downloads" has to be read the same way on
/// both platforms, or a setup exported by one is refused by the other.
/// </para>
/// <para>
/// That is why this is not <c>PackageLaunch</c>. That parser also reads
/// <c>cmd /c npx …</c> and refuses specs that end in <c>.js</c> or <c>.py</c>,
/// which is right for MCP Doctor and wrong here: <c>chart.js</c> is a registry
/// package, the macOS app exports it as a literal, and this app must import what it
/// exports. This is a straight port of the macOS parser's behaviour for the bare
/// launcher names the format allows.
/// </para>
/// </remarks>
internal static class PortablePackages
{
    private static readonly HashSet<string> FloatingTags =
        new(["latest", "next", "beta", "alpha", "canary"], StringComparer.Ordinal);

    private static readonly Regex VersionRange = new(
        @"\A[~^<>=!0-9.,* |]+\z", RegexOptions.CultureInvariant);

    private static readonly Regex PythonRequirement = new(
        @"\A([A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)(\[[A-Za-z0-9._,\s-]*\])?(.*)\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The index of the argument holding a verifiable registry package, or null.
    /// </summary>
    /// <param name="command">The launcher as it will be written — a literal, or null.</param>
    /// <param name="safeArguments">The flags that may precede the package.</param>
    public static int? SafePackageIndex(
        string? command,
        IReadOnlyList<string> args,
        IReadOnlySet<string> safeArguments)
    {
        if (Parse(command, args) is not { } launch ||
            System.Text.Encoding.UTF8.GetByteCount(launch.Argument) > 256 ||
            !args.Take(launch.ArgumentIndex).All(safeArguments.Contains))
        {
            return null;
        }

        // An unusual tag or prerelease stays a required input rather than risking a
        // secret hidden in a version tag.
        if (launch.RequestedVersion is { } version &&
            !FloatingTags.Contains(version) &&
            !VersionRange.IsMatch(version))
        {
            return null;
        }
        return launch.ArgumentIndex;
    }

    private sealed record Launch(int ArgumentIndex, string Argument, string? RequestedVersion);

    private static Launch? Parse(string? command, IReadOnlyList<string> args)
    {
        if (string.IsNullOrEmpty(command)) return null;
        if (Runner.Detect(command, args) is not { } runner) return null;

        var rest = args.Skip(runner.SubcommandCount).ToArray();
        if (runner.Scan(rest) is not { } target) return null;
        var index = target.Index + runner.SubcommandCount;

        if (runner.Python)
        {
            return PythonVersion(target.Spec, runner.AcceptsAtVersion && !target.FromFlag) is { } python
                ? new Launch(index, args[index], python.Version)
                : null;
        }
        return NpmVersion(target.Spec) is { } npm ? new Launch(index, args[index], npm.Version) : null;
    }

    // MARK: - Specs

    /// <summary><c>name</c>, <c>name@version</c>, <c>@scope/name</c>, <c>@scope/name@version</c>.</summary>
    private static (string Name, string? Version)? NpmVersion(string raw)
    {
        var spec = raw.Trim(' ', '\t');
        if (spec.Length == 0 ||
            spec.Contains(':') ||
            spec.StartsWith('.') || spec.StartsWith('/') || spec.StartsWith('~') ||
            spec.EndsWith(".tgz", StringComparison.Ordinal) ||
            spec.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return null;
        }

        int nameEnd;
        if (spec[0] == '@')
        {
            var at = spec.IndexOf('@', 1);
            nameEnd = at < 0 ? spec.Length : at;
            if (spec[..nameEnd].Count(character => character == '/') != 1) return null;
        }
        else
        {
            var at = spec.IndexOf('@');
            nameEnd = at < 0 ? spec.Length : at;
            // `user/repo` is GitHub shorthand to npm, not a registry name.
            if (spec[..nameEnd].Contains('/')) return null;
        }

        var name = spec[..nameEnd];
        if (name.Length > 214 ||
            name.EndsWith('/') ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || "-._~@/".Contains(character)) ||
            !name.Any(char.IsAsciiLetterOrDigit))
        {
            return null;
        }
        return (name, nameEnd < spec.Length ? spec[(nameEnd + 1)..] : null);
    }

    /// <summary>A PEP 508 requirement without markers or URL, or uv's <c>name@version</c>.</summary>
    private static (string Name, string? Version)? PythonVersion(string raw, bool allowingAt)
    {
        var spec = raw.Trim(' ', '\t');
        if (spec.Length == 0 ||
            spec.Contains("://", StringComparison.Ordinal) ||
            spec.Contains(';') ||
            spec.Contains(" @", StringComparison.Ordinal) ||
            spec.StartsWith("git+", StringComparison.Ordinal) ||
            spec.StartsWith('.') || spec.StartsWith('/') || spec.StartsWith('~') ||
            spec.EndsWith(".whl", StringComparison.Ordinal) ||
            spec.EndsWith(".tar.gz", StringComparison.Ordinal) ||
            spec.EndsWith(".zip", StringComparison.Ordinal))
        {
            return null;
        }

        var match = PythonRequirement.Match(spec);
        if (!match.Success) return null;

        var name = match.Groups[1].Value;
        var tail = match.Groups[3].Value.Trim(' ', '\t');
        if (tail.Length == 0) return (name, null);

        if (tail[0] == '@')
        {
            if (!allowingAt) return null;
            var version = tail[1..];
            return version.Length == 0 ? null : (name, version);
        }
        return "=<>!~".Contains(tail[0]) ? (name, tail) : null;
    }

    // MARK: - Runners

    private sealed record Runner(
        int SubcommandCount,
        bool Python,
        IReadOnlySet<string> PackageFlags,
        IReadOnlySet<string> ValueFlags,
        bool AcceptsAtVersion)
    {
        public readonly record struct Target(int Index, string Spec, bool FromFlag);

        public static Runner? Detect(string command, IReadOnlyList<string> args)
        {
            var name = command.ToLowerInvariant();
            foreach (var suffix in (string[])[".cmd", ".exe", ".ps1"])
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
            }
            var first = args.Count > 0 ? args[0] : null;
            var second = args.Count > 1 ? args[1] : null;

            return name switch
            {
                "npx" => Npm(0),
                "npm" when first is "exec" or "x" => Npm(1),
                "pnpm" when first == "dlx" => Npm(1),
                "yarn" when first == "dlx" => Npm(1),
                "bunx" => Npm(0),
                "bun" when first == "x" => Npm(1),
                "uvx" => Uv(0),
                "uv" when first == "tool" && second == "run" => Uv(2),
                "pipx" when first == "run" => new Runner(
                    1,
                    Python: true,
                    Set("--spec"),
                    Set("--python", "--index-url", "-i", "--pip-args", "--backend"),
                    AcceptsAtVersion: false),
                _ => null,
            };
        }

        private static Runner Npm(int subcommandCount) => new(
            subcommandCount,
            Python: false,
            Set("-p", "--package"),
            Set(
                "-c", "--call", "--registry", "--cache", "--userconfig", "--prefix",
                "-w", "--workspace", "--node-options", "--loglevel", "--shell",
                "--dir", "-C", "--filter"),
            AcceptsAtVersion: true);

        private static Runner Uv(int subcommandCount) => new(
            subcommandCount,
            Python: true,
            Set("--from"),
            Set(
                "--with", "-w", "--with-editable", "--with-requirements", "--python", "-p",
                "--index", "--index-url", "-i", "--extra-index-url", "--default-index",
                "--find-links", "-f", "--constraints", "--overrides", "--build-constraints",
                "--directory", "--project", "--cache-dir", "--config-file", "--env-file",
                "--python-preference", "--resolution", "--prerelease", "--index-strategy",
                "--keyring-provider", "--exclude-newer", "--refresh-package", "-P",
                "--upgrade-package", "--reinstall-package", "--no-build-package",
                "--no-binary-package", "--only-binary", "--no-binary", "--link-mode",
                "--color", "--allow-insecure-host", "--config-setting", "-C"),
            AcceptsAtVersion: true);

        /// <summary>
        /// Finds the argument holding the package spec, skipping this runner's
        /// options. An unknown option is assumed to take no value.
        /// </summary>
        public Target? Scan(IReadOnlyList<string> args)
        {
            var packages = new List<Target>();
            int? positional = null;
            var index = 0;

            while (index < args.Count)
            {
                var argument = args[index];
                if (argument == "--")
                {
                    if (index + 1 < args.Count) positional = index + 1;
                    break;
                }
                var joined = PackageFlags.FirstOrDefault(
                    flag => argument.StartsWith(flag + "=", StringComparison.Ordinal));
                if (joined is not null)
                {
                    packages.Add(new Target(index, argument[(joined.Length + 1)..], FromFlag: true));
                    index++;
                    continue;
                }
                if (PackageFlags.Contains(argument))
                {
                    if (index + 1 >= args.Count) return null;
                    packages.Add(new Target(index + 1, args[index + 1], FromFlag: true));
                    index += 2;
                    continue;
                }
                if (argument.StartsWith('-') && argument.Length > 1)
                {
                    index += ValueFlags.Contains(argument) && !argument.Contains('=') ? 2 : 1;
                    continue;
                }
                positional = index;
                break;
            }

            // With `-p a -p b` there is no telling which package is the server.
            if (packages.Count > 1) return null;
            if (packages.Count == 1) return packages[0];
            return positional is { } found ? new Target(found, args[found], FromFlag: false) : null;
        }

        private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
    }
}
