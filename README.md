# UniBackup .NET (v2.0)

A high-performance, modern .NET 9 (C# 13) backup automation and cloud disaster recovery platform for Windows servers. UniBackup compresses large folders (150 GB+) into streaming zip archives, uploads them to **Amazon S3** and/or **Google Drive**, enforces retention policies, alerts administrators via email & webhooks, and provides instant 1-click cloud recovery.

---

## 🚀 Key Advantages Over the Legacy Python Version

| Feature | Legacy Python (`backup_to_s3_gui.py`) | UniBackup .NET 9 |
|---|---|---|
| **Performance** | Single-threaded GIL; slow CPU-bound zip compression | Multi-core parallel streaming with pooled memory buffers (`ArrayPool<byte>`) |
| **Locked Files** | Crashes or skips files locked by IIS / databases | **VSS (Volume Shadow Copy)** integration for in-use and locked files |
| **IIS Integration** | Parses console output of `appcmd.exe` | Direct API integration via native `Microsoft.Web.Administration` |
| **Windows Scheduling** | Spawns `schtasks.exe` via shell string concatenation | Direct Win32 / COM `TaskScheduler` API |
| **Resilience & Retries** | Basic retry loops | **Polly v8** resilience pipelines with jittered exponential backoff |
| **Security** | Deprecated 3DES with static key | **AES-256-GCM** and **Windows DPAPI** (`ProtectedData`) + seamless 3DES backward unseal |
| **Alerts & Webhooks** | SMTP / Gmail / M365 Email only | **Microsoft Teams**, **Slack**, and **Discord** Webhooks + MailKit SMTP/OAuth |
| **Deployment Footprint** | PyInstaller bundle (~150MB+ uncompressed), `%TEMP%` extraction risks | Self-contained, single-file native `.exe` with zero runtime dependencies |

---

## 📁 Architecture & Solution Structure

```
UniBackup Pro/
├── src/
│   ├── UniBackup.Core/             # Domain entities, enums, interfaces, utilities
│   │   ├── Models/                   # BackupJob, AppConfig, RunHistoryRecord, ServerIdentity, IisSiteInfo
│   │   ├── Interfaces/               # IStorageProvider, IArchiveService, ISchedulerService, INotificationService
│   │   └── Common/                   # Result<T>, AppPaths, FormattingUtils
│   │
│   ├── UniBackup.Infrastructure/   # Cloud SDKs, OS APIs, Task Scheduler, MailKit, Serilog
│   │   ├── Storage/                  # AwsS3StorageProvider (AWSSDK.S3), GoogleDriveStorageProvider
│   │   ├── Compression/              # HighSpeedZipArchiveService
│   │   ├── Windows/                  # IisSiteDiscoveryService, WinTaskSchedulerService
│   │   ├── Notifications/            # EmailNotificationService, WebhookNotificationService
│   │   ├── Security/                 # CredentialProtector (AES + DPAPI + legacy 3DES)
│   │   └── Logging/                  # LoggingSetup (daily rotating file + console)
│   │
│   ├── UniBackup.Engine/           # Backup execution pipeline, batch runner, recovery, diagnostics
│   │   ├── Pipeline/                 # BackupPipeline, BatchBackupRunner
│   │   └── Services/                 # ConfigurationService, RecoveryService, ServerDiagnosticsService
│   │
│   ├── UniBackup.Cli/              # Standalone CLI project (not shipped; UniBackup.exe has the same CLI mode)
│   │   └── Program.cs                # Arguments: --auto, --job <id>, --type, --validate, --list-jobs
│   │
│   └── UniBackup.UI/               # WPF app; publishes the single UniBackup.exe (GUI + CLI + service host)
│       ├── ViewModels/               # MainViewModel, JobCardViewModel, NewBackupViewModel
│       └── Views/                    # MainWindow, NewBackupDialog
│
├── tests/
│   └── UniBackup.Tests/            # xUnit automated test suite (crypto, compression, models)
│
├── UniBackup.slnx                  # Solution file
└── publish_release.bat               # Single-file self-contained publish script
```

---

## 🛠️ Execution Modes: Windows Service vs. Task Scheduler

UniBackup allows the operator to choose their preferred execution model based on server environment and operational requirements:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        CHOOSE YOUR EXECUTION MODEL                     │
│                                                                        │
│  [OPTION A]  Windows Background Service (Recommended for 24/7 Servers) │
│  • Runs continuously in the background under NT AUTHORITY\SYSTEM.      │
│  • Self-healing: automatically restarts upon system reboot or failure. │
│  • Evaluates schedules in real-time without task scheduler overhead.   │
│                                                                        │
│  [OPTION B]  Windows Task Scheduler (Recommended for Low-Memory VMs)   │
│  • Zero memory footprint when idle (process only starts when due).     │
│  • Fires `UniBackup.exe --auto` via Windows Task Scheduler.          │
│                                                                        │
│  [OPTION C]  Interactive Desktop UI & System Tray                      │
│  • Card-based dashboard with real-time visual progress & log viewer.   │
│  • 1-click cloud recovery and instant IIS website import.              │
└────────────────────────────────────────────────────────────────────────┘
```

### Windows Service Management Commands

The service can be managed via GUI (Settings → Automation & Execution Engine) or via CLI:

```powershell
# Check current service state (Running / Stopped / Not Installed):
UniBackup.exe --service status

# Install as Windows Service (run in elevated prompt):
UniBackup.exe --service install

# Start / Stop the background service:
UniBackup.exe --service start
UniBackup.exe --service stop

# Uninstall the service:
UniBackup.exe --service uninstall
```

The service is named `UniBackup`, so it can be installed next to an existing CloudPacker service.

---

## 🛠️ CLI Headless Commands (Windows Task Scheduler)

`UniBackup.exe` runs headless whenever it gets arguments:

```powershell
# Run all enabled backup jobs sequentially (recommended for nightly batch tasks):
UniBackup.exe --auto

# Run a specific backup job by ID:
UniBackup.exe --auto --job 7a9f2b1c

# SQL Server jobs: choose the backup type (full is the default)
UniBackup.exe --auto --job sqlprod --type log

# Run server diagnostics & connection tests (outputs colored table with exit code):
UniBackup.exe --validate

# List all configured jobs, schedules, and targets:
UniBackup.exe --list-jobs
```

If the same job is already running in another UniBackup process (the service, a scheduled task or the UI), the second run is skipped with a clear message instead of running twice.

---

## 🗄️ SQL Server Jobs

A SQL Server job is a `jobs.json` entry with `"kind": "sqlserver"` and a `sql` block (until the Phase 5 UI editor, jobs are added by editing the file):

```json
{ "id": "sqlprod", "name": "SQL production", "kind": "sqlserver", "destinations": ["s3"],
  "sql": { "server": "localhost\\SQL2022", "all_user_databases": true, "exclude_patterns": ["*_archive"],
           "verify": true, "run_checkdb": false } }
```

- SQL Server writes each `.bak` / `.trn` into its own default backup folder (no permission changes needed), UniBackup verifies it with `RESTORE VERIFYONLY`, uploads it to `<folder>/<database>/FULL|DIFF|LOG/` and deletes the local copy.
- A DIFF or LOG is taken only when the backup it builds on is already in **every** destination. If that base is missing, its upload failed, or another tool backed the database up in between, UniBackup takes a FULL instead, so the cloud copy is always restorable.
- Offline databases, and LOG backups of SIMPLE-recovery databases, are skipped and reported.
- A local SQL Server's self-signed certificate is trusted automatically. For a remote server, set `"trust_server_certificate": true` only if you accept its certificate, and use a UNC `staging_folder` both machines can reach.
- SQL backups are not pruned by retention yet (chain-aware retention arrives in Phase 3).

### Exit Codes:
- `0`: All backup jobs / diagnostics passed successfully.
- `1`: Failure occurred (one or more jobs failed, or missing source directory).
- `2`: Partial success (e.g. S3 succeeded but Google Drive timed out).

---

## 🔒 Backward Compatibility with Existing Configurations

UniBackup .NET automatically detects and imports existing configuration files from previous deployments:
1. `config.json`: Automatically unseals passwords encrypted with legacy Python 3DES (`enc:...`) and re-saves securely.
2. `jobs.json`: Loads existing job definitions, schedules, and destination settings.
3. `history.json`: Loads run logs for cards and summary analytics.
4. `gdrive_credentials.json` & `gdrive_token.json`: Reuses existing OAuth desktop client secrets and tokens without requiring re-authentication.

UniBackup keeps its own data in `C:\Users\UniBackup` and does **not** read an installed CloudPacker's `C:\Users\CloudPacker`. To bring CloudPacker settings across, place its `config.json` and `jobs.json` next to `UniBackup.exe`: they are read from there until UniBackup saves its own copies.

---

## 📦 Building and Publishing

To build the self-contained native single-file binaries:

```powershell
# Run the automated build script:
.\publish_release.bat
```

Or manually via `dotnet`:

```powershell
dotnet publish src/UniBackup.UI/UniBackup.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/UniBackup_Release
```
