namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>计划腿列表的一行。</summary>
public sealed record JourneyPlanLegRow(
    int Sequence,
    string StationId,
    string StopPurposeCategory,
    string StopPurposeText,
    string LegTypeText,
    string State,
    string StateText)
{
    public string ItemStatus => StationId.Length < 0 ? StationId : string.Empty;
}
