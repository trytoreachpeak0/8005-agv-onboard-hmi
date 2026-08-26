# WIRE_TO_GATE / Legacy 边界审计

执行：

    .\scripts\audit-w2g-boundary.ps1

审计会检查：

- HMI 源码没有 MesIngest 读取；
- WIRE_TO_GATE 启用时使用 DisabledRuleGateway，旧 RuleGateway 不可旁路；
- 旅程只来自 ControlServer 的 VehicleBusinessState、CurrentStopWorklist 和
  UpcomingStopPlan 快照；
- 扫码输入只能匹配已承诺 worklist，不能在本地发现、排序或绑定 Demand。
