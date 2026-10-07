using System.Collections.Concurrent;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What this vehicle does once the control server refuses one of its durable messages with a <c>MANUAL_REVIEW</c> code
/// and the session client gives the outbox row up (onboard-hmi#254).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every type: the operator is told and the log keeps both sides' facts.</b> The refusal says the server already holds
/// something else under this message's identity. It does not say what -- a <c>ProtocolProblem</c> carries no copy of
/// the content the server kept -- so the log records what this vehicle holds and where the server's copy is to be found.
/// </para>
/// <para>
/// <b>Then, per type:</b>
/// </para>
/// <list type="bullet">
/// <item><c>SafetyStateChanged</c>: the change still pending is dropped and its version skipped, as for one the IO has
/// moved past (onboard-hmi#208), and the session is not dropped for it. Kept, it was resent under the same key and refused
/// before it went out, and the snapshot request the server makes after a refused safety message (control-server#478) went
/// unanswered for as long as it stayed (<see cref="AnswerSafetyStateSnapshotRequestAsync"/>).</item>
/// <item><c>OperationResult</c>: the attempt stays unsettled here, which keeps the recovery entry on offer
/// (<see cref="TrySettleInterruptedOperationAsync"/>).</item>
/// <item>The five recovery results: the forced mechanical recovery goes on to its isolation; the other four keep their
/// vector until a verified maintainer ends it after a manual check (<c>WireToGateBusinessService.ConflictedRecovery.cs</c>,
/// onboard-hmi#254 part 2).</item>
/// <item>The rest wait for nothing.</item>
/// </list>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string SafetyStateChangedKeyPrefix = "safety-state-changed:";

    /// <summary>
    /// The messageIds given up in this process, for the sends that catch the refusal by its exception and must tell a
    /// row given up from one still owed. A restart forgets them, and the outbox still knows.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _abandonedMessageIds = new(StringComparer.Ordinal);

    /// <summary>The <c>SafetyStateChanged</c> given up last, until the safety work drops it as pending.</summary>
    private AbandonedSafetyChange? _abandonedSafetyChange;

    private void OnDurableMessageAbandoned(
        object? sender,
        ValueChangedEventArgs<WireToGateDurableMessageAbandonment> args)
    {
        WireToGateDurableMessageAbandonment abandoned = args.Value;
        _abandonedMessageIds[abandoned.MessageId] = abandoned.ReasonCode;
        ReportAbandonment(abandoned);
        if (abandoned.DeduplicationKey.StartsWith(SafetyStateChangedKeyPrefix, StringComparison.Ordinal)
            && SafetyChangeOf(abandoned.WireLine) is { } change)
        {
            // Set here, on the thread that read the refusal and before it is thrown, so the safety work that sent it
            // finds it in its catch; dropped as pending under the safety gate, never here. No safety pass is asked for
            // here: a refusal mid-session is caught by that work, which asks for one, and one in a handshake is followed
            // by the readiness that asks for one (review of PR #258, N2: an extra request here was never needed).
            Volatile.Write(ref _abandonedSafetyChange, change);
            return;
        }

        if (abandoned.DeduplicationKey.StartsWith(RecoveryVectorResultKeyPrefix, StringComparison.Ordinal) && !_disposed)
        {
            // Not here: inside a handshake this runs on the handshake's thread, which waits for it, and mid-session the
            // send that was refused may still hold the recovery request gate.
            TrackTask(SettleVectorOnFileByItsResultRowAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Tells the operator and the log that a durable message was refused for good and given up.
    /// </summary>
    private void ReportAbandonment(WireToGateDurableMessageAbandonment abandoned)
    {
        (string what, string whereToLook) = DescribeRefusal(abandoned.ReasonCode);
        _logger.Write(
            LogSeverity.Error,
            nameof(WireToGateBusinessService),
            $"服务端以{abandoned.ReasonCode}拒收本车的持久报文，已放弃该发件箱行、不再重发，需人工核对：{what}"
            + $"messageType={abandoned.MessageType}，messageId={abandoned.MessageId}，key={abandoned.DeduplicationKey}，"
            + $"本车内容sha256={abandoned.ContentSha256}，服务端说明={abandoned.ServerDisplayMessage ?? "(无)"}。"
            + $"{whereToLook}本车内容：{abandoned.WireLine.TrimEnd('\r', '\n')}");
        PublishOperatorEvent(
            $"durable-message-abandoned:{abandoned.MessageId}",
            "DURABLE_MESSAGE_ABANDONED",
            $"服务端以 {abandoned.ReasonCode} 拒收本车的 {abandoned.MessageType}（messageId {abandoned.MessageId}）：{what}"
            + "本车已放弃这条报文、不再重发，请联系维护人员核对。 ");
    }

    /// <summary>
    /// What a refusal says happened at the server, and where a person checks it -- per code, because they do not all
    /// mean that the server kept something (review of PR #258, S1). The server's reasons: <c>WireToGateStore</c>
    /// <c>CaptureFirstResponseAsync</c> (messageId), <c>ApplyOperationResultAsync</c> and the recovery coordinator
    /// (business id, including a result whose identity does not match the operation on file, and an unload whose
    /// transport demand already has another completion on file),
    /// <c>ApplyRevision</c> (snapshot revision) and <c>RequireResumeAuthorizationAsync</c> (resume scope, nothing kept).
    /// </summary>
    private static (string What, string WhereToLook) DescribeRefusal(string reasonCode) => reasonCode switch
    {
        "MESSAGE_ID_CONTENT_CONFLICT" => (
            "服务端在同一 messageId 下已收下另一份内容，两端不一致。",
            "服务端收下的那一份不随拒绝回传，请在服务端收件箱按同一messageId查看。"),
        "BUSINESS_ID_CONTENT_CONFLICT" => (
            "服务端对同一业务号已有另一份记录，或这条报文与服务端登记的操作身份不符，两端不一致。",
            "请在服务端按报文里的业务号（需求、仓位操作attempt、恢复工作流或结果号）查看它登记的记录；"
            + "卸货结果还要查同一运输需求（TransportDemandKey）是否已有另一份完成记录。"),
        "SNAPSHOT_REVISION_CONTENT_CONFLICT" => (
            "服务端在同一安全状态版本号下已收下另一份内容。",
            "本车已跳过该版本并改报此刻读数；请在服务端查看本会话的安全状态版本。"),
        "RECOVERY_SCOPE_MISMATCH" => (
            "服务端没有收下这条结果：它超出续作授权的范围（需求、仓位或续作的命令不一致）。",
            "请核对服务端的续作授权与本车实际操作的仓位。"),
        _ => ("服务端拒收了这条报文。", "请在服务端按同一messageId查看拒收原因。")
    };

    /// <summary>
    /// Drops the pending safety change when it is the one given up, skipping its version, and reports whether it did.
    /// Under <see cref="_safetySendGate"/>.
    /// </summary>
    private bool ForgetAbandonedSafetyChange()
    {
        if (Volatile.Read(ref _abandonedSafetyChange) is not { } abandoned
            || _pendingSafetyChange is not { } pending
            || pending.Version != abandoned.Version
            || pending.ObservedAt.ToUniversalTime() != abandoned.ObservedAt)
        {
            return false;
        }

        _nextSafetyStateVersion = Math.Max(_nextSafetyStateVersion, checked(pending.Version + 1));
        _pendingSafetyChange = null;
        // What the server holds is not this change, so the present reading goes out as a change of its own.
        _lastSafetySignature = null;
        Interlocked.CompareExchange(ref _abandonedSafetyChange, null, abandoned);
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"SafetyStateChanged（版本{pending.Version}）被服务端拒收并已放弃；跳过该版本，改报此刻读数，并回应服务端的快照请求。");
        return true;
    }

    /// <summary>Whether the durable message sent under <paramref name="messageId"/> was given up in this process.</summary>
    private bool WasGivenUp(string messageId) => _abandonedMessageIds.ContainsKey(messageId);

    private static AbandonedSafetyChange? SafetyChangeOf(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.TryGetProperty("payload", out JsonElement payload)
            && payload.TryGetProperty("safetyStateVersion", out JsonElement version)
            && version.TryGetInt64(out long safetyStateVersion)
            && payload.TryGetProperty("observedAt", out JsonElement observedAt)
            && observedAt.TryGetDateTimeOffset(out DateTimeOffset at)
                ? new AbandonedSafetyChange(safetyStateVersion, at.ToUniversalTime())
                : null;
    }

    private sealed record AbandonedSafetyChange(long Version, DateTimeOffset ObservedAt);
}
