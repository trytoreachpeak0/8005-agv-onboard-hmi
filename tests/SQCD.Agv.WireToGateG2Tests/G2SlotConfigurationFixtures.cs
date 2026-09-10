using SQCD.Agv.Core;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// G2 各条用例共用的仓位配置夹具。
/// </summary>
/// <remarks>
/// 协议 v2 起，会话客户端必须拿得到本机生效配置——<c>CapabilitySnapshot</c> 报的指纹与版本名取自它，
/// 消息 7 的核验也拿它比对。G2 关心的是线上的形状与时序，不是配置怎么落盘，所以这里给一份内存里的
/// 原子文档与一份与 REQ-0267 已批准事实一致的八仓配置。
/// </remarks>
internal static class G2SlotConfigurationFixtures
{
    /// <summary>
    /// 与控制服务端 <c>ApprovedSlotHardwareFacts</c> 逐字段相同的一份配置。
    /// </summary>
    /// <remarks>
    /// 用它，是为了让 G2 里的假服务端可以拿真正会被接受的那个指纹来下发激活——用一份随手编的配置，
    /// 每条激活用例都会走进指纹不匹配那条分支，正例就没了。
    /// </remarks>
    public static ActiveSlotConfiguration Approved() => new(
        "eight-slot-v1",
        "eight-slot-modbus-v1",
        [
            .. Enumerable.Range(1, 8).Select(number => new SlotConfigurationEntry(
                number,
                number <= 4 ? "LEFT" : "RIGHT",
                $"DO{number}",
                $"DI{number}",
                $"DI{number + 8}",
                "ACTIVE_HIGH",
                500))
        ]);

    /// <summary>内存里的原子文档：整份换掉，没有半写状态。</summary>
    public sealed class InMemoryAtomicDocument : IAtomicDocument
    {
        private string? _committed;

        public string? ReadCommitted() => _committed;

        public void Commit(string content) => _committed = content;
    }
}
