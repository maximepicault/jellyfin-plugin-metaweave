# État du projet
Dernière mise à jour : 2026-11-25 (D017 Projection theme direction C; before: memory audit merged: proven corrections, D003 rewritten and fixed in 4e9c4bc, new decisions D010–D016; d8ec62d deployed; code map added in docs/architecture/code-map.md)

## Objectif
Jellyfin 10.11.6 metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`, version 0.0.8 alpha) enriching media from user-configured sources (REST API, Local, People, Dump). The `{PREFIX-ID}` tag in a file or folder name selects the source.

## Fait
- Providers for movies, series, seasons, episodes, images; multi-source config, multi-tag episode files (D002, D003).
- Missing/virtual episodes page (`missing.html` v2, cards) and `config.html` redesign (list + editor, 3 tabs).
- Local sources, people directory, cast overlay, image upload and folder upload (D007).
- Dump source type (D008), genres/tags taxonomy screen with pill editor.
- Image persistence fix (D006); repo published with CI, updater, docs and changelog.
- Recent work (2026-11-20/21): CLAUDE.md split into docs/architecture/, project skills and memory agents, folder upload to a gallery, photo cropper for people, test bench fast mode.
- Recent work (2026-11-21/23): missing-episode video downloads, landscape poster crop, Projection web theme (`theme/projection.css`), gallery typed by shape + multi-selection, people networks/backdrops, paginated dump APIs and dump seasons analysed like API seasons, missing-episode numbering (D009, committed in 41544ee).

## Déployé
- Last successful deployment: d8ec62d, develop, 2026-11-24

## En cours
- Projection theme moved to the compact sheet + wall of season posters (D017), committed on develop (19f6bfd) on 2026-11-25 with the `tools/ui-test/theme-*.mjs` seed and audit (a33c721); not on `main` yet.
- Committed state: everything on `develop` after the deployed d8ec62d is docs, comments, the web theme and test tools: no plugin code changed, no redeploy needed (the theme reaches the server through jsDelivr).
- Untracked: `theme/previews/` (PNG screenshots: never commit).

## Prochaines étapes
- Nothing recorded: ask the maintainer before assuming a next task.

## Pièges découverts
- Library image fetchers must include the plugin, or the provider is silently never called (warned in the Sources tab).
- An old `poster.jpg`/`backdrop.jpg` left in the media folder wins again at the next scan.
- A forced "replace all images" series refresh wipes virtual episodes' thumbnails until a reprocess.
- Test container on port 8096 belongs to another project; use 8097 only.
- The `UNIQUE constraint … UserData` scan error is not a plugin bug: see D005 before suggesting causes.
- Jellyfin's series refresh deletes a virtual episode whose number a real episode of the same season-number group holds; all unnumbered seasons share one group (D009).
- Cast changes and renames do not reach already-enriched episodes by themselves: "Analyser" and "Appliquer aux épisodes" (background task, not QueueRefresh) propagate them (D007).

## Questions ouvertes
- Not built: cast on local-source series/seasons (API series/seasons get the cast overlay), real merge of two people, series/season ordering, Diagnostic replay of a dump name match, targeted refresh of API-source movies (dump movies are covered since 3bcfb36).
- `theme/projection.css` is not on `main` yet: the public `@main` import (README, file header) works only after the release pull request; the maintainer's server may import `@develop` meanwhile (D013).
- `config.html` is hand-maintained (no generator); long header lists and many sources unchecked.
