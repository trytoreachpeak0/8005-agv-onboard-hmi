namespace SQCD.Agv.UnitTests;

/// <summary>
/// Structural facts about the entry that ends a recovery whose result the server refused for good
/// (<c>8005-agv-onboard-hmi#254</c>) that no session test can reach: how the App wires it.
/// </summary>
/// <remarks>
/// The behaviour -- who may press it, what it writes, what follows -- is <c>RecoveryVectorG2Tests.ManualCheckCloseOut</c>.
/// </remarks>
public sealed class WireToGateConflictedRecoveryTests
{
    /// <summary>
    /// <c>App.xaml.cs</c> wires the entry through <c>ConflictedRecoveryWiring.Configure</c>, the method the G2 tests call
    /// -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresTheEntryThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string seam = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "RecoveryVectorG2Tests.ManualCheckCloseOut.cs"));

        Assert.Contains("ConflictedRecoveryWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureConflictedRecovery(", app, StringComparison.Ordinal);
        Assert.Contains("ConflictedRecoveryWiring.Configure(viewModel, harness.Business);", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureConflictedRecovery(", seam, StringComparison.Ordinal);
    }
}
