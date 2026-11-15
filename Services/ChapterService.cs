using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Writes the chapters (time codes and names) an API or a local source gives into Jellyfin, for episodes that have a file.
    /// A metadata provider cannot return chapters, and Jellyfin saves them apart from the item, so the episode provider only
    /// remembers them (by file path) and this service, listening to the library's "item updated" event, saves them once the refresh
    /// is over: the media probe (ffprobe) that runs during a refresh would otherwise overwrite them.
    /// Virtual (missing) episodes have no video: their time codes are added to the synopsis as text instead (see ChapterText).
    /// </summary>
    public class ChapterService : IHostedService
    {
        private static readonly TimeSpan Memory = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, (DateTime At, List<ChapterMark> Marks)> Pending = new ConcurrentDictionary<string, (DateTime, List<ChapterMark>)>(StringComparer.OrdinalIgnoreCase);

        private readonly ILibraryManager _library;
        private readonly IChapterManager _chapters;
        private readonly ILogger<ChapterService> _logger;

        public ChapterService(ILibraryManager library, IChapterManager chapters, ILogger<ChapterService> logger)
        {
            _library = library;
            _chapters = chapters;
            _logger = logger;
        }

        /// <summary>Called by the episode provider: these chapters are to be saved for the episode at this path, after its refresh</summary>
        public static void Remember(string path, List<ChapterMark> marks)
        {
            if (string.IsNullOrEmpty(path) || marks == null || marks.Count == 0)
            {
                return;
            }

            Pending[path] = (DateTime.UtcNow, marks);
            foreach (var old in Pending.Where(p => DateTime.UtcNow - p.Value.At > Memory).Select(p => p.Key).ToList())
            {
                Pending.TryRemove(old, out _);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemUpdated += OnItemUpdated;
            _library.ItemAdded += OnItemUpdated;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemUpdated -= OnItemUpdated;
            _library.ItemAdded -= OnItemUpdated;
            return Task.CompletedTask;
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            // Cheap checks first: this event fires for every item of the library
            if (Pending.IsEmpty || e.Item is not Episode episode || episode.IsVirtualItem || string.IsNullOrEmpty(episode.Path))
            {
                return;
            }

            // Kept (not taken): the event can fire before the probe of the same refresh, and again after; applying is idempotent
            if (!Pending.TryGetValue(episode.Path, out var entry))
            {
                return;
            }

            _ = Task.Run(() => Apply(episode, entry.Marks));
        }

        private void Apply(Episode episode, List<ChapterMark> marks)
        {
            try
            {
                var runtimeSeconds = episode.RunTimeTicks is long ticks && ticks > 0 ? ticks / (double)TimeSpan.TicksPerSecond : 0;
                // Jellyfin chapters have a start and no end: a chapter that ends before the next one (or the end of the video) is closed by an extra chapter
                var wanted = ChapterParser.WithGaps(marks, runtimeSeconds)
                    .Select(m => new ChapterInfo { StartPositionTicks = (long)Math.Round(m.Seconds * TimeSpan.TicksPerSecond), Name = m.Title })
                    // A time code past the end of the video cannot be shown; the length is known once the file was probed
                    .Where(c => !(episode.RunTimeTicks is long runtime && runtime > 0 && c.StartPositionTicks >= runtime))
                    .ToList();
                if (wanted.Count == 0)
                {
                    return;
                }

                var existing = _chapters.GetChapters(episode.Id);
                if (existing != null && existing.Count == wanted.Count
                    && existing.Zip(wanted, (a, b) => Math.Abs(a.StartPositionTicks - b.StartPositionTicks) < TimeSpan.TicksPerMillisecond * 10 && string.Equals(a.Name, b.Name, StringComparison.Ordinal)).All(same => same))
                {
                    return;
                }

                _chapters.SaveChapters(episode, wanted);
                _logger.LogInformation("🕒 [CHAPTERS] {Count} chapter(s) saved for '{Name}' (before: {Before})", wanted.Count, episode.Name, existing?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🕒 [CHAPTERS] ⚠️ Could not save the chapters of '{Name}'", episode.Name);
            }
        }
    }

    /// <summary>Time codes written in a synopsis (episodes that have no video to carry chapters)</summary>
    public static class ChapterText
    {
        /// <summary>The synopsis followed by the time codes, unless it already lists them (an API that gives them in the text)</summary>
        public static string AppendToOverview(string overview, List<ChapterMark> marks)
        {
            if (marks == null || marks.Count == 0 || ChapterParser.ParseLines(overview, false).Count >= 2)
            {
                return overview;
            }

            return (string.IsNullOrWhiteSpace(overview) ? string.Empty : overview.TrimEnd() + "\n\n") + "Repères\n" + ChapterParser.ToText(marks);
        }
    }
}
