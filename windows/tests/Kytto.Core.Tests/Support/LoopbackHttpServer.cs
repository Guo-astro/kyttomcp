using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kytto.Core.Tests;

/// <summary>One request as the loopback server received it.</summary>
public sealed record RecordedRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string Path => Target.Split('?')[0];

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>What the loopback server answers with.</summary>
/// <param name="HoldOpen">
/// Keep the connection open this long after writing, without a Content-Length — what
/// a Streamable HTTP server that leaves its event stream open looks like.
/// </param>
/// <param name="DeclaredLength">Send this Content-Length instead of the body's real one.</param>
public sealed record LoopbackResponse(
    int Status = 200,
    string Body = "",
    string ContentType = "application/json",
    IReadOnlyDictionary<string, string>? Headers = null,
    TimeSpan? HoldOpen = null,
    TimeSpan? DelayBeforeReply = null,
    long? DeclaredLength = null,
    byte[]? RawBody = null);

/// <summary>
/// A real HTTP server on the loopback interface only, for tests of the actual
/// transport: redirects, timeouts, size limits, an event stream that never ends.
/// </summary>
/// <remarks>
/// <c>TcpListener</c> on <c>IPAddress.Loopback</c> rather than <c>HttpListener</c>,
/// which needs a URL reservation for some prefixes. No real network and no real
/// service are ever involved.
/// </remarks>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<RecordedRequest, LoopbackResponse> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _lock = new();

    public LoopbackHttpServer(Func<RecordedRequest, LoopbackResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string Url(string path = "/mcp") => $"http://127.0.0.1:{Port}{path}";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock) return _requests.ToArray();
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception)
        {
            // Disposed.
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequest(stream).ConfigureAwait(false);
            if (request is null) return;
            lock (_lock) _requests.Add(request);

            var response = _handler(request);
            if (response.DelayBeforeReply is { } delay) await Task.Delay(delay, _stop.Token).ConfigureAwait(false);

            var body = response.RawBody ?? Encoding.UTF8.GetBytes(response.Body);
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {response.Status} X\r\n");
            if (response.Status != 204) head.Append($"Content-Type: {response.ContentType}\r\n");
            foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            {
                head.Append($"{name}: {value}\r\n");
            }
            if (response.HoldOpen is null)
            {
                head.Append($"Content-Length: {response.DeclaredLength ?? body.Length}\r\n");
            }
            head.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
            await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            if (response.HoldOpen is { } hold) await Task.Delay(hold, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A client that gave up is not a failure of the fixture.
        }
    }

    private static async Task<RecordedRequest?> ReadRequest(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var declared) ? int.Parse(declared) : 0;
        var body = buffer.Skip(headerEnd + 4).ToList();
        while (body.Count < length)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) break;
            body.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        return new RecordedRequest(parts[0], parts[1], headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (var index = 0; index + 3 < buffer.Count; index++)
        {
            if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
