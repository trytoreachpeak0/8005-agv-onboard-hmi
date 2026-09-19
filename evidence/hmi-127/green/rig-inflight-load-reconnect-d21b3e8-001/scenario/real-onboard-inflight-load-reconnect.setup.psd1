### control-server#189 第一步（只查不改）：在途装货断线重连后两端是否互等。一次性调试场景，不进服务端仓 scripts/。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
#
# 站点期限放到 10 分钟：本场景要在装货进行中、装货刚结束后各观察几十秒，不让「到站期限到了」的处理
# （STATION_TIMEOUT_DOOR_NOT_CLOSED 等）混进要看的两端行为里。期待动作超时门槛保持出厂（6 分钟），全程用不到。
@{
    Onboard                     = 'Real'
    ProtocolFaultProxy          = $true
    StationDepartureWaitTimeout = '00:10:00'
}
