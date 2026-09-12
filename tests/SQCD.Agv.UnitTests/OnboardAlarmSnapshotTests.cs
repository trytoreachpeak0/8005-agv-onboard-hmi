using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载端告警快照与技术日志（<c>FP-IS-15</c> 车载半边的业务语义）。
/// </summary>
/// <remarks>
/// 与 <see cref="SlotConfigurationActivationTests"/> 同理：这里固化的是语义，不是消息面。
/// <c>OnboardAlarmSnapshot</c> 与 <c>AlarmEntry</c> 的类型层与传输属批次 2 轨 A，尚未落地。
///
/// 不挂 <c>IntegrationSlice</c> trait：<c>FP-IS-15</c> 不在 vendored 的
/// <c>integration-slices/index.json</c> 里，标上去会让
/// <see cref="IntegrationSliceCoverageArchitectureTests"/> 的双向相等直接红。
/// </remarks>
public sealed class OnboardAlarmSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASnapshotCarriesTheWholeCurrentAlarmSetRatherThanWhatChanged()
    {
        OnboardAlarmBoard board = new("AGV-01", new FixedClock(Now));
        board.Raise(Alarm(OnboardAlarmCodes.IoModuleDisconnected, AlarmScope.CurrentVehicle));
        board.Raise(Alarm(OnboardAlarmCodes.RuleGatewayDisconnected, AlarmScope.CurrentVehicle));

        OnboardAlarmSnapshot first = board.Capture();
        Assert.Equal("SNAPSHOT", OnboardAlarmSnapshot.DeliveryClass);
        Assert.Equal(2, first.Alarms.Count);

        // 再抓一次，内容仍是当下全量，不是「自上次以来的变化」。
        board.Clear(OnboardAlarmCodes.RuleGatewayDisconnected);
        OnboardAlarmSnapshot second = board.Capture();
        Assert.Single(second.Alarms);
        Assert.Equal(OnboardAlarmCodes.IoModuleDisconnected, second.Alarms[0].AlarmCode);
        Assert.Equal(first.SnapshotSequence + 1, second.SnapshotSequence);
    }

    [Fact]
    public void AfterAReconnectTheSnapshotIsSimplyTheCurrentTruthWithNoNotionOfWhatWasMissed()
    {
        OnboardAlarmBoard board = new("AGV-01", new FixedClock(Now));
        board.Raise(Alarm(OnboardAlarmCodes.IoModuleDisconnected, AlarmScope.CurrentVehicle));
        board.Capture();

        // 断线期间抬起又清掉了一条：重连后的快照对它一无所知，也不需要知道。
        board.Raise(Alarm(OnboardAlarmCodes.SlotLightCurtainBlocked, AlarmScope.CurrentVehicle));
        board.Clear(OnboardAlarmCodes.SlotLightCurtainBlocked);
        board.Raise(Alarm(OnboardAlarmCodes.SlotLockFeedbackLost, AlarmScope.CurrentVehicle));

        OnboardAlarmSnapshot afterReconnect = board.Capture();
        Assert.Equal(
            [OnboardAlarmCodes.IoModuleDisconnected, OnboardAlarmCodes.SlotLockFeedbackLost],
            afterReconnect.Alarms.Select(alarm => alarm.AlarmCode).Order(StringComparer.Ordinal));

        // 快照类型上没有任何一处可以承载「上次是什么」——所以界面上也没有「已过期」可显示。
        string[] propertyNames = [.. typeof(OnboardAlarmSnapshot).GetProperties().Select(p => p.Name)];
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Stale", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Previous", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LastKnown", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyAlarmsDirectlyRelatedToThisVehicleStopOrOperationAreShownOnTheLocalScreen()
    {
        OnboardAlarmBoard board = new("AGV-01", new FixedClock(Now));
        board.Raise(Alarm(OnboardAlarmCodes.IoModuleDisconnected, AlarmScope.CurrentVehicle));
        board.Raise(Alarm(OnboardAlarmCodes.StationOperationOverdue, AlarmScope.CurrentStop) with
        {
            StationId = "ST-7"
        });
        board.Raise(Alarm(OnboardAlarmCodes.SlotLockFeedbackLost, AlarmScope.CurrentOperation) with
        {
            SlotOperationAttemptId = "ATT-1"
        });
        board.Raise(Alarm(OnboardAlarmCodes.RuleGatewayDisconnected, AlarmScope.Fleet));

        OnboardAlarmSnapshot snapshot = board.Capture();
        IReadOnlyList<AlarmEntry> shown = OnboardAlarmVisibility.ForLocalDisplay(
            snapshot, new OnboardAlarmContext("AGV-01", "ST-7", "ATT-1"));

        Assert.Equal(
            [
                OnboardAlarmCodes.IoModuleDisconnected,
                OnboardAlarmCodes.SlotLockFeedbackLost,
                OnboardAlarmCodes.StationOperationOverdue
            ],
            shown.Select(alarm => alarm.AlarmCode).Order(StringComparer.Ordinal));

        // 换一个停靠与一次操作之后，那两条就不再是「当前」的了。
        IReadOnlyList<AlarmEntry> elsewhere = OnboardAlarmVisibility.ForLocalDisplay(
            snapshot, new OnboardAlarmContext("AGV-01", "ST-9", "ATT-2"));
        Assert.Equal(
            [OnboardAlarmCodes.IoModuleDisconnected],
            elsewhere.Select(alarm => alarm.AlarmCode));

        // 车队范围的那条在任何本机上下文里都不显示，它归看板。
        Assert.DoesNotContain(
            OnboardAlarmCodes.RuleGatewayDisconnected, shown.Select(alarm => alarm.AlarmCode));
    }

    [Fact]
    public void AlarmVisibilityIsEvaluatedWithoutAnyIdentityOrKeyInput()
    {
        // 本期没有人员认证。把可见性做成需要身份才能求值的权限判断，会让它落在一个全场共用环境变量
        // 密钥的地基上——所以求值函数的入参里不允许出现身份或密钥。
        System.Reflection.ParameterInfo[] parameters = typeof(OnboardAlarmVisibility)
            .GetMethod(nameof(OnboardAlarmVisibility.ForLocalDisplay))!
            .GetParameters();
        Assert.Equal(
            [typeof(OnboardAlarmSnapshot), typeof(OnboardAlarmContext)],
            parameters.Select(parameter => parameter.ParameterType));

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SQCD.Agv.Core", "OnboardAlarms.cs"));
        foreach (string forbidden in new[]
        {
            "GetEnvironmentVariable", "administratorRole", "sharedSecret", "password", "operatorId"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AlarmCodesAreAnOpenStringSetAndShareNothingWithTheClosedProtocolErrorCodeRegistry()
    {
        // 告警 code 不是 enum：把开放集合塞进封闭 enum 等于每加一个故障码就发一次 breaking change。
        Assert.Equal(
            typeof(string),
            typeof(AlarmEntry).GetProperty(nameof(AlarmEntry.AlarmCode))!.PropertyType);

        HashSet<string> protocolErrorCodes = ReadProtocolErrorCodes();
        Assert.NotEmpty(protocolErrorCodes);
        foreach (string alarmCode in OnboardAlarmCodes.All)
        {
            Assert.DoesNotContain(alarmCode, protocolErrorCodes);
        }
    }

    private static HashSet<string> ReadProtocolErrorCodes()
    {
        // v0.3.0 线把 vendor 按发布版本分目录（vendor/8005-agv-protocol/protocol-v0.3.0/...），v2 线
        // 直接就是那一份候选本身，少一层。这里跟 v2 线的布局。
        string path = Path.Combine(
            FindRepositoryRoot(), "vendor", "8005-agv-protocol", "errors", "error-codes.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        HashSet<string> codes = new(StringComparer.Ordinal);
        foreach (JsonElement entry in document.RootElement.GetProperty("codes").EnumerateArray())
        {
            codes.Add(entry.GetProperty("code").GetString()!);
        }
        return codes;
    }

    private static AlarmEntry Alarm(string code, AlarmScope scope) =>
        new(code, "WARNING", Now, scope, $"{code} 触发。");

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("从测试二进制找不到仓库根目录。");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
