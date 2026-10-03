namespace SQCD.Agv.Wpf;

/// <summary>
/// What the operator is told when an administrator's slot fault declaration was applied (REQ-0359,
/// 8005-agv-onboard-hmi#215). The categories are CP-0005's own words for the four the protocol names.
/// </summary>
/// <remarks>
/// A notice, not an entry: the declaration is made on the server, and this screen offers nothing to press about
/// it (REQ-0359 "OnboardHmi 不提供判定入口"). What follows -- the exception recovery session -- goes through the
/// entries the vehicle already has.
/// </remarks>
public static class WireToGateSlotFaultDeclarationText
{
    public static string Applied(int slotNo, string faultCategory) =>
        $"管理员已判定{slotNo}号仓故障：{Category(faultCategory)}，本次操作转人工恢复。";

    public static string Category(string faultCategory) => faultCategory switch
    {
        "LOCK" => "锁",
        "LIGHT_CURTAIN" => "仓内光幕",
        "DOOR_MECHANISM" => "门机构",
        "IO_MODULE" => "IO 模块",
        _ => faultCategory
    };
}
