# hmi#208 第一步：三问的结论

车载端 `w2g/fp-v2-impl@b789c3d4`，服务端只读 `fp/v2-impl@a407ec61`。每条标明是读到的还是推的。

## 第 1 问：路径走不走得到

走得到。〔读到：G2 实测，`01-red-on-test-commit.txt`〕两格：`LandedAfterTheOutboxRead`（hmi#204 的形状，行在新握手读完发件箱之后才落盘）与 `NeverOnFile`（写发件箱就失败，从没写进日志）。

与票面推测不同：服务端**不会**停在旧内容上直到下一次 IO 变化。〔读到：代码与 `03` 探针〕每发成一条安全变化，会话都会 `Publish` 一次状态（`WireToGateSessionClient.SendSafetyStateChangedAsync`），业务服务因此立刻再评估一轮，读数与刚发的旧内容不同就补发一份。窗口是一个 `SafetyStateChanged` 来回。另外〔读到〕真 Modbus IO 客户端每轮轮询（默认 100 ms）都触发 `SnapshotChanged`（`ModbusTcpIoModuleClient.PublishSnapshot`），不只在变化时触发。

## 第 2 问：旧内容能不能是更「安全」的方向

能。〔读到：代码与 G2 实测〕断线期间的触发都在 `CanPublishSafetyRevision` 为假时直接返回；`_pendingSafetyChange` 用 `??=`，从不被替换；能丢掉它的只有「已接受版本 ≥ 它的号」这条对账，而握手没补发它时这条不成立。用例在断线期间特意发了一次 IO 事件，修复前待发项仍原样发出。

## 第 3 问：服务端哪些判断读这个值（`a407ec61`）

- 两种报文走同一个 `ApplyRevision`，只比号，不比 `observedAt`（`WireToGateStore.cs:3358-3369`）。〔子任务读到〕
- 就绪判定 `DecideReadinessAsync` 读 `row.DepartureSafe`。〔子任务读到；G2 里假服务端复现了 READY〕
- 派空闲车去取货：`VehicleDynamicFactsCriterion.SaysTheVehicleMayDepart` 只读存下的摘要，`AcceptAndDispatchToPickupAsync` 直接 `ReconcileOrCreateAsync` 建 RIoT 行驶单，没有出发前检查。〔读到〕
- 自有单重建（`OwnOrderRebuild`）用同一判定，也不做出发前检查。〔子任务读到，未逐行复核〕
- 从站点出发：`AuthorizeMovementAsync` 只认本次出发前检查的结果（版本等于存下的号，且 SAFE），车载端回答时现场读 IO（`WireToGateBusinessService.HandlePreDepartureSafetyCheckAsync`）。〔读到〕不受旧内容影响。
- 到站信任、站点超时判门没关（`SlotDoorsNotProvenClosed`）也读存下的摘要，不直接动车。〔子任务读到〕
- cs#335 仍是 OPEN、没有 PR。〔子任务读到〕若建在存下的摘要上，旧的「安全」会在窗口内盖住它要抓的状态。〔推的〕

对准入线第 1 条：窗口内服务端可能误判车可以离开当前位置，只对空闲车派取货、自有单重建这两条不做出发前检查的路成立，前提是一轮派车恰好落在这一个来回的窗口里；从站点离站不会。〔推的〕
