# hmi#264 证据：把 hmi#254、hmi#261、hmi#250 同步进 batch-p3/v3

## 哪些证据是 v3 的，哪些不是

`evidence/hmi-254/g2-full-9349f30/` 和 `evidence/hmi-250/g2-full-6aa0389/` 是 cherry-pick 时随 fp 线的提交一起带过来的，记录的是 `w2g/fp-v2-impl` 上、协议 `protocol-v2.0.0` 下的全量结果，**不是 v3 的**。`evidence/hmi-254/` 和 `evidence/hmi-250/` 下的其他文件也一样，都属于 fp 线。

本票自己的证据只在 `evidence/hmi-264/` 下。

## 判故障窗口用例（`declaration-window-green-red.txt`）

`StationDeadlineExpiredG2Tests.AnAckArrivingAfterTheDeclarationAbortedItsProgressIsTakenAndTheResultStillGoesOut`。

用例的做法是：替身扣住重新提示的 ack，等车载端写出「已执行服务端的人工判故障」之后才放行。这句日志在执行器停下之后才写，此时那次进度发送的等待已经随中止撤掉，所以放行的 ack 一定是迟到的。用例里还断言了放行之前这个 ack 确实没有发出。第一版用的是固定 1 秒延迟，按 PR #265 审查的意见改成了现在的写法。

- 修复后：通过。
- 修复前（在本分支上临时去掉接收循环里处理迟到 ack 的那一支）：在「发件箱记为已确认」处等待超时，日志是「收到未处理的WIRE_TO_GATE消息：DurableAck」。还原后重新构建。

两次都是单跑这一条用例，没有负载。改用例之后，`StationDeadlineExpiredG2Tests` 全类重跑 71/71。
