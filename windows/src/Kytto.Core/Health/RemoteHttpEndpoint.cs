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

    /// <summary>
    /// Whether a host is spelled as this machine or a non-public address: <c>localhost</c>,
    /// or a literal loopback, private, link-local, unique-local or unspecified IP.
    /// </summary>
    /// <remarks>
    /// Spelling only, like <see cref="IsLoopbackHost"/>: a name that merely resolves
    /// somewhere private is not caught here, and nothing here asks a resolver.
    /// </remarks>
    internal static bool IsLocalOrPrivateLiteral(string host)
    {
        var bare = host.Trim('[', ']');
        if (bare.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            bare.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(bare, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is 10 or 0 or >= 224 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
               (bytes[0] & 0xFE) == 0xFC;
    }

    private static RemoteHttpException Invalid() => new(RemoteHttpFailure.InvalidEndpoint);
}
