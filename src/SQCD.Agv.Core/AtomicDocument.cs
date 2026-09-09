namespace SQCD.Agv.Core;

/// <summary>
/// 一份要么整份是旧内容、要么整份是新内容的文档。
/// </summary>
/// <remarks>
/// 「原子」这三个字全部落在这一个接口上，所以它可以被单独测：断电、崩溃、断线都不能留下半份
/// 文档。上层（<see cref="SlotConfigurationActivationCoordinator"/>）因此不必自己处理半写状态，
/// 而不必处理的状态才是真的不会出现。
/// </remarks>
public interface IAtomicDocument
{
    /// <summary>读回已提交的内容；从未提交过则为 <c>null</c>。</summary>
    string? ReadCommitted();

    /// <summary>提交新内容。返回时要么全部生效，要么抛异常且旧内容完好。</summary>
    void Commit(string content);
}
