using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>One video item of the library, with what the theme's filtered lists filter and sort on.</summary>
    public sealed class IndexEntry
    {
        public Guid Id;
        public string Type;
        public string Name;
        public string SortName;
        public Guid? SeriesId;
        public string SeriesName;
        public Guid? SeasonId;
        public string SeasonName;
        public Guid? TopParentId;
        public int? Number;
        public int? SeasonNumber;
        public DateTime? Premiere;
        public DateTime? Created;
        public int? Year;
        public bool IsVirtual;
        public float? Rating;
        public long Ticks;
        public bool HasBackdrop;
        public string[] Genres;
        public string[] Tags;
        public string[] Studios;
        public string[] People;

        /// <summary>The place of the item's season in its series (see <see cref="LibraryIndexService"/>)</summary>
        public int SeasonOrder;
    }

    /// <summary>The library's video items as read at one moment. Shared between requests: never modified.</summary>
    public sealed class IndexSnapshot
    {
        public IReadOnlyList<IndexEntry> Entries;
        public IReadOnlyDictionary<Guid, IndexEntry> ById;
        public long Version;
        public DateTime BuiltUtc;
    }

    /// <summary>
    /// What the theme's filtered lists (« Index ») need to filter, count and page a library on the server: one snapshot of
    /// every video item (genres, tags, studios, cast), kept in memory and read from Jellyfin's database in a few queries
    /// (asking the library item by item for the cast would cost one query per item). READ ONLY: nothing is ever written
    /// (D005). Which items a user may see is asked to Jellyfin's own library (access, parental rating, blocked tags), and
    /// the watched states are read at each request. Log tag <c>🗂️ [LIBRARY INDEX]</c>.
    /// </summary>
    public sealed class LibraryIndexService : IHostedService
    {
        // a scan raises events without pause: a snapshot just built is served as it is
        private static readonly TimeSpan MinAge = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AccessAge = TimeSpan.FromSeconds(60);

        private static readonly BaseItemKind[] Kinds =
        {
            BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo
        };

        private readonly IDbContextFactory<JellyfinDbContext> _database;
        private readonly ILibraryManager _library;
        private readonly ILogger<LibraryIndexService> _logger;
        private readonly ConcurrentDictionary<(Guid User, Guid Parent), (HashSet<Guid> Ids, long Version, DateTime BuiltUtc)> _access = new();
        private readonly object _building = new();
        private IndexSnapshot _snapshot;
        private long _version;

        public LibraryIndexService(IDbContextFactory<JellyfinDbContext> database, ILibraryManager library, ILogger<LibraryIndexService> logger)
        {
            _database = database;
            _library = library;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded += OnLibraryChanged;
            _library.ItemUpdated += OnLibraryChanged;
            _library.ItemRemoved += OnLibraryChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded -= OnLibraryChanged;
            _library.ItemUpdated -= OnLibraryChanged;
            _library.ItemRemoved -= OnLibraryChanged;
            return Task.CompletedTask;
        }

        private void OnLibraryChanged(object sender, ItemChangeEventArgs e)
        {
            if (e?.Item is Episode || e?.Item is Series || e?.Item is Season || e?.Item is Video)
            {
                Interlocked.Increment(ref _version);
            }
        }

        public IndexSnapshot Snapshot()
        {
            var current = _snapshot;
            if (current != null && IsFresh(current))
            {
                return current;
            }

            lock (_building)
            {
                current = _snapshot;
                if (current != null && IsFresh(current))
                {
                    return current;
                }

                _snapshot = Build();
                return _snapshot;
            }
        }

        private bool IsFresh(IndexSnapshot snapshot)
        {
            var age = DateTime.UtcNow - snapshot.BuiltUtc;
            return age < MinAge || (age < MaxAge && snapshot.Version == Interlocked.Read(ref _version));
        }

        /// <summary>
        /// The ids of the video items <paramref name="user"/> may see, in <paramref name="parentId"/> when given. Null for
        /// a scope this index cannot read (a view grouping several libraries).
        /// </summary>
        public HashSet<Guid> Visible(User user, Guid parentId, IndexSnapshot snapshot)
        {
            var key = (user.Id, parentId);
            if (_access.TryGetValue(key, out var kept) && kept.Version == snapshot.Version && DateTime.UtcNow - kept.BuiltUtc < AccessAge)
            {
                return kept.Ids;
            }

            var query = new InternalItemsQuery(user) { IncludeItemTypes = Kinds, Recursive = true };
            HashSet<Guid> ids;
            var parent = parentId == Guid.Empty ? null : _library.GetItemById<BaseItem>(parentId, user);
            if (parentId == Guid.Empty)
            {
                ids = _library.GetItemIds(query).ToHashSet();
            }
            else if (parent == null)
            {
                ids = new HashSet<Guid>();
            }
            else if (parent is CollectionFolder library)
            {
                // a library is not an ancestor of its items: they hang under the folders of its paths
                var folders = library.GetPhysicalFolders().Select(f => f.Id).ToHashSet();
                ids = Visible(user, Guid.Empty, snapshot)
                    .Where(id => snapshot.ById.TryGetValue(id, out var e) && e.TopParentId.HasValue && folders.Contains(e.TopParentId.Value))
                    .ToHashSet();
            }
            else if (parent is UserView)
            {
                return null;
            }
            else
            {
                query.AncestorIds = new[] { parentId };
                ids = _library.GetItemIds(query).ToHashSet();
            }

            _access[key] = (ids, snapshot.Version, DateTime.UtcNow);
            return ids;
        }

        /// <summary>The items the user finished (true) or started (false); the others are untouched.</summary>
        public Dictionary<Guid, bool> Watched(Guid userId)
        {
            using var db = _database.CreateDbContext();
            var rows = db.UserData.AsNoTracking()
                .Where(u => u.UserId == userId && (u.Played || u.PlaybackPositionTicks > 0))
                .Select(u => new { u.ItemId, u.Played })
                .ToList();
            var watched = new Dictionary<Guid, bool>(rows.Count);
            foreach (var row in rows)
            {
                // several rows per item are possible (one per data key): finished wins
                watched[row.ItemId] = row.Played || (watched.TryGetValue(row.ItemId, out var played) && played);
            }

            return watched;
        }

        /// <summary>The person items behind names of the cast: their id, and whether they have a picture.</summary>
        public Dictionary<string, (Guid Id, bool HasImage)> Persons(IReadOnlyCollection<string> names)
        {
            var found = new Dictionary<string, (Guid, bool)>(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0)
            {
                return found;
            }

            using var db = _database.CreateDbContext();
            // the plugin has a Person of its own: Jellyfin's is meant here
            var type = typeof(MediaBrowser.Controller.Entities.Person).FullName;
            var wanted = names.ToList();
            var people = db.BaseItems.AsNoTracking()
                .Where(i => i.Type == type && wanted.Contains(i.Name))
                .Select(i => new { i.Id, i.Name, HasImage = i.Images.Any(m => m.ImageType == ImageInfoImageType.Primary) })
                .ToList();
            foreach (var person in people)
            {
                if (!found.TryGetValue(person.Name, out var kept) || (!kept.Item2 && person.HasImage))
                {
                    found[person.Name] = (person.Id, person.HasImage);
                }
            }

            return found;
        }

        private IndexSnapshot Build()
        {
            var watch = Stopwatch.StartNew();
            var version = Interlocked.Read(ref _version);
            var types = new Dictionary<string, string>
            {
                [typeof(Series).FullName] = "Series",
                [typeof(Season).FullName] = "Season",
                [typeof(Episode).FullName] = "Episode",
                [typeof(Movie).FullName] = "Movie",
                [typeof(Video).FullName] = "Video",
                [typeof(MusicVideo).FullName] = "Video"
            };
            var typeNames = types.Keys.ToList();
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            string Shared(string value) => value == null ? null : strings.TryGetValue(value, out var kept) ? kept : strings[value] = value;
            string[] Split(string values) => string.IsNullOrEmpty(values)
                ? Array.Empty<string>()
                : values.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Shared).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            using var db = _database.CreateDbContext();
            var items = db.BaseItems.AsNoTracking().Where(i => typeNames.Contains(i.Type));

            // extras (trailers, bonus videos) and alternate versions are not items of their own in a list.
            // PrimaryVersionId is read by name: its type changed with Jellyfin 12, reading it this way does not bind to it
            var rows = items.Where(i => i.ExtraType == null && EF.Property<object>(i, "PrimaryVersionId") == null).Select(i => new
            {
                i.Id, i.Type, i.Name, i.SortName, i.SeriesId, i.SeriesName, i.SeasonId, i.SeasonName, i.TopParentId, i.IndexNumber, i.ParentIndexNumber,
                i.PremiereDate, i.DateCreated, i.ProductionYear, i.IsVirtualItem, i.CommunityRating, i.RunTimeTicks, i.Genres, i.Tags, i.Studios
            }).ToList();

            var cast = db.PeopleBaseItemMap.AsNoTracking()
                .Where(m => items.Any(i => i.Id == m.ItemId))
                .Select(m => new { m.ItemId, m.People.Name })
                .ToList()
                .GroupBy(m => m.ItemId)
                .ToDictionary(g => g.Key, g => g.Select(m => Shared(m.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            var backdrops = db.BaseItemImageInfos.AsNoTracking()
                .Where(m => m.ImageType == ImageInfoImageType.Backdrop)
                .Select(m => m.ItemId)
                .Distinct()
                .ToHashSet();

            var entries = rows.Select(r => new IndexEntry
            {
                Id = r.Id,
                Type = types[r.Type],
                Name = r.Name ?? string.Empty,
                SortName = r.SortName ?? r.Name ?? string.Empty,
                SeriesId = r.SeriesId,
                SeriesName = Shared(r.SeriesName),
                SeasonId = r.SeasonId,
                SeasonName = Shared(r.SeasonName),
                TopParentId = r.TopParentId,
                Number = r.IndexNumber,
                SeasonNumber = r.ParentIndexNumber,
                Premiere = r.PremiereDate,
                Created = r.DateCreated,
                Year = r.ProductionYear,
                IsVirtual = r.IsVirtualItem,
                Rating = r.CommunityRating,
                Ticks = r.RunTimeTicks ?? 0,
                HasBackdrop = backdrops.Contains(r.Id),
                Genres = Split(r.Genres),
                Tags = Split(r.Tags),
                Studios = Split(r.Studios),
                People = cast.TryGetValue(r.Id, out var people) ? people : Array.Empty<string>()
            }).ToList();

            OrderSeasons(entries);
            _logger.LogInformation("🗂️ [LIBRARY INDEX] {Count} videos read in {Ms} ms", entries.Count, watch.ElapsedMilliseconds);
            return new IndexSnapshot { Entries = entries, ById = entries.ToDictionary(e => e.Id), Version = version, BuiltUtc = DateTime.UtcNow };
        }

        /// <summary>
        /// Where each season comes in its series: its number, else the number in its name, else (arcs...) its first
        /// release; specials last. Jellyfin often leaves episodes without a season number, so a season is judged as a whole.
        /// </summary>
        private static void OrderSeasons(List<IndexEntry> entries)
        {
            var seasons = entries
                .Where(e => e.Type == "Episode" || e.Type == "Season")
                .GroupBy(e => (Series: e.SeriesId, Season: e.Type == "Season" ? e.Id : e.SeasonId, Name: e.Type == "Episode" && e.SeasonId == null ? e.SeasonName : null))
                .Select(g =>
                {
                    var name = g.Select(e => e.Type == "Season" ? e.Name : e.SeasonName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty;
                    var numbers = g.Select(e => e.Type == "Season" ? e.Number : e.SeasonNumber).Where(n => n.HasValue).Select(n => n.Value).ToList();
                    var special = numbers.Any(n => n <= 0);
                    int? number = numbers.Where(n => n > 0).Select(n => (int?)n).FirstOrDefault();
                    if (number == null)
                    {
                        var digits = new string(name.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
                        number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
                    }

                    return new
                    {
                        g.Key.Series,
                        Items = g.ToList(),
                        Group = special ? 2 : number.HasValue ? 0 : 1,
                        Number = number ?? 0,
                        Date = g.Min(e => e.Premiere) ?? DateTime.MaxValue,
                        Name = name
                    };
                });

            foreach (var series in seasons.GroupBy(s => s.Series))
            {
                var order = 0;
                foreach (var season in series.OrderBy(s => s.Group).ThenBy(s => s.Number).ThenBy(s => s.Date).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var entry in season.Items)
                    {
                        entry.SeasonOrder = order;
                    }

                    order++;
                }
            }
        }
    }
}
