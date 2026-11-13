using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.ScheduledTasks
{
    /// <summary>
    /// Scheduled task (Dashboard → Scheduled tasks): missing-episode detection
    /// Discovered automatically by Jellyfin in the plugin assembly
    /// </summary>
    public class MissingEpisodesTask : IScheduledTask
    {
        private readonly MissingEpisodeService _service;
        private readonly CastApplyService _castApply;

        public MissingEpisodesTask(MissingEpisodeService service, CastApplyService castApply)
        {
            _service = service;
            _castApply = castApply;
        }

        public string Name => "Détecter les épisodes manquants";

        public string Key => "CustomMetadataMissingEpisodes";

        public string Description => "Compare la liste d'épisodes de chaque source (endpoint « Liste des épisodes ») aux fichiers présents, et crée des épisodes virtuels pour les manquants.";

        public string Category => "Custom Metadata Provider";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return RunAsync(progress, cancellationToken);
        }

        private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await _service.AnalyzeAsync(progress, cancellationToken);

            // Cast cascaded from a series / season onto files that carry no identifier (new files included)
            await _castApply.SyncUntaggedAsync(cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Once a day at 4 a.m.
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }
    }
}
