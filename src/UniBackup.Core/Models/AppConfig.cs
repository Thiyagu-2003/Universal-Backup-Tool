using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class BackupSettings
{
    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = [];

    [JsonPropertyName("temp_folder")]
    public string TempFolder { get; set; } = @"C:\UniBackup\temp";

    [JsonPropertyName("log_path")]
    public string LogPath { get; set; } = @"C:\UniBackup\logs\backup.log";

    [JsonPropertyName("max_log_mb")]
    public int MaxLogMb { get; set; } = 10;

    [JsonPropertyName("keep_logs_days")]
    public int KeepLogsDays { get; set; } = 30;

    [JsonPropertyName("warn_stale_days")]
    public int WarnStaleDays { get; set; } = 3;

    [JsonPropertyName("use_vss_default")]
    public bool UseVssDefault { get; set; } = true;

    [JsonPropertyName("history_retention_days")]
    public int HistoryRetentionDays { get; set; } = 90;

    [JsonPropertyName("max_parallelism")]
    public int MaxDegreeOfParallelism { get; set; } = 4;

    [JsonPropertyName("bandwidth_limit_kbps")]
    public int BandwidthLimitKbps { get; set; } = 0;

    [JsonPropertyName("compression_level")]
    public string CompressionLevel { get; set; } = "Optimal";

    [JsonPropertyName("clean_temp_after_backup")]
    public bool CleanTempAfterBackup { get; set; } = true;
}

public class AwsSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("bucket_name")]
    public string BucketName { get; set; } = "iis-backup-2026";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "ap-south-1";

    [JsonPropertyName("s3_prefix")]
    public string S3Prefix { get; set; } = "";

    [JsonPropertyName("storage_class")]
    public string StorageClass { get; set; } = "STANDARD";

    [JsonPropertyName("access_key_id")]
    public string AccessKeyId { get; set; } = "";

    [JsonPropertyName("secret_access_key")]
    public string SecretAccessKey { get; set; } = "";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;

    [JsonPropertyName("multipart_chunk_mb")]
    public int MultipartChunkMb { get; set; } = 100;

    [JsonPropertyName("service_url")]
    public string? ServiceUrl { get; set; }

    [JsonPropertyName("force_path_style")]
    public bool ForcePathStyle { get; set; }

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "aws"; // "aws", "wasabi", "backblaze_b2", "minio", "cloudflare_r2", "custom"
}

public class AzureBlobSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("connection_string")]
    public string ConnectionString { get; set; } = "";

    [JsonPropertyName("container_name")]
    public string ContainerName { get; set; } = "unibackup";

    [JsonPropertyName("access_tier")]
    public string AccessTier { get; set; } = "Hot";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class FtpSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 21;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("use_ftps")]
    public bool UseFtps { get; set; } = true;

    [JsonPropertyName("remote_folder")]
    public string RemoteFolder { get; set; } = "/backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class SftpSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("private_key_path")]
    public string? PrivateKeyPath { get; set; }

    [JsonPropertyName("remote_folder")]
    public string RemoteFolder { get; set; } = "/backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class LocalStorageSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = @"C:\UniBackup\Backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class WebDavSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("remote_folder")]
    public string RemoteFolder { get; set; } = "/backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class DropboxSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("remote_folder")]
    public string RemoteFolder { get; set; } = "/UniBackup";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class OneDriveSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("account_type")]
    public string AccountType { get; set; } = "business"; // "personal", "business"

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("remote_folder")]
    public string RemoteFolder { get; set; } = "/Backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class BoxSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("folder_id")]
    public string FolderId { get; set; } = "0";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;
}

public class ProxySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("proxy_url")]
    public string ProxyUrl { get; set; } = "";

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public class GDriveSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("account_label")]
    public string AccountLabel { get; set; } = "UNIPROCLOUDBACKUP";

    [JsonPropertyName("target_folder")]
    public string TargetFolder { get; set; } = "/Backups";

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 10;
}

public class EmailSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "custom"; // "custom", "gmail", "m365"

    [JsonPropertyName("smtp_host")]
    public string SmtpHost { get; set; } = "smtp.uniprotech.net";

    [JsonPropertyName("smtp_port")]
    public int SmtpPort { get; set; } = 587;

    [JsonPropertyName("use_tls")]
    public bool UseTls { get; set; } = true;

    [JsonPropertyName("from_address")]
    public string FromAddress { get; set; } = "thiyagu@uniprotech.net";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "thiyagu@uniprotech.net";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("to_addresses")]
    public List<string> ToAddresses { get; set; } = ["thiyagu@uniprotech.net"];

    [JsonPropertyName("m365_client_id")]
    public string M365ClientId { get; set; } = "";

    [JsonPropertyName("m365_tenant_id")]
    public string M365TenantId { get; set; } = "common";

    [JsonPropertyName("notify_on_success")]
    public bool NotifyOnSuccess { get; set; } = false;

    [JsonPropertyName("notify_on_failure")]
    public bool NotifyOnFailure { get; set; } = true;
}

public class ScheduleSettings
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "Service"; // "Service" or "TaskScheduler"

    [JsonPropertyName("freq")]
    public string Freq { get; set; } = "daily";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "02:00";

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 1;

    [JsonPropertyName("days")]
    public List<string> Days { get; set; } = ["MON"];

    [JsonPropertyName("day")]
    public string Day { get; set; } = "1";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("window_mode")]
    public string WindowMode { get; set; } = "headless";

    [JsonPropertyName("exit_on_partial")]
    public int ExitOnPartial { get; set; } = 1;
}

public class WebhookSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("teams_webhook_url")]
    public string TeamsWebhookUrl { get; set; } = "";

    [JsonPropertyName("slack_webhook_url")]
    public string SlackWebhookUrl { get; set; } = "";

    [JsonPropertyName("discord_webhook_url")]
    public string DiscordWebhookUrl { get; set; } = "";

    [JsonPropertyName("telegram_bot_token")]
    public string TelegramBotToken { get; set; } = "";

    [JsonPropertyName("telegram_chat_id")]
    public string TelegramChatId { get; set; } = "";

    // Pushover Mobile Push
    [JsonPropertyName("pushover_user_key")]
    public string PushoverUserKey { get; set; } = "";

    [JsonPropertyName("pushover_api_token")]
    public string PushoverApiToken { get; set; } = "";

    // ntfy.sh (or self-hosted)
    [JsonPropertyName("ntfy_server_url")]
    public string NtfyServerUrl { get; set; } = "https://ntfy.sh";

    [JsonPropertyName("ntfy_topic")]
    public string NtfyTopic { get; set; } = "";

    [JsonPropertyName("ntfy_token")]
    public string NtfyToken { get; set; } = "";

    // PagerDuty Events API v2
    [JsonPropertyName("pagerduty_routing_key")]
    public string PagerDutyRoutingKey { get; set; } = "";

    // Generic Custom Webhook (n8n, Zapier, Make, Home Assistant)
    [JsonPropertyName("custom_webhook_url")]
    public string CustomWebhookUrl { get; set; } = "";

    [JsonPropertyName("custom_webhook_header_name")]
    public string CustomWebhookHeaderName { get; set; } = "";

    [JsonPropertyName("custom_webhook_header_value")]
    public string CustomWebhookHeaderValue { get; set; } = "";

    [JsonPropertyName("notify_on_success")]
    public bool NotifyOnSuccess { get; set; } = false;

    [JsonPropertyName("notify_on_failure")]
    public bool NotifyOnFailure { get; set; } = true;
}

public class AppConfig
{
    [JsonPropertyName("backup")]
    public BackupSettings Backup { get; set; } = new();

    [JsonPropertyName("aws")]
    public AwsSettings Aws { get; set; } = new();

    [JsonPropertyName("gdrive")]
    public GDriveSettings GDrive { get; set; } = new();

    [JsonPropertyName("email")]
    public EmailSettings Email { get; set; } = new();

    [JsonPropertyName("schedule")]
    public ScheduleSettings Schedule { get; set; } = new();

    [JsonPropertyName("webhooks")]
    public WebhookSettings Webhooks { get; set; } = new();

    [JsonPropertyName("security")]
    public SecuritySettings Security { get; set; } = new();

    [JsonPropertyName("azure")]
    public AzureBlobSettings Azure { get; set; } = new();

    [JsonPropertyName("ftp")]
    public FtpSettings Ftp { get; set; } = new();

    [JsonPropertyName("sftp")]
    public SftpSettings Sftp { get; set; } = new();

    [JsonPropertyName("local")]
    public LocalStorageSettings Local { get; set; } = new();

    [JsonPropertyName("webdav")]
    public WebDavSettings WebDav { get; set; } = new();

    [JsonPropertyName("dropbox")]
    public DropboxSettings Dropbox { get; set; } = new();

    [JsonPropertyName("onedrive")]
    public OneDriveSettings OneDrive { get; set; } = new();

    [JsonPropertyName("box")]
    public BoxSettings Box { get; set; } = new();

    [JsonPropertyName("proxy")]
    public ProxySettings Proxy { get; set; } = new();

    [JsonPropertyName("notification_matrix")]
    public NotificationTriggerMatrix NotificationMatrix { get; set; } = new();

    [JsonPropertyName("heartbeat")]
    public HeartbeatSettings Heartbeat { get; set; } = new();

    [JsonPropertyName("rpo")]
    public RpoSettings Rpo { get; set; } = new();

    [JsonPropertyName("prometheus")]
    public PrometheusSettings Prometheus { get; set; } = new();

    [JsonPropertyName("appearance")]
    public AppearanceSettings Appearance { get; set; } = new();
}

public class AppearanceSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Light"; // "System", "Light", "Dark"
}

public class SecuritySettings
{
    [JsonPropertyName("require_login")]
    public bool RequireLogin { get; set; } = false;

    [JsonPropertyName("auth_mode")]
    public string AuthMode { get; set; } = "Static"; // "Static" or "DateBased"

    [JsonPropertyName("access_password")]
    public string AccessPassword { get; set; } = "";

    [JsonPropertyName("date_prefix")]
    public string DatePrefix { get; set; } = "admin(";

    [JsonPropertyName("date_format")]
    public string DateFormat { get; set; } = "dd-MM-yyyy";

    [JsonPropertyName("date_suffix")]
    public string DateSuffix { get; set; } = ")";

    public string GenerateExpectedPassword(DateTime? when = null)
    {
        if (string.Equals(AuthMode, "DateBased", StringComparison.OrdinalIgnoreCase))
        {
            var date = when ?? DateTime.Now;
            var format = string.IsNullOrWhiteSpace(DateFormat) ? "dd-MM-yyyy" : DateFormat;
            try
            {
                return $"{DatePrefix ?? ""}{date.ToString(format)}{DateSuffix ?? ""}";
            }
            catch
            {
                return $"{DatePrefix ?? ""}{date:dd-MM-yyyy}{DateSuffix ?? ""}";
            }
        }
        return AccessPassword ?? "";
    }
}
