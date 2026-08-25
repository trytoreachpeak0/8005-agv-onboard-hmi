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

    public void Update(LockerSnapshot snapshot, ActiveOperation? activeOperation)
    {
        IsKnown = snapshot.IsKnown;
        _isLocked = snapshot.IsLocked;
        _hasCargo = snapshot.HasCargo;
        _doActive = snapshot.UnlockOutputRaw is true;
        bool isTarget = activeOperation?.SlotIndex == SlotIndex;
        _operationType = isTarget ? activeOperation?.OperationType : null;
        _operationStage = isTarget ? activeOperation?.Stage ?? OperationStage.None : OperationStage.None;
        _reopenAttempts = isTarget ? activeOperation?.ReopenAttempts ?? 0 : 0;
        IsTarget = isTarget;
        OnPropertyChanged(nameof(DoorText));
        OnPropertyChanged(nameof(CargoText));
        OnPropertyChanged(nameof(RawIoText));
        OnPropertyChanged(nameof(TargetText));
    }
}
