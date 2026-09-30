# hmi#228 证据：G2 用例「等一样、断另一样」

本目录是 onboard-hmi#228 的注入验证、产品回归验证、层间隔探测与复现实验的留档。结论与逐条判定见 PR 正文，这里只说每份东西是什么、怎么复现。

基线：`w2g/fp-v2-impl` @ `b31e5196`。测试改动提交：`f6c464d`（只动 `tests/`，`src/` 一行未改）。

## 目录

| 位置 | 内容 |
| --- | --- |
| `scan-base-b31e519.txt` | 扫描脚本对基线测试目录的输出：每一对「等待 → 紧跟的断言」里断言读到而等待没读到的层（CROSS），末尾是计数与按缺失层的分桶 |
| `runs-summary.tsv` | 本票每一次运行的汇总行与失败用例名（红／绿／回归／探测／复现），一行一轮 |
| `injections/` | 每次注入与回归对 `src/`（和夹具 `FakeControlServer.cs`）的实际改动，`git diff` 原样导出 |
| `runs/red/` | 旧写法加固定延迟的代表性失败原文（每组一轮） |
| `runs/green/` | 新写法加同样延迟的通过记录 |
| `runs/regression/` | 新写法在产品回归下的失败原文；`g2-old-tests` 是旧写法在同一回归下的对照 |
| `runs/probe/` | 在层边界插固定间隔跑 G2 全量或单类的结果 |
| `runs/repro/` | 四大类并行 6 轮里唯一一次失败的原文（范围外，已报调度） |
| `tools/` | `scan.py`（扫描）、`inject.py`（注入与还原）、`run3.sh`（构建一次跑 N 轮）、`repro.ps1`（不重编跑 N 轮） |

## 注入的规矩

- 注入与回归一律在副本上做：`inject.py` 先把被改的产品文件和夹具备份，每次 `apply` 前从备份还原，`restore` 拷回并刷新修改时间以保证重编；不用 `git checkout`。
- 每处替换必须恰好匹配一次，否则整次注入中止，免得「没注进去的注入」看起来像绿。
- 注入行都带 `HMI228-INJECT` 标记；每组跑完都核对过全仓没有残留标记、`git status` 只剩测试文件。
- 延迟都按快照内容或固定位置生效（例如「状态刚成为 AT_WAITING_POINT 时睡 300 ms」「第三个 SnapshotAppliedAck 读到后睡 500 ms」），不依赖竞态时机。唯一的例外写在 PR 里：过期拒收那一条的入口关闭，同一个字段还被控制器的周期刷新写，所以旧写法在延迟下是 1/3 红而不是 3/3。

## 复现

`tools/` 里的脚本把工作树路径写死为 `C:/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi228-8005-agv-onboard-hmi`，换机器要先改开头的常量。

```
python tools/inject.py apply delay-idle-status delay-fake-server-handling   # 注入
bash tools/run3.sh <输出目录> <标签> "<VSTest 过滤器>" 3                        # 构建一次跑三轮
python tools/inject.py tests old                                             # 测试文件换回基线写法
python tools/inject.py tests new                                             # 换回本票写法
python tools/inject.py restore                                               # 还原产品与夹具
python tools/scan.py tests/SQCD.Agv.WireToGateG2Tests [--all] [--dump]       # 扫描
```

## 本机全量

`scripts/run-w2g-g2.ps1`，提交 `f6c464d`，协议 `protocol-v2.0.0` @ `8657545`：Status PASS，G1 PASS；`SQCD.Agv.UnitTests` 636/636，`SQCD.Agv.WireToGateG2Tests` 508/508，失败 0、跳过 0。证据目录 `evidence/g2/protocol-v2.0.0/20260930T172854411Z-f6c464d8a8f5`（本地，未入库）。
