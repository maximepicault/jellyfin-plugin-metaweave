> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs.

# Dump sources

A **Dump** source is for an API that gives you its whole catalogue in ONE call:

```json
{ "data": [ { "id": 12345, "name": "…", "slug": "…", "search_titles": "…", "description": "<p>…</p>",
              "tags": ["…"], "brand": "…", "poster_url": "…", "cover_url": "…", "likes": 30, "dislikes": 2,
              "released_at": "2014-04-25T00:00:05.000Z" } ] }
```

The plugin downloads it once (and again when the configured cache duration has expired), keeps it in memory and finds each of your files in it. Create one with **Ajouter une source → Dump (catalogue)**.

## How a file is matched

In this order, the first step that answers wins:

1. **The id kept on the item** (`custom` provider id) from a previous match: refreshes go straight to the same entry.
2. **A `{prefix-id}` tag** of the dump source in the file name (`Mon épisode {dp-12345}.mkv`): authoritative. An id that is not in the dump gives nothing, with no guessing from the name.
3. *Only when "Reconnaître aussi les fichiers sans tag" is on* — the name is compared with the catalogue:
   - a bare `{12345}` in the file name is the entry's id;
   - the **slug**, then the **exact name** (accents, case and punctuation ignored, `[…]`, `{…}`, `(…)`, `S01E02` and the ignored words removed);
   - a **fuzzy score** (0–100): word overlap with the name, or the file name found in the glued "other titles". Matched automatically from the **minimum score** (70 by default). An `S01E03` is **confirmed** when the entry's other titles hold the title directly followed by that number, or hold the number glued right after the entry's own name (`name3title2…`; the number may be written `ep 3` or `episode 3`, after or before the name). An `Ep 3` / `Episode 3` in the file name is read as an episode number like `S01E03`. When the match is carried by the other titles, an entry whose main name is closer to the file's title earns up to 10 more points, and loses 3 points for each word of its name that the file's title does not have (numbers do not count), which separates two otherwise equal entries: the one with fewer extra words wins. When the title is found but the number is not confirmed, the score is capped at 69: the entry is still accepted if it **stands out from the next best by at least 5 points** (or is the only candidate), otherwise it is only proposed in Jellyfin's **Identify** screen.

The editor's test panel shows the candidates and the score of any file name, so the threshold can be tuned without a scan.

> Use the name matching with care in a library that mixes other content: a file the plugin should leave alone could be related to a similar entry. It is switched off per source.

## Paginated dumps

If the API returns the catalogue in pages, set the **pagination parameter** (for example `page`) in the Catalogue section. The plugin reads `?page=1`, `?page=2`… (the first page number and a page limit are editable) and merges all pages into one catalogue. Reading stops at an empty page, at a page that brings no new id (an API that ignores the parameter), or at a 404 after the first page. If a page fails, or the limit is reached before the end, the download is refused and the previous copy, if any, keeps serving. For an API that expects JSON in the URL, write the whole parameter with `{page}` in place of the number, for example `options={"pagination":{"page":{page},"pageSize":100}}` (the value is URL-encoded for you). Leave the parameter empty for a single request.

## Mappings

What Jellyfin shows comes from the source's **Vidéo / épisode** mapping, applied to each entry, like for an API source. A new dump starts with the usual paths:

| Jellyfin | Default path |
|---|---|
| Title | `name` |
| Overview | `description` (with "The description contains HTML" on) |
| Premiere date | `released_at \|\| created_at` |
| Tags | `tags` (put `tags` in Genres too to get both) |
| Studio | `brand` |
| Rating | `communityRating`, computed as likes / (likes + dislikes) × 10 from the Catalogue paths |
| Primary / Backdrop / Thumb | `poster_url` / `cover_url` / `cover_url` |

The Catalogue section tells where the entries are (`data`, empty = the response is the list), their id, name, slug, other titles, group and likes/dislikes paths.

## Where it applies

- **Episodes** (a Series library) and **movies** (a Movies or mixed library: standalone files). Enable "Custom Metadata Provider" and "Custom Image Provider" for those types in the library settings: the configuration page warns when they are missing.
- A dump has no series or seasons: no endpoint, no episode list, no virtual episodes. The **missing** entries are shown in the "Catalogues" view instead.

## The coverage view ("Épisodes manquants" → Catalogues)

Compares the catalogue with the library: an entry is **owned** when an episode or movie carries its `{prefix-id}` tag or was related to it. A file that was never matched counts as missing (go and see why: Identify, the test panel, the log tagged `📦 [DUMP]`). Groups (the "Groupe" path) show their own coverage; filters, sort, "Copier le nom" and a CSV export of the current filters are available.

## Finding the files that were not recognised for sure

On the coverage screen every owned file says how it was recognised: by its tag, an identifier or its exact name (certain), or by a score. The "Reconnaissance des fichiers" filter is an interval (double slider and two typed fields, 0 to 100 %, both ends included; "Pas certains" = 0 to 99) that keeps the series, then the files, recognised within it; the file name of the library is shown so it can be renamed (adding the `{prefix-id}` tag makes it certain). The score of a file matched through the other titles is its score before the cap on an unconfirmed episode number. The date used by this screen has its own path in the Catalogue section ("Date de sortie (écran Catalogues)"), independent from the video mapping: leave the mapping's date empty to keep it out of Jellyfin and the filters still work. A release-date filter ("Sorti à partir de" / "Sorti jusqu'à", years taken from the dump) works on series and on entries. Series and entries can be sorted by release date, by recognition, by name… and a "Puis par" key chains a second sort that breaks the ties of the first.

## Series inferred from the names

The coverage screen opens on series cards, like the Séries tab; a click lists the entries of that series. Entries whose names only differ by a trailing number ("Title", "Title 2") form a series (minimum entries in the Catalogue section, 0 = off, then the entries are listed directly); an entry that belongs to no group is a series of one episode (its own card). The "Format de l'aperçu" setting (16:9 or 2:3) shapes the pictures. The number is a hint only: it may be a season or an episode, so it is never turned into `SxxExx`, and it stays in the copied name. When an entry of the series is already in the library as an episode of a series, the group shows that series and its seasons.

## Refreshing what the dump recognises

On the Sources tab, a dump group\'s "Actualiser les éléments" does not need tags: pick the libraries (all by default) and the types (episodes, movies). It first looks at the files of those libraries and keeps the ones the dump recognises (step 1/2), then refreshes only those (step 2/2). A file that matches nothing, or is tagged for another source, is left alone. The recognised files are not refreshed through Jellyfin: the dump's data is written straight onto them (a few milliseconds each, no other provider called, no network), fields the dump does not give are left as they are, and a locked item is not touched. The log (`🔁 [SOURCE REFRESH] 📦`) tells how many files were looked at, recognised and refreshed.

## Limits

- The dump is kept in memory only: it is downloaded again after a Jellyfin restart.
- The noise words are compared in lower case without accents: "VOSTFR" and "vostfr" are the same word.
