using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;

namespace Kytto.Core.Health;

/// <summary>One request Kytto sends to a remote MCP or OAuth endpoint.</summary>
public sealed record RemoteHttpRequest(
    HttpMethod Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body = null,
    string? ContentType = null);

/// <summary>What came back: a status, lower-cased headers, and a bounded body.</summary>
public sealed record RemoteHttpReply(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());
}

/// <summary>
/// The one door for remote requests, so everything above it can be tested without a
/// network and everything below it is bounded (§6, §7.3).
/// </summary>
public interface IRemoteHttpTransport
{
    /// <param name="limit">The most body bytes this reply may hold.</param>
    /// <param name="responseId">
    /// When the body is an event stream, the JSON-RPC id whose answer ends the wait: a
    /// server may keep the stream open after answering, so the end is not awaited.
    /// </param>
    Task<RemoteHttpReply> SendAsync(
        RemoteHttpRequest request,
        DateTimeOffset deadline,
        int limit,
        int? responseId,
        CancellationToken cancellationToken);
}

/// <summary>Bounded, credential-free HTTP used only after the user's explicit request.</summary>
/// <remarks>
/// <para>
/// Hand-built on purpose rather than the shared default client, because each default
/// is a way for a credential to leave: no redirect is followed (a bearer token must
/// never be forwarded to another destination), no cookie jar, no ambient Windows
/// credentials, no decompression (a small body must not expand past the limit it was
/// counted against), no certificate callback (TLS validation is the platform's, never
/// loosened here).
/// </para>
/// <para>
/// The deadline is absolute and covers connecting, the response headers and every
/// byte of the body. A body is read until its limit or its end — except an event
/// stream that has already answered, which is not waited on.
/// </para>
/// </remarks>
public sealed class HttpClientRemoteTransport : IRemoteHttpTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpClientRemoteTransport()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = true,
            Proxy = new LoopbackBypassProxy(),
            MaxResponseHeadersLength = 32,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        // The overall deadline is ours; the client's own timeout would only race it.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RemoteHttpReply> SendAsync(
        RemoteHttpRequest request,
        DateTimeOffset deadline,
        int limit,
        int? responseId,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw RemoteHttpException.TimedOut();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining);
        try
        {
            using var message = Build(request);
            using var response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            var headers = Headers(response);
            // A redirect, an error, a challenge: the status and headers are the whole
            // answer, and a body Kytto will not use is not read.
            if (status >= 300) return new RemoteHttpReply(status, headers, []);

            if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            {
                throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
            }

            var eventStream = string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase);
            var body = await ReadBodyAsync(response, limit, eventStream ? responseId : null, eventStream, timeout.Token)
                .ConfigureAwait(false);
            return new RemoteHttpReply(status, headers, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw RemoteHttpException.TimedOut();
        }
        catch (RemoteHttpException)
        {
            throw;
        }
        catch (Exception error) when (
            error is HttpRequestException or IOException or InvalidOperationException or
                     System.Security.Authentication.AuthenticationException or UriFormatException or
                     System.Net.Sockets.SocketException)
        {
            // Never `error.Message`: the HTTP stack's text can name the URL, and a URL
            // can carry a signed query.
            throw RemoteHttpException.Unavailable();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        int limit,
        int? responseId,
        bool eventStream,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var reader = eventStream && responseId is not null ? new ServerSentEventReader(responseId) : null;
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > limit) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
            body.Write(buffer, 0, read);
            // A server may leave the stream open after the answer. Do not wait for EOF.
            if (reader?.Feed(buffer.AsSpan(0, read)) == true) break;
        }
        return body.ToArray();
    }

    private static HttpRequestMessage Build(RemoteHttpRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Url) { Version = HttpVersion.Version11 };
        message.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        if (request.Body is not null)
        {
            var content = new ByteArrayContent(request.Body);
            content.Headers.ContentType = request.ContentType is null
                ? null
                : MediaTypeHeaderValue.Parse(request.ContentType);
            message.Content = content;
        }
        foreach (var (name, value) in request.Headers)
        {
            // Unvalidated by the HTTP stack, validated by Kytto: the stack's own rules
            // would reject some real servers' headers, and Kytto's are stricter
            // where it matters (see RemoteHttpHeaders.Validate).
            if (!message.Headers.TryAddWithoutValidation(name, value) &&
                message.Content is not null)
            {
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return message;
    }

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }
        return headers;
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Sends loopback traffic directly, whatever the system proxy says.</summary>
    /// <remarks>
    /// A corporate proxy cannot reach the user's own machine, and sending a
    /// loopback request through one would also hand it to a third party.
    /// </remarks>
    private sealed class LoopbackBypassProxy : IWebProxy
    {
        private readonly IWebProxy _system = HttpClient.DefaultProxy;

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) =>
            IsBypassed(destination) ? null : _system.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            RemoteHttpEndpoint.IsLoopbackHost(host.Host) || _system.IsBypassed(host);
    }
}
