using System.Text.Json;
using Kytto.Core.Health;
using Kytto.Core.Portable;

namespace Kytto.Core.Tests;

/// <summary>
/// The fingerprint a tool gets must be the one the macOS app computes for it (§7.13).
/// The vectors are real output of the compiled macOS core; this port matches them
/// exactly or a reviewed-contract reference exported on one platform never matches
/// on the other.
/// </summary>
public sealed class PortableContractDigestTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "compatibility", name);

    private static IReadOnlyList<(string Label, ToolSummary Tool, string Digest, string? Schema)> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath("contract-digest-v1-vectors.json")));
        return document.RootElement.EnumerateArray().Select(vector =>
        {
            var tool = vector.GetProperty("tool");
            ToolAnnotations? annotations = null;
            if (tool.TryGetProperty("annotations", out var hints))
            {
                bool? Hint(string name) =>
                    hints.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
                annotations = new ToolAnnotations(
                    Hint("readOnlyHint"), Hint("destructiveHint"), Hint("idempotentHint"), Hint("openWorldHint"));
            }
            string? Text(string name) =>
                tool.TryGetProperty(name, out var value) ? value.GetString() : null;
            return (
                vector.GetProperty("label").GetString()!,
                new ToolSummary(
                    Text("name")!,
                    Text("description"),
                    Text("inputSchemaJSON"),
                    annotations,
                    tool.TryGetProperty("tokenCount", out var tokens) ? tokens.GetInt32() : null),
                vector.GetProperty("expectedDigest").GetString()!,
                vector.TryGetProperty("expectedCanonicalSchema", out var schema) ? schema.GetString() : null);
        }).ToArray();
    }

    [Fact]
    public void EveryMacOsVectorHashesToTheDigestTheMacOsCoreProduced()
    {
        var vectors = Vectors();
        Assert.Equal(5, vectors.Count);
        foreach (var (label, tool, digest, _) in vectors)
        {
            Assert.True(digest == ContractDigest.Of(tool), $"{label} digest differs from the macOS core's");
        }
    }

    [Fact]
    public void CanonicalSchemasMatchTheMacOsTextExactly()
    {
        foreach (var (label, tool, _, schema) in Vectors().Where(vector => vector.Schema is not null))
        {
            Assert.True(
                schema == ContractDigest.CanonicalSchema(tool.InputSchemaJSON!),
                $"{label} canonical schema differs from the macOS core's");
        }
    }

    [Fact]
    public void SchemaPresentationAndTokenCountsNeverChangeTheDigest()
    {
        var vectors = Vectors();
        Assert.Equal(vectors[1].Digest, vectors[2].Digest);
        Assert.NotEqual(vectors[1].Tool.InputSchemaJSON, vectors[2].Tool.InputSchemaJSON);
        Assert.NotEqual(vectors[1].Tool.TokenCount, vectors[2].Tool.TokenCount);
        Assert.Equal(ContractDigest.Of(vectors[1].Tool), ContractDigest.Of(vectors[2].Tool));
    }

    [Fact]
    public void WholeNumbersLoseTheirDecimalPointAndArraysKeepTheirOrder()
    {
        // `1.0` and `1` are one value, `[b, a]` and `[a, b]` are two.
        var canonical = ContractDigest.CanonicalSchema("""{"b":1.0,"a":{"y":2.50,"x":[ "z","a",null ]},"c":1e3}""");
        Assert.Equal("""{"a":{"x":["z","a",null],"y":2.5},"b":1,"c":1000}""", canonical);
        Assert.NotEqual(
            ContractDigest.Of(new ToolSummary("t", null, """{"enum":["a","b"]}""")),
            ContractDigest.Of(new ToolSummary("t", null, """{"enum":["b","a"]}""")));
    }

    [Fact]
    public void ADuplicateKeyIsLastWinsAsADictionaryWouldHaveIt()
    {
        Assert.Equal("""{"a":2}""", ContractDigest.CanonicalSchema("""{"a":1,"a":2}"""));
    }

    [Fact]
    public void AnUnparseableSchemaFallsBackToItsRawTextAndIsNeverRepaired()
    {
        Assert.Null(ContractDigest.CanonicalSchema("{broken"));
        Assert.Null(ContractDigest.CanonicalSchema("// comment\n{}"));
        Assert.Null(ContractDigest.CanonicalSchema("{\"a\":1,}"));

        var raw = new ToolSummary("t", null, "{broken");
        var other = new ToolSummary("t", null, "{broken ");
        Assert.NotEqual(ContractDigest.Of(raw), ContractDigest.Of(other));
    }

    [Fact]
    public void ADigestIgnoresTokenCountsAndAnEmptyAnnotationsObject()
    {
        // This app's parser builds an annotations object even when a tool declares
        // none, and the macOS app records none; they have to be one thing.
        var plain = new ToolSummary("t", "d", "{}");
        Assert.Equal(
            ContractDigest.Of(plain),
            ContractDigest.Of(plain with { Annotations = new ToolAnnotations(), TokenCount = 12_345 }));
        Assert.NotEqual(
            ContractDigest.Of(plain),
            ContractDigest.Of(plain with { Annotations = new ToolAnnotations(ReadOnlyHint: true) }));
    }

    [Fact]
    public void TextIsWrittenTheWayTheMacOsEncoderWritesIt()
    {
        // The envelope, spelled out: sorted keys, nothing escaped that does not
        // need to be, control characters as \u00xx. Pinning the bytes pins the
        // contract, not just the hash function.
        var tool = new ToolSummary(
            "a/b", "tab\there \"quoted\" \\ \u0001 čučoriedka 😀 </script>", """{"type":"object"}""",
            new ToolAnnotations(ReadOnlyHint: true, DestructiveHint: false));
        const string Envelope =
            "{\"annotations\":{\"destructiveHint\":false,\"readOnlyHint\":true}," +
            "\"description\":\"tab\\there \\\"quoted\\\" \\\\ \\u0001 čučoriedka 😀 </script>\"," +
            "\"name\":\"a/b\",\"schema\":\"{\\\"type\\\":\\\"object\\\"}\",\"version\":1}";
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Envelope))),
            ContractDigest.Of(tool));
    }
}
