using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// What a mid-session <c>SafetyStateSnapshot</c> carried (onboard-hmi#419): the safety summary and the IO readings its slot
/// states were built from. The control server keeps both -- the summary decides readiness, the slot states are read as the
/// slots' readings until a later <c>SafetyStateChanged</c> names them -- so whoever compares "what the server holds" with
/// the present reading needs both, not the summary alone.
/// </summary>
public sealed record WireToGateSentSafetySnapshot(WireToGateSafetySummaryPayload Safety, IoSnapshot Readings);
