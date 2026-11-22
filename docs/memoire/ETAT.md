# État du projet
Dernière mise à jour : 2026-11-22 (memory reorganised: bug investigations moved into D005/D006, personal notes trimmed)

## Objectif
Jellyfin 10.11.6 metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`, version 0.0.8 alpha) enriching media from user-configured sources (REST API, Local, People, Dump). The `{PREFIX-ID}` tag in a file or folder name selects the source.

## Fait
- Providers for movies, series, seasons, episodes, images; multi-source config, multi-tag episode files (D002, D003).
- Missing/virtual episodes page (`missing.html` v2, cards) and `config.html` redesign (list + editor, 3 tabs).
- Local sources, people directory, cast overlay, image upload and folder upload (D007).
- Dump source type (D008), genres/tags taxonomy screen with pill editor.
- Image persistence fix (D006); repo published with CI, updater, docs and changelog.
- Recent work (2026-11-20/21): CLAUDE.md split into docs/architecture/, project skills and memory agents, folder upload to a gallery, photo cropper for people, test bench fast mode.

## Déployé
- Last successful deployment: 57ce477, develop, 2026-11-22

## En cours
- Uncommitted: docs/memoire reorganisation (D005, D006, D008, this file) and the new `.claude/hooks/memoire-stale.sh`. Nothing else in flight on `develop`.

## Prochaines étapes
- Not recorded. Ask the maintainer before assuming a next task.

## Pièges découverts
- Library image fetchers must include the plugin, or the provider is silently never called (warned in the Sources tab).
- An old `poster.jpg`/`backdrop.jpg` left in the media folder wins again at the next scan.
- A forced "replace all images" series refresh wipes virtual episodes' thumbnails until a reprocess.
- Test container on port 8096 belongs to another project; use 8097 only.
- The `UNIQUE constraint … UserData` scan error is not a plugin bug: see D005 before suggesting causes.
- Cast changes and renames do not reach already-enriched episodes by themselves: "Analyser" and "Appliquer aux épisodes" (background task, not QueueRefresh) propagate them (D007).

## Questions ouvertes
- Deployment status of recent work on the maintainer's server is unknown: ask before building on top (as of 2026-11-16, local sources and dump source were not deployed).
- Not built: series/season cast, real merge of two people, series/season ordering, Diagnostic replay of a dump name match, movie targeted refresh.
- `config.html` is hand-maintained (no generator); long header lists and many sources unchecked.
