using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    public class TaxonomyVariant
    {
        public string Name { get; set; }
        public int Items { get; set; }
    }

    /// <summary>One line of the screen: a term as it is in the library, with the rule that applies to it</summary>
    public class TaxonomyTerm
    {
        public string Kind { get; set; }

        /// <summary>What the spellings of the term share (see TaxonomyStore.Key)</summary>
        public string Key { get; set; }

        /// <summary>The spelling shown: the one a rule fixes, else the most used</summary>
        public string Name { get; set; }

        /// <summary>Items that carry it (any spelling)</summary>
        public int Items { get; set; }

        /// <summary>Times a source gave it at import since the plugin counts</summary>
        public int Seen { get; set; }

        /// <summary>The spellings in the library when there are several: Jellyfin keeps "DOCU" and "Docu" as two entries</summary>
        public List<TaxonomyVariant> Variants { get; set; } = new List<TaxonomyVariant>();

        public bool Ignored { get; set; }
        public string ReplaceWith { get; set; }
        public bool Created { get; set; }

        /// <summary>Genres only: Jellyfin cannot open this name (its genre entry gets another name), so clicking it finds no episode</summary>
        public bool Unopenable { get; set; }
    }

    public class TaxonomyImpact
    {
        /// <summary>Items that a rule would change</summary>
        public int Items { get; set; }

        /// <summary>Terms that would be removed / renamed across those items</summary>
        public int Removed { get; set; }

        public int Renamed { get; set; }
    }

    public class TaxonomyApplyStatus
    {
        public bool Running { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Changed { get; set; }
        public int Failed { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// The terms of the library the plugin manages (series, seasons and episodes whose folder or file carries the tag of a source, missing
    /// episodes included), the effect of the rules on them, and their application to the items that are already enriched.
    /// Only the terms a rule hits are changed; everything else on an item is left as it is.
    /// </summary>
    public class TaxonomyService : IHostedService
    {
        private static readonly SemaphoreSlim RunLock = new SemaphoreSlim(1, 1);
        private static TaxonomyApplyStatus _status = new TaxonomyApplyStatus();

        private readonly ILibraryManager _library;
        private readonly TaxonomyStore _store;
        private readonly ILogger<TaxonomyService> _logger;

        public TaxonomyService(ILibraryManager library, TaxonomyStore store, ILogger<TaxonomyService> logger)
        {
            _library = library;
            _store = store;
            _logger = logger;
        }

        public static TaxonomyApplyStatus Status => _status;

        // Jellyfin MERGES the tags of a refreshed item with the ones it already had instead of replacing them: a term a rule dropped at
        // import would stay next to the new ones. So the rules are also applied each time a managed item is saved (idempotent: once the
        // terms are right, nothing changes, which also ends the event this very rewrite raises).
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemUpdated += OnItemChanged;
            _library.ItemAdded += OnItemChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemUpdated -= OnItemChanged;
            _library.ItemAdded -= OnItemChanged;
            return Task.CompletedTask;
        }

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            // Cheap checks first: this event fires for every item of the library
            if (!(e.Item is Series || e.Item is Season || e.Item is Episode))
            {
                return;
            }

            var item = e.Item;
            if (!IsManaged(item))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_store.Load().Rules.Count > 0 && Change(item, out var genres, out var tags, out _, out _))
                    {
                        if (genres != null) { item.Genres = genres; }
                        if (tags != null) { item.Tags = tags; }
                        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
                    }

                    EnsureGenres(item.Genres);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Could not apply the rules to '{Name}'", item.Name);
                }
            });
        }

        // Genre name → Jellyfin can open it. Jellyfin only creates a genre's own entry when something asks for it, and clicking a genre
        // filters on that entry's id: without it the click finds no episode. Done once per name and process.
        private static readonly ConcurrentDictionary<string, bool> Ensured = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Makes sure Jellyfin has an entry for each genre, and remembers the names it cannot open</summary>
        private void EnsureGenres(IEnumerable<string> names)
        {
            foreach (var name in (names ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
            {
                if (Ensured.ContainsKey(name))
                {
                    continue;
                }

                try
                {
                    var genre = _library.GetGenre(name);
                    var ok = genre != null && string.Equals(genre.Name, name, StringComparison.Ordinal);
                    Ensured[name] = ok;
                    if (!ok)
                    {
                        _logger.LogWarning("🏷️ [TAXONOMY] ⚠️ Jellyfin cannot open the genre '{Name}' (its entry is named '{Entry}'): clicking it will find nothing", name, genre?.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Could not create the genre entry '{Name}'", name);
                }
            }
        }

        private static HashSet<string> SourcePrefixes()
        {
            return (Plugin.Instance?.Configuration?.Sources ?? new List<Configuration.SourceConfiguration>())
                .Where(s => !s.IsPeople && !string.IsNullOrWhiteSpace(s.Prefix))
                .Select(s => s.Prefix.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsManaged(BaseItem item, HashSet<string> prefixes = null)
        {
            prefixes ??= SourcePrefixes();
            return prefixes.Count > 0 && SourceRefreshService.TagsOf(item).Any(t =>
            {
                var dash = t.IndexOf('-');
                return dash > 0 && prefixes.Contains(t.Substring(0, dash));
            });
        }

        private List<BaseItem> ManagedItems()
        {
            var prefixes = SourcePrefixes();
            if (prefixes.Count == 0)
            {
                return new List<BaseItem>();
            }

            return _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode }, Recursive = true })
                .Where(i => IsManaged(i, prefixes))
                .ToList();
        }

        private static IEnumerable<string> TermsOf(BaseItem item, string kind)
            => (kind == TaxonomyStore.GenreKind ? item.Genres : item.Tags) ?? Array.Empty<string>();

        /// <summary>Every term of the library and of the rules, one line per term (all its spellings together)</summary>
        public List<TaxonomyTerm> Catalog()
        {
            var rules = _store.Load().Rules;
            var byKey = new Dictionary<string, TaxonomyTerm>();
            var spellings = new Dictionary<string, Dictionary<string, int>>();
            var itemKeys = new Dictionary<string, HashSet<Guid>>();

            TaxonomyTerm Get(string kind, string name)
            {
                var id = kind + "|" + TaxonomyStore.Key(name);
                if (!byKey.TryGetValue(id, out var term))
                {
                    term = new TaxonomyTerm { Kind = kind, Key = TaxonomyStore.Key(name), Name = name.Trim() };
                    byKey[id] = term;
                    spellings[id] = new Dictionary<string, int>(StringComparer.Ordinal);
                    itemKeys[id] = new HashSet<Guid>();
                }

                return term;
            }

            foreach (var item in ManagedItems())
            {
                foreach (var kind in new[] { TaxonomyStore.GenreKind, TaxonomyStore.TagKind })
                {
                    foreach (var name in TermsOf(item, kind).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
                    {
                        var term = Get(kind, name);
                        var id = kind + "|" + term.Key;
                        spellings[id][name] = spellings[id].TryGetValue(name, out var n) ? n + 1 : 1;
                        itemKeys[id].Add(item.Id);
                    }
                }
            }

            foreach (var seen in _store.Seen())
            {
                var term = Get(seen.Kind, seen.Name);
                term.Seen += seen.Count;
            }

            foreach (var rule in rules)
            {
                var term = Get(rule.Kind, rule.Name);
                term.Ignored = rule.Ignored;
                term.ReplaceWith = rule.ReplaceWith;
                term.Created = rule.Created;
                // The target of a rename is a term too: it must be there to be chosen, even when no item has it yet
                if (!string.IsNullOrWhiteSpace(rule.ReplaceWith)) { Get(rule.Kind, rule.ReplaceWith); }
            }

            foreach (var pair in byKey)
            {
                var term = pair.Value;
                var found = spellings[pair.Key];
                term.Items = itemKeys[pair.Key].Count;
                if (found.Count > 1)
                {
                    term.Variants = BySpelling(found).Select(f => new TaxonomyVariant { Name = f.Key, Items = f.Value }).ToList();
                }

                // The spelling a rule fixes ("docu" → "Docu") wins, then the most used one
                var fixedSpelling = term.ReplaceWith != null && TaxonomyStore.Key(term.ReplaceWith) == term.Key ? term.ReplaceWith.Trim() : null;
                term.Name = fixedSpelling ?? (found.Count > 0 ? BySpelling(found).First().Key : term.Name);
                term.Unopenable = term.Kind == TaxonomyStore.GenreKind
                    && (found.Keys.Append(term.Name).Any(n => Ensured.TryGetValue(n, out var ok) && !ok));
            }

            return byKey.Values.OrderBy(t => t.Kind, StringComparer.Ordinal).ThenByDescending(t => t.Items).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The spellings of a term, the one to keep first: the most used, then the one written like a name ("Docu" before "DOCU" and "docu"
        /// when each is used as much), then alphabetical order
        /// </summary>
        private static IEnumerable<KeyValuePair<string, int>> BySpelling(Dictionary<string, int> found)
        {
            static bool LikeAName(string s) => s.Length > 0 && char.IsUpper(s[0]) && !s.Skip(1).All(c => !char.IsLetter(c) || char.IsUpper(c)) || (s.Length == 1 && char.IsUpper(s[0]));
            return found.OrderByDescending(f => f.Value).ThenByDescending(f => LikeAName(f.Key)).ThenBy(f => f.Key, StringComparer.Ordinal);
        }

        /// <summary>
        /// A rename must land on the term that already exists: the target is written as the library spells it (typed "docu" for "Docu"),
        /// and when that term is spelled several ways in the library a spelling rule is added, otherwise the renamed items would keep
        /// a spelling of their own and Jellyfin would list two entries.
        /// </summary>
        public TaxonomyFile NormalizeTargets(TaxonomyFile file)
        {
            var rules = file?.Rules ?? new List<TaxonomyRule>();
            var terms = Catalog().GroupBy(t => t.Kind + "|" + t.Key).ToDictionary(g => g.Key, g => g.First());
            var added = new List<TaxonomyRule>();

            TaxonomyRule RuleOf(string kind, string key)
                => rules.Concat(added).FirstOrDefault(r => TaxonomyStore.NormalizeKind(r.Kind) == kind && TaxonomyStore.Key(r.Name) == key);

            foreach (var rule in rules.Where(r => r != null && !r.Ignored && !string.IsNullOrWhiteSpace(r.ReplaceWith)).ToList())
            {
                var kind = TaxonomyStore.NormalizeKind(rule.Kind);
                var key = TaxonomyStore.Key(rule.ReplaceWith);
                if (kind == null || key == TaxonomyStore.Key(rule.Name))
                {
                    continue;   // a spelling fix chosen by hand
                }

                var targetRule = RuleOf(kind, key);
                string canonical = null;
                if (targetRule != null && !targetRule.Ignored && !string.IsNullOrWhiteSpace(targetRule.ReplaceWith) && TaxonomyStore.Key(targetRule.ReplaceWith) == key)
                {
                    canonical = targetRule.ReplaceWith.Trim();   // the spelling already chosen for the target
                }
                else if (terms.TryGetValue(kind + "|" + key, out var term))
                {
                    canonical = term.Name;
                }

                if (canonical == null)
                {
                    continue;
                }

                rule.ReplaceWith = canonical;
                if (targetRule == null && terms.TryGetValue(kind + "|" + key, out var spelled) && spelled.Variants.Count > 1)
                {
                    added.Add(new TaxonomyRule { Kind = kind, Name = canonical, ReplaceWith = canonical });
                }
            }

            rules.AddRange(added);
            return file;
        }

        // Building the catalog walks every managed item of the library: seconds on a big library. The suggestions come from a copy
        // kept for two minutes (and dropped when the rules change), so typing in the editor never waits for the library.
        private static readonly object NamesLock = new object();
        private static List<TaxonomyTerm> _namesCache;
        private static DateTime _namesAt;

        /// <summary>Forgets the suggestions copy (rules saved, "Appliquer" started)</summary>
        public static void InvalidateNames()
        {
            lock (NamesLock) { _namesCache = null; }
        }

        private List<TaxonomyTerm> SuggestibleTerms()
        {
            lock (NamesLock)
            {
                if (_namesCache == null || DateTime.UtcNow - _namesAt > TimeSpan.FromMinutes(2))
                {
                    _namesCache = Catalog()
                        .Where(t => !t.Ignored && (t.ReplaceWith == null || TaxonomyStore.Key(t.ReplaceWith) == t.Key))
                        .OrderByDescending(t => t.Items).ThenBy(t => t.Key, StringComparer.Ordinal)
                        .ToList();
                    _namesAt = DateTime.UtcNow;
                }

                return _namesCache;
            }
        }

        /// <summary>The names to suggest in a field (not ignored, not renamed away), the most used first.
        /// With a kind, only that kind is returned; with a search text, only the terms containing it (ignoring case and accents),
        /// searched among ALL terms; limit caps each list (default 300, at most 300).</summary>
        public Dictionary<string, List<string>> Names(string kind = null, string search = null, int limit = 300)
        {
            limit = Math.Max(1, Math.Min(limit, 300));
            var key = string.IsNullOrWhiteSpace(search) ? null : TaxonomyStore.Key(search);
            return SuggestibleTerms()
                .Where(t => string.IsNullOrEmpty(kind) || string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Where(t => key == null || t.Key.Contains(key, StringComparison.Ordinal))
                .GroupBy(t => t.Kind)
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(t => key != null && t.Key.StartsWith(key, StringComparison.Ordinal))
                    .Select(t => t.Name).Take(limit).ToList());
        }

        private bool Change(BaseItem item, out string[] genres, out string[] tags, out int removed, out int renamed)
        {
            removed = 0;
            renamed = 0;
            var genreList = _store.Resolve(TaxonomyStore.GenreKind, TermsOf(item, TaxonomyStore.GenreKind), false, out var genresChanged);
            var tagList = _store.Resolve(TaxonomyStore.TagKind, TermsOf(item, TaxonomyStore.TagKind), false, out var tagsChanged);
            genres = genresChanged ? genreList.ToArray() : null;
            tags = tagsChanged ? tagList.ToArray() : null;
            foreach (var (kind, terms, hit) in new[] { (TaxonomyStore.GenreKind, TermsOf(item, TaxonomyStore.GenreKind), genresChanged), (TaxonomyStore.TagKind, TermsOf(item, TaxonomyStore.TagKind), tagsChanged) })
            {
                if (!hit) { continue; }
                foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    var name = _store.ResolveOne(kind, term, out var changed);
                    if (name == null) { removed++; }
                    else if (changed) { renamed++; }
                }
            }

            return genresChanged || tagsChanged;
        }

        /// <summary>What "Appliquer" would change, without changing anything</summary>
        public TaxonomyImpact Impact()
        {
            var impact = new TaxonomyImpact();
            foreach (var item in ManagedItems())
            {
                if (Change(item, out _, out _, out var removed, out var renamed))
                {
                    impact.Items++;
                    impact.Removed += removed;
                    impact.Renamed += renamed;
                }
            }

            return impact;
        }

        /// <summary>
        /// Rewrites, in the background and one item at a time, the managed items that hold a term a rule changes. Nothing is refreshed:
        /// the other metadata of the item (set by other providers or by hand) is not touched.
        /// </summary>
        public TaxonomyApplyStatus Start()
        {
            InvalidateNames();
            if (!RunLock.Wait(0))
            {
                return new TaxonomyApplyStatus { Running = true, Message = "Une application des règles est déjà en cours." };
            }

            var todo = ManagedItems().Where(i => Change(i, out _, out _, out _, out _)).ToList();
            _status = new TaxonomyApplyStatus { Running = true, Total = todo.Count, StartedAt = DateTime.UtcNow };
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var item in todo)
                    {
                        try
                        {
                            if (Change(item, out var genres, out var tags, out _, out _))
                            {
                                if (genres != null) { item.Genres = genres; }
                                if (tags != null) { item.Tags = tags; }
                                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
                                _status.Changed++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _status.Failed++;
                            _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Could not rewrite the terms of '{Name}'", item.Name);
                        }

                        _status.Done++;
                    }
                }
                finally
                {
                    try { EnsureGenres(ManagedItems().SelectMany(i => i.Genres ?? Array.Empty<string>())); }
                    catch (Exception ex) { _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Could not create the genre entries"); }
                    _status.Running = false;
                    _status.FinishedAt = DateTime.UtcNow;
                    _logger.LogInformation("🏷️ [TAXONOMY] ✅ {Changed} item(s) rewritten, {Failed} failed", _status.Changed, _status.Failed);
                    RunLock.Release();
                }
            });
            return _status;
        }
    }
}
