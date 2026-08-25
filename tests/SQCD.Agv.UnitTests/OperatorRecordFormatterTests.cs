using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class OperatorRecordFormatterTests
{
    [Fact]
    public void RepeatedOperatingGuidanceAcrossInternalStagesIsRecordedOnce()
    {
        OperatorRecordFormatter formatter = new();
        ActiveOperation writing = CreateOperation(OperationStage.WritingUnlock);
        ActiveOperation waiting = writing with { Stage = OperationStage.WaitingUnlockFeedback };

        OperatorRecord? first = formatter.Format(CreateSnapshot(
            OnboardState.Operating,
            "正在开启1号仓…",
            operation: writing));
        OperatorRecord? duplicate = formatter.Format(CreateSnapshot(
            OnboardState.Operating,
            "正在开启1号仓…",
            operation: waiting));

        Assert.NotNull(first);
        Assert.Equal(OperatorRecordKind.Operation, first.Kind);
        Assert.Null(duplicate);
    }

    [Fact]
    public void CompletedOperationCreatesPhysicalSlotSuccessRecord()
    {
        OperatorRecordFormatter formatter = new();
        ActiveOperation operation = CreateOperation(OperationStage.WaitingCargoAndRelock);
        _ = formatter.Format(CreateSnapshot(
            OnboardState.Operating,
            "1号仓已打开，请放入货物并手动关闭仓门。",
            operation: operation));
        _ = formatter.Format(CreateSnapshot(
            OnboardState.Reporting,
            "状态已确认，正在提交操作结果。",
            operation: operation with { Stage = OperationStage.ReportingResult }));

        OperatorRecord? completed = formatter.Format(CreateSnapshot(
            OnboardState.ReadyToScan,
            "操作完成，可以继续扫描。"));

        Assert.NotNull(completed);
        Assert.Equal(OperatorRecordKind.Success, completed.Kind);
        Assert.Equal("1号仓装货完成，仓门和货物状态已确认。", completed.Message);
    }

    [Fact]
    public void IntermediateStartupConnectionsAreCollapsed()
    {
        OperatorRecordFormatter formatter = new();

        OperatorRecord? starting = formatter.Format(CreateSnapshot(OnboardState.Starting, "系统正在启动…"));
        OperatorRecord? connecting = formatter.Format(CreateSnapshot(OnboardState.Connecting, "正在连接…"));
        OperatorRecord? intermediate = formatter.Format(CreateSnapshot(
            OnboardState.Connecting,
            "正在等待任务系统连接…",
            ioConnected: true));
        OperatorRecord? waitingArrival = formatter.Format(CreateSnapshot(
            OnboardState.WaitingArrival,
            "设备已连接，等待到站通知…",
            ioConnected: true,
            ruleConnected: true));

        Assert.NotNull(starting);
        Assert.NotNull(connecting);
        Assert.Null(intermediate);
        Assert.NotNull(waitingArrival);
    }

    [Fact]
    public void BlockingFaultUsesErrorRecordKind()
    {
        OperatorRecordFormatter formatter = new();

        OperatorRecord? record = formatter.Format(CreateSnapshot(
            OnboardState.Faulted,
            "仓门控制设备连接中断，所有仓位状态无法确认。",
            "IO_OFFLINE"));

        Assert.NotNull(record);
        Assert.Equal(OperatorRecordKind.Error, record.Kind);
    }

    private static ActiveOperation CreateOperation(OperationStage stage) =>
        new(
            "VISIT-001",
            "OP-001",
            "TASK-001",
            "LOAD-001",
            0,
            OperationType.Load,
            stage,
            DateTimeOffset.Now);

    private static OnboardSnapshot CreateSnapshot(
        OnboardState state,
        string guidance,
        string? errorCode = null,
        ActiveOperation? operation = null,
        bool ioConnected = false,
        bool ruleConnected = false)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        IoSnapshot io = new(
            ioConnected,
            Enumerable.Range(0, 8)
                .Select(index => new LockerSnapshot(index, index + 1, false, true, true, now))
                .ToArray(),
            now);
        return new OnboardSnapshot(
            state,
            ruleConnected,
            ioConnected,
            null,
            io,
            operation,
            false,
            guidance,
            errorCode,
            now);
    }
}
