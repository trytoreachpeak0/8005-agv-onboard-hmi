namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 按下「取消装货」时取下的一次按键：确认框要复述的那一句，与确认后要执行的按键（8005-agv-onboard-hmi#291）。两者出自同一次读取。
/// </summary>
/// <param name="DetailText">确认框里复述的将要取消的任务；没有可复述的为空。</param>
/// <param name="SendAsync">确认框点「是」之后执行的按键。</param>
public sealed record LoadCancellationConfirmation(
    string DetailText,
    Func<CancellationToken, Task<bool>> SendAsync);
