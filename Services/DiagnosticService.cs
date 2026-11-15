using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// "Why is this element not enriched?": replays, for one file or folder name, the chain the providers follow
    /// (tag → source → library → API call → mapped fields → images) and stops at the first step that blocks.
    /// Also lists the library elements the plugin could not take in charge, with checks that cost no API call.
    /// Read-only: it never changes a library, an item or the configuration.
    /// </summary>
    public class DiagnosticService
    {
        public const string MetadataProviderName = "Custom Metadata Provider";
        public const string ImageProviderName = "Custom Image Provider";
        private const int MaxListed = 500;

        // Tags other tools put in names with the same {prefix-id} shape: not ours, never reported as an unknown source
        private static readonly HashSet<string> ForeignPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tmdb", "tmdbid", "imdb", "imdbid", "tvdb", "tvdbid", "edition"
        };

        private static readonly JsonSerializerOptions FileOptions = new JsonSerializerOptions { WriteIndented = true };
        private readonly ILibraryManager _library;
        private readonly CustomApiClient _api;
        private readonly ILogger<DiagnosticService> _logger;
        private readonly object _lock = new object();
        private DiagnosticList _last;

        public DiagnosticService(ILibraryManager library, CustomApiClient api, ILogger<DiagnosticService> logger)
        {
            _library = library;
            _api = api;
            _logger = logger;
        }

        private static string FilePath()
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata");
            return Path.Combine(root, "diagnostic.json");
        }

        private static List<SourceConfiguration> Sources() => Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>();

        private static string PrefixOf(string tag)
        {
            var dash = (tag ?? string.Empty).IndexOf('-');
            return dash > 0 ? tag.Substring(0, dash) : string.Empty;
        }

        /// <summary>The enabled source a tag resolves to (API first, like SourceResolver), else a disabled one, else null</summary>
        private static (SourceConfiguration Source, bool Enabled) FindSource(string prefix)
        {
            var candidates = Sources()
                .Where(s => !s.IsPeople && !string.IsNullOrEmpty(s.Prefix) && s.Prefix.Trim().Equals(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.IsLocal)
                .ToList();
            var enabled = candidates.FirstOrDefault(s => s.Enabled);
            return enabled != null ? (enabled, true) : (candidates.FirstOrDefault(), false);
        }

        private static string OwnName(BaseItem item) => string.IsNullOrEmpty(item.Path) ? string.Empty : Path.GetFileName(item.Path.TrimEnd('/', '\\'));

        private static string KindLabel(BaseItem item) => item is Series ? "Série" : (item is Season ? "Saison" : "Épisode");

        private static string KindPlural(BaseItem item) => item is Series ? "séries" : (item is Season ? "saisons" : "épisodes");

        /// <summary>Providers of ours that the item's library has switched off for its type (empty = nothing missing)</summary>
        private List<string> MissingProviders(BaseItem item)
        {
            var missing = new List<string>();
            var config = Plugin.Instance?.Configuration;
            try
            {
                var options = _library.GetLibraryOptions(item);
                var typeName = item is Series ? "Series" : (item is Season ? "Season" : "Episode");
                // No entry for a type: Jellyfin uses every provider
                var entry = options?.TypeOptions?.FirstOrDefault(o => string.Equals(o.Type, typeName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    return missing;
                }

                if (config?.EnableMetadata != false && !(entry.MetadataFetchers ?? Array.Empty<string>()).Contains(MetadataProviderName))
                {
                    missing.Add(MetadataProviderName);
                }

                if (config?.EnableImages != false && !(entry.ImageFetchers ?? Array.Empty<string>()).Contains(ImageProviderName))
                {
                    missing.Add(ImageProviderName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "🩺 [DIAGNOSTIC] Library options unavailable for {Name}", item.Name);
            }

            return missing;
        }

        private List<BaseItem> LibraryItems()
        {
            return _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            }).Where(i => !string.IsNullOrEmpty(i.Path)).ToList();
        }

        // ------------------------------------------------------------------ one element, step by step

        public async Task<DiagnosticResult> DiagnoseAsync(string name, string kind, CancellationToken cancellationToken)
        {
            name = (name ?? string.Empty).Trim();
            var result = new DiagnosticResult { Name = name };
            var titles = new[] { "Tag dans le nom", "Source du préfixe", "Médiathèque", "Appel de la source", "Champs trouvés", "Images" };
            DiagnosticStep Step(int index, string state, string detail, string action = null, string actionLabel = null)
            {
                var step = new DiagnosticStep { Title = titles[index], State = state, Detail = detail, Action = action, ActionLabel = actionLabel };
                result.Steps.Add(step);
                return step;
            }

            void Finish(string verdict, string detail)
            {
                result.Ok = result.Steps.All(s => s.State != "fail");
                result.Verdict = verdict;
                result.VerdictDetail = detail;
                for (var i = result.Steps.Count; i < titles.Length; i++)
                {
                    Step(i, "skip", "Non vérifié : une étape précédente bloque");
                }
            }

            // 1. the tag, read from the name alone (a full path would pick a parent folder's tag)
            var own = Path.GetFileName(name.TrimEnd('/', '\\'));
            var tag = TagParser.Extract(own);
            if (string.IsNullOrEmpty(tag))
            {
                Step(0, "fail", "Aucun {préfixe-ID} dans ce nom");
                Finish("Bloqué à l'étape 1 : le nom ne porte pas de tag.", "Ajoutez un tag {préfixe-ID} au nom du fichier ou du dossier : sans lui, l'élément est laissé aux autres fournisseurs de Jellyfin.");
                return result;
            }

            result.Tag = tag;
            var prefix = PrefixOf(tag);
            var id = tag.Substring(prefix.Length + 1);
            var extra = TagParser.ExtractAll(own).Count - 1;
            Step(0, "ok", "Tag {" + tag + "} : préfixe " + prefix + ", identifiant " + id
                + (extra > 0 ? " (" + extra + " autre" + (extra > 1 ? "s tags" : " tag") + " dans le nom : le premier sert de référence)" : string.Empty));

            // 2. the source of that prefix
            var (source, enabled) = FindSource(prefix);
            result.Prefix = prefix;
            if (source == null)
            {
                var known = Sources().Where(s => !s.IsPeople && !string.IsNullOrEmpty(s.Prefix)).Select(s => s.Prefix.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Step(1, "fail", "Aucune source n'a le préfixe « " + prefix + " »" + (known.Count > 0 ? ". Préfixes configurés : " + string.Join(", ", known) : ". Aucune source n'est configurée"), "sources", "Voir les sources");
                Finish("Bloqué à l'étape 2 : aucune source pour ce préfixe.", "Corrigez le préfixe dans le nom, ou créez une source qui le porte.");
                return result;
            }

            if (!enabled)
            {
                Step(1, "fail", "La source « " + source.Name + " » a ce préfixe mais elle est désactivée", "edit-source", "Ouvrir la source");
                Finish("Bloqué à l'étape 2 : la source est désactivée.", "Tant qu'elle est désactivée, les éléments qui portent son tag sont laissés aux autres fournisseurs de Jellyfin.");
                return result;
            }

            Step(1, "ok", "Source « " + source.Name + " »" + (source.IsLocal ? " (locale)" : string.Empty) + ", activée");

            // 3. the element in the library, and the providers ticked for its type
            var item = LibraryItems().FirstOrDefault(i => SourceRefreshService.TagsOf(i).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
            if (item != null)
            {
                result.ItemId = item.Id;
                result.ItemName = item.Name;
                var missing = MissingProviders(item);
                if (missing.Count > 0)
                {
                    Step(2, "fail", KindLabel(item) + " « " + item.Name + " » : « " + string.Join(" » et « ", missing) + " » " + (missing.Count > 1 ? "ne sont pas cochés" : "n'est pas coché")
                        + " pour les " + KindPlural(item) + " de sa médiathèque", "libraries", "Ouvrir les médiathèques");
                    Finish("Bloqué à l'étape 3 : le plugin n'est pas activé dans la médiathèque.", "Jellyfin n'appelle alors jamais le plugin pour ces éléments, sans rien signaler. Cochez-le dans les réglages de la médiathèque.");
                    return result;
                }

                Step(2, "ok", KindLabel(item) + " « " + item.Name + " » trouvé" + (item is Series ? "e" : (item is Season ? "e" : string.Empty)) + ", le plugin est activé pour les " + KindPlural(item) + " de sa médiathèque");
            }
            else
            {
                Step(2, "info", "Aucun élément de la médiathèque ne porte ce tag pour le moment : la suite est testée sans lui");
            }

            // 4. the call itself, without cache
            var itemType = item is Series ? "series" : (item is Season ? "season" : (item is Episode ? "video" : NormalizeKind(kind, own)));
            var test = await _api.TestSourceAsync(source, itemType, id, cancellationToken);
            var typeLabel = itemType == "series" ? "série" : (itemType == "season" ? "saison" : "épisode");
            if (!test.Success)
            {
                var what = test.StatusCode.HasValue ? "HTTP " + test.StatusCode : (test.Error ?? "aucune réponse");
                Step(3, "fail", "Appel comme " + typeLabel + " : " + what + (string.IsNullOrEmpty(test.Url) ? string.Empty : " sur " + test.Url)
                    + (test.Attempts > 1 ? " après " + test.Attempts + " tentatives" : string.Empty), "test-source", "Tester la source");
                Finish("Bloqué à l'étape 4 : la source ne répond pas correctement.", test.StatusCode == 404
                    ? "La source ne connaît pas l'identifiant « " + id + " » comme " + typeLabel + ". Vérifiez l'identifiant, ou l'endpoint de ce type."
                    : (test.Error ?? "Ouvrez le test de la source pour voir la réponse complète et les en-têtes envoyés."));
                return result;
            }

            Step(3, "ok", "Appel comme " + typeLabel + (source.IsLocal ? " : données de la source locale" : " : HTTP " + (test.StatusCode ?? 200) + " en " + test.DurationMs + " ms"));

            // 5. what the mapping finds in the answer
            var m = test.Metadata;
            var found = new List<string>();
            if (m != null)
            {
                if (!string.IsNullOrWhiteSpace(m.Title)) { found.Add("titre"); }
                if (!string.IsNullOrWhiteSpace(m.Overview)) { found.Add("synopsis"); }
                if (m.PremiereDate.HasValue) { found.Add("date"); }
                if (m.CommunityRating.HasValue) { found.Add("note"); }
                if (m.IndexNumber.HasValue) { found.Add("numéro"); }
                if (m.Tags != null && m.Tags.Count > 0) { found.Add("tags"); }
                if (m.Genres != null && m.Genres.Count > 0) { found.Add("genres"); }
                if (m.Cast != null && m.Cast.Count > 0) { found.Add("casting"); }
            }

            if (found.Count == 0)
            {
                Step(4, "fail", "La source répond, mais aucun champ du mapping n'y trouve de valeur", "test-source", "Revoir les mappings");
                Finish("Bloqué à l'étape 5 : la réponse est vide pour le plugin.", "Les chemins du mapping ne correspondent pas à la réponse. Le test de la source montre la réponse et permet de les corriger.");
                return result;
            }

            Step(4, "ok", "Trouvés : " + string.Join(", ", found));

            // 6. images (a missing image never blocks the metadata)
            var images = test.Images?.Count ?? 0;
            Step(5, images > 0 ? "ok" : "info", images > 0
                ? images + (images > 1 ? " images trouvées" : " image trouvée") + " (" + string.Join(", ", test.Images.Select(i => i.Type.ToString()).Distinct()) + ")"
                : "Aucune image dans la réponse : Jellyfin garde ses propres images");

            // Only series and seasons keep a trace of a successful enrichment (see Scan)
            var neverEnriched = item != null && !(item is Episode) && string.IsNullOrEmpty(CustomId.Get(item.ProviderIds));
            Finish(item == null ? "Tout est prêt : l'élément sera enrichi dès qu'il sera dans la médiathèque."
                    : (neverEnriched ? "Rien ne bloque, mais cet élément n'a pas encore été enrichi." : "Rien ne bloque pour cet élément."),
                item == null ? "Le nom, la source et sa réponse sont bons."
                    : (neverEnriched ? "Lancez « Actualiser les éléments » sur l'onglet Sources, ou un scan de la médiathèque."
                        : "Si ce que vous voyez dans Jellyfin est ancien, actualisez-le avec « Actualiser les éléments » sur l'onglet Sources."));
            return result;
        }

        private static string NormalizeKind(string kind, string ownName)
        {
            var k = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (k == "series" || k == "season")
            {
                return k;
            }

            // A name with a media extension is a file, so an episode; a folder is tried as a season
            return k == "episode" || k == "video" || Path.HasExtension(ownName) ? "video" : "season";
        }

        // ------------------------------------------------------------------ the whole library, without any API call

        public DiagnosticList GetLast()
        {
            lock (_lock)
            {
                if (_last != null)
                {
                    return _last;
                }

                try
                {
                    var path = FilePath();
                    if (File.Exists(path))
                    {
                        _last = JsonSerializer.Deserialize<DiagnosticList>(File.ReadAllText(path));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🩺 [DIAGNOSTIC] ⚠️ Cannot read the last list");
                }

                return _last;
            }
        }

        public DiagnosticList Scan()
        {
            var list = new DiagnosticList { GeneratedAt = DateTime.UtcNow };
            var items = LibraryItems();
            var ourPrefixes = new HashSet<string>(Sources().Where(s => !s.IsPeople && !string.IsNullOrEmpty(s.Prefix)).Select(s => s.Prefix.Trim()), StringComparer.OrdinalIgnoreCase);
            bool IsOurs(BaseItem i) => i != null && TagParser.ExtractAll(OwnName(i)).Any(t => ourPrefixes.Contains(PrefixOf(t)));

            void Add(BaseItem item, string reason, string message, string tag)
            {
                list.Total++;
                list.Counts[reason] = list.Counts.TryGetValue(reason, out var n) ? n + 1 : 1;
                if (list.Entries.Count >= MaxListed)
                {
                    return;
                }

                var series = item is Episode e ? e.SeriesName : (item is Season s ? s.SeriesName : null);
                list.Entries.Add(new DiagnosticEntry
                {
                    ItemId = item.Id, Name = item.Name, Kind = KindLabel(item), FileName = OwnName(item), SeriesName = series,
                    Reason = reason, Message = message, Tag = tag
                });
            }

            foreach (var item in items)
            {
                list.Checked++;
                var tags = TagParser.ExtractAll(OwnName(item));
                if (tags.Count == 0)
                {
                    // Only worth reporting inside something the plugin manages: an untagged series is simply not ours
                    var parentTagged = item is Episode ep ? IsOurs(ep.Season) || IsOurs(ep.Series) : item is Season se && IsOurs(se.Series);
                    if (parentTagged)
                    {
                        Add(item, "notag", item is Episode ? "Aucun tag dans le nom, dans une série que le plugin enrichit" : "Dossier de saison sans tag, dans une série que le plugin enrichit", null);
                    }

                    continue;
                }

                var tag = tags[0];
                var prefix = PrefixOf(tag);
                var (source, enabled) = FindSource(prefix);
                if (source == null)
                {
                    if (!ForeignPrefixes.Contains(prefix))
                    {
                        Add(item, "source", "Aucune source n'a le préfixe « " + prefix + " »", tag);
                    }

                    continue;
                }

                if (!enabled)
                {
                    Add(item, "source", "La source « " + source.Name + " » (" + prefix + ") est désactivée", tag);
                    continue;
                }

                var missing = MissingProviders(item);
                if (missing.Count > 0)
                {
                    Add(item, "library", "« " + string.Join(" » et « ", missing) + " » " + (missing.Count > 1 ? "ne sont pas cochés" : "n'est pas coché") + " pour les " + KindPlural(item) + " de la médiathèque", tag);
                    continue;
                }

                // Series and seasons store the plugin's id when a provider answered. File-backed episodes do not
                // (CustomEpisodeProvider only keeps an id that is already there), so nothing cheap tells for them.
                if (!(item is Episode) && string.IsNullOrEmpty(CustomId.Get(item.ProviderIds)))
                {
                    Add(item, "never", "Jamais enrichi par le plugin : la source n'a pas répondu, ou l'élément n'a pas encore été actualisé", tag);
                }
            }

            _logger.LogInformation("🩺 [DIAGNOSTIC] {Checked} library items checked, {Total} not enriched ({Detail})", list.Checked, list.Total,
                string.Join(", ", list.Counts.Select(c => c.Key + "=" + c.Value)));
            lock (_lock)
            {
                _last = list;
                try
                {
                    var path = FilePath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(list, FileOptions));
                    File.Move(tmp, path, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🩺 [DIAGNOSTIC] ⚠️ Cannot save the list");
                }
            }

            return list;
        }
    }

    public class DiagnosticStep
    {
        public string Title { get; set; }

        /// <summary>"ok", "fail", "info" (nothing wrong, nothing to check) or "skip" (an earlier step blocks)</summary>
        public string State { get; set; }

        public string Detail { get; set; }

        /// <summary>What the page offers on a blocking step: "sources", "edit-source", "test-source" or "libraries"</summary>
        public string Action { get; set; }

        public string ActionLabel { get; set; }
    }

    public class DiagnosticResult
    {
        public string Name { get; set; }
        public string Tag { get; set; }
        public string Prefix { get; set; }
        public Guid? ItemId { get; set; }
        public string ItemName { get; set; }
        public bool Ok { get; set; }
        public string Verdict { get; set; }
        public string VerdictDetail { get; set; }
        public List<DiagnosticStep> Steps { get; set; } = new List<DiagnosticStep>();
    }

    public class DiagnosticEntry
    {
        public Guid ItemId { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string FileName { get; set; }
        public string SeriesName { get; set; }

        /// <summary>"notag", "source", "library" or "never"</summary>
        public string Reason { get; set; }

        public string Message { get; set; }
        public string Tag { get; set; }
    }

    public class DiagnosticList
    {
        public DateTime GeneratedAt { get; set; }

        /// <summary>Library items looked at</summary>
        public int Checked { get; set; }

        /// <summary>Items not enriched (Entries is capped, this is not)</summary>
        public int Total { get; set; }

        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        public List<DiagnosticEntry> Entries { get; set; } = new List<DiagnosticEntry>();
    }
}
