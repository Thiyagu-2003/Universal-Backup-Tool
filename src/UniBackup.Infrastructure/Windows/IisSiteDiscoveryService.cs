using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Microsoft.Web.Administration;
using Serilog;

namespace UniBackup.Infrastructure.Windows;

public class IisSiteDiscoveryService : IIisDiscoveryService
{
    public Task<Result<List<IisSiteInfo>>> DiscoverSitesAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<IisSiteInfo>();

            try
            {
                using var serverManager = new ServerManager();

                foreach (var site in serverManager.Sites)
                {
                    var rootApp = site.Applications.FirstOrDefault(a => a.Path == "/");
                    var rootVdir = rootApp?.VirtualDirectories.FirstOrDefault(v => v.Path == "/");
                    var physicalPath = rootVdir?.PhysicalPath ?? "";

                    // Expand environment variables like %SystemDrive% if present
                    if (!string.IsNullOrEmpty(physicalPath))
                    {
                        physicalPath = Environment.ExpandEnvironmentVariables(physicalPath);
                    }

                    var siteInfo = new IisSiteInfo
                    {
                        Id = site.Id,
                        Name = site.Name,
                        State = site.State.ToString(),
                        AppPool = rootApp?.ApplicationPoolName ?? "",
                        PhysicalPath = physicalPath,
                        Bindings = site.Bindings.Select(b => $"{b.Protocol}://{b.Host}:{b.EndPoint?.Port}").ToList(),
                        PathExistsOnDisk = !string.IsNullOrEmpty(physicalPath) && Directory.Exists(physicalPath)
                    };

                    results.Add(siteInfo);
                }

                Log.Information("Discovered {Count} IIS sites via Microsoft.Web.Administration", results.Count);
                return Result<List<IisSiteInfo>>.Success(results.OrderBy(s => s.Name).ToList());
            }
            catch (FileNotFoundException)
            {
                Log.Warning("IIS administration binaries not found (IIS may not be installed on this machine).");
                return Result<List<IisSiteInfo>>.Failure("IIS is not installed or configured on this machine.");
            }
            catch (UnauthorizedAccessException)
            {
                Log.Warning("Access denied when reading IIS configuration (Administrator privileges required).");
                return Result<List<IisSiteInfo>>.Failure("Administrator privileges are required to inspect IIS sites.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to inspect IIS configuration: {Error}", ex.Message);
                return Result<List<IisSiteInfo>>.Failure($"IIS discovery error: {ex.Message}");
            }
        });
    }
}
