using System.Text.Json;

namespace SQCD.Agv.Infrastructure;

public sealed class OnboardSettings
{
    public string Environment { get; init; } = "Development";

    public string AgvId { get; init; } = "AGV-8005-01";

    public string OnboardInstanceId { get; init; } = "OBU-8005-01";

    public RuleGatewaySettings RuleGateway { get; init; } = new();

    public IoModuleSettings IoModule { get; init; } = new();

    public WorkflowSettings Workflow { get; init; } = new();

    public LogSettings Logging { get; init; } = new();

    public WireToGateRuntimeSettings WireToGate { get; init; } = new();

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

        RuleGateway.Validate();
        IoModule.Validate();
        Workflow.Validate();
        WireToGate.Validate();
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed class WireToGateRuntimeSettings
{
    public string ControlServerHost { get; init; } = "127.0.0.1";
    public int ControlServerPort { get; init; } = 58_005;
    public bool UseTls { get; init; }
    public string? ServerCertificateSha256 { get; init; }
    public string CredentialEnvironmentVariable { get; init; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";
    public int ConnectTimeoutMs { get; init; } = 3_000;
    public string IoSimulatorBaseUrl { get; init; } = "http://127.0.0.1:58006";
    public int IoRequestTimeoutMs { get; init; } = 2_000;
    public string JournalDatabasePath { get; init; } = "%ProgramData%/8005/OnboardHmi/data/onboard-journal.db";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ControlServerHost) || ControlServerPort is < 1 or > 65_535 ||
            string.IsNullOrWhiteSpace(CredentialEnvironmentVariable) || ConnectTimeoutMs <= 0 ||
            !Uri.TryCreate(IoSimulatorBaseUrl, UriKind.Absolute, out _) || IoRequestTimeoutMs <= 0 ||
            string.IsNullOrWhiteSpace(JournalDatabasePath))
        {
            throw new InvalidDataException("WIRE_TO_GATE ControlServer、IO simulator 或 journal 配置无效。");
        }
        if (UseTls && string.IsNullOrWhiteSpace(ServerCertificateSha256))
        {
            throw new InvalidDataException("WIRE_TO_GATE TLS 必须配置服务端证书 SHA-256 pin。");
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

    internal void Validate()
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

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65_535)
        {
            throw new InvalidDataException("IO模块Host或Port无效。");
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
