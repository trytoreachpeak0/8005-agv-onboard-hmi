using Microsoft.Data.Sqlite;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The outbox's "given up" mark (onboard-hmi#254): a row the server refused with a <c>MANUAL_REVIEW</c> code is kept as
/// it was and is no longer read as owed.
/// </summary>
public sealed class JournalAbandonedOutboxTests
{
    private const string MessageId = "25425425-4254-4254-8254-000000000001";

    private static readonly WireToGateDurableMessage Row = new(
        "operation-progress:hmi254",
        "OperationProgress",
        MessageId,
        new string('c', 64),
        "{\"messageId\":\"25425425-4254-4254-8254-000000000001\"}\n",
        DateTimeOffset.UnixEpoch,
        false);

    /// <summary>
    /// A given-up row is not owed any more -- neither the handshake's replay nor the stale-row pass reads it, both of them
    /// through <see cref="SqliteWireToGateJournal.ReadUnacknowledgedOutgoingAsync"/> -- and keeps its content, its
    /// messageId and its acknowledgement state. The journal's own hash, reported to the server in every recovery report,
    /// no longer counts it.
    /// </summary>
    [Fact]
    public async Task AGivenUpRowIsNoLongerOwedAndKeepsItsContent()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = new(NewPath());
        await journal.InitializeAsync(token);
        string emptyHash = await journal.ComputeContentSha256Async(token);
        await journal.SaveOutgoingBeforeSendAsync(Row, token);
        Assert.Single(await journal.ReadUnacknowledgedOutgoingAsync(token));

        WireToGateDurableMessage marked = await journal.MarkOutgoingAbandonedAsync(
            MessageId, Row.ContentSha256, "MESSAGE_ID_CONTENT_CONFLICT", token);

        Assert.Equal(Row with { AbandonedReasonCode = "MESSAGE_ID_CONTENT_CONFLICT" }, marked);
        Assert.True(marked.Abandoned);
        Assert.False(marked.Acknowledged);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(token));
        Assert.Equal(marked, await journal.ReadOutgoingByDeduplicationKeyAsync(Row.DeduplicationKey, token));
        Assert.Equal(emptyHash, await journal.ComputeContentSha256Async(token));
    }

    /// <summary>A row given up twice keeps the reason it was first given up for: that is what the operator was told.</summary>
    [Fact]
    public async Task AGivenUpRowKeepsItsFirstReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = new(NewPath());
        await journal.InitializeAsync(token);
        await journal.SaveOutgoingBeforeSendAsync(Row, token);
        await journal.MarkOutgoingAbandonedAsync(MessageId, Row.ContentSha256, "MESSAGE_ID_CONTENT_CONFLICT", token);

        WireToGateDurableMessage again = await journal.MarkOutgoingAbandonedAsync(
            MessageId, Row.ContentSha256, "BUSINESS_ID_CONTENT_CONFLICT", token);

        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", again.AbandonedReasonCode);
    }

    /// <summary>
    /// An acknowledged result the server later refuses for good -- its replay after a recovery report -- is given up and
    /// stays acknowledged: the mark is a separate fact, never a change of acknowledgement.
    /// </summary>
    [Fact]
    public async Task AnAcknowledgedRowGivenUpStaysAcknowledged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = new(NewPath());
        await journal.InitializeAsync(token);
        await journal.SaveOutgoingBeforeSendAsync(Row, token);
        await journal.MarkOutgoingAcknowledgedAsync(MessageId, Row.ContentSha256, token);

        WireToGateDurableMessage marked = await journal.MarkOutgoingAbandonedAsync(
            MessageId, Row.ContentSha256, "BUSINESS_ID_CONTENT_CONFLICT", token);

        Assert.True(marked.Acknowledged);
        Assert.True(marked.Abandoned);
    }

    [Fact]
    public async Task GivingUpARowThatIsNotOnFileIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = new(NewPath());
        await journal.InitializeAsync(token);

        InvalidDataException missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.MarkOutgoingAbandonedAsync(MessageId, Row.ContentSha256, "MESSAGE_ID_CONTENT_CONFLICT", token));

        Assert.Equal("DURABLE_OUTBOX_ROW_MISSING", missing.Message);
    }

    /// <summary>
    /// A journal written before onboard-hmi#254, on a vehicle being upgraded: its rows read before the handshake
    /// initializes it -- the business service reads the outbox from startup on -- and initializing it adds the column
    /// with every row not given up. A second initialization leaves it as it is.
    /// </summary>
    [Fact]
    public async Task AJournalFromBeforeTheMarkIsReadAndThenGainsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = NewPath();
        await CreateJournalWithoutTheMarkAsync(path, token);

        await using SqliteWireToGateJournal journal = new(path);
        WireToGateDurableMessage? beforeInitialize =
            await journal.ReadOutgoingByDeduplicationKeyAsync(Row.DeduplicationKey, token);
        Assert.Equal(Row, beforeInitialize);

        await journal.InitializeAsync(token);
        await journal.InitializeAsync(token);
        Assert.Equal([Row], await journal.ReadUnacknowledgedOutgoingAsync(token));

        await journal.MarkOutgoingAbandonedAsync(MessageId, Row.ContentSha256, "MESSAGE_ID_CONTENT_CONFLICT", token);
        Assert.Empty(await journal.ReadUnacknowledgedOutgoingAsync(token));
    }

    /// <summary>
    /// Going back to a build without the mark: that build's own read of the outbox (<c>WHERE Acknowledged = 0</c>, its
    /// columns by name) still works on a journal that has the column, and reads a given-up row as owed again. This pins
    /// what the PR says a rollback does -- the loop comes back for those rows, nothing fails and no row changes.
    /// </summary>
    [Fact]
    public async Task ABuildWithoutTheMarkReadsAGivenUpRowAsOwed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = NewPath();
        await using (SqliteWireToGateJournal journal = new(path))
        {
            await journal.InitializeAsync(token);
            await journal.SaveOutgoingBeforeSendAsync(Row, token);
            await journal.MarkOutgoingAbandonedAsync(MessageId, Row.ContentSha256, "MESSAGE_ID_CONTENT_CONFLICT", token);
        }

        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync(token);
        await using SqliteCommand oldRead = connection.CreateCommand();
        oldRead.CommandText = """
            SELECT * FROM WireToGateDurableOutbox
            WHERE Acknowledged = 0
            ORDER BY CreatedAt, DeduplicationKey
            """;
        await using SqliteDataReader reader = await oldRead.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(MessageId, reader.GetString(reader.GetOrdinal("MessageId")));
        Assert.Equal(Row.WireLine, reader.GetString(reader.GetOrdinal("WireLine")));
    }

    private static async Task CreateJournalWithoutTheMarkAsync(string path, CancellationToken token)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync(token);
        await using SqliteCommand command = connection.CreateCommand();
        // The outbox as InitializeAsync created it before onboard-hmi#254.
        command.CommandText = """
            CREATE TABLE WireToGateDurableOutbox (
                DeduplicationKey TEXT NOT NULL PRIMARY KEY,
                MessageType TEXT NOT NULL,
                MessageId TEXT NOT NULL UNIQUE,
                ContentSha256 TEXT NOT NULL,
                WireLine TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Acknowledged INTEGER NOT NULL CHECK (Acknowledged IN (0, 1))
            );
            INSERT INTO WireToGateDurableOutbox
                (DeduplicationKey, MessageType, MessageId, ContentSha256, WireLine, CreatedAt, Acknowledged)
            VALUES ($key, $type, $messageId, $hash, $wireLine, $createdAt, 0);
            """;
        command.Parameters.AddWithValue("$key", Row.DeduplicationKey);
        command.Parameters.AddWithValue("$type", Row.MessageType);
        command.Parameters.AddWithValue("$messageId", Row.MessageId);
        command.Parameters.AddWithValue("$hash", Row.ContentSha256);
        command.Parameters.AddWithValue("$wireLine", Row.WireLine);
        command.Parameters.AddWithValue("$createdAt", Row.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }

    private static string NewPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-journal-abandoned", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "journal.db");
    }
}
