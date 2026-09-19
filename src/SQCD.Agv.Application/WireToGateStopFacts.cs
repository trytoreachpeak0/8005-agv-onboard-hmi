using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

/// <summary>
/// 旅程事实一行里的方向与任务类型文案（批次6-03，<c>8005-agv-onboard-hmi#115</c>）。桩：实现随下一个提交。
/// </summary>
public static class WireToGateStopFacts
{
    public const string UnknownTaskTypeText = "未知";

    public static string TaskTypeText(WireToGateJourneySnapshot journey) => string.Empty;

    public static string DirectionText(WireToGateJourneySnapshot journey) => string.Empty;
}
