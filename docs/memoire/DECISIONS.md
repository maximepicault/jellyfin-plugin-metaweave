# Index
D017 | active | Projection theme detail pages use direction C "Fiche": compact header and a CSS-only wall of season posters | dépend de D013 ; motivée par P005, P006, P007, P008
D016 | active | Dump targeted refresh writes the plugin's data directly, without a Jellyfin refresh | dépend de D008
D015 | active | Missing episodes' videos are downloaded by the plugin (opt-in), streams remuxed by Jellyfin's ffmpeg | —
D014 | active | Episodes without a number take their release-date position; absurd cached numbers are never reused | —
D013 | active | The Projection web theme lives in this repository, independent of the plugin, imported through jsDelivr | —
D012 | active | Genres/tags rules are applied by the plugin to the items it manages, on every save | dépend de D005
D011 | active | Landscape series/season posters are cropped to the centre 2:3 by the plugin (opt-in per source) | —
D010 | active | Local gallery images are typed by their shape on upload; a default image can be demoted | dépend de D007
D009 | active | A missing episode keeps its own number; a clash with a real episode moves it to season number -1 | motivée par P003, P004
D008 | active | Dump source type replaces the separate DumpMetadata plugin | dépend de D002
D007 | active | Local sources and people directory live in the plugin, not in Jellyfin items | dépend de D002
D006 | active | Applied images are persisted explicitly in Jellyfin's database | —
D005 | active | The plugin never repairs Jellyfin's database | motivée par P001
D004 | active | Provider ids are read case-insensitively through CustomId | —
D003 | active | Tag is read from the item's own name, never the full path; season and series read their folder first | —
D002 | active | Multi-source configuration keyed by tag prefix; legacy single-source kept | —
D001 | active | Build against Jellyfin 10.11.6 exactly (net9.0, ExcludeAssets=runtime) | —

# Détail

## D017 — Projection theme detail pages use direction C "Fiche": compact header and a CSS-only wall of season posters
- Date : 2026-11-25 (maintainer's choice; uncommitted at the time of writing)
- Contexte : the maintainer's library has series with many seasons (reference case: One Piece split into ~30 arcs); they want season images to stay large, and the season to resume must come up the page without JS. Three directions were compared on a clickable mockup (https://claude.ai/artifact/LChgPeMiBmeZDdC1BmEoMW): A Affiche (the previous panoramic band), B Cinéma (full-bleed backdrop, text left, season tabs), C Fiche (compact sheet).
- Décision : direction C for jellyfin-web detail pages (series, season, episode, movie; desktop layout). Compact header: `--pj-hero-h` is 14rem on every detail page (was `min(60vh, 36vw)`), the poster starts just under the header, the backdrop is blurred and the fixed layer fades to ink about 26rem under the band (section 8). Series seasons (section 9b): a flex wall of 2:3 posters (`--sw-big`, 3 per row on phones), a state chip on the poster, the first season to watch ringed amber with "À suivre"; watched seasons shrink in place to small posters (`--sw-small`). The wall clears the poster and spans the page gutter.
- Alternatives écartées : A Affiche → the panoramic band pushed the seasons down the page [supposé, inferred from the stated reason]; B Cinéma → P005; "folded watched seasons" → P006; rows of small thumbnails → P007; the previous "sleeve" wide season cards → P008; "all large" seasons → not the maintainer's pick, reason not recorded [supposé: watched seasons would keep the season to resume low on the page].
- Conséquences : no JS anywhere in the theme. A mixed row (small watched posters next to large ones) leaves empty space under the small ones: accepted, it keeps the season order. Counts like "625 / 1076 seen" and per-season progress bars are not available, only Jellyfin's unplayed count.
- Liens : dépend de D013 ; motivée par P005, P006, P007, P008
- Code : theme/projection.css section 8 (:326, `--pj-hero-h: 14rem` :342, poster `top: calc(4.75rem - var(--pj-hero-h))` :345, `blur(22px)` :349) and section 9b (:698, `--sw-big`/`--sw-small` :713-714, "À suivre" :768, phones :799); verification tools tools/ui-test/theme-seed.mjs (varied data incl. a 32-arc "One Piece (démo)" series in the "Séries (thème)" library of the cm-uitest container), theme-audit.mjs (19+ screens × 6 configs: veiled text, contrast, collisions, overflow, cut text, header, tap size, focus), theme-shots.mjs, theme-probe.mjs (all untracked on 2026-11-25).
- À réévaluer si : the maintainer wants JS features (folding, counts) and accepts a script-injection mechanism; or jellyfin-web changes the season card DOM (`.card[data-type="Season"]`, `.countIndicator`, `.playedIndicator`).
- Fiabilité : [vérifié] CSS values and line numbers read in the working tree on 2026-11-25; [vérifié] the tool files exist; [supposé] the audit results (reported by the main agent, not re-run).

## D016 — Dump targeted refresh writes the plugin's data directly, without a Jellyfin refresh
- Date : 2026-11-17 (commit 4494492)
- Contexte : "Actualiser les éléments" on a dump called `item.RefreshMetadata`: every other provider of the library (TMDb over the network), the file probe and the image rebuild ran for data that is already in memory.
- Décision : `SourceRefreshService.ApplyDumpAsync` asks the plugin's own provider for the metadata, `CopyMetadata` sets only the fields returned, then `UpdateToRepositoryAsync(MetadataImport)` and `ApplyImagesAsync` when images are replaced.
- Alternatives écartées : a full `RefreshMetadata` per item → 80-500 ms per item against ~5 ms direct (measure cited in docs/architecture/dump-sources.md:12). No other alternative recorded.
- Conséquences : an optimisation with a trade-off (maintainer, 2026-11-24): the library's other providers, the file probe and the image rebuild are no longer called for a dump refresh. A provider unticked in the library applies nothing (Warning); a locked item is left untouched; the taxonomy and chapter hooks fire through `UpdateToRepositoryAsync`.
- Liens : dépend de D008
- Code : commit 4494492; Services/SourceRefreshService.cs (`ApplyDumpAsync`, :256)
- À réévaluer si : a dump refresh must again call the library's other providers (TMDb…), or Jellyfin makes a full refresh cheap.
- Fiabilité : [vérifié] commit body and code; [supposé] the measure (from the architecture doc, not re-measured).

## D015 — Missing episodes' videos are downloaded by the plugin (opt-in), streams remuxed by Jellyfin's ffmpeg
- Date : 2026-11-21 (commits 769aed9 to 393e2fd)
- Contexte : missing episodes should become real ones without a manual download. The maintainer chose to build the download into the plugin for an extra degree of freedom and to fit it closely to their needs (maintainer, 2026-11-24).
- Décision : per-source option `AutoDownloadVideos`, off by default (Configuration/SourceConfiguration.cs:191); URL from `EpisodeListMapping.VideoUrl`, else the preview URL; the file is saved in the season folder with its `{prefix-id}` tag and the episode number first (33a1a73); direct MP4/WebM/MKV are streamed as they are, HLS and DASH are remuxed to MP4 by the server's ffmpeg (re-encoded only if the copy fails), anything else is refused (769aed9); `MaxParallelDownloads` defaults to 1 (PluginConfiguration.cs:117); after the queue, a library scan then a new analysis (faaee89).
- Alternatives écartées : an existing download library or tool → it would mean bending the usage to fit it and ending with something less efficient and less customisable (maintainer, 2026-11-24).
- Conséquences : a download problem must never stop the analysis, and the background service must never throw, since a crashing background service can stop the whole server (558c71f). Local sources and seasons without a folder are skipped (docs/architecture/missing-episodes.md:45).
- Liens : —
- Code : Services/VideoDownloadService.cs; Api/DownloadsController.cs; Services/MissingEpisodeService.cs (`QueueVideoDownloads`); tools/ui-test/download-real.mjs
- À réévaluer si : the videos to download often come in a format the plugin refuses (neither a direct file nor HLS/DASH, see 769aed9), or the ffmpeg shipped with Jellyfin changes (maintainer, 2026-11-24).
- Fiabilité : [vérifié] commit bodies and code lines above; download-real.mjs added in 6c67653, re-run not recorded.

## D014 — Episodes without a number take their release-date position; absurd cached numbers are never reused
- Date : 2026-11-21 (d89d407, 0c745e1); absurd-number rule 2026-11-23 (9d7d312)
- Contexte : an API episode without a number took the next free number after the last local episode, and a real file without a number kept none. Jellyfin reads episode numbers from file names, tag digits included ("Bonus {ab-1-6149542}.mkv" is episode 6149542), and older versions cached 6149543… for missing episodes (9d7d312).
- Décision : `FillMissingIndexNumbers` gives an API episode without a number its 1-based position in the whole list sorted by release date (API numbers always win; a taken position falls back to the next free number); `EpisodePositionService` applies the same rule to a real episode with no number; a cached number at or above `AbsurdNumber` (10000) is never reused.
- Alternatives écartées : next free number after the last local episode → replaced by d89d407. No other alternative recorded.
- Conséquences : clashes with real episodes are handled by D009 (no typed link: none of the five link terms fits, maintainer 2026-11-24).
- Liens : —
- Code : Services/MissingEpisodeService.cs:787 (`FillMissingIndexNumbers`), :861 (`AbsurdNumber`); Services/EpisodePositionService.cs; Providers/CustomEpisodeProvider.cs:251; tools/ui-test/episode-number-real.mjs, tools/ui-test/missing-number-real.mjs
- À réévaluer si : a source gives a reliable order (position in its list) better than the release date (maintainer, 2026-11-24).
- Fiabilité : [vérifié] commit bodies and code lines; docs/architecture/missing-episodes.md:23 says the same.

## D013 — The Projection web theme lives in this repository, independent of the plugin, imported through jsDelivr
- Date : 2026-11-21 (commit 612618d)
- Contexte : an optional custom CSS for jellyfin-web 10.11, kept in the repository "so it can be imported through jsDelivr and follow updates" (612618d).
- Décision : `theme/projection.css` is the only theme in use, imported in Dashboard → General → Custom CSS code with an `@import` from jsDelivr. The public documentation keeps `@main` (README.md:84-90, theme/projection.css:8); until the release, the maintainer's own server may import from `@develop`, since the file does not exist on `main` before it (maintainer, 2026-11-24).
- Alternatives écartées : a raw.githubusercontent.com link → served as text/plain, refused by browsers (theme/projection.css:9-10). No other alternative recorded.
- Conséquences : the import follows `main`, so a theme change reaches users only after the develop → main release (on 2026-11-24 the file is not on `origin/main`); jsDelivr caches up to 12 hours (README.md:90); the 10.11 admin dashboard (React) gets no custom CSS; the French labels are in sections 9b and 13. `theme/aurora.css` and `theme/projection-v1.css` were proofs of concept, never committed, deleted on 2026-11-24 (maintainer's request).
- Liens : —
- Code : theme/projection.css; README.md:84-90; commits 612618d to d5dc17c
- À réévaluer si : upgrade to Jellyfin 10.12 (jellyfin-web may change its CSS classes): the same trigger as D001 (maintainer, 2026-11-24).
- Fiabilité : [vérifié] file header and README; [supposé] the maintainer validated the theme on 2026-11-22 (not recorded in the repository).

## D012 — Genres/tags rules are applied by the plugin to the items it manages, on every save
- Date : 2026-11-16 (dbe5137, ed8e5b8)
- Contexte : Jellyfin keeps "Docu", "DOCU" and "docu" as separate entries (ed8e5b8), and merges a refreshed item's tags with the old ones instead of replacing them (dbe5137; Services/TaxonomyService.cs:96-97).
- Décision : rules (ignore, rename, merge, fix the spelling) with the highest authority over API sources, local sources and file-backed episodes; applied at import AND on every `ItemUpdated` of a managed item, idempotent (Services/TaxonomyService.cs:101); an "Appliquer" action rewrites the items that already hold old terms; only the terms a rule hits, only on items the plugin manages; imported terms stored in alphabetical order (720ab16).
- Alternatives écartées : deleting the old unused entries from Jellyfin's database → not done, "the plugin does not edit" it (ed8e5b8). No other alternative recorded.
- Conséquences : the old unused entries stay in Jellyfin's database.
- Liens : dépend de D005
- Code : Services/TaxonomyService.cs, Services/TaxonomyStore.cs, Api/TaxonomyController.cs, docs/architecture/taxonomy.md; tools/ui-test/taxonomy-real.mjs
- À réévaluer si : Jellyfin replaces tags on refresh instead of merging them (the on-save hook would no longer be needed) [déduit du commentaire TaxonomyService.cs:96-97; confirmed by the maintainer, 2026-11-24].
- Fiabilité : [vérifié] commit bodies and code lines; taxonomy-real.mjs added in dd22abf, re-run not recorded.

## D011 — Landscape series/season posters are cropped to the centre 2:3 by the plugin (opt-in per source)
- Date : 2026-11-21 (518362c, e16a597)
- Contexte : jellyfin-web asks the server for a width-limited poster and crops it to portrait in the browser, so a 1080p landscape picture ends up blurry (518362c; docs/architecture/images.md:15).
- Décision : `SourceConfiguration.LandscapePosterCrop`, off by default (Configuration/SourceConfiguration.cs:184); Series/Season Primary only; centre 2:3 crop at the source's full resolution (Services/LandscapePosterConverter.cs); the option is read again at download time and counts on the tag's source or on the local source that overrides the item (Providers/CustomImageProvider.cs:177); `ForgetLandscapePoster` drops a current landscape Primary so that a plain refresh takes the cropped one (:282); a portrait poster is never touched.
- Alternatives écartées : a plain colour or black background → not pretty on screen; a blurred background like the one of the episode thumbnails (Services/PortraitThumbConverter.cs:283-290) was the option considered → in a 2:3 frame a 16:9 picture keeps its full width but fills only about 37 % of the height (2/3 ÷ 16/9 ≈ 0.375), so the subject becomes a thin band in the middle of the poster, while the crop fills the whole frame (maintainer, 2026-11-24; the converter crops: Services/LandscapePosterConverter.cs:8, :18).
- Conséquences : for an uploaded poster of a local override, only the local source may carry the setting (docs/architecture/images.md:15).
- Liens : —
- Code : Services/LandscapePosterConverter.cs; Providers/CustomImageProvider.cs; tools/ui-test/poster-crop-real.mjs, tools/ui-test/poster-crop-local-real.mjs
- À réévaluer si : jellyfin-web stops cropping landscape posters in the browser [déduit from 518362c; confirmed by the maintainer, 2026-11-24].
- Fiabilité : [vérifié] commit bodies and code lines; tests added in 99a46a5, be13d30, af413c1, re-run not recorded.

## D010 — Local gallery images are typed by their shape on upload; a default image can be demoted
- Date : 2026-11-20 to 2026-11-22 (df49def to 24305ab)
- Contexte : galleries are filled with many files or a whole folder at once (df49def; 57ce477 mentions a hundred images). The ratios and the 1280 px threshold come from the maintainer's own tests, which showed gaps in the basic rules given at the start of the development (maintainer, 2026-11-24).
- Décision : a sub-folder named after an image type (French names included) sets the type of its images (df49def); otherwise "Selon le format", the same rule for every kind: ratio ≤ 1 → Primary, ratio ≥ 3 → Banner, landscape at least 1280 px wide → Backdrop, smaller → Thumb (Configuration/config.html:4091-4102, in the browser); a portrait detected on an episode is flagged no-default (24305ab); any default can be demoted (`LocalImage.NoDefault`, Services/LocalSourceStore.cs:57: kept in the gallery, listed last by Providers/CustomImageProvider.cs:171, skipped by the targeted refresh); `CleanGallery` keeps up to 1000 images per node (Services/LocalSourceStore.cs:608-617; it silently kept 60 before 57ce477).
- Alternatives écartées : the first size rule squeezed images below 1280 px into the wrong types → abaa7c5 (a small backdrop looks pixelated); episodes limited to Thumb/Primary → a backdrop never reached an episode (9d8d306, 1fa1c28). No other alternative recorded.
- Conséquences : the shape rule runs only in the browser; `NoDefault` has no automated server-side test (docs/architecture/local-sources.md:19).
- Liens : dépend de D007
- Code : Configuration/config.html (`detectGalleryType`); Services/LocalSourceStore.cs; Providers/CustomImageProvider.cs; Services/SourceRefreshService.cs (`ApplyImagesAsync`); commits df49def, daf884f, abaa7c5, 9d8d306, 8086bd2, 1fa1c28, 57ce477, 24305ab
- À réévaluer si : images are often typed wrongly on upload (the usual formats of the sources change), or the detection moves to the server (maintainer, 2026-11-24).
- Fiabilité : [vérifié] commit bodies and code lines; tools/ui-test/config-local-ui.mjs covers the shape cases with real decoded images (abaa7c5, 9d8d306), re-run not recorded.

## D009 — A missing episode keeps its own number; a clash with a real episode moves it to season number -1
- Date : 2026-11-23
- Contexte : Jellyfin 10.11.6 `SeriesMetadataService.RemoveObsoleteEpisodes` (every series refresh) groups the series' episodes by `ParentIndexNumber` and deletes a virtual episode with no number or whose number a real episode of the same group holds. All seasons without a season number share the null group, so a real `Clip 09.mkv` in another unnumbered season deleted missing episode 9. Earlier plugin versions renumbered the virtual episode, then told the user to number the season folder (P003); the maintainer rejected both: the number typed in the list must win, with no workaround on their side.
- Décision : a missing episode always keeps its number from the list (local source or API). When a real episode of its group holds that number, the virtual episode gets `ParentIndexNumber = -1` (`SeparateGroupSeasonNumber`), and goes back to the season's number once the number is free. Only a number given twice by the list still moves the second episode to the next free number.
- Pourquoi -1 : no real episode ever has a negative season number (own group, never deleted); `AiredEpisodeOrderComparer` counts null as -1, so it sorts exactly like the other episodes of an unnumbered season; `Series.FilterEpisodesBySeason` also matches on the parent season, so it stays listed in its own season only; jellyfin-web shows "Saison locale - 9. Neuf".
- Alternatives écartées : renumbering (huge numbers, then behind the other unnumbered seasons) + asking for a season number in the folder name → P003; a positive unused season number → P004; renaming the user's season folders, or a season-number field in the plugin → both touch the user's seasons.
- Conséquences : `CustomEpisodeProvider` returns the assigned episode AND season numbers for a virtual episode, so a Jellyfin refresh keeps them ([vérifié] in the 10.11.6 source: `MetadataService.MergeData` copies the provider's `ParentIndexNumber` when replacing all metadata, so a null there would put the episode back in the null group, where it gets deleted).
- Liens : motivée par P003, P004
- Code : Services/MissingEpisodeService.cs (`SeparateGroupSeasonNumber`, `AssignIndexNumbers`, `ComputeIndexNumbers`, `EnforceNumbersAsync`, `TryGetAssignedNumber`); Providers/CustomEpisodeProvider.cs; tools/ui-test/missing-number-real.mjs; docs/architecture/missing-episodes.md. Commit 41544ee (2026-11-23); replaces the renumbering of commits 4ba6180, 9d7d312, f3e7c13 [vérifié, commit bodies read 2026-11-24]; the provider-side enforcement of the chosen number started in b296b42.
- À réévaluer si : a Jellyfin upgrade changes the `RemoveObsoleteEpisodes` grouping, the episode sort comparer or `FilterEpisodesBySeason`, or jellyfin-web starts showing the negative season number.
- Fiabilité : [vérifié] missing-number-real.mjs (two unnumbered seasons, full series refresh with replace-all metadata) ALL PASS; display checked on the bench. [vérifié] the Jellyfin internals above, read in the 10.11.6 source (`SeriesMetadataService.RemoveObsoleteEpisodes`, `AiredEpisodeOrderComparer`, `Series.FilterEpisodesBySeason`, `LibraryManager.FillMissingEpisodeNumbersFromPath`).

## D008 — Dump source type replaces the separate DumpMetadata plugin
- Date : 2026-11-16
- Contexte : the maintainer had a separate plugin (one JSON dump, files matched by id / slug / name / fuzzy score) and wanted it inside this one, replicable under several sources.
- Décision : `SourceType = "Dump"`; the catalogue is kept in memory and served in place of an API call; the movie provider checks dump sources first (legacy path untouched). The fuzzy score caps an unconfirmed SxxExx match at 69 (`UnconfirmedCap`) so it never reaches the default auto-match threshold of 70.
- Alternatives écartées : keeping two plugins → two providers on the same items; the old plugin must be disabled by the user.
- Conséquences : no on-disk copy of the dump (re-downloaded after a restart); old plugin config (ApiUrl, header, noise words) is not migrated: the user recreates it as a Dump source and must disable the old plugin. Not done: Diagnostic replay of a name match (docs/architecture/dump-sources.md:13). The targeted refresh of a dump does cover movies since 3bcfb36 / 4494492 (2026-11-17; Services/SourceRefreshService.cs:382-386), see D016.
- Liens : dépend de D002
- Code : Services/DumpStore.cs, Services/DumpSearch.cs, Api/DumpController.cs, docs/DUMP_SOURCE.md, docs/architecture/dump-sources.md
- À réévaluer si : dumps become too large for memory, or a restart re-download becomes a problem.
- Fiabilité : [vérifié] tested in the 10.11.6 container with tools/ui-test/dump-real.mjs (dump-real.mjs itself was changed by 11 later commits, the last d6bbbd6 on 2026-11-22; whether it was re-run is not recorded); the cap is still in the code (`UnconfirmedCap = 69d`, Services/DumpStore.cs:94, used at :1007).

## D007 — Local sources and people directory live in the plugin, not in Jellyfin items
- Date : 2026-11-13
- Contexte : a forced Jellyfin refresh loses hand-typed edits, and episodes not owned yet need to be declarable (shown as missing).
- Décision : source types Local and People with their own stores, served through interception in `ResilientHttp`; cast overlay for API-source episodes keyed by the episode tag; "Appliquer aux épisodes" runs as a background task (CastApplyService), not QueueRefresh.
- Alternatives écartées : none recorded.
- Conséquences : cast on local-source series/seasons and a real merge of two people are not built (API series/seasons do get the cast overlay: Providers/CustomSeriesProvider.cs:162, Providers/CustomSeasonProvider.cs:161); series/season ordering and episode moves are not built (docs/architecture/local-sources.md:23).
- Liens : dépend de D002
- Code : Services/LocalSourceStore.cs, Services/PeopleStore.cs, Services/CastOverlayStore.cs, Services/CastApplyService.cs, Api/ResilientHttp.cs (local interception, :119-120), docs/architecture/local-sources.md, docs/architecture/people-cast.md
- À réévaluer si : edits must survive outside Jellyfin (export/import needed).
- Fiabilité : [vérifié] in the test container (2026-11-13), 34 checks in tools/ui-test/config-local-ui.mjs at the time; the script now has 55 checks according to docs/architecture/people-cast.md:26 and was changed by later commits up to 24305ab (2026-11-22); not re-run by the 2026-11-24 audit.

## D006 — Applied images are persisted explicitly in Jellyfin's database
- Date : 2026-11-15
- Contexte : after "Actualiser les éléments", series poster and backdrop vanished once Jellyfin reloaded the item.
- Décision : `SourceRefreshService.ApplyImagesAsync` calls `UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate)` after `IProviderManager.SaveImage`, which only writes the file and sets the path in memory.
- Alternatives écartées : none.
- Conséquences : the bug is only visible with library image fetchers off AND by reading jellyfin.db before anything else touches the item.
- Symptôme : local source, images imported from the `Primary/` / `Backdrop/` sub-folders, "Actualiser les éléments" → poster and backdrop gone in the library (after a restart or cache eviction), yet "Modifier les images" still listed them; nothing abnormal in the logs. `BaseItemImageInfos` in jellyfin.db kept the old list.
- Pourquoi c'était difficile à reproduire : in the test container Jellyfin's own refresh brought the images (fetchers included the plugin), so the plugin's apply path never did the work; `GET /Items/{id}` answers from memory and opening the item in jellyfin-web saves it, both hiding the missing write; the media was mounted `:ro`.
- Ce qui l'a rendu visible : library image fetchers off, run the refresh, read jellyfin.db read-only BEFORE anything else touches the item (`tools/ui-test/refresh-persist-real.mjs`).
- Pièges voisins : see ETAT.md (old `poster.jpg`/`backdrop.jpg` wins again, now reported as `LocalFilesKept`; fetchers without the plugin → provider never called, now warned in the Sources tab).
- Liens : —
- Code : commit 49f48e5; Services/SourceRefreshService.cs (`UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate)` at :637); tools/ui-test/refresh-persist-real.mjs
- À réévaluer si : Jellyfin changes SaveImage to persist by itself.
- Fiabilité : [vérifié] reproduced by reading jellyfin.db, and confirmed by the maintainer on their server. SourceRefreshService.cs changed since (3bcfb36, 4494492, 1fa1c28) and still makes the call; refresh-persist-real.mjs is unchanged since 49f48e5, a re-run is not recorded.

## D005 — The plugin never repairs Jellyfin's database
- Date : 2026-11-14
- Contexte : "UNIQUE constraint failed: UserData…" on DeleteItem after a series folder rename.
- Décision : cause is two existing real episodes sharing one CustomDataKey for a user, not the plugin; the fix is manual SQL (dedupe) applied by the maintainer. The plugin only logs and skips a refused virtual-episode deletion (commit 7a449c5).
- Symptôme : on the maintainer's server (2026-11-14) the scan failed with `UNIQUE constraint failed: UserData.ItemId, UserData.UserId, UserData.CustomDataKey` in `BaseItemRepository.DeleteItem`; old items stayed with "Could not find file <old path>" image errors.
- Cause : two EXISTING real episodes (not virtual, some without any source tag) shared one `CustomDataKey` (series key + S/E digits, e.g. `…001001`) for the same user; deleting the old series moves both rows to the placeholder item in one UPDATE, which collides.
- Correctif appliqué par le mainteneur : stop Jellyfin, copy `jellyfin.db*`, then delete all but the first row (`o.rowid < UserData.rowid`) of each (UserId, CustomDataKey) group among non-placeholder rows. 5 rows, scan OK afterwards.
- Alternatives écartées : automatic placeholder repair in the plugin → experiments showed Jellyfin 10.11.0/.3/.6 already handles that clash (placeholder+live and live+live pairs deleted by separate scans do not fail).
- Conséquences : if the error is reported again, ask for the read-only duplicate-keys query result first. Do not suggest plugin causes (the maintainer stressed these episodes are unrelated to missing episodes).
- Liens : motivée par P001
- Code : commit 7a449c5 (Services/MissingEpisodeService.cs; same patch as the pre-merge commit 19c1e31, which no branch contains)
- À réévaluer si : a failing case is shown that Jellyfin does not handle.
- Fiabilité : [vérifié] source of three versions read and reproduced in the test container; [supposé] root cause on the maintainer's DB (deduced from the duplicate rows).

## D004 — Provider ids are read case-insensitively through CustomId
- Date : on or before 2026-11-13 (Services/CustomId.cs is in the initial import 1966d81; exact date not recorded)
- Contexte : items re-read from the DB get a case-sensitive ProviderIds; the maintainer's DB held `Custom=` on virtual episodes, so `TryGetValue("custom")` silently failed.
- Décision : always use `CustomId.Get` / `CustomId.IsCustomKey`; bookkeeping must not depend on ProviderIds. The movie provider still uses the old lookup (the maintainer asked not to touch movies).
- Alternatives écartées : none.
- Conséquences : new code must never compare ProviderIds keys directly.
- Liens : —
- Code : Services/CustomId.cs
- À réévaluer si : the movie provider is allowed to be modified (it was touched once since, 5f47bf9 for dump sources, and kept the old lookup).
- Fiabilité : [vérifié] Services/CustomId.cs:17, :28 (OrdinalIgnoreCase at :30); the only direct `TryGetValue("custom")` left is Providers/CustomMetadataProvider.cs:171 (grep 2026-11-24).

## D003 — Tag is read from the item's own name, never the full path; season and series read their folder first
- Date : on or before 2026-11-13 (Services/TagParser.cs and the `info.Path` reading of CustomSeasonProvider are in the initial import 1966d81; exact date not recorded)
- Contexte : a full path makes a season or episode pick up the series id from a parent folder; `info.Name` is Jellyfin's parsed title, truncated at things like `(2025)`. `CustomSeasonProvider` read `info.Name`, so season API metadata never applied on refresh (fixed 2026-11-13).
- Décision : rewritten on 2026-11-24 to describe the current code (maintainer's request). `TagParser` only looks at the item's own name (`TagParser.GetOwnName`), never at the full path, but each provider has its own order:
  - season: own folder name from `info.Path`, then `info.Name`, then the stored `custom` id (Providers/CustomSeasonProvider.cs:77);
  - series: own folder name from `info.Path`, then the stored `custom` id, then `info.Name`, then the IMDb id and the plain name (Providers/CustomSeriesProvider.cs, `GetItemIdentifier`; folder first since commit 4e9c4bc (2026-11-24); before it: stored id, then `info.Name`, never the path);
  - episode: the stored `custom` id (Providers/CustomEpisodeProvider.cs:387), then `Path.GetFileName(info.Path)` (:400-405), `info.Name` only when there is no path (:410); then the dump match and the series-based id.
- Alternatives écartées : full path → a season or episode takes the parent's tag; `info.Name` first → truncated tag (the season bug above).
- Conséquences : multi-tag files only for episodes (first tag is the reference). `info.Name` is only a fallback for seasons and series (after the folder name), which the CLAUDE.md rule (request flow, step 1) tolerates in practice. Before the 2026-11-24 fix a series kept its id only through the stored `custom` id: once enriched, its `info.Name` is the API title (no tag), so a series whose `custom` id was cleared was never re-identified. A tagged folder now wins over a stored id set by hand (Identify) on that series, as for seasons.
- Liens : —
- Code : Services/TagParser.cs, Providers/CustomSeasonProvider.cs, Providers/CustomSeriesProvider.cs (commit 4e9c4bc), Providers/CustomEpisodeProvider.cs
- À réévaluer si : Jellyfin stops truncating info.Name; a user needs to point a tagged series or season at another id than its folder tag (Identify), which the folder tag now overrides.
- Fiabilité : [vérifié] provider order, code read on 2026-11-24. [vérifié] container test on 2026-11-24 (Jellyfin 10.11.6, plugin DLL of 2026-11-23, CustomSeriesProvider unchanged since dbe5137), folder « Série datée (2025) {dm-bigimg} »: on the first scan `info.Name` is the whole folder name, year and tag included, so the tag is found and the series enriched (a year does NOT lose the tag); a full refresh then logs `Processing series: Grande affiche` (the API title) and still finds `dm-bigimg` through the stored id; after clearing the provider ids (`POST /Items/{id}`) a full refresh logs `Found identifier 'Grande affiche'` then `No source found` and the series stays unidentified (no provider id in a read-only copy of jellyfin.db). [vérifié] after the fix (same container, same scenario): the cleared series logs `Found identifier 'dm-bigimg'` and gets `custom = dm-bigimg` back (read-only copy of jellyfin.db); `Tech Talks {dm-techtalks}` unchanged; « Série sans identifiant » still skipped. [vérifié] earlier container test (series, season, episode, collision case), date not recorded.

## D002 — Multi-source configuration keyed by tag prefix; legacy single-source kept
- Date : on or before 2026-11-13 (Services/SourceResolver.cs and Configuration/SourceConfiguration.cs are in the initial import 1966d81; exact date not recorded)
- Contexte : several APIs must be queried from one plugin; the tag prefix selects the source.
- Décision : `PluginConfiguration.Sources`; a prefix cannot contain a hyphen (split at the first `-`); no matching enabled source → empty result so other providers can act. Legacy top-level fields stay for XML compatibility; the plugin id key stays `"custom"`.
- Alternatives écartées : none recorded.
- Conséquences : no Dictionary in the config (XML serialization → HTTP 500); XML element names are the C# names.
- Liens : —
- Code : PluginConfiguration.cs, Configuration/SourceConfiguration.cs, Services/SourceResolver.cs, Providers/CustomExternalId.cs
- À réévaluer si : the legacy single-source path can be dropped.
- Fiabilité : [vérifié] split at the first hyphen: Services/SourceResolver.cs:109 (`Split('-', 2)`); key `"custom"`: Providers/CustomExternalId.cs:23.

## D001 — Build against Jellyfin 10.11.6 exactly (net9.0, ExcludeAssets=runtime)
- Date : on or before 2026-11-13 (same package versions in the initial import 1966d81; exact date not recorded)
- Contexte : building against a newer 10.11.x than the server can break plugin loading.
- Décision : net9.0, Jellyfin.Controller/Model 10.11.6 with ExcludeAssets=runtime; Microsoft.Extensions.* at 9.0.11 (avoids NU1605); SkiaSharp 3.116.1, the server's exact version, with ExcludeAssets=runtime;native (used by the image converters).
- Alternatives écartées : newer package versions.
- Conséquences : bump the package only together with the server.
- Liens : —
- Code : Jellyfin.Plugin.CustomMetadata.csproj
- À réévaluer si : the server is upgraded.
- Fiabilité : [vérifié] Jellyfin.Plugin.CustomMetadata.csproj:4, :19, :22, :27-28, :30-34 (read 2026-11-24).
