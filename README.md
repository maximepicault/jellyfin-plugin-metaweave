# Custom Metadata Provider — Jellyfin plugin

A Jellyfin plugin that enriches your media (movies, series, seasons, episodes) from **your own sources**: a REST API of your choice, records you type in yourself, and a shared directory of people for the cast.

A file or folder whose name carries a `{prefix-ID}` tag (for example `{dm-1234}`) is sent to the source that owns that prefix. Files without a tag are left alone: Jellyfin's other providers keep handling them.

> **Status: alpha (`0.0.x`).** The plugin is built against **Jellyfin 10.11.6** (see [Compatibility](#compatibility)). The plugin's user interface is currently in **French**; the screen names below give the French label in quotes.

## What it does

- **Three kinds of sources**
  - **API**: title, overview, date, rating, tags, genres, cast and images read from your API's JSON, with a configurable field mapping (dotted paths).
  - **Local**: series, seasons and episodes typed in the plugin, including episodes you do not have yet. The data lives in the plugin, not in Jellyfin's database, so a forced "Replace all metadata" never erases it. Images can be uploaded or given as web addresses.
  - **People**: an API that describes people (name, photo, biography), queried by identifier.
- **Missing episodes**: from an episode list provided by the API (or a local source), the plugin creates greyed-out "virtual" episodes for those that have no file, with thumbnail, duration, overview and a preview video. A dedicated screen ("Épisodes manquants") lists what is missing, with a "copy name" button (`Title {prefix-ID}`) to name the future file.
- **Cast**: a people directory shared by all sources (deduplicated by identifier and by name, with photos), hand-entered casts for local episodes, and casts for series, seasons and episodes of API sources attached by tag, without modifying the API.
- **Files that bundle several episodes**: `Ep {dm-1} {dm-2}.mkv` merges the metadata (the first tag is the reference, lists are merged without duplicates, episode range `3-4`).
- **Images**: posters, backdrops, logos, banners and thumbnails, fetched with the source's HTTP settings; optional conversion of portrait thumbnails to 16:9.
- **Network robustness**: per-attempt timeout, retries with growing delays, concurrent-request limit, memory + disk cache, and a "Test source" button.
- **HTML entities** (`&amp;`, `&quot;`…) are decoded in titles, overviews, tags and cast.

## Installation

### From a release (recommended)

1. Download `Jellyfin.Plugin.CustomMetadata.dll` from the [latest release](../../releases/latest).
2. Copy it into Jellyfin's plugin folder and restart:
   ```bash
   sudo mkdir -p /var/lib/jellyfin/plugins/CustomMetadata
   sudo cp Jellyfin.Plugin.CustomMetadata.dll /var/lib/jellyfin/plugins/CustomMetadata/
   sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/CustomMetadata
   sudo systemctl restart jellyfin
   ```
3. Three shortcuts appear under **Extensions** in the administration menu: **Custom Metadata**, **Épisodes manquants** (missing episodes) and **Personnes** (people).

For automatic updates from the releases, see [`deploy/README.md`](deploy/README.md).

### From source

Requires the .NET 9 SDK.

```bash
./build.sh        # cleans, then builds in Release mode
# → bin/Release/net9.0/Jellyfin.Plugin.CustomMetadata.dll
```

## Getting started

1. **Administration menu → Extensions → Custom Metadata → "Ajouter une source"** (add a source), then choose its type.
2. **API** source: a prefix (no hyphen, for example `dm`), the base URL, the endpoints (`/videos/{id}`, `/sources/{id}`, `/series/{id}`) and the JSON field mapping. The "Tester" (test) button calls the API with the form's current values and shows what would be extracted.
3. Name your media with the source's tag:
   ```
   Series/
   └── The Adventurers {dm-adventurers}/            ← series  : "series" endpoint
       └── Season 2024 {dm-a1}/                     ← season  : "source/season" endpoint
           ├── E01 The departure {dm-a1v1}.mkv      ← episode : "video" endpoint
           └── E02 First bivouac {dm-a1v2}.mkv
   ```
   The prefix is read up to the **first** hyphen. A tag is only looked up in the item's **own** file or folder name (never in the full path).
4. Run a library scan. To apply a change to items that are already enriched, use "Replace all metadata".

The fields, the JSON path notation and sample responses are described in [`docs/`](docs/), in particular [`JSON_MAPPING_GUIDE.md`](docs/JSON_MAPPING_GUIDE.md) and [`GUIDE_TAGS.md`](docs/GUIDE_TAGS.md).

### Missing episodes

Set the source's "episode list" endpoint (for example `/sources/{id}/videos`): `{id}` is the identifier of the season folder. The analysis (a daily scheduled task, or the "Analyser" button of the screen) compares the API's list with the files you have. "Réanalyser sans cache" reprocesses everything.

### Cast

The **Personnes** screen holds the directory (add by identifier from a people source, or enter by hand with a photo) and the **distribution** tab for series, seasons and episodes of API sources, by tag or by a typed identifier. After a change, **"Appliquer aux épisodes"** refreshes only the affected episodes. The "Importer le casting" (import cast) setting must be on.

## Stored data

| What | Where |
|---|---|
| Configuration (sources, settings) | `/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml` |
| Directory, casts, local sources, caches | the plugin's data folder: `plugins/Jellyfin.Plugin.CustomMetadata/` (`people.json`, `episode-cast.json`, `local-sources/`, `cache/`, `missing-episodes-cache/`) |

Include these files in your backups: local sources and the people directory exist nowhere else.

## Troubleshooting

- Logs are very detailed and prefixed by component: `journalctl -u jellyfin -f | grep -E "PROVIDER|API CLIENT|CACHE|MISSING|PEOPLE|LOCAL SOURCE"`.
- An image or response refused with HTTP 403 (which `curl` does not get) often comes from the identity the plugin sends: adjust the User-Agent, `Origin` and `Referer` in the source's "Réseau" (network) section. The test button shows the headers sent.
- A title or poster that does not change after you edit your API: the cache keeps responses for `CacheTTLHours` hours. A manual "Replace all metadata" ignores it.
- A `poster.png` placed in a media folder always wins (Jellyfin's local-image priority).

## Compatibility

The plugin is compiled against `Jellyfin.Controller` / `Jellyfin.Model` **10.11.6**, targets **.NET 9**, and uses the SkiaSharp version shipped with that server. Building against a newer version than the server's can prevent the plugin from loading. For another 10.11.x server, align the versions in [`Jellyfin.Plugin.CustomMetadata.csproj`](Jellyfin.Plugin.CustomMetadata.csproj).

## Development

- Branching, commit, changelog and release rules: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- Architecture, conventions and pitfalls (jellyfin-web 10.11, cache, tags, cast…): [`CLAUDE.md`](CLAUDE.md).
- **Test bench**: `tools/ui-test/ui-test.sh` builds the plugin, starts a Jellyfin 10.11.6 in Docker (http://localhost:8097, `admin`/`admin`) with a fake API and demo media, then takes screenshots. The `tools/ui-test/*.mjs` scripts drive the interface in Chrome (`config-local-ui.mjs` runs without Docker, against a fake server).
- There is no unit-test suite: verification is done with the test bench and by reading the logs.

## License

[GNU GPL v3](LICENSE).
