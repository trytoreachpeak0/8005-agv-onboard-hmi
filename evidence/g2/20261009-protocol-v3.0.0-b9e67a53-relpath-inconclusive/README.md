# 无效运行：相对路径的 -EvidenceRoot（不是门禁结论）

control-server#393 出口第 5 步的第一遍 `ONBOARD_HMI_G2`，2026-10-09 23:3x 在本机跑，15 片全部退出码 1，**没有任何一片写出 `gate-result.json`**。

原因是调用方式错了，不是车载端或门禁的问题：传的是相对路径 `-EvidenceRoot evidence/g2/...`。`scripts/run-w2g-g2.ps1` 跑协议 G1 时先 `subst` 一个盘符（`:327`），再 `Push-Location` 到那个盘的根目录（`:334`），于是相对的日志路径被解析到 `X:\` 下面，写日志失败。报错原文：

```
Out-File ... run-w2g-g2.ps1:371 ... Could not find a part of the path 'X:\evidence\g2\20261009-protocol-v3.0.0-b9e67a53\FP-IS-00\protocol-v3.0.0\FP-IS-00\20261009T153839935Z-b9e67a538ba4\logs\protocol-g1.log'.
```

改用绝对路径重跑，15 片全部 PASS，正式证据在同级目录 `20261009-protocol-v3.0.0-b9e67a53/`。脚本不会自己把相对路径转成绝对路径，这一点已报调度，不在本票修。

本目录只留这一遍的 15 份控制台输出，原样保留。
