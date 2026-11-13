using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Adds the API cast to the Jellyfin result (if EnableCasting is on)
    /// Jellyfin 10.11: people go through MetadataResult.AddPerson, not through a People property on the item
    /// </summary>
    public static class CastMapper
    {
        public static void AddCast<T>(MetadataResult<T> result, List<CastMember> cast, ILogger logger)
            where T : BaseItem
        {
            if (!(Plugin.Instance?.Configuration.EnableCasting ?? true))
            {
                logger.LogDebug("🎭 [CAST] Casting disabled in plugin settings - skipping");
                return;
            }

            if (cast == null || cast.Count == 0)
            {
                return;
            }

            var added = 0;
            foreach (var member in cast.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
            {
                result.AddPerson(ToPersonInfo(member));
                added++;
            }

            logger.LogInformation("🎭 [CAST] Added {Count} people", added);
        }

        public static PersonInfo ToPersonInfo(CastMember member)
        {
            return new PersonInfo
            {
                Name = member.Name.Trim(),
                Role = member.Role,
                Type = ParsePersonKind(member.Type),
                SortOrder = member.Order,
                // Jellyfin downloads this photo for the person's page (if it does not already have one)
                ImageUrl = Uri.TryCreate(member.Image, UriKind.Absolute, out var photo) && (photo.Scheme == Uri.UriSchemeHttp || photo.Scheme == Uri.UriSchemeHttps) ? member.Image : null
            };
        }

        /// <summary>
        /// "Actor", "director", "Writer"... → PersonKind (case-insensitive). Unknown → Actor
        /// </summary>
        private static PersonKind ParsePersonKind(string type)
        {
            if (!string.IsNullOrWhiteSpace(type)
                && Enum.TryParse<PersonKind>(type.Trim(), true, out var kind)
                && Enum.IsDefined(kind)
                && kind != PersonKind.Unknown)
            {
                return kind;
            }

            return PersonKind.Actor;
        }
    }
}
