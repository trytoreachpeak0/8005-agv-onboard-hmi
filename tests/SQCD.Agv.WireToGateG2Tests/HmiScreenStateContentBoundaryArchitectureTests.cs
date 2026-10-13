using System.Collections;
using System.Reflection;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The guard on what the screen-state snapshot leaves out on purpose (onboard-hmi#313, T1a): no field anywhere in
/// <see cref="HmiScreenState"/> can hold the banner's guidance line, an operator record's message, a dialog body or an
/// input box's text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Research section 4.1 lists the guidance line and the operator record among the fields; both can carry a
/// sublot, and the guidance line can carry what the operator typed into the forced handoff box
/// (<c>WireToGateBusinessService.RecoveryVectors.cs</c>, the sublot mismatch notice) -- a leak in the sense of section 6,
/// item 5. Leaving them out is a deliberate departure from section 4.1, decided by the coordinator on 2026-10-12; the
/// cost is that T3's <c>-CrossCheck</c> can compare only <c>StateText</c> and the operator notice, not the guidance line.
/// Someone adding them back should have to come through here.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It checks property names across the snapshot's types, so a field holding that text
/// under another name passes; the G2 scenarios close part of that gap by searching the written snapshot for the actual
/// guidance and record text (<c>MultiDemandJourneyG2Tests.AssertCarriesNoGuidanceOrRecordText</c>).
/// </para>
/// </remarks>
public sealed class HmiScreenStateContentBoundaryArchitectureTests
{
    /// <summary>Names that would hold free text the boundary excludes. <c>Text</c> alone stays allowed: <c>StateText</c> and the operator notice are fixed wording.</summary>
    private static readonly string[] Forbidden =
    [
        "Guidance",
        "Message",
        "MessageBoxText",
        "Body",
        "ScanText",
        "Sublot",
        "RecoveryReason",
        "ForcedHandoffSublot",
        "ForcedHandoffReceiverName",
        "HardwareRecoveryObservations",
        "ConfirmationText",
    ];

    [Fact]
    public void NoFieldOfTheSnapshotCanHoldExcludedText()
    {
        string[] offenders = [.. Properties(typeof(HmiScreenState), [])
            .Where(property => Forbidden.Contains(property.Name, StringComparer.Ordinal))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")];

        Assert.True(offenders.Length == 0, "The snapshot must not carry: " + string.Join(", ", offenders));
    }

    /// <summary>The walk itself reaches the nested records, so an empty result above means something.</summary>
    [Fact]
    public void TheWalkReachesTheNestedRecords()
    {
        string[] names = [.. Properties(typeof(HmiScreenState), []).Select(property => $"{property.DeclaringType!.Name}.{property.Name}")];

        Assert.Contains("HmiBannerState.StateText", names);
        Assert.Contains("HmiInputState.HasText", names);
        Assert.Contains("HmiOperatorRecordState.Kind", names);
        Assert.Contains("OpenModalDialog.Title", names);
        // T1b (onboard-hmi#314): the walk only follows SQCD.Agv.Wpf types, so a basis record moved out of that namespace
        // would drop out of the content boundary without a sound. One property of each, the guidance flags among them.
        Assert.Contains("HmiScreenBasisState.RecoveryEntriesLatched", names);
        Assert.Contains("HmiControllerState.HasControllerGuidance", names);
        Assert.Contains("HmiPresentationInputsState.ReasonCodes", names);
        Assert.Contains("HmiSlotOperationState.HasOperationGuidance", names);
        Assert.Contains("HmiRecoveryEntryState.OfferFromLastRefresh", names);
        Assert.Contains("HmiAcceptedSafetyState.ReasonCodes", names);
    }

    private static IEnumerable<PropertyInfo> Properties(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type) || type.Namespace is not { } ns || !ns.StartsWith("SQCD.Agv.Wpf", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return property;
            foreach (Type nested in Nested(property.PropertyType))
            {
                foreach (PropertyInfo inner in Properties(nested, seen))
                {
                    yield return inner;
                }
            }
        }
    }

    private static Type[] Nested(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying != typeof(string) && typeof(IEnumerable).IsAssignableFrom(underlying) && underlying.IsGenericType)
        {
            return underlying.GetGenericArguments();
        }

        return [underlying];
    }
}
