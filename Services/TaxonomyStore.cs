using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// A rule of the "Genres et tags" screen, about ONE term (the match ignores case, accents and extra spaces):
    /// - Ignored: the term is dropped wherever the plugin imports or applies terms;
    /// - ReplaceWith: the term is renamed (several terms replaced by the same name = a merge). When the replacement is the same term
    ///   spelled differently ("docu" → "Docu"), it only fixes the spelling;
    /// - neither: the term is only listed (Created = it was added by hand, even if no item uses it yet).
    /// </summary>
    public class TaxonomyRule
    {
        /// <summary>"genre" or "tag"</summary>
        public string Kind { get; set; }

        public string Name { get; set; }
        public bool Ignored { get; set; }
        public string ReplaceWith { get; set; }
        public bool Created { get; set; }
    }

    public class TaxonomyFile
    {
        public int Version { get; set; } = 1;
        public List<TaxonomyRule> Rules { get; set; } = new List<TaxonomyRule>();
    }

    /// <summary>A term the plugin received from a source, kept even when a rule drops it so that it stays listed (and can be restored)</summary>
    public class SeenTerm
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    /// <summary>
    /// Rules on genres and tags, with the highest authority over everything the plugin gives to Jellyfin: API sources, local sources
    /// and episodes that have a file all go through <see cref="Genres"/> / <see cref="Tags"/> (see the providers), and the "Appliquer"
    /// action of the screen rewrites the items that already hold a term a rule changes. Stored in DataFolderPath/taxonomy.json.
    /// </summary>
    public class TaxonomyStore : IHostedService
    {
        public const string GenreKind = "genre";
        public const string TagKind = "tag";
        private const int MaxChain = 10;
        private const int MaxSeen = 5000;

        private static readonly JsonSerializerOptions FileOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private (DateTime Stamp, TaxonomyFile File, Dictionary<string, TaxonomyRule> Index)? _cached;
        private readonly ConcurrentDictionary<string, SeenTerm> _seen = new ConcurrentDictionary<string, SeenTerm>();
        private bool _seenLoaded;
        private Timer _flush;

        // A hosted service so that the instance exists from the start of the server: the providers reach it through Current, and nothing
        // else would ask the container for it before the first import
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_flush != null) { FlushSeen(); }
            return Task.CompletedTask;
        }

        /// <summary>The instance of the running plugin, for code that is not built by dependency injection (static helpers, providers)</summary>
        public static TaxonomyStore Current { get; private set; }

        public TaxonomyStore(ILogger logger)
        {
            _logger = logger;
            Current = this;
        }

        // ---------------------------------------------------------------- names

        /// <summary>What two spellings of a term share: lower case, no accent, single spaces ("Sci-Fi" and "sci-fi " match, "SciFi" does not)</summary>
        public static string Key(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var space = false;
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }

                if (space)
                {
                    sb.Append(' ');
                    space = false;
                }

                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        public static string NormalizeKind(string kind)
        {
            var k = (kind ?? string.Empty).Trim().ToLowerInvariant();
            return k == GenreKind || k == "genres" ? GenreKind : (k == TagKind || k == "tags" ? TagKind : null);
        }

        private static string IndexKey(string kind, string name) => kind + "|" + Key(name);

        // ---------------------------------------------------------------- the rules

        private static string FilePath()
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata");
            return Path.Combine(root, "taxonomy.json");
        }

        private static Dictionary<string, TaxonomyRule> Index(TaxonomyFile file)
        {
            var index = new Dictionary<string, TaxonomyRule>();
            foreach (var rule in file.Rules)
            {
                index[IndexKey(rule.Kind, rule.Name)] = rule;
            }

            return index;
        }

        public TaxonomyFile Load()
        {
            lock (_lock)
            {
                var path = FilePath();
                if (!File.Exists(path))
                {
                    return new TaxonomyFile();
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cached.HasValue && _cached.Value.Stamp == stamp)
                {
                    return _cached.Value.File;
                }

                try
                {
                    var file = JsonSerializer.Deserialize<TaxonomyFile>(File.ReadAllText(path), FileOptions) ?? new TaxonomyFile();
                    file.Rules ??= new List<TaxonomyRule>();
                    _cached = (stamp, file, Index(file));
                    return file;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🏷️ [TAXONOMY] ❌ Cannot read {Path}", path);
                    return new TaxonomyFile();
                }
            }
        }

        private Dictionary<string, TaxonomyRule> CurrentIndex()
        {
            Load();
            lock (_lock)
            {
                return _cached?.Index ?? new Dictionary<string, TaxonomyRule>();
            }
        }

        /// <summary>
        /// Cleans the rules (valid kinds, trimmed names, one rule per term, "ignore" wins over "replace", a replacement that would lead back
        /// to the term itself is dropped, a rule that does nothing and was not created by hand is dropped) and saves them
        /// </summary>
        public TaxonomyFile Save(TaxonomyFile file)
        {
            var rules = new Dictionary<string, TaxonomyRule>();
            foreach (var raw in file?.Rules ?? new List<TaxonomyRule>())
            {
                var kind = NormalizeKind(raw?.Kind);
                var name = raw?.Name?.Trim();
                if (kind == null || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var replace = raw.Ignored ? null : raw.ReplaceWith?.Trim();
                rules[IndexKey(kind, name)] = new TaxonomyRule { Kind = kind, Name = name, Ignored = raw.Ignored, ReplaceWith = string.IsNullOrEmpty(replace) ? null : replace, Created = raw.Created };
            }

            // A chain of replacements must end: a rule that leads back to its own term (A → B → A) is dropped
            foreach (var rule in rules.Values.Where(r => r.ReplaceWith != null).ToList())
            {
                var own = Key(rule.Name);
                if (Key(rule.ReplaceWith) == own)
                {
                    continue;   // a spelling fix, not a chain
                }

                var next = rule.ReplaceWith;
                for (var step = 0; step < 50 && next != null; step++)
                {
                    if (Key(next) == own) { rule.ReplaceWith = null; break; }
                    next = rules.TryGetValue(IndexKey(rule.Kind, next), out var following) && Key(following.ReplaceWith) != Key(following.Name) ? following.ReplaceWith : null;
                }
            }

            var cleaned = new TaxonomyFile
            {
                Rules = rules.Values
                    .Where(r => r.Ignored || r.ReplaceWith != null || r.Created)
                    .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => Key(r.Name), StringComparer.Ordinal)
                    .ToList()
            };

            lock (_lock)
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cleaned, FileOptions));
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }

                File.Move(tmp, path, true);
                _cached = (File.GetLastWriteTimeUtc(path), cleaned, Index(cleaned));
            }

            _logger.LogInformation("🏷️ [TAXONOMY] 💾 Saved {Count} rule(s) ({Ignored} ignored, {Replaced} replaced)",
                cleaned.Rules.Count, cleaned.Rules.Count(r => r.Ignored), cleaned.Rules.Count(r => r.ReplaceWith != null));
            return cleaned;
        }

        // ---------------------------------------------------------------- applying the rules

        public static string[] Genres(IEnumerable<string> terms) => Alphabetical(Current?.Resolve(GenreKind, terms, true, out _) ?? Plain(terms));

        public static string[] Tags(IEnumerable<string> terms) => Alphabetical(Current?.Resolve(TagKind, terms, true, out _) ?? Plain(terms));

        private static readonly System.Globalization.CompareInfo French = new System.Globalization.CultureInfo("fr-FR").CompareInfo;

        /// <summary>A to Z, ignoring case and accents, so the terms read in order wherever Jellyfin lists them (import points only).</summary>
        private static string[] Alphabetical(IEnumerable<string> terms)
        {
            var list = terms.ToList();
            list.Sort((a, b) => French.Compare(a, b, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace));
            return list.ToArray();
        }

        private static List<string> Plain(IEnumerable<string> terms)
        {
            return (terms ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The final name of a term, or null when it is dropped. {rule} is the rule that decided, if any.</summary>
        public string ResolveOne(string kind, string term, out bool hit)
        {
            hit = false;
            var index = CurrentIndex();
            var name = term.Trim();
            for (var step = 0; step < MaxChain; step++)
            {
                if (!index.TryGetValue(IndexKey(kind, name), out var rule))
                {
                    break;
                }

                if (rule.Ignored)
                {
                    hit = true;
                    return null;
                }

                if (string.IsNullOrWhiteSpace(rule.ReplaceWith))
                {
                    break;
                }

                var sameTerm = Key(rule.ReplaceWith) == Key(name);
                if (!string.Equals(rule.ReplaceWith.Trim(), name, StringComparison.Ordinal)) { hit = true; }
                name = rule.ReplaceWith.Trim();
                if (sameTerm)
                {
                    break;   // a spelling fix ends the chain: looking the same term up again would loop
                }
            }

            return name;
        }

        /// <summary>
        /// The terms after the rules: dropped, renamed, without duplicates (same term, same spelling after the rules). {changed}: a rule
        /// acted on at least one term. {recordSeen}: it is an import, the terms received are remembered for the screen.
        /// </summary>
        public List<string> Resolve(string kind, IEnumerable<string> terms, bool recordSeen, out bool changed)
        {
            changed = false;
            var result = new List<string>();
            var keys = new HashSet<string>();
            foreach (var term in terms ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    continue;
                }

                if (recordSeen)
                {
                    RecordSeen(kind, term.Trim());
                }

                var name = ResolveOne(kind, term, out var hit);
                if (hit) { changed = true; }
                if (name != null && keys.Add(Key(name)))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------- terms seen at import

        private static string SeenPath()
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata");
            return Path.Combine(root, "taxonomy-seen.json");
        }

        private void LoadSeen()
        {
            if (_seenLoaded) { return; }
            lock (_lock)
            {
                if (_seenLoaded) { return; }
                _seenLoaded = true;
                try
                {
                    if (File.Exists(SeenPath()))
                    {
                        foreach (var term in JsonSerializer.Deserialize<List<SeenTerm>>(File.ReadAllText(SeenPath()), FileOptions) ?? new List<SeenTerm>())
                        {
                            _seen[IndexKey(term.Kind, term.Name)] = term;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Cannot read the terms seen at import");
                }
            }
        }

        private void RecordSeen(string kind, string term)
        {
            LoadSeen();
            if (_seen.Count >= MaxSeen && !_seen.ContainsKey(IndexKey(kind, term)))
            {
                return;
            }

            _seen.AddOrUpdate(IndexKey(kind, term),
                _ => new SeenTerm { Kind = kind, Name = term, Count = 1, Last = DateTime.UtcNow },
                (_, old) => { old.Count++; old.Last = DateTime.UtcNow; return old; });
            // Written a little later, once: an import records thousands of terms in a few seconds
            lock (_lock)
            {
                _flush ??= new Timer(_ => FlushSeen(), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            }
        }

        private void FlushSeen()
        {
            lock (_lock)
            {
                _flush?.Dispose();
                _flush = null;
                try
                {
                    var path = SeenPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_seen.Values.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList(), FileOptions));
                    File.Move(tmp, path, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🏷️ [TAXONOMY] ⚠️ Cannot save the terms seen at import");
                }
            }
        }

        public List<SeenTerm> Seen()
        {
            LoadSeen();
            return _seen.Values.ToList();
        }
    }
}
