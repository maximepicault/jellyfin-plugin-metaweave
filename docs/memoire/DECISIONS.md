# Index
D008 | active | Dump source type replaces the separate DumpMetadata plugin | dépend de D002
D007 | active | Local sources and people directory live in the plugin, not in Jellyfin items | dépend de D002
D006 | active | Applied images are persisted explicitly in Jellyfin's database | —
D005 | active | The plugin never repairs Jellyfin's database | motivée par P001
D004 | active | Provider ids are read case-insensitively through CustomId | —
D003 | active | Tag is read from the item's own file/folder name, never the full path or info.Name | —
D002 | active | Multi-source configuration keyed by tag prefix; legacy single-source kept | —
D001 | active | Build against Jellyfin 10.11.6 exactly (net9.0, ExcludeAssets=runtime) | —

# Détail

## D008 — Dump source type replaces the separate DumpMetadata plugin
- Date : 2026-11-16
- Contexte : the maintainer had a separate plugin (one JSON dump, files matched by id / slug / name / fuzzy score) and wanted it inside this one, replicable under several sources.
- Décision : `SourceType = "Dump"`; the catalogue is kept in memory and served in place of an API call; the movie provider checks dump sources first (legacy path untouched). The fuzzy score caps an unconfirmed SxxExx match at 69 (`UnconfirmedCap`) so it never reaches the default auto-match threshold of 70.
- Alternatives écartées : keeping two plugins → two providers on the same items; the old plugin must be disabled by the user.
- Conséquences : no on-disk copy of the dump (re-downloaded after a restart); old plugin config is not migrated.
- Liens : dépend de D002
- Code : Services/DumpStore.cs, Services/DumpSearch.cs, Api/DumpController.cs, docs/DUMP_SOURCE.md, docs/architecture/dump-sources.md
- À réévaluer si : dumps become too large for memory, or a restart re-download becomes a problem.
- Fiabilité : [vérifié] tested in the 10.11.6 container with tools/ui-test/dump-real.mjs (56+ checks); the cap rule [vérifié] by the same tests.

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
- Liens : —
- Code : commit 49f48e5; Services/SourceRefreshService.cs; tools/ui-test/refresh-persist-real.mjs
- À réévaluer si : Jellyfin changes SaveImage to persist by itself.
- Fiabilité : [vérifié] reproduced by reading jellyfin.db, and confirmed by the maintainer on their server.

## D005 — The plugin never repairs Jellyfin's database
- Date : 2026-11-14
- Contexte : "UNIQUE constraint failed: UserData…" on DeleteItem after a series folder rename.
- Décision : cause is two existing real episodes sharing one CustomDataKey for a user, not the plugin; the fix is manual SQL (dedupe) applied by the maintainer. The plugin only logs and skips a refused virtual-episode deletion (commit 19c1e31).
- Alternatives écartées : automatic placeholder repair in the plugin → experiments showed Jellyfin 10.11.0/.3/.6 already handles that clash.
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
- Code : Configuration/PluginConfiguration.cs, Services/SourceResolver.cs, Providers/CustomExternalId.cs
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
