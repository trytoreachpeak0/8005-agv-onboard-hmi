namespace SQCD.Agv.Wpf.ScreenState;

/// <summary>
/// The controls of <c>MainWindow.xaml</c> the snapshot reports, with the binding paths written there. The builder
/// evaluates these paths against the view model, so the snapshot reads exactly the property the screen is bound to;
/// <c>HmiScreenStateXamlCoverageArchitectureTests</c> reads the XAML and requires this table to match it both ways.
/// </summary>
internal static class HmiScreenBindings
{
    /// <summary>Every <c>Button</c> in the window, in XAML order.</summary>
    internal static IReadOnlyList<ButtonBinding> Buttons { get; } =
    [
        Command("ManualSubmitCommand", "手动提交"),
        Click("OnWireToGateRecoveryClick", "申请恢复", "CanRequestWireToGateRecovery", "CanRequestWireToGateRecovery"),
        // 文字是绑定（待送点取货停靠上叫「撤销本次开仓」，8005-agv-onboard-hmi#348），没有字面量可记。
        Click("OnLoadCancellationClick", null, "CanRequestLoadCancellation", "CanPressLoadCancellation"),
        Click("OnLoadCompensationClick", "补偿清空", "CanRequestLoadCompensation", "CanRequestLoadCompensation"),
        Click("OnLoadCorrectionClick", "修正装货", "CanRequestLoadCorrection", "CanRequestLoadCorrection"),
        Click("OnFaultCargoHandoffClick", "故障交接", "CanRequestFaultCargoHandoff", "CanPressFaultCargoHandoff"),
        Click(
            "OnForcedMechanicalRecoveryClick",
            "强制机械恢复",
            "CanRequestForcedMechanicalRecovery",
            "CanPressForcedMechanicalRecovery"),
        Click(
            "OnConfirmForcedMechanicalRecoveryClick",
            "已隔离并完成机械取出",
            "CanConfirmForcedMechanicalRecovery",
            "CanSubmitForcedMechanicalRecoveryConfirmation"),
        Click(
            "OnSubmitHardwareRecoveryRecordClick",
            "提交硬件恢复记录",
            "CanSubmitHardwareRecoveryRecord",
            "CanSubmitHardwareRecoveryRecord"),
        Click(
            "OnHardwareRepairReleaseClick",
            "维修放行",
            "CanRequestHardwareRepairRelease",
            "CanRequestHardwareRepairRelease",
            "HardwareRepairRelease"),
        Click(
            "OnCloseConflictedRecoveryClick",
            "人工核对后结束此恢复",
            "CanCloseConflictedRecovery",
            "CanCloseConflictedRecovery",
            "CloseConflictedRecovery"),
        Click("OnManualChargingReturnClick", "充电后返回服务", "CanRequestManualChargingReturn", "CanRequestManualChargingReturn"),
        Click(
            "OnConfirmStationClearanceClick",
            "确认清桩",
            "StationClearance.CanConfirm",
            "StationClearance.CanConfirm",
            "ConfirmStationClearance"),
        Click(
            "OnConfirmHeldRecoveryCommandClick",
            "确认执行",
            "HeldRecoveryCommand.CanConfirm",
            "HeldRecoveryCommand.CanConfirm",
            "ConfirmHeldRecoveryCommand"),
        Click(
            "OnDeclineHeldRecoveryCommandClick",
            "不执行",
            "HeldRecoveryCommand.CanDecline",
            "HeldRecoveryCommand.CanDecline",
            "DeclineHeldRecoveryCommand"),
        Click("OnRetryPendingResultClick", "重新上报结果", "CanRetryPendingResult", "CanRetryPendingResult"),
        // v2: the slot held shut over the opposite occupancy (8005-agv-onboard-hmi#385); MVP: the controller's reopen.
        Click("OnReopenOperationClick", "重新开门", "CanReopenOperation", "CanReopenOperation", "ReopenOperation"),
        Click("OnCancelOperationClick", "取消本次操作", "CanCancelOperation", "CanCancelOperation"),
        Click("OnSafetyReviewClick", "启动安全复核", "CanSafetyReview", "CanSafetyReview"),
        Click("OnClearFatalFaultClick", "复位严重安全故障", "CanClearFatalFault", "CanClearFatalFault"),
        new ButtonBinding(
            "OnConfirmUnableToChargeClick",
            ButtonKind.Template,
            null,
            null,
            "UnableToCharge.CanConfirm",
            null,
            null,
            "UnableToCharge.Options"),
        Click(
            "OnCurrentStopDepartureClick",
            "出发",
            "CurrentStopDeparture.IsShown",
            "CurrentStopDeparture.CanPress",
            "CurrentStopDeparture"),
        Command("ClearLogsCommand", "清空界面记录"),
    ];

    /// <summary>
    /// Every <c>Button</c> in the worklist's row template, in XAML order: one per row, reported on the row
    /// (<see cref="HmiWorklistRowState"/>), with its <c>Visibility</c> and <c>IsEnabled</c> paths read off the row, not the view
    /// model (8005-agv-onboard-hmi#353).
    /// </summary>
    internal static IReadOnlyList<ButtonBinding> WorklistRowButtons { get; } =
    [
        Click(
            "OnWorklistRowCancellationClick",
            "取消",
            "OffersCancellation",
            "CanPressCancellation",
            "WorklistItemCancellation"),
    ];

    /// <summary>Every editable <c>TextBox</c> in the window, in XAML order.</summary>
    internal static IReadOnlyList<InputBinding> Inputs { get; } =
    [
        new("ScanTextBox", null, "CanSubmit", "ScanText", "ScannerSubmitCommand"),
        new("RecoveryReason", "HasRecoveryReasonInput", "IsRecoveryReasonEditable", "RecoveryReason", null),
        new("ForcedHandoffSublot", "NeedsForcedCargoHandoff", null, "ForcedHandoffSublot", null),
        new("ForcedHandoffReceiverName", "NeedsForcedCargoHandoff", null, "ForcedHandoffReceiverName", null),
        new("HardwareRecoveryObservations", "CanSubmitHardwareRecoveryRecord", null, "HardwareRecoveryObservations", null),
    ];

    /// <summary>
    /// Every element outside an item template that carries a bound <c>AutomationProperties.ItemStatus</c>, in XAML order,
    /// with the nearest <c>Visibility</c> binding on it or above it.
    /// </summary>
    internal static IReadOnlyList<RawCodeBinding> RawCodes { get; } =
    [
        new("BatteryStatus", "BatteryStatus", null),
        new("ChargingStatus", "ChargingStatus", null),
        new("IdleReturnStatus", "IdleReturnStatus", null),
        new("StationDepartureCountdown", "StationDepartureCountdownTier", "HasStationDepartureCountdown"),
        new("SublotRejectionReason", "SublotRejectionReasonCode", "HasSublotRejection"),
        new("DoorHoldNotice", "DoorHoldSlots", "HasDoorHold"),
        new("NoOnboardExitNotice", "NoOnboardExitNoticeStatus", "HasNoOnboardExitNotice"),
        // 「恢复尚未开放，请联系系统维护人员。」：值为 RECOVERY_AUTHENTICATION_FAILED（8005-agv-onboard-hmi#391）。
        new("RecoveryNotOpenNotice", "RecoveryNotOpenNoticeCode", "HasRecoveryNotOpenNotice"),
        new("ReopenOperation", "ReopenWaitStatus", "CanReopenOperation"),
        new("StationClearanceStatus", "StationClearance.Status", "StationClearance.HasStatus"),
        new("UnableToChargeStatus", "UnableToCharge.Status", "UnableToCharge.HasStatus"),
        new("CargoHoldingCountdown", "CargoHoldingCountdownStatus", "HasCargoHoldingCountdown"),
        new("LoadingClosedReason", "LoadingClosedReasonCode", "HasLoadingClosedReason"),
        new("StagingSummary", "CurrentStopDeparture.SummaryCode", "CurrentStopDeparture.HasSummary"),
        new("CurrentStopDepartureNotice", "CurrentStopDeparture.NoticeCode", "CurrentStopDeparture.HasNotice"),
        new("CurrentStopDepartureStatus", "CurrentStopDeparture.Status", "CurrentStopDeparture.HasStatus"),
    ];

    private static ButtonBinding Click(
        string handler,
        string? content,
        string visibility,
        string isEnabled,
        string? automationId = null) =>
        new(handler, ButtonKind.Click, automationId, content, visibility, isEnabled, null, null);

    private static ButtonBinding Command(string command, string content) =>
        new(command, ButtonKind.Command, null, content, null, null, command, null);

    internal enum ButtonKind
    {
        /// <summary>A <c>Click</c> handler, with <c>IsEnabled</c> and <c>Visibility</c> bindings.</summary>
        Click,

        /// <summary>A <c>Command</c> binding: enabled is <c>CanExecute</c>.</summary>
        Command,

        /// <summary>A button in an item template, one per item, with no binding of its own; enabled is "there are items".</summary>
        Template,
    }

    /// <param name="Key">The <c>Click</c> handler, or the command path.</param>
    /// <param name="Visibility">The button's own <c>Visibility</c> binding; for a template button, the nearest one above it.</param>
    /// <param name="ItemsSource">For a template button, the <c>ItemsSource</c> of its <c>ItemsControl</c>.</param>
    internal sealed record ButtonBinding(
        string Key,
        ButtonKind Kind,
        string? AutomationId,
        string? Content,
        string? Visibility,
        string? IsEnabled,
        string? Command,
        string? ItemsSource);

    /// <param name="Key">The <c>AutomationId</c>, or the <c>x:Name</c> when there is none.</param>
    /// <param name="KeyCommand">The <c>Command</c> of a <c>KeyBinding</c> in the box's <c>InputBindings</c>.</param>
    internal sealed record InputBinding(string Key, string? Visibility, string? IsEnabled, string Text, string? KeyCommand);

    internal sealed record RawCodeBinding(string AutomationId, string ItemStatus, string? Visibility);
}
