# État du projet
Dernière mise à jour : 2026-11-27 (D025 server-side Index and library screens, P024/P025; b35cfca deployed; D024 theme « Index » lists, P021–P023; D023 person page, P019/P020; D022 episode photo gallery, P015–P018; D021 preview viewer « Salle », P013/P014; D020 hover preview of real episodes; D019 optional theme script; D018 theme served by the plugin; D017 Projection theme direction C; earlier: memory audit, D010–D016, code map in docs/architecture/code-map.md)

## Objectif
Jellyfin 10.11.6 metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`, version 0.0.8 alpha) enriching media from user-configured sources (REST API, Local, People, Dump). The `{PREFIX-ID}` tag in a file or folder name selects the source.

## Fait
- Providers for movies, series, seasons, episodes, images; multi-source config, multi-tag episode files (D002, D003).
- Missing/virtual episodes page (`missing.html` v2, cards) and `config.html` redesign (list + editor, 3 tabs).
- Local sources, people directory, cast overlay, image upload and folder upload (D007).
- Dump source type (D008), genres/tags taxonomy screen with pill editor.
- Image persistence fix (D006); repo published with CI, updater, docs and changelog.
- Recent work (2026-11-20/21): CLAUDE.md split into docs/architecture/, project skills and memory agents, folder upload to a gallery, photo cropper for people, test bench fast mode. Then (2026-11-21/23): missing-episode video downloads, landscape poster crop, Projection web theme (`theme/projection.css`), gallery typed by shape + multi-selection, people networks/backdrops, paginated dump APIs and dump seasons analysed like API seasons, missing-episode numbering (D009, committed in 41544ee).

## Déployé
- Last successful deployment: b35cfca, develop, 2026-11-27

## En cours
- Projection theme moved to the compact sheet + wall of season posters (D017), committed on develop (19f6bfd) on 2026-11-25 with the `tools/ui-test/theme-*.mjs` seed and audit (a33c721); not on `main` yet. Header adapted to the backdrop committed in 164e57b.
- Served theme route (D018): Custom CSS `@import url("../CustomMetadata/Theme/projection.css")`. Header « Affiche » for series and movies (backdrop + poster, poster only, typographic) and the picture-built header for seasons and episodes added on 2026-11-26 (see D017 note).
- Theme script (D019) + backdrop at its own ratio: committed (17fdd9c) and deployed (49b9351) on 2026-11-25 (`Services/ThemeScriptMiddleware.cs`, `theme/projection.js`, `EnableThemeScript`, CSS 9c). Remaining for the maintainer: turn on « Améliorations du thème Projection » (Settings tab) and reload with Ctrl+F5. Untested: trailer hover preview, real base URL. Source-preview hover of real episodes (D020) committed (5bc22c8); its amendment (ffmpeg preview clips `Services/PreviewClipService.cs` + `ScheduledTasks/PreviewClipsTask.cs`, route `Preview/{itemId}/Clip`, ambient light) committed (10c9e1d); second amendment (no 4 s give-up, wider lookup, colour-cloud light, P012) committed (0423885). Later theme work is on develop up to 4e31727.
- Preview viewer « Salle » (D021, 0267574) and the header fix for revisited pages (288301d): committed on develop and deployed (71c5b1e) on 2026-11-26, checked together on cm-uitest with throwaway scripts. Untracked `theme/previews/` (mockups and screenshots): never commit.
- Episode photo gallery (D022, merge of the « Episode Gallery » plugin): committed on develop (92c2ed2), pushed and deployed (46c2177) on 2026-11-26. Tests reported passing on cm-uitest (`photos-real.mjs`, `config-local-ui.mjs`, `local-gallery-real.mjs`, `local-folder-images-real.mjs`); real server and library not verified. See `docs/architecture/episode-photos.md`.
- Person page « Fiche d'artiste » (D023, 2026-11-27): committed on develop (bf4b8fb, tests 9172584), pushed and deployed (702de1c) on 2026-11-27 (`theme/projection.css` 9d, `theme/projection.js`, `theme-seed.mjs`, `theme-audit.mjs`, CHANGELOG). Audit reported 0 FAIL on the person screens (cm-uitest); not verified: network image icons on the link chips, TV layout, Firefox/Safari, real server.
- Theme « Index » for genre / tag / studio / person lists (D024, 2026-11-27): committed on develop (dcf1dbb, tests 48fb309), pushed and deployed (b35cfca) on 2026-11-27 (`theme/projection-index.js`, `projection.css` 18, `projection.js`, `ThemeController`, `ThemeScriptMiddleware`, csproj, docs incl. `docs/architecture/theme-index.md`, `index-real.mjs`, `theme-audit.mjs`). Reported on cm-uitest: `index-real.mjs` 55/55, `config-local-ui.mjs` 168 pass, audit 0 FAIL but one intentionally faded chip on mobile; not verified: real server and library, TV layout, Firefox/Safari, server request time on a large library. Never commit `theme/previews/`.
- Index searched on the server + library screens (D025, amends D024): committed on develop (2b772f5, tests 177f66f), **not pushed, not deployed** (new `Api/LibraryIndexController.cs`, `Services/LibraryIndexService.cs`, `LibraryIndexQuery.cs`, `tools/ui-test/big-library.sh`, `index-perf.mjs`; modified theme, docs, tests). Reported: `index-real.mjs` 74/74, `config-local-ui.mjs` 168 pass, large test library screens 0.3 to 1.8 s; not verified: real server and library, TV layout, Firefox/Safari, grouped library views, memory use.

## Prochaines étapes
- D025: push and deploy only when the maintainer asks; then check on the real server and library (screen times, memory use, a user with limited access).
- D022, remaining for the maintainer: turn on « Galerie photos des épisodes » (Settings tab), reload with Ctrl+F5 and uninstall the old « Episode Gallery » plugin.

## Pièges découverts
- Server code: the plugin has its own class `Person` (`Services/PeopleStore.cs`), so `typeof(Person)` there is not Jellyfin's; `InternalItemsQuery.ParentId` set to a library (`CollectionFolder`) returns nothing in `GetItemIds`, filter by its physical folders (`TopParentId`) (D025).
- Library pages (10.11.6): the experimental layout changes view without firing `viewshow`; the classic layout keeps one address for all the tabs of a library (D025).
- Theme scripts: a `window` capture `keydown` listener that calls `stopPropagation` (preview viewer, Index panel) also silences the target's own `keydown` handlers, so Enter must be handled inside that listener (D024).
- Theme, person page: detect its sections with `.verticalSection` (a season page also has `data-type="Episode"` rows in `#childrenContent`); the stock mobile rule's ink on `.detailPageWrapperContainer` hides a `z-index: -1` banner (D023). Test scripts: a `/Persons` search with an accent finds nothing, use `/Persons/{name}`.
- Theme: the document has ONE picture header (`header` in `theme/projection.js`) while jellyfin-web keeps visited detail pages: a page shown again must have its header checked on its own (`detail()` calls `canvasHeader` with `page.__pjItem`), and each build is its own object so a late load cannot overwrite a newer one. It was lost on back navigation until the 2026-11-26 fix (288301d; 66/66, 40/40 checks with the viewer merged).
- Library image fetchers must include the plugin, or the provider is silently never called (warned in the Sources tab).
- A numbered folder of photos becomes an empty season (in a series folder) or a folder item (in a season) unless `ImageFolderIgnoreRule` keeps it out, which it does only with the photo gallery on (D022).
- An old `poster.jpg`/`backdrop.jpg` left in the media folder wins again at the next scan.
- A forced "replace all images" series refresh wipes virtual episodes' thumbnails until a reprocess.
- Test container on port 8096 belongs to another project; use 8097 only.
- The `UNIQUE constraint … UserData` scan error is not a plugin bug: see D005 before suggesting causes.
- Jellyfin's series refresh deletes a virtual episode whose number a real episode of the same season-number group holds; all unnumbered seasons share one group (D009).
- Cast changes and renames do not reach already-enriched episodes by themselves: "Analyser" and "Appliquer aux épisodes" (background task, not QueueRefresh) propagate them (D007).

## Questions ouvertes
- Index (D025): only the Series, Episodes and Films views of a library are replaced, the other views stay stock (assistant's implementation, not confirmed by the maintainer). Known limits: an edit can take up to 20 s to show in counts; the hidden stock page still loads its own first items.
- Not built: cast on local-source series/seasons (API series/seasons get the cast overlay), real merge of two people, series/season ordering, Diagnostic replay of a dump name match, targeted refresh of API-source movies (dump movies are covered since 3bcfb36).
- `theme/projection.css` is not on `main` yet: the public `@main` import (README, file header) works only after the release pull request; the maintainer's server may import `@develop` meanwhile (D013).
- `config.html` is hand-maintained (no generator); long header lists and many sources unchecked.
