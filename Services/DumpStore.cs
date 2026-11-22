using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>One entry of a dump: what is needed to match a file, plus its raw JSON (served to the mapper)</summary>
    public sealed class DumpEntry
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Slug { get; init; }
        public string SearchTitles { get; init; }
        public string Group { get; init; }

        /// <summary>The entry exactly as the dump gave it</summary>
        public string Raw { get; init; }

        // Pre-normalised for the fuzzy scoring (computed once, at load time)
        internal string NormName { get; init; }
        internal string SpacelessName { get; init; }
        internal string SpacelessSearch { get; init; }
        internal HashSet<string> NameTokens { get; init; }
    }

    /// <summary>The dump of one source, downloaded once and indexed</summary>
    public sealed class DumpCatalog
    {
        public string Signature { get; init; }
        public DateTime LoadedAtUtc { get; init; }
        public List<DumpEntry> Entries { get; init; }
        internal Dictionary<string, DumpEntry> ById { get; init; }
        internal Dictionary<string, DumpEntry> BySlug { get; init; }
        internal Dictionary<string, DumpEntry> ByTitle { get; init; }
    }

    /// <summary>A file matched to an entry of a dump</summary>
    public sealed class DumpMatch
    {
        public SourceConfiguration Source { get; init; }

        /// <summary>Identifier WITHOUT prefix</summary>
        public string Id { get; init; }

        /// <summary>"prefix-id": the value kept in ProviderIds["custom"]</summary>
        public string Tag => $"{Source.Prefix.Trim().ToLowerInvariant()}-{Id}";

        public DumpEntry Entry { get; init; }
        public double Score { get; init; }

        /// <summary>stored | tag | id | slug | title | fuzzy</summary>
        public string How { get; init; }
    }

    /// <summary>
    /// "Dump" sources (SourceType = "Dump"): ONE call returns the whole catalogue, kept in memory for
    /// <see cref="DumpSettings.CacheMinutes"/>. It does two things:
    ///  - serves each entry in place of an API call (ResilientHttp → <see cref="ServeAsync"/>): the providers, the images,
    ///    the cast… work unchanged, as for an API source;
    ///  - finds the entry of a file that has no {prefix-id} tag, by its slug, its name or a fuzzy score
    ///    (<see cref="ResolveAsync"/>, <see cref="FindCandidates"/>).
    /// If a download fails, the previous copy is kept (same URL and paths) rather than breaking every refresh.
    /// </summary>
    public class DumpStore
    {
        private const string Tag = "📦 [DUMP]";

        // Entry URL served to the mapper: the identifier is the last segment ("/items/123")
        public const string ItemEndpoint = "/items/{id}";
        private const string ItemSegment = "items";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly ResilientHttp _http;

        private readonly ConcurrentDictionary<string, DumpCatalog> _catalogs = new ConcurrentDictionary<string, DumpCatalog>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        // Highest score of an entry whose title matches but whose episode number the file does not confirm: just under the default minimum (70).
        // Such an entry is only accepted when it clearly stands out from the others (see DominanceMargin)
        private const double UnconfirmedCap = 69d;

        // An unconfirmed entry is accepted anyway when its score (before the cap) is at least the minimum AND beats the next best entry
        // by this many points, or when it is the only candidate: nothing else to mistake it for. In a series where every episode holds
        // the same title, the right one does not stand out like this, so the wrong-episode risk the cap guards against stays out.
        private const double DominanceMargin = 5d;

        // Most points an entry earns for a main name close to the file's title, when it was found through its other titles
        private const double NameClosenessBonus = 10d;

        // Points taken off for each word of the entry's name that is not in the file's title (up to the maximum)
        private const double ExtraWordPenalty = 3d;
        private const double MaxExtraWordPenalty = 12d;

        private static readonly Regex EpisodeRegex = new Regex(@"[Ss](\d{1,2})[\s._-]?[Ee](\d{1,3})", RegexOptions.Compiled);

        // "Ep 3", "Ep. 3", "Episode 3", "Épisode 3" written in a file name, when there is no SxxExx: also an episode number
        private static readonly Regex WordEpisodeRegex = new Regex(@"(?<![\p{L}\d])(?:ep|[eé]pisode)\.?[\s._-]*(\d{1,3})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // [..], {..} and (..) blocks: ids, resolutions, (VOSTFR), (RAW), (Season 1)…
        private static readonly Regex BracketBlocks = new Regex(@"\[[^\]]*\]|\{[^}]*\}|\([^)]*\)", RegexOptions.Compiled);

        public DumpStore(IHttpClientFactory httpClientFactory, ILogger logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            // Does the actual download: no dump store inside, otherwise it would serve itself
            _http = new ResilientHttp(logger);
        }

        // ------------------------------------------------------------------------------------------------------
        // Loading
        // ------------------------------------------------------------------------------------------------------

        /// <summary>What a cached copy must still equal: another URL or other paths = another dump</summary>
        public static string SignatureOf(SourceConfiguration source)
        {
            var d = source.Dump ?? new DumpSettings();
            return string.Join("|", source.BaseUrl?.Trim(), d.ItemsPath, d.IdPath, d.NamePath, d.SlugPath, d.SearchTitlesPath, d.GroupPath,
                d.PageParameter?.Trim(), d.FirstPage, d.MaxPages);
        }

        /// <summary>The catalogue currently in memory for this source (null when never loaded or loaded with other settings)</summary>
        public DumpCatalog Peek(SourceConfiguration source)
        {
            return _catalogs.TryGetValue(Key(source), out var catalog) && catalog.Signature == SignatureOf(source) ? catalog : null;
        }

        private readonly ConcurrentDictionary<string, string> _lastResponses = new ConcurrentDictionary<string, string>();
        private const int PreviewChars = 3000;

        /// <summary>The start of the last response received for this source (or of its error body): what the editor shows when the dump cannot be read</summary>
        public string LastResponsePreview(SourceConfiguration source)
        {
            return _lastResponses.TryGetValue(Key(source), out var text) ? text : null;
        }

        private void RememberResponse(SourceConfiguration source, string text)
        {
            if (text != null && text.Length > PreviewChars)
            {
                text = text.Substring(0, PreviewChars) + "\n… (suite tronquée)";
            }

            _lastResponses[Key(source)] = text;
        }

        private static string Key(SourceConfiguration source) => (source.Prefix ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>Forgets the copy in memory: the next use downloads the dump again</summary>
        public void Invalidate(SourceConfiguration source)
        {
            _catalogs.TryRemove(Key(source), out _);
        }

        /// <summary>
        /// The dump of this source, downloaded when absent, expired or when force is set.
        /// Never throws for HTTP/parse errors: they come back in <paramref name="error"/> (and the previous copy, if any, is returned).
        /// </summary>
        public async Task<(DumpCatalog Catalog, string Error)> GetCatalogAsync(SourceConfiguration source, bool force, CancellationToken cancellationToken)
        {
            if (source == null || !source.IsDump)
            {
                return (null, "Cette source n'est pas un dump");
            }

            var existing = Peek(source);
            if (!force && existing != null && IsFresh(source, existing))
            {
                return (existing, null);
            }

            var gate = _locks.GetOrAdd(Key(source), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = Peek(source);
                if (!force && existing != null && IsFresh(source, existing))
                {
                    return (existing, null);
                }

                var (catalog, error) = await DownloadAsync(source, cancellationToken).ConfigureAwait(false);
                if (catalog != null)
                {
                    _catalogs[Key(source)] = catalog;
                    return (catalog, null);
                }

                // Failure: the previous copy (same dump) keeps serving
                if (existing != null)
                {
                    _logger.LogWarning("{Tag} ⚠️ Reload of '{Source}' failed ({Error}) - keeping the copy of {Loaded:u}", Tag, source.Name, error, existing.LoadedAtUtc);
                    return (existing, error);
                }

                return (null, error);
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool IsFresh(SourceConfiguration source, DumpCatalog catalog)
        {
            var minutes = Math.Max(1, source.Dump?.CacheMinutes ?? 60);
            return (DateTime.UtcNow - catalog.LoadedAtUtc).TotalMinutes < minutes;
        }

        private async Task<(DumpCatalog Catalog, string Error)> DownloadAsync(SourceConfiguration source, CancellationToken cancellationToken)
        {
            var settings = source.Dump ?? new DumpSettings();
            var url = (source.BaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url) || url.Contains("votre-api.com") || url.Contains("example.com"))
            {
                return (null, "URL du dump absente ou d'exemple (votre-api.com / example.com)");
            }

            _logger.LogInformation("{Tag} Downloading the dump of '{Source}' from {Url}", Tag, source.Name, url);

            var client = _httpClientFactory.CreateClient();
            SourceHeaders.Apply(client, source, !string.IsNullOrEmpty(source.ApiKey) ? source.ApiKey : Plugin.Instance?.Configuration.ApiKey ?? string.Empty,
                _logger, LogLevel.Debug, Tag);
            var paginated = !string.IsNullOrWhiteSpace(settings.PageParameter);
            var maxPages = paginated ? Math.Max(1, settings.MaxPages) : 1;
            var entries = new List<DumpEntry>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedNoId = 0;
            var pagesRead = 0;
            var ended = !paginated;

            try
            {
                for (var i = 0; i < maxPages; i++)
                {
                    var pageUrl = url;
                    if (paginated)
                    {
                        pageUrl = AppendPage(url, settings.PageParameter, settings.FirstPage + i);
                        _logger.LogInformation("{Tag} Page {Page} of '{Source}': {Url}", Tag, settings.FirstPage + i, source.Name, pageUrl);
                    }

                    var fetch = await _http.GetStringAsync(client, pageUrl, source, Tag, cancellationToken).ConfigureAwait(false);
                    RememberResponse(source, fetch.IsSuccess ? fetch.Content : $"HTTP {fetch.StatusCode?.ToString() ?? "(pas de réponse)"} — {fetch.Error}\n{fetch.ErrorBody}");
                    if (!fetch.IsSuccess)
                    {
                        if (i > 0 && fetch.StatusCode == 404)
                        {
                            // Many paginated APIs answer 404 after the last page
                            ended = true;
                            break;
                        }

                        _logger.LogError("{Tag} ❌ Download of '{Source}' failed: {Error} ({Attempts} attempt(s))", Tag, source.Name, fetch.Error, fetch.Attempts);
                        return (null, (paginated ? $"Page {settings.FirstPage + i} : " : string.Empty) + (fetch.Error ?? "Échec du téléchargement"));
                    }

                    using var doc = JsonDocument.Parse(fetch.Content);
                    if (!JsonPathExtractor.TryGetArray(doc.RootElement, settings.ItemsPath, out var elements))
                    {
                        if (i > 0)
                        {
                            ended = true;
                            break;
                        }

                        var where = string.IsNullOrWhiteSpace(settings.ItemsPath) ? "à la racine" : $"au chemin « {settings.ItemsPath} »";
                        return (null, $"Aucune liste d'entrées {where} dans la réponse. {DescribeLists(doc.RootElement)}");
                    }

                    pagesRead++;
                    var added = 0;
                    foreach (var element in elements)
                    {
                        var id = (JsonPathExtractor.GetString(element, settings.IdPath) ?? string.Empty).Trim();
                        if (id.Length == 0)
                        {
                            skippedNoId++;
                            continue;
                        }

                        // An API that ignores the page parameter returns the same page again: nothing new = end
                        if (!seenIds.Add(id))
                        {
                            continue;
                        }

                        added++;
                        var name = HtmlText.Decode(Read(element, settings.NamePath));
                        var slug = Read(element, settings.SlugPath);
                        var normName = Normalize(name);
                        entries.Add(new DumpEntry
                        {
                            Id = id,
                            Name = name,
                            Slug = slug,
                            SearchTitles = Read(element, settings.SearchTitlesPath),
                            Group = HtmlText.Decode(Read(element, settings.GroupPath)),
                            Raw = element.GetRawText(),
                            NormName = normName,
                            SpacelessName = normName.Replace(" ", string.Empty, StringComparison.Ordinal),
                            // The other titles are glued together without a separator: only usable as a substring, never as an equality
                            SpacelessSearch = Normalize(Read(element, settings.SearchTitlesPath)).Replace(" ", string.Empty, StringComparison.Ordinal),
                            NameTokens = normName.Length > 0
                                ? normName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
                                : new HashSet<string>(StringComparer.Ordinal)
                        });
                    }

                    if (paginated && added == 0)
                    {
                        ended = true;
                        break;
                    }
                }

                if (!ended)
                {
                    _logger.LogError("{Tag} ❌ Dump of '{Source}': still not finished after {Max} pages - not kept (raise the page limit)", Tag, source.Name, maxPages);
                    return (null, $"Le dump n'est pas fini après {maxPages} pages : augmentez la limite de pages");
                }

                var byId = new Dictionary<string, DumpEntry>(StringComparer.OrdinalIgnoreCase);
                var bySlug = new Dictionary<string, DumpEntry>(StringComparer.OrdinalIgnoreCase);
                var byTitle = new Dictionary<string, DumpEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    byId[e.Id] = e;
                    if (!string.IsNullOrWhiteSpace(e.Slug))
                    {
                        bySlug[e.Slug.Trim()] = e;
                        var normSlug = Normalize(e.Slug);
                        if (normSlug.Length > 0)
                        {
                            byTitle.TryAdd(normSlug, e);
                        }
                    }

                    if (e.NormName.Length > 0)
                    {
                        byTitle.TryAdd(e.NormName, e);
                    }
                }

                _logger.LogInformation("{Tag} ✅ Dump of '{Source}' loaded: {Count} entries{Pages}{Skipped}, valid {Minutes} min",
                    Tag, source.Name, entries.Count, paginated ? $" in {pagesRead} page(s)" : string.Empty,
                    skippedNoId > 0 ? $" ({skippedNoId} ignored: no id)" : string.Empty, settings.CacheMinutes);

                return (new DumpCatalog
                {
                    Signature = SignatureOf(source),
                    LoadedAtUtc = DateTime.UtcNow,
                    Entries = entries,
                    ById = byId,
                    BySlug = bySlug,
                    ByTitle = byTitle
                }, null);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "{Tag} ❌ The response of '{Source}' is not valid JSON", Tag, source.Name);
                return (null, "La réponse n'est pas du JSON valide : " + ex.Message);
            }
        }

        /// <summary>
        /// A plain name ("page") gives "?page=N". A template holding {page} (e.g. options={"pagination":{"page":{page},"pageSize":100}})
        /// is a whole query parameter: the text before the first "=" is the name, the rest the value with {page} replaced; both are URL-encoded.
        /// </summary>
        internal static string AppendPage(string url, string parameter, int page)
        {
            var spec = (parameter ?? string.Empty).Trim();
            string name;
            string value;
            var eq = spec.IndexOf('=');
            if (spec.Contains("{page}", StringComparison.OrdinalIgnoreCase) && eq > 0)
            {
                name = spec.Substring(0, eq).Trim();
                value = spec.Substring(eq + 1).Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                name = spec;
                value = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // The URL may already carry this parameter (typed by hand in the base URL): the pagination one replaces it
            var queryStart = url.IndexOf('?');
            var root = queryStart >= 0 ? url.Substring(0, queryStart) : url;
            var kept = queryStart >= 0
                ? url.Substring(queryStart + 1).Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(pair => !string.Equals(SafeUnescape(pair.Split('=')[0]), name, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();
            kept.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
            return root + "?" + string.Join("&", kept);
        }

        private static string SafeUnescape(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text);
            }
            catch (UriFormatException)
            {
                return text;
            }
        }

        /// <summary>Tells where the lists of the response are, so that the "list path" can be corrected without guessing</summary>
        internal static string DescribeLists(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                return $"La réponse EST une liste de {root.GetArrayLength()} éléments : laissez « Liste des entrées » vide.";
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return "La réponse n'est ni un objet ni une liste.";
            }

            var found = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    found.Add($"« {property.Name} » ({property.Value.GetArrayLength()} éléments)");
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inner in property.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                    {
                        found.Add($"« {property.Name}.{inner.Name} » ({inner.Value.GetArrayLength()} éléments)");
                    }
                }
            }

            var keys = string.Join(", ", root.EnumerateObject().Select(p => p.Name).Take(12));
            return found.Count > 0
                ? "Listes trouvées : " + string.Join(", ", found.Take(8)) + "."
                : $"Aucune liste dans la réponse (champs : {keys}).";
        }

        private static string Read(JsonElement element, string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : (JsonPathExtractor.GetString(element, path) ?? string.Empty);
        }

        // ------------------------------------------------------------------------------------------------------
        // Serving (in place of an API call)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// ResilientHttp calls this for a dump source: "{base}/items/{id}" answers with the entry's JSON.
        /// Anything else (a series, a season…) is "not found": a dump only holds standalone episodes.
        /// </summary>
        public async Task<HttpFetchResult> ServeAsync(SourceConfiguration source, string url, CancellationToken cancellationToken)
        {
            var result = new HttpFetchResult { Url = url, Attempts = 1, HttpVersion = "dump", StatusCode = 404 };

            string[] parts;
            try
            {
                parts = Uri.UnescapeDataString(new Uri(url).AbsolutePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (UriFormatException)
            {
                result.Error = "URL du dump invalide";
                return result;
            }

            var id = parts.Length >= 2 && parts[^2] == ItemSegment ? parts[^1] : null;
            if (id == null)
            {
                result.Error = "Un dump ne contient que des épisodes autonomes : pas de série ni de saison";
                return result;
            }

            var (catalog, error) = await GetCatalogAsync(source, false, cancellationToken).ConfigureAwait(false);
            if (catalog == null)
            {
                result.StatusCode = 502;
                result.Error = "Dump indisponible : " + error;
                return result;
            }

            if (!catalog.ById.TryGetValue(id, out var entry))
            {
                result.Error = $"Identifiant introuvable dans le dump : {id}";
                _logger.LogWarning("{Tag} ⚠️ Id '{Id}' is not in the dump of '{Source}'", Tag, id, source.Name);
                return result;
            }

            result.StatusCode = 200;
            result.Content = WithRating(entry.Raw, source.Dump);
            return result;
        }

        /// <summary>
        /// Adds "communityRating" = likes / (likes + dislikes) × 10 (a default mapping reads it). An entry that
        /// already has one keeps it.
        /// </summary>
        private static string WithRating(string raw, DumpSettings settings)
        {
            if (settings == null || (string.IsNullOrWhiteSpace(settings.LikesPath) && string.IsNullOrWhiteSpace(settings.DislikesPath)))
            {
                return raw;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var likes = Number(doc.RootElement, settings.LikesPath);
                var dislikes = Number(doc.RootElement, settings.DislikesPath);
                if (likes + dislikes <= 0 || doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.TryGetProperty("communityRating", out _))
                {
                    return raw;
                }

                var node = JsonNode.Parse(raw) as JsonObject;
                if (node == null)
                {
                    return raw;
                }

                node["communityRating"] = Math.Round(likes / (likes + dislikes) * 10d, 1);
                return node.ToJsonString();
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        private static double Number(JsonElement root, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !JsonPathExtractor.TryNavigate(root, path, out var value))
            {
                return 0;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d
                : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s
                : 0;
        }

        // ------------------------------------------------------------------------------------------------------
        // Which entry is this file?
        // ------------------------------------------------------------------------------------------------------

        /// <summary>The enabled dump sources that look for a file by its name</summary>
        private static List<SourceConfiguration> NameMatchingSources()
        {
            return (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>())
                .Where(s => s.Enabled && s.IsDump && s.Dump != null && s.Dump.MatchWithoutTag && !string.IsNullOrWhiteSpace(s.Prefix))
                .ToList();
        }

        private static SourceConfiguration DumpSourceByPrefix(string prefix)
        {
            return (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>())
                .FirstOrDefault(s => s.Enabled && s.IsDump && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds the dump entry of an item, in this order: the "custom" id already stored on it, a {prefix-id} tag of a dump
        /// source in its file name, then (sources that allow it) a bare {id}, the slug, the name, a fuzzy score.
        /// Null when no dump source is configured or nothing matches. Only for items with a file.
        /// </summary>
        public async Task<DumpMatch> ResolveAsync(
            string path,
            string lookupName,
            IReadOnlyDictionary<string, string> providerIds,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var config = Plugin.Instance?.Configuration;
            if (config?.Sources == null || !config.Sources.Any(s => s.Enabled && s.IsDump))
            {
                return null;
            }

            // 1) The id kept on the item by a previous match
            if (CustomId.Get(providerIds) is string stored && SplitTag(stored) is var (storedPrefix, storedId)
                && DumpSourceByPrefix(storedPrefix) is SourceConfiguration storedSource)
            {
                return new DumpMatch { Source = storedSource, Id = storedId, Score = 100, How = "stored" };
            }

            // 2) A tag of a dump source in the file name (the other sources' tags are not ours)
            var ownName = TagParser.GetOwnName(path);
            foreach (var tag in TagParser.ExtractAll(ownName))
            {
                if (SplitTag(tag) is var (prefix, id) && DumpSourceByPrefix(prefix) is SourceConfiguration tagSource)
                {
                    return new DumpMatch { Source = tagSource, Id = id, Score = 100, How = "tag" };
                }
            }

            if (TagParser.Extract(ownName) != null)
            {
                // Tagged for another source: not a file to guess about
                return null;
            }

            // 3) By name, source by source: the best score wins
            DumpMatch best = null;
            foreach (var source in NameMatchingSources())
            {
                var (catalog, error) = await GetCatalogAsync(source, false, cancellationToken).ConfigureAwait(false);
                if (catalog == null)
                {
                    _logger.LogWarning("{Tag} ⚠️ '{Source}' unavailable, cannot match '{File}': {Error}", Tag, source.Name, ownName, error);
                    continue;
                }

                var found = FindCandidates(catalog, source.Dump, path, lookupName, 1, source.Dump.MinScore, out var how).FirstOrDefault();
                if (found.Entry != null && (best == null || found.Score > best.Score))
                {
                    best = new DumpMatch { Source = source, Id = found.Entry.Id, Entry = found.Entry, Score = found.Score, How = how };
                }
            }

            if (best != null)
            {
                _logger.LogInformation("{Tag} 🎯 '{File}' → '{Name}' ({Tag2}, {How}, score {Score:F0})", Tag, ownName, best.Entry.Name, best.Tag, best.How, best.Score);
            }
            else
            {
                // Said at Information level: a dump source is configured, so "nothing matched" is what someone looks for in the log.
                // The nearest miss (and its score against the threshold) tells whether lowering the minimum or tagging the file would help.
                var near = new List<string>();
                foreach (var source in NameMatchingSources())
                {
                    var catalog = Peek(source);
                    if (catalog == null)
                    {
                        continue;
                    }

                    var miss = FindCandidates(catalog, source.Dump, path, lookupName, 1, 40, out _).FirstOrDefault();
                    if (miss.Entry != null)
                    {
                        near.Add($"'{miss.Entry.Name}' ({source.Prefix}-{miss.Entry.Id}) score {miss.Score:F0} < {source.Dump.MinScore}");
                    }
                }

                _logger.LogInformation("{Tag} ❔ No match for '{File}'{Near}", Tag, ownName, near.Count > 0 ? " - nearest: " + string.Join("; ", near) : " - nothing close in the dump");
            }

            return best;
        }

        /// <summary>Candidates for the "Identify" screen: up to <paramref name="limit"/> entries over all name-matching sources, best first</summary>
        public async Task<List<DumpMatch>> SearchAsync(string path, string lookupName, int limit, CancellationToken cancellationToken)
        {
            var results = new List<DumpMatch>();
            foreach (var source in (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>()).Where(s => s.Enabled && s.IsDump && s.Dump != null))
            {
                var (catalog, _) = await GetCatalogAsync(source, false, cancellationToken).ConfigureAwait(false);
                if (catalog == null)
                {
                    continue;
                }

                // Lowered threshold: the person picks
                foreach (var found in FindCandidates(catalog, source.Dump, path, lookupName, limit, Math.Min(40, source.Dump.MinScore), out var how))
                {
                    results.Add(new DumpMatch { Source = source, Id = found.Entry.Id, Entry = found.Entry, Score = found.Score, How = how });
                }
            }

            return results.OrderByDescending(r => r.Score).Take(limit).ToList();
        }

        /// <summary>"mv1-123" → ("mv1", "123")</summary>
        private static (string Prefix, string Id)? SplitTag(string tag)
        {
            var dash = (tag ?? string.Empty).IndexOf('-');
            return dash > 0 && dash < tag.Length - 1 ? (tag.Substring(0, dash), tag.Substring(dash + 1)) : null;
        }

        // ------------------------------------------------------------------------------------------------------
        // Matching a file name against the entries
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Candidates, best score first. A bare {id}, the slug or the exact name answer alone with 100; otherwise every entry is
        /// scored (see <see cref="ComputeScore"/>). <paramref name="how"/> tells which step answered.
        /// </summary>
        public List<(DumpEntry Entry, double Score)> FindCandidates(
            DumpCatalog catalog,
            DumpSettings settings,
            string path,
            string lookupName,
            int limit,
            double minScore,
            out string how)
        {
            how = null;
            var results = new List<(DumpEntry Entry, double Score)>();
            var fileName = string.IsNullOrWhiteSpace(path) ? lookupName : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fileName) || catalog == null)
            {
                return results;
            }

            // A bare {12345} is the entry's id. A number that is not in the dump is AUTHORITATIVE (no guessing from the name);
            // another word ({1080p}, {VOSTFR}) is just noise
            var bare = Regex.Match(fileName, @"\{(\w+)\}", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (bare.Success)
            {
                var bareId = bare.Groups[1].Value;
                if (catalog.ById.TryGetValue(bareId, out var byId))
                {
                    how = "id";
                    results.Add((byId, 100d));
                    return results;
                }

                if (bareId.All(char.IsDigit) && bareId.Length >= 3)
                {
                    _logger.LogWarning("{Tag} ⚠️ Id {{{Id}}} of '{File}' is not in the dump - no match (no guessing from the name)", Tag, bareId, fileName);
                    return results;
                }
            }

            CleanFileName(fileName, settings, out var cleaned, out var episodeNumber, out var tokens);
            if (tokens.Count == 0)
            {
                return results;
            }

            var normFile = string.Join(' ', tokens);
            var spaceless = normFile.Replace(" ", string.Empty, StringComparison.Ordinal);

            if (settings.MatchBySlug && catalog.BySlug.TryGetValue(cleaned.Trim(), out var slugExact))
            {
                how = "slug";
                results.Add((slugExact, 100d));
                return results;
            }

            if ((settings.MatchBySlug || settings.MatchByName) && catalog.ByTitle.TryGetValue(normFile, out var titleExact))
            {
                how = "title";
                results.Add((titleExact, 100d));
                return results;
            }

            // "Title S01E03" → entry named "Title 3" (the SxxExx was removed, put the number back)
            if (episodeNumber > 0 && (settings.MatchBySlug || settings.MatchByName)
                && catalog.ByTitle.TryGetValue(normFile + " " + episodeNumber.ToString(CultureInfo.InvariantCulture), out var titleWithNumber))
            {
                how = "title";
                results.Add((titleWithNumber, 100d));
                return results;
            }

            if (!settings.MatchByName)
            {
                return results;
            }

            var tokenSet = tokens.ToHashSet(StringComparer.Ordinal);
            var suffix = episodeNumber > 0 ? episodeNumber.ToString(CultureInfo.InvariantCulture) : null;

            // Every entry that could matter, with its score before the cap (Raw): the cap alone would put the right episode and the
            // rest of its series at the same level and hide that one of them stands out
            var floor = Math.Min(minScore, 40d);
            var scored = new List<(DumpEntry Entry, double Score, double Raw, bool Unconfirmed)>();
            foreach (var entry in catalog.Entries)
            {
                var (score, raw, unconfirmed) = ComputeScore(entry, tokenSet, spaceless, suffix);
                if (raw >= floor)
                {
                    scored.Add((entry, score, raw, unconfirmed));
                }
            }

            scored.Sort((a, b) => b.Raw.CompareTo(a.Raw));
            DumpEntry dominantEntry = null;
            if (scored.Count > 0 && scored[0].Unconfirmed && scored[0].Raw >= minScore
                && (scored.Count == 1 || scored[0].Raw - scored[1].Raw >= DominanceMargin))
            {
                // The only candidate, or one that stands out: accepted at its real score
                dominantEntry = scored[0].Entry;
                scored[0] = (scored[0].Entry, scored[0].Raw, scored[0].Raw, false);
            }

            foreach (var item in scored.Where(c => c.Score >= minScore).OrderByDescending(c => c.Score).ThenByDescending(c => c.Raw).Take(limit))
            {
                results.Add((item.Entry, item.Score));
            }

            how = results.Count > 0 && results[0].Score >= 100d ? "title" : (dominantEntry != null && results.Count > 0 && results[0].Entry == dominantEntry ? "dominant" : "fuzzy");
            return results;
        }

        /// <summary>
        /// How well a file name matches ONE given entry (the coverage screen shows it for every owned file, to spot the poor matches
        /// and fix their names by hand): 100 for a bare id, the slug or the exact name, otherwise the score before the cap
        /// (an unconfirmed episode number is flagged). Null when the name cannot be read.
        /// </summary>
        public (int Score, string How)? ScoreOf(DumpCatalog catalog, DumpSettings settings, string path, DumpEntry entry)
        {
            var fileName = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fileName) || catalog == null || entry == null || settings == null)
            {
                return null;
            }

            var bare = Regex.Match(fileName, @"\{(\w+)\}", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (bare.Success && string.Equals(bare.Groups[1].Value, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                return (100, "id");
            }

            CleanFileName(fileName, settings, out var cleaned, out var episodeNumber, out var tokens);
            if (tokens.Count == 0)
            {
                return null;
            }

            var normFile = string.Join(' ', tokens);
            if (settings.MatchBySlug && catalog.BySlug.TryGetValue(cleaned.Trim(), out var slugExact) && slugExact == entry)
            {
                return (100, "slug");
            }

            if ((settings.MatchBySlug || settings.MatchByName)
                && ((catalog.ByTitle.TryGetValue(normFile, out var titleExact) && titleExact == entry)
                    || (episodeNumber > 0 && catalog.ByTitle.TryGetValue(normFile + " " + episodeNumber.ToString(CultureInfo.InvariantCulture), out var titleWithNumber) && titleWithNumber == entry)))
            {
                return (100, "title");
            }

            var suffix = episodeNumber > 0 ? episodeNumber.ToString(CultureInfo.InvariantCulture) : null;
            var (_, raw, unconfirmed) = ComputeScore(entry, tokens.ToHashSet(StringComparer.Ordinal), normFile.Replace(" ", string.Empty, StringComparison.Ordinal), suffix);
            return ((int)Math.Round(raw), unconfirmed ? "unconfirmed" : "fuzzy");
        }

        /// <summary>
        /// Score 0-100. Two ways to reach the threshold, the better one is kept:
        ///  - tokens: overlap with the name (55 pts) + reverse overlap (20 pts);
        ///  - other titles: the file name (spaceless) appears in the glued "search titles". Longer = fewer accidental hits:
        ///      title + episode number confirmed → 75-95 (automatic match);
        ///      title found but the file has an SxxExx that is NOT confirmed → 50-65 ("Identify" only: avoids the wrong episode of a series);
        ///      title found, no SxxExx in the file (one-shot) → 65-85.
        /// Bonuses: "title + number" equals the name (100), direct inclusion (+5), name ending with the episode number (+5).
        /// </summary>
        private static (double Score, double Raw, bool Unconfirmed) ComputeScore(DumpEntry entry, HashSet<string> fileTokens, string spacelessFile, string episodeSuffix)
        {
            double score = 0;
            var commonWithName = 0;

            if (entry.NameTokens.Count > 0)
            {
                commonWithName = entry.NameTokens.Count(fileTokens.Contains);
                score += 55d * commonWithName / entry.NameTokens.Count;
                score += 20d * commonWithName / fileTokens.Count;
            }

            if (episodeSuffix != null && entry.SpacelessName.Length > 0
                && string.Equals(entry.SpacelessName, spacelessFile + episodeSuffix, StringComparison.Ordinal))
            {
                return (100d, 100d, false);
            }

            var unconfirmed = false;
            if (spacelessFile.Length >= 6 && entry.SpacelessSearch.Length >= spacelessFile.Length)
            {
                double alt = 0;
                if (episodeSuffix != null)
                {
                    if (entry.SpacelessSearch.Contains(spacelessFile + episodeSuffix, StringComparison.Ordinal))
                    {
                        alt = 75d + Math.Min(20d, spacelessFile.Length);
                    }
                    else if (entry.SpacelessSearch.Contains(spacelessFile, StringComparison.Ordinal))
                    {
                        // The title is in the other titles but not followed by the episode number. The number may still be there, glued
                        // after the entry's OWN name ("name1 3 name2 name3"): then it is this entry's number, and the title found is just
                        // one of its other titles
                        if (NumberFollowsName(entry, episodeSuffix))
                        {
                            alt = 75d + Math.Min(20d, spacelessFile.Length);
                        }
                        else
                        {
                            alt = 50d + Math.Min(15d, spacelessFile.Length);
                            unconfirmed = alt > score;
                        }
                    }
                }
                else if (entry.SpacelessSearch.Contains(spacelessFile, StringComparison.Ordinal))
                {
                    alt = 65d + Math.Min(20d, spacelessFile.Length);
                }

                // When the other titles carry the match (not the name), several entries of a series can reach the same score. The one whose
                // main NAME is closest to the file's title is preferred: up to NameClosenessBonus for the share of the file's words the
                // name holds, minus ExtraWordPenalty for each word the name has that the file does not (a number is the episode, not a
                // word). Of two names that hold the whole title, the one with fewer extra words ranks higher
                if (alt > score)
                {
                    var extraWords = entry.NameTokens.Count(t => !fileTokens.Contains(t) && !t.All(char.IsDigit));
                    var closeness = NameClosenessBonus * commonWithName / Math.Max(1, fileTokens.Count) - Math.Min(MaxExtraWordPenalty, ExtraWordPenalty * extraWords);
                    score = alt + closeness;
                }
            }

            if (spacelessFile.Length >= 4 && entry.SpacelessName.Length >= 4
                && (entry.SpacelessName.Contains(spacelessFile, StringComparison.Ordinal) || spacelessFile.Contains(entry.SpacelessName, StringComparison.Ordinal)))
            {
                score += 5d;
            }

            if (episodeSuffix != null && entry.SpacelessName.EndsWith(episodeSuffix, StringComparison.Ordinal))
            {
                score += 5d;
            }

            // The title is there but the file's episode number is not confirmed: the bonuses above must not push it over the
            // default threshold (70), or "Title S01E03" would be matched to "Title 1". Proposed in "Identify" only
            var raw = Math.Min(100d, score);
            return (Math.Min(unconfirmed ? UnconfirmedCap : 100d, score), raw, unconfirmed);
        }

        /// <summary>
        /// "other titles" often read "{name} {episode number} {title 2} {title 3}", glued together, with an optional "ep" next to the
        /// number ("name ep 3 title 2"): true when the entry's own name is found there directly followed by (or preceded by, with "ep")
        /// exactly this episode number, so "name3other" or "nameep3other" for episode 3, never "name13other"
        /// </summary>
        private static bool NumberFollowsName(DumpEntry entry, string episodeSuffix)
        {
            if (entry.SpacelessName.Length < 3 || string.IsNullOrEmpty(episodeSuffix) || !int.TryParse(episodeSuffix, out var wanted))
            {
                return false;
            }

            var text = entry.SpacelessSearch;
            var from = 0;
            while (from < text.Length)
            {
                var at = text.IndexOf(entry.SpacelessName, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    return false;
                }

                // After the name: [ep | episode] digits
                var pos = at + entry.SpacelessName.Length;
                if (string.CompareOrdinal(text, pos, "episode", 0, 7) == 0) { pos += 7; }
                else if (string.CompareOrdinal(text, pos, "ep", 0, 2) == 0) { pos += 2; }
                if (DigitsAt(text, pos) == wanted)
                {
                    return true;
                }

                // Before the name: "ep" / "episode" digits
                var start = at;
                while (start > 0 && char.IsDigit(text[start - 1])) { start--; }
                if (start < at && DigitsAt(text, start) == wanted
                    && ((start >= 7 && string.CompareOrdinal(text, start - 7, "episode", 0, 7) == 0) || (start >= 2 && string.CompareOrdinal(text, start - 2, "ep", 0, 2) == 0)))
                {
                    return true;
                }

                from = at + 1;
            }

            return false;
        }

        /// <summary>The whole number (up to 4 digits) that starts at this position, or -1</summary>
        private static int DigitsAt(string text, int pos)
        {
            var end = pos;
            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            return end > pos && end - pos <= 4 && int.TryParse(text.AsSpan(pos, end - pos), out var value) ? value : -1;
        }

        private static HashSet<string> NoiseSet(string words)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in (words ?? string.Empty).Split(new[] { ' ', ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = Normalize(word);
                if (norm.Length > 0)
                {
                    set.Add(norm);
                }
            }

            return set;
        }

        /// <summary>
        /// The file name as it is compared: blocks [..], {..}, (..) removed, the episode number taken out (an SxxExx, otherwise
        /// "Ep 3" / "Episode 3"), the ignored words dropped, the rest normalised into tokens. An "Episode 3" that is the WHOLE
        /// name is kept: it is the title of an entry named so.
        /// </summary>
        private static void CleanFileName(string fileName, DumpSettings settings, out string cleaned, out int episodeNumber, out List<string> tokens)
        {
            cleaned = BracketBlocks.Replace(fileName, " ");
            episodeNumber = 0;
            var noise = NoiseSet(settings.NoiseWords);
            List<string> Tokens(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !noise.Contains(t)).ToList();

            var sxe = EpisodeRegex.Match(cleaned);
            if (sxe.Success)
            {
                _ = int.TryParse(sxe.Groups[2].Value, out episodeNumber);
                cleaned = cleaned.Remove(sxe.Index, sxe.Length);
            }
            else
            {
                var word = WordEpisodeRegex.Match(cleaned);
                if (word.Success)
                {
                    var without = cleaned.Remove(word.Index, word.Length);
                    if (Tokens(without).Count > 0)
                    {
                        _ = int.TryParse(word.Groups[1].Value, out episodeNumber);
                        cleaned = without;
                    }
                }
            }

            tokens = Tokens(cleaned);
        }

        /// <summary>Lower case, no accents, every non-alphanumeric run reduced to one space</summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            var lastWasSpace = true;
            foreach (var c in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }

            return sb.ToString().Trim();
        }
    }
}
