# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

UniBackup Pro: .NET 9 / C# 13 Windows backup tool. It combines CloudPacker (folder and IIS-site backups to S3 / Google Drive) with SQL Server FULL/DIFF/LOG backups, with the goal of exceeding SQL Backup Master on features, speed and UI. This is the active project. The original CloudPacker codebase it was copied and renamed from is archived as `../Cloud Packer New.rar`.

The git repository root is the parent folder (`Universal Backup Tool/`). Its `.gitignore` excludes build output, archives and every `config.json` / `jobs.json` / `*credentials*.json` / `*token*.json`. `config/` in this folder holds real credentials, so never force-add it.

## Commands

Run everything from this folder (`UniBackup Pro/`). The solution file is `UniBackup.slnx`.

```powershell
dotnet build UniBackup.slnx
dotnet test UniBackup.slnx                                                    # SQL integration tests skip unless UNIBACKUP_TEST_SQL is set
$env:UNIBACKUP_TEST_SQL='localhost\SQL2022'; dotnet test UniBackup.slnx      # include them (needs sysadmin on that instance)
dotnet test UniBackup.slnx --filter "FullyQualifiedName~SqlRulesTests"        # one class
dotnet test UniBackup.slnx --filter "FullyQualifiedName~SqlRulesTests.OverallStatus"
.\publish_release.bat   # self-contained single-file build to dist\UniBackup_Release\UniBackup.exe (ends with `pause`)
```

There is no linter.

## How the shipped binary works

`publish_release.bat` publishes only **`src/UniBackup.UI`** (`AssemblyName=UniBackup`), and `UniBackup.UI/Program.cs` picks a mode from its arguments:

- `--service-worker`: runs as the Windows service host (Generic Host + `CloudPackerWorker` from the Service project). The service is named `UniBackup`, and `--service install` registers it with this flag in its binPath.
- any other arguments: CLI mode (`--auto [--job <id> [--type full|diff|log]]`, `--validate`, `--list-jobs`, `--service install|uninstall|start|stop|status`). It attaches to the parent console via `AttachConsole`. Exit codes: 0 success, 1 failure, partial = `config.Schedule.ExitOnPartial`.
- no arguments: WPF app. Login dialog (unless `security.require_login` is false), then `MainWindow`.

`UniBackup.Cli` and `UniBackup.Service` still build as standalone executables with their own `Program.cs`, but the release does not use them.

**The object graph is wired by hand in several places.** If you add a dependency to `BackupPipeline` or another service, update all of them:
- `src/UniBackup.UI/Program.cs`: `RunWindowsServiceWorker` (DI container) and `RunCliModeAsync` (manual `new`)
- `src/UniBackup.UI/MainWindow.xaml.cs` (the GUI builds and runs its own pipeline in-process)
- `src/UniBackup.Cli/Program.cs`
- `src/UniBackup.Service/Program.cs`

## Architecture

Projects depend in this order: `Core` (net9.0, no packages) → `Infrastructure` (SqlClient, AWS, Google Drive, MailKit, IIS, Task Scheduler, Serilog) → `Engine` → `Service` / `Cli` / `UI`. The UI project references Service, and the test project references the UI project, so the XAML tests (`UiStartupXamlTests`) build the real windows.

- **`Engine/Pipeline/BackupPipeline.ExecuteJobAsync`** runs one job. It first takes an exclusive lock file, `<data dir>\locks\<jobId>.lock`, because the service timer, Task Scheduler and the UI are separate processes. If another process holds it, the run is skipped (status `FAILED`, no history, no alert). The run itself is `RunJobAsync`, whose `finally` block calls `FinalizeRunAsync` (appends history, updates `jobs.json`, sends notifications) exactly once per run, so early-exit branches only `return history`. `BatchBackupRunner` runs every enabled job in sequence.
  - **Folder jobs** (`kind` missing or `"folder"`): preflight, zip (`IArchiveService`), upload to each `IStorageProvider` with a Polly retry, then age-based retention.
  - **SQL jobs** (`"kind": "sqlserver"` plus a `sql` block): `BackupPipeline.Sql.cs`, a partial class. It lists the databases, then `SqlDatabaseSelector` picks which to include and which to skip. For each database `SqlJobRules.EffectiveType` promotes a DIFF/LOG to FULL unless SQL Server has a chain **and** the base it would build on (the newest non-copy-only FULL, or FULL-or-LOG, in `msdb.dbo.backupset`) is a file this job uploaded to every destination (`SqlDatabaseRunResult.Uploaded` in history). That catches a base whose upload failed and one taken by another tool. File names have 1-second resolution, so a name already in the cloud is never reused. SQL Server then writes the `.bak`/`.trn`. The file goes to the instance's own default backup path under `UniBackup\<jobId>`, so no ACL changes are needed. A remote instance needs a UNC staging folder. The file is then verified, uploaded to `<base>/<db>/<FULL|DIFF|LOG>/` and deleted locally. All T-SQL lives in `Infrastructure/Sql/SqlBackupText` and takes `@db`/`@path` parameters, never concatenated identifiers.
- **Destination folders** are resolved only in `Core/Common/DestinationFolders.Resolve`: the job's `cloud_folder`, else the config prefix, else the per-server default. It never returns the bucket root. Providers receive any sub-path through `UploadAsync`'s `targetFolderOverride`.
- **Scheduling** has two separate mechanisms. `Service/Worker.cs` polls every 30 s and fires when the hour and minute match a job's schedule or the global nightly `config.Schedule`. Alternatively, Windows Task Scheduler (`WinTaskSchedulerService`) creates tasks named `BackupJob.TaskNameFor(id)` (`UniBackup_<id>`) that run `UniBackup.exe --auto`.
- **State** is JSON files in the data directory from `AppPaths.ResolveDataDirectory()`. That is the path in `%ProgramData%\UniBackup\home.txt` if the file exists, else `C:\Users\UniBackup`, else `%LocalAppData%\UniBackup`. It is separate from CloudPacker's `C:\Users\CloudPacker`. The files are `config.json`, `jobs.json`, `history.json`, `logs/`, plus the Google Drive OAuth files `gdrive_credentials.json` / `gdrive_token.json`. All reads and writes go through `Engine/Services/ConfigurationService`.
- **Secrets** go only through `ICredentialProtector.Seal`/`Unseal` (`Infrastructure/Security/CredentialProtector`: AES-256-GCM + DPAPI). It still unseals legacy Python-era `enc:` 3DES values so that old configs keep loading.

## Conventions that must hold

- UniBackup must be able to run next to an installed CloudPacker. Never reuse CloudPacker's service name, data directory, scheduled-task names or log file names.
- JSON is snake_case, set with `[JsonPropertyName]` on every model property. Config and job files from CloudPacker and the legacy Python tool must keep loading, so give new fields defaults and make them optional.
- Run status strings are exactly `"SUCCESS"`, `"PARTIAL"`, `"FAILED"`. Per-database SQL results also use `"SKIPPED"`.
- Tests pass a temp folder to `new ConfigurationService(crypto, tempDir)`, so history, jobs and job locks go there instead of the real data directory. Use a mock `IStorageProvider` like `MockFailingStorageProvider` in `PipelineAndResilienceTests.cs`, never a real cloud provider.
- Keep `SqlDatabaseRunResult.Uploaded` true only when every destination received the file. The chain check depends on it, and a wrong `true` makes cloud backups unrestorable.
- Connections to a local SQL Server trust its certificate automatically (`SqlServerBackupService.IsLocalServer`). Remote servers still validate unless `trust_server_certificate` is set.
- Add a NuGet package only when the phase being implemented names it.
- Never reuse SQL Backup Master's name, branding, wording or licensing. Parity means matching its behaviour with an independent implementation.
- Shared settings come from `Directory.Build.props`: `Nullable` enabled, `ImplicitUsings`, `LangVersion 13.0`. Target framework is `net9.0-windows` everywhere except `UniBackup.Core` (`net9.0`).

## Plans

- `docs/superpowers/plans/2026-11-21-sql-backup-merge.md` is the **source of truth** for scope and order. Tick its checkboxes as steps finish. Its "Plan review (21 Nov 2026)" section lists every review issue with its status (✅ fixed, 📝 plan fixed for a later phase, ⏳ open). Update that table when a 📝 or ⏳ item is built.
- `../sql_backup_modern_architecture_plan.md` is the product blueprint: the feature comparison against SQL Backup Master, the UI and motion design (theme tokens, animations, the timeline scrubber). It must agree with the step plan; update both together.
- **Build order:** 1 → 2 → 3 → 3B → 6A → 4 → 5 → 6B → 7 → 8 → 9. **Phase 1 is done; Phase 2 (scheduler v2 + SQLite history) is next.** Phases 2–9 give scope and design only, and each gets its own step-level plan when it starts.
- Phase 3B is the speed work: parallel uploads, overlapping the next backup with the current upload, and opt-in direct-to-cloud SQL backups (`BACKUP TO URL`, SQL Server 2022+). Backing up to a named pipe was tested and rejected (SQL Server fails with OS error 231). zstd is adopted only if measurements justify it.
- SQL jobs are never pruned by retention until Phase 3 builds chain-aware retention. Age-based deletion would break restore chains.

`../SQL Backup Master/` is the evidence base: a static reverse-engineering write-up (`README.md`, `data/*.facts.json`) of the vendor product. Its `_vendor/` folder holds third-party binaries and is not source code.
