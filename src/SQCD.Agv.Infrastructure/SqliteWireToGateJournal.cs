using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class SqliteWireToGateJournal : IWireToGateJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    static SqliteWireToGateJournal()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }

    public SqliteWireToGateJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string expandedPath = Environment.ExpandEnvironmentVariables(databasePath);
        string fullPath = Path.GetFullPath(expandedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("WIRE_TO_GATE journal路径没有父目录。"));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;

                CREATE TABLE IF NOT EXISTS WireToGateJournalMetadata (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    JournalEpoch TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS WireToGateRecoveryState (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    ContentJson TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS WireToGateDurableOutbox (
                    DeduplicationKey TEXT NOT NULL PRIMARY KEY,
                    MessageType TEXT NOT NULL,
                    MessageId TEXT NOT NULL UNIQUE,
                    ContentSha256 TEXT NOT NULL,
                    WireLine TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    Acknowledged INTEGER NOT NULL CHECK (Acknowledged IN (0, 1))
                );

                CREATE TABLE IF NOT EXISTS WireToGateAppliedJourneySnapshots (
                    MessageType TEXT NOT NULL PRIMARY KEY,
                    MessageId TEXT NOT NULL,
                    Revision INTEGER NOT NULL CHECK (Revision >= 0),
                    ContentSha256 TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    AppliedAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand metadataSeed = connection.CreateCommand();
            metadataSeed.CommandText = """
                INSERT OR IGNORE INTO WireToGateJournalMetadata (Id, JournalEpoch)
                VALUES (1, $journalEpoch)
                """;
            metadataSeed.Parameters.AddWithValue("$journalEpoch", Guid.NewGuid().ToString("D"));
            await metadataSeed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT OR IGNORE INTO WireToGateRecoveryState (Id, ContentJson, UpdatedAt)
                VALUES (1, $content, $updatedAt)
                """;
            seed.Parameters.AddWithValue("$content", SerializeRecoveryState(WireToGateRecoveryState.Empty));
            seed.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UnixEpoch.ToString("O"));
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT JournalEpoch FROM WireToGateJournalMetadata WHERE Id = 1";
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not string journalEpoch
                || !Guid.TryParseExact(journalEpoch, "D", out _))
            {
                throw new InvalidDataException("WIRE_TO_GATE journal epoch无效或尚未初始化。");
            }

            return journalEpoch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateRecoveryState> ReadRecoveryStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRecoveryStateCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteRecoveryStateAsync(
        WireToGateRecoveryState state,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRecoveryState(state);
        // One door at a time (REQ-0357, ADR-cross-0061): the active unlock set is the one door that may
        // be standing open. Checked on write only. A journal written before this rule may still carry a
        // wider set, and reading it back must keep working, because recovery treats every slot of that
        // set as a fence and never pulses it again -- refusing the read would strand the vehicle
        // instead of making it safer.
        if (state.ActiveUnlockSlots.Count > 1)
        {
            throw new InvalidDataException("ACTIVE_UNLOCK_SET_MORE_THAN_ONE_SLOT");
        }

        string json = SerializeRecoveryState(state);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE WireToGateRecoveryState
                SET ContentJson = $content, UpdatedAt = $updatedAt
                WHERE Id = 1
                """;
            command.Parameters.AddWithValue("$content", json);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("WIRE_TO_GATE journal尚未初始化。");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
        WireToGateDurableMessage message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDurableMessage(message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            WireToGateDurableMessage? existing = await ReadByDeduplicationKeyAsync(
                connection,
                message.DeduplicationKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.MessageType != message.MessageType
                    || existing.MessageId != message.MessageId
                    || existing.ContentSha256 != message.ContentSha256
                    || existing.WireLine != message.WireLine)
                {
                    throw new InvalidDataException("BUSINESS_ID_CONTENT_CONFLICT");
                }

                return existing;
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WireToGateDurableOutbox
                    (DeduplicationKey, MessageType, MessageId, ContentSha256, WireLine, CreatedAt, Acknowledged)
                VALUES ($key, $type, $messageId, $hash, $wireLine, $createdAt, 0)
                """;
            Bind(command, message);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidDataException("MESSAGE_ID_CONTENT_CONFLICT", exception);
            }

            return message with { Acknowledged = false };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
        WireToGateDurableMessage expected,
        WireToGateDurableMessage replacement,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDurableMessage(expected);
        ValidateDurableMessage(replacement);
        if (!string.Equals(expected.DeduplicationKey, replacement.DeduplicationKey, StringComparison.Ordinal)
            || !string.Equals(expected.MessageType, replacement.MessageType, StringComparison.Ordinal)
            || !string.Equals(expected.MessageId, replacement.MessageId, StringComparison.Ordinal)
            || replacement.Acknowledged)
        {
            throw new InvalidDataException("DURABLE_OUTBOX_REBIND_IDENTITY_CONFLICT");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            WireToGateDurableMessage? current = await ReadByDeduplicationKeyAsync(
                connection,
                expected.DeduplicationKey,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new InvalidDataException("DURABLE_OUTBOX_MISSING");
            }

            if (current.Acknowledged)
            {
                return current;
            }

            if (!SameDurableContent(current, expected))
            {
                throw new InvalidDataException("DURABLE_OUTBOX_CONTENT_MISMATCH");
            }

            if (SameDurableWire(current, replacement))
            {
                return current;
            }

            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE WireToGateDurableOutbox
                SET ContentSha256 = $newHash, WireLine = $newWire
                WHERE DeduplicationKey = $key
                  AND MessageType = $type
                  AND MessageId = $messageId
                  AND ContentSha256 = $oldHash
                  AND WireLine = $oldWire
                  AND Acknowledged = 0
                """;
            command.Parameters.AddWithValue("$newHash", replacement.ContentSha256);
            command.Parameters.AddWithValue("$newWire", replacement.WireLine);
            command.Parameters.AddWithValue("$key", expected.DeduplicationKey);
            command.Parameters.AddWithValue("$type", expected.MessageType);
            command.Parameters.AddWithValue("$messageId", expected.MessageId);
            command.Parameters.AddWithValue("$oldHash", expected.ContentSha256);
            command.Parameters.AddWithValue("$oldWire", expected.WireLine);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("DURABLE_OUTBOX_REBIND_CONFLICT");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replacement with
            {
                CreatedAt = current.CreatedAt,
                Acknowledged = false
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadByDeduplicationKeyAsync(connection, deduplicationKey, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUuid(messageId, nameof(messageId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM WireToGateDurableOutbox WHERE MessageId = $messageId";
            command.Parameters.AddWithValue("$messageId", messageId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkOutgoingAcknowledgedAsync(
        string messageId,
        string acceptedContentSha256,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUuid(messageId, nameof(messageId));
        RequireSha256(acceptedContentSha256, nameof(acceptedContentSha256));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE WireToGateDurableOutbox
                SET Acknowledged = 1
                WHERE MessageId = $messageId AND ContentSha256 = $hash
                """;
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$hash", acceptedContentSha256);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                return;
            }

            await using SqliteCommand already = connection.CreateCommand();
            already.CommandText = """
                SELECT ContentSha256, Acknowledged
                FROM WireToGateDurableOutbox
                WHERE MessageId = $messageId
                """;
            already.Parameters.AddWithValue("$messageId", messageId);
            await using SqliteDataReader reader = await already
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.GetInt32(reader.GetOrdinal("Acknowledged")) == 1
                && string.Equals(
                    reader.GetString(reader.GetOrdinal("ContentSha256")),
                    acceptedContentSha256,
                    StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidDataException("CONTENT_HASH_MISMATCH");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadUnacknowledgedCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            List<WireToGateAppliedJourneySnapshot> snapshots = [];
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT MessageType, MessageId, Revision, ContentSha256, PayloadJson, AppliedAt
                FROM WireToGateAppliedJourneySnapshots
                ORDER BY MessageType
                """;
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshots.Add(ReadAppliedJourneySnapshot(reader));
            }

            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
        WireToGateAppliedJourneySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateAppliedJourneySnapshot(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            WireToGateAppliedJourneySnapshot? existing = await ReadAppliedJourneySnapshotCoreAsync(
                connection,
                snapshot.MessageType,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (snapshot.Revision < existing.Revision)
                {
                    throw new InvalidDataException("SNAPSHOT_REVISION_REGRESSION");
                }

                if (snapshot.Revision == existing.Revision)
                {
                    string existingPayloadSha256 = WireToGateProtocolSerializer
                        .ComputePayloadContentSha256(existing.PayloadJson);
                    string incomingPayloadSha256 = WireToGateProtocolSerializer
                        .ComputePayloadContentSha256(snapshot.PayloadJson);
                    if (!string.Equals(
                        incomingPayloadSha256,
                        existingPayloadSha256,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("SNAPSHOT_REVISION_CONTENT_CONFLICT");
                    }

                    return existing;
                }
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WireToGateAppliedJourneySnapshots
                    (MessageType, MessageId, Revision, ContentSha256, PayloadJson, AppliedAt)
                VALUES ($messageType, $messageId, $revision, $contentSha256, $payloadJson, $appliedAt)
                ON CONFLICT(MessageType) DO UPDATE SET
                    MessageId = excluded.MessageId,
                    Revision = excluded.Revision,
                    ContentSha256 = excluded.ContentSha256,
                    PayloadJson = excluded.PayloadJson,
                    AppliedAt = excluded.AppliedAt
                """;
            command.Parameters.AddWithValue("$messageType", snapshot.MessageType);
            command.Parameters.AddWithValue("$messageId", snapshot.MessageId);
            command.Parameters.AddWithValue("$revision", snapshot.Revision);
            command.Parameters.AddWithValue("$contentSha256", snapshot.ContentSha256);
            command.Parameters.AddWithValue("$payloadJson", snapshot.PayloadJson);
            command.Parameters.AddWithValue("$appliedAt", snapshot.AppliedAt.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            WireToGateRecoveryState state = await ReadRecoveryStateCoreAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<WireToGateDurableMessage> pending = await ReadUnacknowledgedCoreAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            object content = new
            {
                recoveryState = Normalize(state),
                pendingBusinessMessages = pending
                    .Where(item => item.MessageType != "RecoveryStateReport")
                    .OrderBy(item => item.DeduplicationKey, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        item.DeduplicationKey,
                        item.MessageType,
                        item.MessageId,
                        item.ContentSha256
                    })
                    .ToArray()
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(content, SerializerOptions);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<WireToGateRecoveryState> ReadRecoveryStateCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ContentJson FROM WireToGateRecoveryState WHERE Id = 1";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string json)
        {
            throw new InvalidDataException("WIRE_TO_GATE journal尚未初始化。");
        }

        WireToGateRecoveryState state;
        try
        {
            state = JsonSerializer.Deserialize<WireToGateRecoveryState>(json, SerializerOptions)
                ?? throw new InvalidDataException("WIRE_TO_GATE recovery state内容无效。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery state内容无效。", exception);
        }

        ValidateRecoveryState(state);
        return state;
    }

    private static async Task<WireToGateDurableMessage?> ReadByDeduplicationKeyAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WireToGateDurableOutbox WHERE DeduplicationKey = $key";
        command.Parameters.AddWithValue("$key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private static async Task<WireToGateAppliedJourneySnapshot?> ReadAppliedJourneySnapshotCoreAsync(
        SqliteConnection connection,
        string messageType,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MessageType, MessageId, Revision, ContentSha256, PayloadJson, AppliedAt
            FROM WireToGateAppliedJourneySnapshots
            WHERE MessageType = $messageType
            """;
        command.Parameters.AddWithValue("$messageType", messageType);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAppliedJourneySnapshot(reader)
            : null;
    }

    private static async Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        List<WireToGateDurableMessage> messages = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM WireToGateDurableOutbox
            WHERE Acknowledged = 0
            ORDER BY CreatedAt, DeduplicationKey
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    private static WireToGateDurableMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("DeduplicationKey")),
        reader.GetString(reader.GetOrdinal("MessageType")),
        reader.GetString(reader.GetOrdinal("MessageId")),
        reader.GetString(reader.GetOrdinal("ContentSha256")),
        reader.GetString(reader.GetOrdinal("WireLine")),
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("CreatedAt")),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
        reader.GetInt32(reader.GetOrdinal("Acknowledged")) == 1);

    private static WireToGateAppliedJourneySnapshot ReadAppliedJourneySnapshot(
        SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("MessageType")),
        reader.GetString(reader.GetOrdinal("MessageId")),
        reader.GetInt64(reader.GetOrdinal("Revision")),
        reader.GetString(reader.GetOrdinal("ContentSha256")),
        reader.GetString(reader.GetOrdinal("PayloadJson")),
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("AppliedAt")),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind));

    private static bool SameDurableContent(
        WireToGateDurableMessage left,
        WireToGateDurableMessage right) =>
        string.Equals(left.DeduplicationKey, right.DeduplicationKey, StringComparison.Ordinal)
        && string.Equals(left.MessageType, right.MessageType, StringComparison.Ordinal)
        && string.Equals(left.MessageId, right.MessageId, StringComparison.Ordinal)
        && string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
        && string.Equals(left.WireLine, right.WireLine, StringComparison.Ordinal);

    private static bool SameDurableWire(
        WireToGateDurableMessage left,
        WireToGateDurableMessage right) =>
        string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.Ordinal)
        && string.Equals(left.WireLine, right.WireLine, StringComparison.Ordinal);

    private static void Bind(SqliteCommand command, WireToGateDurableMessage message)
    {
        command.Parameters.AddWithValue("$key", message.DeduplicationKey);
        command.Parameters.AddWithValue("$type", message.MessageType);
        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$hash", message.ContentSha256);
        command.Parameters.AddWithValue("$wireLine", message.WireLine);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
    }

    private static string SerializeRecoveryState(WireToGateRecoveryState state) =>
        JsonSerializer.Serialize(Normalize(state), SerializerOptions);

    private static WireToGateRecoveryState Normalize(WireToGateRecoveryState state) => state with
    {
        ActiveUnlockSlots = state.ActiveUnlockSlots.Order().ToArray(),
        CompletedSlots = state.CompletedSlots.Order().ToArray(),
        SlotResults = state.SlotResults
            .OrderBy(item => item.SlotNo)
            .ToArray(),
        PendingResults = state.PendingResults
            .OrderBy(item => item.MessageId, StringComparer.Ordinal)
            .ToArray(),
        RecoveryVector = state.RecoveryVector is { } vector
            ? vector with { Slots = vector.Slots.Order().ToArray() }
            : null,
        LastCompletedLoadOperationContext = state.LastCompletedLoadOperationContext is { } lastLoad
            ? lastLoad with { Slots = lastLoad.Slots.Order().ToArray() }
            : null
    };

    private static void ValidateRecoveryState(WireToGateRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.UnsettledSlotOperationAttemptId is not null)
        {
            RequireUuid(state.UnsettledSlotOperationAttemptId, nameof(state.UnsettledSlotOperationAttemptId));
        }

        if (state.ActiveUnlockSlots is null
            || state.CompletedSlots is null
            || state.SlotResults is null
            || state.PendingResults is null
            || state.ForcedRecoveryGeneration < 0
            || state.ActiveUnlockSlots.Any(slot => slot is < 1 or > 8)
            || state.ActiveUnlockSlots.Distinct().Count() != state.ActiveUnlockSlots.Count
            || state.CompletedSlots.Any(slot => slot is < 1 or > 8)
            || state.CompletedSlots.Distinct().Count() != state.CompletedSlots.Count
            || state.SlotResults.Any(result => result.SlotNo is < 1 or > 8)
            || state.SlotResults.Select(result => result.SlotNo).Distinct().Count() != state.SlotResults.Count)
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery state字段无效。");
        }

        if (state.OperationContext is { } context)
        {
            ValidateOperationContext(context, requireLoad: false);

            if (state.UnsettledSlotOperationAttemptId is not null
                && !string.Equals(
                    state.UnsettledSlotOperationAttemptId,
                    context.SlotOperationAttemptId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("WIRE_TO_GATE recovery state与operation context不一致。");
            }
        }

        if (state.LastCompletedLoadOperationContext is { } lastLoad)
        {
            ValidateOperationContext(lastLoad, requireLoad: true);
        }

        if (state.RecoveryVector is { } vector)
        {
            ValidateRecoveryVectorContext(vector);
        }

        ValidateOptionalUuid(state.ExceptionRecoverySessionId, nameof(state.ExceptionRecoverySessionId));
        ValidateOptionalUuid(state.RecoveryActionId, nameof(state.RecoveryActionId));
        ValidateOptionalUuid(state.RecoverySessionRequestId, nameof(state.RecoverySessionRequestId));
        ValidateOptionalUuid(state.RecoveryActionRequestId, nameof(state.RecoveryActionRequestId));

        if (state.PendingLoadCancellation is { } cancellation)
        {
            RequireUuid(cancellation.CancellationId, nameof(cancellation.CancellationId));
            ValidateOptionalUuid(
                cancellation.SlotOperationAttemptId,
                nameof(cancellation.SlotOperationAttemptId));
            ArgumentException.ThrowIfNullOrWhiteSpace(cancellation.OperatorId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cancellation.OperatorVerificationMethod);
            ArgumentException.ThrowIfNullOrWhiteSpace(cancellation.Reason);
        }

        foreach (WireToGatePendingResult pending in state.PendingResults)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pending.MessageType);
            ArgumentException.ThrowIfNullOrWhiteSpace(pending.BusinessId);
            RequireUuid(pending.MessageId, nameof(pending.MessageId));
            RequireSha256(pending.ContentSha256, nameof(pending.ContentSha256));
        }
    }

    private static void ValidateOptionalUuid(string? value, string name)
    {
        if (value is not null)
        {
            RequireUuid(value, name);
        }
    }

    private static void ValidateOperationContext(
        WireToGateRecoveryOperationContext context,
        bool requireLoad)
    {
        RequireUuid(context.MessageId, nameof(context.MessageId));
        RequireUuid(context.DemandId, nameof(context.DemandId));
        RequireUuid(context.OperationSessionId, nameof(context.OperationSessionId));
        RequireUuid(context.SlotOperationAttemptId, nameof(context.SlotOperationAttemptId));
        RequireSha256(context.CommandContentSha256, nameof(context.CommandContentSha256));
        if (context.CorrelationId is not null)
        {
            RequireUuid(context.CorrelationId, nameof(context.CorrelationId));
        }

        if (context.SessionGeneration < 0
            || context.Slots is null
            || context.Slots.Count is < 1 or > 8
            || context.Slots.Any(slot => slot is < 1 or > 8)
            || context.Slots.Distinct().Count() != context.Slots.Count
            || !context.Slots.SequenceEqual(context.Slots.Order())
            || context.ExpectedBasketCount != context.Slots.Count
            || context.ExpectedOccupied != (context.OperationType == OperationType.Load)
            || requireLoad && context.OperationType != OperationType.Load)
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery operation context字段无效。");
        }
    }

    private static void ValidateRecoveryVectorContext(
        WireToGateRecoveryVectorContext vector)
    {
        if (!WireToGateRecoveryVectorTypes.IsKnown(vector.VectorType))
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery vector类型无效。");
        }

        RequireUuid(vector.PrimaryId, nameof(vector.PrimaryId));
        RequireUuid(vector.DemandId, nameof(vector.DemandId));
        ValidateOptionalUuid(vector.ExceptionRecoverySessionId, nameof(vector.ExceptionRecoverySessionId));
        ValidateOptionalUuid(vector.SlotOperationAttemptId, nameof(vector.SlotOperationAttemptId));
        ValidateOptionalUuid(vector.HandoffId, nameof(vector.HandoffId));
        // Only the cancellation before any sublot is entered carries no slot; every other vector names
        // the slots it puts into a proven state.
        if (vector.Slots is null
            || vector.Slots.Count > 8
            || vector.Slots.Count == 0
                && !WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector)
            || vector.Slots.Any(slot => slot is < 1 or > 8)
            || vector.Slots.Distinct().Count() != vector.Slots.Count
            || !vector.Slots.SequenceEqual(vector.Slots.Order())
            || vector.CommandContentSha256 is not null
                && !IsSha256(vector.CommandContentSha256))
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery vector字段无效。");
        }

        if (vector.VectorType is WireToGateRecoveryVectorTypes.LoadCompensation
                or WireToGateRecoveryVectorTypes.FaultCargoHandoff
                or WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
            && (vector.ExceptionRecoverySessionId is null
                || vector.SlotOperationAttemptId is null)
            || vector.VectorType == WireToGateRecoveryVectorTypes.LoadCorrection
                && vector.SlotOperationAttemptId is null
            || vector.VectorType == WireToGateRecoveryVectorTypes.FaultCargoHandoff
                && vector.HandoffId is null)
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery vector范围无效。");
        }

        // The generation belongs to exactly one vector.  Carrying it on any other would mean a
        // fence had been recorded for a vector nothing fences, and its absence on this one would
        // leave the result with no generation to report.
        if (vector.ForcedRecoveryGeneration is { } generation
            ? vector.VectorType != WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
                || generation < 0
            : vector.VectorType == WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery
                && vector.CommandContentSha256 is not null)
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery vector代际无效。");
        }

        if (vector.OperatorId is not null
            && (string.IsNullOrWhiteSpace(vector.OperatorId)
                || string.IsNullOrWhiteSpace(vector.OperatorVerificationMethod)
                || vector.OperatorVerifiedAt is null))
        {
            throw new InvalidDataException("WIRE_TO_GATE recovery vector操作员字段无效。");
        }
    }

    private static void ValidateDurableMessage(WireToGateDurableMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.DeduplicationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.WireLine);
        RequireUuid(message.MessageId, nameof(message.MessageId));
        RequireSha256(message.ContentSha256, nameof(message.ContentSha256));
    }

    private static void ValidateAppliedJourneySnapshot(WireToGateAppliedJourneySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.MessageType);
        RequireUuid(snapshot.MessageId, nameof(snapshot.MessageId));
        if (snapshot.Revision < 0)
        {
            throw new InvalidDataException("旅程快照revision不能为负数。");
        }

        RequireSha256(snapshot.ContentSha256, nameof(snapshot.ContentSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.PayloadJson);
    }

    private static void RequireUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidDataException($"{name}必须是标准UUID。");
        }
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{name}必须是64位SHA-256十六进制字符串。");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
