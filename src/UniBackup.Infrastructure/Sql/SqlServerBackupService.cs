using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Microsoft.Data.SqlClient;
using Serilog;

namespace UniBackup.Infrastructure.Sql;

public class SqlServerBackupService(ICredentialProtector crypto)
{
    private const int CompressionNotSupported = 1844;

    public string BuildConnectionString(SqlServerSource source)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = source.Server,
            InitialCatalog = "master",
            IntegratedSecurity = source.UseWindowsAuth,
            Encrypt = source.Encrypt,
            // A local instance usually has a self-signed certificate; the traffic never leaves the machine.
            TrustServerCertificate = source.TrustServerCertificate || IsLocalServer(source.Server),
            ApplicationName = "UniBackup",
            ConnectTimeout = 15
        };
        if (!source.UseWindowsAuth)
        {
            b.UserID = source.Username ?? "";
            b.Password = string.IsNullOrEmpty(source.PasswordSealed) ? "" : crypto.Unseal(source.PasswordSealed);
        }
        return b.ConnectionString;
    }

    public static bool IsLocalServer(string server)
    {
        var host = server.Split('\\', ',')[0].Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) host = host[prefix.Length..];
        return host is "." or "(local)" or "127.0.0.1"
               || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Result<List<SqlDatabaseInfo>>> ListDatabasesAsync(SqlServerSource source, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(source, ct);
            await using var cmd = new SqlCommand(SqlBackupText.ListDatabases, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<SqlDatabaseInfo>();
            while (await reader.ReadAsync(ct))
                list.Add(new SqlDatabaseInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4) <= 4));
            return list;
        }
        catch (SqlException ex)
        {
            return Result<List<SqlDatabaseInfo>>.Failure($"Cannot list databases on '{source.Server}': [{ex.Number}] {ex.Message}");
        }
    }

    public async Task<Result<SqlChainState>> GetChainStateAsync(SqlServerSource source, string database, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(source, ct);
            await using var cmd = new SqlCommand(SqlBackupText.ChainState, conn);
            cmd.Parameters.AddWithValue("@db", database);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new SqlChainState(!reader.IsDBNull(0), !reader.IsDBNull(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        catch (SqlException ex)
        {
            return Result<SqlChainState>.Failure($"Cannot read backup chain for '{database}': [{ex.Number}] {ex.Message}");
        }
    }

    /// Stages backups in the instance's own default backup path (SQL Server can always write there, so no ACL changes are needed).
    /// A remote instance needs a UNC staging folder that both machines can reach.
    public async Task<Result<string>> ResolveStagingFolderAsync(SqlServerSource source, string jobId, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(source, ct);
            await using var cmd = new SqlCommand(SqlBackupText.ServerFacts, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var machine = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var defaultPath = reader.IsDBNull(1) ? null : reader.GetString(1);
            var isLocal = machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            string root;
            if (!string.IsNullOrWhiteSpace(source.StagingFolder))
            {
                if (!isLocal && !source.StagingFolder.StartsWith(@"\\"))
                    return Result<string>.Failure($"SQL Server '{source.Server}' runs on {machine}; use a UNC staging folder (\\\\server\\share) that both machines can reach.");
                root = source.StagingFolder;
            }
            else
            {
                if (!isLocal)
                    return Result<string>.Failure($"SQL Server '{source.Server}' runs on {machine}; remote servers need a UNC staging folder.");
                if (string.IsNullOrWhiteSpace(defaultPath))
                    return Result<string>.Failure("SQL Server did not report a default backup path (needs SQL Server 2019+); set a staging folder.");
                root = defaultPath;
            }

            var folder = Path.Combine(root, "UniBackup", jobId);
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure($"Cannot prepare staging folder: {ex.Message}");
        }
    }

    public async Task<Result> BackupAsync(SqlServerSource source, string database, SqlBackupType type, string filePath, IProgress<int>? percent, CancellationToken ct)
    {
        var result = await RunAsync(source, SqlBackupText.Backup(type, source, source.NativeCompression), percent, ct, ("@db", database), ("@path", filePath));
        if (result.IsFailure && source.NativeCompression && result.Error!.Contains($"[{CompressionNotSupported}]"))
        {
            Log.Warning("[SQL] This edition does not support backup compression; retrying {Db} without it", database);
            result = await RunAsync(source, SqlBackupText.Backup(type, source, compression: false), percent, ct, ("@db", database), ("@path", filePath));
        }
        return result;
    }

    public Task<Result> VerifyAsync(SqlServerSource source, string filePath, CancellationToken ct) =>
        RunAsync(source, SqlBackupText.Verify(source.Checksum), null, ct, ("@path", filePath));

    public Task<Result> CheckDbAsync(SqlServerSource source, string database, CancellationToken ct) =>
        RunAsync(source, SqlBackupText.CheckDb(source.CheckDbPhysicalOnly), null, ct, ("@db", database));

    public async Task<Result> EnsureS3CredentialAsync(SqlServerSource source, string credentialUrl, string accessKey, string secretKey, CancellationToken ct)
    {
        var sql = SqlBackupText.CreateS3Credential(credentialUrl, accessKey, secretKey);
        return await RunAsync(source, sql, null, ct);
    }

    public async Task<Result> BackupToUrlAsync(SqlServerSource source, string database, SqlBackupType type, IReadOnlyList<string> urls, IProgress<int>? percent, CancellationToken ct)
    {
        var sql = SqlBackupText.BackupToUrl(type, source, source.NativeCompression, urls);
        var parameters = new List<(string Name, string Value)> { ("@db", database) };
        for (int i = 0; i < urls.Count; i++)
        {
            parameters.Add(($"@url{i}", urls[i]));
        }

        var result = await RunAsync(source, sql, percent, ct, parameters.ToArray());
        if (result.IsFailure && source.NativeCompression && result.Error!.Contains($"[{CompressionNotSupported}]"))
        {
            Log.Warning("[SQL] This edition does not support backup compression; retrying {Db} without it", database);
            sql = SqlBackupText.BackupToUrl(type, source, compression: false, urls);
            result = await RunAsync(source, sql, percent, ct, parameters.ToArray());
        }
        return result;
    }

    public Task<Result> VerifyFromUrlAsync(SqlServerSource source, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var sql = SqlBackupText.VerifyFromUrl(source.Checksum, urls);
        var parameters = new List<(string Name, string Value)>();
        for (int i = 0; i < urls.Count; i++)
        {
            parameters.Add(($"@url{i}", urls[i]));
        }
        return RunAsync(source, sql, null, ct, parameters.ToArray());
    }

    private async Task<SqlConnection> OpenAsync(SqlServerSource source, CancellationToken ct)
    {
        var conn = new SqlConnection(BuildConnectionString(source));
        await conn.OpenAsync(ct);
        return conn;
    }

    private async Task<Result> RunAsync(SqlServerSource source, string sql, IProgress<int>? percent, CancellationToken ct,
        params (string Name, string Value)[] parameters)
    {
        var errors = new List<string>();
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(source));
            // With this flag every server message (STATS progress *and* errors) fires InfoMessage immediately
            // instead of errors being thrown, so errors must be collected here.
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
        return errors.Count == 0 ? Result.Success() : Result.Failure(Explain(string.Join(" | ", errors)));
    }

    private static string Explain(string error) =>
        error.Contains("Operating system error 5", StringComparison.OrdinalIgnoreCase)
            ? error + " — the SQL Server service account cannot write to the staging folder. Grant it Modify on that folder " +
              "(find the account with: SELECT service_account FROM sys.dm_server_services)."
            : error;
}
