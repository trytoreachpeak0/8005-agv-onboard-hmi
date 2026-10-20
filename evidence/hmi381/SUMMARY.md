# hmi#381 证据：卸货全部仓位用例在新 CI 机上偶发判 UNKNOWN

## 根因

- 失败路径：执行器每个仓位开锁前做「其他门都已关」检查（`WireToGateSingleDoorRule.OtherDoorNotShut`），
  先判 IO 快照是否新鲜（`IoSnapshotMaxAge` = 1 s）。不新鲜就是 `SLOT_STATE_UNKNOWN`，抛 `RefusedBeforeFirstPulseException`，
  该仓记 `NOT_STARTED[SLOT_STATE_UNKNOWN]`，其后各仓 `NOT_STARTED[]`，整单 `UNKNOWN`。
- 为什么只在测试里出现：用例的假 IO（`SimulationIo`）只在被调用（开锁脉冲、`WaitForLockerAsync`）时才刷新快照时间戳。
  两仓之间执行器要写一次 SQLite 检查点，这段时间没人刷新快照，快照按挂钟变旧。一次超过 1 s 的停顿就让下一仓的检查失败。
  CI 失败那次（run 39442679716）整条用例用了 3 s，本机平时 0.5～0.9 s。
- 产品不受影响：真 `ModbusTcpIoModuleClient` 有独立的轮询循环（`PollIntervalMs`），快照时间戳一直在刷新，与执行器写日志簿快慢无关。
  轮询本身停住超过 1 s 时执行器拒开，是有意的失败即关闭。
- 同源的第二条路径（推断，未单独复现）：假 IO 的等待截止也按挂钟算（1 s），一次 `Task.Delay(1)` 停顿超过 1 s 会抛 `TimeoutException`，
  同样落到 UNKNOWN（`ACTION_NOT_ALLOWED_IN_STATE`）。本次修法一并消掉。
- 孪生用例：`EveryUnlockOfALoadOrAnUnloadFindsEveryOtherDoorShut(Unload)` 是同一夹具、同样 8 仓卸货，本机在并行负载下红过一次（2 s），
  修法覆盖它。

## 复现率（`red/amplified-before-fix.txt`，基线 `985bfc9`）

临时诊断用例（`Hmi381Diag.cs.txt`，不入库；改前版本用 `SystemClock` 与挂钟时间戳，其余相同），同一 8 仓卸货，只缩新鲜度预算：

| IoSnapshotMaxAge | 红 | 开锁前快照最大年龄 |
| --- | --- | --- |
| 1000 ms（产品值） | 0/30 | 约 100～120 ms |
| 100 ms | 1/20 | |
| 30 ms | 20/20 | 约 20～29 ms |
| 10 ms | 20/20 | |

红的形状全部是「某仓 `NOT_STARTED[SLOT_STATE_UNKNOWN]`、其后 `NOT_STARTED[]`、整单 `UNKNOWN`」。

## 修法

`TestFixture` 给执行器和 `SimulationIo` 同一个冻结时钟（`FrozenClock`），假 IO 的时间戳与等待截止都取这个时钟。
快照年龄恒为 0，与机器忙不忙无关；`MakeStale` 一类造旧快照的用例按同一时钟往回减，仍然成立。没有放宽任何预算。
卸货用例另加 30 s 上限，仓位永远到不了 EMPTY 时判红而不是挂住。

## 红证据

- `red/test-commit-567108d.txt`：测试提交（执行器用冻结时钟、假 IO 仍用挂钟）上红，`Expected "COMPLETED", Actual "FAILED"`。
- `red/semantic-slot5-never-empty.txt`：改后变异假 IO，让 5 号仓关门后仍有货（永远到不了 EMPTY），用例在 30 s 上限处红；变异已还原。

## 绿证据

- `green/amplified-after-fix.txt`：同一诊断改用冻结时钟，预算 1000／30／10／1 ms 各 20/20 绿。
- `green/loop20-after-fix.txt`：卸货用例与孪生用例（Load、Unload 两组）原样循环 20 轮，20/20 绿。
- 整类 `WireToGateSlotOperationExecutorTests` 86 条绿；`dotnet format --verify-no-changes` exit 0。
- 本机 `scripts/run-w2g-g2.ps1 -Slice FP-IS-04`（提交 `fd6742c`，协议 `protocol-v5.0.0` `18d4fb3`）：Status PASS；
  `ArchitectureTests` 203 条绿。
