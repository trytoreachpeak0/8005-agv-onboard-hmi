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
/// <para>
/// <see cref="Fingerprint"/> 是 <c>CapabilitySnapshot.activeSlotConfigurationFingerprint</c> 的值，
/// 服务端凭它判断车上到底是哪一版。它由内容算出来，不是另存的一个字段——存两份就会有两份真相。
/// </para>
/// <para>
/// <b>算法是两端共用的一套，必须逐字节一致。</b>协议 v2 的消息 7
/// <c>SlotConfigurationActivationCommand</c> **不携带配置内容**——整个协议里没有任何一条消息携带仓位
/// IO 绑定。它带的是版本号与指纹。所以那次激活是一次**核验**：服务端发它批准的那一版的指纹，车算自己
/// 手上那份的指纹，相等才切换。两端各算各的，这条握手永远不成立。
/// </para>
/// <para>
/// 因此摘要**只取两端都有的那六个字段**：仓号、三个 IO 点、极性、脉冲复位毫秒。
/// <see cref="SlotConfigurationEntry.SlotPosition"/> 是本机的位置名，服务端没有这个概念，算进去服务端
/// 就算不出车能算出的值。两个版本名也不在摘要里——摘要回答「两边手上的硬件事实是不是同一份」，版本名
/// 是另一个问题，消息 7 用 <c>targetSlotConfigurationVersion</c> 单独带；把版本名算进去等于要求车知道
/// 服务端的版本命名，而车恰恰不知道。
/// </para>
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

    /// <summary>
    /// 两端共用的规范化摘要。控制服务端那一半在 <c>ControlServer.Domain.SlotConfigurationFingerprint</c>，
    /// 规则逐字相同。
    /// </summary>
    /// <remarks>
    /// 数字一律用不变文化格式化——按当前区域格式化会在某些区域给出带分组分隔符的毫秒数，那样两台机器
    /// 算出的指纹不同，而且只在部署到那些机器上时才不同。
    /// </remarks>
    private static string ComputeFingerprint(ActiveSlotConfiguration configuration)
    {
        // 两个不可能出现在 IO 点名里的分隔符，免得两个字段拼起来正好等于另一种拼法。
        const char FieldSeparator = '';
        const char SlotSeparator = '';
        StringBuilder canonical = new();
        foreach (SlotConfigurationEntry slot in configuration.Slots.OrderBy(slot => slot.PhysicalSlotNumber))
        {
            if (canonical.Length > 0)
            {
                canonical.Append(SlotSeparator);
            }
            canonical
                .Append(slot.PhysicalSlotNumber.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                .Append(slot.UnlockOutputPoint).Append(FieldSeparator)
                .Append(slot.LockFeedbackInputPoint).Append(FieldSeparator)
                .Append(slot.LightCurtainInputPoint).Append(FieldSeparator)
                .Append(slot.SignalPolarity).Append(FieldSeparator)
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

/// <summary>
/// 服务端下发的一次激活。
/// </summary>
/// <remarks>
/// <b>它不带配置内容，因为协议里没有任何一条消息带。</b>消息 7
/// <c>SlotConfigurationActivationCommand</c> 带的是版本号与指纹，所以那次激活是一次**核验**：车拿自己
/// 手上那份配置算指纹，与 <see cref="TargetFingerprint"/> 比，相等才把这一版认作生效版本，不等就拒绝
/// 并报 <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c>。
///
/// <see cref="TargetConfigurationVersion"/> 是服务端对这一版的命名，车不参与命名，也不据它做任何判断
/// ——核验只看指纹。它的用处是激活成功后把这个名字记在本机生效配置上，好让
/// <c>CapabilitySnapshot.activeSlotConfigurationVersion</c> 报得出服务端认得的那个名字。
/// </remarks>
public sealed record SlotConfigurationActivationRequest(
    string ActivationId,
    string TargetConfigurationVersion,
    string TargetFingerprint);

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

    /// <summary>指纹核不上时的稳定错误码，取自协议那本封闭注册表。</summary>
    public const string FingerprintMismatchReasonCode = "SLOT_CONFIGURATION_FINGERPRINT_MISMATCH";

    private readonly IActiveSlotConfigurationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>本进程内真正执行过多少次激活。补报不增加它。</summary>
    public int ActivationsPerformed { get; private set; }

    /// <summary>
    /// 本机此刻的生效配置。<c>CapabilitySnapshot</c> 报的版本名与指纹取自它。
    /// </summary>
    /// <remarks>
    /// 从配置项现算那两项会让每次激活之后两端立刻对不上：激活记下的是服务端对这一版的命名，而配置项
    /// 里写的是本机自述的那个名字，两者本来就可以不同。
    /// </remarks>
    public ActiveSlotConfiguration ActiveConfiguration => _store.Current;

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

        ActiveSlotConfiguration held = _store.Current;

        // 先看本机这份配置本身合不合法。不合法的配置绝不允许被认作生效版本——半个配置比旧配置危险
        // 得多，而这条判断不需要服务端参与。
        string? rejection = held.RejectionReasonCode()
            // 再核指纹。不等意味着服务端批准的那一版与车手上这份不是同一份硬件事实，双方都不该让步：
            // 服务端改口就丢了权威，车改口就是宣称自己装着从没收到过的东西。拒绝，让人去查。
            ?? (string.Equals(held.Fingerprint, request.TargetFingerprint, StringComparison.Ordinal)
                ? null
                : FingerprintMismatchReasonCode);
        if (rejection is not null)
        {
            SlotConfigurationActivationResult rejected = new(
                request.ActivationId,
                SlotConfigurationActivationStatus.Rejected,
                held.Fingerprint,
                rejection,
                _clock.GetUtcNow());
            _store.Commit(configuration: null, rejected);
            return rejected;
        }

        // 指纹相等，所以「切换」不动任何硬件事实——它把服务端对这一版的命名记到本机生效配置上，
        // 好让 CapabilitySnapshot 报得出服务端认得的那个名字。指纹因此不变，这是对的。
        ActiveSlotConfiguration activatedConfiguration =
            held with { ConfigurationVersion = request.TargetConfigurationVersion };
        SlotConfigurationActivationResult activated = new(
            request.ActivationId,
            SlotConfigurationActivationStatus.Activated,
            activatedConfiguration.Fingerprint,
            null,
            _clock.GetUtcNow());
        _store.Commit(activatedConfiguration, activated);
        ActivationsPerformed++;
        return activated;
    }
}
