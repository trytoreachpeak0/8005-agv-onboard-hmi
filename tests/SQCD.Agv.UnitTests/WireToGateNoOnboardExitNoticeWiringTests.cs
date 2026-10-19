namespace SQCD.Agv.UnitTests;

/// <summary>
/// How the App wires the line that says the vehicle has no exit of its own while an unknown result waits for the server
/// (<c>8005-agv-onboard-hmi#370</c>): through the method the G2 harness drives. The behaviour is
/// <c>MultiDemandJourneyG2Tests.NoOnboardExitNotice</c>.
/// </summary>
/// <remarks>
/// The refresh on every cached recovery state is subscribed inside that method too (<c>8005-agv-onboard-hmi#375</c>), so
/// neither side subscribes <c>RecoveryStateCached</c> by hand: a hand-written subscription in one of them is a refresh the
/// other does not run. The harness passes only its stand-in UI thread.
/// </remarks>
public sealed class WireToGateNoOnboardExitNoticeWiringTests
{
    [Fact]
    public void TheAppWiresTheNoticeThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string seam = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "MultiDemandJourneyG2Tests.cs"));

        Assert.Contains("NoOnboardExitNoticeWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureNoOnboardExitNotice(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryStateCached", app, StringComparison.Ordinal);
        Assert.Contains(
            "NoOnboardExitNoticeWiring.Configure(viewModel, business, harness.OnUiThread);",
            seam,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureNoOnboardExitNotice(", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryStateCached", seam, StringComparison.Ordinal);
    }
}
