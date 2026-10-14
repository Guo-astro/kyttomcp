using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Kytto.Core.Health;

/// <summary>
/// The sign-in's callback: a listener that exists for one sign-in, on the loopback
/// interface only, and takes exactly one request (RFC 8252 §7.3, §8.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>No installation.</b> Windows has no equivalent of macOS's registered
/// <c>app.kytto.oauth://</c> scheme without writing to the registry, and an
/// <c>HttpListener</c> prefix may need a URL reservation. A plain socket on
/// <c>127.0.0.1</c> needs neither: nothing is installed, registered or left behind,
/// and nothing listens before a person starts a sign-in or after it ends.
/// </para>
/// <para>
/// <b>What it accepts.</b> A <c>GET</c> for exactly <see cref="CallbackPath"/> whose
/// <c>Host</c> is exactly this socket's address — a name that resolves here from a web
/// page (DNS rebinding) is not — carrying the sign-in's own random <c>state</c>. Any
/// other request, from a browser tab or another process, is answered and ignored; it
/// neither ends the wait nor is passed on. The first valid request completes the
/// sign-in; a second one finds it already spent. The state is compared in constant
/// time and the page shown back reflects nothing from the request.
/// </para>
/// <para>
/// The socket is exclusive (no other process can share the port), reads are bounded
/// in size, time and concurrency, and <see cref="Dispose"/> closes it whether the
/// sign-in completed, was cancelled or timed out.
/// </para>
/// </remarks>
public sealed class OAuthLoopbackListener : IDisposable
{
    public const string CallbackPath = "/oauth/callback";

    private const int MaxRequestBytes = 8 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Uri> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _connections = new(8);
    private string? _expectedState;
    private int _completed;
    private int _disposed;

    private OAuthLoopbackListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The redirect URI to register and to send to the authorization server.</summary>
    public string RedirectUri => $"http://127.0.0.1:{Port}{CallbackPath}";

    /// <summary>Binds a free loopback port and starts answering.</summary>
    public static OAuthLoopbackListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start(backlog: 8);
        var instance = new OAuthLoopbackListener(listener);
        _ = Task.Run(instance.AcceptLoopAsync);
        return instance;
    }

    /// <summary>Waits for the one valid callback and returns its URL.</summary>
    /// <exception cref="RemoteOAuthException">Nothing valid arrived in time.</exception>
    /// <exception cref="OperationCanceledException">The sign-in was cancelled.</exception>
    public async Task<Uri> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _expectedState, expectedState);
        try
        {
            return await _result.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RemoteOAuthException(RemoteOAuthError.ExpiredAuthorization);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeAsync(client).ConfigureAwait(false);
                    }
                    finally
                    {
                        _connections.Release();
                    }
                });
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Closed: the sign-in is over.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(ReadTimeout);
            var stream = client.GetStream();

            var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (head is null)
            {
                await RespondAsync(stream, 431, "Request too large.").ConfigureAwait(false);
                return;
            }

            var (status, body, callback) = Judge(head);
            if (callback is not null)
            {
                // Exactly once, however many requests race for it.
                if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                {
                    await RespondAsync(stream, 409, "This sign-in was already completed.").ConfigureAwait(false);
                    return;
                }
                await RespondAsync(stream, status, body).ConfigureAwait(false);
                _result.TrySetResult(callback);
                // Closing here is what makes the listener short-lived: the sign-in is
                // spent and nothing else is wanted on this port.
                _stop.Cancel();
                _listener.Stop();
                return;
            }
            await RespondAsync(stream, status, body).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A client that went away or was too slow is not a sign-in.
        }
    }

    /// <summary>The request line and headers, or null if they are past the size bound.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return length >= buffer.Length ? null : Encoding.ASCII.GetString(buffer, 0, length);
    }

    private (int Status, string Body, Uri? Callback) Judge(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return (400, "Bad request.", null);
        }
        if (request[0] != "GET") return (405, "Method not allowed.", null);

        string? host = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null) return (400, "Bad request.", null);
                host = line[(colon + 1)..].Trim();
            }
        }
        if (host != $"127.0.0.1:{Port}") return (400, "Bad request.", null);

        var target = request[1];
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        if (path != CallbackPath) return (404, "Not found.", null);

        var state = Volatile.Read(ref _expectedState);
        if (state is null || question < 0) return (400, "Bad request.", null);

        string? presented = null;
        foreach (var item in target[(question + 1)..].Split('&'))
        {
            var separator = item.IndexOf('=');
            var name = separator < 0 ? item : item[..separator];
            if (name != "state") continue;
            if (presented is not null) return (400, "Bad request.", null);
            presented = Uri.UnescapeDataString((separator < 0 ? "" : item[(separator + 1)..]).Replace('+', ' '));
        }
        if (presented is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(state)))
        {
            return (400, "Bad request.", null);
        }

        if (!Uri.TryCreate($"http://127.0.0.1:{Port}{target}", UriKind.Absolute, out var callback))
        {
            return (400, "Bad request.", null);
        }
        return (200, "You are signed in. You can close this tab and return to Kytto.", callback);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message)
    {
        // A fixed page: nothing from the request is reflected, so there is nothing to
        // inject into it.
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=\"utf-8\"><title>Kytto</title>" +
            "<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><h1>Kytto</h1><p>" + message + "</p>");
        var head =
            $"HTTP/1.1 {status} X\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already closed by a completed sign-in.
        }
        _result.TrySetCanceled();
        _stop.Dispose();
    }
}
