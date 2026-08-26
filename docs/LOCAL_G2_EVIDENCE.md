# 本机 WIRE_TO_GATE G2 证据

scripts/run-w2g-g2.ps1 为每次本机验证创建一个不可复用的证据目录：

    .\scripts\run-w2g-g2.ps1

默认输出到 evidence/g2/protocol-v0.1.1/<UTC时间>-<HMI commit>/，包含：

- summary.json：精确协议身份、HMI commit、IS-00/IS-01 向量、命令退出码和外部门禁；
- transcript.ndjson：运行事件顺序；
- journal.ndjson：身份绑定和本机验证结果摘要；
- logs/：protocol G1、Release build、test、format 的原始输出；
- test-results/：dotnet test 结果目录。

脚本会校验 protocol HEAD 和 protocol-v0.1.1 tag 都指向
1531489e42e328f28bfe0c51ed3f8c56e5ce0279，并执行 protocol G1。工作区路径包含
# 时，脚本临时映射一个盘符运行 G1；协议仓库本身不会被修改。

该证据只代表 OnboardHmi 本机 G2。summary.json 会明确保留
controlServerG2=PENDING_EXTERNAL 和 g3=PENDING_JOINT，不能把本机结果当作
ControlServer 或现场联合验收。
