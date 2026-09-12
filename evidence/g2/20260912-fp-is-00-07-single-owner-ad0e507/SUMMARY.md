# `ONBOARD_HMI_G2`：`FP-IS-00`～`07` 在协议 `16e2567` 上重证（`ad0e507`）

**它取代 `../20260909-fp-is-00-07-v2-recertification/`，作为这八片车载端这一半的现行证据。**那一份八片绑
`w2g/fp-v2-impl@360a405` 与协议 `f6ee75d`，原样保留、一字未改，对它自己的绑定仍然成立。

## 为什么要重跑

产品负责人 2026-09-12 批准把单人签名规则移植到协议候选，并重跑全部门禁。移植后协议候选是 `16e2567`，content manifest 从 `84f984ea…`
变为 `25fd6689…`，车载端在 `e30d421` 跟上了这个身份。票 17 这八片的车载端 G2 绑的是旧 manifest，所以在签 `protocol-v1.0.0`
之前统一重跑一次。

## 结论

| 片 | `status` | selected | recorded | build／test／format | `protocol.g1Status` |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 11 | 11 | 0／0／0 | `PASS` |
| `FP-IS-01` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-02` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-03` | **`PASS`** | 9 | 9 | 0／0／0 | `PASS` |
| `FP-IS-04` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-05` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-06` | **`PASS`** | 8 | 8 | 0／0／0 | `PASS` |
| `FP-IS-07` | **`PASS`** | 19 | 19 | 0／0／0 | `PASS` |

八份身份逐字段一致：`implementationCommit` 为 `ad0e50705cb80a45ff6f1d05dba9250228364c7c`（分支 `w2g/b3-on-v2`），
`protocolRepositoryCommit` 为 `16e2567a7033883f00fc999f7fa08f954dd13a26`，`protocolManifestSha256` 为 `25fd6689…`，
`protocolApprovalStatus` 为 `SUPERSEDING_CANDIDATE`。与同日 `FP-IS-14`／`15` 那份（`../20260912-fp-is-14-15-single-owner-ad0e507/`）
绑的是同一个车载端 commit。

**八片的测试条数与 09-09 那份逐片相同。**`ad0e507` 是 `w2g/fp-v2-impl` 加上批次 3 的改动：批次 3 加的告警求值与会话中途发布测试
带的是 `FP-IS-15` 的 trait，不进这八片。

## 装置

与同日 `FP-IS-14`／`15` 那份用的是同一份干净克隆 `C:\Users\szy\8005-b3\hmi-g2-ad0e507`，`-ProtocolRoot` 指向协议普通克隆
`C:\Users\szy\8005-b3\proto-g1-16e2567`，PATH 上有 `C:\Program Files\nodejs`（`pnpm` 解析到 `pnpm.ps1`，`ad0e507` 修过的那条路径）。
跑完两份克隆都干净。八片各给一个 `-EvidenceRoot`，层级为 `<本目录>/<片>/protocol-v1.0.0/<片>/<运行 id>/`。
由一个 pwsh 脚本文件依次调用，路径不经过任何 shell 字符串转义。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半。服务端八片在服务端仓同日的 `evidence/g2/20260912-fp-is-00-07-single-owner-6369616/`。
- 真实车辆停稳信号、Modbus／锁／门／光幕、现场明文网络都不在本目录的主张范围内。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
