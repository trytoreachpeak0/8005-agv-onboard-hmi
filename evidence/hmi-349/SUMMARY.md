# hmi#349 证据：待送点取货停靠的「出发」按钮

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/349
PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/350
协议：`4.0.0` 候选 `4d67655c`（`8005-agv-protocol#13` CP-0012、`#15` CP-0013，`SUPERSEDING_CANDIDATE`），
manifest `38a5d2d5…`。证据所在提交：`664a554`。

原始输出精简后入库，只留判定行与失败原因；完整 G2 证据目录在本机
`F:/8005-scratch/g2-hmi349/protocol-v4.0.0/20261014T164129193Z-664a554d4e48`（未入库，体积大）。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `01-red-four-new-tests-at-4c645e3.txt` | 第 6 轮返工新增的 4 条 G2 用例放到返工前的 `4c645e3` 上跑 | 4/4 红，原因各自对应：协议问题未结算为拒绝、受理后按钮仍开着、下一趟同站仍显示旧结果、同会话丢确认不补发 |
| `02-green-unit-tests.txt` | `664a554` 单元测试全量 | 1205 通过 / 0 失败 |
| `03-green-g2-targeted.txt` | `664a554` 出发相关 G2 定向 | 27 通过 / 0 失败 |
| `04-p3-mutation-*.txt` | 删掉 `WireToGateCurrentStopDepartureGate.Judge` 里「仓位操作进行中」与「更正或取消未结」两条判定 | 各被对应单元测试抓住（各 1 红） |
| `05-format-verify.txt` | `dotnet format --verify-no-changes` | exit 0 |
| `06-full-g2-*` | `scripts/run-w2g-g2.ps1` 全量（G1 + 单元 + G2 + 线上报文 schema 核对） | `PASS`：单元 1205/0，G2 957/0，schema 核对 24034 行 0 违规 |
| `07-ci-real-rig-38675942172.txt` | CI 真装置 `real-onboard-compensate-then-reconnect` ×1（control-server `c291b78b`＝cs#713 v4 分支，onboard `92481234`，simulator `fb5f7c59`） | `PASS`，83 秒，两端都是 v4 |

变异只改被变异的那一个文件，每轮之后还原并核对工作树干净。
