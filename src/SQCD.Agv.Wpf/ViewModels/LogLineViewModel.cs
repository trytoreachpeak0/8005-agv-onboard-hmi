using System.Globalization;
using SQCD.Agv.Application;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>One line of the operator record (「操作记录」) on screen.</summary>
/// <param name="Source">Which of the write sites produced it; written to the technical log by <c>OperatorRecordLog</c> (onboard-hmi#317), not shown on screen.</param>
/// <param name="AttemptId">The slot operation attempt the line is about, when its source knows one; technical log only.</param>
/// <param name="EventKind">The business event's own kind (for example <c>SUBLOT_REJECTED</c>) for <see cref="OperatorRecordSource.OperatorEvent"/>; technical log only.</param>
public sealed record LogLineViewModel(
    DateTimeOffset Timestamp,
    OperatorRecordKind Kind,
    string Message,
    OperatorRecordSource Source = OperatorRecordSource.Unknown,
    string? AttemptId = null,
    string? EventKind = null)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CategoryText => Kind switch
    {
        OperatorRecordKind.System => "系统",
        OperatorRecordKind.Operation => "操作",
        OperatorRecordKind.Success => "成功",
        OperatorRecordKind.Warning => "提醒",
        OperatorRecordKind.Error => "故障",
        _ => "记录"
    };
}

/// <summary>
/// The four places that add a line to <c>MainViewModel.Logs</c> (research <c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>,
/// section 2, O3). The name is written into the technical log as is, so renaming one changes the log format.
/// </summary>
public enum OperatorRecordSource
{
    /// <summary>A line whose write site did not say; a new write site that forgets to is visible in the log as this.</summary>
    Unknown,

    /// <summary>A business service operator event (<c>ApplyWireToGateOperatorEvent</c>).</summary>
    OperatorEvent,

    /// <summary>The notice that a correction window closed with the next load (<c>AnnounceClosedCorrectionWindowCore</c>).</summary>
    CorrectionWindow,

    /// <summary>A business rejection put in front of the operator (<c>ReportOperatorRejection</c>).</summary>
    OperatorRejection,

    /// <summary>A controller snapshot turned into a record by <c>OperatorRecordFormatter</c> (<c>AppendOperatorRecord</c>).</summary>
    ControllerRecord
}
