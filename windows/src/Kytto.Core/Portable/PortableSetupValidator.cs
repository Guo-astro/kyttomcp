using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Portable;

/// <summary>
/// Every rule a portable setup must satisfy before any of it is believed (§7.13).
/// </summary>
/// <remarks>
/// Run on a file that arrives from outside, and again on the setup an export is
/// about to write: a rule this app would refuse in somebody else's file it must not
/// break in its own. The limits and the whitelists are the macOS app's exactly,
/// because the same file crosses between the two.
/// </remarks>
internal static class PortableSetupValidator
{
    /// <summary>
    /// The launchers that may stay in the file as they are. Anything else — a path, a
    /// wrapper script, an unknown program — is the destination's to supply.
    /// </summary>
    public static readonly IReadOnlySet<string> SafeCommands = new HashSet<string>(
        ["npx", "npm", "pnpm", "yarn", "bunx", "bun", "uvx", "uv", "pipx", "node", "python", "python3", "docker"],
        StringComparer.Ordinal);

    /// <summary>
    /// Flags that select how a launcher runs and carry no value of their own.
    /// </summary>
    public static readonly IReadOnlySet<string> SafeArguments = new HashSet<string>(
        [
            "-y", "--yes", "--no-install", "--ignore-scripts", "--offline", "--no-cache", "--package",
            "-p", "--from", "--spec", "--", "exec", "dlx", "x", "tool", "run", "-m", "--stdio", "-i", "--rm",
        ],
        StringComparer.Ordinal);

    private static readonly Regex EnvironmentKey = new(
        @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly Regex HeaderName = new(
        @"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\z", RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Hex = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    /// <summary>Stands in for a required argument when asking whether a package is literal.</summary>
    private const string RequiredArgument = "__kytto_required_input__";

    /// <summary>Non-empty, bounded in UTF-8 bytes, and free of control characters.</summary>
    public static bool ValidText(string text, int limit = 256)
    {
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > limit) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) return false;
        }
        return true;
    }

    public static bool IsSha256(string text) => Sha256Hex.IsMatch(text);

    public static void Validate(PortableSetup setup)
    {
        if (setup.Format != PortableSetup.FormatName || setup.Version != PortableSetup.CurrentVersion)
        {
            throw PortableSetupException.Invalid("unsupported format or version");
        }
        if (setup.Servers.Count > 200 || setup.Profiles.Count > 100 ||
            setup.Requirements.Count > 4096 || setup.ReviewedContracts.Count > 200)
        {
            throw PortableSetupException.Invalid("too many entries");
        }

        var requirements = new Dictionary<string, PortableRequirement>(StringComparer.Ordinal);
        foreach (var requirement in setup.Requirements)
        {
            if (!Guid.TryParseExact(requirement.Id, "D", out _) ||
                requirements.ContainsKey(requirement.Id) ||
                !ValidText(requirement.Label, 512))
            {
                throw PortableSetupException.Invalid("invalid or duplicate required input");
            }
            requirements[requirement.Id] = requirement;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        void CheckValue(
            PortableValue entry,
            IReadOnlySet<string>? literals,
            PortableRequirementKind? kind)
        {
            if (entry.Literal is { } literal)
            {
                if (entry.RequirementID is not null || literals is null || !literals.Contains(literal))
                {
                    throw PortableSetupException.Invalid("only whitelisted launcher literals are allowed");
                }
            }
            else if (entry.RequirementID is { } id && requirements.TryGetValue(id, out var requirement))
            {
                if (!used.Add(id) || (kind is not null && requirement.Kind != kind))
                {
                    throw PortableSetupException.Invalid("reused required input or incorrect input kind");
                }
            }
            else
            {
                throw PortableSetupException.Invalid("missing required input reference");
            }
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in setup.Servers)
        {
            var identity = Server.Identity(server.Name);
            if (!ValidText(server.Name) ||
                server.Name != server.Name.Trim() ||
                server.Name.Any(char.IsWhiteSpace) ||
                !identities.Add($"{server.SourceClient.Raw()}:{identity}") ||
                server.Args.Count > 128 || server.Environment.Count > 128 || server.Headers.Count > 64)
            {
                throw PortableSetupException.Invalid("invalid or duplicate server");
            }
            names.Add(identity);

            if (server.Transport == Transport.Stdio)
            {
                if (server.Command is not { } command || server.Url is not null || server.Headers.Count > 0)
                {
                    throw PortableSetupException.Invalid("invalid stdio definition");
                }
                CheckValue(command, SafeCommands, PortableRequirementKind.Path);
            }
            else
            {
                if (server.Url is not { } url || server.Command is not null ||
                    server.Args.Count > 0 || server.WorkingDirectory is not null)
                {
                    throw PortableSetupException.Invalid("invalid remote definition");
                }
                CheckValue(url, null, PortableRequirementKind.Secret);
            }

            // A package may be literal only when its launcher is — the same rule the
            // exporter applies, spelled from the file rather than from a config.
            var packageIndex = PortablePackages.SafePackageIndex(
                server.Command?.Literal,
                server.Args.Select(argument => argument.Literal ?? RequiredArgument).ToArray(),
                SafeArguments);
            for (var index = 0; index < server.Args.Count; index++)
            {
                var argument = server.Args[index];
                IReadOnlySet<string> literals = index == packageIndex
                    ? new HashSet<string>(SafeArguments.Append(argument.Literal ?? ""), StringComparer.Ordinal)
                    : SafeArguments;
                CheckValue(argument, literals, null);
            }

            if (server.WorkingDirectory is { } cwd) CheckValue(cwd, null, PortableRequirementKind.Path);

            foreach (var (key, entry) in server.Environment)
            {
                if (!EnvironmentKey.IsMatch(key) || Encoding.UTF8.GetByteCount(key) > 128)
                {
                    throw PortableSetupException.Invalid("invalid environment key");
                }
                CheckValue(entry, null, PortableRequirementKind.Secret);
            }

            var headerNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, entry) in server.Headers)
            {
                if (!HeaderName.IsMatch(key) || Encoding.UTF8.GetByteCount(key) > 128 ||
                    !headerNames.Add(key.ToLowerInvariant()))
                {
                    throw PortableSetupException.Invalid("invalid or duplicate header name");
                }
                CheckValue(entry, null, PortableRequirementKind.Secret);
            }
        }
        if (!used.SetEquals(requirements.Keys))
        {
            throw PortableSetupException.Invalid("unused required input");
        }

        var profileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in setup.Profiles)
        {
            if (!ValidText(profile.Name, 320) ||
                new StringInfo(profile.Name).LengthInTextElements > 80 ||
                profile.Name != profile.Name.Trim() ||
                !profileNames.Add(profile.Name.ToLowerInvariant()) ||
                profile.ServerIDs.Count > 200 ||
                profile.ServerIDs.Distinct(StringComparer.Ordinal).Count() != profile.ServerIDs.Count ||
                !profile.ServerIDs.All(id => id == Server.Identity(id) && names.Contains(id)) ||
                (profile.TokenBudget is { } budget && budget is < 1 or > TokenWeight.ReferenceContextWindow))
            {
                throw PortableSetupException.Invalid("invalid or duplicate profile");
            }
        }

        var contractIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in setup.ReviewedContracts)
        {
            if (!names.Contains(contract.ServerID) ||
                !contractIds.Add(contract.ServerID) ||
                contract.ToolDigests.Count > 2048 ||
                !contract.ToolDigests.All(pair => ValidText(pair.Key) && Sha256Hex.IsMatch(pair.Value)))
            {
                throw PortableSetupException.Invalid("invalid or duplicate contract reference");
            }
        }
    }

    /// <summary>Whether this one server would pass, with its own requirements.</summary>
    public static bool IsPortable(PortableServer server, IReadOnlyList<PortableRequirement> requirements)
    {
        try
        {
            Validate(new PortableSetup(
                PortableSetup.FormatName, PortableSetup.CurrentVersion, [server], [], requirements, []));
            return true;
        }
        catch (PortableSetupException)
        {
            return false;
        }
    }
}
