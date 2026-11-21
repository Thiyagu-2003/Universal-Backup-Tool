using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Core.Interfaces;

public interface INotificationService
{
    Task<Result> SendRunReportAsync(AppConfig config, BackupJob job, RunHistoryRecord history, CancellationToken ct = default);
    Task<Result> SendBatchReportAsync(AppConfig config, List<RunHistoryRecord> histories, TimeSpan totalDuration, CancellationToken ct = default);
    Task<Result> SendTestNotificationAsync(AppConfig config, CancellationToken ct = default);
}
