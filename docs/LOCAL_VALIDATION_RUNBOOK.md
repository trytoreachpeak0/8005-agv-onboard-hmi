# 本机验证运行手册

一键执行目前不依赖现场的验证：

    .\scripts\run-local-validation.ps1

它依次运行：

1. protocol v0.1.1 G1、HMI Release build/test/format，并生成独立 G2 证据；
2. slots-simulator 核心 18 项和 HTTP/Modbus 自动化 14 项；
3. W2G/Legacy 边界静态审计；
4. UI 布局静态审计。

真实 ControlServer 业务、车辆停稳信号、现场明文网络和硬件动作不会被这个脚本
伪造或标记为通过。
