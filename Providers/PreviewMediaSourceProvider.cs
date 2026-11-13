using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Makes a missing (virtual) episode PLAYABLE: its video source = its preview.
    /// jellyfin-web shows a play button on the thumbnails of a season's episode list
    /// (listview imagePlayButton) even for a virtual episode; without a source, playback fails.
    /// MediaSourceManager.GetPlaybackMediaSources drops the virtual episode's Placeholder source
    /// and keeps the dynamic sources supplied here (same path as channels / live TV).
    /// Discovered automatically by Jellyfin in the plugin assembly.
    /// </summary>
    public class PreviewMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<PreviewMediaSourceProvider> _logger;

        public PreviewMediaSourceProvider(ILibraryManager libraryManager, ILogger<PreviewMediaSourceProvider> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            // Called on every playback of any item: exit immediately if it is not one of ours
            // (no ProviderIds test: GetPreviewTrailers identifies our previews by OwnerId + URL)
            if (item is not Episode episode
                || !episode.IsVirtualItem
                || episode.ExtraIds.Length == 0)
            {
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }

            var sources = PreviewMapper.GetPreviewTrailers(episode, _libraryManager)
                .SelectMany(trailer => trailer.GetMediaSources(false))
                .Where(source => !string.IsNullOrEmpty(source.Path))
                .ToList();

            foreach (var source in sources)
            {
                // Id = the Trailer's Id (unique, stable): it is what the client sends back as mediaSourceId
                source.Name = PreviewMapper.NamePrefix;
                source.RequiresOpening = false;
                source.RequiresClosing = false;
                source.Type = MediaSourceType.Default;
            }

            if (sources.Count > 0)
            {
                _logger.LogDebug("🎞️ [PREVIEW] Playing preview as media source of missing episode '{Name}': {Url}", episode.Name, sources[0].Path);
            }

            return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
        }

        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            // Our sources do not need opening (RequiresOpening = false)
            throw new NotSupportedException();
        }
    }
}
