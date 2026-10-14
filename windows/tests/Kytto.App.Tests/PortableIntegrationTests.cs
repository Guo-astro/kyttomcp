using System.IO;
using System.Reflection;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Portable;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

/// <summary>
/// Portable setup through the real model and the real command handlers, against
/// throwaway profiles and an in-memory secret store. The native dialogs are replaced
/// by <see cref="FakePrompts"/>; everything behind them is the shipped code (§7.13).
/// </summary>
public sealed class PortableIntegrationTests : IDisposable
{
    private const string TypedSecret = "typed-secret-that-must-stay-native";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-portable-app-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldDataRoot = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
    private readonly List<AppModel> _models = [];
    private readonly IPortableSetupPrompts _oldPrompts = PortableSetupController.Prompts;

    public PortableIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        // A failing command logs; keep that inside this test's folder.
        Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", Path.Combine(_root, "log"));
    }

    public void Dispose()
    {
        PortableSetupController.Prompts = _oldPrompts;
        foreach (var model in _models) model.Dispose();
        Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", _oldDataRoot);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary profile is not worth hiding the assertion result.
        }
    }

    private sealed class Profile
    {
        public required string Home { get; init; }
        public required string Data { get; init; }
        public required AppModel Model { get; init; }
        public required CommandRouter Router { get; init; }

        public string Path(string relative) => System.IO.Path.Combine(Home, relative);

        public void Write(string relative, string text)
        {
            var full = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    private Profile NewProfile(string name, IAppLocator? locator = null)
    {
        var home = Path.Combine(_root, name);
        var data = Path.Combine(home, "KyttoData");
        Directory.CreateDirectory(home);
        var model = new AppModel(
            new KyttoPaths(data), home, locator ?? new AllInstalled(), new InMemorySecretStore(),
            watchConfigs: false);
        _models.Add(model);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);
        return new Profile { Home = home, Data = data, Model = model, Router = router };
    }

    private sealed class AllInstalled : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => true;
        public bool PackageExists(string familyName) => true;
        public bool ExecutableExists(string name, string home) => true;
    }

    /// <summary>Only Codex counts as installed.</summary>
    private sealed class OnlyCodex : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) =>
            name.StartsWith("codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The native dialogs, answered by the test.</summary>
    private sealed class FakePrompts : IPortableSetupPrompts
    {
        public bool? Export { get; set; } = false;
        public string? ExportFile { get; set; }
        public string? ImportFile { get; set; }
        public List<string?> Errors { get; } = [];
        public List<PortableImportSelection> Attempts { get; } = [];
        public Func<PortableSetupSummary, IReadOnlyList<(ClientId Id, string DisplayName)>, Func<PortableImportSelection, string?>, bool>?
            Collect { get; set; }

        public bool? ConfirmExport() => Export;

        public string? ChooseExportFile() => ExportFile;

        public string? ChooseImportFile() => ImportFile;

        public bool CollectInputs(
            PortableSetupSummary summary,
            IReadOnlyList<(ClientId Id, string DisplayName)> targets,
            Func<PortableImportSelection, string?> submit)
        {
            if (Collect is not null) return Collect(summary, targets, Attempt(submit));
            return Attempt(submit)(new PortableImportSelection([ClientId.Codex], Filled(summary))) is null;
        }

        private Func<PortableImportSelection, string?> Attempt(Func<PortableImportSelection, string?> submit) =>
            selection =>
            {
                Attempts.Add(selection);
                var error = submit(selection);
                Errors.Add(error);
                return error;
            };

        public static Dictionary<string, string> Filled(PortableSetupSummary summary) =>
            summary.Requirements.ToDictionary(
                requirement => requirement.Id,
                requirement => requirement.Kind == PortableRequirementKind.Path
                    ? @"C:\mapped\portable-path"
                    : requirement.Label.EndsWith("remote URL", StringComparison.Ordinal)
                        ? "https://example.test/mcp"
                        : requirement.Label.Contains("argument", StringComparison.Ordinal)
                            ? "@modelcontextprotocol/server-memory"
                            : TypedSecret);
    }

    private static JsonElement Data(string reply)
    {
        var document = JsonDocument.Parse(reply);
        Assert.True(
            document.RootElement.GetProperty("ok").GetBoolean(),
            $"the command failed: {reply}");
        return document.RootElement.GetProperty("data");
    }

    private static string ErrorCode(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private async Task<string> ExportedFrom(Profile source, bool includeContracts = false)
    {
        var file = Path.Combine(source.Home, "setup.kytto.json");
        PortableSetupController.Prompts = new FakePrompts { Export = includeContracts, ExportFile = file };
        var reply = await source.Router.InvokeForTests("setup.export");
        Assert.False(Data(reply).GetProperty("cancelled").GetBoolean());
        return file;
    }

    private static void SeedSource(Profile source) => source.Write(
        @".cursor\mcp.json",
        """{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"FIXTURE_TOKEN":"fixture-secret-do-not-export"}}}}""");

    [Fact]
    public async Task NativePreviewKeepsInputsNativeAndImportsDisabledWithoutChangingTheSource()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        source.Model.Reload();
        var original = File.ReadAllBytes(source.Path(@".cursor\mcp.json"));

        var file = await ExportedFrom(source);
        Assert.DoesNotContain("fixture-secret-do-not-export", File.ReadAllText(file), StringComparison.Ordinal);

        var prompts = new FakePrompts { ImportFile = file };
        PortableSetupController.Prompts = prompts;
        var previewReply = await destination.Router.InvokeForTests("setup.previewImport");
        var preview = Data(previewReply);

        // Nothing a person typed, and no path or command from the file, is in the reply.
        foreach (var forbidden in new[] { TypedSecret, destination.Home, source.Home, "npx", "fixture-secret" })
        {
            Assert.DoesNotContain(forbidden, previewReply, StringComparison.Ordinal);
        }
        Assert.Equal(
            ["id", "serverNames", "profileNames", "targetClients", "pathDisplays",
             "pendingContractCount", "warnings", "enabled"],
            preview.EnumerateObject().Select(property => property.Name));
        Assert.False(preview.GetProperty("enabled").GetBoolean());
        Assert.Equal(["memory"], preview.GetProperty("serverNames").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["codex"], preview.GetProperty("targetClients").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(destination.Model.Current().Servers);

        var id = preview.GetProperty("id").GetString()!;
        var applied = Data(await destination.Router.InvokeForTests(
            "setup.applyImport", $$"""{ "previewID": "{{id}}" }"""));

        var servers = applied.GetProperty("servers").EnumerateArray().ToArray();
        var imported = Assert.Single(servers);
        Assert.Equal("disabled", imported.GetProperty("enabledIn").GetProperty("codex").GetString());
        Assert.Equal(original, File.ReadAllBytes(source.Path(@".cursor\mcp.json")));
        Assert.Contains(
            TypedSecret,
            File.ReadAllText(destination.Path(@".codex\config.toml")),
            StringComparison.Ordinal);

        // Single use.
        Assert.Equal(
            "portableSetup",
            ErrorCode(await destination.Router.InvokeForTests(
                "setup.applyImport", $$"""{ "previewID": "{{id}}" }""")));
    }

    [Fact]
    public async Task EveryCancellationIsAQuietNullAndNeverAnError()
    {
        var source = NewProfile("source");
        SeedSource(source);
        source.Model.Reload();

        PortableSetupController.Prompts = new FakePrompts { Export = null };
        Assert.True(Data(await source.Router.InvokeForTests("setup.export")).GetProperty("cancelled").GetBoolean());

        PortableSetupController.Prompts = new FakePrompts { Export = true, ExportFile = null };
        Assert.True(Data(await source.Router.InvokeForTests("setup.export")).GetProperty("cancelled").GetBoolean());
        Assert.False(File.Exists(source.Path("setup.kytto.json")));

        PortableSetupController.Prompts = new FakePrompts { ImportFile = null };
        Assert.Equal(JsonValueKind.Null, Data(await source.Router.InvokeForTests("setup.previewImport")).ValueKind);

        var file = await ExportedFrom(source);
        PortableSetupController.Prompts = new FakePrompts
        {
            ImportFile = file,
            Collect = (_, _, _) => false,
        };
        Assert.Equal(JsonValueKind.Null, Data(await source.Router.InvokeForTests("setup.previewImport")).ValueKind);
    }

    [Fact]
    public async Task CancellingAfterAPlanWasMadeReleasesItsInputs()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        source.Model.Reload();
        var file = await ExportedFrom(source);

        PortableSetupController.Prompts = new FakePrompts
        {
            ImportFile = file,
            Collect = (summary, _, submit) =>
            {
                Assert.Null(submit(new PortableImportSelection([ClientId.Codex], FakePrompts.Filled(summary))));
                return false;
            },
        };
        Assert.Equal(
            JsonValueKind.Null,
            Data(await destination.Router.InvokeForTests("setup.previewImport")).ValueKind);

        Assert.Equal(0, destination.Model.PortableSetup().RetainedPlanCount);
    }

    [Fact]
    public async Task ARefusedInputIsAnErrorForTheWindowNotAnExceptionAndNeverEchoesWhatWasTyped()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        source.Model.Reload();
        var file = await ExportedFrom(source);

        var prompts = new FakePrompts { ImportFile = file };
        prompts.Collect = (summary, _, submit) =>
        {
            var values = FakePrompts.Filled(summary);
            // Missing one input, then the whole form again with the same values.
            var secret = summary.Requirements.First(requirement => requirement.IsSecret);
            var incomplete = new Dictionary<string, string>(values) { [secret.Id] = "" };
            var first = submit(new PortableImportSelection([ClientId.Codex], incomplete));
            Assert.NotNull(first);
            Assert.DoesNotContain(TypedSecret, first, StringComparison.Ordinal);
            Assert.Contains("environment", first, StringComparison.Ordinal);
            return submit(new PortableImportSelection([ClientId.Codex], values)) is null;
        };
        PortableSetupController.Prompts = prompts;

        var preview = Data(await destination.Router.InvokeForTests("setup.previewImport"));

        Assert.Equal(2, prompts.Attempts.Count);
        Assert.False(string.IsNullOrEmpty(preview.GetProperty("id").GetString()));
        // Both attempts saw the same typed inputs apart from the one the test blanked.
        Assert.Equal(prompts.Attempts[0].Values.Count, prompts.Attempts[1].Values.Count);
    }

    [Fact]
    public async Task OnlyWritableInstalledBuiltInClientsAreTargets()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination", new OnlyCodex());
        SeedSource(source);
        source.Model.Reload();
        var file = await ExportedFrom(source);

        Assert.Equal([ClientId.Codex], destination.Model.PortableTargets().Select(target => target.Id));

        var prompts = new FakePrompts
        {
            ImportFile = file,
            Collect = (summary, targets, submit) =>
            {
                Assert.Equal([ClientId.Codex], targets.Select(target => target.Id));
                var error = submit(new PortableImportSelection([ClientId.Cursor], FakePrompts.Filled(summary)));
                Assert.Contains("writable target", error, StringComparison.Ordinal);
                return false;
            },
        };
        PortableSetupController.Prompts = prompts;
        Assert.Equal(JsonValueKind.Null, Data(await destination.Router.InvokeForTests("setup.previewImport")).ValueKind);
    }

    [Fact]
    public async Task ASetupWithServersAndNoInstalledClientIsRefusedClearly()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination", new NoApps());
        SeedSource(source);
        source.Model.Reload();
        PortableSetupController.Prompts = new FakePrompts { ImportFile = await ExportedFrom(source) };

        var reply = await destination.Router.InvokeForTests("setup.previewImport");

        Assert.Equal("portableSetup", ErrorCode(reply));
    }

    private sealed class NoApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    [Fact]
    public async Task ReconfiguringInvalidatesPlansButAnUnrelatedSettingDoesNot()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        source.Model.Reload();
        var file = await ExportedFrom(source);
        PortableSetupController.Prompts = new FakePrompts { ImportFile = file };

        var first = Data(await destination.Router.InvokeForTests("setup.previewImport")).GetProperty("id").GetString()!;
        destination.Model.UpdateSettings(settings => settings with { SidebarWidth = 300 });
        Assert.Equal(1, destination.Model.PortableSetup().RetainedPlanCount);
        Data(await destination.Router.InvokeForTests("setup.applyImport", $$"""{ "previewID": "{{first}}" }"""));

        var second = NewProfile("second-destination");
        var preview = Data(await second.Router.InvokeForTests("setup.previewImport")).GetProperty("id").GetString()!;
        second.Model.UpdateSettings(settings => settings with
        {
            ClientPathOverrides = new Dictionary<string, string> { ["codex"] = second.Path("elsewhere.toml") },
        });

        Assert.Equal(0, second.Model.PortableSetup().RetainedPlanCount);
        Assert.Equal(
            "portableSetup",
            ErrorCode(await second.Router.InvokeForTests("setup.applyImport", $$"""{ "previewID": "{{preview}}" }""")));
        Assert.False(File.Exists(second.Path("elsewhere.toml")));
    }

    [Fact]
    public async Task DiscardReleasesAPlanAndAnUnknownIdIsHarmless()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        source.Model.Reload();
        PortableSetupController.Prompts = new FakePrompts { ImportFile = await ExportedFrom(source) };
        var id = Data(await destination.Router.InvokeForTests("setup.previewImport")).GetProperty("id").GetString()!;

        Assert.Equal(
            JsonValueKind.Object,
            Data(await destination.Router.InvokeForTests("setup.discardImport", $$"""{ "previewID": "{{id}}" }""")).ValueKind);
        Data(await destination.Router.InvokeForTests("setup.discardImport", """{ "previewID": "nothing" }"""));

        Assert.Equal(
            "portableSetup",
            ErrorCode(await destination.Router.InvokeForTests(
                "setup.applyImport", $$"""{ "previewID": "{{id}}" }""")));
        Assert.False(File.Exists(destination.Path(@".codex\config.toml")));
    }

    [Fact]
    public void NoCommandCanAskForAnImportToBeEnabled()
    {
        // The whole of what the web layer can send about an apply or a discard.
        Assert.Equal(
            ["PreviewID"],
            typeof(PortableApplyPayload).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name));
        Assert.Equal(
            ["Id", "ServerNames", "ProfileNames", "TargetClients", "PathDisplays",
             "PendingContractCount", "Warnings", "Enabled"],
            typeof(PortableImportPreviewDto).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));

        var preview = new PortableImportPreview("id", [], [], [], [], 0, [], false);
        Assert.False(preview.ToDto().Enabled);
    }

    // MARK: - Imported contract references

    [Fact]
    public async Task UnreadableImportedFingerprintsCannotPartiallyAcknowledgeALocalAlert()
    {
        var profile = NewProfile("local");
        profile.Write(@".cursor\mcp.json", """{"mcpServers":{"memory":{"command":"node"}}}""");
        var metadata = new MetadataStore(new KyttoPaths(profile.Data));
        foreach (var description in new[] { "before", "after" })
        {
            metadata.Record(
                "memory",
                new HealthResult(
                    HealthStatus.Passed, Timestamp.Now, 1, [new ToolSummary("read", description)],
                    null, null, 0, null, null),
                null);
        }
        File.WriteAllText(Path.Combine(profile.Data, "pending-contract-imports.json"), "malformed");
        profile.Model.Reload();
        var baseline = new MetadataStore(new KyttoPaths(profile.Data)).For("memory")?.ContractTools;

        Assert.Throws<PortableSetupException>(() => profile.Model.AcknowledgeContract("memory"));

        var after = new MetadataStore(new KyttoPaths(profile.Data)).For("memory");
        Assert.Equal(baseline, after?.ContractTools);
        Assert.NotEmpty(after?.ContractChanges ?? []);
        Assert.Equal("malformed", File.ReadAllText(Path.Combine(profile.Data, "pending-contract-imports.json")));

        // The state says so, safely, and still lists nothing it could not read.
        var state = Data(await profile.Router.InvokeForTests("state.get"));
        var pending = state.GetProperty("pendingContractImports");
        Assert.Empty(pending.GetProperty("entries").EnumerateArray());
        var message = pending.GetProperty("error").GetString();
        Assert.Contains("could not be read", message, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshLocalCheckAfterTheImportIsWhatLetsAnImportedReferenceBeReviewed()
    {
        var source = NewProfile("source");
        var destination = NewProfile("destination");
        SeedSource(source);
        var tool = new ToolSummary("read", "Read a file", """{"type":"object"}""");
        new MetadataStore(new KyttoPaths(source.Data)).Record(
            "memory",
            new HealthResult(HealthStatus.Passed, Timestamp.Now, 1, [tool], null, null, 0, null, null),
            null);
        source.Model.Reload();
        var file = await ExportedFrom(source, includeContracts: true);

        PortableSetupController.Prompts = new FakePrompts { ImportFile = file };
        var id = Data(await destination.Router.InvokeForTests("setup.previewImport")).GetProperty("id").GetString()!;
        var applied = Data(await destination.Router.InvokeForTests(
            "setup.applyImport", $$"""{ "previewID": "{{id}}" }"""));

        var pending = applied.GetProperty("pendingContractImports");
        Assert.Equal(["entries", "error"], pending.EnumerateObject().Select(property => property.Name));
        var entry = Assert.Single(pending.GetProperty("entries").EnumerateArray());
        Assert.Equal(["serverID", "toolCount", "matching"], entry.EnumerateObject().Select(property => property.Name));
        Assert.Equal("memory", entry.GetProperty("serverID").GetString());
        Assert.Equal(1, entry.GetProperty("toolCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("matching").ValueKind);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("error").ValueKind);
        // No imported hash was copied into local trust.
        Assert.Null(new MetadataStore(new KyttoPaths(destination.Data)).For("memory"));

        // Nothing has been checked here, so a review is refused and nothing moves.
        Assert.Equal(
            "portableSetup",
            ErrorCode(await destination.Router.InvokeForTests("contract.acknowledge", """{ "serverID": "memory" }""")));
        Assert.Single(destination.Model.PendingContractImports().Entries);

        new MetadataStore(new KyttoPaths(destination.Data)).Record(
            "memory",
            new HealthResult(HealthStatus.Passed, Timestamp.Now + TimeSpan.FromSeconds(1), 1, [tool], null, null, 0, null, null),
            null);
        var compared = Data(await destination.Router.InvokeForTests("state.get"))
            .GetProperty("pendingContractImports").GetProperty("entries")[0];
        Assert.True(compared.GetProperty("matching").GetBoolean());

        var reviewed = Data(await destination.Router.InvokeForTests(
            "contract.acknowledge", """{ "serverID": "memory" }"""));
        Assert.Empty(reviewed.GetProperty("pendingContractImports").GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task TheCapabilityIsAdvertisedBecauseTheNativePathExists()
    {
        var profile = NewProfile("local");
        // No dialog may open from a test: every prompt answers "cancel".
        PortableSetupController.Prompts = new FakePrompts { Export = null };
        var info = Data(await profile.Router.InvokeForTests("app.info"));

        Assert.Contains(
            "portable-setup",
            info.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        // The commands the capability promises are registered.
        foreach (var command in new[] { "setup.export", "setup.previewImport", "setup.applyImport", "setup.discardImport" })
        {
            using var reply = JsonDocument.Parse(await profile.Router.InvokeForTests(command, "{}"));
            var code = reply.RootElement.TryGetProperty("error", out var error)
                ? error.GetProperty("code").GetString()
                : "";
            Assert.NotEqual("unknownCommand", code);
        }
    }
}
