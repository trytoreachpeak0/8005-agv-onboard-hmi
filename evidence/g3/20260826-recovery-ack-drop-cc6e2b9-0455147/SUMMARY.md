# 2026-08-26 阶段性 G3：RecoveryStateReport 首 Ack 丢失

## 结论

`STAGED_G3_REAL_PEERS_RECOVERY_ACK_DROP` 稳定复现
`FAIL_CROSS_REPOSITORY_RECOVERY_REPLAY`。该红结果阻断 `W2G-IS-00` 与
`W2G-IS-06` 的相关 G3 向量；不得由此前“不动车重连”阶段性 PASS 覆盖。

本次只在 loopback 代理中丢弃第一次 `RecoveryStateReport` 的
`DurableAck`。没有创建 RIoT 订单、发送移动命令、执行开仓动作或伪造车辆停稳
信号。

## 冻结身份

- ControlServer：`cc6e2b97e4308fa14b519edf9a0089d0da7d6d14`
- OnboardHmi：`045514770da9858a8a49196dede276192e4f2a1b`
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- 运行配置 SHA-256：`bf7da14960ac8fcc0130c9165056d1f922add8b1472e759037e5a8572a41fc40`

## 实际轨迹

1. generation `1` 完成 Capability 与 Safety 快照，真实 Onboard 发送
   `RecoveryStateReport`，messageId 为
   `1ff687b2-bbfc-4a6c-94c3-ca8b435586f5`；
2. 代理只丢弃该消息的第一条 `DurableAck`，同时转发同一次服务端写入中的
   `SessionReadiness`；Onboard 因期望 Ack 而收到 Readiness，安全断开并在 2 秒后
   重连；
3. generation `2`、`3`、`4` 的新连接上，Onboard 三次逐字重放同一 messageId，
   但 envelope 仍携带旧 generation `1`；
4. ControlServer 每次按 session 围栏正确拒绝旧代次消息，记录
   `StaleSessionGenerationException: Message does not belong to the current connection session.`，
   因而无法回到 `DEPARTURE_SAFETY_NOT_READY` 的安全稳定终态；
5. SQLite 中该 `RecoveryStateReport` 仍只有一行 inbox，说明没有重复接受或业务
   副作用；当前 session 已推进至 generation `4`，但停在
   `RECOVERY_REQUIRED / HANDSHAKE_INCOMPLETE`，Capability/Safety revision 均为空。

## 归责

已接受决策要求：新连接必须用当前 `sessionGeneration` 重新封装待补报消息，
同时保留原 `messageId` 与 payload；旧 generation 的迟到消息必须拒绝。当前
ControlServer 符合该围栏。Onboard 的
`WireToGateSessionClient.ReplayDurableOutgoingAsync` 直接发送 journal 中保存的旧
wire line，且现有 G2 测试还把“跨重连逐字相同 wire”写成断言，因此缺陷归属
OnboardHmi。

根据既定人员边界，本轮 AI 不修改王昆端产品代码或测试；修复要求与回归条件最初
写入王昆现有交接文档并推送至 OnboardHmi
`116a0b33c324159d466f5761dd28583d27c9b1c6`。本目录现为该缺陷红证据的归属仓
权威来源，可重跑入口为
[`scripts/run-staged-g3-recovery-ack-drop.ps1`](../../../scripts/run-staged-g3-recovery-ack-drop.ps1)。
修复前不继续依赖恢复成功的业务 drop/delay/duplicate 向量。

## 证据

- [`run-result.json`](run-result.json)：机器可读红结果，SHA-256
  `319c6394b64c5852f01ed3e9dacbe6f2679b8b27b243b993a459889d93ecb80b`
- [`proxy-events.ndjson`](proxy-events.ndjson)：无 payload/凭据的消息元数据轨迹，
  SHA-256 `eab60c20bfaae76c0201baf0359029408ea5c886ff7ab3c90da9290dea86f119`
- [`runner.ps1`](runner.ps1)：本次最终实际运行器快照，SHA-256
  `0c9fe05a4db3b49d30a0d70fc53b224a8f540a7429fe0422a81f408a84da44a7`
- 本目录内 10 个运行文件（不含本摘要）的排序
  `name<TAB>sha256<TAB>length` 集合 SHA-256：
  `16448eac40af22e342621168aa5c4ad405fa054899a38fb998564a803f2f9c6b`

## 解除阻断的回归条件

1. 旧 journal 中的 pending `RecoveryStateReport` 在新连接上保留原 messageId 与
   payload，但以当前 generation 重新封装并在发送前持久化新的 wire/hash；
2. Fake ControlServer 对所有非 SessionHello 消息校验当前 generation，不再接受
   旧代次 envelope；
3. 首 Ack 丢失后，真实双端重连只接受一次报告，稳定收敛到
   `RECOVERY_REQUIRED / DEPARTURE_SAFETY_NOT_READY`，没有重复 IO 或业务副作用；
4. 修复提交完成本端 G2 后，使用本运行器从全新数据库/journal 重跑该阶段性
   G3；红证据保留，不覆盖。
