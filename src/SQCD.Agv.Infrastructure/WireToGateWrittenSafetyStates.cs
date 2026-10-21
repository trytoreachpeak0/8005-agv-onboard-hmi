using System.Globalization;
using System.Text.Json;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Every <c>safetyStateVersion</c> this client has written to a connection, with the content it carried
/// (8005-agv-onboard-hmi#394). A version names one safety state: once a <c>SafetyStateChanged</c> or
/// <c>SafetyStateSnapshot</c> has gone onto the wire under it, no other content may go out under it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written, not accepted.</b> The accepted version only moves on an acknowledgement; a snapshot whose ack is lost on a
/// roaming link has still been seen, or may have been, by the server. So the record is made at the write, in
/// <c>SendLineAsync</c>, the one place every line passes through -- a first send, a durable resend and a handshake
/// replay alike.
/// </para>
/// <para>
/// <b>For one process.</b> The configured version starts every process again; the server's own scope, one generation, is
/// narrower still (control-server <c>WireToGateStore.ApplyRevision</c>). Only the handshake numbers from this record;
/// the business service keeps numbering above the accepted version, which the handshake moves above everything written,
/// and gives up a change it still held whose version a handshake went past (<c>SettlePassedSafetyChangeAsync</c>).
/// </para>
/// <para>
/// The contents are kept for the last <see cref="RememberedCount"/> versions only, to say in the log when a version went
/// out twice with different content. Nothing is refused on it: a line refused here would be a durable row the next
/// handshake replays and refuses again.
/// </para>
/// </remarks>
internal sealed class WireToGateWrittenSafetyStates
{
    internal const int RememberedCount = 64;

    private readonly object _gate = new();
    private readonly Dictionary<long, string> _contents = [];
    private readonly Queue<long> _order = new();
    private long? _highest;

    /// <summary>The highest version written so far, or null when this client has written none.</summary>
    internal long? Highest
    {
        get
        {
            lock (_gate)
            {
                return _highest;
            }
        }
    }

    /// <summary>
    /// The version a handshake snapshot goes out under: the accepted one while nothing has been written -- the configured
    /// baseline of a fresh process, which names nothing yet -- and above everything written and accepted after that.
    /// </summary>
    internal long NextHandshakeVersion(long acceptedVersion)
    {
        lock (_gate)
        {
            return _highest is long highest
                ? checked(Math.Max(highest, acceptedVersion) + 1)
                : acceptedVersion;
        }
    }

    /// <summary>
    /// Whether a stored <c>SafetyStateChanged</c> line may go out again under its version (8005-agv-onboard-hmi#394 review
    /// M1): yes when that version was written with this same content, or lies above everything written; no when it went
    /// out with other content, or lies at or below the highest written without this content on record -- a version a
    /// handshake numbered past, or one too old to tell.
    /// </summary>
    internal bool MayReplay(string line)
    {
        (long version, string content) = Parse(line)
            ?? throw new InvalidDataException("Not a safety state line.");
        lock (_gate)
        {
            if (_contents.TryGetValue(version, out string? earlier))
            {
                return string.Equals(earlier, content, StringComparison.Ordinal);
            }

            return _highest is not long highest || version > highest;
        }
    }

    /// <summary>
    /// Records <paramref name="line"/> if it is a safety state. Returns a diagnostic when its version was written before
    /// with other content, otherwise null.
    /// </summary>
    internal string? Record(string line)
    {
        if (Parse(line) is not { } parsed)
        {
            return null;
        }

        (long version, string content) = parsed;

        lock (_gate)
        {
            _highest = _highest is long highest ? Math.Max(highest, version) : version;
            if (_contents.TryGetValue(version, out string? earlier))
            {
                return string.Equals(earlier, content, StringComparison.Ordinal)
                    ? null
                    : $"安全状态版本号{version.ToString(CultureInfo.InvariantCulture)}已用于另一份内容又被写出："
                        + $"先前={earlier}；这次={content}。";
            }

            _contents[version] = content;
            _order.Enqueue(version);
            while (_order.Count > RememberedCount)
            {
                _contents.Remove(_order.Dequeue());
            }

            return null;
        }
    }

    /// <summary>The version and content of a safety state line, or null for any other line.</summary>
    private static (long Version, string Content)? Parse(string line)
    {
        // Cheap enough for every line: the parse only runs for the two safety messages.
        if (!line.Contains("\"SafetyState", StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string messageType = root.GetProperty("messageType").GetString() ?? string.Empty;
        if (messageType is not ("SafetyStateChanged" or "SafetyStateSnapshot"))
        {
            return null;
        }

        JsonElement payload = root.GetProperty("payload");
        return (payload.GetProperty("safetyStateVersion").GetInt64(), messageType + " " + payload.GetRawText());
    }
}
