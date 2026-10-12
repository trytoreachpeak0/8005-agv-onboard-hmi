using System.IO;
using System.Text;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>
/// The file operations the screen-state writer uses, behind one seam so a test can stand in a read-only directory or a
/// full disk, and record which thread wrote (onboard-hmi#315).
/// </summary>
internal interface IHmiScreenStateFileSystem
{
    void CreateDirectory(string path);

    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>Moves <paramref name="source"/> over <paramref name="destination"/> in one step.</summary>
    void Replace(string source, string destination);

    void AppendAllBytes(string path, byte[] bytes);

    /// <summary>The file's length, 0 when it does not exist.</summary>
    long LengthOf(string path);

    /// <summary>File names (not paths) directly in <paramref name="directory"/>, not recursive.</summary>
    IReadOnlyList<string> FileNamesIn(string directory);

    void Delete(string path);
}

internal sealed class HmiScreenStateFileSystem : IHmiScreenStateFileSystem
{
    internal static HmiScreenStateFileSystem Instance { get; } = new();

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void Replace(string source, string destination) => File.Move(source, destination, overwrite: true);

    public void AppendAllBytes(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    public long LengthOf(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? file.Length : 0;
    }

    public IReadOnlyList<string> FileNamesIn(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()]
            : [];

    public void Delete(string path) => File.Delete(path);

    internal static byte[] Utf8Line(byte[] json)
    {
        byte[] newline = Encoding.UTF8.GetBytes(Environment.NewLine);
        byte[] line = new byte[json.Length + newline.Length];
        json.CopyTo(line, 0);
        newline.CopyTo(line, json.Length);
        return line;
    }
}
