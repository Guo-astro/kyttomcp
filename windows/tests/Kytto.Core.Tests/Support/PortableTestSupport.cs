using System.Diagnostics;
using Kytto.Core.Clients;
using Kytto.Core.Portable;

namespace Kytto.Core.Tests.Support;

/// <summary>What a portable-setup test needs on top of a throwaway profile.</summary>
internal static class PortableTestSupport
{
    /// <summary>A service over the harness's profile, stores and ledger.</summary>
    public static PortableSetupService Portable(
        this ToggleHarness harness,
        Func<DateTimeOffset>? clock = null,
        IConfigFileWriter? fileWriter = null) =>
        new(
            harness.Home,
            new ConfigWriter(harness.Backups, fileWriter),
            harness.ParkStore,
            harness.Ledger,
            harness.Paths,
            clock: clock);

    public static string PortableFile(this ToggleHarness harness) =>
        Path.Combine(harness.Home, "setup.kytto.json");

    /// <summary>
    /// Plausible, obviously fake replacements for every requirement a setup names.
    /// </summary>
    public static Dictionary<string, string> Inputs(PortableSetupSummary summary) =>
        summary.Requirements.ToDictionary(
            requirement => requirement.Id,
            requirement =>
            {
                if (requirement.Kind == PortableRequirementKind.Path) return @"C:\mapped\portable-path";
                if (requirement.Label.Contains(": remote URL", StringComparison.Ordinal)) return "https://example.test/mcp";
                if (requirement.Label.Contains("argument", StringComparison.Ordinal)) return "@modelcontextprotocol/server-memory";
                return "replacement-value";
            });

    /// <summary>Exports <paramref name="source"/> and returns the summary and the file.</summary>
    public static PortableSetupSummary ExportTo(
        this ToggleHarness source,
        bool includeReviewedContracts = false) =>
        source.Portable().Export(
            source.PortableFile(), source.Discover().Servers, includeReviewedContracts);

    /// <summary>Makes a directory junction, which needs no privilege. False if it cannot.</summary>
    public static bool TryMakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    /// <summary>Makes a file symbolic link. False when the account may not create one.</summary>
    public static bool TryMakeSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A write primitive whose <paramref name="failAt"/>th write (counting from zero)
    /// fails. With <paramref name="failRollbackToo"/> every later write fails as well,
    /// which is what a rollback that cannot complete looks like.
    /// </summary>
    public sealed class FailingWriter(int failAt, bool failRollbackToo = false, Action? onFailure = null)
        : IConfigFileWriter
    {
        private readonly AtomicConfigFileWriter _real = new();
        private int _writes;

        public void Write(byte[] data, string path)
        {
            var index = _writes++;
            if (index == failAt || (failRollbackToo && index > failAt))
            {
                if (index == failAt) onFailure?.Invoke();
                throw new IOException("injected failure");
            }
            _real.Write(data, path);
        }

        public void Delete(string path)
        {
            if (failRollbackToo) throw new IOException("injected rollback failure");
            _real.Delete(path);
        }
    }
}
