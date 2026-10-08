using System.Collections.ObjectModel;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 车上有多条需求的货时，故障交接与强制机械恢复要操作员先选一条（8005-agv-onboard-hmi#209）。
/// </summary>
/// <remarks>
/// <para>
/// 不用清单选中行：清单只列当前这一站，在别的站装上的货不在里面，而途中故障时要交接的正是它们。列表来自日志簿里按需求保存的
/// 装货上下文（业务服务的 <c>RecoveryDemandChoices</c>），重启、重连之后照样还原。
/// </para>
/// <para>
/// 只剩一条、或有在途操作时列表为空，入口照旧指向那一条，什么都不用选。有多条而没有选中时两个按钮留在原位但禁用，旁边提示先选；
/// 从不替操作员默认选一条：交接的是他要亲手打开、清空的那几扇门。
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private const string RecoveryDemandSelectionHint = "请先在「车上待交接的需求」中选择要处理的一条。";

    private Func<IReadOnlyList<WireToGateRecoveryOperationContext>>? _recoveryDemandChoices;
    private Func<string?, string?, CancellationToken, Task<bool>>? _faultCargoHandoffForDemandRequester;
    private Func<string?, string?, CancellationToken, Task<bool>>? _forcedMechanicalRecoveryForDemandRequester;
    private RecoveryDemandChoiceRow? _selectedRecoveryDemandChoice;

    /// <summary>车上待交接的需求，最早装的在前。只剩一条或有在途操作时为空。</summary>
    public ObservableCollection<RecoveryDemandChoiceRow> RecoveryDemandChoices { get; } = [];

    public RecoveryDemandChoiceRow? SelectedRecoveryDemandChoice
    {
        get => _selectedRecoveryDemandChoice;
        set
        {
            if (SetProperty(ref _selectedRecoveryDemandChoice, value))
            {
                RaiseRecoveryDemandChoiceState();
            }
        }
    }

    /// <summary>
    /// 列表在不在屏上：有可选的行，而且交接或强制恢复的入口至少一个真的在屏上。与 <see cref="HasRecoveryFallbackTarget"/> 同一个
    /// 道理：没有凭据的车上一个按钮都不出现，孤零零挂一张列表说的是一个按不了的东西。
    /// </summary>
    public bool ShowsRecoveryDemandChoices =>
        RecoveryDemandChoices.Count > 0 && (CanRequestFaultCargoHandoff || CanRequestForcedMechanicalRecovery);

    /// <summary>列表在屏上而还没有选中一行。</summary>
    public bool HasRecoveryDemandSelectionHint => ShowsRecoveryDemandChoices && SelectedRecoveryDemandChoice is null;

    public string RecoveryDemandSelectionHintText =>
        HasRecoveryDemandSelectionHint ? RecoveryDemandSelectionHint : string.Empty;

    /// <summary>「故障交接」按不按得动：入口出现，并且不欠一个选择。</summary>
    public bool CanPressFaultCargoHandoff => CanRequestFaultCargoHandoff && !HasRecoveryDemandSelectionHint;

    /// <summary>「强制机械恢复」按不按得动：入口出现，并且不欠一个选择。</summary>
    public bool CanPressForcedMechanicalRecovery =>
        CanRequestForcedMechanicalRecovery && !HasRecoveryDemandSelectionHint;

    internal void ConfigureRecoveryDemandChoices(
        Func<IReadOnlyList<WireToGateRecoveryOperationContext>> choices,
        Func<string?, string?, CancellationToken, Task<bool>> faultCargoHandoffRequester,
        Func<string?, string?, CancellationToken, Task<bool>> forcedMechanicalRecoveryRequester)
    {
        _recoveryDemandChoices = choices ?? throw new ArgumentNullException(nameof(choices));
        _faultCargoHandoffForDemandRequester = faultCargoHandoffRequester
            ?? throw new ArgumentNullException(nameof(faultCargoHandoffRequester));
        _forcedMechanicalRecoveryForDemandRequester = forcedMechanicalRecoveryRequester
            ?? throw new ArgumentNullException(nameof(forcedMechanicalRecoveryRequester));
        RunOnUiThread(RefreshRecoveryDemandChoicesCore);
    }

    /// <summary>
    /// 按下时交给业务服务的那条需求：列表在用时是选中行的，否则 <c>null</c>——只剩一条时什么都不传，与今天一样。
    /// </summary>
    private string? ChosenRecoveryDemandId => RecoveryDemandChoices.Count > 0 ? SelectedRecoveryDemandChoice?.DemandId : null;

    private Task<bool> RequestFaultCargoHandoffForChoiceAsync(CancellationToken cancellationToken)
    {
        string? demandId = ChosenRecoveryDemandId;
        return _faultCargoHandoffForDemandRequester is { } requester
            ? RequestWithReasonAsync((reason, token) => requester(reason, demandId, token), cancellationToken)
            : RequestWithReasonAsync(_wireToGateFaultCargoHandoffRequester, cancellationToken);
    }

    private Task<bool> RequestForcedMechanicalRecoveryForChoiceAsync(CancellationToken cancellationToken)
    {
        string? demandId = ChosenRecoveryDemandId;
        return _forcedMechanicalRecoveryForDemandRequester is { } requester
            ? RequestWithReasonAsync((reason, token) => requester(reason, demandId, token), cancellationToken)
            : RequestWithReasonAsync(_wireToGateForcedMechanicalRecoveryRequester, cancellationToken);
    }

    /// <summary>
    /// 按业务服务给的那份重建列表；内容没变就不动，选中行按 <c>DemandId</c> 找回。选中的那条已经不在了（交接完、卸完）时选择清空，
    /// 下一次按下得到「先选一条」，而不是悄悄换成另一条。
    /// </summary>
    private void RefreshRecoveryDemandChoicesCore()
    {
        if (_recoveryDemandChoices is null)
        {
            return;
        }

        RecoveryDemandChoiceRow[] rows =
        [
            .. _recoveryDemandChoices().Select(load => new RecoveryDemandChoiceRow(
                load.DemandId,
                SublotOf(load.DemandId) is { } sublot
                    ? $"子批 {sublot} / {SlotsText(load.Slots)}"
                    : SlotsText(load.Slots)))
        ];
        if (!rows.SequenceEqual(RecoveryDemandChoices))
        {
            string? selected = SelectedRecoveryDemandChoice?.DemandId;
            RecoveryDemandChoices.Clear();
            foreach (RecoveryDemandChoiceRow row in rows)
            {
                RecoveryDemandChoices.Add(row);
            }

            SelectedRecoveryDemandChoice = RecoveryDemandChoices.FirstOrDefault(row => row.DemandId == selected);
        }

        RaiseRecoveryDemandChoiceState();
    }

    private void RaiseRecoveryDemandChoiceState()
    {
        OnPropertyChanged(nameof(ShowsRecoveryDemandChoices));
        OnPropertyChanged(nameof(HasRecoveryDemandSelectionHint));
        OnPropertyChanged(nameof(RecoveryDemandSelectionHintText));
        OnPropertyChanged(nameof(CanPressFaultCargoHandoff));
        OnPropertyChanged(nameof(CanPressForcedMechanicalRecovery));
    }

    private static string SlotsText(IReadOnlyList<int> slots) => $"{string.Join("、", slots)}号仓";
}
