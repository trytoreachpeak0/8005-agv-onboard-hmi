using System.Text.Json;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The invariant of 8005-agv-onboard-hmi#394: one <c>safetyStateVersion</c> names one safety state. Every
/// <c>SafetyStateChanged</c> and <c>SafetyStateSnapshot</c> the vehicle put on any connection is grouped by its version,
/// and each group must carry one content -- the same message type and the same payload, byte for byte. A resend of a
/// durable change carries its stored payload and passes; a handshake snapshot that keeps a version already spent on other
/// content does not.
/// </summary>
/// <remarks>
/// Envelope fields (<c>messageId</c>, <c>sessionGeneration</c>, <c>sentAt</c>) are left out: a resend rebinds them by design.
/// The real server compares within one generation only (control-server <c>WireToGateStore.ApplyRevision</c>), so a version
/// kept across generations is refused nowhere -- but the vehicle's log, the server's log and the people reading both then
/// see one number for two states, which is how the 10-21 agv03 stop was first misread.
/// </remarks>
internal static class SafetyRevisionContent
{
    /// <summary>Each version that went out with more than one content, with every distinct content it carried.</summary>
    public static string[] Conflicts(IEnumerable<(int Connection, string MessageType, string MessageId, string WireLine)> received) =>
        received
            .Where(item => item.MessageType is "SafetyStateChanged" or "SafetyStateSnapshot")
            .Select(item => (item.Connection, Content: ContentOf(item.MessageType, item.WireLine)))
            .GroupBy(item => item.Content.Version)
            .Where(group => group.Select(item => item.Content.Text).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"v{group.Key}: "
                + string.Join(
                    " <> ",
                    group.Select(item => $"[connection {item.Connection}] {item.Content.Text}")
                        .Distinct(StringComparer.Ordinal)))
            .ToArray();

    public static void AssertNone(IEnumerable<(int Connection, string MessageType, string MessageId, string WireLine)> received)
    {
        string[] conflicts = Conflicts(received);
        Xunit.Assert.True(
            conflicts.Length == 0,
            "one safetyStateVersion went out with different content: " + string.Join(" || ", conflicts));
    }

    private static (long Version, string Text) ContentOf(string messageType, string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        JsonElement payload = document.RootElement.GetProperty("payload");
        return (payload.GetProperty("safetyStateVersion").GetInt64(), messageType + " " + payload.GetRawText());
    }
}
