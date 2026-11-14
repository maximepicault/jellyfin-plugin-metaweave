# 📝 Changelog - Custom Metadata Plugin

Version history, newest first. It replaces the many fix notes written during development (kept outside the repository): each fix appears here in a few lines.

**Numbering:** `0.0.x alpha`, assigned after the fact to match the Git history. The project had no version number: the "1.0.0" to "1.5.0" entries of the old changelogs correspond to 0.0.1 to 0.0.6, and the work done between February and November, never numbered, is grouped as 0.0.7. Git tags are spelled `v0.0.8`. Ongoing changes go under "Unreleased" (rules: [`CONTRIBUTING.md`](CONTRIBUTING.md)).

## Unreleased

- **API sources**: the mapping panes now explain the `[0]` notation (an API that answers with a list), and a list answer logs a warning when the paths do not start with `[0]`. The paths themselves already handled `[n]` and `||` in every mapping.
- **Local sources**: the id of a local series or season can now be changed. Giving it the tag of an element of an API source (`dm-123`) makes the local element **replace** it: its title, overview, dates, tags, genres and images win over the API's (what it leaves empty keeps the API value), and for a season its episodes become the list of missing episodes, even when the API has no episode-list endpoint. The virtual episodes carry the local source's tags (`{loc-abc}`), and "Copier le nom" uses that prefix.
- **Cast distribution**: series, seasons and episodes **without any `{prefix-id}` tag** are now listed too (a series with no tag at any level used to be missing from the library list). Their cast is attached to a fallback key made from their Jellyfin id, and written straight into Jellyfin by "Appliquer aux épisodes" (and by the daily task), since no provider of the plugin ever sees them. The cascade works from them too. Their distribution follows the folder or file: if it is renamed or moved, it has to be redone.
- **Cast**: a person can now hold several jobs on the same element (actor AND director, for example). Cast entries are unique per person **and** job instead of per person, in the distribution screen, in the episodes of local sources and when a series or season cascades its cast: before, a person already present on an episode made the series' entry for them disappear from it, and adding them a second time was refused. Adding someone already there takes the next free job; the suggestions show what they already do.
- **Cast distribution**: the series are now real entries of the library list (a card with an arrow, their seasons listed under them) instead of small uppercase headings that did not look clickable. A series or a season that carries a tag but has no episode in the library is listed too, so its cast can be set.
- **JSON paths**: fallback between fields with `||`. `title || name || label` uses the first path that exists and is not null, empty or blank, so an API whose fields vary from one item to the next can be mapped (works for every field of every mapping, with list positions and the `[]` notation too). The editor says so in each mapping section.
- **Local sources**: the cast field of an episode now has the same dropdown as the distribution screen (photo, name, identifiers, filtered as you type, arrow keys, a click adds the person) instead of the browser's plain list. The directory is read again when the field gets the focus, so people added in the Personnes screen show up without reopening the source.
- **Local sources**: people are no longer shown as "(personne supprimée)" while the directory is loading or when it could not be read: the row says "Chargement…" or "(annuaire illisible)".
- **People sources**: a new optional "Adresse des photos" field for APIs that return the photo as a **path** (`/abc123.jpg`) hosted on another address than the API (an image server). A prefix (`https://images.example.com/p/w185`) or a template with `{value}` (`https://images.example.com/{value}?w=200`). Left empty, a path is still completed with the API's base URL, and a full address is used as it is.
- **Cast distribution**: adding a person now uses a dropdown with a preview (photo, name, identifiers) filtered as you type, with keyboard navigation, instead of the browser's plain list.
- **Cast distribution**: the library listing (slow on a large library) is no longer loaded when the tab opens. The tab starts on "Par identifiant", and a "Parcourir la médiathèque" button loads the series, seasons and episodes on demand.
- **Cast distribution**: a "cascade" checkbox on a series or a season (saved with its distribution). Everything below it, seasons and episodes, missing ones included, inherits its cast when enriched, without needing the library listing. The episodes' own cast is kept and comes first. A preview lists the season and episode tags it reaches, and "Appliquer aux épisodes" also refreshes them (and cleans them when the cascade is turned off).
- **Cast distribution**: the cascade also reaches seasons and episodes whose name has **no identifier**: their inherited cast is written straight into Jellyfin (nothing else of theirs is touched), kept up to date by "Appliquer aux épisodes" and by the daily missing-episodes task, and taken back when the cascade is turned off.
- **Cast distribution**: "Appliquer aux épisodes" now also refreshes an element whose distribution was emptied entirely, which was skipped before.
- **JSON paths**: a position in a list can now be written `[0]` (`[0].name` for a list at the root, `results[0].name` for a list inside an object). It makes an API usable when it answers a search URL with a list of one element, notably for people sources, and it applies to every mapped field. A people source that gets a list without `[0]` now says so instead of "no name found".

## Version 0.0.8 alpha (November 2026)

New interface, local sources, a people directory, cast distribution and automatic updates.

### ✨ New
- **Redesigned configuration screen**: Sources / Settings / Legacy tabs, source cards, a full-page editor with side navigation, a test panel on the right and a sticky save bar.
- **Redesigned "Missing episodes" screen**: series → sources → episodes, search, "Copy name" / "Copy the N names".
- **Local sources**: series, seasons and episodes typed in the plugin (including upcoming episodes), image upload, import of a list of titles. They are not erased by a forced Jellyfin refresh.
- **People directory** ("Personnes" screen): people sources queried by identifier, deduplication by identifier and by name, photos, cast for local episodes, and cast for series, seasons and episodes of API sources by tag or by a typed identifier, without modifying the API. "Apply to episodes" button.
- **Multi-tag files** (`Ep {dm-1} {dm-2}.mkv`): merged metadata, episode range, episodes already present are no longer reported as missing.
- **Shortcuts in the administration menu** (under Extensions).
- Optional **automatic update** from the repository's releases (`deploy/`), with backup and rollback.
- HTML entities are decoded (`&amp;`, `&quot;`, including without a semicolon).

### 🐛 Fixed
- Saving a source reset the fields missing from the form (cast, year, banner) to their defaults, and `ImagesArray` was never saved.
- The season provider looked for its tag in a name Jellyfin had already cleaned, so it never found an identifier. A season's API metadata (title, overview) now applies on refresh.
- Missing episodes pick up their cast again at analysis time when the directory changes.

### ⚠️ Good to know when updating
- Season names may change (API metadata is now applied).
- The first "Analyze" reprocesses every missing episode: pick a quiet moment.
- A people source is never selected by a media file's tag: its prefix is only used for its identifiers.
- Back up the plugin's data folder: local sources and the directory exist nowhere else.

## Version 0.0.7 alpha (February – October 2026)

Intermediate work that was never numbered at the time, grouped here.

- **Missing episodes**: per-source "episode list" endpoint; greyed-out virtual episodes with a free episode number (without which Jellyfin deletes them at every scan), thumbnail downloaded with the source's settings, duration (`mm:ss`, `hh:mm:ss`, seconds, ISO 8601), preview video playable as a local trailer, per-season cache, "reprocess everything", daily scheduled task.
- **Robust networking**: per-attempt timeout, retries with growing delays (timeout, network error, 408, 429, 5xx, `Retry-After`), per-source concurrent-request limit. The identity sent is adjustable (User-Agent, `Origin`, `Referer`, HTTP/2). "Test source" button.
- **Images**: an episode's thumbnail is also offered as its primary image (otherwise Jellyfin takes a frame of the video); images are downloaded with the source's headers; optional conversion of portrait thumbnails to 16:9; 403/404 responses are logged as warnings.
- **Cache**: a manual refresh ignores the cache (an API change finally shows up); cache keys are prefixed with the source.
- **Overview written as a tag list** (`OverviewListTarget`): detected and moved to the tags or genres.
- **Episode number** read from the API (configurable field); providers always return the numbers already known (a full replace used to reset them to null and delete the virtual episodes).
- **Cast restored** with Jellyfin 10.11's API (`AddPerson`), after its removal in 0.0.6.
- The plugin's identifier is read **case-insensitively** (`custom` / `Custom` key depending on where the item comes from) and stays visible under "External IDs".

## Version 0.0.6 alpha (February 2026)

Multi-source, Jellyfin 10.11 compatibility and a series of critical fixes.

### ✨ Multi-source
- **Several sources** in one installation: each has its own prefix, URL, endpoints, mappings, API key and headers. The prefix of the `{PREFIX-ID}` tag selects the source (case-insensitive; enabled sources only).
- The **cache** is split per source (`MV1-EPISODE-123.json`) and its files are **readable** (`SERIES-1004855103.json`, `IMAGES-SERIES-…`) instead of an MD5 hash.
- **Backward compatibility**: an old single-source configuration (global prefix, URL, endpoints) keeps working as long as no source is defined.

### 🔧 Jellyfin 10.11 compatibility
- `Jellyfin.Controller` / `Jellyfin.Model` packages moved to 10.11; `Nullable` disabled (nullable warnings blocked the build).
- `Episode.People`, `Series.People`, `Movie.People` and `EpisodeInfo.SeriesName` no longer exist: cast is suspended (restored since, see 0.0.7). The old "per-source" provider is removed.

### 🐛 Critical fixes
- **Wrong endpoints**: the season provider called a hard-coded route (`/api/metadata/…`) instead of the configured endpoint, hence 404s even though `curl` worked. Series, season and episode now use their own endpoints.
- **Wrong JSON mapping**: the series or season mapping was determined and then never passed on; everything went through the video mapping.
- **Episodes without an identifier**: the ID was not extracted from the file name (`Episode {mv-EP001}.mkv`).
- **Name truncated by `(2025)`**: the tag was looked up in `info.Name` (the title parsed by Jellyfin) instead of the file path.
- **Invalid image ID** for seasons and episodes (`1004855103/season//episode/` → 404): the ID now comes from the file or folder name, then from `ProviderIds`. Season/episode images ask for their own image fields (a series-only mapping is not enough).
- **Strict regular expression**: a tag only accepts letters, digits, `_` and `-` (`{mv-123 with text}` no longer matches).
- **HTTP 500 on the configuration page**: a `Dictionary` cannot be serialized to XML; custom headers are now a key/value list.
- Compatibility overload of `GetMetadataAsync` (calls without a source) and cleanup of orphaned code (89 compile errors); `async` and logging warnings.
- Much more detailed **logs** (cache, images, ID extraction, the URL called), prefixed by component.

## Version 0.0.5 alpha (February 2026)

### 🐛 CRITICAL BUG FIXED - ID extraction from the full path

**The problem:** every provider looked for `{PREFIX-ID}` in the **FULL path** instead of the file/folder name only, so they always found the **FIRST ID** in the path.

**Example:**
```
Path: /var/media/Series {mv-S123}/Season {mv-SOURCE}/Episode {mv-EP001}.mkv

BEFORE:
- Series provider  → searches the whole path → finds "S123" ✅
- Season provider  → searches the whole path → finds "S123" ❌ (should be "SOURCE")
- Episode provider → searches the whole path → finds "S123" ❌ (should be "EP001")
```

**The fix:** `Path.GetFileName()` extracts **only the last segment** of the path; each provider now searches its own file or folder name only.

```
- Series provider  → searches "Series {mv-S123}"          → finds "S123" ✅
- Season provider  → searches "Season {mv-SOURCE}"        → finds "SOURCE" ✅
- Episode provider → searches "Episode {mv-EP001}.mkv"    → finds "EP001" ✅
```

### ✨ Logging improvements

**One emoji per provider:**
- 📺 `[SERIES PROVIDER]` - series logs
- 📁 `[SEASON PROVIDER]` - season logs
- 🎬 `[EPISODE PROVIDER]` - episode logs

**Detailed extraction logs:**
```
📺 [SERIES PROVIDER] Extracted folder name: My Series {mv-S123} from path: /var/media/...
📺 [SERIES PROVIDER] ✅ Extracted series ID 'S123' from folder name
```

### 🔧 Other fixes

- **Stricter regex**: captures only valid IDs (letters, digits, hyphens, underscores)
  - BEFORE: `\{mv-([^}]+)\}` captured EVERYTHING between the braces (`{mv-123 with text}` → `"123 with text"` ❌)
  - NOW: `\{mv-([\w\-]+)\}` captures valid characters only (`{mv-123}` → `"123"` ✅, `{mv-123 text}` → no match)
- Added manual ID extraction for series (fallback when `CustomExternalId` does not work)
- Better logs to trace exactly what is extracted and from where
- Logs showing the regex pattern used and the exact match

---

## Version 0.0.4 alpha (February 2026)

### ✨ New: tags as an array of objects

**Tags can now be objects, with a field extracted from each.**

**Notation: `tagList[].label`**

**BEFORE:**
- ✅ Array of strings: `["fantasy", "sci-fi"]`
- ❌ Array of objects: `[{"label": "fantasy"}]` → did not work

**NOW, two notations are supported:**

**`[]` notation (recommended):**
```
Tags: tagList[].label
```

**Two-field notation (alternative):**
```
Tags: tagList
Tag field: label
```

**Both behave the same**; the `[]` notation is more intuitive.

**Supported formats:**
- ✅ Array of strings: `["fantasy", "sci-fi"]`
- ✅ Array of objects: `[{"label": "fantasy"}, {"id": 123, "label": "sci-fi"}]`
- ✅ Comma-separated string: `"fantasy, sci-fi"`
- ✅ JSONPath-like notation: `tagList[].label`, `metadata.tags[].name`

**Supported JSON example:**
```json
{
  "tagList": [
    {"id": 4420, "count": 1, "label": "fantasy"},
    {"id": 4421, "count": 5, "label": "sci-fi"}
  ]
}
```

**Documentation:** the `GUIDE_TAGS.md` guide was updated.

---

## Version 0.0.3 alpha (February 2026)

### ✨ New: full image configuration

**Image mappings were added to the configuration interface.**

**What is new:**
- ✅ Image URLs configured per item type (videos / sources / series)
- ✅ Direct URLs: `poster`, `backdrop`, `thumb`, `logo`, `banner`
- ✅ Image arrays with type/width/height
- ✅ Thumbnails for episodes
- ✅ Images extracted from the same JSON as the metadata
- ✅ Separate mappings per item type

**Configurable image types:**
- **Videos/Episodes**: Poster, Backdrop, Thumb, Logo
- **Sources/Seasons**: Poster, Backdrop
- **Series**: Poster, Backdrop, Logo, Banner

**Documentation:** new guide `GUIDE_IMAGES.md`.

---

## Version 0.0.2 alpha (February 2026)

### ✨ New: configurable prefix

**The tag's prefix can now be customised.**

**BEFORE:** fixed pattern `{custom-ID}`

**NOW:** configurable pattern: `{custom-ID}`, `{myappsource-ID}`, `{youtube-ID}`, etc.

---

## Version 0.0.1 alpha (February 2026)

### ✨ Initial features

- ✅ Metadata enrichment for movies / series / episodes
- ✅ Image download (posters, backdrops)
- ✅ Two-level cache (memory + disk)
- ✅ Endpoints configurable per type
- ✅ Configurable JSON mappings
- ✅ Three levels of IDs: video, source, series
- ✅ Configurable source name
