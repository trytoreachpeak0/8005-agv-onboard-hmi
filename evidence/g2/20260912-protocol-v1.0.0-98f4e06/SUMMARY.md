# `ONBOARD_HMI_G2`：已发布的 `protocol-v1.0.0` 上十片全部 **`PASS`**（`98f4e06`）

**它取代以下两份，作为这十片车载端这一半的现行证据：**

- `FP-IS-00`～`07`：`../20260912-fp-is-00-07-single-owner-ad0e507/`
- `FP-IS-14`／`15`：`../20260912-fp-is-14-15-single-owner-ad0e507/`

那两份绑的是协议候选 `16e2567`，原样保留、一字未改。

## 为什么要重跑

2026-09-12 产品负责人决定：发布批准也可以由他授权的 AI agent 给出，协议治理随之修改。协议候选变为 `9f22db8`，content manifest 变为 `a0e1deed…`。
同日 `9f22db8` 正式发布为 `protocol-v1.0.0`，批准由 AI 给出，授权人是产品负责人。车载端在 `98f4e06` 绑定这个已发布身份，
`ApprovalStatus` 改为 `APPROVED_RELEASE`。

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
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：

- `implementationCommit`：`98f4e061bd518fc2598fb8f11b8e4838c8ab9496`（分支 `w2g/b3-on-v2`）
- `protocolRepositoryCommit`：`9f22db825d52ad86c1d803bd0c1925dcc58d6793`
- `protocolManifestSha256`：`a0e1deed…`
- `protocolApprovalStatus`：**`APPROVED_RELEASE`**

每份 `summary.json` 都记着协议检出里有 `protocol-v1.0.0` 这个 tag，并且它指向 `9f22db8`。
`98f4e06` 给 `run-w2g-g2.ps1` 加了一条检查：`ApprovalStatus` 声称已发布时，tag 必须存在。这是这条检查第一次实际跑到。

十片的测试条数与 `16e2567` 那一轮逐片相同。

## 装置

从 `hmi-b3` 本地 `git clone -b w2g/b3-on-v2` 出干净克隆 `C:\Users\szy\8005-b3\onboard-g2-98f4e06`。`-ProtocolRoot` 指向协议普通克隆
`C:\Users\szy\8005-b3\proto-rel-9f22db8`，该克隆带着 `protocol-v1.0.0` 这个 tag。PATH 上有 `C:\Program Files\nodejs`。跑完两份克隆都干净。
十片由一个 pwsh 脚本文件依次调用，各给一个 `-EvidenceRoot`。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半，服务端十片在服务端仓同日的 `evidence/g2/20260912-protocol-v1.0.0-6b21662/`。
- 发布批准本身不在本证据里复核，见服务端仓同日的 G1 证据。
- 真实车辆停稳信号、Modbus／锁／门／光幕、现场明文网络不在本目录的主张范围内。
