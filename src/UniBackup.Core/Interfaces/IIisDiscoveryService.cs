using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Core.Interfaces;

public interface IIisDiscoveryService
{
    Task<Result<List<IisSiteInfo>>> DiscoverSitesAsync();
}
