namespace SQCD.Agv.UnitTests;

/// <summary>
/// Structural facts about the forced mechanical recovery's wiring (<c>8005-agv-onboard-hmi#216</c>) that no session
/// test can reach: how the App wires it.
/// </summary>
/// <remarks>
/// The behaviour -- the confirmation, the cargo handoff record, the hardware recovery record -- is
/// <c>RecoveryVectorG2Tests.CargoHandoff</c>, which drives <c>ForcedIsolationWiring.Configure</c> itself. Nothing
/// there notices if <c>App</c> stops calling it: deleting the call left every unit and recovery G2 test green
/// (independent review of 8005-agv-onboard-hmi#245), and without it the forced confirmation never reaches the
/// business service, so the onboard cannot report <c>MECHANICALLY_ISOLATED</c> and the vehicle stays in
/// <c>RecoveryRequired</c>. The line sits on the conflict that merge resolved, which is where it is most likely to
/// be dropped next time.
/// </remarks>
public sealed class WireToGateForcedIsolationWiringTests
{
    /// <summary>
    /// <c>App.xaml.cs</c> wires the forced isolation through <c>ForcedIsolationWiring.Configure</c>, the method the
    /// G2 seam test calls -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresForcedIsolationThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string seam = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "RecoveryVectorG2Tests.CargoHandoff.cs"));

        Assert.Contains("ForcedIsolationWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureForcedIsolation(", app, StringComparison.Ordinal);
        Assert.Contains("ForcedIsolationWiring.Configure(viewModel, harness.Business);", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureForcedIsolation(", seam, StringComparison.Ordinal);
    }
}
