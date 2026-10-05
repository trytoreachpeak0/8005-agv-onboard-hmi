using System.Net;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// An outbox row the server refuses with one of the three <c>MANUAL_REVIEW</c> content-conflict codes
/// (onboard-hmi#254, paired with control-server#478).
/// </summary>
/// <remarks>
/// <para>
/// Since control-server#478 the server answers an inbound message it will not take -- the same messageId with other
/// content, the same business id with other content, the same snapshot revision with other content -- with a
/// <c>ProtocolProblem</c> correlated to it and keeps the connection. The vehicle's handshake replays every
/// unacknowledged outbox row and reads one answer per line; a <c>ProtocolProblem</c> there threw, the handshake closed
/// the connection, and the row stayed unacknowledged, so every reconnect replayed it and ended at the same place.
/// </para>
/// <para>
/// <c>OperationProgress</c> carries the row: nothing in the business layer waits for its acknowledgement or sends it
/// again, so what these tests see is the outbox and the handshake alone.
/// </para>
/// </remarks>
public sealed partial class WireToGateG2Tests
{
    private const string ConflictAttemptId = "25425425-4254-4254-8254-254254254254";

    /// <summary>
    /// The four refusals that give a row up: the three content conflicts the ticket names, and
    /// <c>RECOVERY_SCOPE_MISMATCH</c>, the one other <c>MANUAL_REVIEW</c> code about a message's content that the server's
    /// inbound boundary sends (control-server#479).
    /// </summary>
    public static TheoryData<string> ManualReviewContentConflictCodes =>
    [
        "MESSAGE_ID_CONTENT_CONFLICT",
        "BUSINESS_ID_CONTENT_CONFLICT",
        "SNAPSHOT_REVISION_CONTENT_CONFLICT",
        "RECOVERY_SCOPE_MISMATCH"
    ];

    /// <summary>
    /// Refusal codes that leave a row owed. Those the server's inbound boundary sends whose <c>retryDisposition</c> is not
    /// <c>MANUAL_REVIEW</c> (control-server#479: <c>AFTER_STATE_CHANGE</c>, <c>NEW_MESSAGE_ID</c>, <c>NEVER</c>), and two
    /// <c>MANUAL_REVIEW</c> codes about the session or the vehicle's identity rather than a message's content: given up on
    /// those, a vehicle with a credential problem would give up its whole outbox, results owed to MES among them (review
    /// of PR #258, N5).
    /// </summary>
    public static TheoryData<string> RefusalCodesThatLeaveTheRowOwed =>
    [
        "ACTION_NOT_ALLOWED_IN_STATE",
        "SNAPSHOT_REVISION_REGRESSION",
        "FORCED_RECOVERY_GENERATION_STALE",
        "SLOT_SET_INVALID",
        "CONTENT_HASH_MISMATCH",
        "VEHICLE_CREDENTIAL_INVALID",
        "AGV_ID_MISMATCH"
    ];

    /// <summary>
    /// Any refusal but the four content conflicts keeps today's behaviour: the handshake that replays the row fails on
    /// it, and the row stays owed, so the next handshake replays it again.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusalCodesThatLeaveTheRowOwed))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARowRefusedWithACodeThatIsNotAContentConflictIsStillOwed(string reasonCode)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal) { "OperationProgress" }
        };
        string journalPath = NewJournalPath();
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), journalPath);
        int abandonments = 0;
        client.DurableMessageAbandoned += (_, _) => Interlocked.Increment(ref abandonments);
        await client.ConnectAndRecoverAsync(testToken);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], cancellationToken: testToken));

        server.UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal);
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationProgress"] = reasonCode
        };
        for (int round = 0; round < 2; round++)
        {
            await client.DisconnectAsync();
            InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.ConnectAndRecoverAsync(testToken));
            Assert.Equal(reasonCode, refused.Message);
        }

        Assert.Equal(0, abandonments);
        await using SqliteWireToGateJournal journal = new(journalPath);
        Assert.Single(
            await journal.ReadUnacknowledgedOutgoingAsync(testToken),
            row => row.MessageType == "OperationProgress");
    }

    /// <summary>
    /// A content conflict that names another message is not a refusal of this row: the handshake fails on it as it did
    /// before onboard-hmi#254, and the row stays owed (review of PR #258, S3).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AConflictNamingAnotherMessageDoesNotGiveTheRowUp()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal) { "OperationProgress" }
        };
        string journalPath = NewJournalPath();
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), journalPath);
        int abandonments = 0;
        client.DurableMessageAbandoned += (_, _) => Interlocked.Increment(ref abandonments);
        await client.ConnectAndRecoverAsync(testToken);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], cancellationToken: testToken));

        server.UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal);
        server.ProtocolProblemNamesAnotherMessage = true;
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationProgress"] = "MESSAGE_ID_CONTENT_CONFLICT"
        };
        await client.DisconnectAsync();
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ConnectAndRecoverAsync(testToken));

        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", refused.Message);
        Assert.Equal(0, abandonments);
        await using SqliteWireToGateJournal journal = new(journalPath);
        WireToGateDurableMessage row = Assert.Single(
            await journal.ReadUnacknowledgedOutgoingAsync(testToken),
            item => item.MessageType == "OperationProgress");
        Assert.False(row.Abandoned);
    }

    /// <summary>
    /// The row is given up on file and reported once, with what the operator log needs: its key, type, messageId,
    /// content and the server's code. A send of the same business key afterwards -- a retry of the same progress, the
    /// shape of every resend by key -- is refused here and puts nothing on the wire.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AGivenUpRowIsReportedOnceAndAResendByItsKeyIsRefusedBeforeItGoesOut()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OperationProgress"] = "MESSAGE_ID_CONTENT_CONFLICT"
            }
        };
        string journalPath = NewJournalPath();
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), journalPath);
        List<WireToGateDurableMessageAbandonment> reported = [];
        client.DurableMessageAbandoned += (_, args) =>
        {
            lock (reported)
            {
                reported.Add(args.Value);
            }
        };
        await client.ConnectAndRecoverAsync(testToken);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;

        // Thrown as every refusal is: the caller's handling of a refused send is unchanged.
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], observedAt, testToken));
        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", refused.Message);
        InvalidDataException again = await Assert.ThrowsAsync<InvalidDataException>(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], observedAt, testToken));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", again.Message);

        // And under a new session: refused before it is rebound, so the row stays word for word what was refused
        // (review of PR #258, N1).
        await client.DisconnectAsync();
        await client.ConnectAndRecoverAsync(testToken);
        InvalidDataException underANewSession = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.SendOperationProgressAsync(ConflictAttemptId, "UNLOCKING", [1], [], observedAt, testToken));
        Assert.Equal("DURABLE_MESSAGE_ABANDONED", underANewSession.Message);

        var sent = server.ReceivedEnvelopes.Single(item => item.MessageType == "OperationProgress");
        WireToGateDurableMessageAbandonment abandonment = Assert.Single(reported);
        await using SqliteWireToGateJournal journal = new(journalPath);
        WireToGateDurableMessage row = Assert.IsType<WireToGateDurableMessage>(
            await journal.ReadOutgoingByMessageIdAsync(sent.MessageId, testToken));
        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", row.AbandonedReasonCode);
        Assert.False(row.Acknowledged);
        Assert.Equal(
            new WireToGateDurableMessageAbandonment(
                row.DeduplicationKey,
                "OperationProgress",
                sent.MessageId,
                row.ContentSha256,
                "MESSAGE_ID_CONTENT_CONFLICT",
                null,
                row.WireLine),
            abandonment);
        Assert.Equal(sent.WireLine.TrimEnd('\r', '\n'), row.WireLine.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// A row left unacknowledged by its own session is refused as a content conflict when the next handshake replays it:
    /// the handshake goes on to READY, and no later handshake replays it again.
    /// </summary>
    [Theory]
    [MemberData(nameof(ManualReviewContentConflictCodes))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARowRefusedAsAContentConflictInTheHandshakeIsGivenUpAndTheSessionComesUpReady(string reasonCode)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal) { "OperationProgress" }
        };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        // Left unacknowledged on the first session: the send times out, the row stays on file.
        await Assert.ThrowsAnyAsync<TimeoutException>(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], cancellationToken: testToken));
        string rowMessageId = server.ReceivedEnvelopes.Single(item => item.MessageType == "OperationProgress").MessageId;

        server.UnansweredMessageTypes = new HashSet<string>(StringComparer.Ordinal);
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperationProgress"] = reasonCode
        };
        await client.DisconnectAsync();
        Exception? handshakeFailure = await Record.ExceptionAsync(() => client.ConnectAndRecoverAsync(testToken));
        Assert.True(
            handshakeFailure is null,
            $"the handshake failed: {handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}");
        Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);

        // No loop: two more reconnects, neither replays the refused row.
        for (int round = 0; round < 2; round++)
        {
            await client.DisconnectAsync();
            await client.ConnectAndRecoverAsync(testToken);
            Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        }

        Assert.Equal(
            2,
            server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationProgress" && item.MessageId == rowMessageId));
    }

    /// <summary>
    /// A row refused as a content conflict on the session that sent it is not replayed by the next handshake, which comes
    /// up READY.
    /// </summary>
    [Theory]
    [MemberData(nameof(ManualReviewContentConflictCodes))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ARowRefusedAsAContentConflictMidSessionIsNotReplayedByTheNextHandshake(string reasonCode)
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OperationProgress"] = reasonCode
            }
        };
        await using WireToGateSessionClient client = CreateClient(server, new FakeIoModuleClient(), NewJournalPath());
        await client.ConnectAndRecoverAsync(testToken);

        await Record.ExceptionAsync(() => client.SendOperationProgressAsync(
            ConflictAttemptId, "UNLOCKING", [1], [], cancellationToken: testToken));
        string rowMessageId = server.ReceivedEnvelopes.Single(item => item.MessageType == "OperationProgress").MessageId;

        for (int round = 0; round < 3; round++)
        {
            await client.DisconnectAsync();
            Exception? handshakeFailure = await Record.ExceptionAsync(() => client.ConnectAndRecoverAsync(testToken));
            Assert.True(
                handshakeFailure is null,
                $"handshake {round + 2} failed: {handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}");
            Assert.Equal(WireToGateSessionReadiness.Ready, client.Current.Readiness);
        }

        Assert.Equal(
            1,
            server.ReceivedEnvelopes.Count(item => item.MessageType == "OperationProgress" && item.MessageId == rowMessageId));
    }
}
