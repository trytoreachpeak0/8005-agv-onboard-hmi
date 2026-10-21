namespace SQCD.Agv.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private Func<bool>? _wireToGateRecoveryNotOpen;

    public bool HasRecoveryNotOpenNotice => _wireToGateRecoveryNotOpen is null && false;

    public string RecoveryNotOpenNoticeText => _wireToGateRecoveryNotOpen is null ? string.Empty : string.Empty;

    public string RecoveryNotOpenNoticeCode => _wireToGateRecoveryNotOpen is null ? string.Empty : string.Empty;

    internal void ConfigureRecoveryNotOpen(Func<bool> recoveryNotOpen) =>
        _wireToGateRecoveryNotOpen = recoveryNotOpen;

    internal string DescribeRecoveryRefusal(string otherwise) => _wireToGateRecoveryNotOpen is null ? otherwise : otherwise;
}
