using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed record WireToGateHmiBanner(
    string StateText,
    string Guidance,
    bool HasWarning,
    bool HasError);

public static class WireToGateHmiPresentation
{
    public static WireToGateHmiBanner Create(
        WireToGateSessionSnapshot session,
        WireToGateHmiOperationSnapshot? operation,
        bool canSubmit,
        bool loadCancellationPending = false,
        bool sublotRejected = false,
        bool entryPausedUntilStopped = false)
    {
        if (operation is { Stage: WireToGateHmiOperationStage.RecoveryRequired })
        {
            return new WireToGateHmiBanner(
                "需要恢复",
                operation.Guidance,
                HasWarning: true,
                HasError: false);
        }

        if (operation is not null
            && operation.Stage is not WireToGateHmiOperationStage.Completed)
        {
            return new WireToGateHmiBanner(
                operation.Stage == WireToGateHmiOperationStage.Reporting
                    ? "结果上报中"
                    : "仓位操作中",
                operation.Guidance,
                HasWarning: false,
                HasError: false);
        }

        return session.Readiness switch
        {
            WireToGateSessionReadiness.Disconnected => new(
                "连接中",
                "正在等待仓门控制设备和上层会话连接…",
                HasWarning: false,
                HasError: false),
            WireToGateSessionReadiness.Recovering => new(
                "恢复中",
                "正在恢复上层会话和持久化消息，请勿操作仓门。",
                HasWarning: false,
                HasError: false),
            WireToGateSessionReadiness.RecoveryRequired => new(
                "需要恢复",
                RecoveryGuidance(session.ReasonCodes),
                HasWarning: true,
                HasError: false),
            // A cancellation before any sublot is out: the server starts no load meanwhile, so
            // scanning is paused and the operator is told why rather than invited to scan.
            WireToGateSessionReadiness.Ready when loadCancellationPending => new(
                "取消中",
                "正在取消本站装货，等待服务端结果；取消结束前暂停扫码。",
                HasWarning: true,
                HasError: false),
            // An entry request is open but the vehicle is not stopped, so scanning waits (onboard-hmi#177).
            // Ahead of the rejection row, whose next step turns on canSubmit: with the entry paused rather
            // than withdrawn it would tell the operator the request is gone. And ahead of the plain Ready
            // row for the same reason -- "waiting for a business instruction" reads as if there were none.
            WireToGateSessionReadiness.Ready when entryPausedUntilStopped => new(
                "暂停扫码",
                "车辆尚未停稳，停稳后可继续扫码。",
                HasWarning: true,
                HasError: false),
            // The server refused the last entry (onboard-hmi#77). Its reason has a line of its own;
            // this row says what to do next, which turns on whether the entry request was kept.
            WireToGateSessionReadiness.Ready when sublotRejected => new(
                "子批被拒收",
                WireToGateSublotRejectionText.NextStep(canSubmit),
                HasWarning: true,
                HasError: false),
            WireToGateSessionReadiness.Ready when canSubmit => new(
                "可扫码",
                "已到站，请扫描物料条码。",
                HasWarning: false,
                HasError: false),
            WireToGateSessionReadiness.Ready => new(
                "就绪",
                "仓门控制和上层会话均已就绪，等待业务指令。",
                HasWarning: false,
                HasError: false),
            _ => new(
                "连接中",
                "正在建立上层会话…",
                HasWarning: false,
                HasError: false)
        };
    }

    private static string RecoveryGuidance(IReadOnlyList<string> reasonCodes)
    {
        if (reasonCodes.Count == 0)
        {
            return "上层会话要求恢复，请保持车辆停稳并等待安全条件恢复。";
        }

        string reasons = string.Join("、", reasonCodes.Distinct(StringComparer.Ordinal));
        return $"上层会话要求恢复：{reasons}。请保持车辆停稳，禁止重复操作仓门。";
    }
}
