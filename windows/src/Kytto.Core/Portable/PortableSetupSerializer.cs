using System.Globalization;
using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.Core.Portable;

/// <summary>
/// Reads and writes the portable file by hand, and refuses everything it does not
/// recognise (§7.13).
/// </summary>
/// <remarks>
/// <para>
/// Not a generic JSON binder. A file that arrives from somebody else is untrusted
/// input to a program that writes other programs' configuration, so the shape is
/// checked field by field: strict JSON (no comments, no trailing commas), valid
/// UTF-8, every object a closed set of known keys, no key twice, every type exactly
/// the declared one. Anything else is <see cref="PortableSetupException"/> before a
/// single value is looked at.
/// </para>
/// <para>
/// Written with sorted keys and two-space indentation, so two exports of the same
/// setup differ only in their opaque requirement ids.
/// </para>
/// </remarks>
internal static class PortableSetupSerializer
{
    private const string Unsupported = "invalid JSON or unsupported fields";

    private static readonly string[] RootKeys =
        ["format", "version", "servers", "profiles", "requirements", "reviewedContracts"];

    private static readonly string[] ServerKeys =
    [
        "name", "sourceClient", "wasEnabled", "transport", "command", "args",
        "environment", "headers", "url", "workingDirectory",
    ];

    private static readonly string[] ValueKeys = ["literal", "requirementID"];

    // MARK: - Reading

    public static PortableSetup Read(byte[] data)
    {
        var bytes = data.AsSpan();
        // A byte-order mark is an encoding marker some Windows editors add, not part
        // of the document, so it is skipped rather than refused.
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            bytes = bytes[3..];
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw PortableSetupException.Invalid("UTF-8 is required");
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw PortableSetupException.Invalid(Unsupported);
        }

        using (document)
        {
            try
            {
                return DecodeRoot(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // A string holding a lone surrogate escape parses but cannot be read as
                // text. That is a file this format does not allow, not an internal fault.
                throw PortableSetupException.Invalid(Unsupported);
            }
        }
    }

    private static PortableSetup DecodeRoot(JsonElement root)
    {
        var members = Members(root, RootKeys);
        return new PortableSetup(
            Format: String(Required(members, "format")),
            Version: Integer(Required(members, "version")),
            Servers: Array(Required(members, "servers")).Select(DecodeServer).ToArray(),
            Profiles: Array(Required(members, "profiles")).Select(DecodeProfile).ToArray(),
            Requirements: Array(Required(members, "requirements")).Select(DecodeRequirement).ToArray(),
            ReviewedContracts: Array(Required(members, "reviewedContracts")).Select(DecodeContract).ToArray());
    }

    private static PortableServer DecodeServer(JsonElement element)
    {
        var members = Members(element, ServerKeys);
        var sourceClient = ClientIds.FromRaw(String(Required(members, "sourceClient")))
            ?? throw PortableSetupException.Invalid("invalid or duplicate server");
        var transport = Transports.FromRaw(String(Required(members, "transport")))
            ?? throw PortableSetupException.Invalid(Unsupported);

        return new PortableServer(
            Name: String(Required(members, "name")),
            SourceClient: sourceClient,
            WasEnabled: Boolean(Required(members, "wasEnabled")),
            Transport: transport,
            Command: OptionalValue(members, "command"),
            Args: Array(Required(members, "args")).Select(DecodeValue).ToArray(),
            Environment: ValueMap(Required(members, "environment")),
            Headers: ValueMap(Required(members, "headers")),
            Url: OptionalValue(members, "url"),
            WorkingDirectory: OptionalValue(members, "workingDirectory"));
    }

    private static PortableValue? OptionalValue(Dictionary<string, JsonElement> members, string key) =>
        members.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? DecodeValue(value)
            : null;

    private static PortableValue DecodeValue(JsonElement element)
    {
        var members = Members(element, ValueKeys);
        return new PortableValue(
            OptionalString(members, "literal"),
            OptionalString(members, "requirementID"));
    }

    private static IReadOnlyDictionary<string, PortableValue> ValueMap(JsonElement element)
    {
        var result = new Dictionary<string, PortableValue>(StringComparer.Ordinal);
        foreach (var (key, value) in Members(element, null)) result[key] = DecodeValue(value);
        return result;
    }

    private static PortableProfile DecodeProfile(JsonElement element)
    {
        var members = Members(element, ["name", "serverIDs", "tokenBudget"]);
        int? budget = members.TryGetValue("tokenBudget", out var raw) && raw.ValueKind != JsonValueKind.Null
            ? Integer(raw)
            : null;
        return new PortableProfile(
            String(Required(members, "name")),
            Array(Required(members, "serverIDs")).Select(String).ToArray(),
            budget);
    }

    private static PortableRequirement DecodeRequirement(JsonElement element)
    {
        var members = Members(element, ["id", "label", "kind"]);
        var kind = String(Required(members, "kind")) switch
        {
            "secret" => PortableRequirementKind.Secret,
            "path" => PortableRequirementKind.Path,
            "value" => PortableRequirementKind.Value,
            _ => throw PortableSetupException.Invalid(Unsupported),
        };
        return new PortableRequirement(String(Required(members, "id")), String(Required(members, "label")), kind);
    }

    private static PortableContractReference DecodeContract(JsonElement element)
    {
        var members = Members(element, ["serverID", "toolDigests"]);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Members(Required(members, "toolDigests"), null))
        {
            digests[key] = String(value);
        }
        return new PortableContractReference(String(Required(members, "serverID")), digests);
    }

    // MARK: - Strict primitives

    /// <summary>
    /// An object's members by key. A key twice, or one outside <paramref name="allowed"/>,
    /// is a refusal — a duplicate is exactly how a value a reviewer saw differs from
    /// the one a program reads.
    /// </summary>
    private static Dictionary<string, JsonElement> Members(JsonElement element, string[]? allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw PortableSetupException.Invalid("expected an object");

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value))
            {
                throw PortableSetupException.Invalid("duplicate JSON key");
            }
            if (allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw PortableSetupException.Invalid("unknown field");
            }
        }
        return result;
    }

    private static JsonElement Required(Dictionary<string, JsonElement> members, string key) =>
        members.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw PortableSetupException.Invalid(Unsupported);

    private static string? OptionalString(Dictionary<string, JsonElement> members, string key) =>
        members.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? String(value)
            : null;

    private static string String(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? throw PortableSetupException.Invalid(Unsupported)
            : throw PortableSetupException.Invalid(Unsupported);

    private static bool Boolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw PortableSetupException.Invalid(Unsupported),
    };

    /// <summary>An integer, written as one: <c>1.0</c> and <c>1e0</c> are not.</summary>
    private static int Integer(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw PortableSetupException.Invalid(Unsupported);

    private static List<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToList()
            : throw PortableSetupException.Invalid(Unsupported);

    // MARK: - Writing

    public static byte[] Write(PortableSetup setup)
    {
        var text = new StringBuilder();
        var root = new Writer(text);
        root.Object(
        [
            ("format", w => w.String(setup.Format)),
            ("profiles", w => w.Array(setup.Profiles.Select<PortableProfile, Action<Writer>>(profile => p => WriteProfile(p, profile)))),
            ("requirements", w => w.Array(setup.Requirements.Select<PortableRequirement, Action<Writer>>(item => p => WriteRequirement(p, item)))),
            ("reviewedContracts", w => w.Array(setup.ReviewedContracts.Select<PortableContractReference, Action<Writer>>(item => p => WriteContract(p, item)))),
            ("servers", w => w.Array(setup.Servers.Select<PortableServer, Action<Writer>>(server => p => WriteServer(p, server)))),
            ("version", w => w.Raw(setup.Version.ToString(CultureInfo.InvariantCulture))),
        ]);
        text.Append('\n');
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    private static void WriteServer(Writer writer, PortableServer server)
    {
        var members = new List<(string, Action<Writer>)>
        {
            ("args", w => w.Array(server.Args.Select<PortableValue, Action<Writer>>(value => p => WriteValue(p, value)))),
            ("environment", w => WriteValueMap(w, server.Environment)),
            ("headers", w => WriteValueMap(w, server.Headers)),
            ("name", w => w.String(server.Name)),
            ("sourceClient", w => w.String(server.SourceClient.Raw())),
            ("transport", w => w.String(server.Transport.Raw())),
            ("wasEnabled", w => w.Raw(server.WasEnabled ? "true" : "false")),
        };
        if (server.Command is { } command) members.Add(("command", w => WriteValue(w, command)));
        if (server.Url is { } url) members.Add(("url", w => WriteValue(w, url)));
        if (server.WorkingDirectory is { } cwd) members.Add(("workingDirectory", w => WriteValue(w, cwd)));
        writer.Object(members.OrderBy(member => member.Item1, StringComparer.Ordinal).ToArray());
    }

    private static void WriteValueMap(Writer writer, IReadOnlyDictionary<string, PortableValue> values) =>
        writer.Object(values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select<KeyValuePair<string, PortableValue>, (string, Action<Writer>)>(
                pair => (pair.Key, w => WriteValue(w, pair.Value)))
            .ToArray());

    private static void WriteValue(Writer writer, PortableValue value)
    {
        var members = new List<(string, Action<Writer>)>();
        if (value.Literal is { } literal) members.Add(("literal", w => w.String(literal)));
        if (value.RequirementID is { } id) members.Add(("requirementID", w => w.String(id)));
        writer.Object(members.ToArray());
    }

    private static void WriteProfile(Writer writer, PortableProfile profile)
    {
        var members = new List<(string, Action<Writer>)>
        {
            ("name", w => w.String(profile.Name)),
            ("serverIDs", w => w.Array(profile.ServerIDs.Select<string, Action<Writer>>(id => p => p.String(id)))),
        };
        if (profile.TokenBudget is { } budget)
        {
            members.Add(("tokenBudget", w => w.Raw(budget.ToString(CultureInfo.InvariantCulture))));
        }
        writer.Object(members.OrderBy(member => member.Item1, StringComparer.Ordinal).ToArray());
    }

    private static void WriteRequirement(Writer writer, PortableRequirement requirement) =>
        writer.Object(
        [
            ("id", w => w.String(requirement.Id)),
            ("kind", w => w.String(requirement.KindRaw)),
            ("label", w => w.String(requirement.Label)),
        ]);

    private static void WriteContract(Writer writer, PortableContractReference contract) =>
        writer.Object(
        [
            ("serverID", w => w.String(contract.ServerID)),
            ("toolDigests", w => w.Object(contract.ToolDigests
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select<KeyValuePair<string, string>, (string, Action<Writer>)>(
                    pair => (pair.Key, p => p.String(pair.Value)))
                .ToArray())),
        ]);

    /// <summary>Just enough of a JSON writer to be sure what it emits.</summary>
    private sealed class Writer(StringBuilder text)
    {
        private int _depth;

        public void Raw(string value) => text.Append(value);

        public void String(string value)
        {
            text.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    case < ' ':
                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        break;
                    default: text.Append(character); break;
                }
            }
            text.Append('"');
        }

        public void Object((string Key, Action<Writer> Write)[] members)
        {
            if (members.Length == 0)
            {
                text.Append("{}");
                return;
            }
            text.Append('{');
            _depth++;
            for (var index = 0; index < members.Length; index++)
            {
                text.Append(index == 0 ? "\n" : ",\n").Append(' ', _depth * 2);
                String(members[index].Key);
                text.Append(": ");
                members[index].Write(this);
            }
            _depth--;
            text.Append('\n').Append(' ', _depth * 2).Append('}');
        }

        public void Array(IEnumerable<Action<Writer>> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
            {
                text.Append("[]");
                return;
            }
            text.Append('[');
            _depth++;
            for (var index = 0; index < list.Count; index++)
            {
                text.Append(index == 0 ? "\n" : ",\n").Append(' ', _depth * 2);
                list[index](this);
            }
            _depth--;
            text.Append('\n').Append(' ', _depth * 2).Append(']');
        }
    }
}
