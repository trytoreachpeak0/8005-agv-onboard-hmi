namespace SQCD.Agv.UnitTests;

/// <summary>
/// Structural facts about the held recovery command entry (<c>8005-agv-onboard-hmi#239</c>) that no session test can
/// reach: how the App wires it, and what the window's two click handlers hand back.
/// </summary>
/// <remarks>
/// The behaviour -- which commands are held, the two decisions, the view model's buttons -- is
/// <c>RecoveryVectorG2Tests.RestartReplay</c>.
/// </remarks>
public sealed class WireToGateHeldRecoveryCommandTests
{
    /// <summary>
    /// <c>App.xaml.cs</c> wires the entry through <c>HeldRecoveryCommandWiring.Configure</c>, the method the G2 tests
    /// call -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresTheEntryThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string seam = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "RecoveryVectorG2Tests.RestartReplay.cs"));

        Assert.Contains("HeldRecoveryCommandWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureHeldRecoveryCommand(", app, StringComparison.Ordinal);
        Assert.Contains("HeldRecoveryCommandWiring.Configure(viewModel, afterRestart.Business);", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureHeldRecoveryCommand(", seam, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each click handler reads the view model's entry once, before its dialog, and hands back the prompt from that
    /// one reading.
    /// </summary>
    /// <remarks>
    /// The dialog is a modal <c>MessageBox</c>, which no test can drive, and the view model goes on refreshing while it
    /// is open. A handler that read the entry again after the dialog could show one command and execute another --
    /// a newer one held meanwhile -- with every G2 test still green, because those tests start at the view model.
    /// </remarks>
    [Theory]
    [InlineData("OnConfirmHeldRecoveryCommandClick", "_viewModel.ConfirmHeldRecoveryCommandAsync(prompt)")]
    [InlineData("OnDeclineHeldRecoveryCommandClick", "_viewModel.DeclineHeldRecoveryCommandAsync(prompt)")]
    public void EachClickHandlerDecidesOnThePromptItBuiltTheDialogFrom(string handlerName, string decision)
    {
        string window = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml.cs"));
        int start = window.IndexOf($"void {handlerName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The click handler is gone or renamed.");
        int end = window.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string handler = window[start..end];

        Assert.Equal(1, CountOf(handler, ".HeldRecoveryCommand"));
        Assert.Contains("_viewModel?.HeldRecoveryCommand is not { Prompt: { } prompt }", handler, StringComparison.Ordinal);
        Assert.Contains("prompt.Text", handler, StringComparison.Ordinal);
        Assert.Contains(decision, handler, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
