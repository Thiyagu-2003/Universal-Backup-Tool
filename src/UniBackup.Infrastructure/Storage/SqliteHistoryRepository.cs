using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class SqliteHistoryRepository
{
    private readonly string _connectionString;
    private readonly string _dbFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SqliteHistoryRepository(string dbFilePath)
    {
        _dbFilePath = dbFilePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await InitializeAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                job_name TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                duration_seconds REAL NOT NULL,
                status TEXT NOT NULL,
                trigger TEXT,
                archive_size_bytes INTEGER NOT NULL,
                error_message TEXT,
                kind TEXT NOT NULL DEFAULT 'folder',
                log_file TEXT,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sql_database_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                database_name TEXT NOT NULL,
                backup_type TEXT NOT NULL,
                file_name TEXT,
                size_bytes INTEGER NOT NULL,
                status TEXT NOT NULL,
                message TEXT,
                uploaded INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (run_id) REFERENCES runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS destination_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                destination TEXT NOT NULL,
                status TEXT NOT NULL,
                bytes_uploaded INTEGER NOT NULL,
                duration_seconds REAL NOT NULL,
                target_location TEXT,
                error_message TEXT,
                FOREIGN KEY (run_id) REFERENCES runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS job_run_state (
                job_id TEXT PRIMARY KEY,
                last_full_time TEXT,
                last_full_file TEXT,
                last_diff_time TEXT,
                last_diff_file TEXT,
                last_log_time TEXT,
                last_log_file TEXT,
                last_run_time TEXT,
                last_run_status TEXT,
                retry_count INTEGER DEFAULT 0,
                next_retry_time TEXT,
                uploaded_bases_json TEXT
            );

            CREATE TABLE IF NOT EXISTS recovery_operations (
                operation_id TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                source_backup TEXT NOT NULL,
                target_name TEXT NOT NULL,
                recovery_type TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT,
                duration_seconds REAL NOT NULL,
                status TEXT NOT NULL,
                error_message TEXT,
                log_details TEXT,
                items_restored INTEGER NOT NULL DEFAULT 0,
                bytes_restored INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_runs_job_id ON runs(job_id);
            CREATE INDEX IF NOT EXISTS idx_runs_timestamp ON runs(timestamp);
            CREATE INDEX IF NOT EXISTS idx_sql_db_runs_run_id ON sql_database_runs(run_id);
            CREATE INDEX IF NOT EXISTS idx_dest_runs_run_id ON destination_runs(run_id);
            CREATE INDEX IF NOT EXISTS idx_recovery_start_time ON recovery_operations(start_time);
            CREATE INDEX IF NOT EXISTS idx_recovery_type ON recovery_operations(recovery_type);
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordRunAsync(RunHistoryRecord record)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            var runSql = @"
                INSERT OR REPLACE INTO runs (
                    run_id, job_id, job_name, timestamp, duration_seconds,
                    status, trigger, archive_size_bytes, error_message, kind, log_file, created_at
                ) VALUES (
                    @run_id, @job_id, @job_name, @timestamp, @duration_seconds,
                    @status, @trigger, @archive_size_bytes, @error_message, @kind, @log_file, @created_at
                );
            ";
            await using (var cmd = new SqliteCommand(runSql, conn, (SqliteTransaction)tx))
            {
                cmd.Parameters.AddWithValue("@run_id", record.RunId);
                cmd.Parameters.AddWithValue("@job_id", record.JobId);
                cmd.Parameters.AddWithValue("@job_name", record.JobName);
                cmd.Parameters.AddWithValue("@timestamp", record.Timestamp.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@duration_seconds", record.DurationSeconds);
                cmd.Parameters.AddWithValue("@status", record.Status);
                cmd.Parameters.AddWithValue("@trigger", (object?)record.Trigger ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@archive_size_bytes", record.ArchiveSizeBytes);
                cmd.Parameters.AddWithValue("@error_message", (object?)record.ErrorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@kind", string.IsNullOrEmpty(record.Kind) ? "folder" : record.Kind);
                cmd.Parameters.AddWithValue("@log_file", (object?)record.LogFile ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync();
            }

            var delSql = "DELETE FROM sql_database_runs WHERE run_id = @run_id; DELETE FROM destination_runs WHERE run_id = @run_id;";
            await using (var delCmd = new SqliteCommand(delSql, conn, (SqliteTransaction)tx))
            {
                delCmd.Parameters.AddWithValue("@run_id", record.RunId);
                await delCmd.ExecuteNonQueryAsync();
            }

            foreach (var db in record.SqlDatabases)
            {
                var sqlDb = @"
                    INSERT INTO sql_database_runs (
                        run_id, database_name, backup_type, file_name, size_bytes, status, message, uploaded
                    ) VALUES (
                        @run_id, @database_name, @backup_type, @file_name, @size_bytes, @status, @message, @uploaded
                    );
                ";
                await using var cmd = new SqliteCommand(sqlDb, conn, (SqliteTransaction)tx);
                cmd.Parameters.AddWithValue("@run_id", record.RunId);
                cmd.Parameters.AddWithValue("@database_name", db.Database);
                cmd.Parameters.AddWithValue("@backup_type", db.BackupType.ToString());
                cmd.Parameters.AddWithValue("@file_name", (object?)db.FileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@size_bytes", db.SizeBytes);
                cmd.Parameters.AddWithValue("@status", db.Status);
                cmd.Parameters.AddWithValue("@message", (object?)db.Message ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@uploaded", db.Uploaded ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var d in record.Destinations)
            {
                var sqlDest = @"
                    INSERT INTO destination_runs (
                        run_id, destination, status, bytes_uploaded, duration_seconds, target_location, error_message
                    ) VALUES (
                        @run_id, @destination, @status, @bytes_uploaded, @duration_seconds, @target_location, @error_message
                    );
                ";
                await using var cmd = new SqliteCommand(sqlDest, conn, (SqliteTransaction)tx);
                cmd.Parameters.AddWithValue("@run_id", record.RunId);
                cmd.Parameters.AddWithValue("@destination", d.Destination);
                cmd.Parameters.AddWithValue("@status", d.Status);
                cmd.Parameters.AddWithValue("@bytes_uploaded", d.BytesUploaded);
                cmd.Parameters.AddWithValue("@duration_seconds", d.DurationSeconds);
                cmd.Parameters.AddWithValue("@target_location", (object?)d.TargetLocation ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@error_message", (object?)d.ErrorMessage ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            var state = await GetRunStateInternalAsync(conn, (SqliteTransaction)tx, record.JobId) ?? new JobRunState { JobId = record.JobId };
            state.LastRunTime = record.Timestamp;
            state.LastRunStatus = record.Status;

            if (record.Status == "SUCCESS" || record.Status == "PARTIAL")
            {
                state.RetryCount = 0;
                state.NextRetryTime = null;
            }

            foreach (var db in record.SqlDatabases)
            {
                switch (db.BackupType)
                {
                    case SqlBackupType.Full:
                        state.LastFullTime = record.Timestamp;
                        state.LastFullFile = db.FileName;
                        if (db.Uploaded && !string.IsNullOrEmpty(db.FileName))
                        {
                            state.UploadedBases[db.Database] = db.FileName;
                        }
                        break;
                    case SqlBackupType.Diff:
                        state.LastDiffTime = record.Timestamp;
                        state.LastDiffFile = db.FileName;
                        break;
                    case SqlBackupType.Log:
                        state.LastLogTime = record.Timestamp;
                        state.LastLogFile = db.FileName;
                        break;
                }
            }

            await SaveRunStateInternalAsync(conn, (SqliteTransaction)tx, state);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<RunHistoryRecord>> GetRunsAsync(
        int limit = 1000,
        string? jobId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(jobId)) conditions.Add("job_id = @job_id");
        if (fromDate.HasValue) conditions.Add("timestamp >= @from_date");
        if (toDate.HasValue) conditions.Add("timestamp <= @to_date");

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"SELECT run_id, job_id, job_name, timestamp, duration_seconds, status, trigger, archive_size_bytes, error_message, kind, log_file FROM runs {whereClause} ORDER BY timestamp DESC LIMIT @limit;";

        var runs = new List<RunHistoryRecord>();
        await using (var cmd = new SqliteCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@limit", limit);
            if (!string.IsNullOrEmpty(jobId))
            {
                cmd.Parameters.AddWithValue("@job_id", jobId);
            }
            if (fromDate.HasValue)
            {
                cmd.Parameters.AddWithValue("@from_date", fromDate.Value.ToString("o", CultureInfo.InvariantCulture));
            }
            if (toDate.HasValue)
            {
                cmd.Parameters.AddWithValue("@to_date", toDate.Value.ToString("o", CultureInfo.InvariantCulture));
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                runs.Add(new RunHistoryRecord
                {
                    RunId = reader.GetString(0),
                    JobId = reader.GetString(1),
                    JobName = reader.GetString(2),
                    Timestamp = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DurationSeconds = reader.GetDouble(4),
                    Status = reader.GetString(5),
                    Trigger = reader.IsDBNull(6) ? "Schedule" : reader.GetString(6),
                    ArchiveSizeBytes = reader.GetInt64(7),
                    ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Kind = reader.GetString(9),
                    LogFile = reader.IsDBNull(10) ? null : reader.GetString(10),
                    SqlDatabases = [],
                    Destinations = []
                });
            }
        }

        foreach (var run in runs)
        {
            var dbSql = "SELECT database_name, backup_type, file_name, size_bytes, status, message, uploaded FROM sql_database_runs WHERE run_id = @run_id;";
            await using (var dbCmd = new SqliteCommand(dbSql, conn))
            {
                dbCmd.Parameters.AddWithValue("@run_id", run.RunId);
                await using var dbReader = await dbCmd.ExecuteReaderAsync();
                while (await dbReader.ReadAsync())
                {
                    Enum.TryParse<SqlBackupType>(dbReader.GetString(1), out var bType);
                    run.SqlDatabases.Add(new SqlDatabaseRunResult
                    {
                        Database = dbReader.GetString(0),
                        BackupType = bType,
                        FileName = dbReader.IsDBNull(2) ? null : dbReader.GetString(2),
                        SizeBytes = dbReader.GetInt64(3),
                        Status = dbReader.GetString(4),
                        Message = dbReader.IsDBNull(5) ? null : dbReader.GetString(5),
                        Uploaded = dbReader.GetInt32(6) == 1
                    });
                }
            }

            var destSql = "SELECT destination, status, bytes_uploaded, duration_seconds, target_location, error_message FROM destination_runs WHERE run_id = @run_id;";
            await using (var destCmd = new SqliteCommand(destSql, conn))
            {
                destCmd.Parameters.AddWithValue("@run_id", run.RunId);
                await using var destReader = await destCmd.ExecuteReaderAsync();
                while (await destReader.ReadAsync())
                {
                    run.Destinations.Add(new DestinationRunResult
                    {
                        Destination = destReader.GetString(0),
                        Status = destReader.GetString(1),
                        BytesUploaded = destReader.GetInt64(2),
                        DurationSeconds = destReader.GetDouble(3),
                        TargetLocation = destReader.IsDBNull(4) ? "" : destReader.GetString(4),
                        ErrorMessage = destReader.IsDBNull(5) ? null : destReader.GetString(5)
                    });
                }
            }
        }

        return runs;
    }

    public async Task<JobRunState?> GetRunStateAsync(string jobId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        return await GetRunStateInternalAsync(conn, null, jobId);
    }

    public async Task SaveRunStateAsync(JobRunState state)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await SaveRunStateInternalAsync(conn, null, state);
    }

    private static async Task<JobRunState?> GetRunStateInternalAsync(SqliteConnection conn, SqliteTransaction? tx, string jobId)
    {
        var sql = @"
            SELECT last_full_time, last_full_file, last_diff_time, last_diff_file,
                   last_log_time, last_log_file, last_run_time, last_run_status,
                   retry_count, next_retry_time, uploaded_bases_json
            FROM job_run_state
            WHERE job_id = @job_id;
        ";

        await using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@job_id", jobId);
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        var state = new JobRunState
        {
            JobId = jobId,
            LastFullTime = reader.IsDBNull(0) ? null : DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            LastFullFile = reader.IsDBNull(1) ? null : reader.GetString(1),
            LastDiffTime = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            LastDiffFile = reader.IsDBNull(3) ? null : reader.GetString(3),
            LastLogTime = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            LastLogFile = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastRunTime = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            LastRunStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
            RetryCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            NextRetryTime = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
        };

        if (!reader.IsDBNull(10))
        {
            try
            {
                state.UploadedBases = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(10), JsonOptions)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        return state;
    }

    private static async Task SaveRunStateInternalAsync(SqliteConnection conn, SqliteTransaction? tx, JobRunState state)
    {
        var sql = @"
            INSERT OR REPLACE INTO job_run_state (
                job_id, last_full_time, last_full_file, last_diff_time, last_diff_file,
                last_log_time, last_log_file, last_run_time, last_run_status,
                retry_count, next_retry_time, uploaded_bases_json
            ) VALUES (
                @job_id, @last_full_time, @last_full_file, @last_diff_time, @last_diff_file,
                @last_log_time, @last_log_file, @last_run_time, @last_run_status,
                @retry_count, @next_retry_time, @uploaded_bases_json
            );
        ";

        await using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@job_id", state.JobId);
        cmd.Parameters.AddWithValue("@last_full_time", state.LastFullTime.HasValue ? state.LastFullTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_full_file", (object?)state.LastFullFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_diff_time", state.LastDiffTime.HasValue ? state.LastDiffTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_diff_file", (object?)state.LastDiffFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_log_time", state.LastLogTime.HasValue ? state.LastLogTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_log_file", (object?)state.LastLogFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_run_time", state.LastRunTime.HasValue ? state.LastRunTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@last_run_status", (object?)state.LastRunStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retry_count", state.RetryCount);
        cmd.Parameters.AddWithValue("@next_retry_time", state.NextRetryTime.HasValue ? state.NextRetryTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@uploaded_bases_json", JsonSerializer.Serialize(state.UploadedBases, JsonOptions));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> ImportHistoryJsonAsync(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return 0;

        var json = await File.ReadAllTextAsync(jsonPath);
        var records = JsonSerializer.Deserialize<List<RunHistoryRecord>>(json, JsonOptions);
        if (records == null || records.Count == 0) return 0;

        int count = 0;
        foreach (var r in records)
        {
            await RecordRunAsync(r);
            count++;
        }

        var dir = Path.GetDirectoryName(jsonPath) ?? "";
        var importedPath = Path.Combine(dir, "history.imported.json");
        try
        {
            if (File.Exists(importedPath)) File.Delete(importedPath);
            File.Move(jsonPath, importedPath);
        }
        catch { }

        return count;
    }

    public async Task<int> PruneHistoryAsync(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o", CultureInfo.InvariantCulture);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "DELETE FROM runs WHERE timestamp < @cutoff;";
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync();
    }

    // =========================================================================
    // RECOVERY OPERATIONS AUDIT TRAIL (§6)
    // =========================================================================

    public async Task RecordRecoveryOperationAsync(RecoveryOperationRecord record)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT OR REPLACE INTO recovery_operations (
                operation_id, provider, source_backup, target_name, recovery_type,
                start_time, end_time, duration_seconds, status, error_message, log_details,
                items_restored, bytes_restored
            ) VALUES (
                @operation_id, @provider, @source_backup, @target_name, @recovery_type,
                @start_time, @end_time, @duration_seconds, @status, @error_message, @log_details,
                @items_restored, @bytes_restored
            );
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@operation_id", record.OperationId);
        cmd.Parameters.AddWithValue("@provider", record.Provider);
        cmd.Parameters.AddWithValue("@source_backup", record.SourceBackup);
        cmd.Parameters.AddWithValue("@target_name", record.TargetName);
        cmd.Parameters.AddWithValue("@recovery_type", record.RecoveryType);
        cmd.Parameters.AddWithValue("@start_time", record.StartTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@end_time", record.EndTime.HasValue ? record.EndTime.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@duration_seconds", record.DurationSeconds);
        cmd.Parameters.AddWithValue("@status", record.Status);
        cmd.Parameters.AddWithValue("@error_message", (object?)record.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@log_details", (object?)record.LogDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@items_restored", record.ItemsRestored);
        cmd.Parameters.AddWithValue("@bytes_restored", record.BytesRestored);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateRecoveryOperationAsync(
        string operationId,
        string status,
        double durationSeconds,
        string? errorMessage = null,
        string? logDetails = null,
        int itemsRestored = 0,
        long bytesRestored = 0)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            UPDATE recovery_operations SET
                status = @status,
                end_time = @end_time,
                duration_seconds = @duration_seconds,
                error_message = @error_message,
                log_details = @log_details,
                items_restored = @items_restored,
                bytes_restored = @bytes_restored
            WHERE operation_id = @operation_id;
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@operation_id", operationId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@end_time", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@duration_seconds", durationSeconds);
        cmd.Parameters.AddWithValue("@error_message", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@log_details", (object?)logDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@items_restored", itemsRestored);
        cmd.Parameters.AddWithValue("@bytes_restored", bytesRestored);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<RecoveryOperationRecord>> GetRecoveryHistoryAsync(int limit = 100, string? typeFilter = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM recovery_operations";
        if (!string.IsNullOrWhiteSpace(typeFilter) && !typeFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            sql += " WHERE recovery_type = @type";
        }
        sql += " ORDER BY start_time DESC LIMIT @limit;";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (!string.IsNullOrWhiteSpace(typeFilter) && !typeFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            cmd.Parameters.AddWithValue("@type", typeFilter);
        }

        var results = new List<RecoveryOperationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var r = new RecoveryOperationRecord
            {
                OperationId = reader.GetString(reader.GetOrdinal("operation_id")),
                Provider = reader.GetString(reader.GetOrdinal("provider")),
                SourceBackup = reader.GetString(reader.GetOrdinal("source_backup")),
                TargetName = reader.GetString(reader.GetOrdinal("target_name")),
                RecoveryType = reader.GetString(reader.GetOrdinal("recovery_type")),
                StartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("start_time")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DurationSeconds = reader.GetDouble(reader.GetOrdinal("duration_seconds")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                ItemsRestored = reader.GetInt32(reader.GetOrdinal("items_restored")),
                BytesRestored = reader.GetInt64(reader.GetOrdinal("bytes_restored"))
            };

            var endIdx = reader.GetOrdinal("end_time");
            if (!reader.IsDBNull(endIdx))
            {
                r.EndTime = DateTime.Parse(reader.GetString(endIdx), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            var errIdx = reader.GetOrdinal("error_message");
            if (!reader.IsDBNull(errIdx)) r.ErrorMessage = reader.GetString(errIdx);

            var logIdx = reader.GetOrdinal("log_details");
            if (!reader.IsDBNull(logIdx)) r.LogDetails = reader.GetString(logIdx);

            results.Add(r);
        }

        return results;
    }
}
