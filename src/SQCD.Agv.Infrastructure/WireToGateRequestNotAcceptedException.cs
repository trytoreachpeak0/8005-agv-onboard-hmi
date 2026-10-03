namespace SQCD.Agv.Infrastructure;

/// <summary>
/// The control server answered a request with a <c>ProtocolProblem</c> correlated to that request's messageId:
/// it read the request and did not take it (batch 9-16, <c>8005-agv-onboard-hmi#221</c> review, S1).
/// </summary>
/// <remarks>
/// <para>
/// <b>An answer, not an unknown.</b> A wait that runs out or a session that drops leaves the caller not knowing
/// whether the request was taken; this says it was not. A caller that keeps a business id for resubmission has to
/// tell the two apart, or it keeps resending an id the server has already refused -- and a refusal such as
/// <c>BUSINESS_ID_CONTENT_CONFLICT</c> is about that very id.
/// </para>
/// <para>
/// Its message is the reason code, as with the <see cref="InvalidDataException"/> <c>ThrowIfProtocolProblem</c>
/// throws (that type is sealed, so this cannot derive from it). A <c>ProtocolProblem</c> that is not correlated to a
/// waiting request never becomes this: it fails the session, and the waiting request fails with whatever the
/// session failed with.
/// </para>
/// </remarks>
public sealed class WireToGateRequestNotAcceptedException : Exception
{
    public WireToGateRequestNotAcceptedException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
