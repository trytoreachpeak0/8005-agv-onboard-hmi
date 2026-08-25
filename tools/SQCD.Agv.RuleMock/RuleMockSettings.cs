using System.Text.Json;

namespace SQCD.Agv.RuleMock;

public sealed class RuleMockSettings
{
    // 监听的IP地址，默认
    public string ListenIp { get; init; } = "127.0.0.1";

    // 监听的端口号，默认18080
    public int ListenPort { get; init; } = 18_080;

    // AGV的唯一标识，默认AGV-8005-01
    public string AgvId { get; init; } = "AGV-8005-01";

    // 访问的唯一标识，默认VISIT-MOCK-001
    public string VisitId { get; init; } = "VISIT-MOCK-001";

    // 站点的唯一标识，默认ST-MOCK-01S1
    public string StationId { get; init; } = "ST-MOCK-01";

    // 站点的名称，默认模拟站点A
    public string StationName { get; init; } = "模拟站点A";

    // 是否允许操作，默认true
    public bool AllowOperation { get; init; } = true;

    // 访问有效时间，单位分钟，默认60分钟
    public int VisitValidMinutes { get; init; } = 60;

    // 响应延迟，单位毫秒，默认50毫秒
    public int ResponseDelayMs { get; init; } = 50;

    // true时保持旧行为：握手后自动到站；默认false，用命令模拟在途、到站和离站。
    public bool AutoStartVisit { get; init; }

    // 模拟故障设置
    public MockFaultSettings Faults { get; init; } = new();

    // 子批次规则列表
    public IReadOnlyList<MockSublotRule> Rules { get; init; } = [];

    // 从指定路径加载RuleMock配置文件
    public static RuleMockSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到RuleMock配置文件。", path);
        }

        RuleMockSettings settings = JsonSerializer.Deserialize<RuleMockSettings>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException("RuleMock配置无效。");

        bool hasDuplicateSublots = settings.Rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Sublot))
            .GroupBy(rule => rule.Sublot.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);

        if (string.IsNullOrWhiteSpace(settings.ListenIp)
            || settings.ListenPort is < 1 or > 65_535
            || string.IsNullOrWhiteSpace(settings.AgvId)
            || string.IsNullOrWhiteSpace(settings.VisitId)
            || hasDuplicateSublots
            || settings.Rules.Any(rule => rule.SlotIndex is < 0 or > 7
                || string.IsNullOrWhiteSpace(rule.Sublot)
                || rule.OperationType is not ("Load" or "Unload")))
        {
            throw new InvalidDataException("RuleMock配置字段无效。");
        }

        return settings;
    }

    // JSON序列化选项，忽略大小写、跳过注释、允许尾随逗号
    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

// 仓位规则类，表示一个子批次号对应的任务ID和货位索引，以及操作类型
public sealed class MockSublotRule
{
    // 子批次号，不能为空字符串
    public string Sublot { get; init; } = string.Empty;

    // 任务ID，不能为空字符串
    public string TaskId { get; init; } = string.Empty;

    // 货位索引，范围0-7
    public int SlotIndex { get; init; }

    // 操作类型，Load或Unload，默认Load
    public string OperationType { get; init; } = "Load";
}

// 模拟故障设置类，表示是否忽略扫描请求、是否忽略结果确认、是否发送重复扫描响应、是否返回无效的货位索引
public sealed class MockFaultSettings
{
    // 是否忽略扫描请求，默认false
    public bool IgnoreScanRequests { get; init; }

    // 是否忽略结果确认，默认false
    public bool DropResultAcknowledgements { get; init; }

    // 是否发送重复扫描响应，默认false
    public bool SendDuplicateScanResponse { get; init; }

    // 是否返回无效的货位索引，默认false
    public bool ReturnInvalidSlotIndex { get; init; }
}
