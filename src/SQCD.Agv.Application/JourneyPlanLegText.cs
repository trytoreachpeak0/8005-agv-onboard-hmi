namespace SQCD.Agv.Application;

/// <summary>计划腿列表的文案。</summary>
public static class JourneyPlanLegText
{
    public static string StopPurpose(string category) => category;

    public static string LegType(string? legType) => legType ?? string.Empty;

    public static string State(string state) => state;
}
