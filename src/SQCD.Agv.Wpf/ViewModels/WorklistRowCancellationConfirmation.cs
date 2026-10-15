namespace SQCD.Agv.Wpf.ViewModels;

public sealed record WorklistRowCancellationConfirmation(
    string DetailText,
    Func<CancellationToken, Task<bool>> SendAsync)
{
    public string Text => DetailText;

    public string FailureText => DetailText + string.Empty;
}
