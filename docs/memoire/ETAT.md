# État du projet
Dernière mise à jour : 2026-11-27 (D027 photo albums and photos, committed on develop, not pushed; last deployment recorded: f9f0a21)

## Objectif
Jellyfin 12.4 (only, D026; 10.11.6 up to 0.0.8) metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`, version 0.0.8 alpha) enriching media from user-configured sources (REST API, Local, People, Dump). The `{PREFIX-ID}` tag in a file or folder name selects the source.

## Fait
- Providers for movies, series, seasons, episodes, images; multi-source config, multi-tag episode files (D002, D003); missing/virtual episodes page and downloads (D009, D014, D015); `config.html` redesign (list + editor).
- Local sources, people directory, cast overlay, image and folder upload (D007, D010); dump source type (D008); genres/tags taxonomy (D012); image persistence fix (D006); repo published with CI, updater, docs and changelog.
- Also (2026-11-20/23): CLAUDE.md split into docs/architecture/, skills and memory agents, test bench fast mode, photo cropper, landscape poster crop (D011), Projection theme (D013), people networks/backdrops, paginated dump APIs and dump seasons, missing-episode numbering (41544ee).
- 2026-11-25/26, on develop: theme detail pages (D017: 19f6bfd, audit a33c721, header 164e57b), theme route and script (D018, D019: 17fdd9c, deployed 49b9351; untested: trailer hover preview, real base URL), hover preview (D020: 5bc22c8, 10c9e1d, 0423885; later theme work up to 4e31727), « Salle » viewer (D021: 0267574, header fix 288301d, deployed 71c5b1e), photo gallery (D022: 92c2ed2, deployed 46177c2; real server not verified).
- 2026-11-27, on develop and deployed: person page (D023: bf4b8fb, 702de1c; not verified: network icons on link chips, TV, Firefox/Safari, real server), « Index » lists (D024: dcf1dbb, b35cfca), server-side Index and library screens (D025: 2b772f5, 7ec9ff7, later header work up to f9f0a21), Jellyfin 12.4 only (D026: e5a0330, c09eac1).
- Other test results and what was not verified: see each decision.

## Déployé
- Last successful deployment: f9f0a21, develop, 2026-11-27

## En cours
- Photo albums and photos (D027, `docs/architecture/photo-albums.md`): committed on develop (7144f4e theme fix, 3fde78a feature); NOT pushed, NOT deployed. Reported: `albums-real.mjs` 33/0, `config-local-ui.mjs` 168/0, other config / local scripts and `ui-test.sh` passed on a 12.4 bench, build 0 warnings. Not verified: real server and library, TV layout, Firefox/Safari, large photo libraries, user with limited access; `theme-audit.mjs` not re-run after the `.itemDetailsGroup` fix.
- 0cd0284 (missing episodes hidden by default on the Index screens): committed and pushed on develop; no deployment recorded.
- Never commit `theme/previews/` (untracked mockups and screenshots).

## Prochaines étapes
- D027: push only when the maintainer asks; re-run `theme-audit.mjs`; then check on the real server and library (a source with « Albums photo » ticked, scan cost on a large photo library).
- D026: check on the real server and library: plugin loaded and active, screens, previews of missing episodes, chapters.
- D025: then check screen times, memory use, a user with limited access.
- D019/D022, remaining for the maintainer: turn on « Améliorations du thème Projection » and « Galerie photos des épisodes » (Settings tab), reload with Ctrl+F5, uninstall the old « Episode Gallery » plugin.

## Pièges découverts
- Photos (D027): Jellyfin's EXIF reader rewrites a photo's fields at each of its runs, so a user « search missing metadata » overwrites a typed value on an unlocked photo (only the item lock keeps it); a library scan alone does not apply a source change (targeted refresh needed).
- Jellyfin 12: `BaseItem.ExtraIds` is gone, extras are linked by `OwnerId` only (`Services/PreviewMapper.cs` queries `OwnerIds` + `ExtraTypes`); deleting an item removes its extras from the database (D026).
- Jellyfin 12: legacy authentication is off by default (`X-Emby-Token`, `X-Emby-Authorization`, `api_key` get 401): use `Authorization: MediaBrowser Token="…"` or `ApiKey` in the URL. It compiles and only fails at run time (D026).
- Jellyfin 12: `IChapterManager.SaveChapters` drops every chapter while the video length is unknown (`Services/ChapterService.cs` now waits).
- Jellyfin 12 wraps each info row of a detail page: a CSS rule on direct children (`>`) of `.itemDetailsGroup` hid studios and genres (D027).
- Genres: Jellyfin keeps one spelling of a genre term per item and deletes genres no item uses; the per-process « genre already ensured » cache then made genre clicks find nothing (fixed in `Services/TaxonomyService.cs`; not known to be specific to 12).
- Test bench: a script interrupted halfway can leave « Importer le casting » off, so later cast tests fail (`config-people-real.mjs` now turns it back on at start).
- Server code: the plugin has its own class `Person` (`Services/PeopleStore.cs`), so `typeof(Person)` there is not Jellyfin's; `InternalItemsQuery.ParentId` set to a library (`CollectionFolder`) returns nothing in `GetItemIds`, filter by its physical folders (`TopParentId`) (D025).
- Library pages (10.11.6): the experimental layout changes view without firing `viewshow`; the classic layout keeps one address for all the tabs of a library (D025).
- Theme scripts: a `window` capture `keydown` listener that calls `stopPropagation` (preview viewer, Index panel) also silences the target's own `keydown` handlers, so Enter must be handled inside that listener (D024).
- Theme, person page: detect its sections with `.verticalSection` (a season page also has `data-type="Episode"` rows in `#childrenContent`); the stock mobile rule's ink on `.detailPageWrapperContainer` hides a `z-index: -1` banner (D023). Test scripts: a `/Persons` search with an accent finds nothing, use `/Persons/{name}`.
- Theme: the document has ONE picture header (`header` in `theme/projection.js`) while jellyfin-web keeps visited detail pages: a page shown again must have its header checked on its own (`detail()` calls `canvasHeader` with `page.__pjItem`), and each build is its own object so a late load cannot overwrite a newer one (fixed in 288301d).
- Library image fetchers must include the plugin, or the provider is silently never called (warned in the Sources tab).
- A numbered folder of photos becomes an empty season (in a series folder) or a folder item (in a season) unless `ImageFolderIgnoreRule` keeps it out, which it does only with the photo gallery on (D022).
- An old `poster.jpg`/`backdrop.jpg` left in the media folder wins again at the next scan.
- A forced "replace all images" series refresh wipes virtual episodes' thumbnails until a reprocess.
- Test container on port 8096 belongs to another project; use 8097 (a second bench: `CM_UI_IMAGE`, `CM_UI_NAME`, `CM_UI_PORT`, `CM_UI_WORK` together).
- The `UNIQUE constraint … UserData` scan error is not a plugin bug: see D005 before suggesting causes.
- Jellyfin's series refresh deletes a virtual episode whose number a real episode of the same season-number group holds; all unnumbered seasons share one group (D009).
- Cast changes and renames do not reach already-enriched episodes by themselves: "Analyser" and "Appliquer aux épisodes" (background task, not QueueRefresh) propagate them (D007).

## Questions ouvertes
- D027, assistant's choices not confirmed by the maintainer: albums off by default, album-tag inheritance off, photo found by file name or own tag. Not built: dump sources for albums, local data over an API album, album description above the photo grid in the web client, cast (none on these items).
- D026: facts read or tested on 10.11 (D005, D009 internals, « Library pages » pitfall above) are not reported as re-checked in the 12 source.
- Index (D025): only the Series, Episodes and Films views of a library are replaced (assistant's implementation, not confirmed). Known limits: an edit can take up to 20 s to show in counts; the hidden stock page still loads its own first items.
- Not built: cast on local-source series/seasons (API series/seasons get the cast overlay), real merge of two people, series/season ordering, Diagnostic replay of a dump name match, targeted refresh of API-source movies (dump movies are covered since 3bcfb36).
- `theme/projection.css` is not on `main` yet: the public `@main` import (README, file header) works only after the release pull request; the maintainer's server may import `@develop` meanwhile (D013).
- `config.html` is hand-maintained (no generator); long header lists and many sources unchecked.
