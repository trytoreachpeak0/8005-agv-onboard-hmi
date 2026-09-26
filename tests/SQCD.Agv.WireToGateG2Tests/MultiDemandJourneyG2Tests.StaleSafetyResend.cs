using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 握手没带上的那份安全变化，重连就绪后不能以旧内容、比握手快照大的号写上服务端（8005-agv-onboard-hmi#208）。
/// </summary>
/// <remarks>
/// <para>
/// <b>缺陷：</b>业务服务把没发成的安全变化留在内存里（<c>_pendingSafetyChange</c>），连同它当初记下的内容与号 vN。
/// 握手只补发发件箱里它读到的行，这一份不在其中时，握手快照取已接受的号 v(N−1)、写的是此刻的 IO；会话就绪后
/// 业务服务原样发出 vN。真服务端两种报文走同一个 <c>ApplyRevision</c>，只比号、不比 <c>observedAt</c>
/// （control-server <c>a407ec61</c>，<c>WireToGateStore.cs:3358-3369</c>），于是 vN 的旧内容盖过了快照。
/// </para>
/// <para>
/// <b>危险的方向：</b>记下时安全、之后 IO 变成不安全。服务端把空闲车派去取货（<c>VehicleDynamicFactsCriterion</c>）
/// 与重建自己的单（<c>OwnOrderRebuild</c>）都只读存下的摘要、不做出发前检查，所以此刻存着的「安全」就是它可能据以
/// 让车动起来的东西。车载端下一轮会用当前内容补一份更大的号，所以最终状态是对的——那一段窗口才是缺陷。
/// </para>
/// <para>
/// <b>两格，都是握手没看到这一份：</b><see cref="UnseenByTheHandshake.LandedAfterTheOutboxRead"/> 是 hmi#204 的形状
/// （行在新握手读完发件箱之后才落盘，发送时连接已不在）；<see cref="UnseenByTheHandshake.NeverOnFile"/> 是写发件箱就失败、
/// 从没写进日志，活连接上的失败按老路断开会话。窗口都是构造出来的（<see cref="ReconnectRaceJournal"/>），不靠时序。
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>Why the next handshake did not carry the safety change that did not go out.</summary>
    public enum UnseenByTheHandshake
    {
        /// <summary>Its outbox row landed after the next handshake had read the outbox, and its send found the connection gone.</summary>
        LandedAfterTheOutboxRead,

        /// <summary>Its outbox write failed on a live connection, so it was never on file and the session was disconnected.</summary>
        NeverOnFile
    }

    /// <summary>
    /// 待发的那份记下时出发安全，之后 IO 变成不安全，再重连：新一代里服务端存下的安全状态从头到尾都是不安全，
    /// 服务端也从没据此宣布过 READY；待发那份的号不被另一份内容重用，发件箱里也不留一行旧内容等下一次握手补发。
    /// </summary>
    [Theory]
    [InlineData(UnseenByTheHandshake.LandedAfterTheOutboxRead)]
    [InlineData(UnseenByTheHandshake.NeverOnFile)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public async Task ASafetyChangeTheHandshakeDidNotCarryPutsNoOlderContentOnFileAboveTheSnapshot(
        UnseenByTheHandshake unseen)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // Fresh on every read: the evaluator refuses readings older than IoSnapshotMaxAge, and a stale reading would make
        // the "safe" side of this test unsafe for a reason that has nothing to do with the door.
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        ReconnectRaceJournal race = null!;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                // The real server's revision rule, and its readiness rule on departure safety.
                server.ShareSafetyRevisionAcrossChangeAndSnapshot = true;
                server.RequireSafeSafetyForReadiness = true;
                server.SendReadinessAfterSafetyStateChangedAck = true;
            },
            token,
            io: io,
            wrapJournal: inner => race = new ReconnectRaceJournal(inner));
        Task<WireToGateSessionSnapshot>? reconnect = null;
        try
        {
            await WaitForSafetyReportsToSettleAsync(harness, token);

            // Unsafe to begin with, and on file as such: slot 1's lock feedback stops reading.
            io.SetUnreadable(0);
            io.PublishSnapshot();
            await WaitForLatestOnFileAsync(harness, departureSafe: false, token);
            int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            long acceptedBefore = harness.Session.Current.SafetyStateVersion;

            // The door reads locked again: the vehicle judges itself safe and that change does not go out.
            WireToGateDurableMessage missing;
            if (unseen == UnseenByTheHandshake.LandedAfterTheOutboxRead)
            {
                race.HoldNextSafetyStateChange();
                io.CloseDoor(0, cargo: false);
                io.PublishSnapshot();
                await race.SafetyChangeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

                race.HoldNextHandshakeAfterOutboxRead();
                await harness.Session.Client.DisconnectAsync();
                reconnect = harness.Session.Client.ConnectAndRecoverAsync(token);
                await race.HandshakeHeld.WaitAsync(TimeSpan.FromSeconds(10), token);

                // Unsafe again before anything of the new session is on file.
                io.SetUnreadable(0);
                io.PublishSnapshot();

                race.ReleaseSafetyChange();
                await harness.WaitUntilAsync(
                    () => SafetyReportFailures(harness).Length > 0,
                    "the parked change to find its connection gone",
                    token);
                missing = race.HeldRowAsSaved
                    ?? throw new InvalidOperationException("The parked change was never written.");
                race.ReleaseHandshake();
                await reconnect;
            }
            else
            {
                race.FailNextSave("SafetyStateChanged");
                io.CloseDoor(0, cargo: false);
                io.PublishSnapshot();
                await race.SaveFailed.WaitAsync(TimeSpan.FromSeconds(10), token);
                await harness.WaitUntilAsync(
                    () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Disconnected
                        && SafetyReportFailures(harness).Length > 0,
                    "the business service to disconnect after the failed outbox write",
                    token);
                missing = race.FailedSave
                    ?? throw new InvalidOperationException("No write was failed.");

                // Unsafe again while the vehicle is off line.
                io.SetUnreadable(0);
                io.PublishSnapshot();
                await harness.Session.Client.ConnectAndRecoverAsync(token);
            }

            // The premise, sampled when the change went missing: it said safe, one above what the server had.
            long missingRevision = SafetyStateVersionOf(missing.WireLine);
            Assert.Equal(acceptedBefore + 1, missingRevision);
            Assert.True(
                SafetyOf(missing.WireLine).GetProperty("departureSafe").GetBoolean(),
                $"the change that went missing did not say safe: {missing.WireLine}");

            int secondConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
            Assert.NotEqual(firstConnection, secondConnection);
            // Until the vehicle has said again what it now reads: a change above the handshake snapshot, on file,
            // unsafe, and accepted.
            await harness.WaitUntilAsync(
                () => OnFile(harness, secondConnection) is [{ MessageType: "SafetyStateSnapshot" } snapshot, .., var last]
                    && last.MessageType == "SafetyStateChanged"
                    && last.Revision > snapshot.Revision
                    && !last.DepartureSafe
                    && harness.Session.Current.SafetyStateVersion == last.Revision,
                "a change above the handshake snapshot, saying unsafe, to be on file and accepted",
                token);
            // Room for anything still on its way to land; what is asserted below has already settled.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);

            FakeControlServer.SafetyOnFile[] second = OnFile(harness, secondConnection);
            // The handshake did not carry the missing change -- the case this test is about -- and its snapshot said
            // what the IO said: unsafe, at the revision already accepted.
            FakeControlServer.SafetyOnFile handshake = second[0];
            Assert.Equal("SafetyStateSnapshot", handshake.MessageType);
            Assert.Equal(acceptedBefore, handshake.Revision);
            Assert.False(handshake.DepartureSafe);

            // The defect: the IO read unsafe throughout the second session, and the server had "safe" on file anyway,
            // above the snapshot. Each entry here is a state dispatch could have sent the vehicle off on.
            string[] readyAnnounced = ReadinessSentOn(harness, secondConnection)
                .Where(readiness => readiness == "READY")
                .ToArray();
            FakeControlServer.SafetyOnFile[] safeOnFile = second.Where(entry => entry.DepartureSafe).ToArray();
            Assert.True(
                safeOnFile.Length == 0,
                "the server had departure-safe on file while the IO read unsafe. On file in the second session, in order: "
                + string.Join(" | ", second.Select(entry => $"{entry.MessageType} v{entry.Revision} departureSafe={entry.DepartureSafe}"))
                + $"; SessionReadiness sent on it: {string.Join(", ", ReadinessSentOn(harness, secondConnection))}");
            // And the server did not act on it: it never called the session ready.
            Assert.Empty(readyAnnounced);

            // What the server is left with: the vehicle's present reading. This part held before the fix as well --
            // the business service reported again right after the stale change was accepted -- and it is here so that
            // a fix cannot trade the window for a server left behind.
            FakeControlServer.SafetyOnFile final = second[^1];
            Assert.False(final.DepartureSafe);
            Assert.True(SafetyOfJson(final.SafetyJson).GetProperty("unknownPresent").GetBoolean());

            // One revision, one safety state, across every generation: the missing change's number is not reused for
            // other content (onboard-hmi#206's shape), and the server refused nothing.
            Assert.Empty(harness.Server.SafetyRevisionConflicts);
            string[] reused =
            [
                .. harness.Server.SafetyWrittenOnFile
                    .GroupBy(entry => entry.Revision)
                    .Where(group => group.Select(entry => entry.SafetyJson).Distinct(StringComparer.Ordinal).Count() > 1)
                    .Select(group => $"v{group.Key}: " + string.Join(" / ", group.Select(entry => entry.SafetyJson).Distinct()))
            ];
            Assert.True(reused.Length == 0, "a revision carried two safety states: " + string.Join(" | ", reused));
            // The same rule on the vehicle's own record, which is where a reused number would show: the change that went
            // missing never reached the server, so the wire check above cannot see its number being given to other
            // content. In LandedAfterTheOutboxRead its row is on file; another row under that number with other content
            // is a second safety state the vehicle has put its name to, one replay away from the server.
            string[] reusedInOutbox =
            [
                .. race.SavedRows
                    .Where(row => row.MessageType == "SafetyStateChanged")
                    .GroupBy(row => SafetyStateVersionOf(row.WireLine))
                    .Where(group => group
                        .Select(row => SafetyOf(row.WireLine).GetRawText())
                        .Distinct(StringComparer.Ordinal)
                        .Count() > 1)
                    .Select(group => $"v{group.Key}")
            ];
            Assert.True(
                reusedInOutbox.Length == 0,
                "the outbox holds two safety states under one revision: " + string.Join(", ", reusedInOutbox));

            // Nothing left in the outbox for a later handshake to replay: a replay of the old row would put the same
            // older content on file again, in whatever generation comes next.
            Assert.DoesNotContain(
                await race.ReadUnacknowledgedOutgoingAsync(token),
                row => row.MessageType == "SafetyStateChanged");

            Assert.Equal(secondConnection, harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection));
            Assert.True(harness.Session.Current.Connected);
            Assert.Empty(harness.UiErrors);
        }
        finally
        {
            // Nothing may stay parked, or the harness's disposal waits forever.
            race.ReleaseSafetyChange();
            race.ReleaseHandshake();
        }
    }

    /// <summary>The safety states the server wrote on file on one connection, oldest first.</summary>
    private static FakeControlServer.SafetyOnFile[] OnFile(Harness harness, int connection) =>
        [.. harness.Server.SafetyWrittenOnFile.Where(entry => entry.Connection == connection)];

    /// <summary>The <c>readiness</c> of every SessionReadiness the server sent on one connection, oldest first.</summary>
    private static string[] ReadinessSentOn(Harness harness, int connection) =>
    [
        .. harness.Server.SentEnvelopes
            .Where(envelope => envelope.Connection == connection && envelope.MessageType == "SessionReadiness")
            .Select(envelope =>
            {
                using JsonDocument document = JsonDocument.Parse(envelope.WireLine);
                return document.RootElement.GetProperty("payload").GetProperty("readiness").GetString()!;
            })
    ];

    private static Task WaitForLatestOnFileAsync(Harness harness, bool departureSafe, CancellationToken token) =>
        harness.WaitUntilAsync(
            () => harness.Server.SafetyWrittenOnFile is [.., var last]
                && last.DepartureSafe == departureSafe
                && harness.Session.Current.SafetyStateVersion == last.Revision,
            $"departureSafe={departureSafe} on file and accepted",
            token);

    private static JsonElement SafetyOfJson(string safetyJson)
    {
        using JsonDocument document = JsonDocument.Parse(safetyJson);
        return document.RootElement.Clone();
    }
}
