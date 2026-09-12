using System.Text;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// <see cref="IAtomicDocument"/> 的文件实现：先写临时文件并刷盘，再一次改名盖上去。
/// </summary>
/// <remarks>
/// 改名在 NTFS 上是原子的，所以任何时刻磁盘上的正式文件要么是完整的旧内容、要么是完整的新内容。
/// 断电或崩溃留下的是那个临时文件——它从来没有被读过，下次启动直接删掉。
/// </remarks>
public sealed class AtomicJsonFile : IAtomicDocument
{
    private readonly string _committedPath;
    private readonly string _pendingPath;

    public AtomicJsonFile(string committedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(committedPath);

        _committedPath = committedPath;
        _pendingPath = committedPath + ".pending";
        string? directory = Path.GetDirectoryName(committedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        DiscardUncommitted();
    }

    public string? ReadCommitted() =>
        File.Exists(_committedPath) ? File.ReadAllText(_committedPath, Encoding.UTF8) : null;

    public void Commit(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using (FileStream stream = new(
            _pendingPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        // 到这一行为止，正式文件仍是完整的旧内容。这一行之后，它是完整的新内容。中间没有第三种状态。
        File.Move(_pendingPath, _committedPath, overwrite: true);
    }

    /// <summary>丢掉上次断电或崩溃留下的临时文件。它没有被提交过，所以没有信息可丢。</summary>
    public void DiscardUncommitted()
    {
        if (File.Exists(_pendingPath))
        {
            File.Delete(_pendingPath);
        }
    }
}
