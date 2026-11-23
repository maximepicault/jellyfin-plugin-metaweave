# Index
D009 | active | A missing episode keeps its own number; a clash with a real episode moves it to season number -1 | motivée par P003, P004
D008 | active | Dump source type replaces the separate DumpMetadata plugin | dépend de D002
D007 | active | Local sources and people directory live in the plugin, not in Jellyfin items | dépend de D002
D006 | active | Applied images are persisted explicitly in Jellyfin's database | —
D005 | active | The plugin never repairs Jellyfin's database | motivée par P001
D004 | active | Provider ids are read case-insensitively through CustomId | —
D003 | active | Tag is read from the item's own file/folder name, never the full path or info.Name | —
D002 | active | Multi-source configuration keyed by tag prefix; legacy single-source kept | —
D001 | active | Build against Jellyfin 10.11.6 exactly (net9.0, ExcludeAssets=runtime) | —

# Détail

## D009 — A missing episode keeps its own number (season number -1 on a clash)
- Date : 2026-11-23
- Contexte : Jellyfin 10.11.6 `SeriesMetadataService.RemoveObsoleteEpisodes` (every series refresh) groups the series' episodes by `ParentIndexNumber` and deletes a virtual episode with no number or whose number a real episode of the same group holds. All seasons without a season number share the null group, so a real `Clip 09.mkv` in another unnumbered season deleted missing episode 9. Earlier plugin versions renumbered the virtual episode, then told the user to number the season folder (P003); the maintainer rejected both: the number typed in the list must win, with no workaround on their side.
- Décision : a missing episode always keeps its number from the list (local source or API). When a real episode of its group holds that number, the virtual episode gets `ParentIndexNumber = -1` (`SeparateGroupSeasonNumber`), and goes back to the season's number once the number is free. Only a number given twice by the list still moves the second episode to the next free number.
- Pourquoi -1 : no real episode ever has a negative season number (own group, never deleted); `AiredEpisodeOrderComparer` counts null as -1, so it sorts exactly like the other episodes of an unnumbered season; `Series.FilterEpisodesBySeason` also matches on the parent season, so it stays listed in its own season only; jellyfin-web shows "Saison locale - 9. Neuf".
- Alternatives écartées : renumbering (huge numbers, then behind the other unnumbered seasons) + asking for a season number in the folder name → P003; a positive unused season number → P004; renaming the user's season folders, or a season-number field in the plugin → both touch the user's seasons.
- Conséquences : `CustomEpisodeProvider` returns the assigned episode AND season numbers for a virtual episode, so a Jellyfin refresh keeps them ([vérifié] in the 10.11.6 source: `MetadataService.MergeData` copies the provider's `ParentIndexNumber` when replacing all metadata, so a null there would put the episode back in the null group, where it gets deleted).
- Liens : motivée par P003, P004
- Code : Services/MissingEpisodeService.cs (`SeparateGroupSeasonNumber`, `AssignIndexNumbers`, `ComputeIndexNumbers`, `EnforceNumbersAsync`, `TryGetAssignedNumber`); Providers/CustomEpisodeProvider.cs; tools/ui-test/missing-number-real.mjs; docs/architecture/missing-episodes.md. Uncommitted on develop as of 2026-11-23; replaces the renumbering of commits 4ba6180, 9d7d312, f3e7c13 [supposé, from the commit titles].
- À réévaluer si : a Jellyfin upgrade changes the `RemoveObsoleteEpisodes` grouping, the episode sort comparer or `FilterEpisodesBySeason`, or jellyfin-web starts showing the negative season number.
- Fiabilité : [vérifié] missing-number-real.mjs (two unnumbered seasons, full series refresh with replace-all metadata) ALL PASS; display checked on the bench. [vérifié] the Jellyfin internals above, read in the 10.11.6 source (`SeriesMetadataService.RemoveObsoleteEpisodes`, `AiredEpisodeOrderComparer`, `Series.FilterEpisodesBySeason`, `LibraryManager.FillMissingEpisodeNumbersFromPath`).

## D008 — Dump source type replaces the separate DumpMetadata plugin
- Date : 2026-11-16
- Contexte : the maintainer had a separate plugin (one JSON dump, files matched by id / slug / name / fuzzy score) and wanted it inside this one, replicable under several sources.
- Décision : `SourceType = "Dump"`; the catalogue is kept in memory and served in place of an API call; the movie provider checks dump sources first (legacy path untouched). The fuzzy score caps an unconfirmed SxxExx match at 69 (`UnconfirmedCap`) so it never reaches the default auto-match threshold of 70.
- Alternatives écartées : keeping two plugins → two providers on the same items; the old plugin must be disabled by the user.
- Conséquences : no on-disk copy of the dump (re-downloaded after a restart); old plugin config (ApiUrl, header, noise words) is not migrated: the user recreates it as a Dump source and must disable the old plugin. Not done: Diagnostic replay of a name match, targeted refresh for movies.
- Liens : dépend de D002
- Code : Services/DumpStore.cs, Services/DumpSearch.cs, Api/DumpController.cs, docs/DUMP_SOURCE.md, docs/architecture/dump-sources.md
- À réévaluer si : dumps become too large for memory, or a restart re-download becomes a problem.
- Fiabilité : [vérifié] tested in the 10.11.6 container with tools/ui-test/dump-real.mjs (check count not re-verified since 7+ later commits on DumpStore/DumpSearch); the cap is still in the code (`UnconfirmedCap = 69d`, Services/DumpStore.cs).

## D007 — Local sources and people directory live in the plugin
- Date : 2026-11-13
- Contexte : a forced Jellyfin refresh loses hand-typed edits, and episodes not owned yet need to be declarable (shown as missing).
- Décision : source types Local and People with their own stores, served through interception in `ResilientHttp`; cast overlay for API-source episodes keyed by the episode tag; "Appliquer aux épisodes" runs as a background task (CastApplyService), not QueueRefresh.
- Alternatives écartées : none recorded.
- Conséquences : series/season cast and real merge of two people are not built; series/season ordering and episode moves are not built.
- Liens : dépend de D002
- Code : Services/LocalSourceStore.cs, Services/PeopleStore.cs, Services/CastOverlayStore.cs, Services/CastApplyService.cs, docs/architecture/local-sources.md, docs/architecture/people-cast.md
- À réévaluer si : edits must survive outside Jellyfin (export/import needed).
- Fiabilité : [vérifié] in the test container (2026-11-13), 34 checks in tools/ui-test/config-local-ui.mjs.

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
- Code : commit 49f48e5; Services/SourceRefreshService.cs; tools/ui-test/refresh-persist-real.mjs
- À réévaluer si : Jellyfin changes SaveImage to persist by itself.
- Fiabilité : [vérifié] reproduced by reading jellyfin.db, and confirmed by the maintainer on their server.

## D005 — The plugin never repairs Jellyfin's database
- Date : 2026-11-14
- Contexte : "UNIQUE constraint failed: UserData…" on DeleteItem after a series folder rename.
- Décision : cause is two existing real episodes sharing one CustomDataKey for a user, not the plugin; the fix is manual SQL (dedupe) applied by the maintainer. The plugin only logs and skips a refused virtual-episode deletion (commit 19c1e31).
- Symptôme : on the maintainer's server (2026-11-14) the scan failed with `UNIQUE constraint failed: UserData.ItemId, UserData.UserId, UserData.CustomDataKey` in `BaseItemRepository.DeleteItem`; old items stayed with "Could not find file <old path>" image errors.
- Cause : two EXISTING real episodes (not virtual, some without any source tag) shared one `CustomDataKey` (series key + S/E digits, e.g. `…001001`) for the same user; deleting the old series moves both rows to the placeholder item in one UPDATE, which collides.
- Correctif appliqué par le mainteneur : stop Jellyfin, copy `jellyfin.db*`, then delete all but the first row (`o.rowid < UserData.rowid`) of each (UserId, CustomDataKey) group among non-placeholder rows. 5 rows, scan OK afterwards.
- Alternatives écartées : automatic placeholder repair in the plugin → experiments showed Jellyfin 10.11.0/.3/.6 already handles that clash (placeholder+live and live+live pairs deleted by separate scans do not fail).
- Conséquences : if the error is reported again, ask for the read-only duplicate-keys query result first. Do not suggest plugin causes (the maintainer stressed these episodes are unrelated to missing episodes).
- Liens : motivée par P001
- Code : commit 19c1e31 (Services/MissingEpisodeService.cs)
- À réévaluer si : a failing case is shown that Jellyfin does not handle.
- Fiabilité : [vérifié] source of three versions read and reproduced in the test container; [supposé] root cause on the maintainer's DB (deduced from the duplicate rows).

## D004 — Provider ids are read case-insensitively through CustomId
- Date : 2026-11 (exact date not recorded)
- Contexte : items re-read from the DB get a case-sensitive ProviderIds; the maintainer's DB held `Custom=` on virtual episodes, so `TryGetValue("custom")` silently failed.
- Décision : always use `CustomId.Get` / `CustomId.IsCustomKey`; bookkeeping must not depend on ProviderIds. The movie provider still uses the old lookup (the maintainer asked not to touch movies).
- Alternatives écartées : none.
- Conséquences : new code must never compare ProviderIds keys directly.
- Liens : —
- Code : Services/CustomId.cs
- À réévaluer si : the movie provider is allowed to be modified.
- Fiabilité : [vérifié] as stated in CLAUDE.md.

## D003 — Tag read from the item's own file/folder name
- Date : 2026-11 (exact date not recorded)
- Contexte : a full path makes a season or episode pick up the series id from a parent folder; `info.Name` is truncated at things like `(2025)`. `CustomSeasonProvider` read `info.Name`, so season API metadata never applied on refresh (fixed 2026-11-13).
- Décision : `TagParser` on `Path.GetFileName(info.Path)` only.
- Alternatives écartées : full path, info.Name → reasons above.
- Conséquences : multi-tag files only for episodes (first tag is the reference).
- Liens : —
- Code : Services/TagParser.cs, Providers/CustomSeasonProvider.cs
- À réévaluer si : Jellyfin stops truncating info.Name.
- Fiabilité : [vérifié] in the test container (series, season, episode, collision case).

## D002 — Multi-source configuration keyed by tag prefix
- Date : 2026-11 (exact date not recorded)
- Contexte : several APIs must be queried from one plugin; the tag prefix selects the source.
- Décision : `PluginConfiguration.Sources`; a prefix cannot contain a hyphen (split at the first `-`); no matching enabled source → empty result so other providers can act. Legacy top-level fields stay for XML compatibility; the plugin id key stays `"custom"`.
- Alternatives écartées : none recorded.
- Conséquences : no Dictionary in the config (XML serialization → HTTP 500); XML element names are the C# names.
- Liens : —
- Code : PluginConfiguration.cs, Services/SourceResolver.cs, Providers/CustomExternalId.cs
- À réévaluer si : the legacy single-source path can be dropped.
- Fiabilité : [vérifié] documented in CLAUDE.md.

## D001 — Build against Jellyfin 10.11.6 exactly
- Date : 2026-11 (exact date not recorded)
- Contexte : building against a newer 10.11.x than the server can break plugin loading.
- Décision : net9.0, Jellyfin.Controller/Model 10.11.6 with ExcludeAssets=runtime; Microsoft.Extensions.* at 9.0.11 (avoids NU1605).
- Alternatives écartées : newer package versions.
- Conséquences : bump the package only together with the server.
- Liens : —
- Code : Jellyfin.Plugin.CustomMetadata.csproj
- À réévaluer si : the server is upgraded.
- Fiabilité : [vérifié] csproj read.
