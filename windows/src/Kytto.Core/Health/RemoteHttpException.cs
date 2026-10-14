namespace Kytto.Core.Health;

/// <summary>Why a remote check or sign-in step did not complete.</summary>
public enum RemoteHttpFailure
{
    InvalidEndpoint,
    InvalidHeaders,
    UnresolvedCredential,
    TimedOut,
    Unavailable,
    ResponseTooLarge,
    HttpStatus,
    AuthorizationRequired,
    Protocol,
}

/// <summary>
/// A remote failure whose message is safe to keep and to show (§6).
/// </summary>
/// <remarks>
/// <para>
/// Nothing a server said ever becomes part of this message. A response body, a
/// <c>WWW-Authenticate</c> value, a JSON-RPC error text, a redirect target and the
/// exception text of the HTTP stack can each carry a token, a signed URL query or a
/// reflected <c>Authorization</c> header — and a health result is written to disk and
/// copied into bug reports. So the message is composed here, from the failure kind, an
/// HTTP status number and a fixed-vocabulary <see cref="Detail"/> the code itself chose.
/// </para>
/// </remarks>
public sealed class RemoteHttpException : Exception
{
    public RemoteHttpException(RemoteHttpFailure failure, int? status = null, string? detail = null)
        : base(MessageFor(failure, status, detail))
    {
        Failure = failure;
        Status = status;
        Detail = detail;
    }

    public RemoteHttpFailure Failure { get; }

    /// <summary>The HTTP status, for <see cref="RemoteHttpFailure.HttpStatus"/>.</summary>
    public int? Status { get; }

    /// <summary>A phrase chosen by this code, never text received from a server.</summary>
    public string? Detail { get; }

    internal static RemoteHttpException Protocol(string detail) =>
        new(RemoteHttpFailure.Protocol, detail: detail);

    internal static RemoteHttpException TimedOut() => new(RemoteHttpFailure.TimedOut);

    internal static RemoteHttpException Unavailable() => new(RemoteHttpFailure.Unavailable);

    /// <summary>The wording for one failure; also what a stored health result renders from.</summary>
    public static string MessageFor(RemoteHttpFailure failure, int? status = null, string? detail = null) =>
        failure switch
        {
            RemoteHttpFailure.InvalidEndpoint =>
                "Use an HTTPS endpoint, or HTTP on the loopback interface, without embedded credentials or a fragment.",
            RemoteHttpFailure.InvalidHeaders =>
                "The configured HTTP headers are invalid or override a transport header.",
            RemoteHttpFailure.UnresolvedCredential =>
                "A configured HTTP credential variable could not be resolved. Supply it in the launching environment or sign in explicitly.",
            RemoteHttpFailure.TimedOut =>
                "The remote server did not finish the check within the time limit.",
            RemoteHttpFailure.Unavailable =>
                "The remote server could not be reached. Check its address, network connection and TLS certificate.",
            RemoteHttpFailure.ResponseTooLarge =>
                "The remote response exceeded Kytto's inspection size limit.",
            RemoteHttpFailure.HttpStatus =>
                $"The remote server returned HTTP {status ?? 0}.",
            RemoteHttpFailure.AuthorizationRequired =>
                "The remote server requires authorization. Sign in or check its configured credentials and permissions.",
            _ => $"The remote server did not speak MCP as expected: {detail ?? "unexpected response"}",
        };
}
