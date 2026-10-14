using System.Net;
using System.Text;

namespace Kytto.Core.Health;

/// <summary>Which URLs Kytto will ever send a request to, on a user's say-so (§6, §7.3).</summary>
/// <remarks>
/// HTTPS, or plain HTTP to the loopback interface for a server the user runs
/// themselves. No credentials in the URL, no fragment, and no characters a parser
/// might read differently from the HTTP stack that sends the request.
/// </remarks>
public static class RemoteHttpEndpoint
{
    private const int MaxUrlBytes = 8_192;

    public static Uri Validate(string? text, bool allowLoopbackHttp = true)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) > MaxUrlBytes) throw Invalid();
        foreach (var character in text)
        {
            // Whitespace and control characters are trimmed or dropped by some
            // parsers and not others; a backslash is a slash to some and not to
            // others. Either is how a request ends up somewhere the user did not mean.
            if (character <= ' ' || character == '\u007f' || character == '\\' || character == '#') throw Invalid();
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Invalid();
        }

        var https = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var loopbackHttp = allowLoopbackHttp &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            IsLoopbackHost(uri.Host);
        if (!https && !loopbackHttp) throw Invalid();
        return uri;
    }

    /// <summary>Whether a host names this machine's own loopback interface, by spelling.</summary>
    /// <remarks>
    /// By spelling and not by lookup: <c>127.0.0.1.example</c> must not pass because a
    /// resolver says so, and nothing here asks one.
    /// </remarks>
    internal static bool IsLoopbackHost(string host)
    {
        var bare = host.Trim('[', ']');
        if (bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address);
    }

    private static RemoteHttpException Invalid() => new(RemoteHttpFailure.InvalidEndpoint);
}
