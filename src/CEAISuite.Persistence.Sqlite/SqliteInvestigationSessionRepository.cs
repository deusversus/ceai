using System.Globalization;
using System.Text.Json;
using CEAISuite.Domain;
using Microsoft.Data.Sqlite;

namespace CEAISuite.Persistence.Sqlite;

public sealed class SqliteInvestigationSessionRepository(string databasePath) : IInvestigationSessionRepository
{
    private readonly string _databasePath = databasePath;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            CREATE TABLE IF NOT EXISTS investigation_sessions (
                id TEXT PRIMARY KEY,
                process_name TEXT NOT NULL,
                process_id INTEGER NULL,
                created_at_utc TEXT NOT NULL,
                address_entry_count INTEGER NOT NULL,
                scan_session_count INTEGER NOT NULL,
                action_log_count INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO investigation_sessions (
                id,
                process_name,
                process_id,
                created_at_utc,
                address_entry_count,
                scan_session_count,
                action_log_count,
                payload_json
            )
            VALUES (
                $id,
                $process_name,
                $process_id,
                $created_at_utc,
                $address_entry_count,
                $scan_session_count,
                $action_log_count,
                $payload_json
            )
            ON CONFLICT(id) DO UPDATE SET
                process_name = excluded.process_name,
                process_id = excluded.process_id,
                created_at_utc = excluded.created_at_utc,
                address_entry_count = excluded.address_entry_count,
                scan_session_count = excluded.scan_session_count,
                action_log_count = excluded.action_log_count,
                payload_json = excluded.payload_json;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$process_name", session.ProcessName);
        command.Parameters.AddWithValue("$process_id", session.ProcessId.HasValue ? session.ProcessId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$created_at_utc", session.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$address_entry_count", session.AddressEntries.Count);
        command.Parameters.AddWithValue("$scan_session_count", session.ScanSessions.Count);
        command.Parameters.AddWithValue("$action_log_count", session.ActionLog.Count);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(session, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM investigation_sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string payload)
        {
            return null;
        }

        return JsonSerializer.Deserialize<InvestigationSession>(payload, SerializerOptions);
    }

    public async Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                process_name,
                process_id,
                created_at_utc,
                address_entry_count,
                scan_session_count,
                action_log_count
            FROM investigation_sessions
            ORDER BY created_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SavedInvestigationSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new SavedInvestigationSession(
                    reader.GetString(0),
                    reader.GetString(1),
                    await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt32(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
        }

        return results;
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM investigation_sessions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
}
