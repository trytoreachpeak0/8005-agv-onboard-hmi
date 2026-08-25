using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed class SqliteOnboardExecutionJournal : IOnboardExecutionJournal
{
    private readonly string _connectionString;

    public SqliteOnboardExecutionJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(databasePath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Journal database path has no directory."));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS OnboardExecutionJournal (
                SlotOperationAttemptId TEXT NOT NULL PRIMARY KEY,
                MessageId TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                TargetSlotsJson TEXT NOT NULL,
                ExpectedOccupancy TEXT NOT NULL,
                ForcedRecoveryGeneration INTEGER NOT NULL,
                Status TEXT NOT NULL,
                ResultJson TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_OnboardExecutionJournal_MessageId
                ON OnboardExecutionJournal(MessageId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalAttempt?> GetAsync(string attemptId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM OnboardExecutionJournal WHERE SlotOperationAttemptId = $id";
        command.Parameters.AddWithValue("$id", attemptId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task PrepareAsync(JournalAttempt attempt, CancellationToken cancellationToken)
    {
        JournalAttempt? existing = await GetAsync(attempt.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.MessageId != attempt.MessageId || existing.ContentHash != attempt.ContentHash ||
                existing.ForcedRecoveryGeneration != attempt.ForcedRecoveryGeneration ||
                existing.ExpectedOccupancy != attempt.ExpectedOccupancy ||
                !existing.TargetSlots.SequenceEqual(attempt.TargetSlots))
            {
                throw new InvalidDataException("SlotOperationAttemptId replay has different immutable content.");
            }
            return;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO OnboardExecutionJournal
              (SlotOperationAttemptId, MessageId, ContentHash, TargetSlotsJson, ExpectedOccupancy,
               ForcedRecoveryGeneration, Status, ResultJson, UpdatedAt)
            VALUES ($id, $messageId, $hash, $slots, $expected, $generation, $status, $result, $updatedAt)
            """;
        Bind(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(
        string attemptId,
        JournalAttemptStatus status,
        string? resultJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OnboardExecutionJournal
            SET Status = $status, ResultJson = COALESCE($result, ResultJson), UpdatedAt = $updatedAt
            WHERE SlotOperationAttemptId = $id
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", attemptId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Journal attempt '{attemptId}' does not exist.");
        }
    }

    public async Task<IReadOnlyList<JournalAttempt>> ReadUnsettledAsync(CancellationToken cancellationToken)
    {
        List<JournalAttempt> result = [];
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM OnboardExecutionJournal
            WHERE Status <> 'Completed'
            ORDER BY UpdatedAt, SlotOperationAttemptId
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void Bind(SqliteCommand command, JournalAttempt attempt)
    {
        command.Parameters.AddWithValue("$id", attempt.SlotOperationAttemptId);
        command.Parameters.AddWithValue("$messageId", attempt.MessageId);
        command.Parameters.AddWithValue("$hash", attempt.ContentHash);
        command.Parameters.AddWithValue("$slots", JsonSerializer.Serialize(attempt.TargetSlots));
        command.Parameters.AddWithValue("$expected", attempt.ExpectedOccupancy.ToString());
        command.Parameters.AddWithValue("$generation", attempt.ForcedRecoveryGeneration);
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$result", (object?)attempt.ResultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", attempt.UpdatedAt.ToString("O"));
    }

    private static JournalAttempt Read(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("SlotOperationAttemptId")),
        reader.GetString(reader.GetOrdinal("MessageId")),
        reader.GetString(reader.GetOrdinal("ContentHash")),
        JsonSerializer.Deserialize<int[]>(reader.GetString(reader.GetOrdinal("TargetSlotsJson"))) ?? [],
        Enum.Parse<SlotOccupancy>(reader.GetString(reader.GetOrdinal("ExpectedOccupancy"))),
        reader.GetInt64(reader.GetOrdinal("ForcedRecoveryGeneration")),
        Enum.Parse<JournalAttemptStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        reader.IsDBNull(reader.GetOrdinal("ResultJson")) ? null : reader.GetString(reader.GetOrdinal("ResultJson")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")), System.Globalization.CultureInfo.InvariantCulture));
}
