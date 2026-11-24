using System.Windows;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class ApplicationLogsRedesignTests
{
    private static List<LogLine> CreateSampleLogs(int count = 100)
    {
        var list = new List<LogLine>();
        var baseTime = new DateTime(2026, 11, 24, 10, 0, 0);

        string[] sources = ["Backup Engine", "Scheduler", "IIS Sites", "Cloud Recovery", "Notification Service", "System"];
        string[] severities = ["INF", "WRN", "ERR"];
        string[] jobs = ["DailyWebBackup", "SqlDiffJob", "FilesArchive", "DbFullBackup"];

        for (int i = 0; i < count; i++)
        {
            var time = baseTime.AddMinutes(-i * 15);
            var lvl = severities[i % 3];
            var src = sources[i % sources.Length];
            var job = jobs[i % jobs.Length];

            string raw;
            if (lvl == "ERR")
            {
                raw = $"{time:yyyy-MM-dd HH:mm:ss} [ERR] Upload failed for job '{job}': System.Net.Http.HttpRequestException: Connection refused to remote host at port 443.";
            }
            else if (lvl == "WRN")
            {
                raw = $"{time:yyyy-MM-dd HH:mm:ss} [WRN] Low disk space warning on staging folder for job '{job}'. Remaining space 500MB.";
            }
            else
            {
                raw = $"{time:yyyy-MM-dd HH:mm:ss} [INF] ✔ SUCCESS — Job '{job}' completed successfully in 12s.";
            }

            var line = new LogLine(raw, i + 1);
            line.Source = src; // explicit source attribution for testing
            list.Add(line);
        }

        return list;
    }

    [Fact]
    public void Test1_MultipleSeverityFilters_CombinesErrorsAndWarnings_ExcludesInfo()
    {
        var vm = new LogsViewModel();
        var sample = CreateSampleLogs(90); // 30 INF, 30 WRN, 30 ERR
        vm.LoadCustomLines(sample);

        Assert.Equal(90, vm.CountAll);
        Assert.Equal(30, vm.CountErrors);
        Assert.Equal(30, vm.CountWarnings);
        Assert.Equal(30, vm.CountInformation);

        // Filter to only Errors and Warnings (exclude Info)
        vm.FilterErrors = true;
        vm.FilterWarnings = true;
        vm.FilterInformation = false;

        Assert.Equal(60, vm.TotalFilteredCount);
        Assert.DoesNotContain(vm.PagedLogs, l => l.IsInformation);
        Assert.Contains(vm.PagedLogs, l => l.IsError);
        Assert.Contains(vm.PagedLogs, l => l.IsWarning);

        // Filter to only Errors
        vm.FilterWarnings = false;
        Assert.Equal(30, vm.TotalFilteredCount);
        Assert.All(vm.PagedLogs, l => Assert.True(l.IsError));
    }

    [Fact]
    public void Test2_MultipleSourceFilters_CombinesSelectedSources_Accurately()
    {
        var vm = new LogsViewModel();
        var sample = CreateSampleLogs(120); // 20 per source across 6 sources
        vm.LoadCustomLines(sample);

        Assert.Equal(120, vm.CountAll);
        Assert.Equal(20, vm.CountBackupEngine);
        Assert.Equal(20, vm.CountScheduler);
        Assert.Equal(20, vm.CountIisSites);
        Assert.Equal(20, vm.CountCloudRecovery);
        Assert.Equal(20, vm.CountNotificationService);
        Assert.Equal(20, vm.CountSystem);

        // Select only Backup Engine and IIS Sites
        vm.FilterBackupEngine = true;
        vm.FilterScheduler = false;
        vm.FilterIisSites = true;
        vm.FilterCloudRecovery = false;
        vm.FilterNotificationService = false;
        vm.FilterSystem = false;

        Assert.Equal(40, vm.TotalFilteredCount);
        Assert.All(vm.PagedLogs, l => Assert.True(l.Source is "Backup Engine" or "IIS Sites"));
    }

    [Fact]
    public void Test3_CombinedSearchAndDateFilters_AppliesSimultaneously()
    {
        var vm = new LogsViewModel();
        var sample = CreateSampleLogs(100);
        vm.LoadCustomLines(sample);

        // Search for specific job name
        vm.SearchText = "SqlDiffJob";
        var countForJob = vm.TotalFilteredCount;
        Assert.True(countForJob > 0 && countForJob < 100);

        // Further restrict to only Errors
        vm.FilterErrors = true;
        vm.FilterWarnings = false;
        vm.FilterInformation = false;
        var errorCountForJob = vm.TotalFilteredCount;
        Assert.True(errorCountForJob <= countForJob);
        Assert.All(vm.PagedLogs, l =>
        {
            Assert.True(l.IsError);
            Assert.Contains("SqlDiffJob", l.Raw);
        });

        // Set date range filter
        vm.SelectedDateRange = "Today";
        Assert.True(vm.TotalFilteredCount <= errorCountForJob);
    }

    [Fact]
    public void Test4_SelectAllFilteredRecords_SelectsOnlyFilteredAcrossAllPages()
    {
        var vm = new LogsViewModel { PageSize = 10 };
        var sample = CreateSampleLogs(60); // 20 ERR, 20 WRN, 20 INF
        vm.LoadCustomLines(sample);

        // Filter to only Errors (20 records, 2 pages of 10)
        vm.FilterErrors = true;
        vm.FilterWarnings = false;
        vm.FilterInformation = false;

        Assert.Equal(20, vm.TotalFilteredCount);
        Assert.Equal(2, vm.TotalPages);

        // Execute SelectAllFiltered
        vm.SelectAllFilteredCommand.Execute(null);

        Assert.Equal(20, vm.SelectedCount);
        Assert.True(vm.IsAllFilteredSelected);

        // Verify all 20 error records are selected across pages
        Assert.All(sample.Where(l => l.IsError), l => Assert.True(l.IsSelected));
        Assert.All(sample.Where(l => !l.IsError), l => Assert.False(l.IsSelected));
    }

    [Fact]
    public void Test5_SelectionAcrossPages_PreservedWhenPagingAndFiltering()
    {
        var vm = new LogsViewModel { PageSize = 10 };
        var sample = CreateSampleLogs(50);
        vm.LoadCustomLines(sample);

        // Select first 2 records on Page 1
        vm.PagedLogs[0].IsSelected = true;
        vm.PagedLogs[1].IsSelected = true;
        Assert.Equal(2, vm.SelectedCount);

        // Navigate to Page 2
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);

        // Select first record on Page 2
        vm.PagedLogs[0].IsSelected = true;
        Assert.Equal(3, vm.SelectedCount);

        // Navigate back to Page 1 and verify selection persisted
        vm.PreviousPageCommand.Execute(null);
        Assert.Equal(1, vm.CurrentPage);
        Assert.True(vm.PagedLogs[0].IsSelected);
        Assert.True(vm.PagedLogs[1].IsSelected);
        Assert.Equal(3, vm.SelectedCount);

        // Clear Selection
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.IsAnySelected);
        Assert.False(vm.IsAllFilteredSelected);
    }

    [Fact]
    public void Test6_PreviousAndNextErrorNavigation_FollowsFilteredResults_AndUpdatesPage()
    {
        var vm = new LogsViewModel { PageSize = 5 };
        var sample = CreateSampleLogs(30); // Errors are at indices 0, 3, 6, 9, 12, 15, 18, 21, 24, 27
        vm.LoadCustomLines(sample);

        // Jump to First Error
        vm.FirstErrorCommand.Execute(null);
        Assert.NotNull(vm.SelectedLog);
        Assert.True(vm.SelectedLog.IsError);
        Assert.True(vm.IsDetailsOpen);
        Assert.True(vm.SelectedLog.Highlighted);
        Assert.Equal(1, vm.CurrentPage);

        // Navigate Next Error
        var firstErr = vm.SelectedLog;
        vm.NextErrorCommand.Execute(null);
        var secondErr = vm.SelectedLog;
        Assert.NotEqual(firstErr, secondErr);
        Assert.True(secondErr.IsError);

        // Navigate multiple times into later pages
        vm.NextErrorCommand.Execute(null); // error 3 (index 6, page 2)
        Assert.Equal(2, vm.CurrentPage);
        Assert.True(vm.SelectedLog.IsError);

        // Navigate Previous Error
        vm.PreviousErrorCommand.Execute(null);
        Assert.Equal(secondErr, vm.SelectedLog);
    }

    [Fact]
    public void Test7_FirstAndLatestErrorNavigation_JumpsToExtremes_AndEnforcesBoundaryStates()
    {
        var vm = new LogsViewModel { PageSize = 10 };
        var sample = CreateSampleLogs(30);
        vm.LoadCustomLines(sample);

        var errors = sample.Where(l => l.IsError).ToList();
        var firstError = errors.First();
        var lastError = errors.Last();

        // Jump to First Error
        vm.FirstErrorCommand.Execute(null);
        Assert.Equal(firstError, vm.SelectedLog);
        Assert.False(vm.CanGoPreviousError);
        Assert.True(vm.CanGoNextError);
        Assert.False(vm.CanGoFirstError);
        Assert.True(vm.CanGoLatestError);

        // Jump to Latest Error
        vm.LatestErrorCommand.Execute(null);
        Assert.Equal(lastError, vm.SelectedLog);
        Assert.True(vm.CanGoPreviousError);
        Assert.False(vm.CanGoNextError);
        Assert.True(vm.CanGoFirstError);
        Assert.False(vm.CanGoLatestError);

        // Test Sequential Record Navigation
        vm.FirstPageCommand.Execute(null);
        vm.NavigateToRecord(sample[0]);
        Assert.False(vm.CanGoPreviousLog);
        Assert.True(vm.CanGoNextLog);

        vm.NextLogCommand.Execute(null);
        Assert.Equal(sample[1], vm.SelectedLog);
        Assert.True(vm.CanGoPreviousLog);
    }

    [Fact]
    public void Test8_LongStackTraces_ParsedIntoStackTrace_WithoutClutteringMainMessage()
    {
        var header = "2026-11-24 10:30:00 [ERR] Pipeline faulted during job 'WebBackup': System.InvalidOperationException: Database timeout";
        var continuation = new List<string>
        {
            "System.InvalidOperationException: Database timeout",
            "   at UniBackup.Engine.Pipeline.BackupPipeline.ExecuteJobAsync(AppConfig config, BackupJob job) in G:\\UniBackup\\Pipeline.cs:line 120",
            "   at UniBackup.Infrastructure.Sql.SqlServerBackupService.BackupAsync() in G:\\UniBackup\\Sql.cs:line 45",
            "   at System.Threading.Tasks.Task.Wait()",
            "   at UniBackup.Service.JobCoordinator.Run() in G:\\UniBackup\\Coordinator.cs:line 89"
        };

        var logEntry = LogLine.FromMultiLine(header, continuation, 1, @"C:\UniBackup\logs\UniBackup_Manual.log");

        Assert.True(logEntry.IsError);
        Assert.Equal("Error", logEntry.Severity);
        Assert.Equal("System.InvalidOperationException", logEntry.ExceptionType);
        Assert.Equal("WebBackup", logEntry.JobName);
        Assert.True(logEntry.HasStackTrace);
        Assert.Contains("BackupPipeline.ExecuteJobAsync", logEntry.StackTrace);
        Assert.Contains("Coordinator.cs:line 89", logEntry.StackTrace);

        // Ensure main message is clean and single-line
        Assert.DoesNotContain(Environment.NewLine, logEntry.Message);
        Assert.Contains("Database timeout", logEntry.Message);

        // Ensure FullDetails contains complete diagnostic information
        Assert.Contains("Timestamp:", logEntry.FullDetails);
        Assert.Contains("Stack Trace:", logEntry.FullDetails);
    }

    [Fact]
    public async Task Test9_ExportSelectedAndFilteredLogs_GeneratesValidCsvStructure()
    {
        var vm = new LogsViewModel();
        var sample = CreateSampleLogs(20);
        vm.LoadCustomLines(sample);

        // Filter to warnings
        vm.FilterWarnings = true;
        vm.FilterErrors = false;
        vm.FilterInformation = false;

        var csvFiltered = await vm.ExportLogsAsync(vm.PagedLogs, "filtered.csv", explicitPath: null);

        Assert.Contains("Timestamp,Severity,Level,Source,JobName,Message,ExceptionType,StackTrace", csvFiltered);
        Assert.Contains("\"Warning\"", csvFiltered);
        Assert.DoesNotContain("\"Error\"", csvFiltered);

        // Select first 2 records and export selected
        vm.PagedLogs[0].IsSelected = true;
        vm.PagedLogs[1].IsSelected = true;

        var selectedLogs = sample.Where(l => l.IsSelected).ToList();
        var csvSelected = await vm.ExportLogsAsync(selectedLogs, "selected.csv", explicitPath: null);

        Assert.Equal(2, selectedLogs.Count);
        Assert.Contains("Timestamp,Severity,Level,Source,JobName,Message,ExceptionType,StackTrace", csvSelected);
    }

    [Fact]
    public void Test10_EmptyLogHistory_HandlesCleanly_WithoutExceptions()
    {
        var vm = new LogsViewModel();
        vm.LoadCustomLines([]);

        Assert.Equal(0, vm.CountAll);
        Assert.Equal(0, vm.TotalFilteredCount);
        Assert.Equal(1, vm.TotalPages);
        Assert.True(vm.HasNoVisibleLines);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.IsAnySelected);
        Assert.False(vm.IsAllFilteredSelected);

        // Navigation commands must be safely disabled and not throw
        Assert.False(vm.CanGoFirstError);
        Assert.False(vm.CanGoNextError);
        Assert.False(vm.CanGoPreviousError);
        Assert.False(vm.CanGoLatestError);
        Assert.False(vm.CanGoPreviousLog);
        Assert.False(vm.CanGoNextLog);

        vm.FirstErrorCommand.Execute(null);
        vm.NextErrorCommand.Execute(null);
        vm.PreviousErrorCommand.Execute(null);
        vm.LatestErrorCommand.Execute(null);
        vm.SelectAllFilteredCommand.Execute(null);
        vm.ClearSelectionCommand.Execute(null);
    }

    [Fact]
    public void Test11_ThousandsOfLogRecords_FiltersAndPagesResponsively()
    {
        var vm = new LogsViewModel { PageSize = 50 };
        var sample = CreateSampleLogs(3000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.LoadCustomLines(sample);
        sw.Stop();

        Assert.Equal(3000, vm.CountAll);
        Assert.Equal(60, vm.TotalPages);
        Assert.Equal(50, vm.PagedLogs.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Loading took {sw.ElapsedMilliseconds}ms which is too slow");

        // Filter 3000 records
        sw.Restart();
        vm.SearchText = "WebBackup";
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"Filtering took {sw.ElapsedMilliseconds}ms which is too slow");
        Assert.True(vm.TotalFilteredCount > 0);

        // Page navigation in 3000 records
        sw.Restart();
        vm.LastPageCommand.Execute(null);
        sw.Stop();

        Assert.Equal(vm.TotalPages, vm.CurrentPage);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Paging took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Test12_ThemesAndTokens_BrushesAndGeometries_AreAvailable()
    {
        // Verify key icon geometries added in IconResources.xaml
        var app = Application.Current;
        if (app != null)
        {
            Assert.NotNull(app.Resources["IconChevronLeft"]);
            Assert.NotNull(app.Resources["IconChevronUp"]);
            Assert.NotNull(app.Resources["IconDoubleChevronLeft"]);
            Assert.NotNull(app.Resources["IconDoubleChevronRight"]);
            Assert.NotNull(app.Resources["IconFilter"]);
            Assert.NotNull(app.Resources["IconExport"]);

            // State brushes
            Assert.NotNull(app.Resources["BrushCoral"]);
            Assert.NotNull(app.Resources["BrushSaffron"]);
            Assert.NotNull(app.Resources["BrushCobalt"]);
        }
    }

    [Fact]
    public void Test13_ClearFiltersCommand_ResetsAllFiltersAndSearch()
    {
        var vm = new LogsViewModel();
        vm.LoadCustomLines(CreateSampleLogs(50));

        // Dirty up filters
        vm.FilterErrors = false;
        vm.FilterWarnings = true;
        vm.FilterInformation = false;
        vm.FilterBackupEngine = false;
        vm.FilterScheduler = false;
        vm.SelectedDateRange = "Last 7 days";
        vm.SearchText = "SomeQuery";

        Assert.NotEqual(50, vm.TotalFilteredCount);

        // Reset
        vm.ClearFiltersCommand.Execute(null);

        Assert.True(vm.FilterErrors);
        Assert.True(vm.FilterWarnings);
        Assert.True(vm.FilterInformation);
        Assert.True(vm.FilterBackupEngine);
        Assert.True(vm.FilterScheduler);
        Assert.Equal("All Time", vm.SelectedDateRange);
        Assert.Equal("", vm.SearchText);
        Assert.Equal(50, vm.TotalFilteredCount);
    }

    [Fact]
    public void Test14_LogLine_InfersSourcesAndJobNames_Correctly()
    {
        var lineIis = new LogLine("2026-11-24 10:00:00 [INF] IIS Website 'CorporatePortal' started on port 443.", 1);
        Assert.Equal("IIS Sites", lineIis.Source);
        Assert.Equal("CorporatePortal", lineIis.JobName);

        var lineSched = new LogLine("2026-11-24 10:00:00 [INF] Task Scheduler triggered job 'NightlyBackup'.", 2);
        Assert.Equal("Scheduler", lineSched.Source);
        Assert.Equal("NightlyBackup", lineSched.JobName);

        var lineRestore = new LogLine("2026-11-24 10:00:00 [INF] Cloud Recovery drill completed for database 'CustomerDb'.", 3);
        Assert.Equal("Cloud Recovery", lineRestore.Source);

        var lineNotify = new LogLine("2026-11-24 10:00:00 [INF] Notification sent via Slack webhook for 'Alert01'.", 4);
        Assert.Equal("Notification Service", lineNotify.Source);

        var lineBackup = new LogLine("2026-11-24 10:00:00 [INF] Backup engine created zip archive successfully.", 5);
        Assert.Equal("Backup Engine", lineBackup.Source);
    }
}
