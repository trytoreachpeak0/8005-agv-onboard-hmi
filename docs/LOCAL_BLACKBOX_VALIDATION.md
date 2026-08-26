# 本机 slots-simulator 黑盒验证

执行：

    .\scripts\run-slots-simulator-tests.ps1

脚本运行 slots-simulator 的核心测试和 HTTP/Modbus 自动化测试。它验证的是
HMI 与八仓 IO 模拟器之间的本机物理闭环，不是 ControlServer 业务协议，也不代表
真实 Modbus、锁、门、光幕或车辆信号已经通过现场认证。
