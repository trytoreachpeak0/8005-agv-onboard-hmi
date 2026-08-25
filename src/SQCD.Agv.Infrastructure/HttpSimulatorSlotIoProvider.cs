using System.Net.Http.Json;
using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class HttpSimulatorSlotIoProvider(HttpClient httpClient) : ISlotIoProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SlotIoState>> ReadAllAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            "/api/v1/slots", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        SlotStateDto[] body = await response.Content.ReadFromJsonAsync<SlotStateDto[]>(
            SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("IO simulator returned an empty response.");
        if (body.Length != 8 || body.Select(item => item.SlotNo).Distinct().Count() != 8 ||
            body.Any(item => item.SlotNo is < 1 or > 8))
        {
            throw new InvalidDataException("IO simulator must return exactly slots 1..8.");
        }
        return body.OrderBy(item => item.SlotNo).Select(ToDomain).ToArray();
    }

    public async Task PulseUnlockAsync(
        IReadOnlyList<int> physicalSlotNumbers,
        CancellationToken cancellationToken)
    {
        int[] normalized = physicalSlotNumbers.Distinct().Order().ToArray();
        if (normalized.Length == 0 || normalized.Length != physicalSlotNumbers.Count ||
            normalized.Any(slot => slot is < 1 or > 8))
        {
            throw new ArgumentException("Target slots must be a unique non-empty subset of 1..8.", nameof(physicalSlotNumbers));
        }
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/api/v1/unlock", new { slotNumbers = normalized }, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SlotIoState ToDomain(SlotStateDto dto) => new(
        dto.SlotNo,
        dto.Online,
        ParseOccupancy(dto.Occupancy),
        dto.LockState switch
        {
            "LOCKED" => SlotDoorLock.Locked,
            "NOT_LOCKED" => SlotDoorLock.NotLocked,
            _ => SlotDoorLock.Unknown
        },
        dto.UnlockOutputState switch
        {
            "RESET" => UnlockOutputState.Reset,
            "ACTIVE" => UnlockOutputState.Active,
            _ => UnlockOutputState.Unknown
        },
        dto.ObservedAt);

    private static SlotOccupancy ParseOccupancy(string value) => value switch
    {
        "EMPTY" => SlotOccupancy.Empty,
        "OCCUPIED" => SlotOccupancy.Occupied,
        _ => SlotOccupancy.Unknown
    };

    private sealed record SlotStateDto(
        int SlotNo,
        bool Online,
        string Occupancy,
        string LockState,
        string UnlockOutputState,
        string LightCurtainState,
        DateTimeOffset ObservedAt);
}
