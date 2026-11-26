using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// The preview video of a REAL (file-backed) episode, for the Projection theme's hover preview. Missing episodes get theirs
    /// as a trailer (<see cref="MissingEpisodeService"/>); a real episode cannot hold one (D: trailers are rebuilt from disk), so
    /// the URL is looked up on demand: the episode list of the season named by the tag of the file's folder, the entry whose key
    /// is the file's first tag, its <c>PreviewUrl</c> (EpisodeListMapping.PreviewUrl) made absolute. Lists are kept 30 minutes:
    /// hovering a row of episodes asks for each of them.
    /// </summary>
    public class EpisodePreviewService
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, (DateTime At, SourceConfiguration Source, Dictionary<string, string> Urls)> _seasons
            = new ConcurrentDictionary<string, (DateTime, SourceConfiguration, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);

        public EpisodePreviewService(CustomApiClient apiClient, SourceResolver sourceResolver, ILogger logger)
        {
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
            _logger = logger;
        }

        /// <summary>The episode's preview URL and the source it comes from (for its headers), or nulls</summary>
        public async Task<(string Url, SourceConfiguration Source)> GetAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                if (episode == null || string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
                {
                    return (null, null);
                }

                // the episode's keys: every tag of its file name (a file may join several API episodes), then its custom id
                var keys = TagParser.ExtractAll(Path.GetFileName(episode.Path)).ToList();
                var ownId = CustomId.Get(episode.ProviderIds);
                if (!string.IsNullOrEmpty(ownId) && !keys.Contains(ownId, StringComparer.OrdinalIgnoreCase)) { keys.Add(ownId); }

                // its season: the tag of the file's folder, else the season item's custom id (a folder named "Season 1")
                var folder = Path.GetDirectoryName(episode.Path);
                var seasonTag = string.IsNullOrEmpty(folder) ? null : TagParser.Extract(Path.GetFileName(folder));
                if (string.IsNullOrEmpty(seasonTag) && episode.Season != null)
                {
                    seasonTag = TagParser.Extract(Path.GetFileName(episode.Season.Path ?? string.Empty)) ?? CustomId.Get(episode.Season.ProviderIds);
                }

                if (keys.Count == 0 || string.IsNullOrEmpty(seasonTag))
                {
                    _logger.LogInformation("🎞️ [PREVIEW] '{Episode}': no preview lookup ({Reason})", episode.Name, keys.Count == 0 ? "the file carries no tag" : "no season tag");
                    return (null, null);
                }

                var source = _sourceResolver.GetSourceByPrefix(_sourceResolver.ExtractPrefixFromId(seasonTag));
                if (source == null || string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
                {
                    _logger.LogInformation("🎞️ [PREVIEW] '{Episode}': season {Season} has no source with an episode list", episode.Name, seasonTag);
                    return (null, null);
                }

                if (!_seasons.TryGetValue(seasonTag, out var known) || DateTime.UtcNow - known.At > Lifetime)
                {
                    var list = await _apiClient.GetEpisodeListAsync(_sourceResolver.ExtractIdWithoutPrefix(seasonTag), source, cancellationToken);
                    if (list == null)
                    {
                        return (null, null);
                    }

                    var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in list)
                    {
                        var url = PreviewMapper.ResolveUrl(item.PreviewUrl, source.BaseUrl);
                        if (!string.IsNullOrEmpty(url))
                        {
                            urls[item.Key ?? $"{source.Prefix.Trim()}-{item.Id}"] = url;
                        }
                    }

                    known = (DateTime.UtcNow, source, urls);
                    _seasons[seasonTag] = known;
                    _logger.LogInformation("🎞️ [PREVIEW] Season {Season}: {Count} preview video(s) in the episode list", seasonTag, urls.Count);
                }

                foreach (var key in keys)
                {
                    if (known.Urls.TryGetValue(key, out var found)) { return (found, known.Source); }
                }

                _logger.LogDebug("🎞️ [PREVIEW] '{Episode}': no preview for {Keys} in season {Season}", episode.Name, string.Join(", ", keys), seasonTag);
                return (null, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "🎞️ [PREVIEW] Preview of {Episode} not found", episode?.Name);
                return (null, null);
            }
        }
    }
}
