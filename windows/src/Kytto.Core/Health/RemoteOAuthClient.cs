using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Kytto.Core.Json;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Health;

/// <summary>
/// OAuth 2.1 assistance for an explicit Remote Doctor sign-in (§7.3).
/// </summary>
/// <remarks>
/// <para>
/// Spec: the MCP authorization profile with RFC 9728 (protected resource metadata),
/// RFC 8414 (server metadata), RFC 9207 (issuer in the response), RFC 8707 (resource
/// indicators), RFC 7636 (PKCE, S256 only) and RFC 8252 (loopback redirects). No
/// browser is opened here and nothing runs unless the person asked for a sign-in.
/// </para>
/// <para>
/// What is trusted, and what is not: the configured resource URL is the user's. Every
/// other URL — the metadata the resource points to, the issuer, the endpoints — is
/// data from a server, and is accepted only if it is HTTPS, in the shape the
/// specification gives it, and consistent with the rest: the metadata's
/// <c>resource</c> must be this resource, the issuer must be the one metadata lists
/// and the one its own document claims, and a resource hint must be same-origin so a
/// challenge cannot aim Kytto at an arbitrary host.
/// </para>
/// </remarks>
public sealed class RemoteOAuthClient
{
    private readonly IRemoteHttpTransport _transport;
    private readonly bool _allowLoopbackHttp;
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(10);
    private const int MetadataLimit = 256 * 1024;
    private const int MaxTokenBytes = 16 * 1024;

    public RemoteOAuthClient(IRemoteHttpTransport? transport = null)
        : this(transport, allowLoopbackHttpEndpoints: false)
    {
    }

    /// <param name="allowLoopbackHttpEndpoints">
    /// Tests only: lets the issuer's endpoints be loopback HTTP, so a local fake can
    /// stand in for a provider. Never set outside a test.
    /// </param>
    internal RemoteOAuthClient(IRemoteHttpTransport? transport, bool allowLoopbackHttpEndpoints)
    {
        _transport = transport ?? new HttpClientRemoteTransport();
        _allowLoopbackHttp = allowLoopbackHttpEndpoints;
    }

    // MARK: - Resource identity

    /// <summary>The exact resource a credential belongs to.</summary>
    /// <remarks>
    /// Lower-case scheme and host, no default port, no trailing slash, no fragment — so
    /// two spellings of one endpoint share a sign-in and two endpoints never do. The
    /// query stays, because two servers on one path told apart by a query are two
    /// sign-ins.
    /// </remarks>
    public static string CanonicalResource(string text)
    {
        var uri = RemoteHttpEndpoint.Validate(text);
        var origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        var path = uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped).TrimEnd('/');
        return origin + path + uri.Query;
    }

    /// <summary>Whether protected-resource metadata may speak for the endpoint.</summary>
    /// <remarks>
    /// Same origin, and a path that is the endpoint's or a parent of it on a segment
    /// boundary. Root metadata describes the origin (RFC 9728 §3.3), so an exact match
    /// would refuse every server found through the root fallback.
    /// </remarks>
    internal static bool ResourceCovers(string described, Uri endpoint)
    {
        Uri url;
        try
        {
            url = RemoteHttpEndpoint.Validate(described);
        }
        catch (RemoteHttpException)
        {
            return false;
        }
        if (!SameOrigin(url, endpoint)) return false;
        if (url.Query.Length > 0 && url.Query != endpoint.Query) return false;
        var parent = url.AbsolutePath.TrimEnd('/');
        var path = endpoint.AbsolutePath.TrimEnd('/');
        return parent.Length == 0 || path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);
    }

    // MARK: - Discovery

    /// <param name="authorization">
    /// The saved credential's headers, when signing in again. The probe then carries
    /// them, so a server that needs more scope says which in a 403
    /// <c>insufficient_scope</c> challenge (step-up).
    /// </param>
    /// <param name="previousScopes">
    /// What the saved credential was granted. A step-up asks for these and the
    /// challenged ones together, so it gains scope rather than trading one for another.
    /// </param>
    public async Task<RemoteOAuthConfiguration> DiscoverAsync(
        string resourceUrl,
        string? challenge = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? authorization = null,
        IReadOnlyList<string>? previousScopes = null)
    {
        try
        {
            return await DiscoverCoreAsync(resourceUrl, challenge, authorization, previousScopes ?? [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RemoteHttpException failure)
        {
            // Transport trouble while discovering reads as "no usable metadata", and
            // carries none of what the server said.
            throw new RemoteOAuthException(RemoteOAuthError.Discovery, failure);
        }
    }

    private async Task<RemoteOAuthConfiguration> DiscoverCoreAsync(
        string resourceUrl,
        string? challenge,
        IReadOnlyDictionary<string, string>? authorization,
        IReadOnlyList<string> previousScopes,
        CancellationToken cancellationToken)
    {
        var canonical = CanonicalResource(resourceUrl);
        var resource = RemoteHttpEndpoint.Validate(canonical);
        // The resource indicator is sent to the issuer. A query on it is the user's
        // own business for a check and a leak for a sign-in: refuse rather than hand a
        // URL's query string to a third party.
        if (resource.Query.Length > 0) throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        var deadline = DateTimeOffset.UtcNow + StepBudget;

        if (challenge is null && authorization is not null)
        {
            // Step-up: ask with the token on file. A server that wants more answers 403
            // with the scope it needs; one that is content answers 2xx and the ordinary
            // probe below finds its metadata.
            List<KeyValuePair<string, string>> headers =
            [
                new("Accept", "application/json, text/event-stream"),
                new("MCP-Protocol-Version", McpStdioClient.ModernProtocolVersion),
                new("Mcp-Method", "tools/list"),
                .. authorization,
            ];
            try
            {
                var stepUp = await _transport.SendAsync(
                    new RemoteHttpRequest(
                        HttpMethod.Post,
                        resource,
                        headers,
                        Encoding.UTF8.GetBytes(McpStdioClient.Request(1, "tools/list", null, modern: true)),
                        "application/json"),
                    deadline,
                    MetadataLimit,
                    responseId: 1,
                    cancellationToken).ConfigureAwait(false);
                if (stepUp.Status is 401 or 403) challenge = stepUp.Header("www-authenticate");
            }
            catch (RemoteHttpException)
            {
                // The ordinary probe below decides.
            }
        }

        if (challenge is null)
        {
            var probe = await _transport.SendAsync(
                new RemoteHttpRequest(
                    HttpMethod.Post,
                    resource,
                    [new("Accept", "application/json, text/event-stream")],
                    Encoding.UTF8.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" +
                        McpHttpClient.PreferredProtocolVersion +
                        "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Kytto\",\"version\":\"1.0\"}}}"),
                    "application/json"),
                deadline,
                MetadataLimit,
                responseId: 1,
                cancellationToken).ConfigureAwait(false);
            challenge = probe.Header("www-authenticate");
        }

        var parameters = ChallengeParameters(challenge);
        IReadOnlyList<Uri> candidates;
        if (parameters.TryGetValue("resource_metadata", out var hinted))
        {
            var hint = RemoteHttpEndpoint.Validate(hinted, allowLoopbackHttp: _allowLoopbackHttp);
            // The specification lets a hint point anywhere. Kytto does not follow one
            // off the resource's own origin: a challenge must not be a way to make
            // this app request an arbitrary address.
            if (!SameOrigin(hint, resource)) throw new RemoteOAuthException(RemoteOAuthError.ResourceMismatch);
            candidates = [hint];
        }
        else
        {
            var list = WellKnown(resource, "oauth-protected-resource", includeAppend: false);
            if (resource.AbsolutePath != "/" && resource.AbsolutePath.Length > 0)
            {
                list.Add(new Uri(resource.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource"));
            }
            candidates = list;
        }

        var metadata = await FirstMetadataAsync(candidates, deadline, cancellationToken).ConfigureAwait(false);
        if (metadata.Root["resource"]?.StringValue is not { } described ||
            !ResourceCovers(described, resource))
        {
            throw new RemoteOAuthException(RemoteOAuthError.ResourceMismatch);
        }

        var issuers = metadata.Root["authorization_servers"]?.Elements?
            .Select(node => node.StringValue).OfType<string>().ToArray();
        if (issuers is not { Length: > 0 and <= 16 }) throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        // The first advertised issuer. Not a choice Kytto makes for the person.
        var issuer = issuers[0];
        var issuerUrl = ValidEndpoint(issuer);
        if (issuerUrl.Query.Length > 0) throw new RemoteOAuthException(RemoteOAuthError.Discovery);

        var discovery = WellKnown(issuerUrl, "oauth-authorization-server", includeAppend: false);
        discovery.AddRange(WellKnown(issuerUrl, "openid-configuration", includeAppend: true));
        var authorizationServer = await FirstMetadataAsync(discovery, deadline, cancellationToken).ConfigureAwait(false);

        if (authorizationServer.Root["issuer"]?.StringValue != issuer) throw new RemoteOAuthException(RemoteOAuthError.IssuerMismatch);
        if (authorizationServer.Root["code_challenge_methods_supported"]?.Elements?.Any(node => node.StringValue == "S256") != true)
        {
            throw new RemoteOAuthException(RemoteOAuthError.PkceUnsupported);
        }
        if (authorizationServer.Root["response_types_supported"]?.Elements is { } modes &&
            !modes.Any(node => node.StringValue == "code"))
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        }

        string Endpoint(string key)
        {
            var value = authorizationServer.Root[key]?.StringValue ?? throw new RemoteOAuthException(RemoteOAuthError.Discovery);
            ValidEndpoint(value);
            return value;
        }

        IReadOnlyList<string> scopes;
        if (parameters.TryGetValue("scope", out var required))
        {
            scopes = ParseScopes(required);
        }
        else
        {
            scopes = metadata.Root["scopes_supported"]?.Elements?.Select(node => node.StringValue).OfType<string>().ToArray() ?? [];
            ParseScopes(string.Join(' ', scopes));
            scopes = scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        if (previousScopes.Count > 0 && scopes.Count > 0)
        {
            scopes = ParseScopes(string.Join(' ', scopes.Concat(previousScopes)));
        }

        var registration = authorizationServer.Root["registration_endpoint"]?.StringValue;
        if (registration is not null) ValidEndpoint(registration);

        return new RemoteOAuthConfiguration(
            canonical,
            issuer,
            Endpoint("authorization_endpoint"),
            Endpoint("token_endpoint"),
            registration,
            scopes,
            authorizationServer.Root["authorization_response_iss_parameter_supported"]?.BoolValue == true,
            described);
    }

    // MARK: - Registration

    /// <summary>Public dynamic registration, offered only if the issuer advertises it.</summary>
    public async Task<string> RegisterAsync(
        RemoteOAuthConfiguration configuration,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ValidateRedirect(redirectUri);
        if (configuration.RegistrationEndpoint is not { } endpoint) throw new RemoteOAuthException(RemoteOAuthError.RegistrationUnavailable);

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            client_name = "Kytto MCP",
            redirect_uris = new[] { redirectUri },
            // Required in registration requests since MCP 2026-07-28 (SEP-837).
            application_type = "native",
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        });
        var reply = await SendOAuthAsync(
            new RemoteHttpRequest(HttpMethod.Post, ValidEndpoint(endpoint), [new("Accept", "application/json")], body, "application/json"),
            cancellationToken).ConfigureAwait(false);

        if (reply.Status is < 200 or >= 300 ||
            RemoteJson.TryParseObject(reply.Body) is not { } document ||
            document.Root["client_id"]?.StringValue is not { } clientId ||
            !ValidClient(clientId) ||
            // A public client has no secret. One that is handed back is a confidential
            // registration Kytto has nothing to do with.
            !string.IsNullOrEmpty(document.Root["client_secret"]?.StringValue) ||
            (document.Root["token_endpoint_auth_method"]?.StringValue is { } method && method != "none"))
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidClient);
        }
        if (document.Root["redirect_uris"]?.Elements is { } redirects &&
            !redirects.Any(node => node.StringValue == redirectUri))
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidClient);
        }
        return clientId;
    }

    // MARK: - Authorization

    public RemoteOAuthPendingAuthorization PrepareAuthorization(
        RemoteOAuthConfiguration configuration,
        string clientId,
        string redirectUri)
    {
        if (!ValidClient(clientId)) throw new RemoteOAuthException(RemoteOAuthError.InvalidClient);
        ValidateRedirect(redirectUri);

        var verifier = RemoteOAuthCredential.RandomBase64Url(32);
        var state = RemoteOAuthCredential.RandomBase64Url(32);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var endpoint = ValidEndpoint(configuration.AuthorizationEndpoint);
        var reserved = new[]
        {
            "response_type", "client_id", "redirect_uri", "state", "code_challenge",
            "code_challenge_method", "resource", "scope",
        };
        var existing = endpoint.Query.TrimStart('?');
        if (existing.Length > 0 && existing.Split('&').Any(item =>
                reserved.Contains(Uri.UnescapeDataString(item.Split('=')[0]), StringComparer.Ordinal)))
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        }

        var query = new StringBuilder(existing);
        void Add(string name, string value)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        Add("response_type", "code");
        Add("client_id", clientId);
        Add("redirect_uri", redirectUri);
        Add("state", state);
        Add("code_challenge", challenge);
        Add("code_challenge_method", "S256");
        Add("resource", configuration.ResourceIndicator);
        if (configuration.Scopes.Count > 0) Add("scope", string.Join(' ', configuration.Scopes));

        var url = new UriBuilder(endpoint) { Query = query.ToString() }.Uri;
        return new RemoteOAuthPendingAuthorization(url, configuration, clientId, redirectUri, verifier, state, DateTimeOffset.UtcNow);
    }

    // MARK: - Tokens

    /// <summary>Trades a callback for tokens, if and only if it is exactly this sign-in's.</summary>
    /// <remarks>
    /// The transaction is spent before anything is checked, so a callback can be tried
    /// once: a wrong one ends the attempt instead of leaving it open to guessing. A
    /// tampered callback is rejected before any token request is made.
    /// </remarks>
    public async Task<RemoteOAuthCredential> ExchangeAsync(
        RemoteOAuthPendingAuthorization pending,
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        pending.Consume(DateTimeOffset.UtcNow);

        var parameters = ParseCallback(pending, callback);
        if (!FixedTimeEquals(parameters.GetValueOrDefault("state"), pending.State))
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
        }
        if (parameters.TryGetValue("iss", out var issuer))
        {
            if (issuer != pending.Configuration.Issuer) throw new RemoteOAuthException(RemoteOAuthError.IssuerMismatch);
        }
        else if (pending.Configuration.AuthorizationResponseIssuerSupported)
        {
            throw new RemoteOAuthException(RemoteOAuthError.IssuerMismatch);
        }
        if (parameters.ContainsKey("error")) throw new RemoteOAuthException(RemoteOAuthError.AuthorizationRejected);
        if (!parameters.TryGetValue("code", out var code) || code.Length == 0 || Encoding.UTF8.GetByteCount(code) > 8_192)
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
        }

        return await TokensAsync(
            pending.Configuration,
            pending.ClientId,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.RedirectUri,
                ["code_verifier"] = pending.Verifier,
            },
            previous: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new access token from the saved refresh token; the rotated one is kept.</summary>
    public async Task<RemoteOAuthCredential> RefreshAsync(
        RemoteOAuthCredential credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(credential.RefreshToken)) throw new RemoteOAuthException(RemoteOAuthError.RefreshUnavailable);
        try
        {
            return await TokensAsync(
                credential.Configuration,
                credential.ClientId,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken,
                },
                credential,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteOAuthException failure) when (failure.Error == RemoteOAuthError.InvalidToken)
        {
            // Whatever the provider answered, the saved sign-in cannot be renewed.
            throw new RemoteOAuthException(RemoteOAuthError.RefreshUnavailable, failure);
        }
    }

    private async Task<RemoteOAuthCredential> TokensAsync(
        RemoteOAuthConfiguration configuration,
        string clientId,
        Dictionary<string, string> fields,
        RemoteOAuthCredential? previous,
        CancellationToken cancellationToken)
    {
        fields["client_id"] = clientId;
        fields["resource"] = configuration.ResourceIndicator;
        var form = string.Join('&', fields
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

        var reply = await SendOAuthAsync(
            new RemoteHttpRequest(
                HttpMethod.Post,
                ValidEndpoint(configuration.TokenEndpoint),
                [new("Accept", "application/json")],
                Encoding.UTF8.GetBytes(form),
                "application/x-www-form-urlencoded"),
            cancellationToken).ConfigureAwait(false);

        if (reply.Status is < 200 or >= 300 ||
            RemoteJson.TryParseObject(reply.Body) is not { } document ||
            document.Root["access_token"]?.StringValue is not { Length: > 0 } token ||
            !ValidToken(token) ||
            !string.Equals(document.Root["token_type"]?.StringValue, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidToken);
        }

        // The authorization server may grant other scopes than requested (RFC 6749
        // §3.3) — its default when none were asked for, or a normalised spelling. What
        // it granted is what is recorded.
        var requested = previous?.Scopes ?? configuration.Scopes;
        var granted = document.Root["scope"]?.StringValue is { } scope ? ParseScopes(scope) : requested;

        DateTimeOffset? expiry = null;
        if (document.Root["expires_in"] is { IsNull: false } lifetime)
        {
            // Some servers send it as a string.
            var seconds = lifetime.NumberValue ??
                (double.TryParse(lifetime.StringValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
            if (seconds is not { } value || !double.IsFinite(value) || value <= 0 || value > 366 * 24 * 3600)
            {
                throw new RemoteOAuthException(RemoteOAuthError.InvalidToken);
            }
            expiry = DateTimeOffset.UtcNow.AddSeconds(value);
        }

        // Rotation: a refresh token in the response replaces the saved one; none
        // means the saved one is still the one to use.
        var refresh = document.Root["refresh_token"]?.StringValue ?? previous?.RefreshToken;
        if (refresh is not null && !ValidToken(refresh)) throw new RemoteOAuthException(RemoteOAuthError.InvalidToken);

        return new RemoteOAuthCredential(configuration, clientId, granted, expiry, token, refresh);
    }

    private async Task<RemoteHttpReply> SendOAuthAsync(RemoteHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.SendAsync(
                request,
                DateTimeOffset.UtcNow + StepBudget,
                MetadataLimit,
                responseId: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteHttpException failure)
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery, failure);
        }
    }

    // MARK: - Callback

    private static Dictionary<string, string> ParseCallback(RemoteOAuthPendingAuthorization pending, Uri callback)
    {
        var expected = new Uri(pending.RedirectUri);
        if (callback.AbsoluteUri.Length > 16_384 ||
            !callback.IsAbsoluteUri ||
            callback.Fragment.Length > 0 ||
            callback.Scheme != expected.Scheme ||
            !string.Equals(callback.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            callback.Port != expected.Port ||
            callback.AbsolutePath != expected.AbsolutePath)
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = callback.Query.TrimStart('?');
        if (query.Length == 0) return parameters;
        foreach (var item in query.Split('&'))
        {
            var separator = item.IndexOf('=');
            if (separator < 0) throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
            var key = Decode(item[..separator]);
            var value = Decode(item[(separator + 1)..]);
            // A repeated name is ambiguous, and an ambiguous callback is not honoured.
            if (!parameters.TryAdd(key, value)) throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
        }
        return parameters;
    }

    private static string Decode(string component)
    {
        var spaced = component.Replace('+', ' ');
        for (var index = 0; index < spaced.Length; index++)
        {
            if (spaced[index] != '%') continue;
            if (index + 2 >= spaced.Length ||
                !Uri.IsHexDigit(spaced[index + 1]) || !Uri.IsHexDigit(spaced[index + 2]))
            {
                throw new RemoteOAuthException(RemoteOAuthError.InvalidCallback);
            }
        }
        return Uri.UnescapeDataString(spaced);
    }

    private static bool FixedTimeEquals(string? left, string right) =>
        left is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    // MARK: - Metadata

    /// <summary>The first candidate that answers with a JSON object.</summary>
    /// <remarks>
    /// Clients must try the well-known locations in order, so a 401 from auth
    /// middleware, a redirect (never followed) or an HTML catch-all page moves on to
    /// the next one rather than ending discovery.
    /// </remarks>
    private async Task<JsonDocument> FirstMetadataAsync(
        IReadOnlyList<Uri> candidates,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var reply = await _transport.SendAsync(
                new RemoteHttpRequest(HttpMethod.Get, candidate, [new("Accept", "application/json")]),
                deadline,
                MetadataLimit,
                responseId: null,
                cancellationToken).ConfigureAwait(false);
            if (reply.Status == 200 && RemoteJson.TryParseObject(reply.Body) is { } document) return document;
        }
        throw new RemoteOAuthException(RemoteOAuthError.Discovery);
    }

    private Uri ValidEndpoint(string text)
    {
        Uri uri;
        try
        {
            uri = RemoteHttpEndpoint.Validate(text, allowLoopbackHttp: _allowLoopbackHttp);
        }
        catch (RemoteHttpException failure)
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery, failure);
        }

        // The resource is the user's own address, and may be on their network. An
        // issuer, an endpoint or a registration address is a server's word, and must
        // not aim Kytto at this machine or at a private address: that is how a metadata
        // document would turn a sign-in into a request to something internal.
        if (!_allowLoopbackHttp && RemoteHttpEndpoint.IsLocalOrPrivateLiteral(uri.Host))
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        }
        return uri;
    }

    /// <summary><c>/.well-known/&lt;suffix&gt;</c> inserted before the path, or appended after it.</summary>
    /// <remarks>RFC 8414 §3.1: a trailing slash is removed before insertion.</remarks>
    internal static List<Uri> WellKnown(Uri url, string suffix, bool includeAppend)
    {
        var authority = url.GetLeftPart(UriPartial.Authority);
        var path = url.AbsolutePath.TrimEnd('/');
        var result = new List<Uri> { new(authority + "/.well-known/" + suffix + path) };
        if (includeAppend && path.Length > 0)
        {
            result.Add(new Uri(authority + "/" + path.Trim('/') + "/.well-known/" + suffix));
        }
        return result;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool ValidClient(string value) =>
        value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= 8_192 && value.All(c => c is >= '!' and <= '~');

    private static bool ValidToken(string value) =>
        value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= MaxTokenBytes && value.All(c => c is >= '!' and <= '~');

    /// <summary>A redirect Kytto can actually receive: loopback, with a port, nothing else.</summary>
    internal static void ValidateRedirect(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttp ||
            url.IsDefaultPort ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            url.Query.Length > 0 ||
            url.Fragment.Length > 0 ||
            !(url.Host is "127.0.0.1" or "[::1]"))
        {
            throw new RemoteOAuthException(RemoteOAuthError.InvalidRedirect);
        }
    }

    internal static IReadOnlyList<string> ParseScopes(string text)
    {
        // RFC 6749 §3.3: printable ASCII except '"' and '\', space-separated.
        if (Encoding.UTF8.GetByteCount(text) > 8_192 ||
            !text.All(c => c == ' ' || c == '!' || (c >= '#' && c <= '[') || (c >= ']' && c <= '~')))
        {
            throw new RemoteOAuthException(RemoteOAuthError.Discovery);
        }
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A bounded RFC 6750 Bearer challenge parser, quoted commas and escapes included.</summary>
    internal static Dictionary<string, string> ChallengeParameters(string? header)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (header is null || Encoding.UTF8.GetByteCount(header) > 16_384) return parameters;

        var bearer = header.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (bearer < 0 || (bearer > 0 && header[bearer - 1] is not (' ' or ','))) return parameters;

        var index = bearer + "Bearer ".Length;
        while (index < header.Length)
        {
            while (index < header.Length && header[index] is ' ' or ',') index++;
            var keyStart = index;
            while (index < header.Length && (char.IsLetterOrDigit(header[index]) || header[index] == '_')) index++;
            var key = header[keyStart..index];
            while (index < header.Length && header[index] == ' ') index++;
            if (key.Length == 0 || index >= header.Length || header[index] != '=') break;
            index++;
            while (index < header.Length && header[index] == ' ') index++;

            var value = new StringBuilder();
            if (index < header.Length && header[index] == '"')
            {
                index++;
                var closed = false;
                while (index < header.Length)
                {
                    var next = header[index++];
                    if (next == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (next == '\\' && index < header.Length) value.Append(header[index++]);
                    else value.Append(next);
                }
                if (!closed) return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                while (index < header.Length && header[index] is not (',' or ' ')) value.Append(header[index++]);
            }

            // A repeated parameter is ambiguous; the whole challenge is then ignored.
            if (!parameters.TryAdd(key.ToLowerInvariant(), value.ToString())) return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return parameters;
    }
}
