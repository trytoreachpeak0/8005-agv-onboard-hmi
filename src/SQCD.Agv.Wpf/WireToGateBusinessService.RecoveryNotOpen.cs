namespace SQCD.Agv.Wpf;

/// <summary>
/// Recovery the server has not opened for this vehicle (8005-agv-onboard-hmi#391, the onboard half of control-server#797
/// option C): whether the last word from the server on a recovery was <see cref="RecoveryNotOpenReasonCode"/>.
/// </summary>
/// <remarks>
/// <para>
/// With option C the recovery entries are always offered, and maintenance grants a time-limited authorization per vehicle
/// and journey on the server. Without one, the server refuses the session -- or an action outside what was granted -- with
/// <c>RECOVERY_AUTHENTICATION_FAILED</c>, and a session whose authorization expires, is withdrawn or does not survive a
/// server restart is closed with that code as its <c>closedReason</c> (CP-0022, ADR-cross-0066 decision 5). The server uses
/// the one code for a wrong credential and for no authorization, and this end cannot tell them apart, so both read as "not
/// open, call maintenance".
/// </para>
/// <para>
/// <b>Display only.</b> Nothing here gates an entry or asks the server anything: the next press is the operator's, and a
/// refusal is never sent again by the vehicle. The flag goes when the server opens a session -- the authorization exists
/// then -- and the screen shows it only beside an entry that opens one (<c>MainViewModel.HasRecoveryNotOpenNotice</c>), so a
/// journey resolved some other way does not leave it standing.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    internal const string RecoveryNotOpenReasonCode = "RECOVERY_AUTHENTICATION_FAILED";

    private int _recoveryNotOpen;

    /// <summary>The server last refused a recovery, or closed its session, for want of authorization.</summary>
    public bool IsRecoveryNotOpen => Volatile.Read(ref _recoveryNotOpen) == 1;

    internal int OperatorPressCountForTest => _authorizationRequestedInThisProcess.Count;

    /// <summary>
    /// Called with the reason of every recovery refusal before it is published, so the screen refreshed by that event
    /// reads the flag already set. Any other reason leaves the flag as it was: a refusal for something else says nothing
    /// about the authorization.
    /// </summary>
    private void NoteRecoveryRefusal(string reasonCode)
    {
        if (string.Equals(reasonCode, RecoveryNotOpenReasonCode, StringComparison.Ordinal))
        {
            Volatile.Write(ref _recoveryNotOpen, 1);
        }
    }

    /// <summary>The server opened, or reports open, a recovery session: the authorization exists.</summary>
    private void NoteRecoverySessionOpened() => Volatile.Write(ref _recoveryNotOpen, 0);
}
