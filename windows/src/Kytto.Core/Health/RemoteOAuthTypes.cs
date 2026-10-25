using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kytto.Core.Health;

public enum RemoteOAuthError
{
    Discovery,
    IssuerMismatch,
    ResourceMismatch,
    PkceUnsupported,
    RegistrationUnavailable,
    InvalidClient,
    InvalidRedirect,
    InvalidCallback,
    ExpiredAuthorization,
    AuthorizationRejected,
    InvalidToken,
    RefreshUnavailable,
    CredentialStore,
}

/// <summary>A sign-in step that did not complete; its message is fixed text (§6).</summary>
/// <remarks>
/// Like <see cref="RemoteHttpException"/>, nothing a server said — an
/// <c>error_description</c>, a body, a URL — is ever put in the message.
/// </remarks>
public sealed class RemoteOAuthException(RemoteOAuthError error, Exception? inner = null)
    : Exception(MessageFor(error), inner)
{
    public RemoteOAuthError Error { get; } = error;

    public static string MessageFor(RemoteOAuthError error) => error switch
    {
        RemoteOAuthError.Discovery =>
            "The server did not provide usable OAuth discovery metadata. Check its OAuth support or configured authorization headers.",
        RemoteOAuthError.IssuerMismatch => "OAuth issuer validation failed. The sign-in was stopped.",
        RemoteOAuthError.ResourceMismatch => "OAuth metadata describes a different MCP resource. The sign-in was stopped.",
        RemoteOAuthError.PkceUnsupported => "The authorization server does not advertise the required S256 PKCE protection.",
        RemoteOAuthError.RegistrationUnavailable => "This authorization server requires a pre-registered client ID.",
        RemoteOAuthError.InvalidClient => "The OAuth client registration is not a usable public client.",
        RemoteOAuthError.InvalidRedirect => "The OAuth callback must be a loopback redirect on this computer.",
        RemoteOAuthError.InvalidCallback => "The OAuth callback did not match this sign-in request.",
        RemoteOAuthError.ExpiredAuthorization => "The sign-in request expired or was already used. Start it again.",
        RemoteOAuthError.AuthorizationRejected => "The authorization server declined the sign-in request.",
        RemoteOAuthError.InvalidToken => "The authorization server returned an unusable token response.",
        RemoteOAuthError.RefreshUnavailable => "The saved authorization cannot be refreshed. Sign in again.",
        _ => "The authorization could not be saved securely.",
    };
}

/// <summary>Validated metadata kept natively; never sent to web state (§6).</summary>
/// <param name="ResourceUrl">The configured endpoint, canonicalised. Names the stored credential.</param>
/// <param name="Resource">
/// The protected resource the metadata described (RFC 9728), sent as the RFC 8707
/// <c>resource</c> parameter. It may be the endpoint or a parent of it on the same
/// origin. Absent in records saved before it was kept.
/// </param>
public sealed record RemoteOAuthConfiguration(
    string ResourceUrl,
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> Scopes,
    bool AuthorizationResponseIssuerSupported,
    string? Resource = null)
{
    /// <summary>What the authorization and token requests name as their audience.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResourceIndicator => Resource ?? ResourceUrl;
}

/// <summary>
/// A single-use, short-lived PKCE transaction: never persisted, never in IPC (§6).
/// </summary>
public sealed class RemoteOAuthPendingAuthorization
{
    /// <summary>How long a transaction is good for; the person is signing in in a browser.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private bool _used;

    internal RemoteOAuthPendingAuthorization(
        Uri authorizationUrl,
        RemoteOAuthConfiguration configuration,
        string clientId,
        string redirectUri,
        string verifier,
        string state,
        DateTimeOffset now)
    {
        AuthorizationUrl = authorizationUrl;
        Configuration = configuration;
        ClientId = clientId;
        RedirectUri = redirectUri;
        Verifier = verifier;
        State = state;
        ExpiresAt = now + Lifetime;
    }

    /// <summary>The one page the system browser is sent to.</summary>
    public Uri AuthorizationUrl { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The random <c>state</c>: what an exact callback has to carry back.</summary>
    internal string State { get; }

    internal RemoteOAuthConfiguration Configuration { get; }
    internal string ClientId { get; }
    internal string RedirectUri { get; }
    internal string Verifier { get; }

    /// <summary>Spends the transaction. The second call, or one after expiry, throws.</summary>
    internal void Consume(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_used || now >= ExpiresAt) throw new RemoteOAuthException(RemoteOAuthError.ExpiredAuthorization);
            _used = true;
        }
    }

    public override string ToString() => nameof(RemoteOAuthPendingAuthorization);
}

/// <summary>
/// A secret-bearing record. Stored only through <see cref="RemoteOAuthCredentials"/>;
/// attached to no DTO, no log line and no exception (§6).
/// </summary>
/// <remarks>
/// A class with a fixed <see cref="ToString"/> rather than a record, whose generated
/// one would print every token.
/// </remarks>
public sealed class RemoteOAuthCredential
{
    internal RemoteOAuthCredential(
        RemoteOAuthConfiguration configuration,
        string clientId,
        IReadOnlyList<string> scopes,
        DateTimeOffset? expiresAt,
        string accessToken,
        string? refreshToken)
    {
        Configuration = configuration;
        ClientId = clientId;
        Scopes = scopes;
        ExpiresAt = expiresAt;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    public RemoteOAuthConfiguration Configuration { get; }
    public string ClientId { get; }
    public IReadOnlyList<string> Scopes { get; }
    public DateTimeOffset? ExpiresAt { get; }
    internal string AccessToken { get; }
    internal string? RefreshToken { get; }

    /// <summary>The one header Kytto's sign-in replaces in a check.</summary>
    public IReadOnlyDictionary<string, string> AuthorizationHeaders =>
        new Dictionary<string, string> { ["Authorization"] = "Bearer " + AccessToken };

    /// <summary>Whether the access token is gone or about to be.</summary>
    /// <remarks>
    /// Refresh happens only when a check is explicitly asked for; nothing renews a
    /// token in the background.
    /// </remarks>
    public bool RequiresRefresh(DateTimeOffset now) => ExpiresAt is { } expiry && expiry - now < TimeSpan.FromSeconds(60);

    /// <summary>Whether a refresh token is on file to renew this with.</summary>
    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

    public override string ToString() => nameof(RemoteOAuthCredential);

    // MARK: - Storage shape

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal string Serialize() => JsonSerializer.Serialize(
        new Stored(
            1,
            Configuration,
            ClientId,
            Scopes,
            ExpiresAt?.ToUnixTimeMilliseconds(),
            AccessToken,
            RefreshToken),
        Options);

    internal static RemoteOAuthCredential Deserialize(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json, Options);
            if (stored is not { Version: 1, Configuration: { } configuration, ClientId: { Length: > 0 } clientId } ||
                string.IsNullOrEmpty(stored.AccessToken))
            {
                throw new RemoteOAuthException(RemoteOAuthError.CredentialStore);
            }
            return new RemoteOAuthCredential(
                configuration,
                clientId,
                stored.Scopes ?? [],
                stored.ExpiresAtMilliseconds is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null,
                stored.AccessToken,
                stored.RefreshToken);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new RemoteOAuthException(RemoteOAuthError.CredentialStore, error);
        }
    }

    private sealed record Stored(
        [property: JsonPropertyName("version")] int Version,
        RemoteOAuthConfiguration? Configuration,
        string? ClientId,
        IReadOnlyList<string>? Scopes,
        long? ExpiresAtMilliseconds,
        string? AccessToken,
        string? RefreshToken)
    {
        public override string ToString() => nameof(Stored);
    }

    internal static string RandomBase64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
