using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Number of a real episode when neither the file name nor the API gives one: its position in the season's episode list sorted
    /// by release date, the same rule the missing episodes follow (<see cref="MissingEpisodeService.FillMissingIndexNumbers"/>).
    /// The season is the one named by the tag of the file's folder. The list is kept for a few minutes: a season refresh asks for
    /// the number of every episode in a row.
    /// </summary>
    public class EpisodePositionService
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, int> Numbers)> _seasons
            = new ConcurrentDictionary<string, (DateTime, Dictionary<string, int>)>(StringComparer.OrdinalIgnoreCase);

        public EpisodePositionService(CustomApiClient apiClient, SourceResolver sourceResolver, ILogger logger)
        {
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
            _logger = logger;
        }

        /// <summary>Number of the episode tagged <paramref name="episodeTag"/> (e.g. "dm-b1v2") that lives in a folder tagged with its season, or null</summary>
        public async Task<int?> GetNumberAsync(string episodePath, string episodeTag, CancellationToken cancellationToken)
        {
            try
            {
                var folder = Path.GetDirectoryName(episodePath);
                var seasonTag = string.IsNullOrEmpty(folder) ? null : TagParser.Extract(Path.GetFileName(folder));
                if (string.IsNullOrEmpty(seasonTag) || string.IsNullOrEmpty(episodeTag))
                {
                    return null;
                }

                var source = _sourceResolver.GetSourceByPrefix(_sourceResolver.ExtractPrefixFromId(seasonTag));
                if (source == null || source.IsLocal || string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
                {
                    return null;
                }

                if (!_seasons.TryGetValue(seasonTag, out var known) || DateTime.UtcNow - known.At > Lifetime)
                {
                    var list = await _apiClient.GetEpisodeListAsync(_sourceResolver.ExtractIdWithoutPrefix(seasonTag), source, cancellationToken);
                    if (list == null)
                    {
                        return null;
                    }

                    MissingEpisodeService.FillMissingIndexNumbers(list);
                    var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in list.Where(e => e.IndexNumber is int n && n > 0))
                    {
                        numbers[item.Key ?? $"{source.Prefix.Trim()}-{item.Id}"] = item.IndexNumber.Value;
                    }

                    known = (DateTime.UtcNow, numbers);
                    _seasons[seasonTag] = known;
                }

                return known.Numbers.TryGetValue(episodeTag, out var number) ? number : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "🎬 [EPISODE POSITION] ⚠️ could not work out the number of {Tag}: {Message}", episodeTag, ex.Message);
                return null;
            }
        }
    }
}
