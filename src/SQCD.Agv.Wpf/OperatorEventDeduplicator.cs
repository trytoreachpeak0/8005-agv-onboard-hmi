namespace SQCD.Agv.Wpf;

/// <summary>
/// 操作员「广播」事件的去重。广播是状态推送——服务端重放同一条命令、快照轮询重复读到同一版本，
/// 都会让同一句话被推很多遍，去重是它存在的理由。对操作员按键的「应答」不走这里，
/// 见 <see cref="WireToGateBusinessService"/> 的 PublishOperatorResponse。
///
/// 去重键是进程内状态，会话不是。服务端重启后新会话会重放它认为车载端可能没收到的命令，
/// 此时沉默是错的：新一代的操作员本来就该重新看到当前状态。所以换代要清空，
/// 与 QueueSafetyStateChangeAsync 里 _lastSafetySignature 的换代重置是同一个形状的修法。
/// 那次重置顺带治掉了这个集合此前从不清空的无界增长。
/// </summary>
public sealed class OperatorEventDeduplicator
{
    private readonly object _gate = new();
    private readonly HashSet<string> _publishedKeys = new(StringComparer.Ordinal);
    private long? _generation;

    /// <summary>
    /// 首次见到该键返回 true，同一代内再次见到返回 false。
    /// </summary>
    public bool ShouldPublish(string deduplicationKey)
    {
        lock (_gate)
        {
            return _publishedKeys.Add(deduplicationKey);
        }
    }

    /// <summary>
    /// 会话代号变化时清空。代号不变是常态，此时什么都不做。
    /// </summary>
    public void ResetOnNewGeneration(long? generation)
    {
        lock (_gate)
        {
            if (_generation == generation)
            {
                return;
            }

            _generation = generation;
            _publishedKeys.Clear();
        }
    }
}
