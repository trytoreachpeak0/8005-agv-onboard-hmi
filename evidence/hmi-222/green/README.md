# hmi#222 本机全量 G2

命令（从本票 worktree 跑，协议仓用 `scratch/hmi230-protocol`，`protocol-v2.0.0` 的普通克隆 `8657545`）：

```text
pwsh -File C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#222 pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot C:/Users/szy/Desktop/8005-workspace-v2/scratch/hmi230-protocol
```

- 提交 `4d00e4c0ad22154bb3b774d058bab129a2d7568e`，证据目录 `evidence/g2/protocol-v2.0.0/20261001T163507087Z-4d00e4c0ad22`（被 gitignore），一次通过
- `Status: PASS`
- `SQCD.Agv.UnitTests`：`Passed! - Failed: 0, Passed: 667, Skipped: 0, Total: 667`
- `SQCD.Agv.WireToGateG2Tests`：`Passed! - Failed: 0, Passed: 564, Skipped: 0, Total: 564`
- 出站 schema 校验：13026 行，0 条意外违约；登记在案的故意违约 4 条（本票 1 条：`chargingPolicyDecision` 为 `CHARGE_ELSEWHERE` 的那条应答）。
  本票两条消息：`UnableToChargeFieldConfirmationRequested` 产品发出 42 行、`UnableToChargeFieldConfirmationResult` 假服务端发出 40 行

## `-Slice FP-IS-13 -SkipProtocolG1`（开发态）

`evidence/g2/protocol-v2.0.0/FP-IS-13/20261001T164026366Z-4d00e4c0ad22`：`Status: PASS`，`selectedTestCount` 18、`recordedTestCount` 18，
G2 `Passed: 18`。构成：`CV-AUTOMATIC-CHARGING-CYCLE` 5、`CV-MANUAL-STATION-CLEARANCE` 2、`CV-MANUAL-CHARGING-RETURN` 7 行（6 条测试，
其中一条是两行的 Theory）、`CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION` 4 行（1 条 Fact 加 3 行的 Theory）。

## 其它

- `dotnet format SQCD_8005AGV.sln --verify-no-changes` 退出码 0
- `scripts/check-ui-layout.ps1`：`Status: PASS`
