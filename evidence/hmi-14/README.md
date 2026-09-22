# onboard-hmi#14（v2 移植）红绿证据

**要修的事**：v2 的 `App.OnStartup` 先 `OnboardSettings.Load` 后建日志器。配置一被拒，catch 里的日志器是 null，一个字节都不写；接着是一个阻塞的模态框挡在 `Shutdown(-1)` 前面。车上是无人值守的自动登录桌面，没人点确定，于是「进程活着、没窗口、没日志」。control-server#306 那次 `messageTimeoutMs=3000` 被拒就是这个形状。

**修法**：先建不读业务配置的引导日志器（`<程序目录>/logs`，与出厂配置同一目录），再读配置；被拒时把原文与配置文件路径写进日志，以 -1 退出。提示框只在交互式桌面上弹，放在后台线程，最多挡 30 秒。

| 文件 | 内容 |
| --- | --- |
| `red-before-fix.txt` | 修复前（`86d42ce` 的 `App.xaml.cs`）三种真实拒收全部 90 秒不退出 |
| `green.txt` | 修复后 7 条全绿；进程用例约 32 秒＝本机交互式桌面上提示框的 30 秒时限＋启动 |
| `reverse-app-revert.txt` | 只把 `App.xaml.cs` 退回 `86d42ce`（新文件保留），进程用例三条全红，都是 90 秒不退出 |
| `mutation-notice.txt` | 提示框两处变异：线程改为前台、去掉超时，各自只红 `ANoticeNobodyDismissesStopsHoldingTheExitAtTheTimeout`；两次都先确认 `0 Error(s)` |

拒收是真实的：`messageTimeoutMs=3000`（出厂 `appsettings.json` 打开 `wireToGate` 后只改这一个值）、配置文件缺失、JSON 语法错误，不是删掉判据。
