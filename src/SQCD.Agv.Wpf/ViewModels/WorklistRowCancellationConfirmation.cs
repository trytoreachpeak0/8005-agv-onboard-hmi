namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 按下待送点清单某一行的「取消」时取下的一次按键（8005-agv-onboard-hmi#353）：确认框正文、被拒时弹框的正文，与确认后要执行的
/// 按键，出自同一次读取。
/// </summary>
/// <remarks>弹框标题是 <c>MainWindow.OnWorklistRowCancellationClick</c> 里的字面量（<c>ModalDialogRegistrationArchitectureTests</c>）。</remarks>
/// <param name="DetailText">确认框里复述的那一条。</param>
/// <param name="SendAsync">确认框点「是」之后执行的按键。</param>
public sealed record WorklistRowCancellationConfirmation(
    string DetailText,
    Func<CancellationToken, Task<bool>> SendAsync)
{
    /// <summary>确认框正文：说清这条需求就此结束、不再派、不能撤回。</summary>
    public string Text => WireToGateLoadCancellationText.RowConfirmationText(DetailText);

#pragma warning disable CA1822 // Read by the window beside Text, from the same press.
    /// <summary>按键未被接受时弹框的正文。</summary>
    public string FailureText => WireToGateLoadCancellationText.RowFailureText;
#pragma warning restore CA1822
}
