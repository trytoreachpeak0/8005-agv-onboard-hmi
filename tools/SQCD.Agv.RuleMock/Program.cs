using SQCD.Agv.RuleMock;

// 读取JSON配置文件
string settingsPath = Path.Combine(AppContext.BaseDirectory, "rulemock.settings.json");
RuleMockSettings settings = RuleMockSettings.Load(settingsPath);
// 创建并启TCP服务端
await using RuleMockServer server = new(settings);
using CancellationTokenSource shutdown = new();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("8005 AGV RuleMock，按 Ctrl+C 停止。");
Console.WriteLine("命令：arrive=到站，depart=离站/在途，status=状态，help=帮助，quit=退出。");

Task serverTask = server.RunAsync(shutdown.Token);
try
{
    while (!shutdown.IsCancellationRequested)
    {
        string? command = await Console.In.ReadLineAsync(shutdown.Token);
        if (command is null)
        {
            break;
        }

        // Redirected test input can carry a UTF-8 BOM. Some Windows consoles decode
        // that preamble as either U+FEFF or the visible "锘?" prefix.
        string normalizedCommand = command
            .Trim()
            .TrimStart('\uFEFF', '\u9518', '\uFFFD', '?')
            .ToLowerInvariant();

        switch (normalizedCommand)
        {
            case "arrive":
                await server.ArriveAsync(shutdown.Token);
                break;
            case "depart":
                await server.DepartAsync("MOCK控制台模拟离站", shutdown.Token);
                break;
            case "status":
                Console.WriteLine(server.GetStatusText());
                break;
            case "help":
                Console.WriteLine("arrive 到站并允许扫码；depart 结束到站并进入在途；status 查看状态；quit 退出。");
                break;
            case "quit":
            case "exit":
                shutdown.Cancel();
                break;
            case "":
                break;
            default:
                Console.WriteLine($"未知命令：{command}。输入 help 查看帮助。");
                break;
        }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
finally
{
    shutdown.Cancel();
    await serverTask;
}
