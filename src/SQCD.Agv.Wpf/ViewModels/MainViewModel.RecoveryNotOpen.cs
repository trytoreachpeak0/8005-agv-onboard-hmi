using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 恢复尚未开放（8005-agv-onboard-hmi#391，control-server#797 方案 C 的车载端）：服务端以
/// <c>RECOVERY_AUTHENTICATION_FAILED</c> 拒了恢复申请或授权外的动作、或者因授权失效关了会话之后，恢复入口旁边常驻一行
/// 「恢复尚未开放，请联系系统维护人员。」，弹框也说这一句，而不是叫人检查凭据。
/// </summary>
/// <remarks>
/// <para>
/// 操作记录里那一条会被后来的记录顶走，提示行八秒就过期；这一行跟着业务服务的状态走，直到服务端开了会话才撤。
/// <c>ItemStatus</c> 是原始码，屏幕快照据此判「显示了这句」，不比中文全文。
/// </para>
/// <para>
/// 只在会开会话的四个入口有一个亮着时显示（<see cref="HasRecoveryReasonInput"/>）：旅程经别的途径结束、入口都收起后，
/// 这句话不再对着任何按钮，就不留着。
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private Func<bool>? _wireToGateRecoveryNotOpen;
    private bool _recoveryNotOpen;

    /// <summary>恢复入口旁那一行是否显示。</summary>
    public bool HasRecoveryNotOpenNotice => _recoveryNotOpen && HasRecoveryReasonInput;

    /// <summary>那一行字，与操作记录里被拒那一条的句首是同一句（<see cref="OnboardCommandRejectionText"/>）。</summary>
    public string RecoveryNotOpenNoticeText =>
        HasRecoveryNotOpenNotice ? OnboardCommandRejectionText.Describe(WireToGateBusinessService.RecoveryNotOpenReasonCode) : string.Empty;

    /// <summary>UIA 的 ItemStatus：显示时为原始码，不显示时为空。</summary>
    public string RecoveryNotOpenNoticeCode =>
        HasRecoveryNotOpenNotice ? WireToGateBusinessService.RecoveryNotOpenReasonCode : string.Empty;

    /// <remarks>接线本身在 <c>RecoveryNotOpenWiring</c>，G2 夹具调的是同一个方法。</remarks>
    internal void ConfigureRecoveryNotOpen(Func<bool> recoveryNotOpen)
    {
        _wireToGateRecoveryNotOpen = recoveryNotOpen ?? throw new ArgumentNullException(nameof(recoveryNotOpen));
        RunOnUiThread(RefreshRecoveryNotOpenCore);
    }

    /// <summary>
    /// 会开会话的入口被拒后，弹框正文：恢复尚未开放时说那一句，其余照调用方给的话。
    /// </summary>
    internal string DescribeRecoveryRefusal(string otherwise) =>
        HasRecoveryNotOpenNotice ? RecoveryNotOpenNoticeText : otherwise;

    private void RefreshRecoveryNotOpenCore()
    {
        bool notOpen = _wireToGateRecoveryNotOpen?.Invoke() == true;
        if (notOpen == _recoveryNotOpen)
        {
            return;
        }

        _recoveryNotOpen = notOpen;
        RaiseRecoveryNotOpenNoticeChanged();
    }

    private void RaiseRecoveryNotOpenNoticeChanged()
    {
        OnPropertyChanged(nameof(HasRecoveryNotOpenNotice));
        OnPropertyChanged(nameof(RecoveryNotOpenNoticeText));
        OnPropertyChanged(nameof(RecoveryNotOpenNoticeCode));
    }
}
