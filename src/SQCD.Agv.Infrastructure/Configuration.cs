using System.Net;
using System.Text.Json;

namespace SQCD.Agv.Infrastructure;

public sealed class OnboardSettings
{
    public string Environment { get; init; } = "Development";

    public string AgvId { get; init; } = "AGV-8005-01";

    public string OnboardInstanceId { get; init; } = "OBU-8005-01";

    public RuleGatewaySettings RuleGateway { get; init; } = new();

    public WireToGateSettings WireToGate { get; init; } = new();

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

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>
/// Configuration for the read-only vehicle-safety projection exposed by
/// ControlServer.  The endpoint is deliberately a complete HTTPS URI so that
/// an accidental HTTP fallback cannot be hidden in a host/port combination.
/// </summary>
public sealed class VehicleSafetySettings
{
    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = "https://control.example.invalid/api/onboard/v1/vehicle-safety";

    public string CredentialEnvironmentVariable { get; init; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    public string ExpectedVehicleKey { get; init; } = string.Empty;

    public int MaximumEvidenceAgeMs { get; init; } = 5_000;

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
            && endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(endpoint.UserInfo)
            && string.IsNullOrEmpty(endpoint.Fragment);
        if (!validEndpoint
            || string.IsNullOrWhiteSpace(CredentialEnvironmentVariable)
            || OnboardSettings.IsPlaceholderValue(CredentialEnvironmentVariable)
            || string.IsNullOrWhiteSpace(ExpectedVehicleKey)
            || MaximumEvidenceAgeMs <= 0
            || PollIntervalMs <= 0
            || RequestTimeoutMs <= 0)
        {
            throw new InvalidDataException("ControlServer车辆安全投影配置无效，必须使用HTTPS并配置身份、凭据和证据时效。");
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
    public bool Enabled { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 58_015;

    public string OnboardInstanceId { get; init; } = string.Empty;

    public string OnboardBuildCommit { get; init; } = string.Empty;

    public string CredentialEnvironmentVariable { get; init; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";

    public bool UseTls { get; init; } = true;

    public string? ServerCertificateSha256 { get; init; }

    public int ConnectTimeoutMs { get; init; } = 3_000;

    public int MessageTimeoutMs { get; init; } = 3_000;

    public long CapabilityVersion { get; init; } = 1;

    public long SafetyStateVersion { get; init; } = 1;

    public string SlotModelVersion { get; init; } = "eight-slot-v1";

    public string ActiveSlotConfigurationVersion { get; init; } = "eight-slot-modbus-v1";

    public bool SupportsBatchUnlock { get; init; }

    public int JourneySnapshotMaxAgeMs { get; init; } = 5_000;

    public string OperatorIdEnvironmentVariable { get; init; } = "CONTROL_SERVER_OPERATOR_ID";

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
            UseTls,
            ServerCertificateSha256,
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

        string normalizedCertificateHash = ServerCertificateSha256?
            .Replace(":", string.Empty, StringComparison.Ordinal) ?? string.Empty;
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
            || string.IsNullOrWhiteSpace(JournalPath)
            || (UseTls && normalizedCertificateHash.Length != 64))
        {
            throw new InvalidDataException("WIRE_TO_GATE配置无效。");
        }

        if (production
            && (IsForbiddenProductionHost(Host)
                || OnboardSettings.IsPlaceholderValue(OnboardBuildCommit)
                || string.IsNullOrWhiteSpace(ServerCertificateSha256)
                || ServerCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal)
                    .All(character => character == '0')))
        {
            throw new InvalidDataException(
                "Production环境的ControlServer地址、构建commit或TLS指纹仍是本机/占位配置。");
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

    internal void Validate()
    {
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
