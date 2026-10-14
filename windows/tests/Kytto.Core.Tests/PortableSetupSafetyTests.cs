using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Portable;
using Kytto.Core.Profiles;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// An untrusted file, a destination that moves, a write that fails halfway: the
/// ways an import could damage somebody's configuration (§7.13, §6).
/// </summary>
public sealed class PortableSetupSafetyTests
{
    /// <summary>A source profile with one portable server, already exported.</summary>
    private static ToggleHarness ExportedSource(string server = """{"new":{"command":"npx"}}""")
    {
        var harness = new ToggleHarness();
        harness.Write("{\"mcpServers\":" + server + "}", ".cursor/mcp.json");
        harness.ExportTo();
        return harness;
    }

    private static PortableSetupFailure FailureOf(Action action) =>
        Assert.Throws<PortableSetupException>(action).Failure;

    // MARK: - Untrusted files

    [Fact]
    public void StrictVersionDuplicateKeyUnknownFieldLiteralAndSizeValidation()
    {
        using var source = ExportedSource();
        var good = File.ReadAllText(source.PortableFile());
        var mutations = new[]
        {
            good.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal),
            good.Replace("\"format\":", "\"version\":1,\"format\":", StringComparison.Ordinal),
            good.Replace("\"format\":", "\"unexpected\":true,\"format\":", StringComparison.Ordinal),
            good.Replace("\"literal\": \"npx\"", "\"literal\": \"sh\"", StringComparison.Ordinal),
            new string(' ', PortableSetupService.MaximumFileBytes + 1),
        };
        foreach (var text in mutations)
        {
            Assert.NotEqual(good, text);
            File.WriteAllText(source.PortableFile(), text);
            Assert.Throws<PortableSetupException>(() => source.Portable().Inspect(source.PortableFile()));
        }
    }

    [Theory]
    [InlineData("// comment\n{}")]
    [InlineData("{\"format\":\"kytto-portable-setup\",}")]
    [InlineData("{\"format\":\"kytto-portable-setup\",\"format\":\"kytto-portable-setup\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void MalformedDocumentsAreRefusedBeforeAnythingIsLookedAt(string text)
    {
        using var harness = new ToggleHarness();
        File.WriteAllText(harness.PortableFile(), text);
        Assert.Equal(
            PortableSetupFailure.Invalid,
            FailureOf(() => harness.Portable().Inspect(harness.PortableFile())));
    }

    [Fact]
    public void ShapeTypeAndDuplicateRulesHoldAtEveryLevel()
    {
        using var source = ExportedSource();
        var good = File.ReadAllText(source.PortableFile());
        var mutations = new Dictionary<string, string>
        {
            ["version as a decimal"] = good.Replace("\"version\": 1", "\"version\": 1.0", StringComparison.Ordinal),
            ["wasEnabled as text"] = good.Replace("\"wasEnabled\": true", "\"wasEnabled\": \"true\"", StringComparison.Ordinal),
            ["unknown transport"] = good.Replace("\"transport\": \"stdio\"", "\"transport\": \"carrier-pigeon\"", StringComparison.Ordinal),
            ["custom source client"] = good.Replace("\"sourceClient\": \"cursor\"", "\"sourceClient\": \"custom.00000000-0000-0000-0000-000000000001\"", StringComparison.Ordinal),
            ["unknown field in a value"] = good.Replace("\"literal\": \"npx\"", "\"literal\": \"npx\", \"extra\": 1", StringComparison.Ordinal),
            ["both literal and requirement"] = good.Replace(
                "\"literal\": \"npx\"",
                "\"literal\": \"npx\", \"requirementID\": \"00000000-0000-0000-0000-000000000000\"",
                StringComparison.Ordinal),
            ["missing args"] = good.Replace("\"args\": []", "\"argz\": []", StringComparison.Ordinal),
            ["null servers"] = good.Replace("\"servers\": [", "\"servers\": null, \"x\": [", StringComparison.Ordinal),
        };
        foreach (var (name, text) in mutations)
        {
            Assert.NotEqual(good, text);
            File.WriteAllText(source.PortableFile(), text);
            var failure = Assert.Throws<PortableSetupException>(() => source.Portable().Inspect(source.PortableFile()));
            Assert.True(failure.Failure == PortableSetupFailure.Invalid, $"{name} must be refused");
        }
    }

    [Fact]
    public void AByteOrderMarkIsSkippedButInvalidUtf8IsRefused()
    {
        using var source = ExportedSource();
        var good = File.ReadAllBytes(source.PortableFile());

        File.WriteAllBytes(source.PortableFile(), [0xEF, 0xBB, 0xBF, .. good]);
        Assert.Equal(["new"], source.Portable().Inspect(source.PortableFile()).ServerNames);

        File.WriteAllBytes(source.PortableFile(), [.. good.Take(good.Length - 3), 0xC3, 0x28, .. good.Skip(good.Length - 3)]);
        Assert.Equal(
            PortableSetupFailure.Invalid,
            FailureOf(() => source.Portable().Inspect(source.PortableFile())));
    }

    [Fact]
    public void ARequirementThatIsReusedOrUnusedOrOfTheWrongKindIsRefused()
    {
        using var source = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"node","env":{"A":"1","B":"2"}}}}""", ".cursor/mcp.json");
        source.ExportTo();
        var setup = PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile()));
        var server = setup.Servers.Single();

        void Refuses(PortableSetup candidate, string because)
        {
            File.WriteAllBytes(source.PortableFile(), PortableSetupSerializer.Write(candidate));
            Assert.True(
                PortableSetupFailure.Invalid == FailureOf(() => source.Portable().Inspect(source.PortableFile())),
                because);
        }

        // The same input standing for two values.
        var reused = server.Environment["A"];
        Refuses(
            setup with { Servers = [server with { Environment = new Dictionary<string, PortableValue> { ["A"] = reused, ["B"] = reused } }] },
            "a reused input");
        // An input nothing refers to.
        Refuses(
            setup with { Requirements = [.. setup.Requirements, new PortableRequirement(Guid.NewGuid().ToString("D"), "spare", PortableRequirementKind.Secret)] },
            "an unused input");
        // An environment value declared as a path.
        Refuses(
            setup with
            {
                Requirements = setup.Requirements
                    .Select(item => item.Id == reused.RequirementID ? item with { Kind = PortableRequirementKind.Path } : item)
                    .ToArray(),
            },
            "the wrong kind");
        // A header on a stdio server.
        Refuses(
            setup with { Servers = [server with { Headers = new Dictionary<string, PortableValue> { ["X-Key"] = reused } }] },
            "a header on stdio");
    }

    [Fact]
    public void LimitsOnLabelsEnvironmentKeysHeaderNamesAndProfilesAreEnforced()
    {
        using var harness = new ToggleHarness();
        var id = Guid.NewGuid().ToString("D");
        PortableSetup Setup(PortableServer server, IReadOnlyList<PortableRequirement> requirements, IReadOnlyList<PortableProfile>? profiles = null) =>
            new(PortableSetup.FormatName, 1, [server], profiles ?? [], requirements, []);
        PortableServer Remote(IReadOnlyDictionary<string, PortableValue> headers) => new(
            "remote", ClientId.Cursor, true, Kytto.Core.Model.Transport.Http, null, [],
            new Dictionary<string, PortableValue>(), headers, PortableValue.Required(id), null);
        var url = new PortableRequirement(id, "remote: remote URL", PortableRequirementKind.Secret);

        PortableSetupValidator.Validate(Setup(Remote(new Dictionary<string, PortableValue>()), [url]));

        // A header name that is not a token, one that repeats in another case, and a control character in a label.
        var second = Guid.NewGuid().ToString("D");
        var bad = new PortableRequirement(second, "label", PortableRequirementKind.Secret);
        Assert.Throws<PortableSetupException>(() => PortableSetupValidator.Validate(Setup(
            Remote(new Dictionary<string, PortableValue> { ["bad name"] = PortableValue.Required(second) }), [url, bad])));
        Assert.Throws<PortableSetupException>(() => PortableSetupValidator.Validate(Setup(
            Remote(new Dictionary<string, PortableValue>()),
            [url with { Label = "contains\nnewline" }])));
        Assert.Throws<PortableSetupException>(() => PortableSetupValidator.Validate(Setup(
            Remote(new Dictionary<string, PortableValue>()), [url],
            [new PortableProfile("Pointing at nothing", ["ghost"], null)])));
        Assert.Throws<PortableSetupException>(() => PortableSetupValidator.Validate(Setup(
            Remote(new Dictionary<string, PortableValue>()), [url],
            [new PortableProfile("Budget", [], Kytto.Core.Health.TokenWeight.ReferenceContextWindow + 1)])));
        Assert.Throws<PortableSetupException>(() => PortableSetupValidator.Validate(Setup(
            Remote(new Dictionary<string, PortableValue>()), [url],
            [new PortableProfile("Dup", [], null), new PortableProfile("DUP", [], null)])));
    }

    // MARK: - Where an export may land

    [Fact]
    public void ExportCannotOverwriteALiveConfigurationOrRecoveryStore()
    {
        using var source = ExportedSource();
        var config = Path.Combine(source.Home, ".cursor", "mcp.json");
        var before = File.ReadAllBytes(config);
        var servers = source.Discover().Servers;
        var service = source.Portable();

        Assert.Equal(PortableSetupFailure.ProtectedLocation, FailureOf(() => service.Export(config, servers)));
        Assert.Throws<PortableSetupException>(() => service.Export(source.Paths.ProfilesFile, servers));
        Assert.Throws<PortableSetupException>(() =>
            service.Export(Path.Combine(source.Paths.Backups, "cursor", "anything.json"), servers));
        Assert.Throws<PortableSetupException>(() =>
            service.Export(Path.Combine(source.Home, ".claude.json"), servers));
        // Letter case and a redundant segment name the same file.
        Assert.Throws<PortableSetupException>(() =>
            service.Export(Path.Combine(source.Home, ".CURSOR", "..", ".cursor", "MCP.JSON"), servers));
        Assert.Equal(before, File.ReadAllBytes(config));
        Assert.False(File.Exists(source.Paths.ProfilesFile));
    }

    [Fact]
    public void ExportCannotReachALiveConfigurationThroughAJunction()
    {
        using var source = ExportedSource();
        var config = Path.Combine(source.Home, ".cursor", "mcp.json");
        var before = File.ReadAllBytes(config);
        var junction = Path.Combine(source.Home, "innocent-looking");
        Assert.True(PortableTestSupport.TryMakeJunction(junction, Path.Combine(source.Home, ".cursor")));
        var viaJunctionToData = Path.Combine(source.Home, "elsewhere");
        Directory.CreateDirectory(source.Paths.Root);
        Assert.True(PortableTestSupport.TryMakeJunction(viaJunctionToData, source.Paths.Root));
        var servers = source.Discover().Servers;

        Assert.Throws<PortableSetupException>(() =>
            source.Portable().Export(Path.Combine(junction, "mcp.json"), servers));
        Assert.Throws<PortableSetupException>(() =>
            source.Portable().Export(Path.Combine(viaJunctionToData, "profiles.json"), servers));
        // Not yet existing, below a junction into the data folder.
        Assert.Throws<PortableSetupException>(() =>
            source.Portable().Export(Path.Combine(viaJunctionToData, "new-folder", "x.json"), servers));
        Assert.Equal(before, File.ReadAllBytes(config));
        Assert.False(File.Exists(source.Paths.ProfilesFile));
    }

    [Fact]
    public void ExportCannotReachALiveConfigurationThroughASymbolicLink()
    {
        using var source = ExportedSource();
        var config = Path.Combine(source.Home, ".cursor", "mcp.json");
        var before = File.ReadAllBytes(config);
        var alias = Path.Combine(source.Home, "alias.json");
        if (!PortableTestSupport.TryMakeSymbolicLink(alias, config))
        {
            // Creating a symbolic link takes Developer Mode or an elevated account;
            // the junction test above covers the same resolution without either.
            return;
        }

        Assert.Throws<PortableSetupException>(() => source.Portable().Export(alias, source.Discover().Servers));
        Assert.Equal(before, File.ReadAllBytes(config));
    }

    [Fact]
    public void ExportRefusesAlternateDataStreamNames()
    {
        using var source = ExportedSource();
        Assert.Throws<PortableSetupException>(() => source.Portable().Export(
            Path.Combine(source.Home, "innocent.json:evil"), source.Discover().Servers));
    }

    [Fact]
    public void AnExportAnywhereElseSucceedsAndLeavesNoTemporaryFile()
    {
        using var source = ExportedSource();
        var elsewhere = Path.Combine(source.Home, "Documents", "my-setup.json");
        source.Portable().Export(elsewhere, source.Discover().Servers);

        Assert.True(File.Exists(elsewhere));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(elsewhere)!, "*.tmp"));
    }

    // MARK: - Inputs and collisions

    [Fact]
    public void EveryRequiredInputIsValidatedBeforeTouchingAnyDestination()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"C:\\private\\tool.exe","env":{"TOKEN":"secret"}}}}""", ".cursor/mcp.json");
        var summary = source.ExportTo();
        var service = destination.Portable();
        Assert.Equal(
            PortableSetupFailure.MissingInput,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor])));

        var path = summary.Requirements.First(requirement => requirement.Kind == PortableRequirementKind.Path);
        foreach (var relative in new[] { @".\relative", "relative", @"C:drive-relative", @"\rooted-on-this-drive", "/posix" })
        {
            var values = PortableTestSupport.Inputs(summary);
            values[path.Id] = relative;
            Assert.True(
                PortableSetupFailure.Invalid == FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor], values)),
                $"\"{relative}\" must not be accepted as an absolute path");
        }

        var tooLong = PortableTestSupport.Inputs(summary);
        tooLong[summary.Requirements.First(requirement => requirement.Kind == PortableRequirementKind.Secret).Id] = new string('x', 16_385);
        Assert.Equal(
            PortableSetupFailure.MissingInput,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor], tooLong)));

        var unknown = PortableTestSupport.Inputs(summary);
        unknown[Guid.NewGuid().ToString("D")] = "x";
        Assert.Equal(
            PortableSetupFailure.Invalid,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor], unknown)));
        Assert.False(Directory.Exists(destination.Paths.Root));
        Assert.False(destination.Exists(".cursor/mcp.json"));
    }

    [Fact]
    public void ChoosingNoClientsTheSameClientTwiceOrTwoClientsOneFileIsRefused()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var service = destination.Portable();

        Assert.Equal(PortableSetupFailure.Invalid, FailureOf(() => service.Preview(source.PortableFile(), [])));
        Assert.Equal(
            PortableSetupFailure.Invalid,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor, ClientId.Cursor])));

        // Two overrides naming one file are one destination, whatever they are called.
        var overridden = new PortableSetupService(
            destination.Home, new ConfigWriter(destination.Backups), destination.ParkStore, destination.Ledger,
            destination.Paths,
            pathOverrides: new Dictionary<string, string>
            {
                ["cursor"] = Path.Combine(destination.Home, "one.json"),
                ["vsCode"] = Path.Combine(destination.Home, "ONE.JSON"),
            });
        Assert.Equal(
            PortableSetupFailure.Invalid,
            FailureOf(() => overridden.Preview(source.PortableFile(), [ClientId.Cursor, ClientId.VsCode])));
    }

    [Fact]
    public void ExistingParkedDefinitionsAndCaseInsensitiveInFileNamesRefuseTheImport()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        destination.Write("""{"mcpServers":{"NEW":{"command":"keep-me"}}}""", ".cursor/mcp.json");
        var before = destination.Text(".cursor/mcp.json");
        var service = destination.Portable();

        Assert.Equal(
            PortableSetupFailure.Conflict,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor])));
        Assert.Equal(before, destination.Text(".cursor/mcp.json"));

        destination.ParkStore.Park(ClientId.Codex, "new", "{}");
        var parkedBytes = File.ReadAllBytes(destination.Paths.ParkFile);
        Assert.Equal(
            PortableSetupFailure.Conflict,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Codex])));
        Assert.Equal(parkedBytes, File.ReadAllBytes(destination.Paths.ParkFile));
    }

    [Fact]
    public void AProfileNameThatExistsRefusesTheImportAtPreviewAndAtApply()
    {
        using var source = ExportedSource();
        new ProfileStore(source.Paths).Create("Shared", ["new"]);
        source.ExportTo();
        using var destination = new ToggleHarness();
        var service = destination.Portable();

        var profiles = new ProfileStore(destination.Paths);
        profiles.Create("shared", []);
        Assert.Equal(
            PortableSetupFailure.Conflict,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Codex])));
        profiles.Delete(profiles.All().Single().Id);

        var preview = service.Preview(source.PortableFile(), [ClientId.Codex]);
        profiles.Create("SHARED", []);
        // Either way it is refused: the store changed, or the name is taken.
        Assert.Throws<PortableSetupException>(() => service.Apply(preview.Id));
        Assert.False(destination.Exists(".codex/config.toml"));
    }

    [Fact]
    public void ADamagedOwnStoreStopsThePreviewAndIsNeverTreatedAsEmpty()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var service = destination.Portable();
        Directory.CreateDirectory(destination.Paths.Root);

        File.WriteAllText(destination.Paths.ParkFile, "{ not json");
        var parked = File.ReadAllBytes(destination.Paths.ParkFile);
        Assert.Equal(
            PortableSetupFailure.StoreUnreadable,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor])));
        Assert.Equal(parked, File.ReadAllBytes(destination.Paths.ParkFile));
        File.Delete(destination.Paths.ParkFile);

        File.WriteAllText(destination.Paths.ProfilesFile, "{ not json");
        Assert.Equal(
            PortableSetupFailure.StoreUnreadable,
            FailureOf(() => service.Preview(source.PortableFile(), [ClientId.Cursor])));
        Assert.Equal("{ not json", File.ReadAllText(destination.Paths.ProfilesFile));
    }

    // MARK: - Moving targets

    [Fact]
    public void ExternalChangesInvalidateAPreviewIncludingATargetThatOnlyReceivesAParkedDefinition()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor]);

        destination.Write("""{"mcpServers":{"new":{"command":"external"}}}""", ".cursor/mcp.json");

        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(preview.Id)));
        Assert.Empty(destination.ParkStore.All());
        Assert.Contains("external", destination.Text(".cursor/mcp.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangesToTheSourceFileAProfileSaveOrAParkInvalidateNativePlans()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var service = destination.Portable();

        var first = service.Preview(source.PortableFile(), [ClientId.Codex]);
        File.WriteAllText(source.PortableFile(), "{}");
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(first.Id)));

        source.ExportTo();
        var second = service.Preview(source.PortableFile(), [ClientId.Codex]);
        new ProfileStore(destination.Paths).Create("Other", []);
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(second.Id)));

        var third = service.Preview(source.PortableFile(), [ClientId.Codex]);
        destination.ParkStore.Park(ClientId.Cursor, "unrelated", "{}");
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(third.Id)));

        Assert.False(destination.Exists(".codex/config.toml"));
    }

    [Fact]
    public void ADisappearingOrReappearingDestinationIsStaleToo()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        destination.Write("""{"mcpServers":{}}""", ".cursor/mcp.json");
        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor]);

        File.Delete(Path.Combine(destination.Home, ".cursor", "mcp.json"));

        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(preview.Id)));
    }

    [Fact]
    public void ConcurrentPreviewsCannotSilentlyReplaceEachOthersImportedSetup()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var first = destination.Portable();
        var second = destination.Portable();
        var a = first.Preview(source.PortableFile(), [ClientId.Codex]);
        var b = second.Preview(source.PortableFile(), [ClientId.Codex]);

        var successes = 0;
        Parallel.Invoke(
            () => { try { first.Apply(a.Id); Interlocked.Increment(ref successes); } catch (Exception) { } },
            () => { try { second.Apply(b.Id); Interlocked.Increment(ref successes); } catch (Exception) { } });

        Assert.Equal(1, successes);
        Assert.Single(destination.Discover().Servers, server => server.Name == "new");
    }

    // MARK: - A plan's lifetime

    [Fact]
    public void APlanExpiresAfterTenMinutesAndCancellationReleasesIt()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var now = DateTimeOffset.UtcNow;
        var service = destination.Portable(() => now);

        var expiring = service.Preview(source.PortableFile(), [ClientId.Codex]);
        now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
        Assert.Equal(1, service.RetainedPlanCount);
        now += TimeSpan.FromSeconds(2);
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(expiring.Id)));
        Assert.Equal(0, service.RetainedPlanCount);

        var cancelled = service.Preview(source.PortableFile(), [ClientId.Codex]);
        service.Discard(cancelled.Id);
        service.Discard("never-existed");
        Assert.Equal(0, service.RetainedPlanCount);
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(cancelled.Id)));
        Assert.False(destination.Exists(".codex/config.toml"));
    }

    [Fact]
    public void AtMostFourPlansAreRetainedAndTheOldestGoesFirst()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var now = DateTimeOffset.UtcNow;
        var service = destination.Portable(() => now += TimeSpan.FromSeconds(1));

        var previews = Enumerable.Range(0, 5)
            .Select(_ => service.Preview(source.PortableFile(), [ClientId.Codex]))
            .ToArray();

        Assert.Equal(PortableSetupService.MaximumPlans, service.RetainedPlanCount);
        Assert.Equal(PortableSetupFailure.StalePreview, FailureOf(() => service.Apply(previews[0].Id)));
        service.Apply(previews[4].Id);

        service.DiscardAll();
        Assert.Equal(0, service.RetainedPlanCount);
    }

    // MARK: - Failure and rollback

    [Fact]
    public void ALaterDestinationFailureRestoresEarlierConfigAndDenyListWrites()
    {
        using var source = ExportedSource();
        using var destination = new ToggleHarness();
        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.ClaudeCode, ClientId.Codex]);

        // A file where a folder is needed makes the later Codex commit fail without
        // relying on permissions, installed helpers or any real client files.
        destination.Write("not a directory", ".codex");

        Assert.Throws<IOException>(() => service.Apply(preview.Id));
        Assert.False(destination.Exists(".claude.json"));
        Assert.False(destination.Exists(".claude/settings.json"));
        Assert.Empty(destination.ParkStore.All());
    }

    [Fact]
    public void AFailedConfigCommitPutsBackKytostOwnStoresByteForByte()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        new ProfileStore(source.Paths).Create("Imported", ["new"]);
        source.ExportTo(includeReviewedContracts: false);

        destination.ParkStore.Park(ClientId.Cursor, "old", """{"command":"old"}""");
        new ProfileStore(destination.Paths).Create("Existing", []);
        var parkedBefore = File.ReadAllBytes(destination.Paths.ParkFile);
        var profilesBefore = File.ReadAllBytes(destination.Paths.ProfilesFile);

        // Cursor parks, the profile is imported, then Codex's file cannot be written.
        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor, ClientId.Codex]);
        destination.Write("not a directory", ".codex");

        Assert.Throws<IOException>(() => service.Apply(preview.Id));

        Assert.Equal(parkedBefore, File.ReadAllBytes(destination.Paths.ParkFile));
        Assert.Equal(profilesBefore, File.ReadAllBytes(destination.Paths.ProfilesFile));
        Assert.False(File.Exists(Path.Combine(destination.Paths.Root, "pending-contract-imports.json")));
        Assert.Null(destination.ParkStore.Parked(ClientId.Cursor, "new"));
    }

    [Fact]
    public void AFailureWhileSavingProfilesPutsTheParkStoreBackAndWritesNoConfig()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        new ProfileStore(source.Paths).Create("Imported", ["new"]);
        source.ExportTo();

        destination.ParkStore.Park(ClientId.Cursor, "old", """{"command":"old"}""");
        new ProfileStore(destination.Paths).Create("Existing", []);
        var parkedBefore = File.ReadAllBytes(destination.Paths.ParkFile);
        var profilesBefore = File.ReadAllBytes(destination.Paths.ProfilesFile);
        var service = destination.Portable();
        // Cursor parks the new server (the park store changes first), Codex writes a config.
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor, ClientId.Codex]);

        // Something holds the profile store open, so replacing it fails — after the
        // park store has already been written and before any config is touched.
        using (new FileStream(destination.Paths.ProfilesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => service.Apply(preview.Id));
        }

        Assert.Equal(parkedBefore, File.ReadAllBytes(destination.Paths.ParkFile));
        Assert.Equal(profilesBefore, File.ReadAllBytes(destination.Paths.ProfilesFile));
        Assert.False(destination.Exists(".codex/config.toml"));
        Assert.False(File.Exists(Path.Combine(destination.Paths.Root, "pending-contract-imports.json")));
        Assert.Null(destination.ParkStore.Parked(ClientId.Cursor, "new"));
        Assert.Single(new ProfileStore(destination.Paths).All());
    }

    [Fact]
    public void ADisabledServerKytoIsHoldingExportsAsDisabledAndIsStillNotEnabledByAnImport()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"held":{"command":"npx","env":{"KEY":"secret-value"}}}}""", ".cursor/mcp.json");
        source.Toggles.SetEnabled(false, source.Server("held"), ClientId.Cursor);
        Assert.NotNull(source.ParkStore.Parked(ClientId.Cursor, "held"));

        var summary = source.ExportTo();
        var setup = PortableSetupSerializer.Read(File.ReadAllBytes(source.PortableFile()));
        Assert.Equal(["held"], summary.ServerNames);
        Assert.False(Assert.Single(setup.Servers).WasEnabled);
        Assert.DoesNotContain("secret-value", File.ReadAllText(source.PortableFile()), StringComparison.Ordinal);

        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor], PortableTestSupport.Inputs(summary));
        service.Apply(preview.Id);
        Assert.Equal(
            Kytto.Core.Model.Enablement.Disabled,
            destination.Discover().Servers.First(server => server.Name == "held").EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void ARollbackThatCannotCompleteIsReportedAndNeverHidden()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        source.ExportTo();
        destination.Write("""{"mcpServers":{}}""", ".claude.json");
        destination.Write("""{"deniedMcpServers":[]}""", ".claude/settings.json");

        // The second write fails and so does every write after it, including the
        // attempt to undo the first.
        var service = destination.Portable(fileWriter: new PortableTestSupport.FailingWriter(1, failRollbackToo: true));
        var preview = service.Preview(source.PortableFile(), [ClientId.ClaudeCode]);

        var failure = Assert.Throws<PortableSetupException>(() => service.Apply(preview.Id));

        Assert.Equal(PortableSetupFailure.RollbackFailed, failure.Failure);
        Assert.NotNull(failure.InnerException);
        Assert.Contains("could not completely restore", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreThatCannotBePutBackIsReportedAndNeverHidden()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write("""{"mcpServers":{"new":{"command":"npx"}}}""", ".cursor/mcp.json");
        source.ExportTo();
        destination.Write("""{"mcpServers":{}}""", ".claude.json");
        destination.Write("""{"deniedMcpServers":[]}""", ".claude/settings.json");
        destination.ParkStore.Park(ClientId.Cursor, "old", "{}");

        FileStream? hold = null;
        try
        {
            // The config commit fails, and in that moment something holds the park
            // store open, so Kytto cannot restore it.
            var writer = new PortableTestSupport.FailingWriter(0, onFailure: () =>
                hold = new FileStream(destination.Paths.ParkFile, FileMode.Open, FileAccess.Read, FileShare.None));
            var service = destination.Portable(fileWriter: writer);
            var preview = service.Preview(source.PortableFile(), [ClientId.Cursor, ClientId.ClaudeCode]);

            var failure = Assert.Throws<PortableSetupException>(() => service.Apply(preview.Id));

            Assert.Equal(PortableSetupFailure.RollbackFailed, failure.Failure);
            Assert.Contains("Parked definitions", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    [Fact]
    public void ADisabledDefinitionKeepsItsExactBytesWhenSwitchedOnAfterAnImport()
    {
        using var source = ExportedSource("""{"new":{"command":"npx","args":["-y"]}}""");
        using var destination = new ToggleHarness();
        var service = destination.Portable();
        var preview = service.Preview(source.PortableFile(), [ClientId.Cursor]);
        service.Apply(preview.Id);
        var parked = destination.ParkStore.Parked(ClientId.Cursor, "new")!.SourceText;

        destination.Toggles.SetEnabled(true, destination.Server("new"), ClientId.Cursor);

        // Restored verbatim, and still valid JSON beside whatever else the file holds.
        Assert.Contains(parked, destination.Text(".cursor/mcp.json"), StringComparison.Ordinal);
        Assert.Equal(
            Kytto.Core.Model.Enablement.Enabled,
            destination.Discover().Servers.First(server => server.Name == "new").EnabledIn[ClientId.Cursor]);
    }
}
