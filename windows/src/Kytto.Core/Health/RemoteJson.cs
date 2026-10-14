using System.Text;
using System.Text.Json;
using Kytto.Core.Json;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Health;

/// <summary>Untrusted JSON from a remote server, parsed under limits (§6, §7.3).</summary>
/// <remarks>
/// <para>
/// Strict first, lossless second. The standard library validates the text — valid
/// UTF-8, no comments, no trailing commas, nesting no deeper than 128 — and only then
/// is it handed to the span-keeping parser the rest of Kytto reads configs with, which
/// would otherwise accept more than JSON does. Duplicate keys are refused outright:
/// two clients can resolve one differently, and what the model is shown must not
/// depend on which.
/// </para>
/// </remarks>
internal static class RemoteJson
{
    internal const int MaxNesting = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = MaxNesting };

    /// <summary>The text, or null when the bytes are not valid UTF-8.</summary>
    internal static string? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A parsed document, or null for anything out of bounds or ambiguous.</summary>
    internal static JsonDocument? TryParse(string text)
    {
        try
        {
            using (System.Text.Json.JsonDocument.Parse(text, Strict))
            {
                // Validation only; the spans come from the parser below.
            }
            var document = JsonDocument.Parse(text);
            return Unambiguous(document.Root) ? document : null;
        }
        catch (Exception error) when (error is JsonException or JsonParseException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static JsonDocument? TryParse(ReadOnlySpan<byte> bytes) =>
        Decode(bytes) is { } text ? TryParse(text) : null;

    /// <summary>A JSON object, for metadata and token responses; anything else is null.</summary>
    internal static JsonDocument? TryParseObject(ReadOnlySpan<byte> bytes) =>
        TryParse(bytes) is { } document && document.Root.Members is not null ? document : null;

    private static bool Unambiguous(JsonNode node)
    {
        if (node.Members is { } members)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!names.Add(member.Key) || !Unambiguous(member.Value)) return false;
            }
            return true;
        }
        if (node.Elements is { } elements)
        {
            foreach (var element in elements)
            {
                if (!Unambiguous(element)) return false;
            }
        }
        return true;
    }

    /// <summary>Whether this message is the response to the request with this id.</summary>
    internal static bool IsResponseTo(JsonDocument document, int id) =>
        document.Root["jsonrpc"]?.StringValue == "2.0" &&
        document.Root["id"]?.NumberValue is { } number && number == id &&
        (document.Root["result"] is not null || document.Root["error"] is not null);
}

/// <summary>
/// Reads a Server-Sent Events stream as it arrives, complete events only (§7.3).
/// </summary>
/// <remarks>
/// A Streamable HTTP server may answer a POST with an event stream and then keep it
/// open. This reports each finished event as soon as its blank line has been read, so
/// the caller can stop at the matching response instead of waiting for an end that
/// may never come. Comments and heartbeats (<c>:</c> lines) and every field other
/// than <c>data</c> are ignored; several <c>data</c> lines join with a newline. An
/// event the stream ends in the middle of was never complete and is never reported.
/// </remarks>
internal sealed class ServerSentEventReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly List<byte> _line = [];
    private readonly List<string> _data = [];
    private readonly int? _responseId;
    private bool _afterCarriageReturn;

    internal ServerSentEventReader(int? responseId = null) => _responseId = responseId;

    /// <summary>Every complete event's data, in order.</summary>
    internal List<string> Events { get; } = [];

    /// <summary>The event that answered the request, once it has been read.</summary>
    internal bool Matched { get; private set; }

    /// <summary>Feeds bytes; returns true as soon as the matching response is complete.</summary>
    internal bool Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var current in bytes)
        {
            Accept(current);
            if (Matched) return true;
        }
        return Matched;
    }

    private void Accept(byte current)
    {
        if (_afterCarriageReturn)
        {
            _afterCarriageReturn = false;
            if (current == (byte)'\n') return;
        }
        if (current is (byte)'\r' or (byte)'\n')
        {
            _afterCarriageReturn = current == (byte)'\r';
            EndLine();
            return;
        }
        _line.Add(current);
    }

    private void EndLine()
    {
        var bytes = _line.ToArray();
        _line.Clear();

        // Strip the byte order mark of the first line, now that it is known to be one.
        if (Events.Count == 0 && _data.Count == 0 && bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        if (bytes.Length == 0)
        {
            Dispatch();
            return;
        }
        if (bytes[0] == (byte)':') return;

        var colon = Array.IndexOf(bytes, (byte)':');
        var name = colon < 0 ? bytes : bytes[..colon];
        if (!name.AsSpan().SequenceEqual("data"u8)) return;

        var value = colon < 0 ? [] : bytes[(colon + 1)..];
        if (value.Length > 0 && value[0] == (byte)' ') value = value[1..];
        try
        {
            _data.Add(StrictUtf8.GetString(value));
        }
        catch (DecoderFallbackException)
        {
            throw RemoteHttpException.Protocol("the event stream was not valid UTF-8");
        }
    }

    private void Dispatch()
    {
        if (_data.Count == 0) return;
        var message = string.Join('\n', _data);
        _data.Clear();
        Events.Add(message);

        if (_responseId is { } id && RemoteJson.TryParse(message) is { } document && RemoteJson.IsResponseTo(document, id))
        {
            Matched = true;
        }
    }

    /// <summary>The complete events in a finished body.</summary>
    internal static IReadOnlyList<string> Messages(ReadOnlySpan<byte> body)
    {
        var reader = new ServerSentEventReader();
        reader.Feed(body);
        return reader.Events;
    }
}
