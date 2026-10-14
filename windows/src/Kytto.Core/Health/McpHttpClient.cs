using System.Net.Http;
using System.Text.Json;
using Kytto.Core.Tokenizer;
using JsonDocument = Kytto.Core.Json.JsonDocument;
using JsonNode = Kytto.Core.Json.JsonNode;

namespace Kytto.Core.Health;

/// <summary>
/// Streamable HTTP inspection (§7.3): the handshake and read-only listings, and never
/// <c>tools/call</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>initialize</c>, <c>notifications/initialized</c>, then <c>tools/list</c> page by
/// page — the same exchange the stdio client has, over POSTs. Responses may be plain
/// JSON or an event stream; each is matched to its request by JSON-RPC id, and
/// anything else a server says on the way (its own notifications and requests) is
/// ignored and never answered.
/// </para>
/// <para>
/// Everything here is bounded, because the other end is not trusted: 4 MiB per
/// response, 8 MiB in all, 50 pages, 10,000 tools, JSON nested at most 128 deep, a
/// cursor that repeats is refused, and one absolute deadline covers the network and
/// the parsing and the token counting that follow it.
/// </para>
/// </remarks>
public sealed class McpHttpClient
{
    public const string PreferredProtocolVersion = McpStdioClient.PreferredProtocolVersion;

    private static readonly string[] AcceptedProtocolVersions =
        [PreferredProtocolVersion, "2025-11-25", "2025-06-18", "2025-03-26"];

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxPages = 50;
    private const int MaxTools = 10_000;
    private const int MaxCursorBytes = 8_192;

    /// <summary>How long the best-effort session close may take, inside what is left.</summary>
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromMilliseconds(300);

    private readonly IRemoteHttpTransport? _transport;

    public McpHttpClient() { }

    /// <param name="transport">A transport to use instead of the network; owned by the caller.</param>
    public McpHttpClient(IRemoteHttpTransport transport) => _transport = transport;

    public async Task<ToolListing> ListToolsAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = RemoteHttpEndpoint.Validate(url);
        RemoteHttpHeaders.Validate(headers);

        var budget = timeout ?? TimeSpan.FromSeconds(10);
        if (budget < TimeSpan.FromMilliseconds(50)) budget = TimeSpan.FromMilliseconds(50);
        if (budget > TimeSpan.FromSeconds(60)) budget = TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + budget;

        var owned = _transport is null ? new HttpClientRemoteTransport() : null;
        try
        {
            var session = new Session(_transport ?? owned!, endpoint, headers, deadline, cancellationToken);
            return await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private sealed class Session(
        IRemoteHttpTransport transport,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? configuredHeaders,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        private string? _sessionId;
        private string? _protocolVersion;
        private int _receivedBytes;

        internal async Task<ToolListing> RunAsync()
        {
            try
            {
                return await InspectAsync().ConfigureAwait(false);
            }
            finally
            {
                await EndSessionAsync().ConfigureAwait(false);
            }
        }

        private async Task<ToolListing> InspectAsync()
        {
            var initialize = await RequestAsync("initialize", 1, new
            {
                protocolVersion = PreferredProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "Kytto", version = "1.0" },
            }).ConfigureAwait(false);

            var negotiated = initialize.ValueAt("result", "protocolVersion")?.StringValue;
            var capabilityNode = initialize.ValueAt("result", "capabilities");
            if (negotiated is null ||
                !AcceptedProtocolVersions.Contains(negotiated, StringComparer.Ordinal) ||
                capabilityNode?.Members is null)
            {
                throw RemoteHttpException.Protocol("unsupported protocol version or missing capabilities");
            }
            _protocolVersion = negotiated;

            await NotifyInitializedAsync().ConfigureAwait(false);

            var capabilities = capabilityNode.Keys.Order(StringComparer.Ordinal).ToArray();
            var entries = new List<(JsonDocument Document, JsonNode Tool)>();
            var arrays = new List<string>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;

            if (capabilities.Contains("tools", StringComparer.Ordinal))
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    ThrowIfExpired();
                    var document = await RequestAsync(
                        "tools/list",
                        2 + page,
                        cursor is null ? new { } : (object)new { cursor }).ConfigureAwait(false);

                    var tools = document.ValueAt("result", "tools");
                    if (tools?.Elements is not { } elements ||
                        !elements.All(tool => tool.LastValue("name")?.StringValue is not null &&
                                              tool.LastValue("inputSchema")?.Members is not null))
                    {
                        throw RemoteHttpException.Protocol("tools/list did not return valid tool definitions");
                    }
                    if (entries.Count + elements.Count > MaxTools)
                    {
                        throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
                    }
                    entries.AddRange(elements.Select(tool => (document, tool)));
                    arrays.Add(document.Slice(tools.Span));

                    var next = document.ValueAt("result", "nextCursor");
                    if (next is null) break;
                    if (next.StringValue is not { Length: > 0 } text ||
                        System.Text.Encoding.UTF8.GetByteCount(text) > MaxCursorBytes ||
                        !cursors.Add(text) ||
                        page == MaxPages - 1)
                    {
                        throw RemoteHttpException.Protocol("tools pagination did not finish within the inspection limit");
                    }
                    cursor = text;
                }
            }

            var notes = new List<string>();
            var prompts = await OptionalCountAsync("prompts", "prompts/list", 100, capabilities, notes).ConfigureAwait(false);
            var resources = await OptionalCountAsync("resources", "resources/list", 101, capabilities, notes).ConfigureAwait(false);

            var tokenizer = BpeTokenizer.Cl100kBase;
            var summaries = new List<ToolSummary>(entries.Count);
            foreach (var (document, tool) in entries)
            {
                ThrowIfExpired();
                var hints = tool.LastValue("annotations");
                int? tokens;
                try
                {
                    tokens = tokenizer?.CountTokens(document.Slice(tool.Span), deadline);
                }
                catch (TimeoutException)
                {
                    throw RemoteHttpException.TimedOut();
                }
                summaries.Add(new ToolSummary(
                    tool.LastValue("name")!.StringValue!,
                    tool.LastValue("description")?.StringValue,
                    tool.LastValue("inputSchema") is { } schema ? document.Slice(schema.Span) : null,
                    // As the stdio client reads them, so one server compares the same
                    // whichever way it was checked: no annotations is the empty set.
                    new ToolAnnotations(
                        hints?.LastValue("readOnlyHint")?.BoolValue,
                        hints?.LastValue("destructiveHint")?.BoolValue,
                        hints?.LastValue("idempotentHint")?.BoolValue,
                        hints?.LastValue("openWorldHint")?.BoolValue),
                    tokens));
            }
            ThrowIfExpired();

            var rawTools = arrays.Count == 1
                ? arrays[0]
                : "[" + string.Join(",", entries.Select(entry => entry.Document.Slice(entry.Tool.Span))) + "]";
            ThrowIfExpired();

            return new ToolListing(
                Tools: summaries,
                RawToolsJson: rawTools,
                ServerName: initialize.ValueAt("result", "serverInfo", "name")?.StringValue,
                ServerVersion: initialize.ValueAt("result", "serverInfo", "version")?.StringValue,
                NegotiatedProtocolVersion: negotiated,
                CapabilityNames: capabilities,
                PromptCount: prompts,
                ResourceCount: resources,
                InspectionNotes: notes,
                ResolvedCommand: "");
        }

        private async Task<int?> OptionalCountAsync(
            string capability,
            string method,
            int id,
            IReadOnlyList<string> capabilities,
            List<string> notes)
        {
            if (!capabilities.Contains(capability, StringComparer.Ordinal)) return null;
            try
            {
                var document = await RequestAsync(method, id, new { }).ConfigureAwait(false);
                if (document.ValueAt("result", capability)?.Elements is not { } values)
                {
                    notes.Add($"{method} did not return its expected array.");
                    return null;
                }
                if (document.ValueAt("result", "nextCursor") is not null)
                {
                    notes.Add($"{char.ToUpperInvariant(capability[0])}{capability[1..]} count shows the first page; the server has more.");
                }
                return values.Count;
            }
            catch (RemoteHttpException failure) when (
                failure.Failure is RemoteHttpFailure.Protocol or RemoteHttpFailure.HttpStatus)
            {
                // Optional inspector data must not turn a successful tools handshake
                // into a failed check. Authorization, timeouts and size limits still
                // propagate: those are about the endpoint, not about this listing.
                notes.Add($"{method} could not be inspected.");
                return null;
            }
        }

        // MARK: - Requests

        private async Task<JsonDocument> RequestAsync(string method, int id, object parameters)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            });
            var reply = await SendAsync(HttpMethod.Post, body, id).ConfigureAwait(false);
            ThrowForStatus(reply);

            if (reply.Header("mcp-session-id") is { } assigned && _sessionId is null)
            {
                if (assigned.Length == 0 || assigned.Length > 1_024 || assigned.Any(c => c is < '!' or > '~'))
                {
                    throw RemoteHttpException.Protocol("invalid session header");
                }
                _sessionId = assigned;
            }

            var media = (reply.Header("content-type") ?? "").ToLowerInvariant().Split(';')[0].Trim();
            IEnumerable<string> messages;
            if (media == "application/json")
            {
                messages = RemoteJson.Decode(reply.Body) is { } text
                    ? [text]
                    : throw RemoteHttpException.Protocol("the response was not valid UTF-8");
            }
            else if (media == "text/event-stream")
            {
                messages = ServerSentEventReader.Messages(reply.Body);
            }
            else
            {
                throw RemoteHttpException.Protocol("expected application/json or text/event-stream");
            }

            foreach (var message in messages)
            {
                if (RemoteJson.TryParse(message) is not { } document || !RemoteJson.IsResponseTo(document, id)) continue;
                // A server's error text can echo an Authorization header or a signed
                // URL query. The fact is recorded; the words are not.
                if (document.Root["error"] is not null) throw RemoteHttpException.Protocol("the server returned a JSON-RPC error");
                if (document.Root["result"]?.Members is null) throw RemoteHttpException.Protocol("the response has no result object");
                return document;
            }
            throw RemoteHttpException.Protocol("no matching JSON-RPC response");
        }

        private async Task NotifyInitializedAsync()
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
            });
            var reply = await SendAsync(HttpMethod.Post, body, responseId: null).ConfigureAwait(false);
            ThrowForStatus(reply);
            // 202 with nothing in it is the spec. 204, or a 200 with nothing in it, is
            // what several real servers send; any body at all is not a notification ack.
            if (reply.Status is not (200 or 202 or 204) || reply.Body.Length != 0)
            {
                throw RemoteHttpException.Protocol("the initialized notification was not accepted");
            }
        }

        private async Task<RemoteHttpReply> SendAsync(HttpMethod method, byte[]? body, int? responseId)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (method == HttpMethod.Post)
            {
                headers.Add(new("Accept", "application/json, text/event-stream"));
            }
            foreach (var header in configuredHeaders ?? new Dictionary<string, string>()) headers.Add(header);
            if (_sessionId is not null) headers.Add(new("MCP-Session-Id", _sessionId));
            if (_protocolVersion is not null) headers.Add(new("MCP-Protocol-Version", _protocolVersion));

            var limit = Math.Min(MaxResponseBytes, MaxTotalBytes - _receivedBytes);
            if (limit <= 0) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);

            var reply = await transport.SendAsync(
                new RemoteHttpRequest(method, endpoint, headers, body, body is null ? null : "application/json"),
                deadline,
                limit,
                responseId,
                cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= deadline) throw RemoteHttpException.TimedOut();
            if (reply.Body.Length > limit) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
            _receivedBytes += reply.Body.Length;
            return reply;
        }

        private static void ThrowForStatus(RemoteHttpReply reply)
        {
            if (reply.Status == 401 || (reply.Status == 403 && reply.Header("www-authenticate") is not null))
            {
                throw new RemoteHttpException(RemoteHttpFailure.AuthorizationRequired);
            }
            if (reply.Status is < 200 or >= 300)
            {
                throw new RemoteHttpException(RemoteHttpFailure.HttpStatus, status: reply.Status);
            }
        }

        private void ThrowIfExpired()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw RemoteHttpException.TimedOut();
        }

        /// <summary>Best-effort <c>DELETE</c> of the session, within what is left of the deadline.</summary>
        /// <remarks>
        /// A server that issued a session id is told it can drop it. Failing to say so
        /// changes nothing about the check, so every failure is swallowed — and it never
        /// takes more than 0.3 s, nor anything past the deadline the check was given.
        /// </remarks>
        private async Task EndSessionAsync()
        {
            if (_sessionId is null) return;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;

            var budget = remaining < CleanupBudget ? remaining : CleanupBudget;
            try
            {
                var headers = (configuredHeaders ?? new Dictionary<string, string>()).ToList();
                headers.Add(new("MCP-Session-Id", _sessionId));
                if (_protocolVersion is not null) headers.Add(new("MCP-Protocol-Version", _protocolVersion));

                using var cleanup = new CancellationTokenSource(budget);
                await transport.SendAsync(
                    new RemoteHttpRequest(HttpMethod.Delete, endpoint, headers),
                    DateTimeOffset.UtcNow + budget,
                    1_024,
                    responseId: null,
                    cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort, by design.
            }
        }
    }
}
