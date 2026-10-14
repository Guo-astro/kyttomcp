using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

/// <summary>
/// A transport that answers from a script and records what it was asked, so the
/// protocol and the limits can be tested with no network at all.
/// </summary>
public sealed class ScriptedTransport : IRemoteHttpTransport
{
    private readonly object _lock = new();
    private readonly Queue<RemoteHttpReply> _replies;
    private readonly List<RemoteHttpRequest> _requests = [];

    public ScriptedTransport(params RemoteHttpReply[] replies) => _replies = new Queue<RemoteHttpReply>(replies);

    public IReadOnlyList<RemoteHttpRequest> Requests
    {
        get
        {
            lock (_lock) return _requests.ToArray();
        }
    }

    public Task<RemoteHttpReply> SendAsync(
        RemoteHttpRequest request,
        DateTimeOffset deadline,
        int limit,
        int? responseId,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _requests.Add(request);
            if (_replies.Count == 0) throw new RemoteHttpException(RemoteHttpFailure.Unavailable);
            var reply = _replies.Dequeue();
            if (reply.Body.Length > limit) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
            return Task.FromResult(reply);
        }
    }

    public static RemoteHttpReply Reply(
        string text = "",
        int status = 200,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var all = new Dictionary<string, string> { ["content-type"] = "application/json" };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) all[name.ToLowerInvariant()] = value;
        return new RemoteHttpReply(status, all, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A JSON-RPC success message, built by concatenation so the JSON stays readable.</summary>
    public static string Rpc(int id, string result) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}";

    public const string Initialize =
        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2026-07-28","capabilities":{"tools":{}},"serverInfo":{"name":"remote-fixture","version":"1"}}}""";

    public const string EmptyTools = """{"jsonrpc":"2.0","id":2,"result":{"tools":[]}}""";
}

/// <summary>A scripted remote MCP server behind <see cref="LoopbackHttpServer"/>.</summary>
public sealed class FakeRemoteMcp : IDisposable
{
    private readonly LoopbackHttpServer _server;
    private readonly object _lock = new();
    private readonly List<string> _methods = [];

    public FakeRemoteMcp(
        Func<RecordedRequest, string, LoopbackResponse?>? intercept = null,
        string sessionId = "session-fixture",
        int pages = 1,
        TimeSpan? deleteDelay = null)
    {
        _server = new LoopbackHttpServer(request =>
        {
            if (request.Method == "DELETE")
            {
                return new LoopbackResponse(204, DelayBeforeReply: deleteDelay);
            }
            using var document = JsonDocument.Parse(request.Body.Length == 0 ? "{}" : request.Body);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            lock (_lock) _methods.Add(method);

            if (intercept?.Invoke(request, method) is { } custom) return custom;

            switch (method)
            {
                case "notifications/initialized":
                    return new LoopbackResponse(202);
                case "initialize":
                    return Json(
                        root.GetProperty("id").GetInt32(),
                        """{"protocolVersion":"2026-07-28","capabilities":{"tools":{}},"serverInfo":{"name":"loopback","version":"1"}}""",
                        new Dictionary<string, string>
                        {
                            ["MCP-Session-Id"] = sessionId,
                            // A cookie must never come back.
                            ["Set-Cookie"] = "tracker=1",
                        });
                case "tools/list":
                    var cursor = root.GetProperty("params").TryGetProperty("cursor", out var c) ? c.GetString() : null;
                    var page = cursor is null ? 0 : int.Parse(cursor);
                    var next = page + 1 < pages ? $",\"nextCursor\":\"{page + 1}\"" : "";
                    return Json(
                        root.GetProperty("id").GetInt32(),
                        "{\"tools\":[{\"name\":\"fixture_read_" + page + "\",\"inputSchema\":{\"type\":\"object\"}}]" + next + "}");
                default:
                    return new LoopbackResponse(400);
            }
        });
    }

    public string Url => _server.Url();

    public IReadOnlyList<RecordedRequest> Requests => _server.Requests;

    /// <summary>Every JSON-RPC method received, in order.</summary>
    public IReadOnlyList<string> Methods
    {
        get
        {
            lock (_lock) return _methods.ToArray();
        }
    }

    public static LoopbackResponse Json(int id, string result, IReadOnlyDictionary<string, string>? headers = null) =>
        new(200, ScriptedTransport.Rpc(id, result), Headers: headers);

    public void Dispose() => _server.Dispose();
}

internal static class RemoteTestServers
{
    public static Server Http(string url, string definition = "{}", IReadOnlyList<EnvEntry>? env = null) => new()
    {
        Id = "remote",
        Name = "remote",
        Transport = Transport.Http,
        Command = null,
        Args = [],
        Env = env ?? [],
        Url = url,
        EnabledIn = [],
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = "test",
        IsBundled = false,
        DefinitionSource = definition,
    };
}
