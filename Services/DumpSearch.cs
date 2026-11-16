using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>The "Identify" screen of Jellyfin, fed by the dump sources</summary>
    public static class DumpSearch
    {
        /// <summary>
        /// Best candidates for an item, as search results. Picking one stores its "custom" id on the item (ProviderIds),
        /// which the next refresh then follows straight to that entry.
        /// </summary>
        public static async Task<IEnumerable<RemoteSearchResult>> ToSearchResultsAsync(
            DumpStore dump, string path, string name, string providerName, CancellationToken cancellationToken)
        {
            var candidates = await dump.SearchAsync(path, name, 5, cancellationToken).ConfigureAwait(false);
            return candidates.Select(c =>
            {
                var result = new RemoteSearchResult { Name = c.Entry.Name, SearchProviderName = providerName };
                result.ProviderIds[CustomId.Key] = c.Tag;
                return result;
            }).ToList();
        }
    }
}
