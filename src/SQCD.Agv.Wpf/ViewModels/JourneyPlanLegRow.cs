using System.Globalization;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 计划腿列表的一行（批次7-13，<c>8005-agv-onboard-hmi#134</c>）。
/// </summary>
/// <remarks>
/// 整行不可变，计划换修订号时整张表重建。<see cref="ItemStatus"/> 是给 UIA 读的原始值
/// <c>sequence|stopPurposeCategory|state</c>，服务端 G3 场景据此判顺序与状态，不比中文全文。
/// </remarks>
public sealed record JourneyPlanLegRow(
    int Sequence,
    string StationId,
    string StopPurposeCategory,
    string StopPurposeText,
    string LegTypeText,
    string State,
    string StateText)
{
    public string SequenceText => Sequence.ToString(CultureInfo.InvariantCulture);

    public string ItemStatus => string.Create(CultureInfo.InvariantCulture, $"{Sequence}|{StopPurposeCategory}|{State}");
}
