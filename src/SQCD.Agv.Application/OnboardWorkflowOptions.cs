namespace SQCD.Agv.Application;

/// <summary>
///
/// </summary>
/// <param name="UnlockFeedbackTimeout">开锁反馈超时,车载端写入开锁 DO 后，最多等待多久必须看到锁反馈 DI 从“已锁”变成“未锁”。目前设置的是3000ms</param>
/// <param name="UnlockOutputResetTimeout">确认仓门已经解锁后，最多等待多久必须看到硬件脉冲对应的开锁DO自动恢复为0。</param>
/// <param name="OperationTimeout">人工操作和关门确认超时,确认仓门已解锁后，允许操作员放料或取料，并手动关闭仓门的最长时间。目前设置的是120000ms，也就是两分钟</param>
/// <param name="FeedbackStableWindow">反馈稳定窗口，某个“成功条件”不能只是瞬间出现，必须连续保持多久，车载端才真正认可。当前配置是300ms</param>
/// <param name="IoSnapshotMaxAge">允许用于安全判断的IO快照最大年龄。</param>
/// <param name="MaxSublotLength">允许提交的SUBLOT最大长度。</param>
/// <param name="MaxReopenAttempts">同一次任务允许操作员重新打开当前仓门的最大次数。</param>
public sealed record OnboardWorkflowOptions(
    TimeSpan UnlockFeedbackTimeout,
    TimeSpan UnlockOutputResetTimeout,
    TimeSpan OperationTimeout,
    TimeSpan FeedbackStableWindow,
    TimeSpan IoSnapshotMaxAge,
    int MaxSublotLength,
    int MaxReopenAttempts);
