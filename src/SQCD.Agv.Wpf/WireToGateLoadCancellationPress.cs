namespace SQCD.Agv.Wpf;

/// <summary>
/// A 「取消装货」 press taken when the entry is pressed, to be sent once the operator confirms (<c>8005-agv-onboard-hmi#291</c>).
/// </summary>
/// <param name="SubjectDemandId">
/// The demand the press would act on as the screen stood when it was pressed, or <c>null</c> when it had no subject. Read
/// from the same snapshot the press is held to, so the confirmation names what the press will be compared against.
/// </param>
/// <param name="SubjectIsLoadInFlight">
/// Whether that subject is a load in flight (or an in-flight cancellation already on file) rather than a cancellation
/// before any sublot.
/// </param>
/// <param name="SendAsync">Sends the press, held to the subject above.</param>
public sealed record WireToGateLoadCancellationPress(
    string? SubjectDemandId,
    bool SubjectIsLoadInFlight,
    Func<CancellationToken, Task<bool>> SendAsync);
