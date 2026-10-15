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

    [Fact]
    public void AShareOfAnotherComputerIsNotMistakenForThisOne()
    {
        Assert.False(FinalPath.IsShareOnThisComputer(@"\\another-computer-that-is-not-this-one\share\setup.json"));
        Assert.False(FinalPath.IsShareOnThisComputer(@"C:\Users\someone\setup.json"));
        Assert.False(FinalPath.IsShareOnThisComputer(@"\\10.255.255.1\share\setup.json"));
    }
}
