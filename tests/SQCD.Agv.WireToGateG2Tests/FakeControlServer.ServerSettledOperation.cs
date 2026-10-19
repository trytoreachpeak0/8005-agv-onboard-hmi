namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class FakeControlServer
{
    /// <summary>
    /// The control server concludes an operation on its own, with nothing from the vehicle: the withdrawal of a stuck
    /// journey writes its <c>StationOperations</c> row <c>Cancelled</c> (trytoreachpeak0/8005-agv-control-server#771).
    /// From the next handshake on, a report naming the attempt is taken off the pending facts
    /// (<c>WireToGateStore.TryTakeOffSettledReportedAttemptsAsync</c>) and the operation no longer holds the vehicle in
    /// recovery, so the vehicle is answered READY (8005-agv-onboard-hmi#372).
    /// </summary>
    /// <remarks>
    /// Nothing is announced on the running session: the withdrawal is written with the service stopped, so the vehicle
    /// learns it from the handshake that follows the service's restart.
    /// </remarks>
    public void SettleOperationOnServer(string slotOperationAttemptId)
    {
        lock (_sync)
        {
            _settledAttempts.Add(slotOperationAttemptId);
            _operationsNeedingRecovery.Remove(slotOperationAttemptId);
        }
    }
}
