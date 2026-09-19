using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>持货等单那一行显示哪一种。</summary>
public enum LoadingPhaseLine
{
    None,
    CargoHolding,
    VehicleFull,
    Closed
}

/// <summary>持货等单那一行。</summary>
public sealed record LoadingPhaseView(LoadingPhaseLine Line, string Text, string Code);

/// <summary>持货等单文案。</summary>
public static class LoadingPhaseText
{
    public static LoadingPhaseView Describe(WireToGateLoadingPhase? phase, DateTimeOffset now, TimeZoneInfo zone) =>
        new(LoadingPhaseLine.None, string.Empty, string.Empty);
}
