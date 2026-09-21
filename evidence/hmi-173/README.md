# onboard-hmi#173（一并落 #165）红绿证据

**要防的事**：同一台车载电脑上起出两个车载端进程。两个都会无条件构造 Modbus 客户端，同时对着同一个真实 IO 模块写开锁 DO。
服务端的 `sessionGeneration` 只裁决会话，管不住两个进程同时写 DO。2026-09-20 在生产车 agv01 上发生过：现场线装着守卫，
却因为「拿不到互斥体」而放行，35 秒后第二个实例真的起来了。v2 在本票之前 `src`、`tests` 下没有任何单例守卫（`git grep -iE "mutex|singleinstance"` 零命中）。

## 做了什么

- `src/SQCD.Agv.Infrastructure/SingleInstanceGuard.cs`：整机名字 `Global\SQCD.Agv.Wpf.Onboard`（不带车辆身份）。
  先 `Mutex.TryOpenExisting`：存在但被拒绝访问 → 已有实例（#165 第 1 条）；不存在 → 建；等待时拿到 `AbandonedMutexException` 算抢到；
  名字被别的类型对象占着、或别的错误 → 判断不了。**只有「抢到了」才启动**，其余一律不启动。不降级到 `Local\`。
- 跨版本：再**只打开、不创建**现场线守卫会用的两个名字（`Global\SQCD.Agv.Wpf-{agvId}` 与 `Local\SQCD.Agv.Wpf-{agvId}`，
  agvId 去首尾空白、反斜杠换成下划线，照现场线 03027de 的 `BuildName`），存在即不启动。
- `App.OnStartup`：读完配置、建好日志器之后，在构造车辆安全投影（一构造就轮询服务端）与 Modbus IO 客户端之前问守卫；
  被挡下时以退出码 3 退出。名字在 IO 客户端停下之后才释放。
- `src/SQCD.Agv.Wpf/RunningInstanceWindow.cs`：被挡下的新进程 `AllowSetForegroundWindow` 让出前台权，再广播一条注册窗口消息；
  已有实例在自己的窗口过程里收到后，最小化时还原、然后激活（#165 第 2 条：现场线让新进程直接 `SetForegroundWindow`，
  被 Windows 前台限制静默拒绝）。拒绝提示窗口 10 秒后自己关掉：G3 以隐藏窗口启动车载端，模态框会让被挡下的进程永远不退出。

## 与 #165／调研报告结论相反之处

#165 与 2026-09-21 调研报告都写「守卫自己建不起来时放行」，理由是「一辆车因为抢不到一个名字而起不来，代价大于偶发双开」，
前提是建 `Global\` 名字要 `SeCreateGlobalPrivilege`、没有它的账户会被误伤。**这个前提不成立**。微软文档《Kernel object namespaces》：

> The creation of a file-mapping object or symbolic link object in the global namespace … from a session other than session zero is a privileged operation.

需要这个特权的只有 file-mapping 与 symbolic link 对象，互斥体不在其列。所以「自己建不起来、其实没有别的实例」这一支对互斥体基本不存在，
剩下的「判断不了」一律按已有实例处理（调度 09-21 定）。**代价**：真碰上判断不了时车起不来，但那是看得见的——提示窗口写明原因、
日志有一行、退出码 3；两个进程同时写 DO 是看不见的。

## red/：变异探针（守卫与启动路径，8 个）

把一处判断改回错的样子，看预期的测试红、红在预期的断言上。每份开头有事先写下的预期与对 HEAD 的 diff，编译 `0 Error(s)` 才算数，
末尾列出失败断言所在的行；按字节备份还原，blob 与 HEAD 一致。`--filter SingleInstanceGuardTests`（12 条）。

| 文件 | 改回的错 | 实际（与预期一致） |
| --- | --- | --- |
| `M1-access-denied-open-lets-the-start-through` | 打开被拒绝访问 → 可以启动（#165 原样） | 只红「存在但打不开 = 已有实例」 |
| `M2-abandoned-mutex-taken-as-failure` | 被遗弃 → 抢失败 | 只红「被遗弃的名字算抢到」 |
| `M3-only-already-running-refuses` | 除「已有实例」外都启动（现场线的失败方向） | 红「只有抢到才启动」与「名字被别的类型占着」两条 |
| `M4-field-line-names-not-looked-at` | 不看现场线的名字 | 红两条跨版本用例 |
| `M5-field-line-name-denied-taken-as-absent` | 现场线名字被拒绝访问 → 当作不存在 | 只红那一条 |
| `M6-machine-name-session-local` | 整机名字改成 `Local\` | 只红「整机名字是 Global」 |
| `M7-io-client-built-before-the-guard` | IO 客户端移到守卫之前构造 | 只红启动顺序那条结构守卫 |
| `M8-name-released-before-the-io-client-stops` | 名字在 IO 客户端停下之前释放 | 只红启动顺序那条结构守卫 |

**M2 第一次没红。** 第一版「被遗弃」用例让持有线程不释放就退出，但那个线程握着的是唯一的句柄，线程一退，内核把对象连名字一起销毁，
下一次走的是新建，根本碰不到被遗弃那一支——把它改成「抢失败」用例照样绿。现在测试另开一个句柄让对象活着。同一个误解也写在现场线
注释里（「L2/G3 强杀重启靠这一支」）：被杀的进程握着唯一句柄时，重启走的其实是新建。守卫注释已改正，8 个变异在改正后全部重跑。

## local-two-instances.txt：本机真起多个车载端

默认 `appsettings.json`（IO 与规则网关都是 127.0.0.1，WIRE_TO_GATE 与自动化接口关着，车辆安全端点是 `.invalid`），构建输出复制到
临时目录运行，全程持有桌面锁 `Global\W2G-InteractiveDesktop`。在 `7f4adfd` 的产品代码上跑；之后产品代码只改了注释（非注释改动 0 行）。

| 步骤 | 结果 |
| --- | --- |
| A 起来并有窗口，然后把 A 最小化 | 是 |
| 起 B | B 以**退出码 3** 退出（约 11 秒，提示窗口停留 10 秒后自己关）；A 仍在跑、**不再最小化、是前台窗口** |
| 强杀 A，再起 C | C 正常起来并有窗口 |
| 正常关闭 C（退出码 0），再起 D | D 正常起来 |
| 本机持有现场线名字 `Global\SQCD.Agv.Wpf-AGV-8005-01`，起 E | E 以**退出码 3** 退出 |
| 日志 | 「车载端启动」恰好 3 行（A、C、D）；守卫 5 行，两行「本次不启动」分别写明整机名字被持有、现场线名字存在 |

跨账户（ssh 的 session 0）那条路径没在本机真起：它的判定已由「拒绝所有人的 DACL」用例覆盖，那是同一个 `UnauthorizedAccessException`。

## green/

- `unit-tests.txt`：`dotnet test tests/SQCD.Agv.UnitTests -c Release`，**526 通过、0 失败**（基线 514，新增 12 条）。
- `dotnet-format-verify.txt`：`exit=0`。

## 守不到的

- **反方向**：现场线车载端看不见 v2 的整机名字，先起 v2、后起现场线时，现场线会照样起来。不在本票（调度 09-21 定），需要现场线侧改。
- 另一台机器上的实例（同一个 IO 模块被两台电脑连着）：名字是本机的内核对象，跨机器看不见。
- 不经过本程序、直接连 IO 模块的其它工具。
- 一个进程故意长时间持有同名对象会让车载端起不来（按「判断不了」拒绝）；那是看得见的，不是静默双开。

## 还没做的

- 真装置 `real-onboard-compensate-then-reconnect`：它会重启车载端，要证实先强杀再起时新实例能拿到名字、场景照常通过。已带运行清单向调度申请。
- 服务端对同一 agvId 第二次握手的具体行为（票面「未确定」）：本票的修法不依赖它——第二个实例在构造任何连接之前就退出了，所以没有申请专门的真装置运行。
