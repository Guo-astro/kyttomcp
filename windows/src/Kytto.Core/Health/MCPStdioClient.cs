using System.Text.Json;
using Kytto.Core.Json;
using Kytto.Core.Tokenizer;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Health;

/// <param name="RawToolsJson">
/// The <c>tools</c> array exactly as the server sent it. Kept verbatim because
/// token weight is measured on what a model would actually be shown, not on a
/// re-serialization of Kytto's own model.
/// </param>
public sealed record ToolListing(
    IReadOnlyList<ToolSummary> Tools,
    string RawToolsJson,
    string? ServerName,
    string? ServerVersion,
    string? NegotiatedProtocolVersion,
    IReadOnlyList<string> CapabilityNames,
    int? PromptCount,
    int? ResourceCount,
    IReadOnlyList<string> InspectionNotes,
    string ResolvedCommand);

public sealed class McpException(HealthFailureReason failure) : Exception(failure.CurrentMessage())
{
    public HealthFailureReason Failure { get; } = failure;

    public static McpException Protocol(string detail) =>
        new(HealthFailureReason.ProtocolError(detail));

    public static McpException ServerError(int code, string message) =>
        new(HealthFailureReason.ServerRefused(code, message));

    public static McpException ClosedBeforeAnswering() =>
        new(HealthFailureReason.ClosedBeforeAnswering());

    public static McpException Exited(int status) =>
        new(HealthFailureReason.Exited(status));
}

/// <summary>An error with the server's own output attached.</summary>
public sealed class HealthFailure(Exception underlying, string stderr, string? resolvedCommand = null)
    : Exception(underlying.Message)
{
    public Exception Underlying { get; } = underlying;
    public string Stderr { get; } = stderr;
    public string? ResolvedCommand { get; } = resolvedCommand;
}

/// <summary>
/// The smallest MCP client that can answer "does this server work, and how much
/// context do its tools cost?" (§7.3, §7.4).
/// </summary>
/// <remarks>
/// Deliberately hand-written rather than pulled in as a dependency: the whole
/// exchange is four messages, and doing it directly keeps full control over the
/// timeout and over capturing stderr, which is the part users actually need when
/// something is broken.
/// </remarks>
public sealed class McpStdioClient
{
    /// <summary>The protocol revision Kytto asks for.</summary>
    /// <remarks>
    /// A server that speaks a different one answers with its own, and for listing
    /// tools that is fine — so the negotiated value is recorded, not enforced.
    /// </remarks>
    public const string PreferredProtocolVersion = "2026-07-28";

    public (ToolListing Listing, string Stderr) ListTools(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        using var process = new ManagedProcess(command, arguments, environment);
        process.SetTimeoutBudget(timeout);
        var deadline = DateTimeOffset.UtcNow + timeout;

        try
        {
            process.WriteLine(InitializeRequest());
            var initialize = ReadResponse(1, process, deadline);

            process.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            process.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            var tools = ReadResponse(2, process, deadline);
            var notes = new List<string>();
            var pages = MorePages(tools, process, deadline, notes);

            var capabilityNames = initialize.ValueAt("result", "capabilities")?.Keys
                .Order(StringComparer.Ordinal)
                .ToArray() ?? [];
            var promptCount = capabilityNames.Contains("prompts", StringComparer.Ordinal)
                ? ReadOptionalCount("prompts", 3, process, deadline, notes)
                : null;
            var resourceCount = capabilityNames.Contains("resources", StringComparer.Ordinal)
                ? ReadOptionalCount("resources", 4, process, deadline, notes)
                : null;

            return (MakeListing(
                initialize,
                pages,
                capabilityNames,
                promptCount,
                resourceCount,
                notes,
                process.ResolvedExecutable), process.CapturedStderr);
        }
        catch (Exception error)
        {
            // Whatever went wrong, the server's own words explain it better than
            // ours, so they travel with the error.
            throw new HealthFailure(error, process.CapturedStderr, process.ResolvedExecutable);
        }
    }

    /// <summary>Pages <c>tools/list</c> until the server stops returning a cursor.</summary>
    /// <remarks>
    /// Unlike prompts and resources, the tool list is what everything else is
    /// measured on — token weight, the safety scan, Contract Guard — so stopping at
    /// the first page would make all of them blind to the rest. A page that cannot be
    /// fetched ends the walk with a note rather than failing a server that did answer.
    /// </remarks>
    private static IReadOnlyList<JsonDocument> MorePages(
        JsonDocument first,
        ManagedProcess process,
        DateTimeOffset deadline,
        ICollection<string> notes)
    {
        var pages = new List<JsonDocument> { first };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pages[^1].ValueAt("result", "nextCursor")?.StringValue is { } cursor)
        {
            if (!seen.Add(cursor) || pages.Count >= MaxToolPages)
            {
                notes.Add($"Tool count shows the first {pages.Count} pages; the server kept offering more.");
                break;
            }
            var id = 100 + pages.Count;
            try
            {
                process.WriteLine(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method = "tools/list",
                    @params = new { cursor },
                }));
                var page = ReadResponse(id, process, deadline);
                if (page.ValueAt("result", "tools")?.Elements is null)
                {
                    notes.Add(
                        $"tools/list page {pages.Count + 1} did not return a tools array; " +
                        $"counts cover the first {pages.Count}.");
                    break;
                }
                pages.Add(page);
            }
            catch (Exception error) when (error is McpException or ProcessException)
            {
                notes.Add($"Tool count shows the first {pages.Count} pages; the next could not be read.");
                break;
            }
        }
        return pages;
    }

    private const int MaxToolPages = 50;

    private static int? ReadOptionalCount(
        string kind,
        int id,
        ManagedProcess process,
        DateTimeOffset deadline,
        ICollection<string> notes)
    {
        try
        {
            process.WriteLine(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method = $"{kind}/list",
                @params = new { },
            }));
            var response = ReadResponse(id, process, deadline);
            var result = response.ValueAt("result");
            var count = result?[kind]?.Elements?.Count;
            if (count is null)
            {
                notes.Add($"{kind}/list did not return a {kind} array.");
                return null;
            }
            if (result?["nextCursor"] is { IsNull: false })
            {
                notes.Add($"{kind}/list has more pages; this count is for the first page.");
            }
            return count;
        }
        catch (Exception error) when (error is McpException or ProcessException)
        {
            // Optional inspector data must not turn a successful tools handshake
            // into a failed health check.
            notes.Add($"{kind}/list: {error.Message}");
            return null;
        }
    }

    private static string InitializeRequest() => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = PreferredProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "Kytto", version = "1.0" },
        },
    });

    /// <summary>Reads until the response with this id arrives.</summary>
    /// <remarks>
    /// Skips anything else on the way: notifications, requests the server makes of
    /// us, and plain text. Servers are not supposed to print to stdout, but enough
    /// of them log a banner there that refusing to tolerate it would fail perfectly
    /// working servers.
    /// </remarks>
    private static JsonDocument ReadResponse(int id, ManagedProcess process, DateTimeOffset deadline)
    {
        while (true)
        {
            if (process.ReadLine(deadline) is not { } line)
            {
                throw process.HasExited
                    ? McpException.Exited(process.ExitCode)
                    : McpException.ClosedBeforeAnswering();
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (document.Root["id"]?.NumberValue is not { } responseId) continue;
            if (responseId != id) continue;
            if (document.Root["jsonrpc"]?.StringValue != "2.0")
            {
                throw McpException.Protocol($"response {id} did not declare JSON-RPC 2.0");
            }

            if (document.Root["error"] is { } error)
            {
                throw McpException.ServerError(
                    (int)(error["code"]?.NumberValue ?? 0),
                    error["message"]?.StringValue ?? "no message");
            }
            if (document.Root["result"] is null)
            {
                throw McpException.Protocol($"response {id} had neither result nor error");
            }
            return document;
        }
    }

    private static ToolListing MakeListing(
        JsonDocument initialize,
        IReadOnlyList<JsonDocument> pages,
        IReadOnlyList<string> capabilityNames,
        int? promptCount,
        int? resourceCount,
        IReadOnlyList<string> notes,
        string resolvedCommand)
    {
        var tools = pages[0];
        var node = tools.ValueAt("result", "tools");
        if (node?.Elements is null)
        {
            throw McpException.Protocol("tools/list did not return a tools array");
        }
        var entries = pages
            .SelectMany(page => (page.ValueAt("result", "tools")?.Elements ?? [])
                .Select(tool => (Page: page, Tool: tool)))
            .ToArray();

        // Measured on each tool's verbatim entry rather than on a re-serialization
        // of Kytto's model, for the same reason `RawToolsJson` is kept: the cost is
        // what the client hands the model, whitespace and key order included. The
        // per-tool numbers therefore sum to slightly less than the whole-array
        // weight — the brackets and separators between them belong to no single
        // tool.
        var tokenizer = BpeTokenizer.Cl100kBase;
        var summaries = entries
            .Select(entry =>
            {
                // Last-wins, as the client that hands these to the model reads them.
                var tool = entry.Tool;
                var annotations = tool.LastValue("annotations");
                return new ToolSummary(
                    tool.LastValue("name")?.StringValue ?? "(unnamed)",
                    tool.LastValue("description")?.StringValue,
                    tool.LastValue("inputSchema") is { } schema ? entry.Page.Slice(schema.Span) : "{}",
                    new ToolAnnotations(
                        annotations?.LastValue("readOnlyHint")?.BoolValue,
                        annotations?.LastValue("destructiveHint")?.BoolValue,
                        annotations?.LastValue("idempotentHint")?.BoolValue,
                        annotations?.LastValue("openWorldHint")?.BoolValue),
                    tokenizer?.CountTokens(entry.Page.Slice(tool.Span)));
            })
            .ToArray();

        return new ToolListing(
            Tools: summaries,
            // One page: the exact bytes. Several: the entries as sent, in one array,
            // which is what a client that pages hands the model.
            RawToolsJson: pages.Count == 1
                ? tools.Slice(node.Span)
                : "[" + string.Join(",", entries.Select(entry => entry.Page.Slice(entry.Tool.Span))) + "]",
            ServerName: initialize.ValueAt("result", "serverInfo", "name")?.StringValue,
            ServerVersion: initialize.ValueAt("result", "serverInfo", "version")?.StringValue,
            NegotiatedProtocolVersion: initialize.ValueAt("result", "protocolVersion")?.StringValue,
            CapabilityNames: capabilityNames,
            PromptCount: promptCount,
            ResourceCount: resourceCount,
            InspectionNotes: notes,
            ResolvedCommand: resolvedCommand);
    }
}
