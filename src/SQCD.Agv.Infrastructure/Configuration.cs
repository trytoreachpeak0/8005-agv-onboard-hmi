using System.Globalization;
using System.Net;
using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class OnboardSettings
{
    public string Environment { get; init; } = "Development";

    public string AgvId { get; init; } = "AGV-8005-01";

    public string OnboardInstanceId { get; init; } = "OBU-8005-01";

    public RuleGatewaySettings RuleGateway { get; init; } = new();

    public WireToGateSettings WireToGate { get; init; } = new();

    public OnboardAutomationSettings Automation { get; init; } = new();

    public VehicleSafetySettings VehicleSafety { get; init; } = new();

    public IoModuleSettings IoModule { get; init; } = new();

    public WorkflowSettings Workflow { get; init; } = new();

    public LogSettings Logging { get; init; } = new();

    public static OnboardSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到车载端配置文件。", path);
        }

        string json = File.ReadAllText(path);
        RejectRemovedTransportKeys(json);
        OnboardSettings settings = JsonSerializer.Deserialize<OnboardSettings>(json, SerializerOptions)
            ?? throw new InvalidDataException("车载端配置文件内容为空或格式错误。");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Environment)
            || string.IsNullOrWhiteSpace(AgvId)
            || string.IsNullOrWhiteSpace(OnboardInstanceId))
        {
            throw new InvalidDataException("Environment、AgvId和OnboardInstanceId不能为空。");
        }

        bool production = Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
        if (production && !WireToGate.Enabled)
        {
            throw new InvalidDataException("Production环境必须启用WIRE_TO_GATE，禁止回退到仅使用旧规则协议。");
        }

        if (production
            && (IsPlaceholderValue(AgvId)
                || IsPlaceholderValue(OnboardInstanceId)))
        {
            throw new InvalidDataException("Production环境必须配置真实且稳定的AgvId和OnboardInstanceId。");
        }

        RuleGateway.Validate(production);
        WireToGate.Validate(production);
        Automation.Validate(
            production,
            WireToGate.Enabled,
            WireToGate.Port,
            IoModule.Port);
        VehicleSafety.Validate(production && WireToGate.Enabled);
        IoModule.Validate(production);
        Workflow.Validate();

        if (production)
        {
            RequireProductionEnvironmentVariable(
                WireToGate.CredentialEnvironmentVariable,
                "ControlServer凭据");
            RequireProductionEnvironmentVariable(
                VehicleSafety.CredentialEnvironmentVariable,
                "车辆安全投影凭据");
            RequireProductionEnvironmentVariable(
                WireToGate.OperatorIdEnvironmentVariable,
                "操作员ID");
            if (WireToGate.RecoveryResumeEnabled)
            {
                RequireProductionEnvironmentVariable(
                    WireToGate.RecoveryAuthenticationProofEnvironmentVariable,
                    "恢复管理员凭据");
            }
        }
    }

    internal static bool IsPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string normalized = value.Trim();
        return normalized.Equals("AGV-8005-01", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("OBU-8005-01", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("EXAMPLE", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CHANGEME", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("YOUR-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("TODO", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireProductionEnvironmentVariable(string name, string displayName)
    {
        if (IsPlaceholderValue(name)
            || string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(name)))
        {
            throw new InvalidDataException($"Production环境未提供{displayName}环境变量。");
        }
    }

    private static void RejectRemovedTransportKeys(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        JsonElement wireToGate = default;
        bool foundWireToGate = false;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("wireToGate", StringComparison.OrdinalIgnoreCase))
            {
                wireToGate = property.Value;
                foundWireToGate = true;
                break;
            }
        }

        if (!foundWireToGate || wireToGate.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in wireToGate.EnumerateObject())
        {
            if (property.Name.Equals("useTls", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("serverCertificateSha256", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"WIRE_TO_GATE配置键{property.Name}已移除，当前版本固定使用明文TCP/HTTP传输。");
            }
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>
/// Configuration for the read-only vehicle-safety projection exposed by
/// ControlServer. The endpoint is a complete plaintext HTTP URI so the remote
/// host and port remain explicit in production configuration.
/// </summary>
public sealed class VehicleSafetySettings
{
    private const int MaximumClockSkewToleranceMs = 1_000;

    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = "http://control.example.invalid/api/onboard/v1/vehicle-safety";

    public string CredentialEnvironmentVariable { get; init; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    public string ExpectedVehicleKey { get; init; } = string.Empty;

    public int MaximumEvidenceAgeMs { get; init; } = 5_000;

    public int ClockSkewToleranceMs { get; init; } = 500;

    public int PollIntervalMs { get; init; } = 1_000;

    public int RequestTimeoutMs { get; init; } = 3_000;

    internal void Validate(bool production = false)
    {
        if (!Enabled)
        {
            if (production)
            {
                throw new InvalidDataException("Production环境必须启用ControlServer车辆安全投影。");
            }

            return;
        }

        bool validEndpoint = Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint)
            && endpoint is not null
            && endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(endpoint.UserInfo)
            && string.IsNullOrEmpty(endpoint.Fragment);
        if (!validEndpoint
            || string.IsNullOrWhiteSpace(CredentialEnvironmentVariable)
            || OnboardSettings.IsPlaceholderValue(CredentialEnvironmentVariable)
            || string.IsNullOrWhiteSpace(ExpectedVehicleKey)
            || MaximumEvidenceAgeMs <= 0
            || ClockSkewToleranceMs < 0
            || ClockSkewToleranceMs > MaximumClockSkewToleranceMs
            || ClockSkewToleranceMs >= MaximumEvidenceAgeMs
            || PollIntervalMs <= 0
            || RequestTimeoutMs <= 0)
        {
            throw new InvalidDataException("ControlServer车辆安全投影配置无效，必须使用HTTP并配置身份、凭据和证据时效。");
        }

        if (production
            && (IsForbiddenProductionHost(endpoint!.Host)
                || OnboardSettings.IsPlaceholderValue(ExpectedVehicleKey)))
        {
            throw new InvalidDataException("Production环境的车辆安全投影地址或期望车辆身份仍是本机/占位配置。");
        }
    }

    private static bool IsForbiddenProductionHost(string host)
    {
        if (OnboardSettings.IsPlaceholderValue(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}

public sealed class WireToGateSettings
{
    public const int DefaultControlServerPort = 58_005;

    public bool Enabled { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = DefaultControlServerPort;

    public string OnboardInstanceId { get; init; } = string.Empty;

    public string OnboardBuildCommit { get; init; } = string.Empty;

    public string CredentialEnvironmentVariable { get; init; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    public int ConnectTimeoutMs { get; init; } = 3_000;

    /// <summary>
    /// 一条要应答的 WIRE_TO_GATE 消息等多久，毫秒。
    /// </summary>
    /// <remarks>
    /// 它同时是**两条心跳到达间距的上界**：心跳循环串行地等 <c>HeartbeatAck</c>，服务端看到的间距是
    /// max(<see cref="SessionHeartbeatIntervalMs"/>, ack 往返)。所以它与心跳间隔受同一个界约束，
    /// 见 <see cref="Validate"/>。默认值曾是 3000，恰好等于那个界、余量为零；2026-09-20 随
    /// onboard-hmi#142 的审查下调到 2500。
    /// </remarks>
    public int MessageTimeoutMs { get; init; } = 2_500;

    /// <summary>
    /// WIRE_TO_GATE 会话心跳的间隔，毫秒。ADR-cross-0027 定的是 2 秒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与同一份文件里 <c>ruleGateway.heartbeatIntervalMs</c> 不是一回事：那一个是旧规则网关的心跳
    /// （<c>TcpJsonRuleGateway</c>），与 WIRE_TO_GATE 会话无关，仍是 5 秒。
    /// </para>
    /// <para>
    /// 心跳与失联阈值是项目级统一配置、不按车设置，所以这里能配的只是「现场真要调」的那一格，
    /// 上下限由 <see cref="Validate"/> 按阈值卡死，而不是听任现场填。
    /// </para>
    /// </remarks>
    public int SessionHeartbeatIntervalMs { get; init; } =
        (int)WireToGateSessionService.DefaultHeartbeatInterval.TotalMilliseconds;

    public long CapabilityVersion { get; init; } = 1;

    public long SafetyStateVersion { get; init; } = 1;

    public string SlotModelVersion { get; init; } = "eight-slot-v1";

    public string ActiveSlotConfigurationVersion { get; init; } = "eight-slot-modbus-v1";

    /// <summary>
    /// 生效配置与激活结果那份原子文档放在哪。
    /// </summary>
    /// <remarks>
    /// 与日志、日志簿分开：它是**车对自己装着什么的唯一权威记录**，重启之后
    /// <c>CapabilitySnapshot</c> 报的指纹与版本名都从它读。默认落在日志簿旁边，好让一台车的持久状态
    /// 集中在一处，现场备份不会漏。
    /// </remarks>
    public string ActiveSlotConfigurationPath { get; init; } = "active-slot-configuration.json";

    public bool SupportsBatchUnlock { get; init; }

    public int JourneySnapshotMaxAgeMs { get; init; } = 5_000;

    public string OperatorIdEnvironmentVariable { get; init; } = "CONTROL_SERVER_OPERATOR_ID";

    public bool RecoveryResumeEnabled { get; init; }

    public string RecoveryAuthenticationProofEnvironmentVariable { get; init; } =
        "CONTROL_SERVER_RECOVERY_PROOF";

    public string RecoveryAdministratorRole { get; init; } = "MAINTENANCE_ADMINISTRATOR";

    public string RecoveryVerificationMethod { get; init; } = "CONFIGURED_PROOF";

    public string JournalPath { get; init; } = "%LOCALAPPDATA%\\SQCD\\8005AGV\\onboard-journal.db";

    public WireToGateSessionOptions CreateSessionOptions(string agvId)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("WIRE_TO_GATE尚未启用。");
        }

        Validate();
        return new WireToGateSessionOptions(
            Host,
            Port,
            agvId,
            OnboardInstanceId,
            OnboardBuildCommit,
            CredentialEnvironmentVariable,
            TimeSpan.FromMilliseconds(ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(MessageTimeoutMs),
            CapabilityVersion,
            SafetyStateVersion,
            SlotModelVersion,
            ActiveSlotConfigurationVersion,
            SupportsBatchUnlock);
    }

    internal void Validate(bool production = false)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Host)
            || Port is < 1 or > 65_535
            || !Guid.TryParseExact(OnboardInstanceId, "D", out _)
            || OnboardBuildCommit.Length != 40
            || OnboardBuildCommit.Any(character => !Uri.IsHexDigit(character))
            || OnboardBuildCommit.All(character => character == '0')
            || string.IsNullOrWhiteSpace(CredentialEnvironmentVariable)
            || ConnectTimeoutMs <= 0
            || MessageTimeoutMs <= 0
            || CapabilityVersion < 0
            || SafetyStateVersion < 0
            || string.IsNullOrWhiteSpace(SlotModelVersion)
            || string.IsNullOrWhiteSpace(ActiveSlotConfigurationVersion)
            || JourneySnapshotMaxAgeMs <= 0
            || string.IsNullOrWhiteSpace(OperatorIdEnvironmentVariable)
            || string.IsNullOrWhiteSpace(RecoveryAuthenticationProofEnvironmentVariable)
            || string.IsNullOrWhiteSpace(RecoveryAdministratorRole)
            || string.IsNullOrWhiteSpace(RecoveryVerificationMethod)
            || string.IsNullOrWhiteSpace(JournalPath))
        {
            throw new InvalidDataException("WIRE_TO_GATE配置无效。");
        }

        if (SessionHeartbeatIntervalMs <= 0
            || SessionHeartbeatIntervalMs >= WireToGateSessionService.MaximumHeartbeatInterval.TotalMilliseconds)
        {
            throw new InvalidDataException(
                "WIRE_TO_GATE会话心跳间隔必须为正，且严格小于ADR-cross-0027静默失联阈值的一半"
                + $"（{WireToGateSessionService.MaximumHeartbeatInterval.TotalMilliseconds:0}毫秒）。 ");
        }

        // 心跳循环串行地等 HeartbeatAck，所以服务端看到的**到达间距**是
        // max(心跳间隔, ack 往返)，而 ack 往返的上界就是这个超时。只卡心跳间隔不卡它，
        // 上面那道界就能从另一扇门绕开：间隔配 2 秒、超时配 10 秒，服务端慢应答时两条心跳可以隔
        // 10 秒才到，ADR-cross-0027「单次心跳丢失不构成失联」当场失效。所以这两个值受同一个界约束。
        if (MessageTimeoutMs >= WireToGateSessionService.MaximumHeartbeatInterval.TotalMilliseconds)
        {
            throw new InvalidDataException(
                "WIRE_TO_GATE消息超时必须严格小于ADR-cross-0027静默失联阈值的一半"
                + $"（{WireToGateSessionService.MaximumHeartbeatInterval.TotalMilliseconds:0}毫秒）："
                + "心跳要等应答才算走完一拍，这个超时就是两条心跳到达间距的上界。 ");
        }

        if (production
            && (IsForbiddenProductionHost(Host)
                || OnboardSettings.IsPlaceholderValue(OnboardBuildCommit)))
        {
            throw new InvalidDataException(
                "Production环境的ControlServer地址或构建commit仍是本机/占位配置。");
        }

        if (RecoveryAdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR"))
        {
            throw new InvalidDataException("恢复管理员角色必须是受支持的协议角色。 ");
        }

        if (OnboardSettings.IsPlaceholderValue(RecoveryAuthenticationProofEnvironmentVariable))
        {
            throw new InvalidDataException("恢复管理员凭据环境变量不能使用占位名称。 ");
        }

        if (RecoveryVerificationMethod is "BADGE" or "CARD" or "BIOMETRIC")
        {
            throw new InvalidDataException(
                "当前车载端只支持已配置凭据证明，不能把它标记为未接入的实体徽章或生物识别方式。 ");
        }
    }

    private static bool IsForbiddenProductionHost(string host)
    {
        if (OnboardSettings.IsPlaceholderValue(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}

public sealed class OnboardAutomationSettings
{
    public bool Enabled { get; init; }

    public string ListenAddress { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 58_007;

    internal void Validate(
        bool production,
        bool wireToGateEnabled,
        int wireToGatePort,
        int ioModulePort)
    {
        if (!Enabled)
        {
            return;
        }

        if (production)
        {
            throw new InvalidDataException(
                "Production环境禁止启用车载端自动化loopback接口，除非完成独立安全评审。 ");
        }

        if (!IPAddress.TryParse(ListenAddress, out IPAddress? address)
            || !IPAddress.IsLoopback(address)
            || Port is < 1 or > 65_535
            || Port == wireToGatePort
            || Port == ioModulePort
            || !wireToGateEnabled)
        {
            throw new InvalidDataException(
                "车载端自动化接口必须绑定loopback、使用独立端口，并且只能在WIRE_TO_GATE启用时开启。 ");
        }
    }
}

public sealed class RuleGatewaySettings
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 18_080;

    public int ConnectTimeoutMs { get; init; } = 3_000;

    public int RequestTimeoutMs { get; init; } = 3_000;

    public int HeartbeatIntervalMs { get; init; } = 5_000;

    public int ResultRetryCount { get; init; } = 3;

    public int[] ReconnectDelaysMs { get; init; } = [1_000, 2_000, 5_000, 10_000];

    internal void Validate(bool production = false)
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65_535)
        {
            throw new InvalidDataException("规则模块Host或Port无效。");
        }

        if (ConnectTimeoutMs <= 0 || RequestTimeoutMs <= 0 || HeartbeatIntervalMs <= 0 || ResultRetryCount <= 0)
        {
            throw new InvalidDataException("规则模块超时、心跳和重试参数必须大于0。");
        }

        if (ReconnectDelaysMs.Length == 0 || ReconnectDelaysMs.Any(delay => delay <= 0))
        {
            throw new InvalidDataException("规则模块重连间隔必须至少包含一个正整数。");
        }

        if (production && OnboardSettings.IsPlaceholderValue(Host))
        {
            throw new InvalidDataException("Production环境禁止保留规则模块占位地址。");
        }
    }
}

public sealed class IoModuleSettings
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 1_502;

    public byte UnitId { get; init; } = 255;

    public ushort DoStartAddress { get; init; } = 100;

    public ushort DiStartAddress { get; init; } = 200;

    public ushort ChannelCount { get; init; } = 16;

    public int PollIntervalMs { get; init; } = 100;

    public int RequestTimeoutMs { get; init; } = 1_000;

    public int ReconnectDelayMs { get; init; } = 1_000;

    public IReadOnlyList<SlotIoMapping> Slots { get; init; } = CreateDefaultSlots();

    internal void Validate(bool production = false)
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65_535)
        {
            throw new InvalidDataException("IO模块Host或Port无效。");
        }

        if (production && OnboardSettings.IsPlaceholderValue(Host))
        {
            throw new InvalidDataException("Production环境禁止保留IO模块占位地址。");
        }

        if (ChannelCount is 0 or > 2_000 || PollIntervalMs <= 0 || RequestTimeoutMs <= 0 || ReconnectDelayMs <= 0)
        {
            throw new InvalidDataException("IO模块通道数、轮询或超时参数无效。");
        }

        if (Slots.Count != 8
            || Slots.Select(slot => slot.SlotIndex).Distinct().Count() != 8
            || Slots.Any(slot => slot.SlotIndex is < 0 or > 7))
        {
            throw new InvalidDataException("必须且只能配置slotIndex 0到7的八个仓位。");
        }

        if (Slots.Any(slot => slot.DoChannel >= ChannelCount
            || slot.LockFeedbackDiChannel >= ChannelCount
            || slot.LightCurtainDiChannel >= ChannelCount))
        {
            throw new InvalidDataException("仓位IO映射超出配置的通道范围。");
        }

        if (Slots.Select(slot => slot.DoChannel).Distinct().Count() != 8)
        {
            throw new InvalidDataException("8个仓位的开锁DO通道不得重复。");
        }

        if (Slots.Any(slot => string.IsNullOrWhiteSpace(slot.SignalPolarity)
            || slot.PulseResetMilliseconds <= 0))
        {
            throw new InvalidDataException("仓位信号极性不得为空，开锁脉冲复位毫秒必须为正。");
        }

        ushort[] allDiChannels = Slots
            .SelectMany(slot => new[] { slot.LockFeedbackDiChannel, slot.LightCurtainDiChannel })
            .ToArray();
        if (allDiChannels.Distinct().Count() != 16)
        {
            throw new InvalidDataException("锁反馈DI和光幕DI必须使用16个互不重复的输入通道。");
        }
    }

    private static SlotIoMapping[] CreateDefaultSlots()
    {
        return Enumerable.Range(0, 8)
            .Select(index => new SlotIoMapping
            {
                SlotIndex = index,
                DoChannel = (ushort)index,
                LockFeedbackDiChannel = (ushort)index,
                LightCurtainDiChannel = (ushort)(index + 8)
            })
            .ToArray();
    }
}

public sealed class SlotIoMapping
{
    public int SlotIndex { get; init; }

    public ushort DoChannel { get; init; }

    public ushort LockFeedbackDiChannel { get; init; }

    public ushort LightCurtainDiChannel { get; init; }

    /// <summary>
    /// 这个仓的信号极性与开锁脉冲复位毫秒。
    /// </summary>
    /// <remarks>
    /// 它们是**协议 v2 的仓位配置指纹要算进去的硬件事实**，不是本机的运行参数——服务端手上有它批准的
    /// 一份同样的东西，激活时两边算同一个摘要来核对。默认值取自 REQ-0267 已批准的八仓事实
    /// （<c>ACTIVE_HIGH</c>、500ms）；现场若与服务端那份对不上，激活会被拒并报
    /// <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c>，那正是这条握手存在的意义。
    /// </remarks>
    public string SignalPolarity { get; init; } = "ACTIVE_HIGH";

    public int PulseResetMilliseconds { get; init; } = 500;
}

/// <summary>
/// 本机自述的整车仓位配置：协议 v2 的指纹算的就是它。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是车对「我装着什么」的自述，不是服务端下发的东西。</b>协议里没有任何一条消息携带仓位 IO
/// 绑定——消息 7 只带版本号与指纹。所以配置内容来自本机设置，激活是一次核验：服务端发它批准的那一版
/// 的指纹，车拿这份自述配置算指纹与之比对。
/// </para>
/// <para>
/// IO 点名由通道号渲染：<c>DO{通道+1}</c>、<c>DI{通道+1}</c>。默认通道映射渲染出来正好是 REQ-0267
/// 已批准的 <c>DO1..DO8</c>／<c>DI1..DI8</c>／<c>DI9..DI16</c>。现场改了通道映射而服务端那份没跟着改，
/// 指纹就对不上，激活被拒——这是这条握手要抓的事，不是要绕开的事。
/// </para>
/// </remarks>
public static class OnboardActiveSlotConfigurationFactory
{
    public static ActiveSlotConfiguration Create(WireToGateSettings wireToGate, IoModuleSettings ioModule)
    {
        ArgumentNullException.ThrowIfNull(wireToGate);
        ArgumentNullException.ThrowIfNull(ioModule);

        return new ActiveSlotConfiguration(
            wireToGate.SlotModelVersion,
            wireToGate.ActiveSlotConfigurationVersion,
            [
                .. ioModule.Slots.OrderBy(slot => slot.SlotIndex).Select(slot => new SlotConfigurationEntry(
                    slot.SlotIndex + 1,
                    slot.SlotIndex < 4 ? SlotGroupPresentation.FrontPosition : SlotGroupPresentation.RearPosition,
                    Point("DO", slot.DoChannel),
                    Point("DI", slot.LockFeedbackDiChannel),
                    Point("DI", slot.LightCurtainDiChannel),
                    slot.SignalPolarity,
                    slot.PulseResetMilliseconds))
            ]);
    }

    private static string Point(string prefix, ushort channel) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}{channel + 1}");
}

public sealed class WorkflowSettings
{
    public int UnlockFeedbackTimeoutMs { get; init; } = 3_000;

    public int UnlockOutputResetTimeoutMs { get; init; } = 3_000;

    public int OperationTimeoutMs { get; init; } = 120_000;

    public int FeedbackStableMs { get; init; } = 300;

    public int IoSnapshotMaxAgeMs { get; init; } = 1_000;

    public int MaxSublotLength { get; init; } = 128;

    public int MaxReopenAttempts { get; init; } = 2;

    /// <summary>
    /// 期待动作超时门槛（REQ-0358）：当前仓自本次操作第一次开锁起累计等待多久就上报服务端。不配时是 3 个
    /// <see cref="OperationTimeoutMs"/>（默认 6 分钟）；投运按现场实测标定时直接写毫秒数。
    /// </summary>
    public int? ExpectedActionOverdueMs { get; init; }

    public TimeSpan ExpectedActionOverdueThreshold =>
        TimeSpan.FromMilliseconds(ExpectedActionOverdueMs ?? 3L * OperationTimeoutMs);

    internal void Validate()
    {
        if (ExpectedActionOverdueMs is <= 0)
        {
            throw new InvalidDataException("期待动作超时门槛必须是正数毫秒。");
        }

        if (UnlockFeedbackTimeoutMs <= 0
            || UnlockOutputResetTimeoutMs <= 0
            || OperationTimeoutMs <= 0
            || FeedbackStableMs < 0
            || IoSnapshotMaxAgeMs <= 0
            || MaxSublotLength is < 1 or > 4_096
            || MaxReopenAttempts is < 0 or > 10)
        {
            throw new InvalidDataException("业务反馈超时、稳定窗口、快照时效、条码长度或重新开门次数参数无效。");
        }
    }
}

public sealed class LogSettings
{
    public string Directory { get; init; } = "logs";

    public bool WriteToConsole { get; init; }
}
