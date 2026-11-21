# UniBackup + SQL Server Backup: Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Grow UniBackup .NET (`UniBackup Pro/`, originally CloudPacker) into one product that does everything SQL Backup
Master 8.7 does for SQL Server (full/diff/log backups, schedules, chain-aware retention, restore,
15 destinations, notifications, reports, service + API), alongside UniBackup's existing folder/IIS
backups. Ship it with a modern animated UI and a handful of features SBM lacks.

**Architecture:** Keep the existing solution layout (`Core` → `Infrastructure` → `Engine` → `Service` /
`Cli` / `UI`). SQL Server becomes a second *job kind* that runs through the existing
`BackupPipeline` and `IStorageProvider` uploads. Over the phases the Windows service becomes the only
scheduler and executor, exposing a local named-pipe API that the WPF UI, CLI and PowerShell all use.
That's the same split SBM uses, built on modern .NET instead of WCF.

**Tech Stack:** .NET 9 / C# 13, WPF (Fluent `ThemeMode`), Microsoft.Data.SqlClient, AWSSDK.S3,
Google.Apis.Drive.v3, MailKit, Polly 8, Serilog, xUnit 2.9.

**Evidence base:** `../SQL Backup Master/README.md` (REA static analysis of SBM 8.7.1149). Every
"parity" item below traces to a section there. Nothing was copied from SBM. Its code was never
decompiled, and its names, branding and licensing are not reused. Parity means the same *behaviour*,
implemented independently.

## Global Constraints

- Target `net9.0-windows` (`UniBackup.Core` stays `net9.0`), `LangVersion 13.0`, `Nullable enable`, from `Directory.Build.props`.
- Phase 1 adds exactly one NuGet package: `Microsoft.Data.SqlClient` **6.0.2** (to `UniBackup.Infrastructure`). Any later package is named in its phase.
- JSON uses snake_case via `[JsonPropertyName]`. Existing `jobs.json`, `config.json` and `history.json` files must load unchanged: new fields are optional with defaults.
- Run status strings stay `"SUCCESS"`, `"PARTIAL"`, `"FAILED"`. Per-database results add `"SKIPPED"`.
- Secrets are stored only through `ICredentialProtector.Seal` / `Unseal`.
- SQL integration tests run only when env var `UNIBACKUP_TEST_SQL` is set. On this dev box use `localhost\SQL2022`, and the test runner's Windows account must be `sysadmin` on that instance.
- Product name stays **UniBackup**. Do not use the name, logo, wording or licensing scheme of SQL Backup Master.
- UniBackup must run next to an installed CloudPacker. Never reuse CloudPacker's service name, data directory, scheduled-task names or log file names (UniBackup's tasks are `UniBackup_<jobId>`).
- A DIFF or LOG may only build on a base this job uploaded to **every** destination. Anything else is promoted to FULL (see "Phase 1 follow-ups").
- All commands below run from `UniBackup Pro/`.

## Problems found while planning (fixed inside Phase 1, Tasks 5 and 7)

1. **Double finalisation:** the early-failure branches of `BackupPipeline.ExecuteJobAsync`
   (missing source, compression failure, no providers) call `FinalizeRunAsync` and then `return`,
   and the `finally` block calls it again. Each such failure appends history twice and sends every
   notification twice.
2. **Retention can target the bucket root:** the retention step resolves the folder as
   `job.CloudFolder ?? config.Aws.S3Prefix` and never falls back to `ServerIdentity`. With a blank
   prefix it cleans `""`, while the upload went to `backups/<ip>/`.
3. **The scheduler can only express "at HH:mm".** `Worker.cs` polls every 30 s and matches the
   hour and minute. That allows no intervals (log backups need "every 15 min"), no run windows and
   no missed-run catch-up. Phase 2 replaces it.
4. `MailKit 4.11.0` had advisory GHSA-9j88-vvj5-vhgr (build warning NU1902). **Done:** bumped to 4.18.1 on 21 Nov 2026.

---

## Feature parity map

| SBM capability (README §) | UniBackup today | Phase |
|---|---|---|
| Full / Differential / Log backups via T-SQL `BACKUP` (§4.5) | — | **1** |
| Native compression, CHECKSUM, COPY_ONLY, timeout (§4.5) | — | **1** |
| `RESTORE VERIFYONLY`, `DBCC CHECKDB` (incl. PHYSICAL_ONLY) (§4.6) | — | **1** |
| Chain validation for diff/log (§4.5) | — | **1**, better: auto-promote to FULL instead of failing |
| DB selection: all user DBs, explicit list, wildcard excludes, skip offline / SIMPLE-for-log (§4.4) | — | **1** |
| SQL service account must be able to write the `.bak` (§4.3) | — | **1**, better: stage in the instance's own default backup path, so no ACL changes are needed |
| Remote layout per database / per backup type (§4.8) | flat | **1** |
| Schedules: full/diff/log intervals, days, run window, missed runs, retries (§3) | daily/weekly/monthly at HH:mm | **2** |
| History DB + pruning (§6, §3) | `history.json` (unbounded) | **2** (SQLite) |
| Chain-aware retention, obviated/aged-out, S3 version purge (§4.9) | age-based delete | **3** |
| Abort abandoned multipart uploads (§5) | — | **3** |
| AES-256 encrypted backups (§4.7) | — | **3** |
| Windows service owns jobs; UI/CLI talk over local IPC; admin-only; live progress callbacks (§1, §8) | service + UI both execute | **4** |
| Modern UI (SBM: WPF + Telerik) | WPF, custom styles | **5** |
| Restore from destination, RESTORE HEADERONLY / FILELISTONLY (§2, §11) | folder restore only | **6** |
| S3-compatible (Wasabi, B2, IDrive e2, GCS, custom), Azure Blob, OneDrive (+Business), Dropbox, Box, FTP/SFTP, WebDAV, Folder/UNC (§5) | S3, Google Drive | **7** |
| Per-destination backup-type routing, emergency destination, storage class, SSE (§4.8, §5) | — | **7** |
| Proxy settings (§5) | — | **7** |
| Email (SMTP / Gmail OAuth / M365 OAuth), webhooks, desktop toast; per type × outcome triggers (§4.10) | email + webhooks (no matrix) | **8** |
| Scheduled reports (12 report types) (§2) | — | **8** (HTML email, no report designer) |
| Pre/post scripts: SQL, batch, PowerShell (§4.2) | — | **9** |
| Run-as / impersonation, UNC temp auth (§4.3) | — | **9** |
| Config export/import with password (§7) | — | **9** |
| PowerShell cmdlets (§8) | CLI | **9** |
| Maintenance: history prune, log archive, OAuth keepalive, temp sweep (§3) | partial | **2** / **4** |
| Product licensing, editions, trials | — | **Non-goal** (in-house tool) |

### Features beyond SBM (the "much better" part)

| New feature | Why it matters | Phase |
|---|---|---|
| **Auto chain promotion**: a diff/log with no valid base becomes a FULL automatically | SBM warns or errors; ours just produces a restorable backup | 1 |
| **Point-in-time restore wizard**: pick a timestamp, and it resolves FULL + DIFF + LOG chain from the cloud and restores with `STOPAT` | SBM restores files; ours restores a *moment* | 6 |
| **Automated restore drills**: scheduled test restore to a scratch DB + CHECKDB + measured RTO | Proves backups restore; SBM only verifies headers | 6 |
| **RPO dashboard + breach alerts**: per database "minutes since last good backup" vs target | Surfaces silent failures before they matter | 8 |
| **Immutable backups (S3 Object Lock)** + **GFS retention** | Ransomware-resistant; long-term monthly fulls | 3 |
| **One tool for folders, IIS sites and SQL Server** | One service, one UI, one set of destinations | inherent |
| **Parallel multi-destination upload** | Every destination receives the file at the same time; the window is the slowest upload, not the sum | 3B |
| **Direct-to-cloud SQL backups** (SQL Server 2022+ to S3-compatible storage) | No local staging disk at all for large databases | 3B |
| **Heartbeat monitoring** (Healthchecks.io-style ping) | Alerts when the server or service stops running backups at all | 8 |

### Explicit non-goals

Product licensing or editions, a Telerik-style report designer, WCF, custom free-form `WITH`
parameters for `BACKUP` (an injection surface with little value), SQL Server on Linux, cross-platform
builds (the app is Windows-only: WPF, DPAPI, Task Scheduler), and SQL backups written to a named pipe
(SQL Server doesn't support it; tested 21 Nov 2026, see Phase 3B).

---

## Phase overview

Each phase ends with working, shippable software. Phase 1 is broken down to individual steps
below. Phases 2–9 have their scope, design decisions, files and acceptance tests fixed here. Each
gets its own step-level plan (`docs/superpowers/plans/<date>-phase-N-*.md`) when it starts, because
step-level code written now would be stale by the time those phases run.

| # | Phase | Ships |
|---|---|---|
| 1 | SQL Server backup engine | SQL jobs run (CLI / existing triggers) and upload FULL/DIFF/LOG to S3 + Drive |
| 2 | Scheduler v2 + SQLite history | Every-N-minute log backups, run windows, missed-run catch-up, retries |
| 3 | Retention, immutability, encryption | Chain-aware + GFS retention, Object Lock, AES-256 files, multipart sweep |
| 3B | Speed | Parallel uploads to all destinations, upload overlapped with the next backup, direct-to-cloud SQL backups (SQL Server 2022+) |
| 4 | Service-owned engine + local API | Named-pipe API, live progress, admin-only, UI/CLI become clients |
| 5 | Modern UI + motion | New shell, dashboard, SQL job editor, run monitor, animations |
| 6A | Restore engine + CLI | Point-in-time restore from the cloud via `UniBackup.exe --restore` |
| 6B | Restore UI + drills | Point-in-time restore wizard, scheduled restore drills |
| 7 | Destination parity | S3-compatible presets, Azure, OneDrive, Dropbox, Box, FTP/SFTP, WebDAV, UNC |
| 8 | Notifications, reports, RPO | Trigger matrix, toasts, scheduled HTML reports, RPO breach alerts |
| 9 | Automation & admin | Pre/post scripts, run-as, config export/import, PowerShell module |

**Build order:** 1 → 2 → 3 → 3B → 6A → 4 → 5 → 6B → 7 → 8 → 9. Restore (6A) comes before the service
rewrite and the UI because a backup tool that can't restore from the command line isn't safe to deploy.

---

# Phase 1: SQL Server backup engine (detailed)

> **Status (21 Nov 2026): ✅ complete.** Implemented and tested, then hardened by the plan review (see "Plan review (21 Nov 2026)" after Task 8): 78 tests green with `UNIBACKUP_TEST_SQL=localhost\SQL2022`. `--type` and DI were also wired into `src/UniBackup.UI/Program.cs`, the real shipped exe. Still open: the Task 8 S3 smoke test (needs a real bucket). The per-task commit steps were skipped because no git repository existed then. The workspace is now a git repo, and these changes are not committed yet.

**File map**

| File | Responsibility |
|---|---|
| Create `src/UniBackup.Core/Models/SqlServerSource.cs` | SQL job settings, backup type enum, per-DB result, server/chain facts |
| Modify `src/UniBackup.Core/Models/BackupJob.cs` | `kind` + `sql` fields |
| Modify `src/UniBackup.Core/Models/RunHistoryRecord.cs` | `sql_databases` list |
| Create `src/UniBackup.Core/Common/SqlBackupFileName.cs` | Backup file naming + parsing, remote subfolder |
| Create `src/UniBackup.Core/Common/SqlDatabaseSelector.cs` | Which databases a run includes / skips and why |
| Create `src/UniBackup.Core/Common/SqlJobRules.cs` | Chain promotion + overall run status |
| Create `src/UniBackup.Core/Common/DestinationFolders.cs` | The single place a destination's base folder is resolved |
| Modify `src/UniBackup.Infrastructure/Storage/AwsS3StorageProvider.cs`, `GoogleDriveStorageProvider.cs` | Use `DestinationFolders` |
| Create `src/UniBackup.Infrastructure/Sql/SqlBackupText.cs` | All T-SQL text + STATS progress parsing (pure) |
| Create `src/UniBackup.Infrastructure/Sql/SqlServerBackupService.cs` | Talks to SQL Server |
| Modify `src/UniBackup.Engine/Pipeline/BackupPipeline.cs` | `partial`, shared upload helpers, bug fixes, SQL branch |
| Create `src/UniBackup.Engine/Pipeline/BackupPipeline.Sql.cs` | SQL job execution |
| Modify `src/UniBackup.Cli/Program.cs`, `src/UniBackup.Service/Program.cs`, UI composition root | `--type`, DI registration |
| Tests in `tests/UniBackup.Tests/` | `SqlModelTests`, `SqlBackupFileNameTests`, `SqlBackupTextTests`, `SqlRulesTests`, `DestinationFoldersTests`, `SqlServerBackupServiceTests` |

---

### Task 1: SQL job model

**Files:**
- Create: `src/UniBackup.Core/Models/SqlServerSource.cs`
- Modify: `src/UniBackup.Core/Models/BackupJob.cs` (add 3 members to `BackupJob`)
- Modify: `src/UniBackup.Core/Models/RunHistoryRecord.cs` (add 1 member to `RunHistoryRecord`)
- Test: `tests/UniBackup.Tests/SqlModelTests.cs`

**Interfaces:**
- Produces: `enum SqlBackupType { Full, Diff, Log }` (serialized as strings); `class SqlServerSource`;
  `class SqlDatabaseRunResult`; `record SqlDatabaseInfo(string Name, string State, string RecoveryModel, bool IsReadOnly, bool IsSystem)`;
  `record SqlChainState(bool HasDifferentialBase, bool HasLogChain)`; `BackupJob.Kind`, `BackupJob.Sql`,
  `BackupJob.IsSqlServer`; `RunHistoryRecord.SqlDatabases`.

- [x] **Step 1: Write the failing test**

`tests/UniBackup.Tests/SqlModelTests.cs`:
```csharp
using System.Text.Json;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlModelTests
{
    [Fact]
    public void LegacyJobJson_LoadsAsFolderJob()
    {
        var job = JsonSerializer.Deserialize<BackupJob>("{\"id\":\"a1\",\"name\":\"Web\",\"source_folder\":\"C:\\\\inetpub\"}")!;

        Assert.False(job.IsSqlServer);
        Assert.Equal("folder", job.Kind);
        Assert.Null(job.Sql);
    }

    [Fact]
    public void SqlJob_RoundTrips()
    {
        var job = new BackupJob
        {
            Kind = "sqlserver",
            Sql = new SqlServerSource { AllUserDatabases = false, Databases = ["Shop"] }
        };

        var back = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(job))!;

        Assert.True(back.IsSqlServer);
        Assert.Equal(new[] { "Shop" }, back.Sql!.Databases);
        Assert.True(back.Sql.Checksum);
    }

    [Fact]
    public void BackupType_SerializesAsString()
    {
        var history = new RunHistoryRecord
        {
            SqlDatabases = [new SqlDatabaseRunResult { Database = "Shop", BackupType = SqlBackupType.Diff }]
        };

        Assert.Contains("\"backup_type\":\"Diff\"", JsonSerializer.Serialize(history));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlModelTests"`
Expected: build FAILS: `'BackupJob' does not contain a definition for 'IsSqlServer'`.

- [x] **Step 3: Write minimal implementation**

`src/UniBackup.Core/Models/SqlServerSource.cs`:
```csharp
using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SqlBackupType>))]
public enum SqlBackupType { Full, Diff, Log }

public class SqlServerSource
{
    [JsonPropertyName("server")] public string Server { get; set; } = "localhost";
    [JsonPropertyName("use_windows_auth")] public bool UseWindowsAuth { get; set; } = true;
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password_sealed")] public string? PasswordSealed { get; set; } // ICredentialProtector.Seal
    [JsonPropertyName("encrypt")] public bool Encrypt { get; set; } = true;
    [JsonPropertyName("trust_server_certificate")] public bool TrustServerCertificate { get; set; }
    [JsonPropertyName("all_user_databases")] public bool AllUserDatabases { get; set; } = true;
    [JsonPropertyName("databases")] public List<string> Databases { get; set; } = [];
    [JsonPropertyName("exclude_patterns")] public List<string> ExcludePatterns { get; set; } = []; // wildcards * ?
    [JsonPropertyName("copy_only")] public bool CopyOnly { get; set; }
    [JsonPropertyName("checksum")] public bool Checksum { get; set; } = true;
    [JsonPropertyName("native_compression")] public bool NativeCompression { get; set; } = true;
    [JsonPropertyName("verify")] public bool Verify { get; set; } = true;
    [JsonPropertyName("run_checkdb")] public bool RunCheckDb { get; set; }
    [JsonPropertyName("checkdb_physical_only")] public bool CheckDbPhysicalOnly { get; set; }
    [JsonPropertyName("staging_folder")] public string? StagingFolder { get; set; } // blank = instance default backup path
    [JsonPropertyName("command_timeout_seconds")] public int CommandTimeoutSeconds { get; set; } // 0 = no timeout
}

public class SqlDatabaseRunResult
{
    [JsonPropertyName("database")] public string Database { get; set; } = "";
    [JsonPropertyName("backup_type")] public SqlBackupType BackupType { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "SUCCESS"; // SUCCESS, FAILED, SKIPPED
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public record SqlDatabaseInfo(string Name, string State, string RecoveryModel, bool IsReadOnly, bool IsSystem);

public record SqlChainState(bool HasDifferentialBase, bool HasLogChain);
```

In `src/UniBackup.Core/Models/BackupJob.cs`, add inside `class BackupJob` (after the `retention_days` property):
```csharp
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "folder"; // "folder" | "sqlserver"

    [JsonPropertyName("sql")]
    public SqlServerSource? Sql { get; set; }

    [JsonIgnore]
    public bool IsSqlServer => Kind.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);
```

In `src/UniBackup.Core/Models/RunHistoryRecord.cs`, add inside `class RunHistoryRecord` (after `LogFile`):
```csharp
    [JsonPropertyName("sql_databases")]
    public List<SqlDatabaseRunResult> SqlDatabases { get; set; } = [];
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlModelTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Core/Models tests/UniBackup.Tests/SqlModelTests.cs
git commit -m "feat(sql): add SQL Server job model"
```

---

### Task 2: Backup file naming

**Files:**
- Create: `src/UniBackup.Core/Common/SqlBackupFileName.cs`
- Test: `tests/UniBackup.Tests/SqlBackupFileNameTests.cs`

**Interfaces:**
- Consumes: `SqlBackupType`, `FormattingUtils.SanitizePathSegment(string)`.
- Produces: `SqlBackupFileName.Format(string database, SqlBackupType type, DateTime timestamp) : string`,
  `SqlBackupFileName.RemoteSubfolder(string database, SqlBackupType type) : string` (e.g. `Shop/FULL`),
  `SqlBackupFileName.TryParse(string fileName, out (string Database, SqlBackupType Type, DateTime Timestamp) parsed) : bool`.
  Phase 3 retention depends on `TryParse`.

- [x] **Step 1: Write the failing test**

```csharp
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlBackupFileNameTests
{
    [Fact]
    public void Format_ThenParse_RoundTrips()
    {
        var ts = new DateTime(2026, 11, 21, 14, 5, 9);
        var name = SqlBackupFileName.Format("Shop_DB", SqlBackupType.Log, ts);

        Assert.Equal("Shop_DB_LOG_20261121_140509.trn", name);
        Assert.True(SqlBackupFileName.TryParse(name, out var p));
        Assert.Equal("Shop_DB", p.Database);
        Assert.Equal(SqlBackupType.Log, p.Type);
        Assert.Equal(ts, p.Timestamp);
    }

    [Fact]
    public void Format_SanitizesInvalidFileNameCharacters()
    {
        Assert.Equal("a_b_FULL_20260101_000000.bak",
            SqlBackupFileName.Format("a:b", SqlBackupType.Full, new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void RemoteSubfolder_IsDatabaseThenType()
    {
        Assert.Equal("Shop/DIFF", SqlBackupFileName.RemoteSubfolder("Shop", SqlBackupType.Diff));
    }

    [Fact]
    public void TryParse_RejectsFolderArchives()
    {
        Assert.False(SqlBackupFileName.TryParse("Website_20261121_020000.zip", out _));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlBackupFileNameTests"`
Expected: build FAILS: `The name 'SqlBackupFileName' does not exist`.

- [x] **Step 3: Write minimal implementation**

`src/UniBackup.Core/Common/SqlBackupFileName.cs`:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static partial class SqlBackupFileName
{
    public static string Format(string database, SqlBackupType type, DateTime timestamp) =>
        $"{FormattingUtils.SanitizePathSegment(database)}_{Tag(type)}_{timestamp:yyyyMMdd_HHmmss}{(type == SqlBackupType.Log ? ".trn" : ".bak")}";

    public static string RemoteSubfolder(string database, SqlBackupType type) =>
        $"{FormattingUtils.SanitizePathSegment(database)}/{Tag(type)}";

    public static bool TryParse(string fileName, out (string Database, SqlBackupType Type, DateTime Timestamp) parsed)
    {
        parsed = default;
        var m = Pattern().Match(fileName);
        if (!m.Success) return false;

        var type = m.Groups["type"].Value switch
        {
            "FULL" => SqlBackupType.Full,
            "DIFF" => SqlBackupType.Diff,
            _ => SqlBackupType.Log
        };
        var ts = DateTime.ParseExact(m.Groups["ts"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        parsed = (m.Groups["db"].Value, type, ts);
        return true;
    }

    private static string Tag(SqlBackupType type) => type switch
    {
        SqlBackupType.Full => "FULL",
        SqlBackupType.Diff => "DIFF",
        _ => "LOG"
    };

    [GeneratedRegex(@"^(?<db>.+)_(?<type>FULL|DIFF|LOG)_(?<ts>\d{8}_\d{6})\.(bak|trn)$")]
    private static partial Regex Pattern();
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlBackupFileNameTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Core/Common/SqlBackupFileName.cs tests/UniBackup.Tests/SqlBackupFileNameTests.cs
git commit -m "feat(sql): backup file naming and parsing"
```

---

### Task 3: T-SQL text (pure)

**Files:**
- Create: `src/UniBackup.Infrastructure/Sql/SqlBackupText.cs`
- Test: `tests/UniBackup.Tests/SqlBackupTextTests.cs`

**Interfaces:**
- Consumes: `SqlBackupType`, `SqlServerSource`.
- Produces: `SqlBackupText.Backup(SqlBackupType, SqlServerSource, bool compression)`, `Verify(bool checksum)`,
  `CheckDb(bool physicalOnly)`, constants `ListDatabases`, `ChainState`, `ServerFacts`,
  `TryParsePercent(string message, out int percent)`. Every statement takes its values as `@db` / `@path`
  parameters. No identifier is ever concatenated into SQL.

- [x] **Step 1: Write the failing test**

```csharp
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Sql;
using Xunit;

namespace UniBackup.Tests;

public class SqlBackupTextTests
{
    [Fact]
    public void FullBackup_DefaultOptions()
    {
        Assert.Equal(
            "BACKUP DATABASE @db TO DISK = @path WITH INIT, CHECKSUM, COMPRESSION, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Full, new SqlServerSource(), compression: true));
    }

    [Fact]
    public void DiffBackup_NeverCopyOnly()
    {
        Assert.Equal(
            "BACKUP DATABASE @db TO DISK = @path WITH DIFFERENTIAL, INIT, CHECKSUM, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Diff, new SqlServerSource { CopyOnly = true }, compression: false));
    }

    [Fact]
    public void LogBackup_CopyOnlyWithoutChecksum()
    {
        Assert.Equal(
            "BACKUP LOG @db TO DISK = @path WITH INIT, COPY_ONLY, COMPRESSION, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Log, new SqlServerSource { CopyOnly = true, Checksum = false }, compression: true));
    }

    [Fact]
    public void VerifyAndCheckDb()
    {
        Assert.Equal("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;", SqlBackupText.Verify(checksum: true));
        Assert.Equal("DBCC CHECKDB (@db) WITH NO_INFOMSGS, PHYSICAL_ONLY;", SqlBackupText.CheckDb(physicalOnly: true));
    }

    [Theory]
    [InlineData("10 percent processed.", true, 10)]
    [InlineData("100 percent processed.", true, 100)]
    [InlineData("Processed 344 pages for database 'Shop', file 'Shop' on file 1.", false, 0)]
    public void ParsesStatsMessages(string message, bool ok, int expected)
    {
        Assert.Equal(ok, SqlBackupText.TryParsePercent(message, out var pct));
        Assert.Equal(expected, pct);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlBackupTextTests"`
Expected: build FAILS: `The type or namespace name 'Sql' does not exist in the namespace 'UniBackup.Infrastructure'`.

- [x] **Step 3: Write minimal implementation**

`src/UniBackup.Infrastructure/Sql/SqlBackupText.cs`:
```csharp
using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Sql;

public static partial class SqlBackupText
{
    public static string Backup(SqlBackupType type, SqlServerSource options, bool compression)
    {
        var with = new List<string>();
        if (type == SqlBackupType.Diff) with.Add("DIFFERENTIAL");
        with.Add("INIT");
        if (options.CopyOnly && type != SqlBackupType.Diff) with.Add("COPY_ONLY"); // no effect on DIFFERENTIAL
        if (options.Checksum) with.Add("CHECKSUM");
        if (compression) with.Add("COMPRESSION");
        with.Add("STATS = 5");

        var verb = type == SqlBackupType.Log ? "BACKUP LOG" : "BACKUP DATABASE";
        return $"{verb} @db TO DISK = @path WITH {string.Join(", ", with)};";
    }

    public static string Verify(bool checksum) =>
        checksum ? "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;" : "RESTORE VERIFYONLY FROM DISK = @path;";

    public static string CheckDb(bool physicalOnly) =>
        physicalOnly ? "DBCC CHECKDB (@db) WITH NO_INFOMSGS, PHYSICAL_ONLY;" : "DBCC CHECKDB (@db) WITH NO_INFOMSGS;";

    public const string ListDatabases =
        "SELECT name, state_desc, recovery_model_desc, is_read_only, database_id FROM sys.databases WHERE name <> 'tempdb' ORDER BY name;";

    // NULL differential_base_lsn = no full to diff against; NULL last_log_backup_lsn = log chain not started.
    public const string ChainState =
        "SELECT (SELECT TOP (1) differential_base_lsn FROM sys.master_files WHERE database_id = DB_ID(@db) AND type = 0), " +
        "(SELECT last_log_backup_lsn FROM sys.database_recovery_status WHERE database_id = DB_ID(@db));";

    public const string ServerFacts =
        "SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(256)), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));";

    public static bool TryParsePercent(string message, out int percent)
    {
        var m = PercentPattern().Match(message);
        percent = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        return m.Success;
    }

    [GeneratedRegex(@"^(\d{1,3}) percent processed", RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlBackupTextTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Infrastructure/Sql/SqlBackupText.cs tests/UniBackup.Tests/SqlBackupTextTests.cs
git commit -m "feat(sql): parameterised T-SQL statements and STATS parsing"
```

---

### Task 4: Database selection, chain promotion, run status

**Files:**
- Create: `src/UniBackup.Core/Common/SqlDatabaseSelector.cs`
- Create: `src/UniBackup.Core/Common/SqlJobRules.cs`
- Test: `tests/UniBackup.Tests/SqlRulesTests.cs`

**Interfaces:**
- Consumes: `SqlDatabaseInfo`, `SqlServerSource`, `SqlBackupType`, `SqlChainState`, `SqlDatabaseRunResult`, `DestinationRunResult`.
- Produces: `SqlDatabaseSelector.Select(IEnumerable<SqlDatabaseInfo>, SqlServerSource, SqlBackupType) : (List<string> Included, List<SqlDatabaseRunResult> Skipped)`;
  `SqlJobRules.EffectiveType(SqlBackupType requested, SqlChainState chain) : SqlBackupType`;
  `SqlJobRules.OverallStatus(IReadOnlyCollection<SqlDatabaseRunResult>, IReadOnlyCollection<DestinationRunResult>) : string`.

- [x] **Step 1: Write the failing test**

```csharp
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlRulesTests
{
    private static readonly SqlDatabaseInfo[] Server =
    [
        new("master", "ONLINE", "SIMPLE", false, true),
        new("msdb", "ONLINE", "SIMPLE", false, true),
        new("Shop", "ONLINE", "FULL", false, false),
        new("Shop_Archive", "ONLINE", "FULL", true, false),
        new("Reports", "ONLINE", "SIMPLE", false, false),
        new("OldCrm", "OFFLINE", "FULL", false, false),
    ];

    [Fact]
    public void AllUserDatabases_ExcludesSystemAndWildcards_SkipsOffline()
    {
        var (included, skipped) = SqlDatabaseSelector.Select(Server, new SqlServerSource { ExcludePatterns = ["*_archive"] }, SqlBackupType.Full);

        Assert.Equal(new[] { "Shop", "Reports" }, included);
        var s = Assert.Single(skipped);
        Assert.Equal("OldCrm", s.Database);
        Assert.Equal("SKIPPED", s.Status);
    }

    [Fact]
    public void LogBackup_SkipsSimpleRecovery()
    {
        var (included, skipped) = SqlDatabaseSelector.Select(Server, new SqlServerSource(), SqlBackupType.Log);

        Assert.Equal(new[] { "Shop", "Shop_Archive" }, included);
        Assert.Contains(skipped, x => x.Database == "Reports" && x.Message!.Contains("SIMPLE"));
    }

    [Fact]
    public void ExplicitList_AllowsSystemDb_AndFailsMissingOnes()
    {
        var src = new SqlServerSource { AllUserDatabases = false, Databases = ["master", "Ghost"] };

        var (included, skipped) = SqlDatabaseSelector.Select(Server, src, SqlBackupType.Diff);

        Assert.Empty(included); // master supports full backups only
        Assert.Contains(skipped, x => x.Database == "master" && x.Status == "SKIPPED");
        Assert.Contains(skipped, x => x.Database == "Ghost" && x.Status == "FAILED");
    }

    [Theory]
    [InlineData(SqlBackupType.Full, false, false, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Diff, false, true, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Diff, true, true, SqlBackupType.Diff)]
    [InlineData(SqlBackupType.Log, true, false, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Log, true, true, SqlBackupType.Log)]
    public void EffectiveType_PromotesToFullWhenChainMissing(SqlBackupType requested, bool diffBase, bool logChain, SqlBackupType expected)
    {
        Assert.Equal(expected, SqlJobRules.EffectiveType(requested, new SqlChainState(diffBase, logChain)));
    }

    private static SqlDatabaseRunResult Db(string status) => new() { Status = status };
    private static DestinationRunResult Up(string status) => new() { Status = status };

    [Fact]
    public void OverallStatus()
    {
        Assert.Equal("SUCCESS", SqlJobRules.OverallStatus([Db("SUCCESS"), Db("SKIPPED")], [Up("SUCCESS")]));
        Assert.Equal("PARTIAL", SqlJobRules.OverallStatus([Db("SUCCESS"), Db("FAILED")], [Up("SUCCESS")]));
        Assert.Equal("PARTIAL", SqlJobRules.OverallStatus([Db("SUCCESS")], [Up("SUCCESS"), Up("FAILED")]));
        Assert.Equal("FAILED", SqlJobRules.OverallStatus([Db("FAILED")], []));
        Assert.Equal("FAILED", SqlJobRules.OverallStatus([Db("SUCCESS")], [Up("FAILED")]));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlRulesTests"`
Expected: build FAILS: `The name 'SqlDatabaseSelector' does not exist`.

- [x] **Step 3: Write minimal implementation**

`src/UniBackup.Core/Common/SqlDatabaseSelector.cs`:
```csharp
using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlDatabaseSelector
{
    public static (List<string> Included, List<SqlDatabaseRunResult> Skipped) Select(
        IEnumerable<SqlDatabaseInfo> databases, SqlServerSource source, SqlBackupType type)
    {
        var all = databases.ToList();
        var excludes = source.ExcludePatterns.Select(WildcardToRegex).ToList();
        var included = new List<string>();
        var skipped = new List<SqlDatabaseRunResult>();

        foreach (var db in all)
        {
            var wanted = source.AllUserDatabases
                ? !db.IsSystem
                : source.Databases.Contains(db.Name, StringComparer.OrdinalIgnoreCase);
            if (!wanted || excludes.Any(r => r.IsMatch(db.Name))) continue;

            var reason = SkipReason(db, type);
            if (reason is null) included.Add(db.Name);
            else skipped.Add(new SqlDatabaseRunResult { Database = db.Name, BackupType = type, Status = "SKIPPED", Message = reason });
        }

        if (!source.AllUserDatabases)
        {
            var onServer = all.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            skipped.AddRange(source.Databases.Where(n => !onServer.Contains(n)).Select(n =>
                new SqlDatabaseRunResult { Database = n, BackupType = type, Status = "FAILED", Message = "Database not found on server" }));
        }

        return (included, skipped);
    }

    private static string? SkipReason(SqlDatabaseInfo db, SqlBackupType type)
    {
        if (!db.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)) return $"Database is {db.State}";
        if (type == SqlBackupType.Log && db.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            return "SIMPLE recovery model has no log backups";
        if (type == SqlBackupType.Diff && db.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
            return "master supports full backups only";
        return null;
    }

    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
}
```

`src/UniBackup.Core/Common/SqlJobRules.cs`:
```csharp
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlJobRules
{
    /// A DIFF needs a full base and a LOG needs a started log chain; otherwise take a FULL so the run still produces a restorable backup.
    public static SqlBackupType EffectiveType(SqlBackupType requested, SqlChainState chain) => requested switch
    {
        SqlBackupType.Diff when !chain.HasDifferentialBase => SqlBackupType.Full,
        SqlBackupType.Log when !chain.HasLogChain => SqlBackupType.Full,
        _ => requested
    };

    public static string OverallStatus(IReadOnlyCollection<SqlDatabaseRunResult> databases, IReadOnlyCollection<DestinationRunResult> uploads)
    {
        var succeeded = databases.Count(d => d.Status == "SUCCESS");
        var failed = databases.Count(d => d.Status == "FAILED") + uploads.Count(u => u.Status == "FAILED");

        if (succeeded == 0 || uploads.All(u => u.Status != "SUCCESS")) return "FAILED";
        return failed > 0 ? "PARTIAL" : "SUCCESS";
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlRulesTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Core/Common/SqlDatabaseSelector.cs src/UniBackup.Core/Common/SqlJobRules.cs tests/UniBackup.Tests/SqlRulesTests.cs
git commit -m "feat(sql): database selection, chain promotion and run status rules"
```

---

### Task 5: One destination-folder resolver (fixes retention-at-bucket-root)

**Files:**
- Create: `src/UniBackup.Core/Common/DestinationFolders.cs`
- Modify: `src/UniBackup.Infrastructure/Storage/AwsS3StorageProvider.cs:64-70`
- Modify: `src/UniBackup.Infrastructure/Storage/GoogleDriveStorageProvider.cs:123-129`
- Test: `tests/UniBackup.Tests/DestinationFoldersTests.cs`

**Interfaces:**
- Consumes: `AppConfig.Aws.S3Prefix`, `AppConfig.GDrive.TargetFolder`, `ServerIdentity.Current.DefaultS3Prefix` / `DefaultGDriveFolder`, `BackupJob.CloudFolder`.
- Produces: `DestinationFolders.Resolve(string provider, AppConfig config, BackupJob job) : string`. Never empty, no trailing `/`.

- [x] **Step 1: Write the failing test**

```csharp
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class DestinationFoldersTests
{
    [Fact]
    public void JobFolderWins()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "cfg/";
        Assert.Equal("jobs/web", DestinationFolders.Resolve("s3", cfg, new BackupJob { CloudFolder = "jobs/web/" }));
    }

    [Fact]
    public void ConfigFolderUsedWhenJobBlank()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "cfg/";
        Assert.Equal("cfg", DestinationFolders.Resolve("s3", cfg, new BackupJob()));
    }

    [Fact]
    public void BlankEverything_FallsBackToServerDefault_NeverBucketRoot()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "";
        cfg.GDrive.TargetFolder = "";

        Assert.Equal(ServerIdentity.Current.DefaultS3Prefix.TrimEnd('/'), DestinationFolders.Resolve("s3", cfg, new BackupJob()));
        Assert.Equal(ServerIdentity.Current.DefaultGDriveFolder.TrimEnd('/'), DestinationFolders.Resolve("gdrive", cfg, new BackupJob()));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~DestinationFoldersTests"`
Expected: build FAILS: `The name 'DestinationFolders' does not exist`.

- [x] **Step 3: Write minimal implementation**

`src/UniBackup.Core/Common/DestinationFolders.cs`:
```csharp
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class DestinationFolders
{
    public static string Resolve(string provider, AppConfig config, BackupJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.CloudFolder)) return job.CloudFolder.TrimEnd('/');

        var isS3 = provider.Equals("s3", StringComparison.OrdinalIgnoreCase);
        var configured = isS3 ? config.Aws.S3Prefix : config.GDrive.TargetFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        return (isS3 ? ServerIdentity.Current.DefaultS3Prefix : ServerIdentity.Current.DefaultGDriveFolder).TrimEnd('/');
    }
}
```

In `AwsS3StorageProvider.UploadAsync` replace lines 64-70 (the `var prefix = ...` block through its closing `}`) with:
```csharp
                var prefix = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
```
The existing `if (!prefix.EndsWith('/')) prefix += "/";` on the next line stays.

In `GoogleDriveStorageProvider.UploadAsync` replace lines 123-129 (the `var folderPath = ...` block through its closing `}`) with:
```csharp
            var folderPath = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
```

- [x] **Step 4: Run tests**

Run: `dotnet test tests/UniBackup.Tests`
Expected: PASS. All existing tests are still green, plus the 3 new ones.

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Core/Common/DestinationFolders.cs src/UniBackup.Infrastructure/Storage tests/UniBackup.Tests/DestinationFoldersTests.cs
git commit -m "fix: resolve destination folders in one place (retention could target bucket root)"
```

---

### Task 6: SqlServerBackupService

**Files:**
- Modify: `src/UniBackup.Infrastructure/UniBackup.Infrastructure.csproj` (add package)
- Create: `src/UniBackup.Infrastructure/Sql/SqlServerBackupService.cs`
- Test: `tests/UniBackup.Tests/SqlServerBackupServiceTests.cs` (integration, gated)

**Interfaces:**
- Consumes: `ICredentialProtector.Unseal`, `SqlBackupText.*`, models from Task 1.
- Produces (all on `SqlServerBackupService(ICredentialProtector crypto)`):
  - `string BuildConnectionString(SqlServerSource source)`
  - `Task<Result<List<SqlDatabaseInfo>>> ListDatabasesAsync(SqlServerSource source, CancellationToken ct)`
  - `Task<Result<SqlChainState>> GetChainStateAsync(SqlServerSource source, string database, CancellationToken ct)`
  - `Task<Result<string>> ResolveStagingFolderAsync(SqlServerSource source, string jobId, CancellationToken ct)`
  - `Task<Result> BackupAsync(SqlServerSource source, string database, SqlBackupType type, string filePath, IProgress<int>? percent, CancellationToken ct)`
  - `Task<Result> VerifyAsync(SqlServerSource source, string filePath, CancellationToken ct)`
  - `Task<Result> CheckDbAsync(SqlServerSource source, string database, CancellationToken ct)`
  - Failure messages contain the SQL error number as `[NNNN]`.

- [x] **Step 1: Add the package**

Run: `dotnet add src/UniBackup.Infrastructure package Microsoft.Data.SqlClient --version 6.0.2`
Expected: `PackageReference for package 'Microsoft.Data.SqlClient' version '6.0.2' added`.

- [x] **Step 2: Write the failing integration tests**

`tests/UniBackup.Tests/SqlServerBackupServiceTests.cs`:
```csharp
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace UniBackup.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIBACKUP_TEST_SQL")))
            Skip = "Set UNIBACKUP_TEST_SQL to a local SQL Server instance (e.g. localhost\\SQL2022) to run.";
    }
}

internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public class SqlServerBackupServiceTests : IAsyncLifetime
{
    private readonly SqlServerBackupService _svc = new(new CredentialProtector());
    private readonly SqlServerSource _src = new()
    {
        Server = Environment.GetEnvironmentVariable("UNIBACKUP_TEST_SQL") ?? "",
        TrustServerCertificate = true
    };
    private readonly string _db = $"cp_test_{Guid.NewGuid():N}"[..20];

    private bool Enabled => !string.IsNullOrWhiteSpace(_src.Server);

    public async Task InitializeAsync()
    {
        if (Enabled) await ExecAsync($"CREATE DATABASE [{_db}]; ALTER DATABASE [{_db}] SET RECOVERY FULL;");
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
            await ExecAsync($"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END");
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_svc.BuildConnectionString(_src));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SqlFact]
    public async Task ListDatabases_IncludesTestDb_ExcludesTempdb()
    {
        var dbs = await _svc.ListDatabasesAsync(_src, default);

        Assert.True(dbs.IsSuccess, dbs.Error);
        Assert.Contains(dbs.Value, d => d.Name == _db && d.RecoveryModel == "FULL" && !d.IsSystem);
        Assert.DoesNotContain(dbs.Value, d => d.Name == "tempdb");
        Assert.Contains(dbs.Value, d => d.Name == "master" && d.IsSystem);
    }

    [SqlFact]
    public async Task FreshDatabase_HasNoChain()
    {
        var chain = await _svc.GetChainStateAsync(_src, _db, default);

        Assert.True(chain.IsSuccess, chain.Error);
        Assert.Equal(new SqlChainState(false, false), chain.Value);
    }

    [SqlFact]
    public async Task FullThenLog_BackupVerify_AndChainStarts()
    {
        var folder = await _svc.ResolveStagingFolderAsync(_src, "itest", default);
        Assert.True(folder.IsSuccess, folder.Error);

        var full = Path.Combine(folder.Value, SqlBackupFileName.Format(_db, SqlBackupType.Full, DateTime.Now));
        var percents = new List<int>();
        var backup = await _svc.BackupAsync(_src, _db, SqlBackupType.Full, full, new SyncProgress<int>(percents.Add), default);

        Assert.True(backup.IsSuccess, backup.Error);
        Assert.True(File.Exists(full));
        Assert.NotEmpty(percents);
        Assert.True((await _svc.VerifyAsync(_src, full, default)).IsSuccess);
        Assert.Equal(new SqlChainState(true, true), (await _svc.GetChainStateAsync(_src, _db, default)).Value);

        var log = Path.Combine(folder.Value, SqlBackupFileName.Format(_db, SqlBackupType.Log, DateTime.Now));
        var logResult = await _svc.BackupAsync(_src, _db, SqlBackupType.Log, log, null, default);
        Assert.True(logResult.IsSuccess, logResult.Error);

        File.Delete(full);
        File.Delete(log);
    }

    [SqlFact]
    public async Task Backup_OfMissingDatabase_FailsWithSqlErrorNumber()
    {
        var folder = (await _svc.ResolveStagingFolderAsync(_src, "itest", default)).Value;

        var result = await _svc.BackupAsync(_src, "cp_does_not_exist", SqlBackupType.Full, Path.Combine(folder, "x.bak"), null, default);

        Assert.True(result.IsFailure);
        Assert.Contains("[3013]", result.Error); // BACKUP DATABASE is terminating abnormally
    }

    [SqlFact]
    public async Task CheckDb_OnHealthyDatabase_Succeeds()
    {
        var result = await _svc.CheckDbAsync(_src, _db, default);
        Assert.True(result.IsSuccess, result.Error);
    }
}
```

- [x] **Step 3: Run tests to verify they fail**

Run (PowerShell): `$env:UNIBACKUP_TEST_SQL='localhost\SQL2022'; dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlServerBackupServiceTests"`
Expected: build FAILS: `The type or namespace name 'SqlServerBackupService' could not be found`.

- [x] **Step 4: Write minimal implementation**

`src/UniBackup.Infrastructure/Sql/SqlServerBackupService.cs`:
```csharp
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
            TrustServerCertificate = source.TrustServerCertificate,
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
            return new SqlChainState(!reader.IsDBNull(0), !reader.IsDBNull(1));
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
```

- [x] **Step 5: Run tests to verify they pass**

Run (PowerShell): `$env:UNIBACKUP_TEST_SQL='localhost\SQL2022'; dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~SqlServerBackupServiceTests"`
Expected: PASS (5 tests). Without the env var, all 5 are reported as Skipped.

- [ ] **Step 6: Commit**

```bash
git add src/UniBackup.Infrastructure tests/UniBackup.Tests/SqlServerBackupServiceTests.cs
git commit -m "feat(sql): SqlServerBackupService (list, chain state, staging, backup, verify, checkdb)"
```

---

### Task 7: Pipeline integration (+ double-finalisation fix)

**Files:**
- Modify: `src/UniBackup.Engine/Pipeline/BackupPipeline.cs`
- Create: `src/UniBackup.Engine/Pipeline/BackupPipeline.Sql.cs`
- Modify: `tests/UniBackup.Tests/PipelineAndResilienceTests.cs` (mock records the folder; constructor call)
- Test: add one `[SqlFact]` to `tests/UniBackup.Tests/SqlServerBackupServiceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  - `BackupPipeline(IArchiveService, IEnumerable<IStorageProvider>, INotificationService, WebhookNotificationService, ConfigurationService, SqlServerBackupService)`. The new parameter is **last**.
  - `ExecuteJobAsync(AppConfig config, BackupJob job, IProgress<BackupProgressInfo>? progress = null, CancellationToken ct = default, SqlBackupType sqlType = SqlBackupType.Full)`.
  - Remote layout for SQL: `<DestinationFolders.Resolve(...)>/<db>/<FULL|DIFF|LOG>/<file>`.

- [x] **Step 1: Write the failing test**

In `tests/UniBackup.Tests/PipelineAndResilienceTests.cs`, give `MockFailingStorageProvider` a folder recorder. Add the property and set it as the first line of `UploadAsync`:
```csharp
    public string? LastFolder { get; private set; }
    // first line inside UploadAsync:
        LastFolder = targetFolderOverride;
```

Append to `SqlServerBackupServiceTests` (add `using UniBackup.Engine.Pipeline; using UniBackup.Engine.Services; using UniBackup.Infrastructure.Compression; using UniBackup.Infrastructure.Notifications;`):
```csharp
    [SqlFact]
    public async Task Pipeline_LogRequestOnFreshDb_TakesFull_AndUploadsIntoDbTypeFolder()
    {
        var mock = new MockFailingStorageProvider { FailuresBeforeSuccess = 0 };
        var pipeline = new BackupPipeline(new HighSpeedZipArchiveService(), [mock], new EmailNotificationService(),
            new WebhookNotificationService(), new ConfigurationService(new CredentialProtector()), _svc);
        var job = new BackupJob
        {
            Name = "itest", Kind = "sqlserver", Destinations = ["mock"], CloudFolder = "itest",
            Sql = new SqlServerSource { Server = _src.Server, TrustServerCertificate = true, AllUserDatabases = false, Databases = [_db] }
        };

        var history = await pipeline.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Log);

        Assert.Equal("SUCCESS", history.Status);
        var run = Assert.Single(history.SqlDatabases);
        Assert.Equal(SqlBackupType.Full, run.BackupType);
        Assert.Equal(1, mock.AttemptCount);
        Assert.Equal($"itest/{SqlBackupFileName.RemoteSubfolder(_db, SqlBackupType.Full)}", mock.LastFolder);
    }
```
Note: like the existing pipeline tests, this writes a history entry through the real `ConfigurationService`.

- [x] **Step 2: Run test to verify it fails**

Run (PowerShell): `$env:UNIBACKUP_TEST_SQL='localhost\SQL2022'; dotnet test tests/UniBackup.Tests --filter "FullyQualifiedName~Pipeline_LogRequestOnFreshDb"`
Expected: build FAILS: `'BackupPipeline' does not contain a constructor that takes 6 arguments`.

- [x] **Step 3: Modify `BackupPipeline.cs`**

1. Add `using UniBackup.Infrastructure.Sql;`. Change `public class BackupPipeline` to `public partial class BackupPipeline`.
2. Add field `private readonly SqlServerBackupService _sql;`. Add constructor parameter `SqlServerBackupService sqlService` as the **last** parameter and assign `_sql = sqlService;`.
3. Change the `ExecuteJobAsync` signature to:
   ```csharp
   public async Task<RunHistoryRecord> ExecuteJobAsync(
       AppConfig config,
       BackupJob job,
       IProgress<BackupProgressInfo>? progress = null,
       CancellationToken ct = default,
       SqlBackupType sqlType = SqlBackupType.Full)
   ```
4. As the first statement inside `try {`, before `Log.Information("========== [JOB START]...`, add:
   ```csharp
            if (job.IsSqlServer)
            {
                await RunSqlJobAsync(config, job, sqlType, history, (phase, pct, msg) => Report(phase, pct, msg), ct);
                return history; // finally{} finalises
            }
   ```
5. **Double-finalisation fix:** in the three early-return blocks (missing source directory, `compressResult.IsFailure`,
   `activeProviders.Count == 0`), delete the line `await FinalizeRunAsync(config, job, history, sw.Elapsed);`
   and keep `return history;`. The `finally` block already finalises every run exactly once.
6. Replace the provider-filter statement with `var activeProviders = ActiveProviders(config, job);`.
7. Replace the whole `var retryPipeline = ...Build();` + `var uploadResult = await retryPipeline.ExecuteAsync(...)` block with:
   ```csharp
                var uploadResult = await UploadWithRetryAsync(provider, config, job, stagedArchiveFile, null, uploadProgress, ct);
   ```
8. Replace the retention folder line with:
   ```csharp
                        var folder = DestinationFolders.Resolve(provider.Name, config, job);
   ```
9. Add these two methods to the class (above `FinalizeRunAsync`):
   ```csharp
    private List<IStorageProvider> ActiveProviders(AppConfig config, BackupJob job) =>
        _storageProviders
            .Where(p => job.Destinations.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.IsEnabled(config))
            .ToList();

    private static async Task<Result<DestinationRunResult>> UploadWithRetryAsync(
        IStorageProvider provider, AppConfig config, BackupJob job, string localFile, string? folder,
        IProgress<(long uploadedBytes, long totalBytes)>? progress, CancellationToken ct)
    {
        var retry = new ResiliencePipelineBuilder<Result<DestinationRunResult>>()
            .AddRetry(new RetryStrategyOptions<Result<DestinationRunResult>>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<Result<DestinationRunResult>>().HandleResult(r => r.IsFailure)
            })
            .Build();

        return await retry.ExecuteAsync(async token =>
            await provider.UploadAsync(config, job, localFile, folder, progress, token), ct);
    }
   ```

- [x] **Step 4: Create `BackupPipeline.Sql.cs`**

```csharp
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Pipeline;

public partial class BackupPipeline
{
    private async Task RunSqlJobAsync(AppConfig config, BackupJob job, SqlBackupType requested,
        RunHistoryRecord history, Action<string, double, string> report, CancellationToken ct)
    {
        var sql = job.Sql;
        if (sql is null) { Fail(history, "SQL Server job has no 'sql' settings."); return; }

        report("Preflight", 5, $"Connecting to SQL Server {sql.Server}...");
        var databases = await _sql.ListDatabasesAsync(sql, ct);
        if (databases.IsFailure) { Fail(history, databases.Error!); return; }

        var (included, skipped) = SqlDatabaseSelector.Select(databases.Value, sql, requested);
        history.SqlDatabases.AddRange(skipped);
        if (included.Count == 0) { Fail(history, $"No databases matched this job's selection for a {requested} backup."); return; }

        if (ActiveProviders(config, job).Count == 0) { Fail(history, "No valid cloud destinations enabled for this job."); return; }

        var staging = await _sql.ResolveStagingFolderAsync(sql, job.Id, ct);
        if (staging.IsFailure) { Fail(history, staging.Error!); return; }
        SweepLeftovers(staging.Value); // the folder is private to this job; leftovers are from a crashed run

        var slice = 90.0 / included.Count;
        for (var i = 0; i < included.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            history.SqlDatabases.Add(await BackupOneDatabaseAsync(
                config, job, sql, included[i], requested, staging.Value, history, 5 + i * slice, slice, report, ct));
        }

        history.ArchiveName = $"{included.Count} database(s)";
        history.Status = SqlJobRules.OverallStatus(history.SqlDatabases, history.Destinations);
        if (history.Status != "SUCCESS")
        {
            history.ErrorMessage = string.Join("; ",
                history.SqlDatabases.Where(d => d.Status == "FAILED").Select(d => $"{d.Database}: {d.Message}")
                    .Concat(history.Destinations.Where(d => d.Status == "FAILED").Select(d => $"{d.Destination}: {d.ErrorMessage}")));
        }
        // ponytail: no retention for SQL jobs until Phase 3 (age-based delete would break restore chains).
        Log.Information("[SQL] Retention is not applied to SQL Server jobs yet (chain-aware retention ships in Phase 3).");
    }

    private async Task<SqlDatabaseRunResult> BackupOneDatabaseAsync(AppConfig config, BackupJob job, SqlServerSource sql,
        string db, SqlBackupType requested, string stagingFolder, RunHistoryRecord history,
        double basePct, double slice, Action<string, double, string> report, CancellationToken ct)
    {
        var result = new SqlDatabaseRunResult { Database = db, BackupType = requested };
        string? file = null;
        try
        {
            var chain = await _sql.GetChainStateAsync(sql, db, ct);
            if (chain.IsFailure) return Failed(result, chain.Error!);

            result.BackupType = SqlJobRules.EffectiveType(requested, chain.Value);
            if (result.BackupType != requested)
                Log.Warning("[SQL] {Db}: no base for a {Requested} backup, taking FULL instead", db, requested);

            result.FileName = SqlBackupFileName.Format(db, result.BackupType, DateTime.Now);
            file = Path.Combine(stagingFolder, result.FileName);

            var backupProgress = new Progress<int>(p =>
                report("Backing up", basePct + slice * 0.5 * p / 100, $"{db}: {result.BackupType} backup {p}%"));
            var backup = await _sql.BackupAsync(sql, db, result.BackupType, file, backupProgress, ct);
            if (backup.IsFailure) return Failed(result, backup.Error!);

            result.SizeBytes = new FileInfo(file).Length;
            history.ArchiveSizeBytes += result.SizeBytes;

            if (sql.Verify)
            {
                report("Verifying", basePct + slice * 0.55, $"{db}: RESTORE VERIFYONLY");
                var verify = await _sql.VerifyAsync(sql, file, ct);
                if (verify.IsFailure) return Failed(result, "Verify failed: " + verify.Error);
            }

            if (sql.RunCheckDb && result.BackupType == SqlBackupType.Full)
            {
                report("Checking", basePct + slice * 0.6, $"{db}: DBCC CHECKDB");
                var check = await _sql.CheckDbAsync(sql, db, ct);
                if (check.IsFailure) Failed(result, "DBCC CHECKDB reported problems: " + check.Error); // still upload: newest copy is kept
            }

            foreach (var provider in ActiveProviders(config, job))
            {
                var folder = $"{DestinationFolders.Resolve(provider.Name, config, job)}/{SqlBackupFileName.RemoteSubfolder(db, result.BackupType)}";
                report("Uploading", basePct + slice * 0.7, $"{db}: uploading to {provider.Name.ToUpperInvariant()}");
                var upload = await UploadWithRetryAsync(provider, config, job, file, folder, null, ct);
                history.Destinations.Add(upload.IsSuccess
                    ? upload.Value
                    : new DestinationRunResult { Destination = provider.Name, Status = "FAILED", ErrorMessage = $"{db}: {upload.Error}" });
            }
            return result;
        }
        finally
        {
            if (file != null && File.Exists(file))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Log.Warning(ex, "Could not delete staged backup {File}", file); }
            }
        }
    }

    private static void SweepLeftovers(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            try { File.Delete(f); }
            catch (Exception ex) { Log.Warning(ex, "Could not delete leftover staged backup {File}", f); }
        }
    }

    private static SqlDatabaseRunResult Failed(SqlDatabaseRunResult result, string message)
    {
        result.Status = "FAILED";
        result.Message = message;
        return result;
    }

    private static void Fail(RunHistoryRecord history, string message)
    {
        Log.Error("[SQL] {Message}", message);
        history.Status = "FAILED";
        history.ErrorMessage = message;
    }
}
```

- [x] **Step 5: Fix every existing constructor call**

Run: `grep -rn "new BackupPipeline(" src tests`
For each hit, append `new SqlServerBackupService(<the ICredentialProtector instance in scope>)` as the last argument.
Add `using UniBackup.Infrastructure.Sql;` where needed. DI-built pipelines are handled in Task 8.

- [x] **Step 6: Run the full test suite**

Run (PowerShell): `$env:UNIBACKUP_TEST_SQL='localhost\SQL2022'; dotnet test tests/UniBackup.Tests`
Expected: PASS. All existing tests are green, plus `Pipeline_LogRequestOnFreshDb_TakesFull_AndUploadsIntoDbTypeFolder`.

- [ ] **Step 7: Commit**

```bash
git add src/UniBackup.Engine/Pipeline tests/UniBackup.Tests
git commit -m "feat(sql): run SQL Server jobs through BackupPipeline; fix double finalisation on early failures"
```

---

### Task 8: Wire up CLI, service and UI

**Files:**
- Modify: `src/UniBackup.Cli/Program.cs` (the `--auto --job` branch + help text)
- Modify: `src/UniBackup.Service/Program.cs` (DI)
- Modify: the UI composition root. Find it with `grep -rn "BackupPipeline" src/UniBackup.UI`.

**Interfaces:**
- Consumes: `ExecuteJobAsync(..., sqlType:)`, `SqlServerBackupService`.
- Produces: `UniBackup.Cli.exe --auto --job <id> [--type full|diff|log]`.

- [x] **Step 1: CLI**

In the `--auto` / `--job` branch, before `var history = await pipeline.ExecuteJobAsync(...)`, add:
```csharp
                var typeIndex = Array.IndexOf(args, "--type");
                var sqlType = typeIndex >= 0 && typeIndex < args.Length - 1
                              && Enum.TryParse<SqlBackupType>(args[typeIndex + 1], ignoreCase: true, out var parsedType)
                    ? parsedType
                    : SqlBackupType.Full;
```
Change the call to `var history = await pipeline.ExecuteJobAsync(config, job, progress, sqlType: sqlType);`.
Add the help line after the existing `--auto --job <id>` line:
```csharp
        Console.WriteLine("  UniBackup.Cli.exe --auto --job <id> --type diff   SQL jobs: full (default) | diff | log");
```
Add `using UniBackup.Core.Models;` if it isn't already present.

- [x] **Step 2: Service DI**

In `src/UniBackup.Service/Program.cs`, after `builder.Services.AddSingleton<WebhookNotificationService>();`, add:
```csharp
builder.Services.AddSingleton<SqlServerBackupService>();
```
plus `using UniBackup.Infrastructure.Sql;`. `ICredentialProtector` is already registered, so DI resolves the constructor.

- [x] **Step 3: UI composition root**

Make the same registration (or constructor argument) wherever the UI builds `BackupPipeline`.

- [ ] **Step 4: Build and smoke-test**

Run: `dotnet build UniBackup.slnx -nologo -v q`
Expected: `0 Error(s)`.

Smoke test: add this job to the jobs file the CLI reads (`AppPaths.DefaultJobsFile`), with a destination that's configured:
```json
{ "id": "sqlsmoke", "name": "SQL smoke", "kind": "sqlserver", "destinations": ["s3"],
  "sql": { "server": "localhost\\SQL2022", "trust_server_certificate": true,
           "all_user_databases": false, "databases": ["master"] } }
```
Run: `dotnet run --project src/UniBackup.Cli -- --auto --job sqlsmoke --type full`
Expected: exit code 0. The bucket gets `<prefix>/master/FULL/master_FULL_<ts>.bak`, and the history entry lists `sql_databases[0].status = "SUCCESS"`.

- [ ] **Step 5: Commit**

```bash
git add src/UniBackup.Cli/Program.cs src/UniBackup.Service/Program.cs src/UniBackup.UI
git commit -m "feat(sql): CLI --type flag and DI registration for SQL Server jobs"
```

**Phase 1 exit criteria:** a SQL job runs from the CLI and from an existing schedule trigger, and
produces FULL/DIFF/LOG files per database in `db/TYPE/` folders on S3 and Drive. The 5 unit-test
classes and the 6 integration tests pass. The two pipeline bugs are fixed.

---

## Plan review (21 Nov 2026)

The plan, the blueprint and the built code were validated against each other and against
`localhost\SQL2022`. Rating out of 10, before → after the fixes below: feature coverage 9 → 9.5,
blueprint/plan agreement 5 → 9, technical soundness 6 → 8.5, phase order 6 → 8, built code 7.5 → 9,
safety net 5 → 8.5. **Overall 6.5 → 8.8.**

Status key: ✅ **Fixed** = done now, verified. 📝 **Plan fixed** = the plan/blueprint now says the
right thing; the work itself is built in the phase named. ⏳ **Open** = not addressed.

| # | Issue | Severity | Status | What was done |
|---|---|---|---|---|
| 1 | A DIFF/LOG could build on a FULL that never reached the cloud (upload failed, or another tool took it) | High | ✅ Fixed | msdb base check + `Uploaded` flag; see "Phase 1 follow-ups" |
| 1b | Two FULLs in the same second got the same file name (found by the new test) | High | ✅ Fixed | names already in the cloud are never reused |
| 2 | Named-pipe "zero-disk" streaming doesn't work (tested: OS error 231) | High | 📝 Plan fixed (Phase 3B) | replaced by parallel uploads, upload/backup overlap and `BACKUP TO URL` (SQL Server 2022+); named pipe listed as a non-goal |
| 3 | Phase 4 would delete CloudPacker's `CloudPacker_*` scheduled tasks | High | 📝 Plan fixed (Phase 4) | installer deletes only `UniBackup_*` tasks; coexistence added to Global Constraints |
| 4 | Encrypted backups unrecoverable if the server is lost (key sealed to the machine) | High | 📝 Plan fixed (Phase 3) | admin-held passphrase, key-check value, restore elsewhere asks for it |
| 5 | No version control | High | ✅ Fixed | git repo at the workspace root with `.gitignore` for build output, archives and credential/config files |
| 6 | Blueprint promised things the plan didn't build (zstd, streaming, parallel upload, heartbeat, Prometheus, `jobs.db`, `Get-UBJob`, cross-platform) | Medium | ✅ Fixed (docs) | each item given a phase or listed as a non-goal; blueprint rewritten to match; this plan is the source of truth |
| 7 | One job could run twice at once (service + Task Scheduler + UI) | Medium | ✅ Fixed | cross-process job lock file |
| 8 | Tests wrote to the real data folder | Medium | ✅ Fixed | `ConfigurationService(crypto, dataDirectory)`; tests use temp folders |
| 9 | SQL restore only arrived in Phase 6, after the UI | Medium | 📝 Plan fixed (Phase 6A) | CLI restore moved right after Phase 3B; build order added |
| 10 | Default SQL connection failed on a local self-signed certificate | Medium | ✅ Fixed | local servers trust their certificate automatically |
| 11 | Phase 2 SQLite file in `%ProgramData%`, away from the data folder | Medium | 📝 Plan fixed (Phase 2) | `UniBackup.db` goes in the data directory; jobs stay in `jobs.json` |
| 12 | MailKit security advisory | Low | ✅ Fixed | bumped to 4.18.1 |
| 13 | CloudPacker names left in plans and tests (`.cpenc`, `cp_drill_`, `Get-CPJob`, test env var) | Low | ✅ Fixed | renamed to `.ubenc`, `ub_drill_`, `Get-UBJob`, `UNIBACKUP_TEST_SQL` |
| 14 | READMEs claimed unbuilt features and unmeasured numbers | Low | ✅ Fixed (docs) | top-level README marks each feature built/planned; project README commands match the real `UniBackup.exe` |
| 15 | No UI to create SQL jobs until Phase 5 (`jobs.json` edited by hand) | Low | ⏳ Open | accepted for now; the Phase 5 job editor covers it. Ask if a minimal SQL form is wanted sooner |

## Phase 1 follow-ups (code fixes from the review): ✅ done

All fixed, with tests (78 green with `UNIBACKUP_TEST_SQL=localhost\SQL2022`).

| Problem | Fix | Test |
|---|---|---|
| A DIFF/LOG could build on a FULL that never reached the cloud: its upload failed, or another tool (maintenance plan, old SBM) took it. SQL Server's own chain state can't tell. | `ChainState` also reads the newest non-copy-only FULL and FULL-or-LOG device from `msdb.dbo.backupset`. `SqlJobRules.EffectiveType` promotes to FULL unless that file is one this job uploaded to every destination (`SqlDatabaseRunResult.Uploaded`). | `SqlRulesTests.EffectiveType_PromotesToFull_WhenBaseIsNotInTheCloud`, `SqlServerBackupServiceTests.Diff_IsTaken_OnlyWhenTheFullBaseReachedTheCloud` |
| Backup names have 1-second resolution, so two FULLs in one second shared a name (cloud overwrite + wrong chain match). | The pipeline never reuses a name that is already uploaded; it moves the timestamp forward a second. | same integration test |
| Service timer, Task Scheduler and the UI are separate processes and could run one job twice at once. | `ExecuteJobAsync` takes an exclusive lock file `<data dir>\locks\<jobId>.lock`; a second run is skipped (no history, no alert). | `PipelineAndResilienceTests.SecondRunOfSameJob_IsSkippedWhileFirstHoldsTheLock` |
| Pipeline tests wrote to the real `history.json`. | `ConfigurationService(crypto, dataDirectory)`; tests pass a temp folder. | all pipeline tests |
| Default SQL connection failed against a local instance's self-signed certificate. | `TrustServerCertificate` is on automatically for local servers (`SqlServerBackupService.IsLocalServer`); remote servers still validate. | `SqlServerBackupServiceTests.IsLocalServer` |
| MailKit advisory. | Bumped to 4.18.1. | build has no NU1902 |

---

# Phase 2: Scheduler v2 + SQLite history

**Goal:** Express SBM's cadences: *full daily at 01:00, diff every 4 h, log every 15 min between
08:00 and 20:00 on weekdays*. Never miss or double-run a job, and keep history fast once there are
about 100 runs a day.

**Design decisions**
- New `JobSchedule` v2 fields (old fields keep working). `full: {freq, time, days, day}`,
  `diff: {every_minutes, enabled}`, `log: {every_minutes, enabled}`,
  `window: {start, end, days}`, `missed_run_lookback_minutes`,
  `retry: {max_attempts, wait_minutes}`.
- Pure `ScheduleCalculator.Due(JobSchedule, JobRunState lastRuns, DateTimeOffset now) : SqlBackupType?`.
  Precedence is FULL > DIFF > LOG. A due FULL suppresses a same-tick DIFF/LOG. It catches up a
  missed FULL within the lookback, but never back-fills missed LOGs (the next log covers the gap).
  It is tested with `FakeTimeProvider` (package `Microsoft.Extensions.TimeProvider.Testing`, test project only).
- `Worker.cs` ticks every 15 s and asks the calculator. Last-run state is stored per job and type,
  so a 30 s poll can no longer skip or double-run. The cross-process job lock from the Phase 1
  follow-ups already stops a job overlapping itself; a due LOG that finds the job's FULL holding the
  lock is retried on the next tick instead of being reported as failed.
- Retry: a FAILED run re-queues after `wait_minutes`, up to `max_attempts`. Intermediate
  notifications are suppressed until the final attempt.
- History and run state move to SQLite at `<data directory>\UniBackup.db`, next to `config.json`
  (`ConfigurationService.DataDirectory`, so tests use their temp folder). Package `Microsoft.Data.Sqlite`.
  Tables: `runs`, `sql_database_runs`, `destination_runs`, `job_run_state`. Jobs stay in `jobs.json`
  (hand-editable and backward compatible).
- `job_run_state` stores, per job and database, the files uploaded to every destination. It replaces
  the `history.json` scan behind `SqlJobRules.EffectiveType`, which forgets a base once history passes
  its 1 000-run cap. On first start, import `history.json` and rename it to `history.imported.json`.
  Pruning uses `config.Backup.HistoryRetentionDays` (default 90) and runs in a daily maintenance tick.
- The folder-job Task Scheduler path stays until Phase 4.

**Acceptance tests:** calculator table tests covering intervals, window edges, DST spring-forward
and fall-back, month-end `day=31`, catch-up and precedence. Also a SQLite repository round-trip,
a history.json import, a pruning test, and a worker test proving no double run when a tick lands
twice in one minute.

---

# Phase 3: Retention, immutability, encryption

**Goal:** Delete old SQL backups without ever breaking a restore chain. Add ransomware-resistant
options and encrypt backups before upload.

**Design decisions**
- Add `Task<Result> DeleteAsync(AppConfig, string keyOrId, CancellationToken)` to `IStorageProvider`.
- Pure `SqlRetentionPlanner.Plan(IEnumerable<CloudArchiveItem> items, RetentionPolicy policy, DateTime now)`.
  Items are parsed with `SqlBackupFileName.TryParse`; anything unparsable is never touched.
  - Keep everything newer than `keep_days`.
  - Keep the newest FULL older than the cutoff when a kept DIFF/LOG depends on it (anchor).
  - Delete a DIFF/LOG only when its FULL is also deleted.
  - Never delete the only FULL of a database.
  - **GFS (new):** additionally keep N daily, N weekly and N monthly FULLs.
  - The output lists deletions with a reason (`AgedOut` / `Superseded`) and notices for anchors
    that were kept. The plan is logged before anything is deleted.
- S3 versioned buckets: when deleting, delete every version and delete marker of the key
  (`ListVersions` + `DeleteObject(versionId)`).
- **Object Lock (new):** an opt-in per S3 destination (`governance` | `compliance`, days). Uploads
  set `ObjectLockMode` and `RetainUntilDate`, and the planner skips objects that are still locked.
- **Multipart hygiene:** at the start of each run, `AbortMultipartUploads` older than 24 h under
  the job prefix. Validation warns if the bucket has no `AbortIncompleteMultipartUpload` lifecycle rule.
- **Encryption (new for SQL jobs):** optional AES-256-GCM, applied as a stream to the `.bak` before
  upload, producing `.bak.ubenc`. The format is a header with version, salt and nonce, followed by
  chunked GCM (64 KB chunks, each chunk's nonce derived from the base nonce and a counter, and a
  final-chunk flag in the AAD). The key comes from a job passphrase through PBKDF2-SHA256 with
  600 000 iterations (`Rfc2898DeriveBytes.Pbkdf2`). Only the standard library is used: `AesGcm`.
  `SqlBackupFileName` accepts the `.ubenc` suffix.
- **Key recovery:** the admin types the passphrase; it is never generated silently, and the UI says
  to record it somewhere other than this server. The server keeps it sealed (DPAPI) so scheduled runs
  can encrypt, plus a key-check value so a wrong passphrase is rejected before a restore starts.
  Restoring on another machine asks for the passphrase. Without it, encrypted backups cannot be
  decrypted, by design. Phase 9 config export carries it under the export passphrase.

**Acceptance tests:** planner tests for a chain with expired FULLs and live LOGs, an orphan DIFF, a
single-FULL database, GFS buckets, locked objects and unparsable names. Also an encryption
round-trip, tamper detection (one flipped byte fails) and truncation detection.

---

# Phase 3B: Speed (parallel uploads, direct-to-cloud SQL backups)

**Goal:** Shorten the backup window. This is the "faster than SBM" part of the product.

**Design decisions**
- **Parallel uploads:** a staged file goes to every active destination at once (`Task.WhenAll` over
  `UploadWithRetryAsync`), for folder and SQL jobs. Progress is reported per destination.
- **Overlap:** the next database's `BACKUP` starts while the previous file uploads, with at most two
  staged files at a time, so staging disk use stays bounded.
- **Direct-to-cloud mode (opt-in per SQL job, S3-compatible destinations only):**
  `BACKUP DATABASE @db TO URL = 's3://<endpoint>/<bucket>/<prefix>/<file>'` with a per-job
  `CREATE CREDENTIAL` built from the sealed S3 keys. SQL Server uploads the backup itself; nothing is
  staged locally. Requires SQL Server 2022 or later. Large databases are striped across several URLs
  (stripe count chosen from database size). Verified with `RESTORE VERIFYONLY FROM URL`. Google Drive
  and other non-S3 destinations keep staged mode. Confirm current part-size and stripe limits against
  Microsoft's docs when the phase starts.
- **Rejected: writing the backup to a named pipe.** Tested on `localhost\SQL2022` on 21 Nov 2026:
  SQL Server connects to the pipe, reopens it and fails with OS error 231 ("All pipe instances are
  busy"). Microsoft doesn't support it. If streaming is ever needed for older versions or non-S3
  destinations, the supported API is VDI (Virtual Device Interface), a separate, larger piece of work.
- **zstd is a measure-first decision.** Native `COMPRESSION` already compresses SQL backups. zstd is
  only adopted (package `ZstdSharp.Port`) for folder archives or for editions without native
  compression (Express), and only if it beats today's output by at least 15 % in size or time on real
  data.

**Acceptance tests:** two slow mock destinations finish in about the time of the slower one, not the
sum; the overlap never holds more than two staged files; a direct-to-cloud integration test gated by
`UNIBACKUP_TEST_S3` (MinIO or a test bucket) backs up, verifies from URL, restores and compares a row
count.

---

# Phase 4: Service-owned engine + local API

**Goal:** One executor. The Windows service schedules and runs everything, and the UI, CLI and
PowerShell become thin clients with live progress. This replaces both the UI running jobs
in-process and the per-job Task Scheduler entries.

**Design decisions**
- Kestrel inside `UniBackup.Service` listens only on the named pipe `\\.\pipe\UniBackup`
  (`ListenNamedPipe`). The pipe ACL allows only `BUILTIN\Administrators` and `NT AUTHORITY\SYSTEM`,
  using `NamedPipeTransportOptions.PipeSecurity`. That is SBM's admin-only rule, enforced by the OS.
- Minimal API (JSON), mirroring the operations in SBM README §8 that UniBackup needs: jobs CRUD,
  `POST /jobs/{id}/run?type=`, `POST /runs/{runId}/cancel`, `POST /runs/cancel-all`, `GET /history`,
  `GET/PUT /settings`, `POST /destinations/{name}/test`, `POST /sql/test-connection`,
  `GET /sql/databases`, `GET /health`.
- `GET /events` is a Server-Sent Events stream of `BackupProgressInfo` plus job-state changes. It
  replaces WCF duplex callbacks.
- One `UniBackup.Client` class (in `UniBackup.Core`, using `HttpClient` over
  `NamedPipeClientStream` through `SocketsHttpHandler.ConnectCallback`) is shared by the UI and CLI.
- Shutdown: stop accepting runs, signal cancellation, wait up to 120 s, persist state.
- `--install` also runs `sc failure` (restart after 60 s / 180 s / 360 s) and deletes UniBackup's own
  `UniBackup_*` scheduled tasks. It never touches `CloudPacker_*` tasks: CloudPacker may still be
  running on the same server. Task Scheduler code is removed.
- Maintenance tick: prune history, sweep orphaned staging folders, refresh OAuth tokens so they
  don't expire while idle, and trim logs.

**Acceptance tests:** an API test using `WebApplicationFactory`-style in-memory hosting. A pipe ACL
test confirms a non-admin token is refused (manual check documented). A client round-trip test, an
SSE ordering test and a cancel test.

---

# Phase 5: Modern UI + motion

**Goal:** A UI that is clearly better than SBM's Telerik WPF: faster to scan, live, accessible,
with purposeful motion. **When this phase starts, invoke the `impeccable` skill** (the user's
CLAUDE.md requires it for UI work), then write the step-level plan.

**Screens (fewest that cover the features)**
1. **Dashboard**: RPO tiles per database (green / amber / red against target), last 24 h runs
   timeline, storage per destination, next scheduled runs.
2. **Jobs**: one list for folder, IIS and SQL jobs. Each card has a kind icon, a status ring and
   next/last run. Bulk actions as today.
3. **Job editor**: a stepped panel (Source → Schedule → Destinations → Options). The SQL source
   step has a Test connection button and a live database picker (state, recovery model, size,
   last full) with include/exclude wildcards previewed live. The schedule step shows a visual
   week-strip of when FULL/DIFF/LOG will fire.
4. **Run monitor**: per-database rows with phase chips (Backing up → Verifying → Uploading) and
   bytes/s, fed by SSE.
5. **History**: filter by job, type, status and date. Drill into per-database and per-destination
   results. CSV export.
6. **Restore** (screens for Phase 6).
7. **Settings**: destinations, notifications, security, service status.

**Visual system**
- WPF .NET 9 Fluent theme (`ThemeMode="System"`) for light/dark/system, accent colour from Windows,
  and Mica backdrop on Windows 11 with a solid fallback.
- Tokens live in one `Theme.xaml`: 4-pt spacing scale, type ramp (Segoe UI Variable: 12/14/16/20/28),
  semantic colours (`Success`, `Warning`, `Danger`, `Info`) verified at ≥ 4.5:1 contrast in both themes.
- The existing 1 017-line `App.xaml` styles are folded into the token file. Nothing is kept twice.

**Motion system (the requested animations, kept purposeful)**
- Durations: 120 ms (hover/press), 200 ms (enter/exit), 320 ms (page transitions). One easing:
  `CubicEase EaseOut`. Exits are 25% faster than entries.
- Animations: staggered card entrance (30 ms step, capped at 8 cards); page cross-fade plus a 12 px
  slide; status ring sweep while running; success check draw-on at completion; count-up for
  dashboard numbers; skeleton shimmer while data loads; a subtle pulse on RPO tiles in breach.
- Reduced motion: when `SystemParameters.ClientAreaAnimation` is false, every duration becomes 0
  (one `Motion.Duration(...)` helper reads it). No animation blocks input.
- Accessibility: full keyboard paths, `AutomationProperties.Name` on every interactive element,
  visible focus rings, and a high-contrast theme honoured.

**Acceptance tests:** extend `UiStartupXamlTests` so every view loads. View-model tests per screen
use a fake `UniBackup.Client`, and one test checks motion durations are 0 under reduced motion.
A manual check runs the `impeccable` audit pass before the phase is called done.

---

# Phase 6: Restore + restore drills (6A: engine + CLI, built after 3B; 6B: wizard + drills, after Phase 5)

**Goal:** Restore any database to any moment from the cloud, and prove on a schedule that backups
actually restore.

**Design decisions**
- `SqlRestorePlanner.Plan(items, database, targetTime)` (pure) picks the newest FULL at or before
  the target, the newest DIFF between that FULL and the target, and every LOG after the FULL/DIFF
  up to and including the first LOG past the target. It is LSN-validated after download using
  `RESTORE HEADERONLY`.
- `SqlServerRestoreService`:
  - `RESTORE FILELISTONLY` builds the `MOVE` clauses (restore under a new name, relocate files to
    the instance default data/log paths).
  - Restores run `WITH NORECOVERY`, then `RECOVERY`, with `STOPAT` on the last LOG.
  - Overwrite needs an explicit `replace: true` plus typing the database name in the UI.
- **6A ships** `SqlRestorePlanner`, `SqlServerRestoreService` and
  `UniBackup.exe --restore --job <id> --db <name> [--at <time>] [--as <newName>]`, so SQL backups can be
  restored from the cloud before the new UI exists. **6B** adds the wizard UI and the drills below.
- Download goes through `IStorageProvider.DownloadAsync` into the staging folder, and decryption
  (`.ubenc`) streams during download.
- **Restore drills (new):** a per-job schedule restores the latest chain to `ub_drill_<db>`, runs
  `DBCC CHECKDB`, records RTO (wall time) and the result in history, then drops the database. A
  failure alerts through the Phase 8 notifications.

**Acceptance tests:** planner tests (gaps in the log chain, a target before the first FULL, DIFF
selection). Integration tests on `localhost\SQL2022`: full + log, then restore to the mid-point
time and assert a row inserted after the mid-point is absent. A drill end to end.

---

# Phase 7: Destination parity

**Goal:** Cover SBM's destination list, implemented as plain `IStorageProvider`s.

| Provider | Library | Notes |
|---|---|---|
| S3-compatible (one provider + presets: Wasabi, Backblaze B2, IDrive e2, Google Cloud Storage interop, Cloudflare R2, MinIO, custom) | AWSSDK.S3 (existing) | endpoint, region, path-style, storage class, SSE |
| Azure Blob | `Azure.Storage.Blobs` | block upload, access tier |
| OneDrive / OneDrive for Business / SharePoint | `Microsoft.Graph` + existing MSAL | resumable upload session |
| Dropbox | `Dropbox.Api` | upload session |
| Box | `Box.V2.Core` | chunked upload |
| FTP / FTPS | `FluentFTP` | explicit TLS by default; plain FTP warns |
| SFTP | `SSH.NET` | key or password, host-key pinning |
| WebDAV | `HttpClient` | no extra package |
| Local / UNC folder | stdlib | optional credentials via `WNetAddConnection2` |

- **Shared contract test suite:** an abstract `StorageProviderContractTests` class covering
  upload, list, download, delete, folder creation, a 0-byte file and a 5 GB sparse file. Each
  provider subclasses it, and each subclass is gated by its own env var.
- **Per-destination routing:** `backup_types` (e.g. logs only to fast storage) and
  `is_fallback` (used only when every primary destination fails).
- **Global HTTP proxy** settings apply to every HTTP-based provider.
- A **transfer stall watchdog** aborts and retries an upload when no bytes move for N minutes.

---

# Phase 8: Notifications, reports, RPO

- Trigger matrix per job and per webhook: {FULL, DIFF, LOG} × {success, warning, failure}. The
  defaults are failures plus warnings only.
- Templates use `{job}`, `{status}`, `{type}`, `{databases}`, `{duration}`, `{size}`, `{server}`,
  `{errors}` placeholders in subject, body and webhook payload. Teams, Slack and Discord presets
  stay.
- Desktop toast from the tray app (fed by SSE), with click-through to the run.
- **Scheduled HTML email reports** replace SBM's 12 Telerik report types with the 7 that carry
  information: job status, destinations and storage, upcoming schedule, database sizes and
  recovery models, last FULL per database, SQL Server versions, disk space.
- **RPO breach alerts (new):** each database has `rpo_minutes`. The maintenance tick alerts once
  per breach and once on recovery.
- **Heartbeat (new):** after every scheduled run, ping a configured URL (Healthchecks.io, Uptime Kuma,
  or any GET endpoint), with `/fail` on failure. The monitor alerts when pings stop, which catches a
  dead server or a stopped service.
- **Metrics (optional, off by default):** a `/metrics` endpoint in Prometheus text format on a
  localhost TCP port, for teams that already run Prometheus. The Phase 4 API is pipe-only, which
  Prometheus can't scrape.
- MailKit is already bumped to 4.18.1.

---

# Phase 9: Automation & admin

- **Pre/post scripts:** SQL, batch or PowerShell, with a timeout, a stop-on-error option and a
  full-backups-only option. Output is captured into the run log.
- **Run-as:** an optional Windows account for UNC staging (stored sealed). Impersonation is used
  only for file access, never for the SQL connection unless Windows auth is selected.
- **Config export/import:** one file holding settings, jobs and destinations, with secrets
  re-encrypted under an export passphrase (PBKDF2-SHA256 600 000 + AES-GCM, the same code as
  Phase 3). This moves a configuration between servers, which DPAPI-sealed files cannot.
- **PowerShell:** the CLI gains `--json` output. `UniBackup.psm1` provides `Get-UBJob`,
  `Start-UBJob -Type -Wait`, `Stop-UBRun`, `Get-UBHistory`, `Export-UBConfig` and
  `Import-UBConfig`, each wrapping the CLI.

---

## Self-review notes

- **Spec coverage:** every row of the parity map has a phase. Every "beyond SBM" feature has a
  phase. Non-goals are listed explicitly.
- **Type consistency (Phase 1):** these names are used identically in every task:
  `SqlBackupType`, `SqlServerSource`, `SqlDatabaseRunResult`, `SqlDatabaseInfo`, `SqlChainState`,
  `SqlBackupFileName.{Format,RemoteSubfolder,TryParse}`, `SqlDatabaseSelector.Select`,
  `SqlJobRules.{EffectiveType,OverallStatus}`, `DestinationFolders.Resolve`, the
  `SqlServerBackupService` methods, `ActiveProviders`, `UploadWithRetryAsync`, `RunSqlJobAsync`.
- **Known limits to revisit:**
  - An Availability Group or failover-cluster listener reports a virtual `MachineName`, so it is
    treated as remote and needs a UNC staging folder (Phase 6 note).
  - A copy-only LOG request on a database with no chain is promoted to a copy-only FULL.
  - A job with `copy_only` on never becomes a DIFF base, so its DIFF requests always take a FULL.
  - Until Phase 2, the "uploaded base" check reads `history.json`, which keeps the newest 1 000 runs.
    A base older than that forces a FULL (safe, just larger).
