using SQCD.Agv.Application;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The line that replaces a hidden 「充电后返回服务」 while the server holds the vehicle for manual charging
/// (8005-agv-onboard-hmi#293). On 2026-10-10 a vehicle showed 「需人工充电：服务端保持」 with the factory setting
/// (<c>recoveryResumeEnabled=false</c>) and the operator had nothing to press and nothing to read.
/// </summary>
/// <remarks>
/// <para>
/// The expected sentences are written out here rather than read from the production constants, so a changed
/// sentence is a red test someone has to agree with.
/// </para>
/// <para>
/// One sentence at a time, by a fixed priority: latch, then session, then authorization. The latch goes first
/// because it shuts the entry whatever else is true, so the other two would send the operator to fix something
/// that would not open it; the session goes before authorization because a maintainer who configures the
/// vehicle while it is offline still cannot press. The view-model half -- the line follows the hold and the
/// entry, and is announced to the window -- is <c>ManualChargingReturnGuidanceViewModelTests</c>; the
/// business-service half and the press are <c>ManualChargingReturnEntryG2Tests</c>.
/// </para>
/// </remarks>
public sealed class WireToGateManualChargingReturnTextTests
{
    private const string NotAuthorized =
        "充电完成后需维护管理员在本机开启返回服务权限，再按「充电后返回服务」。请联系系统维护人员。";

    private const string SessionNotReady = "与服务端的连接就绪后才能申请返回服务。";

    private const string Latched = "安全故障解除前不能申请返回服务，请联系系统维护人员。";

    /// <summary>The field case: factory setting, session ready, no latch.</summary>
    [Fact]
    public void AHeldVehicleWithoutAnAuthorizedMaintainerIsToldWhoCanReturnIt() =>
        Assert.Equal(
            NotAuthorized,
            WireToGateManualChargingReturnText.Guidance(
                manualChargingHold: true,
                entryOffered: false,
                fatalFaultLatched: false,
                WireToGateManualChargingReturnBlocker.OperatorNotAuthorized));

    /// <summary>Every combination of the three causes, one sentence each, by the fixed priority.</summary>
    [Theory]
    [InlineData(false, WireToGateManualChargingReturnBlocker.SessionNotReady, SessionNotReady)]
    [InlineData(false, WireToGateManualChargingReturnBlocker.OperatorNotAuthorized, NotAuthorized)]
    [InlineData(true, WireToGateManualChargingReturnBlocker.None, Latched)]
    [InlineData(true, WireToGateManualChargingReturnBlocker.SessionNotReady, Latched)]
    [InlineData(true, WireToGateManualChargingReturnBlocker.OperatorNotAuthorized, Latched)]
    public void OneCauseIsShownAtATimeLatchFirstThenSessionThenAuthorization(
        bool fatalFaultLatched,
        WireToGateManualChargingReturnBlocker blocker,
        string expected) =>
        Assert.Equal(
            expected,
            WireToGateManualChargingReturnText.Guidance(
                manualChargingHold: true,
                entryOffered: false,
                fatalFaultLatched,
                blocker));

    /// <summary>
    /// No line without a hold (there is nothing to return from), and none while the entry can be pressed (the
    /// button is the exit). Not authorized is the business service's answer on every factory-set vehicle, held
    /// or not, so the first rows are the ones that keep the line off a healthy screen.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, WireToGateManualChargingReturnBlocker.OperatorNotAuthorized)]
    [InlineData(false, false, false, WireToGateManualChargingReturnBlocker.SessionNotReady)]
    [InlineData(false, false, true, WireToGateManualChargingReturnBlocker.None)]
    [InlineData(false, true, false, WireToGateManualChargingReturnBlocker.None)]
    [InlineData(true, true, false, WireToGateManualChargingReturnBlocker.None)]
    // An entry offered while the business service, read a moment later, names a cause: the button decides.
    [InlineData(true, true, false, WireToGateManualChargingReturnBlocker.OperatorNotAuthorized)]
    [InlineData(true, true, false, WireToGateManualChargingReturnBlocker.SessionNotReady)]
    public void NothingIsShownWithoutAHoldOrWhileTheEntryCanBePressed(
        bool manualChargingHold,
        bool entryOffered,
        bool fatalFaultLatched,
        WireToGateManualChargingReturnBlocker blocker) =>
        Assert.Equal(
            string.Empty,
            WireToGateManualChargingReturnText.Guidance(manualChargingHold, entryOffered, fatalFaultLatched, blocker));

    /// <summary>
    /// The cause is read from the judgement the button itself uses, never from the proof. This request carries no
    /// proof and the server checks only the role it names, so the vehicle's local check is the whole credential
    /// boundary; a second reading of a credential beside it is a second boundary that can drift from the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard is a closed set, not a list of forbidden words (8005-agv-onboard-hmi#293 review S3: a first version
    /// that scanned the cause property alone let a helper, or the view model's refresh, read the proof). Every method
    /// the line's code reaches is named below with the only calls it may make. The one exit from the set is
    /// <c>CanRequestManualChargingReturnToService</c>, the button's own judgement, which the cause must read. A new
    /// call anywhere in the set is red here until someone adds it to this table and says why.
    /// </para>
    /// <para>
    /// Within the set nothing names an environment variable or a credential reader.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCauseComesFromTheButtonsOwnJudgementAndReadsNoCredential()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string business = File.ReadAllText(Path.Combine(
            root, "src", "SQCD.Agv.Wpf", "WireToGateBusinessService.ManualChargingReturn.cs"));
        string viewModel = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "ViewModels", "MainViewModel.cs"));
        string text = File.ReadAllText(Path.Combine(
            root, "src", "SQCD.Agv.Application", "WireToGateManualChargingReturnText.cs"));
        string wiring = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "ManualChargingReturnWiring.cs"));

        const string causeMember = "public WireToGateManualChargingReturnBlocker ManualChargingReturnBlocker =>";
        int causeStart = business.IndexOf(causeMember, StringComparison.Ordinal);
        Assert.True(causeStart >= 0, "ManualChargingReturnBlocker is no longer an expression-bodied property.");
        string cause = business[causeStart..(business.IndexOf(';', causeStart) + 1)];
        Assert.Contains("CanRequestManualChargingReturnToService", cause, StringComparison.Ordinal);

        (string Name, string Body, string[]? AllowedCalls)[] reached =
        [
            ("ManualChargingReturnBlocker", cause, ["SessionCanCarryOperatorRequest"]),
            ("SessionCanCarryOperatorRequest",
                MethodBody(business, "private bool SessionCanCarryOperatorRequest()"), []),
            ("RefreshManualChargingReturnGuidanceCore",
                MethodBody(viewModel, "private void RefreshManualChargingReturnGuidanceCore()"), ["Guidance", "Invoke"]),
            ("ConfigureManualChargingReturnGuidance",
                MethodBody(viewModel, "internal void ConfigureManualChargingReturnGuidance("),
                ["ArgumentNullException", "nameof", "RunOnUiThread"]),
            ("WireToGateManualChargingReturnText.Guidance",
                MethodBody(text, "public static string Guidance("), []),
            ("ManualChargingReturnWiring.Configure",
                MethodBody(wiring, "public static void Configure("),
                ["ThrowIfNull", "ConfigureManualChargingReturnGuidance"]),
            // The two files whole, comments included, for the forbidden names only: no call may be added there either.
            ("WireToGateManualChargingReturnText.cs", text, null),
            ("ManualChargingReturnWiring.cs", wiring, null)
        ];
        foreach ((string name, string body, string[]? allowedCalls) in reached)
        {
            if (allowedCalls is not null)
            {
                string[] outside =
                [
                    .. System.Text.RegularExpressions.Regex.Matches(body, @"\b([A-Za-z_]\w*)\s*\(")
                        .Select(match => match.Groups[1].Value)
                        .Where(call => call is not ("if" or "switch" or "return"))
                        .Distinct()
                        .Except(allowedCalls)
                ];
                Assert.True(
                    outside.Length == 0,
                    $"{name} calls {string.Join(", ", outside)}, outside the set this test allows.");
            }

            foreach (string forbidden in new[]
            {
                "GetEnvironmentVariable",
                "ReadRecoveryProof",
                "ReadOperatorContext",
                "EnvironmentVariable",
                "_recoveryOptions",
                "CONTROL_SERVER_"
            })
            {
                Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The body of the member whose declaration starts with <paramref name="signature"/>, from its opening brace to
    /// its closing one: the declaration itself is not a call.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(index + 1)];
            }
        }

        throw new InvalidDataException($"Unbalanced braces after: {signature}");
    }

    /// <summary>
    /// <c>App.xaml.cs</c> wires the line through <c>ManualChargingReturnWiring.Configure</c>, the method the G2 harness
    /// calls -- so the seam test drives the product's own lines, not a copy of them.
    /// </summary>
    [Fact]
    public void TheAppWiresTheLineThroughTheMethodTheSeamTestDrives()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "SQCD.Agv.Wpf", "App.xaml.cs"));
        string harness = File.ReadAllText(Path.Combine(
            root, "tests", "SQCD.Agv.WireToGateG2Tests", "MultiDemandJourneyG2Tests.cs"));

        Assert.Contains("ManualChargingReturnWiring.Configure(viewModel, _wireToGateBusiness);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureManualChargingReturnGuidance(", app, StringComparison.Ordinal);
        Assert.Contains("ManualChargingReturnWiring.Configure(viewModel, business);", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureManualChargingReturnGuidance(", harness, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window shows the line: its text and its visibility are bound to the two view-model properties, and it
    /// carries an AutomationId the real-rig scenarios can read. The view model is tested; the binding is only here.
    /// </summary>
    [Fact]
    public void TheWindowBindsTheLine()
    {
        string window = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"ManualChargingReturnGuidance\"", window, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ManualChargingReturnGuidance}\"", window, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding HasManualChargingReturnGuidance, Converter={StaticResource BooleanToVisibilityConverter}}\"",
            window,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator-facing sentences name no ticket and no internal role (the workspace's rule for this screen):
    /// 「调度」 reads as the person who dispatches vehicles on the plant floor.
    /// </summary>
    [Theory]
    [InlineData(NotAuthorized)]
    [InlineData(SessionNotReady)]
    [InlineData(Latched)]
    public void TheSentencesNameNoTicketAndNoInternalRole(string sentence)
    {
        Assert.DoesNotContain("#", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", sentence, StringComparison.Ordinal);
    }
}
