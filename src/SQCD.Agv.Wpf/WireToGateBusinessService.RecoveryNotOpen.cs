namespace SQCD.Agv.Wpf;

public sealed partial class WireToGateBusinessService
{
    private int _recoveryNotOpen;

    public bool IsRecoveryNotOpen => Volatile.Read(ref _recoveryNotOpen) == 2;

    internal int OperatorPressCountForTest => _authorizationRequestedInThisProcess.Count;
}
