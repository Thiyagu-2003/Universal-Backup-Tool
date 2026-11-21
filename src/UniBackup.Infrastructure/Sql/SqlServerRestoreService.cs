using System.Data;
using Microsoft.Data.SqlClient;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Sql;

public record SqlBackupFileInfo(string LogicalName, string PhysicalName, char Type);

public class SqlServerRestoreService
{
    private readonly ICredentialProtector _crypto;

    public SqlServerRestoreService(ICredentialProtector crypto)
    {
        _crypto = crypto;
    }

    public async Task<Result<List<SqlBackupFileInfo>>> GetFileListAsync(SqlServerSource source, string backupFilePath, CancellationToken ct = default)
    {
        var files = new List<SqlBackupFileInfo>();
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(source));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path;", conn);
            cmd.Parameters.AddWithValue("@path", backupFilePath);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var logicalName = reader.GetString(reader.GetOrdinal("LogicalName"));
                var physicalName = reader.GetString(reader.GetOrdinal("PhysicalName"));
                var typeStr = reader.GetString(reader.GetOrdinal("Type"));
                files.Add(new SqlBackupFileInfo(logicalName, physicalName, typeStr.Length > 0 ? typeStr[0] : 'D'));
            }

            return Result<List<SqlBackupFileInfo>>.Success(files);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SQL Restore] RESTORE FILELISTONLY failed: {Message}", ex.Message);
            return Result<List<SqlBackupFileInfo>>.Failure("Failed to inspect backup file list: " + ex.Message);
        }
    }

    public async Task<Result<(string DataPath, string LogPath)>> GetInstanceDefaultPathsAsync(SqlServerSource source, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(source));
            await conn.OpenAsync(ct);
            const string sql = "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260));";
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var data = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var log = reader.IsDBNull(1) ? "" : reader.GetString(1);
                return Result<(string DataPath, string LogPath)>.Success((data, log));
            }
            return Result<(string DataPath, string LogPath)>.Failure("Could not query instance default paths.");
        }
        catch (Exception ex)
        {
            return Result<(string DataPath, string LogPath)>.Failure("Query default paths failed: " + ex.Message);
        }
    }

    public async Task<Result> RestoreSequenceAsync(
        SqlServerSource source,
        string targetDatabase,
        IReadOnlyList<string> backupFilePaths,
        DateTime? stopAt = null,
        bool replace = false,
        string? targetDataPath = null,
        string? targetLogPath = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (backupFilePaths == null || backupFilePaths.Count == 0)
        {
            return Result.Failure("No backup files provided for restore sequence.");
        }

        // 1. First file is FULL backup
        var fullFile = backupFilePaths[0];
        var fileListRes = await GetFileListAsync(source, fullFile, ct);
        if (fileListRes.IsFailure) return Result.Failure(fileListRes.Error!);

        var moveClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetDataPath) && !string.IsNullOrWhiteSpace(targetLogPath))
        {
            foreach (var f in fileListRes.Value)
            {
                var ext = f.Type == 'L' ? ".ldf" : (moveClauses.Count == 0 ? ".mdf" : ".ndf");
                var destFolder = f.Type == 'L' ? targetLogPath : targetDataPath;
                var destFile = Path.Combine(destFolder, $"{targetDatabase}_{f.LogicalName}{ext}");
                moveClauses.Add($"MOVE '{f.LogicalName}' TO '{destFile.Replace("'", "''")}'");
            }
        }

        bool hasMoreFiles = backupFilePaths.Count > 1;

        var fullWith = new List<string>();
        if (hasMoreFiles)
        {
            fullWith.Add("NORECOVERY");
        }
        else
        {
            fullWith.Add("RECOVERY");
        }

        if (replace) fullWith.Add("REPLACE");
        fullWith.Add("STATS = 5");
        fullWith.AddRange(moveClauses);

        var fullSql = $"RESTORE DATABASE [{targetDatabase.Replace("]", "]]")}] FROM DISK = @path WITH {string.Join(", ", fullWith)};";
        Log.Information("[SQL Restore] Restoring FULL base into {Db}: {File}", targetDatabase, fullFile);

        var fullResult = await RunSqlAsync(source, fullSql, progress, ct, ("@path", fullFile));
        if (fullResult.IsFailure) return fullResult;

        // 2. Subsequent files (DIFF and LOGs)
        for (int i = 1; i < backupFilePaths.Count; i++)
        {
            var currentFile = backupFilePaths[i];
            bool isLast = (i == backupFilePaths.Count - 1);
            bool isLog = currentFile.EndsWith(".trn", StringComparison.OrdinalIgnoreCase) || currentFile.Contains("_LOG_", StringComparison.OrdinalIgnoreCase);

            var clauses = new List<string>();
            if (!isLast)
            {
                clauses.Add("NORECOVERY");
            }
            else
            {
                clauses.Add("RECOVERY");
                if (stopAt.HasValue && isLog)
                {
                    clauses.Add($"STOPAT = '{stopAt.Value:yyyy-MM-dd HH:mm:ss}'");
                }
            }
            clauses.Add("STATS = 5");

            var verb = isLog ? "RESTORE LOG" : "RESTORE DATABASE";
            var sql = $"{verb} [{targetDatabase.Replace("]", "]]")}] FROM DISK = @path WITH {string.Join(", ", clauses)};";
            Log.Information("[SQL Restore] Applying {Type} ({Idx}/{Total}): {File}", isLog ? "LOG" : "DIFF", i + 1, backupFilePaths.Count, currentFile);

            var res = await RunSqlAsync(source, sql, progress, ct, ("@path", currentFile));
            if (res.IsFailure) return res;
        }

        return Result.Success();
    }

    private async Task<Result> RunSqlAsync(SqlServerSource source, string sql, IProgress<int>? percent, CancellationToken ct,
        params (string Name, string Value)[] parameters)
    {
        var errors = new List<string>();
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(source));
            conn.FireInfoMessageEventOnUserErrors = true;
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError err in e.Errors)
                {
                    if (err.Class > 10) errors.Add($"[{err.Number}] {err.Message}");
                    else if (percent != null && SqlBackupText.TryParsePercent(err.Message, out var p)) percent.Report(p);
                }
            };
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = source.CommandTimeoutSeconds };
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            errors.Add($"[{ex.Number}] {ex.Message}");
        }
        return errors.Count == 0 ? Result.Success() : Result.Failure(string.Join(" | ", errors));
    }

    private string BuildConnectionString(SqlServerSource source)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = source.Server,
            InitialCatalog = "master",
            Encrypt = source.Encrypt,
            TrustServerCertificate = source.TrustServerCertificate || SqlServerBackupService.IsLocalServer(source.Server)
        };

        if (source.UseWindowsAuth)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = source.Username ?? "";
            b.Password = string.IsNullOrEmpty(source.PasswordSealed) ? "" : _crypto.Unseal(source.PasswordSealed);
        }

        return b.ConnectionString;
    }
}
