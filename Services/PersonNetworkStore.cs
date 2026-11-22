using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>A kind of link a person can carry (Instagram, IMDb…): how it is named and built, shown on Jellyfin's person page</summary>
    public class PersonNetwork
    {
        /// <summary>Stable identifier referenced by the people (lowercase letters, digits, underscore)</summary>
        public string Id { get; set; }

        /// <summary>Name shown next to the link on Jellyfin's person page</summary>
        public string Name { get; set; }

        /// <summary>Emoji or short symbol put in front of the name (Jellyfin shows no image for a link)</summary>
        public string Icon { get; set; }

        /// <summary>
        /// Image of the network (SVG, PNG, ICO, WebP, JPEG or GIF) stored in the plugin's network-icons folder. Shown in front of the
        /// link on Jellyfin's web page by the style sheet the plugin serves (<see cref="PersonNetworkStore.BuildCss"/>); the emoji
        /// <see cref="Icon"/> is then left out of the link name, since the image replaces it.
        /// </summary>
        public string IconFile { get; set; }

        /// <summary>Address with a {value} placeholder ("https://instagram.com/{value}"); without it the value is the whole address</summary>
        public string UrlTemplate { get; set; }
    }

    /// <summary>The link of one person on one network: a handle/id (filled into the template) or a whole address</summary>
    public class PersonNetworkValue
    {
        public string NetworkId { get; set; }

        public string Value { get; set; }
    }

    public class PersonNetworkFile
    {
        public int Version { get; set; } = 1;

        public List<PersonNetwork> Networks { get; set; } = new List<PersonNetwork>();
    }

    /// <summary>
    /// Global list of networks, stored in DataFolderPath/person-networks.json (defaults until it is first saved).
    /// Jellyfin lists links through provider classes found by reflection at startup, so the list is exposed through a
    /// fixed pool of slots (<see cref="Providers.PersonNetworkUrlProvider"/>) that read this list on every request:
    /// editing it needs no restart, but at most <see cref="MaxNetworks"/> networks exist.
    /// </summary>
    public class PersonNetworkStore
    {
        public const int MaxNetworks = 24;
        public const int MaxIconBytes = 256 * 1024;
        private static readonly Regex IconFilePattern = new Regex(@"^[A-Za-z0-9_\-]{1,60}\.(svg|png|ico|webp|jpg|gif)$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions FileOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private (DateTime Stamp, List<PersonNetwork> Networks)? _cached;

        public PersonNetworkStore(ILogger<PersonNetworkStore> logger)
        {
            _logger = logger;
        }

        public static List<PersonNetwork> Defaults() => new List<PersonNetwork>
        {
            new PersonNetwork { Id = "website", Name = "Site officiel", Icon = "🌐", UrlTemplate = "{value}" },
            new PersonNetwork { Id = "instagram", Name = "Instagram", Icon = "📷", UrlTemplate = "https://www.instagram.com/{value}" },
            new PersonNetwork { Id = "x", Name = "X", Icon = "🐦", UrlTemplate = "https://x.com/{value}" },
            new PersonNetwork { Id = "facebook", Name = "Facebook", Icon = "👥", UrlTemplate = "https://www.facebook.com/{value}" },
            new PersonNetwork { Id = "youtube", Name = "YouTube", Icon = "▶️", UrlTemplate = "https://www.youtube.com/{value}" },
            new PersonNetwork { Id = "tiktok", Name = "TikTok", Icon = "🎵", UrlTemplate = "https://www.tiktok.com/@{value}" },
            new PersonNetwork { Id = "imdb", Name = "IMDb", Icon = "🎬", UrlTemplate = "https://www.imdb.com/name/{value}" },
            new PersonNetwork { Id = "wikipedia", Name = "Wikipédia", Icon = "📖", UrlTemplate = "{value}" }
        };

        private static string FilePath()
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata");
            return Path.Combine(root, "person-networks.json");
        }

        public List<PersonNetwork> Load()
        {
            lock (_lock)
            {
                var path = FilePath();
                if (!File.Exists(path))
                {
                    return Defaults();
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cached.HasValue && _cached.Value.Stamp == stamp)
                {
                    return _cached.Value.Networks;
                }

                try
                {
                    var file = JsonSerializer.Deserialize<PersonNetworkFile>(File.ReadAllText(path), FileOptions) ?? new PersonNetworkFile();
                    var networks = Normalize(file.Networks);
                    _cached = (stamp, networks);
                    return networks;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🔗 [NETWORKS] ❌ Cannot read {Path}", path);
                    return Defaults();
                }
            }
        }

        public List<PersonNetwork> Save(List<PersonNetwork> networks)
        {
            var clean = Normalize(networks);
            lock (_lock)
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new PersonNetworkFile { Networks = clean }, FileOptions));
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }

                File.Move(tmp, path, true);
                _cached = (File.GetLastWriteTimeUtc(path), clean);
            }

            PruneIcons(clean.Select(n => n.IconFile));
            _logger.LogInformation("🔗 [NETWORKS] 💾 Saved {Count} networks", clean.Count);
            return clean;
        }

        public PersonNetwork Find(string id) => string.IsNullOrEmpty(id) ? null : Load().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Unique ids (generated from the name when missing), cleaned texts, no more than <see cref="MaxNetworks"/></summary>
        public static List<PersonNetwork> Normalize(IEnumerable<PersonNetwork> networks)
        {
            var result = new List<PersonNetwork>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var network in networks ?? Enumerable.Empty<PersonNetwork>())
            {
                var name = string.IsNullOrWhiteSpace(network?.Name) ? null : network.Name.Trim();
                if (name == null || result.Count >= MaxNetworks)
                {
                    continue;
                }

                var id = Regex.Replace((network.Id ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9_]", string.Empty);
                if (id.Length == 0)
                {
                    id = Regex.Replace(PeopleStore.NormalizeName(name).Replace(' ', '_'), @"[^a-z0-9_]", string.Empty);
                }

                if (id.Length == 0) { id = "network"; }
                var unique = id;
                for (var i = 2; !used.Add(unique); i++) { unique = id + i; }

                result.Add(new PersonNetwork
                {
                    Id = unique,
                    Name = name,
                    Icon = string.IsNullOrWhiteSpace(network.Icon) ? null : network.Icon.Trim(),
                    IconFile = network.IconFile != null && IconFilePattern.IsMatch(network.IconFile) ? network.IconFile : null,
                    UrlTemplate = string.IsNullOrWhiteSpace(network.UrlTemplate) ? "{value}" : network.UrlTemplate.Trim()
                });
            }

            return result;
        }

        // ---- Icon images ----

        private static string IconsFolder() => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata"), "network-icons");

        /// <summary>Real type from the first bytes. An SVG with anything that can run code is refused (error set)</summary>
        public static (string Mime, string Extension)? DetectIcon(byte[] d, out string error)
        {
            error = null;
            if (d == null || d.Length < 8) { error = "Fichier vide"; return null; }
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ("image/png", ".png");
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ("image/jpeg", ".jpg");
            if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return ("image/gif", ".gif");
            if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ("image/webp", ".webp");
            if (d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) return ("image/x-icon", ".ico");
            var text = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, MaxIconBytes));
            if (Regex.IsMatch(text, @"<svg[\s>]", RegexOptions.IgnoreCase))
            {
                if (Regex.IsMatch(text, @"<script|<foreignObject|javascript:|\son\w+\s*=", RegexOptions.IgnoreCase))
                {
                    error = "Ce SVG contient du code (script, événement) : il est refusé";
                    return null;
                }

                return ("image/svg+xml", ".svg");
            }

            error = "Format non reconnu (SVG, PNG, ICO, WebP, JPEG ou GIF attendu)";
            return null;
        }

        /// <summary>Stores an icon and returns its file name (null + error if refused)</summary>
        public string SaveIcon(byte[] data, out string error)
        {
            error = null;
            if (data != null && data.Length > MaxIconBytes) { error = $"Image trop lourde (maximum {MaxIconBytes / 1024} Ko)"; return null; }
            var kind = DetectIcon(data, out error);
            if (kind == null) { return null; }
            var name = "n-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + kind.Value.Extension;
            lock (_lock)
            {
                Directory.CreateDirectory(IconsFolder());
                File.WriteAllBytes(Path.Combine(IconsFolder(), name), data);
            }

            return name;
        }

        public bool TryReadIcon(string file, out byte[] data, out string mime)
        {
            data = null;
            mime = null;
            if (string.IsNullOrEmpty(file) || !IconFilePattern.IsMatch(file)) { return false; }
            var path = Path.Combine(IconsFolder(), file);
            if (!File.Exists(path)) { return false; }
            data = File.ReadAllBytes(path);
            mime = DetectIcon(data, out _)?.Mime;
            return mime != null;
        }

        private void PruneIcons(IEnumerable<string> used)
        {
            var keep = new HashSet<string>(used.Where(u => u != null), StringComparer.OrdinalIgnoreCase);
            var folder = IconsFolder();
            if (!Directory.Exists(folder)) { return; }
            foreach (var file in Directory.GetFiles(folder))
            {
                // One day of grace: an icon sent but not saved yet must survive until the save
                if (!keep.Contains(Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
            }
        }

        /// <summary>
        /// Style sheet that puts each network's image in front of its links on Jellyfin's item page. A link is recognised by the
        /// start of its address, which is the template up to {value}: a template that starts with {value} has no fixed start
        /// and gets no image. Image addresses are relative to the style sheet.
        /// </summary>
        public string BuildCss()
        {
            var css = new StringBuilder("/* Custom Metadata plugin: icons of the person networks */\n");
            foreach (var network in Load().Where(n => n.IconFile != null))
            {
                var template = network.UrlTemplate ?? string.Empty;
                var at = template.IndexOf("{value}", StringComparison.Ordinal);
                var prefix = at >= 0 ? template.Substring(0, at) : template;
                if (!Regex.IsMatch(prefix, @"^https?://[^\s""'\\]+$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                css.Append($".itemExternalLinks a.button-link[href^=\"{prefix}\"]::before {{ content: \"\"; display: inline-block; width: 1.15em; height: 1.15em; margin-right: .35em; vertical-align: -0.2em; background: url(\"IconFile/{network.IconFile}\") center / contain no-repeat; }}\n");
            }

            return css.ToString();
        }

        /// <summary>Downloads the icon of the site a link template points to: the page's declared icon, else /favicon.ico</summary>
        public static async Task<(byte[] Data, string Error)> FetchFaviconAsync(HttpClient client, string template, CancellationToken cancellationToken)
        {
            var sample = (template ?? string.Empty).Replace("{value}", "x");
            if (!Uri.TryCreate(sample, UriKind.Absolute, out var site) || (site.Scheme != Uri.UriSchemeHttp && site.Scheme != Uri.UriSchemeHttps))
            {
                return (null, "Le modèle d'adresse doit commencer par http:// ou https:// pour trouver le favicon du site");
            }

            if (IsPrivateHost(site.Host))
            {
                return (null, "Adresse locale refusée");
            }

            client.Timeout = TimeSpan.FromSeconds(15);
            var root = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
            var candidates = new List<Uri>();
            try
            {
                var html = await client.GetStringAsync(root, cancellationToken);
                foreach (Match m in Regex.Matches(html.Length > 200000 ? html.Substring(0, 200000) : html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = m.Value;
                    var rel = Regex.Match(tag, @"rel\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
                    var href = Regex.Match(tag, @"href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (rel.Contains("icon") && href.Length > 0 && Uri.TryCreate(root, System.Net.WebUtility.HtmlDecode(href), out var found) && (found.Scheme == Uri.UriSchemeHttp || found.Scheme == Uri.UriSchemeHttps) && !IsPrivateHost(found.Host))
                    {
                        candidates.Add(found);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // The page itself is unreachable or slow: /favicon.ico is tried anyway
            }

            candidates.Add(new Uri(root, "/favicon.ico"));
            foreach (var candidate in candidates.Distinct().Take(4))
            {
                try
                {
                    var data = await client.GetByteArrayAsync(candidate, cancellationToken);
                    if (data.Length > 0 && data.Length <= MaxIconBytes && DetectIcon(data, out _) != null)
                    {
                        return (data, null);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                }
            }

            return (null, $"Aucun favicon trouvé sur {root.Host}");
        }

        private static bool IsPrivateHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (!IPAddress.TryParse(host, out var ip)) { return false; }
            if (IPAddress.IsLoopback(ip)) { return true; }
            var b = ip.GetAddressBytes();
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                : ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || b[0] == 0xFC || b[0] == 0xFD;
        }

        /// <summary>Address of one link: the value filled into the template, or the value itself when it is already an address. Only http(s) results</summary>
        public static string BuildUrl(PersonNetwork network, string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (value == null)
            {
                return null;
            }

            var url = Uri.TryCreate(value, UriKind.Absolute, out var whole) && (whole.Scheme == Uri.UriSchemeHttp || whole.Scheme == Uri.UriSchemeHttps)
                ? value
                : (network?.UrlTemplate ?? "{value}").Replace("{value}", Uri.EscapeDataString(value.TrimStart('@')).Replace("%2F", "/"));
            return PeopleStore.NormalizeWebsite(url);
        }
    }
}
