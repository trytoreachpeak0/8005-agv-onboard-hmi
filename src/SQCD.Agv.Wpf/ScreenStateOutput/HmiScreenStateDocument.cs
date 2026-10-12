using System.Text.Json;
using System.Text.Json.Serialization;
using SQCD.Agv.Wpf.ScreenState;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>
/// What <c>hmi-state.json</c> holds, and each line of <c>hmi-state-yyyyMMdd.ndjson</c>: the writer's metadata around one
/// <see cref="HmiScreenState"/> (research section 4.1, onboard-hmi#315).
/// </summary>
/// <remarks>
/// <para>
/// <b>Status.</b> <see cref="HmiScreenStateDocumentStatus.Active"/> while the snapshot runs. When a dispatcher entry point
/// fails the snapshot stops for the rest of the process and the last document is
/// <see cref="HmiScreenStateDocumentStatus.Disabled"/>, with no state. T3 tells "the snapshot stopped itself" (status
/// <c>DISABLED</c>) from "the dispatcher is stuck" (<see cref="WrittenAt"/> not advancing while status is <c>ACTIVE</c>).
/// </para>
/// <para>
/// <b>Content boundary.</b> <see cref="DisabledReason"/> is the exception's type name only, never its message: a message
/// can carry whatever the failing code had in hand, a sublot included. The message goes to the technical log, which
/// already records sublots.
/// </para>
/// </remarks>
/// <param name="ContentRevision">Goes up by one each time the content (the state without its build time) changes.</param>
/// <param name="WrittenAt">When this document was written; every write updates it, heartbeats included.</param>
/// <param name="ProcessStartId">A fresh id per process, so a reader can tell a restart from an unchanged screen.</param>
internal sealed record HmiScreenStateDocument(
    int SchemaVersion,
    HmiScreenStateDocumentStatus Status,
    long ContentRevision,
    DateTimeOffset WrittenAt,
    string ProcessStartId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ProgramVersion,
    string? DisabledEntry,
    string? DisabledReason,
    DateTimeOffset? DisabledAt,
    HmiScreenState? State)
{
    internal const int CurrentSchemaVersion = 1;

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
        WriteIndented = false,
    };

    internal byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>
    /// The content the revision counts: the state without its build time. The countdown's per-second text is not in the
    /// state at all (tier and deadline only), so a running countdown does not change this.
    /// </summary>
    internal static string Fingerprint(HmiScreenState state) =>
        JsonSerializer.Serialize(state with { BuiltAt = default }, JsonOptions);
}

internal enum HmiScreenStateDocumentStatus
{
    Active,
    Disabled,
}
