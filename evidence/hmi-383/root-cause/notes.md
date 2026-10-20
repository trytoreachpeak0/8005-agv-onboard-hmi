# hmi#383 根因调查（中间结论，2026-10-21 凌晨）

## 已坐实的事实

1. **不是「5 秒预算偏紧」。** 票面推测是全量并行下 5 秒不够；但两次 CI 失败的消息都没有「given back for process stalls」，
   本机复现到的同形状失败里，测试线程 5 秒内轮询 282 次、最长一次 266 ms、线程池 5 线程 0 排队，产品日志在等待开始后 5 秒内一行都没有。
   也就是说测试线程好好地看了 5 秒，被等的那件事根本没在推进。
2. **两条用例用的夹具（StartTakeOverStopAsync）把车载端「等服务端 DurableAck」的超时设成 30 秒**（messageTimeout: 30s），
   不是默认的 2 秒。所以一条回执晚到，产品就在那儿等，最长 30 秒，测试的 5 秒先到。
3. **本机复现（单核亲和性 0x1，两条用例成对跑）：1/10 复现出 AnUnknownHandoffOverASettledLoadKeepsItsDemandOnBoard 的 CI 原形。**
   失败时假服务端收到的报文序列：…RecoveryActionSubmitted、OperationProgress×3（正常应为 PREPARING、UNLOCKING、WAITING_OPERATOR、PAUSED 四条，
   然后 FaultCargoRecoveryResult）。缺的是 PAUSED：正常它在 WAITING_OPERATOR 之后 2 秒（门等待超时）出现。
   车载端发完 WAITING_OPERATOR 要等它的 DurableAck 才进入门等待；30 秒超时下，回执不到就整段停住。
4. **定点注入可必现同一形状**：用假服务端现成的 OperationProgressAckHold 扣住交接期间的进度回执 → 20 秒内无结果、无日志（mode 1/2/3 三种时点都是）。
   这只证明「回执不到→卡住」，不证明 CI 上回执为什么没到。
5. 之后加了更细的跟踪与失败时自抓线程栈（dotnet-stack），单核、2～3 路并跑共约 110 轮 **0 次复现**——本机复现率 <1%，靠本机抓现场不现实。

## 还没坐实的

- CI 上那条进度回执（或 AClearing… 里 B 的 OperationResult 回执）为什么 3 秒以上没到车载端：
  假服务端读循环被前一条消息的处理卡住？车载端接收循环被同步事件处理（ServerCommandReceived/StateChanged 同步调用、夹具的
  `lock (_uiThread)`）挡住？还是 VM 上部分 vCPU 被宿主抢走（10-21 00:27 我压宿主时 run 39457429374 整轮崩，说明宿主负载会直接传到 VM）。
- 是测试替身/夹具的问题还是产品接收循环的问题，取决于上一条。

## 证据位置

- 本机复现原始日志：C:/work/8005-scratch/hmi383/diag/aff1-two/run-05.log
- 注入探针：C:/work/8005-scratch/hmi383/diag/probe-ackhold*.log
- CI 与本机逐条耗时对比：C:/work/8005-scratch/hmi383/diag/ci.tsv、local.tsv
- 诊断补丁均未提交，在 worktree C:/work/8005-wt/hmi383-8005-agv-onboard-hmi 的工作区里。
