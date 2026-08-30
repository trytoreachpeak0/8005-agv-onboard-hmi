using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class LockerCardViewModel : ViewModelBase
{
    private bool _isKnown;
    private bool _isLocked;
    private bool _hasCargo;
    private bool _doActive;
    private bool _isTarget;
    private OperationType? _operationType;
    private OperationStage _operationStage;
    private int _reopenAttempts;
    private string? _wireToGateTargetText;

    public LockerCardViewModel(int slotIndex)
    {
        SlotIndex = slotIndex;
        PhysicalNumber = slotIndex + 1;
    }

    public int SlotIndex { get; }

    public int PhysicalNumber { get; }

    public string Title => $"{PhysicalNumber}号仓";

    public bool IsKnown
    {
        get => _isKnown;
        private set => SetProperty(ref _isKnown, value);
    }

    public bool IsTarget
    {
        get => _isTarget;
        private set
        {
            if (SetProperty(ref _isTarget, value))
            {
                OnPropertyChanged(nameof(TargetText));
            }
        }
    }

    public string DoorText => !IsKnown ? "仓门：状态未知" : _isLocked ? "仓门：已锁" : "仓门：未锁";

    public string CargoText => !IsKnown ? "货物：状态未知" : _hasCargo ? "货物：有货" : "货物：空仓";

    public string RawIoText => !IsKnown
        ? "DO:-  锁DI:-  光幕DI:-"
        : $"DO:{(_doActive ? 1 : 0)}  锁DI:{(_isLocked ? 1 : 0)}  光幕DI:{(_hasCargo ? 0 : 1)}";

    public string TargetText => !IsTarget
        ? string.Empty
        : _wireToGateTargetText is not null
            ? _wireToGateTargetText
        : _operationStage == OperationStage.Failed
            ? "当前操作异常"
            : _operationStage == OperationStage.WaitingOperatorRecovery
                ? "装卸未完成，等待处理"
                : _operationStage == OperationStage.WaitingUnlockOutputReset
                    ? "正在确认开门控制复位"
                : _reopenAttempts > 0
                    && (_operationStage is OperationStage.WritingUnlock or OperationStage.WaitingUnlockFeedback)
                    ? "正在重新打开仓门"
                    : _operationType == OperationType.Load ? "当前装料仓位" : "当前卸料仓位";

    public void Update(
        LockerSnapshot snapshot,
        ActiveOperation? activeOperation,
        WireToGateHmiOperationSnapshot? wireToGateOperation = null)
    {
        IsKnown = snapshot.IsKnown;
        _isLocked = snapshot.IsLocked;
        _hasCargo = snapshot.HasCargo;
        _doActive = snapshot.UnlockOutputRaw is true;
        bool wireToGateTarget = wireToGateOperation?.Slots.Contains(PhysicalNumber) == true;
        bool legacyTarget = activeOperation?.SlotIndex == SlotIndex;
        bool isTarget = wireToGateTarget || legacyTarget;
        _operationType = wireToGateTarget
            ? wireToGateOperation!.OperationType
            : legacyTarget ? activeOperation?.OperationType : null;
        _operationStage = wireToGateTarget
            ? MapOperationStage(wireToGateOperation!.Stage)
            : legacyTarget ? activeOperation?.Stage ?? OperationStage.None : OperationStage.None;
        _reopenAttempts = legacyTarget ? activeOperation?.ReopenAttempts ?? 0 : 0;
        _wireToGateTargetText = wireToGateTarget
            ? WireToGateTargetText(wireToGateOperation!)
            : null;
        IsTarget = isTarget;
        OnPropertyChanged(nameof(DoorText));
        OnPropertyChanged(nameof(CargoText));
        OnPropertyChanged(nameof(RawIoText));
        OnPropertyChanged(nameof(TargetText));
    }

    private static OperationStage MapOperationStage(WireToGateHmiOperationStage stage) => stage switch
    {
        WireToGateHmiOperationStage.Preparing => OperationStage.Precheck,
        WireToGateHmiOperationStage.Unlocking => OperationStage.WritingUnlock,
        WireToGateHmiOperationStage.WaitingOperator => OperationStage.WaitingCargoAndRelock,
        WireToGateHmiOperationStage.Verifying => OperationStage.ReportingResult,
        WireToGateHmiOperationStage.Reporting => OperationStage.ReportingResult,
        WireToGateHmiOperationStage.Completed => OperationStage.Completed,
        WireToGateHmiOperationStage.RecoveryRequired => OperationStage.WaitingOperatorRecovery,
        _ => OperationStage.None
    };

    private static string WireToGateTargetText(WireToGateHmiOperationSnapshot operation) => operation.Stage switch
    {
        WireToGateHmiOperationStage.Preparing => "正在进行操作前安全检查",
        WireToGateHmiOperationStage.Unlocking => "正在打开仓门",
        WireToGateHmiOperationStage.WaitingOperator => operation.OperationType == OperationType.Load
            ? "请放入货物并关门"
            : "请取出货物并关门",
        WireToGateHmiOperationStage.Verifying => "正在核对物理状态",
        WireToGateHmiOperationStage.Reporting => "安全收尾，正在上报结果",
        WireToGateHmiOperationStage.Completed => "操作完成",
        WireToGateHmiOperationStage.RecoveryRequired => "操作未完成，需要管理员恢复",
        _ => "当前目标仓"
    };
}
