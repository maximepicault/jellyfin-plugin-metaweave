using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Who is linked to an album, a photo or a video of an album. Three origins, added together: the source (the "cast" of the
    /// API's album or of its list entry; the cast typed in a local album or on one of its files), the People screen (by tag),
    /// and the albums above that give their people to their whole content (local: ticked per person; API: the source's option;
    /// People screen: the "cascade" box). A video also gets them in Jellyfin's own cast.
    /// </summary>
    public class AlbumCastService
    {
        private readonly PhotoLibraryService _photos;
        private readonly SourceResolver _resolver;
        private readonly LocalSourceStore _local;
        private readonly CastOverlayStore _overlay;
        private readonly PeopleStore _people;
        private readonly AlbumCastStore _store;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;

        public AlbumCastService(PhotoLibraryService photos, SourceResolver resolver, LocalSourceStore local, CastOverlayStore overlay, PeopleStore people, AlbumCastStore store, ILibraryManager library, ILogger logger)
        {
            _photos = photos;
            _resolver = resolver;
            _local = local;
            _overlay = overlay;
            _people = people;
            _store = store;
            _library = library;
            _logger = logger;
        }

        private static string TagOf(string path) => string.IsNullOrEmpty(path) ? null : TagParser.Extract(TagParser.GetOwnName(path));

        private (SourceConfiguration Source, string Id)? Resolve(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            var source = _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(tag), SourceContent.Albums);
            return source == null ? null : (source, _resolver.ExtractIdWithoutPrefix(tag));
        }

        /// <summary>
        /// The people of one album, photo or video, each once per job. Complete = false when an API source that had to be asked
        /// did not answer: the list may then miss people who are still linked.
        /// </summary>
        public async Task<(List<CastMember> Members, bool Complete)> ResolveAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var result = new List<CastMember>();
            var complete = true;
            async Task<CustomMetadataResponse> Ask(SourceConfiguration source, string id)
            {
                var answer = await _photos.ReadAlbumAsync(source, id, cancellationToken);
                complete &= answer != null;
                return answer;
            }

            void Add(IEnumerable<CastMember> members)
            {
                foreach (var member in members ?? Enumerable.Empty<CastMember>())
                {
                    if (!string.IsNullOrWhiteSpace(member?.Name) && !result.Any(r => Key(r) == Key(member)))
                    {
                        result.Add(member);
                    }
                }
            }

            var album = SourceRefreshService.IsAlbum(item);
            var ownTag = TagOf(item.Path) ?? CustomId.Get(item.ProviderIds);

            // 1. What the element itself says
            if (album)
            {
                if (Resolve(ownTag) is { } own)
                {
                    if (own.Source.IsLocal)
                    {
                        Add(_people.ResolveCast(_local.FindAlbum(own.Source, own.Id)?.Cast));
                    }
                    else
                    {
                        Add((await Ask(own.Source, own.Id))?.Cast);
                    }
                }

                if (ownTag != null)
                {
                    Add(_people.ResolveCast(_overlay.Get("album", new[] { ownTag })));
                }
            }
            else
            {
                if (Resolve(TagOf(Path.GetDirectoryName(item.Path))) is { } parent && parent.Source.IsLocal)
                {
                    var file = Path.GetFileName(item.Path);
                    Add(_people.ResolveCast(_local.FindAlbum(parent.Source, parent.Id)?.Photos?.FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase))?.Cast));
                }
                else
                {
                    if (Resolve(TagOf(Path.GetDirectoryName(item.Path))) is { } listing)
                    {
                        await Ask(listing.Source, listing.Id);
                    }

                    var match = await _photos.GetPhotoAsync(item.Path, item.ProviderIds, cancellationToken, () => complete = false);
                    Add(match?.Data.Cast);
                }

                if (ownTag != null)
                {
                    Add(_people.ResolveCast(_overlay.Get("media", new[] { ownTag })));
                }
            }

            // 2. What the albums above give to their whole content, nearest first
            for (var folder = Path.GetDirectoryName(item.Path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                var tag = TagOf(folder);
                if (tag == null)
                {
                    continue;
                }

                if (Resolve(tag) is { } above)
                {
                    if (above.Source.IsLocal)
                    {
                        Add(_people.ResolveCast(_local.FindAlbum(above.Source, above.Id)?.Cast?.Where(c => c.All)));
                    }
                    else if (above.Source.PhotoInheritAlbumCast)
                    {
                        Add((await Ask(above.Source, above.Id))?.Cast);
                    }
                }

                Add(_people.ResolveCast(_overlay.GetCascading("album", new[] { tag })));
            }

            return (result, complete);
        }

        private static string Key(CastMember member) => (member.Name ?? string.Empty).Trim().ToLowerInvariant() + "|" + CastMapper.ToPersonInfo(member).Type;

        private static string Key(PersonInfo person) => (person.Name ?? string.Empty).Trim().ToLowerInvariant() + "|" + person.Type;

        /// <summary>Records the people of an item (and writes a video's into Jellyfin). Never throws: a link must not stop a refresh.</summary>
        public async Task ApplyAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!(Plugin.Instance?.Configuration?.EnableCasting ?? true) || string.IsNullOrEmpty(item?.Path) || item.IsLocked)
            {
                return;
            }

            try
            {
                var known = _store.Get(item.Id);
                // Nothing tagged on the way down to the item and nothing recorded: not ours
                if (known == null && !item.Path.Contains('{') && CustomId.Get(item.ProviderIds) == null)
                {
                    return;
                }

                var (members, complete) = await ResolveAsync(item, cancellationToken);
                if (!complete && known != null)
                {
                    // A source that does not answer takes nobody away: the links already known stay until it answers
                    members.AddRange(known.Members.Where(k => !members.Any(m => Key(m) == Key(k))));
                    _logger.LogWarning("👥 [ALBUM CAST] ⚠️ A source did not answer for '{Name}': its known people are kept", item.Name);
                }

                var record = new AlbumCastRecord
                {
                    Members = members.Select((m, i) => new CastMember { Name = m.Name.Trim(), Role = m.Role, Type = CastMapper.ToPersonInfo(m).Type.ToString(), Order = i, Image = Reachable(m.Image) }).ToList()
                };

                if (item.SupportsPeople)
                {
                    var wanted = members.Select(CastMapper.ToPersonInfo).ToList();
                    var wantedKeys = wanted.Select(Key).ToHashSet();
                    var written = new HashSet<string>(known?.Written ?? new List<string>());
                    var existing = _library.GetPeople(item) ?? new List<PersonInfo>();
                    // People that are not ours (typed in Jellyfin, another provider) stay as they are, even when a link names
                    // them too: only what the plugin wrote is ever replaced or taken back
                    var keep = existing.Where(p => !written.Contains(Key(p))).ToList();
                    var theirs = keep.Select(Key).ToHashSet();
                    var merged = keep.Concat(wanted.Where(p => !theirs.Contains(Key(p)))).ToList();
                    for (var i = 0; i < merged.Count; i++)
                    {
                        merged[i].SortOrder = i;
                    }

                    if (!merged.Select(p => Key(p) + "|" + p.Role).OrderBy(k => k, StringComparer.Ordinal)
                        .SequenceEqual(existing.Select(p => Key(p) + "|" + p.Role).OrderBy(k => k, StringComparer.Ordinal)))
                    {
                        await _library.UpdatePeopleAsync(item, merged, cancellationToken);
                    }

                    record.Written = wantedKeys.Where(k => !theirs.Contains(k)).ToList();
                }

                _store.Set(item.Id, record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "👥 [ALBUM CAST] ⚠️ Cannot set the people of '{Name}': {Message}", item.Name, ex.Message);
            }
        }

        /// <summary>A photo uploaded to the plugin is addressed from the server itself: a browser needs the relative address</summary>
        private static string Reachable(string image)
        {
            var at = image?.IndexOf("CustomMetadata/People/Photo/", StringComparison.OrdinalIgnoreCase) ?? -1;
            return at >= 0 ? image.Substring(at) : image;
        }
    }
}
