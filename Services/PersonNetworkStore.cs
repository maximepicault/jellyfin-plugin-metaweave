using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                    UrlTemplate = string.IsNullOrWhiteSpace(network.UrlTemplate) ? "{value}" : network.UrlTemplate.Trim()
                });
            }

            return result;
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
