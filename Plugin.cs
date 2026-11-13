using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CustomMetadata
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Custom Metadata Provider";

        public override Guid Id => Guid.Parse("12345678-1234-1234-1234-123456789abc");

        public override string Description => "Fetch metadata from your custom API with configurable JSON field mapping";

        public static Plugin Instance { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            // The three pages have a shortcut in the "Extensions" section of the left menu (EnableInMainMenu).
            // WATCH THE ORDER: jellyfin-web 10.11 (findBestConfigurationPage) opens, as the plugin's settings page, the FIRST
            // page flagged EnableInMainMenu in the order the server returns. The configuration page must therefore stay first,
            // otherwise clicking the plugin would open "Personnes" or "Épisodes manquants" instead.
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    DisplayName = "Custom Metadata",
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
                },
                new PluginPageInfo
                {
                    // "Missing episodes" screen (also linked from the top of the configuration page)
                    Name = "CustomMetadataMissingEpisodes",
                    DisplayName = "Épisodes manquants",
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.missing.html"
                },
                new PluginPageInfo
                {
                    // "People" screen: the cast directory (also linked from the top of the configuration page)
                    Name = "CustomMetadataPeople",
                    DisplayName = "Personnes",
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.people.html"
                }
            };
        }
    }
}
