using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>Someone a source links to an album or a file (never edited by the People screen)</summary>
    public class CatalogSourcePerson
    {
        public string Name { get; set; }
        public string Image { get; set; }
    }

    /// <summary>One album of the photo libraries, as the People screen shows it</summary>
    public class CastCatalogAlbum
    {
        public string Id { get; set; }

        /// <summary>Id of the album above, null at the top of a library</summary>
        public string ParentId { get; set; }

        public string Library { get; set; }

        public string Name { get; set; }

        /// <summary>Key of its distribution on the People screen; null for the files loose at the root of a library</summary>
        public string Key { get; set; }

        public bool IsRoot { get; set; }

        public bool IsLocked { get; set; }

        /// <summary>Albums directly below</summary>
        public int Albums { get; set; }

        /// <summary>Photos and videos at every depth</summary>
        public int Photos { get; set; }

        public int Videos { get; set; }

        /// <summary>Photos and videos directly in the album</summary>
        public int Files { get; set; }

        /// <summary>Item whose primary image stands for the album</summary>
        public string CoverId { get; set; }

        /// <summary>Keys of its direct files that have a distribution saved on the People screen</summary>
        public List<string> LinkedFiles { get; set; } = new List<string>();

        /// <summary>People the sources link to the album itself, as of its last application</summary>
        public List<CatalogSourcePerson> Source { get; set; } = new List<CatalogSourcePerson>();

        /// <summary>People the sources link to something below the album</summary>
        public List<CatalogSourcePerson> InsideSource { get; set; } = new List<CatalogSourcePerson>();
    }

    /// <summary>One photo or video of an album</summary>
    public class CastCatalogFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Key { get; set; }
        public bool IsVideo { get; set; }
        public bool IsLocked { get; set; }
        public bool HasImage { get; set; }
        public List<CatalogSourcePerson> Source { get; set; } = new List<CatalogSourcePerson>();
    }

    public class CastCatalogFiles
    {
        public int Total { get; set; }
        public List<CastCatalogFile> Files { get; set; } = new List<CastCatalogFile>();
    }

    /// <summary>
    /// The photo libraries for the People screen: their albums (the whole tree at once) and, album by album, their photos and
    /// videos. Reads only the library and the links already recorded: no source is asked.
    /// </summary>
    public class AlbumCatalog
    {
        private readonly ILibraryManager _library;
        private readonly CastOverlayStore _overlay;
        private readonly AlbumCastStore _store;

        public AlbumCatalog(ILibraryManager library, CastOverlayStore overlay, AlbumCastStore store)
        {
            _library = library;
            _overlay = overlay;
            _store = store;
        }

        /// <summary>
        /// The keys under which the People screen may have linked this album or file: the tag of its own name (else the "custom"
        /// id typed in Jellyfin) first, then the key of an element without identifier.
        /// </summary>
        public static IEnumerable<string> ScreenKeys(BaseItem item)
        {
            var tag = string.IsNullOrEmpty(item.Path) ? null : TagParser.Extract(TagParser.GetOwnName(item.Path));
            var named = tag ?? CustomId.Get(item.ProviderIds);
            if (!string.IsNullOrWhiteSpace(named))
            {
                yield return CastOverlayStore.NormalizeKey(named);
            }

            yield return CastCatalog.SyntheticKey(item);
        }

        /// <summary>The key the screen edits: the first one that already has a distribution, else the first one</summary>
        private string EditKey(string type, BaseItem item)
        {
            var keys = ScreenKeys(item).ToList();
            return keys.FirstOrDefault(k => _overlay.Get(type, new[] { k }).Count > 0) ?? keys[0];
        }

        private List<CatalogSourcePerson> SourcePeople(Guid itemId)
        {
            var record = _store.Get(itemId);
            if (record?.FromSource == null || record.FromSource.Count == 0)
            {
                return new List<CatalogSourcePerson>();
            }

            var fromSource = new HashSet<string>(record.FromSource, StringComparer.Ordinal);
            return record.Members
                .Where(m => fromSource.Contains(AlbumCastService.Key(m)))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CatalogSourcePerson { Name = g.First().Name, Image = g.First().Image })
                .ToList();
        }

        private static void Merge(List<CatalogSourcePerson> into, IEnumerable<CatalogSourcePerson> people)
        {
            foreach (var person in people)
            {
                if (!into.Any(p => string.Equals(p.Name, person.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    into.Add(person);
                }
            }
        }

        private static bool HasPrimary(BaseItem item) => item.HasImage(ImageType.Primary, 0);

        /// <summary>The libraries that hold photos: « home videos and photos », and mixed ones with at least one photo</summary>
        private IEnumerable<(string Name, List<string> Locations)> PhotoLibraries(List<BaseItem> photos)
        {
            foreach (var folder in _library.GetVirtualFolders())
            {
                var locations = (folder.Locations ?? Array.Empty<string>()).Select(l => l.TrimEnd(Path.DirectorySeparatorChar)).ToList();
                var wanted = folder.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.homevideos
                    || ((folder.CollectionType == null || folder.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.mixed)
                        && photos.Any(p => locations.Any(l => Below(p.Path, l))));
                if (wanted)
                {
                    yield return (folder.Name, locations);
                }
            }
        }

        private static bool Below(string path, string folder)
            => !string.IsNullOrEmpty(path) && path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        public List<CastCatalogAlbum> Albums()
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.PhotoAlbum, BaseItemKind.Folder, BaseItemKind.Photo, BaseItemKind.Video },
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
            }).Where(i => (SourceRefreshService.IsAlbum(i) || SourceRefreshService.IsAlbumMedia(i)) && !string.IsNullOrEmpty(i.Path) && !i.IsVirtualItem).ToList();

            var result = new List<CastCatalogAlbum>();
            var linked = new HashSet<string>(
                _overlay.Load().Episodes.Where(e => e.Cast != null && e.Cast.Count > 0 && CastOverlayStore.NormalizeType(e.Type) == "media").Select(e => CastOverlayStore.NormalizeKey(e.Key)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var (name, locations) in PhotoLibraries(items.Where(i => i is Photo).ToList()))
            {
                var own = items.Where(i => locations.Any(l => Below(i.Path, l))).ToList();
                var byId = new Dictionary<Guid, CastCatalogAlbum>();
                var parents = new Dictionary<Guid, Guid>();
                foreach (var item in own.Where(SourceRefreshService.IsAlbum))
                {
                    byId[item.Id] = new CastCatalogAlbum
                    {
                        Id = item.Id.ToString("N"),
                        Library = name,
                        // A tagged folder often keeps its tag in the name: the screen shows the tag apart
                        Name = TagParser.Strip(item.Name) is { Length: > 0 } title ? title : item.Name,
                        Key = EditKey("album", item),
                        IsLocked = item.IsLocked,
                        CoverId = HasPrimary(item) ? item.Id.ToString("N") : null,
                        Source = SourcePeople(item.Id)
                    };
                    parents[item.Id] = item.ParentId;
                }

                foreach (var (id, album) in byId)
                {
                    if (byId.TryGetValue(parents[id], out var parent))
                    {
                        album.ParentId = parent.Id;
                        parent.Albums++;
                    }
                }

                // The album and every album above it (a loop in the parents would otherwise never end)
                IEnumerable<CastCatalogAlbum> UpFrom(Guid id)
                {
                    var seen = new HashSet<Guid>();
                    while (seen.Add(id) && byId.TryGetValue(id, out var album))
                    {
                        yield return album;
                        id = parents[id];
                    }
                }

                foreach (var (id, album) in byId)
                {
                    foreach (var above in UpFrom(parents[id]))
                    {
                        Merge(above.InsideSource, album.Source);
                    }
                }

                var roots = new Dictionary<Guid, CastCatalogAlbum>();
                foreach (var file in own.Where(i => !SourceRefreshService.IsAlbum(i)))
                {
                    if (!byId.TryGetValue(file.ParentId, out var holder) && !roots.TryGetValue(file.ParentId, out holder))
                    {
                        holder = roots[file.ParentId] = new CastCatalogAlbum
                        {
                            Id = file.ParentId.ToString("N"),
                            Library = name,
                            Name = "Photos à la racine de « " + name + " »",
                            IsRoot = true
                        };
                    }

                    holder.Files++;
                    var key = ScreenKeys(file).FirstOrDefault(linked.Contains);
                    if (key != null)
                    {
                        holder.LinkedFiles.Add(key);
                    }

                    var people = SourcePeople(file.Id);
                    var chain = holder.IsRoot ? new[] { holder } : UpFrom(file.ParentId).ToArray();
                    foreach (var above in chain)
                    {
                        if (file is Photo)
                        {
                            above.Photos++;
                        }
                        else
                        {
                            above.Videos++;
                        }

                        above.CoverId ??= HasPrimary(file) ? file.Id.ToString("N") : null;
                        Merge(above.InsideSource, people);
                    }
                }

                result.AddRange(byId.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
                result.AddRange(roots.Values);
            }

            return result;
        }

        /// <summary>The photos and videos directly in one album (or loose at the root of a library), a page at a time</summary>
        public CastCatalogFiles Files(Guid albumId, int start, int limit)
        {
            var files = albumId == Guid.Empty
                ? new List<BaseItem>()
                : _library.GetItemList(new InternalItemsQuery
                {
                    ParentId = albumId,
                    IncludeItemTypes = new[] { BaseItemKind.Photo, BaseItemKind.Video },
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
                }).Where(i => SourceRefreshService.IsAlbumMedia(i) && !string.IsNullOrEmpty(i.Path) && !i.IsVirtualItem).ToList();

            return new CastCatalogFiles
            {
                Total = files.Count,
                Files = files.Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 500)).Select(i => new CastCatalogFile
                {
                    Id = i.Id.ToString("N"),
                    Name = Path.GetFileName(i.Path),
                    Key = EditKey("media", i),
                    IsVideo = i is not Photo,
                    IsLocked = i.IsLocked,
                    HasImage = HasPrimary(i),
                    Source = SourcePeople(i.Id)
                }).ToList()
            };
        }
    }
}
