using SQCD.Agv.Core;

namespace SQCD.Agv.Application;

public enum OperatorRecordKind
{
    System,
    Operation,
    Success,
    Warning,
    Error
}

public sealed record OperatorRecord(
    DateTimeOffset Timestamp,
    OperatorRecordKind Kind,
    string Message,
    string DeduplicationKey);

/// <summary>
/// 将控制器的高频状态快照转换成操作员真正关心的事件记录。
/// 技术状态变化仍写入磁盘日志，不在这里重复展示。
/// </summary>
public sealed class OperatorRecordFormatter
{
    private OnboardSnapshot? _previous;
    private ActiveOperation? _lastOperation;
    private string? _lastRecordKey;
    private bool _initialConnectionCompleted;

    public OperatorRecord? Format(OnboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.ActiveOperation is not null)
        {
            _lastOperation = snapshot.ActiveOperation;
        }

        OperatorRecord? candidate = CreateRecord(snapshot);
        if (snapshot.State is OnboardState.WaitingArrival or OnboardState.ReadyToScan)
        {
            _initialConnectionCompleted = true;
        }

        _previous = snapshot;
        if (candidate is null || candidate.DeduplicationKey == _lastRecordKey)
        {
            return null;
        }

        _lastRecordKey = candidate.DeduplicationKey;
        return candidate;
    }

    private OperatorRecord? CreateRecord(OnboardSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
        {
            OperatorRecordKind kind = snapshot.State == OnboardState.Faulted
                ? OperatorRecordKind.Error
                : OperatorRecordKind.Warning;
            return New(
                snapshot,
                kind,
                snapshot.Guidance,
                $"error:{snapshot.ErrorCode}:{snapshot.Guidance}:{snapshot.ActiveOperation?.OperationId}");
        }

        if (snapshot.Guidance.Contains("启动安全复核通过", StringComparison.Ordinal))
        {
            return New(
                snapshot,
                OperatorRecordKind.Success,
                "启动安全复核通过，已保留当前仓位货物状态。",
                "startup-review-passed");
        }

        if (snapshot.Guidance.Contains("操作已取消", StringComparison.Ordinal))
        {
            return New(
                snapshot,
                OperatorRecordKind.Warning,
                snapshot.Guidance,
                $"operation-cancelled:{_lastOperation?.OperationId}");
        }

        if (snapshot.State == OnboardState.ReadyToScan
            && snapshot.Guidance.Contains("操作完成", StringComparison.Ordinal))
        {
            ActiveOperation? completed = _lastOperation;
            _lastOperation = null;
            if (completed is null)
            {
                return New(snapshot, OperatorRecordKind.Success, "装卸操作完成，可以继续扫码。", "operation-completed");
            }

            string action = completed.OperationType == OperationType.Load ? "装货" : "取货";
            return New(
                snapshot,
                OperatorRecordKind.Success,
                $"{completed.SlotIndex + 1}号仓{action}完成，仓门和货物状态已确认。",
                $"operation-completed:{completed.OperationId}");
        }

        return snapshot.State switch
        {
            OnboardState.Starting => New(
                snapshot,
                OperatorRecordKind.System,
                "车载端正在启动。",
                "system-starting"),

            OnboardState.Connecting when !_initialConnectionCompleted
                && _previous?.State != OnboardState.Connecting => New(
                    snapshot,
                    OperatorRecordKind.System,
                    "正在连接仓门控制设备和任务系统。",
                    "initial-connecting"),

            OnboardState.Connecting when _initialConnectionCompleted => New(
                snapshot,
                OperatorRecordKind.Warning,
                GetDisconnectedMessage(snapshot),
                $"connection-interrupted:{snapshot.IoConnected}:{snapshot.RuleConnected}"),

            OnboardState.WaitingArrival => New(
                snapshot,
                OperatorRecordKind.System,
                "设备连接正常，车辆在途，等待到站通知。",
                $"waiting-arrival:{snapshot.Visit?.VisitId}"),

            OnboardState.ReadyToScan when _previous?.State == OnboardState.Connecting
                && _initialConnectionCompleted => New(
                    snapshot,
                    OperatorRecordKind.Success,
                    "设备连接已恢复，可以继续扫码。",
                    $"connection-restored:{snapshot.Visit?.VisitId}"),

            OnboardState.ReadyToScan when _previous?.State != OnboardState.ReadyToScan
                || _previous?.Visit?.VisitId != snapshot.Visit?.VisitId => New(
                    snapshot,
                    OperatorRecordKind.System,
                    "车辆已到站，可以扫描物料条码。",
                    $"ready-to-scan:{snapshot.Visit?.VisitId}"),

            OnboardState.Verifying => New(
                snapshot,
                OperatorRecordKind.Operation,
                snapshot.Guidance.TrimEnd('…', '.'),
                $"verifying:{snapshot.Guidance}"),

            OnboardState.Operating => New(
                snapshot,
                OperatorRecordKind.Operation,
                snapshot.Guidance,
                $"operating:{snapshot.ActiveOperation?.OperationId}:{snapshot.Guidance}"),

            OnboardState.Reporting => null,
            OnboardState.Faulted => New(
                snapshot,
                OperatorRecordKind.Error,
                snapshot.Guidance,
                $"fault:{snapshot.Guidance}"),
            _ => null
        };
    }

    private static string GetDisconnectedMessage(OnboardSnapshot snapshot)
    {
        if (!snapshot.IoConnected && !snapshot.RuleConnected)
        {
            return "仓门控制设备和任务系统连接中断，正在自动恢复。";
        }

        if (!snapshot.IoConnected)
        {
            return "仓门控制设备连接中断，正在自动恢复。";
        }

        if (!snapshot.RuleConnected)
        {
            return "任务系统连接中断，正在自动恢复。";
        }

        return "设备连接状态正在恢复。";
    }

    private static OperatorRecord New(
        OnboardSnapshot snapshot,
        OperatorRecordKind kind,
        string message,
        string key) =>
        new(snapshot.UpdatedAt, kind, message, key);
}
