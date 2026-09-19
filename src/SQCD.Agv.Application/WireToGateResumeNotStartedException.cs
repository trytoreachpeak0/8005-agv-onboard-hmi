namespace SQCD.Agv.Application;

/// <summary>
/// A <c>SlotOperationResumeCommand</c> the executor refused before its first journal write and its
/// first unlock pulse: nothing physical happened and nothing about the operation changed on disk.
/// </summary>
/// <remarks>
/// <para>
/// Only this type tells the caller that the refusal can be answered with
/// <c>SlotOperationCommandRejected</c> (8005-agv-onboard-hmi#119). A plain
/// <see cref="InvalidDataException"/> from <c>ResumeAsync</c> is not enough: the executor also throws
/// one after a pulse (another door not shut, a slot turning unreadable mid-wait), and an
/// <see cref="IOException"/> or <see cref="TimeoutException"/> can come from either side of the
/// first pulse. Those are never classified as "not started".
/// </para>
/// <para>
/// The message is the executor's local reason code, the same text the plain
/// <see cref="InvalidDataException"/> thrown here before this type existed carried; the caller maps
/// it onto a code the protocol registry has. <see cref="InvalidDataException"/> is sealed, so this
/// is a sibling of it rather than a subtype.
/// </para>
/// </remarks>
public sealed class WireToGateResumeNotStartedException : Exception
{
    public WireToGateResumeNotStartedException(string reasonCode)
        : base(reasonCode)
    {
    }

    public WireToGateResumeNotStartedException(string reasonCode, Exception innerException)
        : base(reasonCode, innerException)
    {
    }
}
