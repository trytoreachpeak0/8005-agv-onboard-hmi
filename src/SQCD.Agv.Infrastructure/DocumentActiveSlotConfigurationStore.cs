using System.Text.Json;
using System.Text.Json.Serialization;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 生效配置与激活结果存在同一份原子文档里。
/// </summary>
/// <remarks>
/// 两者一起写是本票的要害。分两次写会留下一个窗口：配置已经切了、结果还没记下——断线重连补报时，
/// 车会以为自己没激活过，于是**再激活一次**，而 REQ-0264 要的恰恰是不能有第二次。
/// </remarks>
public sealed class DocumentActiveSlotConfigurationStore : IActiveSlotConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>保留多少条历史结果供补报。一次激活最多补报几次，几十条绰绰有余。</summary>
    private const int RetainedResults = 64;

    private readonly IAtomicDocument _document;
    private Snapshot _state;

    public DocumentActiveSlotConfigurationStore(IAtomicDocument document, ActiveSlotConfiguration initial)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(initial);

        _document = document;
        string? committed = document.ReadCommitted();
        _state = committed is null
            ? new Snapshot(initial, [])
            : JsonSerializer.Deserialize<Snapshot>(committed, SerializerOptions)
                ?? new Snapshot(initial, []);
    }

    public ActiveSlotConfiguration Current => _state.Configuration;

    public SlotConfigurationActivationResult? FindResult(string activationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationId);

        return _state.Results.FirstOrDefault(
            result => string.Equals(result.ActivationId, activationId, StringComparison.Ordinal));
    }

    public void Commit(ActiveSlotConfiguration? configuration, SlotConfigurationActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<SlotConfigurationActivationResult> results = [result, .. _state.Results];
        if (results.Count > RetainedResults)
        {
            results.RemoveRange(RetainedResults, results.Count - RetainedResults);
        }
        Snapshot next = new(configuration ?? _state.Configuration, results);

        // 先落盘再换内存里的那份：Commit 抛异常时进程内的状态还是旧的，与磁盘一致。
        _document.Commit(JsonSerializer.Serialize(next, SerializerOptions));
        _state = next;
    }

    private sealed record Snapshot(
        ActiveSlotConfiguration Configuration,
        IReadOnlyList<SlotConfigurationActivationResult> Results);
}
