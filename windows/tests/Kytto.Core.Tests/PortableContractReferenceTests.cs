using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Portable;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// An imported fingerprint is evidence, never trust (§7.13, §7.10): it is compared
/// only with a check made here and after it arrived, and reviewing it can neither
/// start from a store that could not be read nor leave a local alert half
/// acknowledged.
/// </summary>
public sealed class PortableContractReferenceTests
{
    private static HealthResult Health(
        IReadOnlyList<ToolSummary> tools,
        DateTimeOffset checkedAt,
        HealthStatus status = HealthStatus.Passed) =>
        new(status, checkedAt, tools.Count, tools, null, null, 0, null, null);

    private static DateTimeOffset In(double seconds) => Timestamp.Now + TimeSpan.FromSeconds(seconds);

    /// <summary>A source with one exported server named <c>new</c> and one reviewed tool.</summary>
    private static ToggleHarness SourceWithContract(params ToolSummary[] tools)
    {
        var source = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        new MetadataStore(source.Paths).Record("new", Health(tools, In(0)), null);
        source.ExportTo(includeReviewedContracts: true);
        return source;
    }

    private static PortableSetupService Imported(ToggleHarness source, ToggleHarness destination)
    {
        var service = destination.Portable();
        service.Apply(service.Preview(source.PortableFile(), [ClientId.Cursor]).Id);
        return service;
    }

    private static string PendingFile(ToggleHarness harness) =>
        Path.Combine(harness.Paths.Root, "pending-contract-imports.json");

    [Fact]
    public void ReviewedContractsAreOptionalAndOffByDefault()
    {
        using var source = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        new MetadataStore(source.Paths).Record("new", Health([new ToolSummary("t", "d")], In(0)), null);

        Assert.Equal(0, source.ExportTo().PendingContractCount);
        Assert.Empty(PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile())).ReviewedContracts);

        Assert.Equal(1, source.ExportTo(includeReviewedContracts: true).PendingContractCount);
        var reference = Assert.Single(PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile())).ReviewedContracts);
        Assert.Equal("new", reference.ServerID);
        Assert.Equal(["t"], reference.ToolDigests.Keys);
    }

    [Fact]
    public void ImportedReviewedContractsStayPendingAndCannotEstablishATrustedBaseline()
    {
        using var source = SourceWithContract(new ToolSummary(
            "tool", "description-secret", """{"default":"schema-secret"}"""));
        using var destination = new ToggleHarness();
        var text = File.ReadAllText(source.PortableFile());
        Assert.DoesNotContain("description-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("schema-secret", text, StringComparison.Ordinal);

        var service = Imported(source, destination);

        Assert.Single(service.PendingContractReferences());
        Assert.Null(new MetadataStore(destination.Paths).For("new"));
        Assert.False(File.Exists(destination.Paths.MetadataFile), "nothing was recorded as trusted");
        Assert.Null(Assert.Single(service.PendingContractSummary()).Matching);
        Assert.Equal(
            PortableSetupFailure.LocalContractNotChecked,
            Assert.Throws<PortableSetupException>(() => service.ReviewPendingContract("new")).Failure);
        Assert.Single(service.PendingContractReferences());
    }

    [Fact]
    public void PendingMatchingIgnoresTokenCountsSchemaKeyOrderAndToolOrderButPreservesArraySemantics()
    {
        using var source = SourceWithContract(
            new ToolSummary("first", "Read", """{"properties":{"mode":{"enum":["a","b"]}},"type":"object"}""", null, 12),
            new ToolSummary("second", null, null, null, 20));
        using var destination = new ToggleHarness();
        var service = Imported(source, destination);
        var metadata = new MetadataStore(destination.Paths);

        ToolSummary[] reordered =
        [
            new("second", null, null, null, 900),
            new("first", "Read", """{ "type":"object", "properties": {"mode":{"enum":["a", "b"]}} }""", null, 600),
        ];
        metadata.Record("new", Health(reordered, In(1)), null);
        var matching = Assert.Single(service.PendingContractSummary());
        Assert.Equal("new", matching.ServerID);
        Assert.Equal(2, matching.ToolCount);
        Assert.True(matching.Matching);

        ToolSummary[] changed =
        [
            new("second", null),
            new("first", "Read", """{"type":"object","properties":{"mode":{"enum":["b","a"]}}}"""),
        ];
        metadata.Record("new", Health(changed, In(2)), null);
        Assert.False(Assert.Single(service.PendingContractSummary()).Matching);

        // A failed check says nothing, and a stale pass cannot be reviewed.
        metadata.Record("new", Health([], In(3), HealthStatus.Failed), null);
        Assert.Null(Assert.Single(service.PendingContractSummary()).Matching);
        Assert.Equal(
            PortableSetupFailure.LocalContractNotChecked,
            Assert.Throws<PortableSetupException>(() => service.ReviewPendingContract("new")).Failure);

        metadata.Record("new", Health(changed, In(4)), null);
        var baselineBefore = metadata.For("new")?.ContractTools;
        service.ReviewPendingContract("new");

        Assert.Empty(service.PendingContractSummary());
        // Reviewing never imports trust and never moves the local baseline.
        Assert.Equal(baselineBefore, metadata.For("new")?.ContractTools);
        Assert.NotEmpty(metadata.For("new")?.ContractChanges ?? []);
    }

    [Fact]
    public void ADuplicateToolNameIsLastWinsLikeContractGuard()
    {
        using var source = SourceWithContract(new ToolSummary("dup", "first"), new ToolSummary("dup", "second"));
        using var destination = new ToggleHarness();
        var service = Imported(source, destination);

        new MetadataStore(destination.Paths).Record(
            "new", Health([new ToolSummary("dup", "second")], In(1)), null);

        Assert.True(Assert.Single(service.PendingContractSummary()).Matching);
    }

    [Fact]
    public void ACachedPassBeforeTheImportCannotReviewAContractAndLegacyArraysAlsoNeedANewCheck()
    {
        var tools = new[] { new ToolSummary("read", "Read") };
        using var source = SourceWithContract(tools);
        using var destination = new ToggleHarness();
        var old = In(-60);
        var metadata = new MetadataStore(destination.Paths);
        metadata.Record("new", Health(tools, old), null);

        var service = Imported(source, destination);

        Assert.Null(Assert.Single(service.PendingContractSummary()).Matching);
        Assert.Equal(
            PortableSetupFailure.LocalContractNotChecked,
            Assert.Throws<PortableSetupException>(() => service.ReviewPendingContract("new")).Failure);
        metadata.Record("new", Health(tools, In(1)), null);
        Assert.True(Assert.Single(service.PendingContractSummary()).Matching);

        // The earlier on-disk representation had no import time. Its first sighting
        // in this process is the time, so it needs a check made after that.
        using var legacy = new ToggleHarness();
        var exported = PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile()));
        var reference = exported.ReviewedContracts.Single();
        Directory.CreateDirectory(legacy.Paths.Root);
        File.WriteAllText(
            PendingFile(legacy),
            JsonSerializer.Serialize(new[] { new { reference.ServerID, reference.ToolDigests } },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var legacyMetadata = new MetadataStore(legacy.Paths);
        legacyMetadata.Record("new", Health(tools, old), null);
        var legacyService = legacy.Portable();

        Assert.Null(Assert.Single(legacyService.PendingContractSummary()).Matching);
        Assert.Equal(
            PortableSetupFailure.LocalContractNotChecked,
            Assert.Throws<PortableSetupException>(() => legacyService.ReviewPendingContract("new")).Failure);
        legacyMetadata.Record("new", Health(tools, In(1)), null);
        Assert.True(Assert.Single(legacyService.PendingContractSummary()).Matching);
        legacyService.ReviewPendingContract("new");
        Assert.Empty(legacyService.PendingContractSummary());
    }

    [Fact]
    public void MalformedPendingReferencesReportASafeErrorAndSurviveAnAttemptedReview()
    {
        using var destination = new ToggleHarness();
        Directory.CreateDirectory(destination.Paths.Root);
        const string Malformed = """[{"serverID":"private-secret","toolDigests":{"tool":"invalid-digest-secret"}}]""";
        File.WriteAllText(PendingFile(destination), Malformed);
        var service = destination.Portable();

        Assert.Equal(
            PortableSetupFailure.PendingContractsUnreadable,
            Assert.Throws<PortableSetupException>(() => service.PendingContractSummary()).Failure);
        var acknowledged = false;
        Assert.Equal(
            PortableSetupFailure.PendingContractsUnreadable,
            Assert.Throws<PortableSetupException>(() =>
                service.ReviewPending("private-secret", () => acknowledged = true)).Failure);

        Assert.False(acknowledged, "an unreadable store must stop the acknowledgement before it starts");
        Assert.Equal(Malformed, File.ReadAllText(PendingFile(destination)));
        var message = PortableSetupException.PendingContractsUnreadable().Message;
        Assert.DoesNotContain("private-secret", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"importedAt":1,"reference":{"serverID":"a","toolDigests":{}},"extra":1}]""")]
    [InlineData("""[{"importedAt":1,"importedAt":2,"reference":{"serverID":"a","toolDigests":{}}}]""")]
    [InlineData("""[{"importedAt":"yesterday","reference":{"serverID":"a","toolDigests":{}}}]""")]
    [InlineData("""[{"importedAt":-5,"reference":{"serverID":"a","toolDigests":{}}}]""")]
    [InlineData("""[{"importedAt":99999999999999,"reference":{"serverID":"a","toolDigests":{}}}]""")]
    [InlineData("""[{"importedAt":1,"reference":{"serverID":"A","toolDigests":{}}}]""")]
    [InlineData("""[{"importedAt":1,"reference":{"serverID":"a","toolDigests":{"t":"abc"}}}]""")]
    [InlineData("""[{"importedAt":1,"reference":{"serverID":"a","toolDigests":{"t":"0000000000000000000000000000000000000000000000000000000000000000","t":"0000000000000000000000000000000000000000000000000000000000000000"}}}]""")]
    [InlineData("""[{"importedAt":1,"reference":{"serverID":"a","toolDigests":{}}},{"importedAt":1,"reference":{"serverID":"a","toolDigests":{}}}]""")]
    [InlineData("""{"not":"an array"}""")]
    public void EveryShapeOrValueRuleOfThePendingStoreIsEnforced(string text)
    {
        using var destination = new ToggleHarness();
        Directory.CreateDirectory(destination.Paths.Root);
        File.WriteAllText(PendingFile(destination), text);

        Assert.Equal(
            PortableSetupFailure.PendingContractsUnreadable,
            Assert.Throws<PortableSetupException>(() => destination.Portable().PendingContractReferences()).Failure);
        Assert.Equal(text, File.ReadAllText(PendingFile(destination)));
    }

    [Fact]
    public void AFutureImportTimeIsNotBelieved()
    {
        using var destination = new ToggleHarness();
        Directory.CreateDirectory(destination.Paths.Root);
        var future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds();
        File.WriteAllText(
            PendingFile(destination),
            "[{\"importedAt\":" + future + ",\"reference\":{\"serverID\":\"a\",\"toolDigests\":{}}}]");

        Assert.Equal(
            PortableSetupFailure.PendingContractsUnreadable,
            Assert.Throws<PortableSetupException>(() => destination.Portable().PendingContractSummary()).Failure);
    }

    [Fact]
    public void ReviewRemovesTheReferenceFirstAndPutsItBackWhenTheBaselineStepRefuses()
    {
        var tools = new[] { new ToolSummary("read", "Read") };
        using var source = SourceWithContract(tools);
        using var destination = new ToggleHarness();
        var service = Imported(source, destination);
        new MetadataStore(destination.Paths).Record("new", Health(tools, In(1)), null);
        var before = File.ReadAllBytes(PendingFile(destination));

        var order = new List<string>();
        Assert.Throws<InvalidDataException>(() => service.ReviewPending("new", () =>
        {
            order.Add(File.Exists(PendingFile(destination)) && service.PendingContractReferences().Count == 0
                ? "reference already removed"
                : "reference still present");
            throw new InvalidDataException("baseline store unreadable");
        }));

        Assert.Equal(["reference already removed"], order);
        Assert.Equal(before, File.ReadAllBytes(PendingFile(destination)));
        Assert.Single(service.PendingContractReferences());

        var acknowledged = false;
        service.ReviewPending("new", () => acknowledged = true);
        Assert.True(acknowledged);
        Assert.Empty(service.PendingContractReferences());
    }

    [Fact]
    public void AServerWithNothingPendingJustAcknowledges()
    {
        using var destination = new ToggleHarness();
        var acknowledged = false;
        destination.Portable().ReviewPending("anything", () => acknowledged = true);
        Assert.True(acknowledged);
        Assert.False(File.Exists(PendingFile(destination)));
    }

    [Fact]
    public void ThePendingStoreIsKeptPrivateAndNeverHoldsContractText()
    {
        using var source = SourceWithContract(new ToolSummary("t", "description-secret"));
        using var destination = new ToggleHarness();
        Imported(source, destination);

        var text = File.ReadAllText(PendingFile(destination));
        Assert.DoesNotContain("description-secret", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        var entry = document.RootElement[0];
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("importedAt").ValueKind);
        Assert.Equal(
            ["importedAt", "reference"],
            entry.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(KyttoStorage.IsPrivate(PendingFile(destination)));
    }

    [Fact]
    public void ImportedAtIsTheLocalClockNotATimeFromTheFile()
    {
        using var source = SourceWithContract(new ToolSummary("t", "d"));
        using var destination = new ToggleHarness();
        var local = new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var service = destination.Portable(() => local);

        service.Apply(service.Preview(source.PortableFile(), [ClientId.Cursor]).Id);

        using var document = JsonDocument.Parse(File.ReadAllText(PendingFile(destination)));
        Assert.Equal(
            local.ToUnixTimeMilliseconds(),
            document.RootElement[0].GetProperty("importedAt").GetInt64());
    }
}
