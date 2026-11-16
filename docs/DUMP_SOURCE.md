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
   - a **fuzzy score** (0–100): word overlap with the name, or the file name found in the glued "other titles". Matched automatically from the **minimum score** (70 by default). An `S01E03` whose number the entry does not confirm is capped under the threshold: the entry is only proposed in Jellyfin's **Identify** screen.

The editor's test panel shows the candidates and the score of any file name, so the threshold can be tuned without a scan.

> Use the name matching with care in a library that mixes other content: a file the plugin should leave alone could be related to a similar entry. It is switched off per source.

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

## Series inferred from the names

The coverage screen opens on series cards, like the Séries tab; a click lists the entries of that series. Entries whose names only differ by a trailing number ("Title", "Title 2") form a series (minimum entries in the Catalogue section, 0 = off, then the entries are listed directly); the others are under "Épisodes seuls". The "Format de l'aperçu" setting (16:9 or 2:3) shapes the pictures. The number is a hint only: it may be a season or an episode, so it is never turned into `SxxExx`, and it stays in the copied name. When an entry of the series is already in the library as an episode of a series, the group shows that series and its seasons.

## Refreshing what the dump recognises

On the Sources tab, a dump group\'s "Actualiser les éléments" does not need tags: pick the libraries (all by default) and the types (episodes, movies). It first looks at the files of those libraries and keeps the ones the dump recognises (step 1/2), then refreshes only those (step 2/2). A file that matches nothing, or is tagged for another source, is left alone. The log (`🔁 [SOURCE REFRESH] 📦`) tells how many files were looked at, recognised and refreshed.

## Limits

- The dump is kept in memory only: it is downloaded again after a Jellyfin restart.
- The noise words are compared in lower case without accents: "VOSTFR" and "vostfr" are the same word.
