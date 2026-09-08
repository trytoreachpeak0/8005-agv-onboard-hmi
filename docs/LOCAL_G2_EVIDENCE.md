# 本机 WIRE_TO_GATE G2 证据

scripts/run-w2g-g2.ps1 为每次本机验证创建一个不可复用的证据目录：

    .\scripts\run-w2g-g2.ps1

默认输出到 evidence/g2/protocol-v0.3.0/<UTC时间>-<HMI commit>/，包含：

- summary.json：精确协议身份、HMI commit、IS-00/IS-01 向量、命令退出码和外部门禁；
- transcript.ndjson：运行事件顺序；
- journal.ndjson：身份绑定和本机验证结果摘要；
- logs/：protocol G1、Release build、test、format 的原始输出；
- test-results/：dotnet test 结果目录。

脚本会校验 `protocol-v0.3.0` tag 指向
345c53c58517968192c87c3e7777ed08ddb48726，并确认当前 protocol HEAD 是该 release
的后继提交，再执行 protocol G1。这样协议仓库可以在不可变 release 之后继续增加
不改变 release 身份的 CI/文档提交；release manifest、Schema bundle 和 vectors
哈希仍会逐项校验。工作区路径包含 # 时，脚本临时映射一个盘符运行 G1；协议仓库
本身不会被修改。

该证据只代表 OnboardHmi 本机 G2。summary.json 会明确保留
controlServerG2=PENDING_EXTERNAL 和 g3=PENDING_JOINT，不能把本机结果当作
ControlServer 或现场联合验收。
