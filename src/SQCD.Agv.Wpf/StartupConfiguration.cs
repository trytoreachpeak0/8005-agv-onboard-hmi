using System.IO;
using System.Text;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// 启动时读配置的那一步：被拒就把原文与配置文件路径写进日志，交给调用方退出（onboard-hmi#14）。
/// </summary>
/// <remarks>
/// 调用方传进来的日志器必须是不依赖业务配置的引导日志器——配置本身就是这里可能被拒的东西。
/// 修复之前，读配置在建日志器之前，被拒时 catch 拿到的日志器是 null，一个字节都不写；
/// control-server#306 那次 <c>messageTimeoutMs=3000</c> 被拒，只能离线调 <c>OnboardSettings.Load</c> 才读到原文。
/// </remarks>
internal static class StartupConfiguration
{
    /// <summary>
    /// 读并校验 <paramref name="settingsPath"/>；被拒时写一条 Error 并返回 null。
    /// </summary>
    /// <param name="standardError">
    /// 同一句话再写一份的地方，缺省是进程的 stderr。拉起车载端的一方（L2 把 <c>logging.directory</c> 指到证据目录，
    /// 另把 stderr 收成 <c>.err.log</c>）看不到引导日志器写在程序旁边的那份；日志目录不可写时，它也是唯一的出处。
    /// </param>
    /// <remarks>
    /// 捕获全部异常，不按类型挑：这里只做读文件、解析、校验，没有任何副作用可以留到一半，而无论被拒成什么样子，
    /// 下一步都只有「写下原因然后退出」这一条路。按类型挑出来的漏网之鱼会重新变成一次不留痕迹的启动失败。
    /// </remarks>
    internal static OnboardSettings? TryLoad(
        string settingsPath,
        IAppLogger bootstrapLogger,
        TextWriter? standardError = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapLogger);
        try
        {
            return OnboardSettings.Load(settingsPath);
        }
#pragma warning disable CA1031 // 见 remarks：被拒成什么样都只能写日志退出。
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // 原因写进正文而不是只交给 exception 参数：FileAppLogger 只取最外层一句，内层（例如 JSON 转换失败时
            // 真正说明哪个值不对的那一句）会丢。
            string line = $"车载端配置被拒，程序退出：path={settingsPath}，reason={Describe(exception)}";
            bootstrapLogger.Write(LogSeverity.Error, nameof(App), line);
            if (standardError is null)
            {
                // 按 UTF-8 写，不用 Console.Error：WinExe 没有控制台，Console.Error 的编码退回系统 ANSI 代码页
                // （中文 Windows 是 GBK），收进 .err.log 的中文就成了乱码。本工作区的文本一律 UTF-8。
                using StreamWriter utf8 = new(Console.OpenStandardError(), new UTF8Encoding(false));
                WriteStandardError(utf8, line);
            }
            else
            {
                WriteStandardError(standardError, line);
            }

            return null;
        }
    }

    private static void WriteStandardError(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
            writer.Flush();
        }
        catch (IOException)
        {
            // stderr 断了（例如读端已关）不该挡住退出；日志文件那份仍在。
        }
    }

    private static string Describe(Exception exception)
    {
        List<string> parts = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" <- ", parts);
    }
}
