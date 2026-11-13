using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Api;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Merges the metadata of several IDs for a file that bundles several API episodes
    /// ("Ep {dm-1} {dm-2}.mkv"). The first ID is the reference: its single values (title, overview, date, rating,
    /// number) win, the following ones only serve as a fallback when a field is empty there. The lists
    /// (tags, genres, cast) are merged without duplicates.
    /// </summary>
    public static class MetadataMerger
    {
        public static CustomMetadataResponse Merge(CustomMetadataResponse primary, IEnumerable<CustomMetadataResponse> others)
        {
            var extras = (others ?? Enumerable.Empty<CustomMetadataResponse>()).Where(o => o != null).ToList();
            if (primary == null)
            {
                // First ID unreachable: the next one becomes the reference
                if (extras.Count == 0)
                {
                    return null;
                }

                primary = extras[0];
                extras.RemoveAt(0);
            }

            if (extras.Count == 0)
            {
                return primary;
            }

            var merged = new CustomMetadataResponse
            {
                Title = primary.Title,
                Overview = primary.Overview,
                SeriesName = primary.SeriesName,
                PremiereDate = primary.PremiereDate,
                CommunityRating = primary.CommunityRating,
                IndexNumber = primary.IndexNumber,
                Tags = primary.Tags,
                Genres = primary.Genres,
                Cast = primary.Cast
            };

            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(merged.Title)) merged.Title = extra.Title;
                if (string.IsNullOrWhiteSpace(merged.Overview)) merged.Overview = extra.Overview;
                if (string.IsNullOrWhiteSpace(merged.SeriesName)) merged.SeriesName = extra.SeriesName;
                merged.PremiereDate ??= extra.PremiereDate;
                merged.CommunityRating ??= extra.CommunityRating;
                merged.IndexNumber ??= extra.IndexNumber;
            }

            merged.Tags = Union(new[] { primary.Tags }.Concat(extras.Select(e => e.Tags)));
            merged.Genres = Union(new[] { primary.Genres }.Concat(extras.Select(e => e.Genres)));
            merged.Cast = UnionCast(new[] { primary.Cast }.Concat(extras.Select(e => e.Cast)));
            return merged;
        }

        /// <summary>Copy of the response with extra cast (the original response may come from the cache: never modified)</summary>
        public static CustomMetadataResponse WithExtraCast(CustomMetadataResponse response, List<CastMember> extra)
        {
            if (response == null || extra == null || extra.Count == 0)
            {
                return response;
            }

            return new CustomMetadataResponse
            {
                Title = response.Title,
                Overview = response.Overview,
                SeriesName = response.SeriesName,
                PremiereDate = response.PremiereDate,
                CommunityRating = response.CommunityRating,
                IndexNumber = response.IndexNumber,
                Tags = response.Tags,
                Genres = response.Genres,
                Cast = UnionCast(new[] { response.Cast, extra })
            };
        }

        private static List<string> Union(IEnumerable<List<string>> lists)
        {
            var result = new List<string>();
            foreach (var item in lists.Where(l => l != null).SelectMany(l => l))
            {
                if (!string.IsNullOrWhiteSpace(item) && !result.Contains(item.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(item.Trim());
                }
            }

            return result.Count > 0 ? result : null;
        }

        // Duplicate = same name and same role type
        public static List<CastMember> UnionCast(IEnumerable<List<CastMember>> lists)
        {
            var result = new List<CastMember>();
            foreach (var member in lists.Where(l => l != null).SelectMany(l => l))
            {
                if (string.IsNullOrWhiteSpace(member?.Name))
                {
                    continue;
                }

                if (!result.Any(r => string.Equals(r.Name?.Trim(), member.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(r.Type ?? string.Empty, member.Type ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(member);
                }
            }

            return result.Count > 0 ? result : null;
        }
    }
}
