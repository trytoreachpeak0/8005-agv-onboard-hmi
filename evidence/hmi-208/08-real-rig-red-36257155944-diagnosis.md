# 新头部第一次真装置红：run 36257155944 的机理

CI `rig=real`，[run 36257155944](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/36257155944)，结论 failure。三端（读自场景那一步）：control-server `a407ec6111ec29e5110728f6d085a9467e39ffbe`、onboard `c19736f3601f4e0dfcea101dda98e2c3eaa57bef`、simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。停机码 `RIG_COMMIT_GUARD`、`RIG_DESKTOP_LOCK`、`RIG_DEADLINE`、`NOT_STARTED` 在源码回显之外都是 0。

失败原文：

```
real-onboard-compensate-then-reconnect-01 -- FAIL (1), 205s
Timed out after 120s waiting for: the onboard is waiting for the operator on the load slot. Last observed: (nothing)
```

以下每条标明「读到」（证据包、代理记录、代码）还是「推的」。证据包是 `gh run download 36257155944`，未入库。

## 车载端起来了、连上了、命令到了车

- 〔读到〕车载端 app 日志：00:58:50 启动，版本串 `0.2.0-safety-mock-travel+c19736f…`；00:58:54 `generation=1，readiness=Ready`。
- 〔读到〕代理连接 1：00:58:52 打开，00:59:17.152 `server->onboard SlotOperationCommand`。

## 机理

1. 〔读到〕**接收循环停了至少 2.2 秒。**代理 00:59:18.717 `onboard->server Heartbeat`，19.022 `server->onboard HeartbeatAck` 已转给车载端；车载端 20.7 那次心跳没发（心跳一问一答，上一问还在等应答），21.23 日志「上层会话不可用：The operation has timed out.」断线。
2. 〔读到〕**断线那一刻，装货处理正在「检查通过之后、写连接之前」。**车载端日志 21.349：PREPARING 进度失败，异常 `WireToGateConnectionGoneException`。hmi#204 之后这个类型只表示：发送前的会话检查通过了，写连接时连接已换。
3. 〔读到代码〕**这段跑在接收循环线程上。**`WireToGateSessionClient.cs` 的接收循环同步 `Invoke` `ServerCommandReceived`；`WireToGateBusinessService.HandleCommandAsync` 在第一个真正让出线程的点之前都在这条线程上。途中是读日志（`IsAttemptTakenOverAsync`、`ReadRecoveryStateCachedAsync`）、`_operationDisplayGate`（只有这一处用，当时空闲），然后是 `SendDurableCoreAsync` 里的 `StoreDurableAsync` 写发件箱。界面层那条订阅（`MainViewModel.RecordSlotOperationCommand`）走 `Dispatcher.BeginInvoke`，不阻塞，已排除。
4. 〔推的〕**卡在 SQLite 写盘。**`Microsoft.Data.Sqlite` 的 async 方法实际同步执行；`SqliteWireToGateJournal` 是 WAL + `synchronous = FULL`，每次写都 fsync；它的 `_gate` 空闲时 `WaitAsync` 同步通过。同一时间 vm01 上还有四个 CI 作业：cs#362 的 l2 `36256827782` 与 test `36256827779`、cs#358 的 l2 `36256730912` 与 test `36256730875`，时间全部重叠。整机已提交内存在场景开始时是 10.69 GiB，断线前后 11.15～11.63 GiB（上限 16）；上一轮通过时场景开始是 4.05 GiB。卡的是哪一次调用、卡了多久，没有直接证据：vm01 上的 stage root 已被作业的清理步骤删掉。
5. 〔读到〕**为什么场景过不去。**重连后握手只补发了已写进发件箱的那条 PREPARING（代理连接 2，00:59:23.354）。UNLOCKING（21.668）与 WAITING_OPERATOR（22.576）是在会话不可用时发的，按现有设计进度只在可用时写进发件箱，所以丢了。场景读的是服务端收件箱里的 `OperationProgress`（control-server `scripts/l2/L2RealOnboard.psm1:344-346`），看不到 WAITING_OPERATOR，等 120 秒超时。

## 不指向本 PR 第二版修复（`53321d5`）

- 〔读到〕放弃分支执行时会写「未发出的SafetyStateChanged（版本N）内容已被此刻读数取代；放弃它、跳过该版本，改报此刻读数。」。这一轮 app 日志共 17 行，没有这一行。清签名那一行只在放弃分支里。
- 〔读到〕断线前连接 1 上没有任何 `SafetyStateChanged`，app 日志也没有安全上报失败，所以没有待发项可放弃。
- 〔读到〕对照上一轮通过的 [run 36252845068](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/36252845068)（onboard `d5969504`）：收到 `SlotOperationCommand` 后 0.17 秒发出第一条 `OperationProgress`。

## 断线后装货结果会怎样

- 〔读到〕车载端：`OperationResult` 即使在会话不可用时产生，也先写进发件箱，下一次握手以同一 messageId 补发（`WireToGateSessionClient.SendDurableCoreAsync`，onboard-hmi#127）。
- 〔读到〕服务端（`fp/v2-impl@a374881c`，`OnboardMessageProcessor.cs`）：`OperationProgress` 只回 `DurableAck`，不改业务状态（`:436-438`）；`OperationResult` 在新会话里补发时会再处理一次、对账挂起的结果（`:134`）。
- 〔推的〕所以现场丢了 UNLOCKING、WAITING_OPERATOR 两条进度，只是看板少显示一段过程：操作员在车旁照常装货，结果重连后补发，服务端照常结算，不会卡住。

## 重跑对照

vm01 空闲时（cs#362 的两个作业 completed、五个挂在 vm01 上的仓库都没有 in_progress 或 queued 作业，01:19:09 核过）同 head 重跑，见 `09-real-rig-rerun-idle.txt`。第二次通过只算空闲条件下的对照，不是本 PR 代码没问题的证据；本 PR 代码不相关的依据是上面「不指向」一节。
