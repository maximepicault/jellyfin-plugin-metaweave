using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CustomMetadata.ScheduledTasks
{
    /// <summary>
    /// Scheduled task (Dashboard → Scheduled tasks): small preview clips of the episodes without a source preview, for the
    /// Projection theme's hover preview. Only does something while the theme script is on.
    /// </summary>
    public class PreviewClipsTask : IScheduledTask
    {
        private readonly PreviewClipService _clips;

        public PreviewClipsTask(PreviewClipService clips)
        {
            _clips = clips;
        }

        public string Name => "Générer les aperçus des épisodes";

        public string Key => "CustomMetadataPreviewClips";

        public string Description => "Crée une courte vidéo d'aperçu (quelques secondes prises tout au long de l'épisode, sans son, en petite taille) pour chaque épisode dont la source ne fournit pas d'aperçu. Utilisé au survol par le thème Projection (« Améliorations du thème Projection »).";

        public string Category => "Custom Metadata Provider";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!(Plugin.Instance?.Configuration?.EnableThemeScript ?? false))
            {
                progress?.Report(100);
                return Task.CompletedTask;
            }

            return _clips.BuildAllAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Every night at 3 a.m., after the library scans of the evening
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks } };
        }
    }
}
