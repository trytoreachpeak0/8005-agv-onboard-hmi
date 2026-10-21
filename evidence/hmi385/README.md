# hmi#385 证据

- `red/console.txt`：测试提交 `879d9dd` 跑在 `2f2ef45` 上（实现之前）。6 条全红；两次现场回放都结成
  `UNKNOWN`／`ACTION_NOT_ALLOWED_IN_STATE`：agv03 装货 3 次开锁后、agv02 卸货 5 次开锁后，与现场一致。
- `green/console.txt`：实现提交上，新 G2 类、改写的三条旧 G2、屏幕快照与架构守卫，50 条全绿。
- `full/unit-tests.txt`：单元测试全量 1291 条全绿。
- `full/g2-full-summary.txt`：全量 G2 1059 条，1051 过、8 败；8 条都是等待超时，跑时本机另有两个重负载在跑。
  `full/g2-rerun-of-failed.txt`：这 8 条单独重跑全部通过。
