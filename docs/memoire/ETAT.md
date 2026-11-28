# État du projet
Dernière mise à jour : 2026-11-28 (D031 photo pictures prepared in advance: developed, in the working tree, not committed; last deployment recorded: 04bab89)

## Objectif
Jellyfin 12.4 (only, D026; 10.11.6 up to 0.0.8) metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`, version 0.0.8 alpha) enriching media from user-configured sources (REST API, Local, People, Dump). The `{PREFIX-ID}` tag in a file or folder name selects the source.

## Fait
- Providers for movies, series, seasons, episodes, images; multi-source config, multi-tag episode files (D002, D003); missing/virtual episodes page and downloads (D009, D014, D015); `config.html` redesign (list + editor).
- Local sources, people directory, cast overlay, image and folder upload (D007, D010); dump source type (D008); genres/tags taxonomy (D012); image persistence fix (D006); repo published with CI, updater, docs and changelog.
- Also (2026-11-20/23): CLAUDE.md split into docs/architecture/, skills and memory agents, test bench fast mode, photo cropper, landscape poster crop (D011), Projection theme (D013), people networks/backdrops, paginated dump APIs and dump seasons, missing-episode numbering (41544ee).
- 2026-11-25/26, on develop: theme detail pages (D017: 19f6bfd, audit a33c721, header 164e57b), theme route and script (D018, D019: 17fdd9c, deployed 49b9351; untested: trailer hover preview, real base URL), hover preview (D020: 5bc22c8, 10c9e1d, 0423885; later theme work up to 4e31727), « Salle » viewer (D021: 0267574, header fix 288301d, deployed 71c5b1e), photo gallery (D022: 92c2ed2, deployed 46177c2; real server not verified).
- 2026-11-27, on develop and deployed: person page (D023: bf4b8fb, 702de1c; not verified: network icons on link chips, TV, Firefox/Safari, real server), « Index » lists (D024: dcf1dbb, b35cfca), server-side Index and library screens (D025: 2b772f5, 7ec9ff7, later header work up to f9f0a21), Jellyfin 12.4 only (D026: e5a0330, c09eac1).
- 2026-11-27, pushed on develop: photo albums and photos (D027: 7144f4e theme fix, 3fde78a, f265ba3; `docs/architecture/photo-albums.md`).
- 2026-11-28, on develop and deployed: theme photo screens, editor menu and « Actualiser » blocks (D028), album videos (D029), people on albums, photos and videos (D030): e756db8, cf3efa4; faster pictures (04bab89).
- Test results and what was not verified: see each decision.

## Déployé
- Last successful deployment: 04bab89, develop, 2026-11-28
- e585b7a includes D027 (f265ba3) and 0cd0284 (missing episodes hidden by default on the Index screens): both are on the server since that deployment (Check and Deploy reported successful, backup /var/backups/metaweave/20261128-003546). Plugin loading in the server log not checked.
- 04bab89 (`perf(theme)`, pictures show sooner; `docs/architecture/photo-albums.md` « Pictures sooner »): gain not measured on the real server; a first visit still waited for the server (follow-up: D031).

## En cours
- D031 (maintainer: « Oui, développe-la »): in the working tree on develop, NOT committed, not deployed; includes the new untracked files `Services/ImagePreparationService.cs` and `ScheduledTasks/ImagePreparationTask.cs`. Reported on a 12.4 bench: build 0 warnings, `photos-library-real` 52/0, `album-people-real` 51/0, `albums-real` 37/0, `config-local-ui` 171/0.
- Not re-run since the D030 fixes (passed before): `config-people-*`, `local-*`, `dump-real`, `diagnostic-real`, `index-real`; `theme-audit.mjs` not re-run (partial before D029: 44 to 52 FAIL, see D028).
- Never commit `theme/previews/` (untracked mockups and screenshots); `.claude/hooks/` is also untracked, its origin was not reported.

## Prochaines étapes
- D031: commit and push only when the maintainer asks; after deployment turn on « Préparer les images des photothèques » (needs « Améliorations du thème Projection »; plus « grands écrans » above 1920×1080), run the task, measure duration, cache size and picture times on the real server.
- Re-run the scripts listed under « En cours » and the full `theme-audit.mjs`.
- D027 to D030: check on the real server and library (a source with « Albums photo » ticked, photo screens and people with the theme script on, scan cost on a large photo library).
- D026: check on the real server and library: plugin loaded and active, screens, previews of missing episodes, chapters.
- D025: then check screen times, memory use, a user with limited access.
- D019/D022, remaining for the maintainer: turn on « Améliorations du thème Projection » and « Galerie photos des épisodes » (Settings tab), reload with Ctrl+F5, uninstall the old « Episode Gallery » plugin.

## Pièges découverts
- Jellyfin's « Clean Cache Directory » deletes old cached images (12.4 bench: 40 days old removed, 25 kept), and prepared pictures only serve if size, quality and format match the browser's request (D031).
- Photos (D027): Jellyfin's EXIF reader rewrites a photo's fields at each of its runs, so a user « search missing metadata » overwrites a typed value on an unlocked photo (only the item lock keeps it); a library scan alone does not apply a source change (targeted refresh needed).
- Jellyfin 12: `BaseItem.ExtraIds` is gone, extras are linked by `OwnerId` only (`Services/PreviewMapper.cs` queries `OwnerIds` + `ExtraTypes`); deleting an item removes its extras from the database (D026).
- Jellyfin 12: legacy authentication is off by default (`X-Emby-Token`, `X-Emby-Authorization`, `api_key` get 401): use `Authorization: MediaBrowser Token="…"` or `ApiKey` in the URL. It compiles and only fails at run time (D026).
- Jellyfin 12: `IChapterManager.SaveChapters` drops every chapter while the video length is unknown (`Services/ChapterService.cs` now waits).
- Jellyfin 12 wraps each info row of a detail page: a CSS rule on direct children (`>`) of `.itemDetailsGroup` hid studios and genres (D027).
- Jellyfin 12.4: `UpdatePeopleAsync` on a PhotoAlbum, Folder or Photo stores nothing; on an album `Video` it persists, but jellyfin-web's person page showed nothing for a person linked only to such a video (D030).
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
- D031, assistant's choices not confirmed by the maintainer (list in the decision). The size and time figures in the setting's hint and the README are estimates, not measured on real photos [supposé]. Not verified: real server and library, 1440p/4K screen, Safari/Firefox, very large libraries.
- D028 to D030, not verified: real server and library, large libraries, TV, Firefox/Safari, stock apps, limited user, video formats beyond H.264 MP4 and MPEG-4 AVI.
- D027, assistant's choices not confirmed by the maintainer: albums off by default, album-tag inheritance off, photo found by file name or own tag. Not built: dump sources for albums, local data over an API album, album description above the photo grid in the stock web client (shown by the D028 theme page).
- D030, assistant's choices not confirmed by the maintainer (list in the decision and its amendment). Known limits as built: while a source does not answer, a removed link stays shown on the items that depend on it; records of deleted items are not purged.
- D028 and D029, assistant's choices not confirmed by the maintainer (lists in the decisions; D029: no transcoding, resume or « seen » state in the viewer, no image from the source on videos, same endpoint and mappings as photos, video-only libraries stay stock). Source cards unchanged: the mockup's content chips are not built.
- D026: facts read or tested on 10.11 (D005, D009 internals, « Library pages » pitfall above) are not reported as re-checked in the 12 source.
- Index (D025): only the Series, Episodes and Films views of a library are replaced (assistant's implementation, not confirmed). Known limits: an edit can take up to 20 s to show in counts; the hidden stock page still loads its own first items.
- Not built: cast on local-source series/seasons (API series/seasons get the cast overlay), real merge of two people, series/season ordering, Diagnostic replay of a dump name match, targeted refresh of API-source movies (dump movies are covered since 3bcfb36).
- `theme/projection.css` is not on `main` yet: the public `@main` import (README, file header) works only after the release pull request; the maintainer's server may import `@develop` meanwhile (D013).
- `config.html` is hand-maintained (no generator); long header lists and many sources unchecked.
