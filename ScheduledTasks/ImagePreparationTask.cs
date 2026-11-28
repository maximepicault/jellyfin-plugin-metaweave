using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CustomMetadata.ScheduledTasks
{
    /// <summary>
    /// Scheduled task (Dashboard → Scheduled tasks): the thumbnails and screen-sized pictures of the photo libraries, made in
    /// advance for the Projection theme's screens. Only does something with the setting and the theme script on.
    /// </summary>
    public class ImagePreparationTask : IScheduledTask
    {
        private readonly ImagePreparationService _images;

        public ImagePreparationTask(ImagePreparationService images)
        {
            _images = images;
        }

        public string Name => "Préparer les images des photothèques";

        public string Key => "CustomMetadataPrepareImages";

        public string Description => "Calcule à l'avance les vignettes et les images à la taille de l'écran des bibliothèques de photos, pour que les écrans du thème Projection les affichent sans attente. Réglage « Préparer les images des photothèques » du plugin (avec « Améliorations du thème Projection »).";

        public string Category => "Custom Metadata Provider";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.PrepareLibraryImages || !config.EnableThemeScript)
            {
                progress?.Report(100);
                return Task.CompletedTask;
            }

            return _images.PrepareAsync(config.PrepareWideScreenImages, progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Every night, after the library scans and the other nightly tasks have started
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks } };
        }
    }
}
