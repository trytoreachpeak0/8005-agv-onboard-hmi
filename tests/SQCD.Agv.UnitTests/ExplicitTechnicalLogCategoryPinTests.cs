using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Every log write that names its technical-log category explicitly is pinned here, in both directions
/// (REQ-0272, 8005-agv-onboard-hmi#312).
/// </summary>
/// <remarks>
/// <para>
/// An explicit category exists only where the source's default would be wrong: a safety latch is logged at
/// <c>Error</c>, which by default is a local fault; the IO signal change comes from the IO client, whose default is IO
/// communication. Deleting the explicit argument therefore compiles, passes every behavioural test, and quietly
/// re-files the line — the review of #312 (M-3) removed four of them and the whole suite stayed green. So each one is
/// pinned by its file and a fragment of its call, and a new explicit category that is not pinned fails too.
/// </para>
/// <para>
/// The classification rule these pins encode (review of #312, M-4): safety judgements, latches, resets and safety-gate
/// read failures are <see cref="OnboardTechnicalLogCategory.SafetyDecision"/> whatever their severity; the startup line
/// is <see cref="OnboardTechnicalLogCategory.RestartRecovery"/> and the effective configuration its own
/// <see cref="OnboardTechnicalLogCategory.Configuration"/> line.
/// </para>
/// <para>This guards source text, not behaviour; <see cref="FileAppLoggerTechnicalLogTests"/> proves an explicit
/// category reaches the file. No <c>IntegrationSlice</c> trait, for the reason <see cref="OnboardAlarmSnapshotTests"/>
/// gives.</para>
/// </remarks>
public sealed class ExplicitTechnicalLogCategoryPinTests
{
    private const string Controller = "src/SQCD.Agv.Application/OnboardController.cs";
    private const string Business = "src/SQCD.Agv.Wpf/WireToGateBusinessService.cs";

    /// <summary>File, a fragment of the call that is unique in that file, and the category it must name.</summary>
    private static readonly (string Path, string Fragment, OnboardTechnicalLogCategory Category)[] PinTable =
    [
        ("src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs", "号仓IO变化", OnboardTechnicalLogCategory.SignalChange),
        (Controller, "IO模块在线", OnboardTechnicalLogCategory.IoCommunication),
        (Controller, "规则响应或开锁前置条件不满足", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "维护人员已执行启动安全复核", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "启动IO快照不符合冷启动基线", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "启动IO安全快照验证通过", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "严重安全故障已锁存", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "严重安全故障复位未生效", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "严重安全故障已复位", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "严重安全故障复位被拒", OnboardTechnicalLogCategory.SafetyDecision),
        (Controller, "读取上层安全会话门禁失败", OnboardTechnicalLogCategory.SafetyDecision),
        (Business, "读取车辆停稳信号失败", OnboardTechnicalLogCategory.SafetyDecision),
        (Business, "恢复动作被安全策略阻断", OnboardTechnicalLogCategory.SafetyDecision),
        (Business, "出发前安全检查的用途无法识别", OnboardTechnicalLogCategory.SafetyDecision),
        (Business, "出发前安全检查已过期", OnboardTechnicalLogCategory.SafetyDecision),
        (Business, "扣车解除检查", OnboardTechnicalLogCategory.SafetyDecision),
        ("src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs", "活动开锁集里的仓门未能确认已关好", OnboardTechnicalLogCategory.SafetyDecision),
        ("src/SQCD.Agv.Wpf/App.xaml.cs", "车载端启动：", OnboardTechnicalLogCategory.RestartRecovery),
        ("src/SQCD.Agv.Wpf/App.xaml.cs", "DescribeForTechnicalLog()", OnboardTechnicalLogCategory.Configuration),
        ("src/SQCD.Agv.Wpf/StartupConfiguration.cs", "nameof(App), line", OnboardTechnicalLogCategory.Configuration),
        ("src/SQCD.Agv.Infrastructure/ProcessFailureLogging.cs", "nameof(ProcessFailureLogging)", OnboardTechnicalLogCategory.LocalFault)
    ];

    public static TheoryData<string, string, OnboardTechnicalLogCategory> Pins
    {
        get
        {
            TheoryData<string, string, OnboardTechnicalLogCategory> data = [];
            foreach ((string path, string fragment, OnboardTechnicalLogCategory category) in PinTable)
            {
                data.Add(path, fragment, category);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Pins))]
    public void APinnedWriteNamesItsCategory(string path, string fragment, OnboardTechnicalLogCategory expected)
    {
        LoggerWriteScanner.WriteCallSite write = Assert.Single(
            LoggerWriteScanner.ScanSource(),
            site => site.IsLoggerWrite && site.Path == path && Matches(site, fragment));

        Assert.True(
            write.Category == expected.ToString(),
            $"{path}:{write.Line} 「{fragment}」应显式标 {expected}，实际是 {write.Category ?? "（未标，取来源默认值）"}。");
    }

    [Fact]
    public void EveryExplicitCategoryUnderSrcIsPinned()
    {
        LoggerWriteScanner.WriteCallSite[] unpinned = LoggerWriteScanner.ScanSource()
            .Where(site => site.IsLoggerWrite && site.Category is not null)
            .Where(site => !PinTable.Any(pin => pin.Path == site.Path && Matches(site, pin.Fragment)))
            .ToArray();

        Assert.True(
            unpinned.Length == 0,
            "这些日志调用显式标了类别，但没有钉在 Pins 里：" + string.Join(", ", unpinned.Select(site => $"{site.Path}:{site.Line}")));
    }

    /// <summary>The effective-configuration line names what an engineer needs and nothing that is a secret.</summary>
    [Fact]
    public void TheEffectiveConfigurationLineCarriesNoCredential()
    {
        string line = new SQCD.Agv.Infrastructure.OnboardSettings().DescribeForTechnicalLog();

        Assert.Contains("wireToGate.enabled=", line, StringComparison.Ordinal);
        Assert.Contains("logging.directory=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("CREDENTIAL", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PROOF", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With the safety projection disabled the endpoint is not validated, so whatever its path, query or user info
    /// carry would reach the log unchecked; only its origin is written.
    /// </summary>
    [Fact]
    public void TheEffectiveConfigurationLineWritesOnlyTheEndpointsOrigin()
    {
        string line = new SQCD.Agv.Infrastructure.OnboardSettings
        {
            VehicleSafety = new SQCD.Agv.Infrastructure.VehicleSafetySettings
            {
                Enabled = false,
                Endpoint = "http://operator:secret@10.0.0.5:58007/api/onboard/v1/vehicle-safety?vehicleKey=VK-02&token=abc"
            }
        }.DescribeForTechnicalLog();

        Assert.Contains("vehicleSafety.endpoint=http://10.0.0.5:58007，", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("token", line, StringComparison.Ordinal);
        Assert.DoesNotContain("VK-02", line, StringComparison.Ordinal);
    }

    private static bool Matches(LoggerWriteScanner.WriteCallSite site, string fragment) =>
        site.Call.Contains(fragment, StringComparison.Ordinal);
}
