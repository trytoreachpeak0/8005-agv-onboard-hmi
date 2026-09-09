using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SQCD.Agv.Core;

/// <summary>
/// 一个仓位在本机生效配置里的全部内容。
/// </summary>
public sealed record SlotConfigurationEntry(
    int PhysicalSlotNumber,
    string SlotPosition,
    string UnlockOutputPoint,
    string LockFeedbackInputPoint,
    string LightCurtainInputPoint,
    string SignalPolarity,
    int PulseResetMilliseconds);

/// <summary>
/// 本机当前生效的整车仓位配置。
/// </summary>
/// <remarks>
/// <see cref="Fingerprint"/> 是 <c>CapabilitySnapshot.activeSlotConfigurationFingerprint</c> 的值，
/// 服务端凭它判断车上到底是哪一版。它由内容算出来，不是另存的一个字段——存两份就会有两份真相。
/// </remarks>
public sealed record ActiveSlotConfiguration(
    string SlotModelVersion,
    string ConfigurationVersion,
    IReadOnlyList<SlotConfigurationEntry> Slots)
{
    private string? _fingerprint;

    /// <summary>整车定长 8 仓。这是 8005 的硬件事实，不是可配置项。</summary>
    public const int RequiredSlotCount = 8;

    public string Fingerprint => _fingerprint ??= ComputeFingerprint(this);

    /// <summary>
    /// 两份配置相等当且仅当内容相等。合成的记录相等会按引用比较 <see cref="Slots"/>，那会让
    /// 「重启后读回来的还是同一份配置吗」这种问题得到错误的答案。
    /// </summary>
    public bool Equals(ActiveSlotConfiguration? other) =>
        other is not null && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// 配置在结构上是否可用。不合法的配置绝不允许被激活——半个配置比旧配置危险得多。
    /// </summary>
    public string? RejectionReasonCode()
    {
        if (Slots.Count != RequiredSlotCount)
        {
            return "SLOT_COUNT_INVALID";
        }
        int[] numbers = [.. Slots.Select(slot => slot.PhysicalSlotNumber).Order()];
        if (!numbers.SequenceEqual(Enumerable.Range(1, RequiredSlotCount)))
        {
            return "SLOT_NUMBERING_INVALID";
        }
        if (Slots.Any(slot =>
                string.IsNullOrWhiteSpace(slot.UnlockOutputPoint)
                || string.IsNullOrWhiteSpace(slot.LockFeedbackInputPoint)
                || string.IsNullOrWhiteSpace(slot.LightCurtainInputPoint)
                || string.IsNullOrWhiteSpace(slot.SignalPolarity)
                || slot.PulseResetMilliseconds <= 0))
        {
            return "SLOT_IO_BINDING_INCOMPLETE";
        }
        if (string.IsNullOrWhiteSpace(SlotModelVersion) || string.IsNullOrWhiteSpace(ConfigurationVersion))
        {
            return "CONFIGURATION_IDENTITY_MISSING";
        }
        return null;
    }

    private static string ComputeFingerprint(ActiveSlotConfiguration configuration)
    {
        // 用一个不可能出现在标识符里的分隔符，免得两个字段拼起来正好等于另一种拼法。
        const char Separator = '\u001f';
        StringBuilder canonical = new();
        canonical.Append(configuration.SlotModelVersion).Append(Separator)
            .Append(configuration.ConfigurationVersion);
        foreach (SlotConfigurationEntry slot in configuration.Slots.OrderBy(slot => slot.PhysicalSlotNumber))
        {
            canonical.Append(Separator)
                .Append(slot.PhysicalSlotNumber.ToString(CultureInfo.InvariantCulture)).Append(Separator)
                .Append(slot.SlotPosition).Append(Separator)
                .Append(slot.UnlockOutputPoint).Append(Separator)
                .Append(slot.LockFeedbackInputPoint).Append(Separator)
                .Append(slot.LightCurtainInputPoint).Append(Separator)
                .Append(slot.SignalPolarity).Append(Separator)
                .Append(slot.PulseResetMilliseconds.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}

public enum SlotConfigurationActivationStatus
{
    Activated,
    Rejected
}

/// <summary>服务端下发的一次激活。</summary>
public sealed record SlotConfigurationActivationRequest(
    string ActivationId,
    ActiveSlotConfiguration Configuration);

/// <summary>
/// 一次激活的结果。断线时它不会丢：重连后按 <c>PENDING_RESULT_REPLAY</c> 补报的就是这一份。
/// </summary>
public sealed record SlotConfigurationActivationResult(
    string ActivationId,
    SlotConfigurationActivationStatus Status,
    string ResultingFingerprint,
    string? ReasonCode,
    DateTimeOffset SettledAt);

/// <summary>
/// 本机生效配置与激活结果的持久化。两者一起原子落盘。
/// </summary>
/// <remarks>
/// 配置与结果写在同一份文档里，一次原子替换同时覆盖两半。分两次写就会有一个窗口：配置已切、
/// 结果没记下——重连补报时车会以为自己没激活过，于是再激活一次。
/// </remarks>
public interface IActiveSlotConfigurationStore
{
    ActiveSlotConfiguration Current { get; }

    /// <summary>这个 <c>activationId</c> 是否已经有结果。有就补报它，不重做。</summary>
    SlotConfigurationActivationResult? FindResult(string activationId);

    /// <summary>
    /// 原子提交：<paramref name="configuration"/> 为 <c>null</c> 时只记结果（激活被拒，
    /// 生效配置一个字节不动）。
    /// </summary>
    void Commit(ActiveSlotConfiguration? configuration, SlotConfigurationActivationResult result);
}

/// <summary>
/// 车载端的仓位配置激活：原子切换，且同一次激活只发生一次。
/// </summary>
public sealed class SlotConfigurationActivationCoordinator(
    IActiveSlotConfigurationStore store,
    TimeProvider clock)
{
    /// <summary>协议 v2 为这条可靠消息新增的 <c>recoveryRole</c>。</summary>
    public const string RecoveryRole = "SLOT_CONFIGURATION";

    private readonly IActiveSlotConfigurationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>本进程内真正执行过多少次激活。补报不增加它。</summary>
    public int ActivationsPerformed { get; private set; }

    public SlotConfigurationActivationResult Activate(SlotConfigurationActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActivationId);

        // 补报走的就是这一句：结果已经在，原样返回，不碰配置。REQ-0264 的「不能猜测成功」
        // 之所以要 RELIABLE 而不是 REQUEST/RESPONSE，正是为了让这条路存在。
        SlotConfigurationActivationResult? recorded = _store.FindResult(request.ActivationId);
        if (recorded is not null)
        {
            return recorded;
        }

        string? rejection = request.Configuration.RejectionReasonCode();
        if (rejection is not null)
        {
            SlotConfigurationActivationResult rejected = new(
                request.ActivationId,
                SlotConfigurationActivationStatus.Rejected,
                _store.Current.Fingerprint,
                rejection,
                _clock.GetUtcNow());
            _store.Commit(configuration: null, rejected);
            return rejected;
        }

        SlotConfigurationActivationResult activated = new(
            request.ActivationId,
            SlotConfigurationActivationStatus.Activated,
            request.Configuration.Fingerprint,
            null,
            _clock.GetUtcNow());
        _store.Commit(request.Configuration, activated);
        ActivationsPerformed++;
        return activated;
    }
}
