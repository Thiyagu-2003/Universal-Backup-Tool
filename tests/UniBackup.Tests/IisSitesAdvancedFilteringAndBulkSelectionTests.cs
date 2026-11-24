using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.UI;
using UniBackup.UI.ViewModels;
using UniBackup.UI.Views;
using Xunit;

namespace UniBackup.Tests;

public class MockTestStorageProvider : IStorageProvider
{
    public string Name => "local";
    public bool IsEnabled(AppConfig config) => true;

    public Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(Result<DestinationRunResult>.Success(new DestinationRunResult
        {
            Destination = Name,
            Status = "SUCCESS",
            BytesUploaded = 1024,
            DurationSeconds = 0.1,
            TargetLocation = localFilePath
        }));
    }

    public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) =>
        Task.FromResult(Result<List<CloudArchiveItem>>.Success([]));

    public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath, IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string folderPrefix, int retentionDays, CancellationToken ct = default) =>
        Task.FromResult(Result<int>.Success(0));

    public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}

public class IisSitesAdvancedFilteringAndBulkSelectionTests
{
    private static List<IisSiteVm> CreateSampleSites()
    {
        return
        [
            new IisSiteVm
            {
                Name = "Portal App",
                State = "Started",
                Path = @"C:\inetpub\wwwroot\portal",
                Bindings = ["http://*:80", "https://portal.uniprotech.com:443"],
                Pool = "PortalAppPool",
                Available = true,
                SizeBytes = 1024 * 1024 * 100,
                FileCount = 450,
                IsSizeComputing = false
            },
            new IisSiteVm
            {
                Name = "API Gateway",
                State = "Started",
                Path = @"D:\Websites\ApiGateway",
                Bindings = ["https://api.uniprotech.com:8443"],
                Pool = "GatewayPool",
                Available = false, // Running in IIS metadata, but folder missing -> UNAVAILABLE!
                Reason = "Physical path not found on this server",
                SizeBytes = 0,
                FileCount = 0,
                IsSizeComputing = false
            },
            new IisSiteVm
            {
                Name = "Internal CRM",
                State = "Stopped",
                Path = @"D:\Websites\Crm",
                Bindings = ["http://crm.internal:8080"],
                Pool = "CrmPool",
                Available = true, // Stopped in IIS, but folder exists -> AVAILABLE!
                SizeBytes = 1024 * 1024 * 50,
                FileCount = 210,
                IsSizeComputing = false
            },
            new IisSiteVm
            {
                Name = "Legacy Site 2012",
                State = "Stopped",
                Path = @"E:\OldSites\Legacy",
                Bindings = ["http://legacy.internal:8085"],
                Pool = "LegacyPool",
                Available = false, // Stopped and missing -> UNAVAILABLE!
                Reason = "Physical path not found on this server",
                SizeBytes = 0,
                FileCount = 0,
                IsSizeComputing = false
            }
        ];
    }

    [Fact]
    public void StatusFilters_ComputeCountsFromActualDiscoveryData_AndDistinguishesRunningFromAvailable()
    {
        var vm = new IisSitesViewModel();
        var sites = CreateSampleSites();
        vm.LoadSites(sites);

        // Verification of requirements:
        // Total: 4
        // Running: 1 ("Portal App" is Running AND Available; "API Gateway" is Running but !Available, so it's Unavailable)
        // Stopped: 1 ("Internal CRM" is Stopped AND Available; "Legacy Site 2012" is Stopped but !Available, so it's Unavailable)
        // Unavailable: 2 ("API Gateway" and "Legacy Site 2012")
        Assert.Equal(4, vm.TotalCount);
        Assert.Equal(1, vm.RunningCount);
        Assert.Equal(1, vm.StoppedCount);
        Assert.Equal(2, vm.UnavailableCount);

        Assert.Equal("All Sites (4)", vm.AllFilterTitle);
        Assert.Equal("Running (1)", vm.RunningFilterTitle);
        Assert.Equal("Stopped (1)", vm.StoppedFilterTitle);
        Assert.Equal("Unavailable (2)", vm.UnavailableFilterTitle);
    }

    [Fact]
    public void StatusFilters_ApplyFilter_SwitchesDisplayedSites()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        // Default: All Sites
        Assert.Equal(4, vm.FilteredSites.Count);

        // Running
        vm.SetFilter("Running");
        Assert.Single(vm.FilteredSites);
        Assert.Equal("Portal App", vm.FilteredSites[0].Name);

        // Stopped
        vm.SetFilter("Stopped");
        Assert.Single(vm.FilteredSites);
        Assert.Equal("Internal CRM", vm.FilteredSites[0].Name);

        // Unavailable
        vm.SetFilter("Unavailable");
        Assert.Equal(2, vm.FilteredSites.Count);
        Assert.Contains(vm.FilteredSites, s => s.Name == "API Gateway");
        Assert.Contains(vm.FilteredSites, s => s.Name == "Legacy Site 2012");

        // Back to All
        vm.SetFilter("All");
        Assert.Equal(4, vm.FilteredSites.Count);
    }

    [Fact]
    public void Selection_SelectAll_AppliesToCurrentFilteredResults_AndPreventsSelectingUnavailable()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        // In 'All' view, Select All selects all available sites in the view
        vm.SelectAll();
        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.AllSites.First(s => s.Name == "Portal App").IsSelected);
        Assert.True(vm.AllSites.First(s => s.Name == "Internal CRM").IsSelected);
        Assert.False(vm.AllSites.First(s => s.Name == "API Gateway").IsSelected);
        Assert.False(vm.AllSites.First(s => s.Name == "Legacy Site 2012").IsSelected);
    }

    [Fact]
    public void Selection_SelectRunning_SelectsOnlyRunningAvailable()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.SelectRunning();
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.AllSites.First(s => s.Name == "Portal App").IsSelected);
        Assert.False(vm.AllSites.First(s => s.Name == "Internal CRM").IsSelected);
    }

    [Fact]
    public void Selection_SelectStopped_SelectsOnlyStoppedAvailable()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.SelectStopped();
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.AllSites.First(s => s.Name == "Internal CRM").IsSelected);
        Assert.False(vm.AllSites.First(s => s.Name == "Portal App").IsSelected);
    }

    [Fact]
    public void Selection_SelectAvailable_SelectsAllAvailableSites()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.SelectAvailable();
        Assert.Equal(2, vm.SelectedCount);
        Assert.All(vm.SelectedSites, s => Assert.True(s.Available));
    }

    [Fact]
    public void Selection_ClearSelection_DeselectsAllSites()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.SelectAll();
        Assert.True(vm.HasSelectedSites);

        vm.ClearSelection();
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelectedSites);
        Assert.All(vm.AllSites, s => Assert.False(s.IsSelected));
    }

    [Fact]
    public void Selection_PreservedWhenFilterChanges_AndDistinguishesVisibleFromTotal()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        // Select Portal App (Running) and Internal CRM (Stopped)
        vm.AllSites.First(s => s.Name == "Portal App").IsSelected = true;
        vm.AllSites.First(s => s.Name == "Internal CRM").IsSelected = true;

        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(2, vm.VisibleSelectedCount);
        Assert.Equal("2 of 4 selected", vm.SelectedSummaryText);

        // Change filter to Running
        vm.SetFilter("Running");

        // Selection must be PRESERVED across filter changes!
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(1, vm.VisibleSelectedCount);
        Assert.Equal("2 selected (1 in view)", vm.SelectedSummaryText);

        // Verify that Internal CRM is still selected in the overall dataset
        Assert.True(vm.AllSites.First(s => s.Name == "Internal CRM").IsSelected);

        // Switch to Stopped
        vm.SetFilter("Stopped");
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(1, vm.VisibleSelectedCount);
        Assert.Equal("2 selected (1 in view)", vm.SelectedSummaryText);

        // Switch to Unavailable
        vm.SetFilter("Unavailable");
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(0, vm.VisibleSelectedCount);
        Assert.Equal("2 selected (0 in view)", vm.SelectedSummaryText);

        // Switch back to All
        vm.SetFilter("All");
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(2, vm.VisibleSelectedCount);
        Assert.Equal("2 of 4 selected", vm.SelectedSummaryText);
    }

    [Theory]
    [InlineData("Portal", 1)]                   // Name
    [InlineData("inetpub", 1)]                  // Path
    [InlineData("portal.uniprotech.com", 1)]    // Binding
    [InlineData("8443", 1)]                     // Port
    [InlineData("CrmPool", 1)]                  // Application pool
    [InlineData("uniprotech", 2)]               // Partial binding across multiple sites
    [InlineData("Websites", 2)]                 // Partial path across multiple sites
    [InlineData("nonexistent_site", 0)]         // No match
    public void Search_MatchesAcrossAllSupportedFields(string query, int expectedCount)
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.SearchQuery = query;
        Assert.Equal(expectedCount, vm.FilteredSites.Count);
    }

    [Fact]
    public void CombinedSearchAndStatusFilter_BothWorkTogether()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        // Status filter: Running (only Portal App)
        // Search query: "443" (matches Portal App:443)
        vm.SetFilter("Running");
        vm.SearchQuery = "443";
        Assert.Single(vm.FilteredSites);
        Assert.Equal("Portal App", vm.FilteredSites[0].Name);

        // Search query: "8080" (Internal CRM has port 8080, but it is Stopped, not Running)
        vm.SearchQuery = "8080";
        Assert.Empty(vm.FilteredSites);
        Assert.True(vm.HasNoFilteredSites);

        // Switch filter to Stopped: now Internal CRM should appear
        vm.SetFilter("Stopped");
        Assert.Single(vm.FilteredSites);
        Assert.Equal("Internal CRM", vm.FilteredSites[0].Name);
    }

    [Fact]
    public void BulkOperations_EmptySelection_DisablesButtons()
    {
        var vm = new IisSitesViewModel();
        vm.LoadSites(CreateSampleSites());

        vm.ClearSelection();
        Assert.False(vm.CanRunSelected);
        Assert.False(vm.CanBackupSelected);
        Assert.False(vm.CanEditSelected);
        Assert.False(vm.CanDeleteSelected);

        // Select an item
        vm.AllSites[0].IsSelected = true;
        Assert.True(vm.CanRunSelected);
        Assert.True(vm.CanBackupSelected);
        Assert.True(vm.CanEditSelected);
        Assert.True(vm.CanDeleteSelected);
    }

    [Fact]
    public async Task BulkOperations_RunSelected_StartsStoppedSites_AndSkipsUnavailable()
    {
        var vm = new IisSitesViewModel();
        var sites = CreateSampleSites();
        vm.LoadSites(sites);

        var stoppedAvailable = vm.AllSites.First(s => s.Name == "Internal CRM");
        var stoppedUnavailable = vm.AllSites.First(s => s.Name == "Legacy Site 2012");

        stoppedAvailable.IsSelected = true;
        stoppedUnavailable.IsSelected = true;

        await vm.RunSelectedAsync();

        // Stopped available site transitioned to Started
        Assert.True(stoppedAvailable.IsRunning);
        Assert.Equal("Started", stoppedAvailable.State);

        // Unavailable site remains Stopped and Unavailable
        Assert.False(stoppedUnavailable.IsRunning);
        Assert.False(stoppedUnavailable.Available);

        // Live counts updated
        Assert.Equal(2, vm.RunningCount); // Portal App + Internal CRM
        Assert.Equal(0, vm.StoppedCount); // Internal CRM is now started
        Assert.Equal(2, vm.UnavailableCount);
    }

    [Fact]
    public async Task BulkOperations_BulkBackup_HandlesMixedStatuses_AndRecordsProgress()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "UniBackupTests_BulkBackup_" + Guid.NewGuid().ToString("N"));
        var siteDir = Path.Combine(tempDir, "ValidSite");
        Directory.CreateDirectory(siteDir);
        File.WriteAllText(Path.Combine(siteDir, "index.html"), "<h1>Test Site</h1>");

        try
        {
            var crypto = new CredentialProtector();
            var configService = new ConfigurationService(crypto, tempDir);
            var archiveService = new HighSpeedZipArchiveService();
            var storageProvider = new MockTestStorageProvider();
            var emailService = new EmailNotificationService();
            var webhookService = new WebhookNotificationService();
            var sqlService = new SqlServerBackupService(crypto);
            var pipeline = new BackupPipeline(archiveService, [storageProvider], emailService, webhookService, configService, sqlService);

            var config = new AppConfig
            {
                Local = new LocalStorageSettings { Enabled = true, TargetPath = tempDir }
            };

            var validSite = new IisSiteVm
            {
                Name = "Valid Test Site",
                State = "Started",
                Path = siteDir,
                Available = true,
                SizeBytes = 100,
                IsSelected = true
            };

            var unavailableSite = new IisSiteVm
            {
                Name = "Missing Path Site",
                State = "Stopped",
                Path = Path.Combine(tempDir, "NonExistentFolder_12345"),
                Available = false,
                Reason = "Physical path not found on this server",
                SizeBytes = 0,
                IsSelected = true
            };

            var dialogVm = new IisBulkBackupViewModel(config, pipeline, configService, [validSite, unavailableSite]);

            Assert.Equal(2, dialogVm.TotalCount);
            Assert.Equal(1, dialogVm.EligibleCount);
            Assert.Equal(1, dialogVm.IneligibleCount);
            Assert.True(dialogVm.HasIneligibleSites);
            Assert.True(dialogVm.CanStart);

            await dialogVm.StartBackupAsync();

            Assert.True(dialogVm.IsCompleted);
            Assert.False(dialogVm.IsRunning);
            Assert.Equal(1, dialogVm.SuccessCount);
            Assert.Equal(1, dialogVm.SkippedCount);
            Assert.Equal(0, dialogVm.FailedCount);

            // Item failure/skip details
            var skippedItem = dialogVm.Items.First(i => i.Name == "Missing Path Site");
            Assert.Equal("Skipped", skippedItem.Status);
            Assert.False(string.IsNullOrEmpty(skippedItem.ErrorMessage));

            var successItem = dialogVm.Items.First(i => i.Name == "Valid Test Site");
            Assert.Equal("Succeeded", successItem.Status);
            Assert.False(string.IsNullOrEmpty(successItem.Details));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void BulkOperations_EditSelected_EnforcesSingleSiteEligibility()
    {
        IisSiteVm? editedSite = null;
        var vm = new IisSitesViewModel(onEditSite: s => editedSite = s);
        vm.LoadSites(CreateSampleSites());

        // Select 2 sites
        vm.AllSites[0].IsSelected = true;
        vm.AllSites[1].IsSelected = true;

        vm.EditSelected();
        Assert.Null(editedSite); // Bulk editing multiple sites is not supported

        // Select exactly 1 site
        vm.AllSites[1].IsSelected = false;
        vm.EditSelected();
        Assert.NotNull(editedSite);
        Assert.Equal("Portal App", editedSite.Name);
    }

    [Fact]
    public void LargeList_100PlusWebsites_FilteringAndSelectionIsResponsive()
    {
        var vm = new IisSitesViewModel();
        var largeList = new List<IisSiteVm>();

        // Generate 250 realistic IIS sites
        for (int i = 1; i <= 250; i++)
        {
            var isRunning = i % 3 != 0;
            var isAvailable = i % 5 != 0;
            largeList.Add(new IisSiteVm
            {
                Name = $"Customer App {i:D3}",
                State = isRunning ? "Started" : "Stopped",
                Path = $@"D:\Websites\Client{i:D3}\wwwroot",
                Bindings = [$"http://app{i:D3}.internal:{8000 + i}", $"https://app{i:D3}.com:{9000 + i}"],
                Pool = $"Pool_{i % 10}",
                Available = isAvailable,
                Reason = isAvailable ? null : "Path not found",
                SizeBytes = i * 1024 * 1024L,
                FileCount = i * 10,
                IsSizeComputing = false
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.LoadSites(largeList);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"LoadSites took {sw.ElapsedMilliseconds}ms");

        Assert.Equal(250, vm.TotalCount);

        // Status Filtering
        sw.Restart();
        vm.SetFilter("Running");
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"SetFilter Running took {sw.ElapsedMilliseconds}ms");
        Assert.All(vm.FilteredSites, s => { Assert.True(s.IsRunning); Assert.True(s.Available); });

        // Search Query on All Sites
        vm.SetFilter("All");
        sw.Restart();
        vm.SearchQuery = "8050";
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"Search took {sw.ElapsedMilliseconds}ms");
        Assert.Single(vm.FilteredSites);
        Assert.Equal("Customer App 050", vm.FilteredSites[0].Name);

        // Clear Search and Select Available
        vm.SearchQuery = "";
        vm.SetFilter("All");
        sw.Restart();
        vm.SelectAvailable();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"SelectAvailable took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(largeList.Count(s => s.Available), vm.SelectedCount);

        // Clear Selection
        sw.Restart();
        vm.ClearSelection();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"ClearSelection took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(0, vm.SelectedCount);
    }
}
