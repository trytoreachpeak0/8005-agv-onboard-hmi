using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>清单项的侧。</summary>
public sealed record WorklistItemSide(string Text, string Code);

/// <summary>清单项标侧。</summary>
public static class WorklistItemSides
{
    public static WorklistItemSide Resolve(
        string demandId,
        IReadOnlyDictionary<string, IReadOnlyList<int>> commandSlotsByDemand,
        WireToGateRecoveryState? journaled,
        SlotGroupLayout layout) => new(string.Empty, string.Empty);
}
