# Custom Metadata Provider — Jellyfin plugin

A Jellyfin plugin that enriches your media (movies, series, seasons, episodes) from **your own sources**: a REST API of your choice, records you type in yourself, and a shared directory of people for the cast.

A file or folder whose name carries a `{prefix-ID}` tag (for example `{dm-1234}`) is sent to the source that owns that prefix. Files without a tag are left alone: Jellyfin's other providers keep handling them.

> **Status: alpha (`0.0.x`).** The plugin is built for **Jellyfin 12.4** (see [Compatibility](#compatibility)). The plugin's user interface is currently in **French**; the screen names below give the French label in quotes.

## What it does

- **Four kinds of sources**
  - **API**: title, overview, date, rating, tags, genres, cast and images read from your API's JSON, with a configurable field mapping (dotted paths).
  - **Local**: series, seasons and episodes typed in the plugin, including episodes you do not have yet. The data lives in the plugin, not in Jellyfin's database, so a forced "Replace all metadata" never erases it. Images can be uploaded or given as web addresses.
  - **Dump**: one call returns a whole catalogue of standalone episodes (`{ "data": [ … ] }`). The plugin keeps it in memory and finds each file in it, by its `{prefix-id}` tag or, without a tag, by its name (slug, exact name, fuzzy score with a configurable threshold). A "Catalogues" view of the missing-episodes screen shows what the dump holds that your library does not. See [docs/DUMP_SOURCE.md](docs/DUMP_SOURCE.md).
  - **People**: an API that describes people (name, photo, biography), queried by identifier.
- **Missing episodes**: from an episode list provided by the API (or a local source), the plugin creates greyed-out "virtual" episodes for those that have no file, with thumbnail, duration, overview and a preview video. A dedicated screen ("Épisodes manquants") lists what is missing, with a "copy name" button (`Title {prefix-ID}`) to name the future file.
- **Photo albums** (API and local sources, off by default): tagged album folders, sub-albums included, and their photos get their metadata. See [Photo albums](#photo-albums).
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

Requires the .NET 10 SDK.

```bash
./build.sh        # cleans, then builds in Release mode
# → bin/Release/net10.0/Jellyfin.Plugin.CustomMetadata.dll
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

### Photo albums

For a library of the type « home videos and photos » with photos enabled. In the source's editor, tick **Albums photo** under « Contenu de cette source » (a source may hold videos, albums, or both).

```
Photos/
└── Voyage {dm-12}/                 ← album (a folder holding only sub-albums works too)
    └── Bretagne {dm-31}/           ← sub-album, with its own tag
        ├── phare.jpg               ← found by its file name in the answer of album 31
        └── plage {dm-7}.jpg        ← or by its own tag
```

- **API source**: an album is read from `/albums/{id}` (« Albums photo » in the editor: tabs « Album » and « Photos et vidéos » for the address and the JSON fields). Its answer may list its photos and videos, by default `"photos": [{ "file": "phare.jpg", "title": "…", "overview": "…", "tags": ["…"] }]`. A file with its own tag is read from `/photos/{id}`. An option adds the album's tags to them.
- **Local source**: « Albums photo »: a tree of albums, each with its form, its two images and its photos and videos (« Lire les fichiers du dossier » lists them once the folder carries the tag and has been scanned).
- What the source gives wins over what is read in the file (EXIF of a photo, probe of a video); an empty field keeps the file's value. Jellyfin reads the file again at each refresh, so **lock the item** in Jellyfin to keep something typed there.
- **Large libraries** (with the [web theme](#web-theme-optional)'s script): the first visit of an album waits for the server to make each size from the original files. Turn on « Préparer les images des photothèques » to have them made every night (task of the same name in the dashboard, to run it at once); count roughly 0.2 to 0.4 GB of Jellyfin cache per 1,000 photos, and a long first run.
- A library scan does not pick up a change of the source: use « Actualiser » on the source's card, « Appliquer » on a local album, or refresh the album in Jellyfin.
- **People**: link them to an album (and, ticked, to its whole content), a photo or a video, from the API (`"cast"` in the album's answer or in a file's entry), a local album, or the People screen (« Par identifiant »). Videos get them in Jellyfin's cast; **for photos and albums Jellyfin keeps no cast, so they show only with the web theme's script** (album page, viewer, library filter, person page).
- Not available: dump sources. In the stock web client, a click on an album shows its photos and its description is on its page; the [web theme](#web-theme-optional)'s script gives albums a page of their own.

## Stored data

| What | Where |
|---|---|
| Configuration (sources, settings) | `/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml` |
| Directory, casts, local sources, caches | the plugin's data folder: `plugins/Jellyfin.Plugin.CustomMetadata/` (`people.json`, `episode-cast.json`, `local-sources/`, `cache/`, `missing-episodes-cache/`) |

Include these files in your backups: local sources and the people directory exist nowhere else.

## Episode photo gallery (optional)

Turn on « Galerie photos des épisodes » in the plugin’s settings, then reload jellyfin-web (Ctrl+F5). An episode that has photos gets a « Photos » button: a wall of its photos at their own ratios, and a full-size viewer (arrows, Escape, swipe). It works with or without the web theme, in the web client only.

Photos come from two places, listed in this order:

1. the images of type **Photo** in the episode’s gallery of a local source (an episode of the source itself, or local data laid over another source’s episode through its tag). They are shown on missing episodes too, and are never applied as the episode’s image;
2. the sub-folder of the video’s folder named after the episode number Jellyfin knows: `3`, `03`, `E03`, or a range such as `03-04` (JPEG, PNG, WebP, GIF, AVIF; natural order; hidden files and links leaving the folder are ignored).

```
Season 01/
  Show - S01E03.mkv
  03/            <- photos of episode 3
    img001.jpg
```

The same photos are offered in **Edit images → Search** (« Photos de l’épisode »), to pick one as the episode image; a scan or a refresh never picks one by itself. With the gallery on, a numbered folder holding photos and no video stays out of the library (otherwise Jellyfin may list it as an empty season; one already created disappears at the next scan). Thumbnails are cached in Jellyfin’s cache folder (`custom-metadata/photo-thumbs`).

If you used the separate « Episode Gallery » plugin, uninstall it: your folders work as they are.

## Web theme (optional)

`theme/projection.css` is a dark theme for jellyfin-web, usable with or without the plugin: detail pages whose header adapts to the item (with a backdrop, a band of about half a screen shows the picture and the title and synopsis sit on its lower edge; without one, a compact header with no empty band), a series' seasons as a wall of large posters where watched seasons shrink in place (made for series with dozens of seasons), episodes as a grid of 16:9 cards (rows on phones), missing episodes shown as empty frames. Put one of these lines in Dashboard → General → Custom CSS code:

- **With the plugin installed** (recommended): the plugin serves the theme it was built with, so it changes when the plugin is updated, with no cache to wait for. The relative path also works when Jellyfin runs under a base URL.

  ```css
  @import url("../CustomMetadata/Theme/projection.css");
  ```

- **Without the plugin**: from the repository through jsDelivr, which caches the file for up to 12 hours after a change.

  ```css
  @import url("https://cdn.jsdelivr.net/gh/maximepicault/jellyfin-plugin-metaweave@main/theme/projection.css");
  ```

**Theme script (optional, plugin only).** In the plugin's settings, « Améliorations du thème Projection » adds the theme's scripts (`theme/projection.js`, `projection-index.js` and `projection-photos.js`) to jellyfin-web's pages: photo libraries as a wall of albums (search, tags and genres), an album page headed by its picture whatever its shape, a photo viewer with the photo's details beside it; a hover preview of episodes (their trickplay thumbnails, or the preview video of a missing episode), « S1 · É3 » and previous / next links on episode pages, the episode's own picture as the backdrop when it is large enough, a series' progress (« 625 / 1 076 vus »), « Reprendre · S27 É2 » on its play button and a bar on each season started, episode numbers on season pages, the backdrop shown at its own ratio, and the « Index »: genre, tag, studio and person lists rebuilt with the filter as their title, episodes included, and every genre, tag, person, year and playback state to narrow them from one searchable panel. A library's Series, Episodes and Films views use the same screen; the plugin searches the library on the server and sends it a page at a time. The plugin inserts the script tag into the page as it is served (nothing is written to Jellyfin's files); turning the setting off restores the stock page at the next load (Ctrl+F5).

The French labels it adds (for example "Manquant" on missing episodes, a label jellyfin-web leaves in English) are in sections 9b and 13 of the file.

## Troubleshooting

- Logs are very detailed and prefixed by component: `journalctl -u jellyfin -f | grep -E "PROVIDER|API CLIENT|CACHE|MISSING|PEOPLE|LOCAL SOURCE"`.
- An image or response refused with HTTP 403 (which `curl` does not get) often comes from the identity the plugin sends: adjust the User-Agent, `Origin` and `Referer` in the source's "Réseau" (network) section. The test button shows the headers sent.
- A title or poster that does not change after you edit your API: the cache keeps responses for `CacheTTLHours` hours. A manual "Replace all metadata" ignores it.
- A `poster.png` placed in a media folder always wins (Jellyfin's local-image priority).

## Compatibility

The plugin is compiled against `Jellyfin.Controller` / `Jellyfin.Model` **12.4.0**, targets **.NET 10**, and uses the SkiaSharp version shipped with that server. **Jellyfin 10.11 is no longer supported**: this build does not load on it (version 0.0.8 and earlier were built for 10.11.6). Building against a newer version than the server's can prevent the plugin from loading. For another 12.x server, align the versions in [`Jellyfin.Plugin.CustomMetadata.csproj`](Jellyfin.Plugin.CustomMetadata.csproj).

## Development

- Branching, commit, changelog and release rules: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- Architecture, conventions and pitfalls (jellyfin-web, cache, tags, cast…): [`CLAUDE.md`](CLAUDE.md).
- **Test bench**: `tools/ui-test/ui-test.sh` builds the plugin, starts a Jellyfin 12.4 in Docker (http://localhost:8097, `admin`/`admin`) with a fake API and demo media, then takes screenshots. The `tools/ui-test/*.mjs` scripts drive the interface in Chrome (`config-local-ui.mjs` runs without Docker, against a fake server).
- There is no unit-test suite: verification is done with the test bench and by reading the logs.

## License

[GNU GPL v3](LICENSE).
