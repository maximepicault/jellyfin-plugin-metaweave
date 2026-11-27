# Code map

> Navigation map of the code: where each thing lives, what calls what, where state is kept. Read it FIRST to find the file to open, then the per-feature doc of this folder for the detail. It holds no business rules (those are in the per-feature docs) and no reasons (those are in `docs/memoire/DECISIONS.md`, cited as `Dxxx`).
>
> Anchors are symbol names, not line numbers: `grep -n` them. Verified against the code at commit f32fe3f (2026-11-24). When you add a service, a controller, a page, a stored file or a background job, update the matching table here in the same commit.

## 1. Layout

| Folder | What is in it |
|---|---|
| `Plugin.cs`, `PluginServiceRegistrator.cs`, `PluginConfiguration.cs` | Entry point (pages list), DI registrations, global config (+ legacy single-source fields, `FieldMapping`) |
| `Configuration/` | `SourceConfiguration.cs` (one source: endpoints, mappings, options, `DumpSettings`, person mappings) and the 6 embedded pages `*.html` (csproj: `EmbeddedResource Include="Configuration\*.html"`) |
| `Providers/` | Everything Jellyfin discovers by reflection: metadata/image providers, external id, ignore rule, media source provider, person link providers |
| `Api/` | REST controllers (`CustomMetadata/...`), plus the HTTP layer: `CustomApiClient`, `ResilientHttp`, `SourceHeaders` |
| `Services/` | Stores (JSON files), background services, pure helpers (parsers, mappers, converters) |
| `ScheduledTasks/` | `MissingEpisodesTask` (daily, key `CustomMetadataMissingEpisodes`) |
| `tools/ui-test/` | Test bench (Docker Jellyfin 12.4 on 8097, mock API `server.py`, demo data `data.py`/`gen.py`, CDP/Playwright scripts) |
| `deploy/` | Server-side updater (systemd timer) and its sandbox test |
| `web/gallery.js` | Episode photo gallery script, embedded in the DLL, added to jellyfin-web’s page by `ThemeScriptMiddleware` when `EnableEpisodePhotos` is on |
| `theme/projection.css`, `theme/projection.js`, `theme/projection-index.js` | Optional jellyfin-web theme (D013) and its optional scripts (the second: filtered lists, `theme-index.md`), embedded in the DLL and served by `ThemeController`; `Services/ThemeScriptMiddleware.cs` adds the scripts to jellyfin-web's page |

## 2. Wiring

### 2.1 Discovered by Jellyfin (reflection, no registration)

| Type | Interface | Role |
|---|---|---|
| `CustomSeriesProvider`, `CustomSeasonProvider`, `CustomEpisodeProvider`, `CustomMetadataProvider` (movies) | `IRemoteMetadataProvider<T, TInfo>` | Metadata per item type (§3) |
| `CustomImageProvider` | `IRemoteImageProvider` | Images for all types, picker URLs, download (`GetImageResponse`) |
| `EpisodePhotoImageProvider` | `IRemoteImageProvider` | Offers an episode’s photos in the interactive image search only |
| `CustomExternalId` | `IExternalId` | Shows `ProviderIds["custom"]` in the metadata editor; `Key` is always `"custom"` |
| `ImageFolderIgnoreRule` | `IResolverIgnoreRule` | Keeps image sub-folders (`Primary/`, `Backdrop/`…) of a tagged series/season out of the library, and numbered photo folders when the photo gallery is on |
| `PreviewMediaSourceProvider` | `IMediaSourceProvider` | Makes a virtual episode playable through its preview trailer (`PreviewMapper`) |
| `PersonNetworkSlot00…` (`PersonNetworkUrlProviders.cs`) | `IExternalUrlProvider` | Fixed pool of link slots on Jellyfin's person page, filled at runtime from `PersonNetworkStore` |
| `MissingEpisodesTask` | `IScheduledTask` | Runs `MissingEpisodeService.AnalyzeAsync`, then `CastApplyService.SyncUntaggedAsync` (cast of the tag-less children of cascading series/seasons) |
| Controllers in `Api/*Controller.cs` | `ControllerBase` | §6 |

### 2.2 Registered in `PluginServiceRegistrator` (all singletons; never `new` them)

| Service | Depends on | Used by |
|---|---|---|
| `MetadataCacheService` | `IMemoryCache` | `CustomApiClient` |
| `LocalSourceStore` | lazy `PeopleStore`, local API URL | `CustomApiClient`, `ResilientHttp`, `CustomImageProvider`, `MissingEpisodeService`, `CastApplyService`, Local/FolderImages/People controllers |
| `PeopleStore` | `LocalSourceStore` | providers (cast), `CastApplyService`, `MissingEpisodeService`, People controller |
| `EpisodePhotoService`, `EpisodePhotoThumbnails`, `EpisodePhotoLinks` (+ `EpisodePhotoDownloadStartupFilter`) | library, users, `LocalSourceStore`, app paths | Photos controller, `EpisodePhotoImageProvider` (resolved through `IServiceProvider`) |
| `PersonNetworkStore` (+ hosted `PersonNetworkReadyService`) | — | person link slots, People controller |
| `DumpStore` | `IHttpClientFactory` | `CustomApiClient`, `ResilientHttp`, episode/movie/image providers, `SourceRefreshService`, Dump/SourceTest controllers |
| `CustomApiClient` (owns its `JsonFieldMapper` and `ResilientHttp`) | cache, `LocalSourceStore`, `DumpStore` | every provider and most services |
| `SourceResolver` | — | providers, `MissingEpisodeService`, `SourceRefreshService`, `CastApplyService`, `EpisodePositionService` |
| `TaxonomyStore` (hosted, D012) | — | static `TaxonomyStore.Tags/Genres` called by series/season/episode providers and `MissingEpisodeService` |
| `TaxonomyService` (hosted) | `ILibraryManager`, `TaxonomyStore` | Taxonomy controller; hooks `ItemAdded/ItemUpdated` |
| `CastOverlayStore` | — | providers, `CastInheritance`, `CastApplyService`, `MissingEpisodeService` |
| `CastCatalog`, `CastInheritance`, `CastApplyService` | library, stores | People controller, providers, `MissingEpisodesTask` |
| `SourceRefreshService` | library, `CustomApiClient`, `SourceResolver`, `IProviderManager`, `DumpStore` | SourceRefresh controller |
| `EpisodePositionService` | `CustomApiClient`, `SourceResolver` | `CustomEpisodeProvider` (number of an unnumbered real episode, D014) |
| `VideoDownloadService` (hosted, `BackgroundService`, D015) | `IMediaEncoder` (ffmpeg), library, `ITaskManager` | `MissingEpisodeService` (queues), Downloads controller |
| `MissingEpisodeService` | library, providers manager, API client, stores, `VideoDownloadService`, `DumpCoverageService` | task, MissingEpisodes/Diagnostic controllers, `CustomEpisodeProvider` (static `TryGetAssignedNumber`) |
| `ChapterService` (hosted) | `IChapterManager` | fed by `CustomEpisodeProvider` through static `ChapterService.Remember` |
| `DumpCoverageCache` (hosted), `DumpCoverageService` | library, `DumpStore` | Dump controller, `MissingEpisodeService` (dump seasons) |
| `DiagnosticService` | library, `CustomApiClient` | Diagnostic controller |
| `LibraryIndexService` (hosted) | Jellyfin's database context (read only), library | LibraryIndex controller (`theme-index.md`) |

Helpers get a non-generic `ILogger` from the registrator, so their log lines carry their own category (CLAUDE.md, logging).

## 3. Metadata pipeline (one provider call)

```
Jellyfin refresh ─► Custom<Type>Provider.GetMetadata
   1. EnableMetadata off → empty result
   2. id = tag of the item's own name (TagParser) / stored custom id (CustomId)      ── order per type: §3.1
      episode only: no tag → DumpStore.ResolveAsync (name match) → series-based id
   3. SourceResolver: prefix (split at first '-') → enabled source, else empty result (other providers act)
   4. CustomApiClient.GetMetadataAsync(id, itemType, source)
        ├─ ResolveEndpoint(itemType): series→SeriesEndpoint, season/source→SourceEndpoint, else VideoEndpoint (+ matching FieldMapping)
        ├─ MetadataCacheService (skipped for local sources, and when IsAutomated=false: user refresh)
        ├─ ResilientHttp.GetStringAsync: Local → LocalSourceStore.Serve | Dump → DumpStore.ServeAsync | HTTP (retries, limiter, CORS flip, SourceHeaders)
        ├─ JsonFieldMapper.MapToMetadata → JsonPathExtractor (the single path walker), HtmlText, DurationParser, OverviewListDetector
        └─ local override: LocalSourceStore.FindOverride + MergeOverride (local fields win when filled)
   5. episode: extra tags (TagParser.ExtractAll) fetched and merged by MetadataMerger
   6. TaxonomyStore.Tags/Genres rules, CastMapper.AddCast (+ CastOverlayStore, CastInheritance, PeopleStore)
   7. episode: numbers (TryGetAssignedNumber for virtual ones, EpisodePositionService), ChapterService.Remember, ProviderIds["custom"]
```

### 3.1 Where each provider reads the id (D003)

| Provider | Order (method) |
|---|---|
| Series | own folder name (`info.Path`) → stored `custom` → tag in `info.Name` → IMDb → name (`GetItemIdentifier`) |
| Season | own folder name (`info.Path`) → `info.Name` → stored `custom` (`ExtractSourceId`) |
| Episode | stored `custom` → own file name → `info.Name` if no path (`GetEpisodeIdentifier`), then dump match, then `GetSeriesBasedIdentifier` |
| Image | episode file / season folder / series folder first, then `ProviderIds` (`GetItemIdentifier`, `ExtractIdFromFilename`, `ExtractIdFromFolderName`) |
| Movie | legacy path only: stored `custom` (case-sensitive lookup), IMDb, name; dump sources first (D004, D008) |

Each provider keeps its own small wrapper around `TagParser` (`ExtractSeriesIdFromName`, `ExtractSourceId`, `ExtractIdFromFilename`…): the regex itself exists only in `TagParser`.

### 3.2 Images

`CustomImageProvider.GetImages` → `CustomApiClient.GetImagesAsync` (API answer, then the local source's images) for the first id, then the other ids of a multi-tag episode, then the image sub-folders (`FolderImages`, listed last); demoted images (`NoDefault`) go last → `RemoteImageInfo` with picker URLs (`ToPickerUrl`). The origin of each URL is remembered in the static `ImageSourcePrefixes` (lives until restart) so that `GetImageResponse` knows the source: downloads go through `SourceHeaders`, `local://img/…` is read from disk, and the converters run there (`PortraitThumbConverter` for episode thumbs, `LandscapePosterConverter` for series/season posters, D011). Local gallery typing and demoted defaults: D010. Detail: `images.md`.

## 4. Background work and locks

| Job | Trigger | Concurrency guard | Detail |
|---|---|---|---|
| Missing-episode analysis (`MissingEpisodeService.AnalyzeAsync`) | daily task, `POST MissingEpisodes/Analyze` (queues the task), end of downloads | static `_runLock` | `missing-episodes.md` |
| Targeted refresh (`SourceRefreshService.Start` / `StartDump`; a dump is written directly, D016) | `POST Refresh` | static `RunLock`, one run at a time | `local-sources.md`, `dump-sources.md` |
| Cast propagation (`CastApplyService`) | `POST People/Apply`; `SyncUntaggedAsync` from the daily task | static `RunLock` | `people-cast.md` |
| Taxonomy apply (`TaxonomyService`) | `POST Taxonomy/Apply`; every `ItemAdded/ItemUpdated` of a managed item | static `RunLock` | `taxonomy.md` |
| Video downloads (`VideoDownloadService`) | queued by the analysis | `MaxParallelDownloads`; queue then `QueueLibraryScan` then `MissingEpisodesTask` | `missing-episodes.md` |
| Chapters (`ChapterService`) | `ItemAdded/ItemUpdated` of an episode remembered by the provider | static `Pending` map | `episode-media.md` |
| Dump coverage cache (`DumpCoverageCache`) | invalidated by `ItemAdded/Updated/Removed` | — | `dump-sources.md` |

Rule kept everywhere: several items are refreshed one at a time in a background `Task.Run`, never through `IProviderManager.QueueRefresh` (it drops items when several are queued). A background service must never throw (it can stop the server).

## 5. State kept by the plugin

All under the plugin's `DataFolderPath` unless noted (a temp-folder fallback exists when `Plugin.Instance` is not ready).

| File / folder | Owner |
|---|---|
| `cache/{key}.json` (+ `IMemoryCache`) | `MetadataCacheService` (TTL `CacheTTLHours`, switch `EnableCache`) |
| `local-sources/{LocalDataId}.json`, `local-sources/{LocalDataId}-images/` | `LocalSourceStore` (also videos; `.bak`, `.deleted` on removal) |
| `people.json`, photos under data id `people-directory` | `PeopleStore` |
| `person-networks.json`, `network-icons/` | `PersonNetworkStore` |
| `episode-cast.json` | `CastOverlayStore` |
| `cast-backdrops.json`, `cast-inherited.json` | `CastApplyService` |
| `missing-episodes.json` (report), `missing-ignored.json` | `MissingEpisodeService` |
| `missing-episodes-cache/{seasonId}.json` | `MissingEpisodeCache` |
| `{CachePath}/custom-metadata/photo-thumbs/` (Jellyfin’s cache folder) | `EpisodePhotoThumbnails` |
| `taxonomy.json`, `taxonomy-seen.json` | `TaxonomyStore` |
| `diagnostic.json` | `DiagnosticService` |
| memory only: dump catalogues, download history, refresh status, CORS choice, image origins | `DumpStore`, `VideoDownloadService`, `SourceRefreshService`, `SourceHeaders.OriginMemory`, `CustomImageProvider.ImageSourcePrefixes` |
| plugin XML config (`Jellyfin.Plugin.CustomMetadata.xml`) | `PluginConfiguration` via `XmlSerializer`: no `Dictionary`, element names = C# names (D002) |

## 6. REST API (`Api/`, all admin-only except where noted)

| Route `CustomMetadata/…` | Controller → service | Used by |
|---|---|---|
| `Status`, `Diagnostic`, `Diagnostic/Element` | `DiagnosticController` → `DiagnosticService` | every page (tab bar counters), `diagnostic.html` |
| `TestSource` | `SourceTestController` → `CustomApiClient.TestSourceAsync` | `config.html` |
| `Refresh`, `Refresh/Status` | `SourceRefreshController` → `SourceRefreshService` | `config.html` |
| `Local/{dataId}`, `/Image`, `/Video`, `/Public/{file}`, `ImportEpisodes` | `LocalSourceController` → `LocalSourceStore` | `config.html`, `people.html` (photo upload) |
| `FolderImages/{itemId}/{folder}/{file}` (anonymous GET), `FolderImages/Import` | `FolderImagesController` → `FolderImages`, `LocalSourceStore` | Jellyfin image picker, `config.html` |
| `Photos/{itemId}`, `/Image/{key}`, `/Thumb/{key}` (any signed-in user who sees the item); `Photos/Remote/{itemId}/{key}` (anonymous, signed); `Photos/gallery.js` (anonymous) | `PhotosController` → `EpisodePhotoService`, `EpisodePhotoThumbnails`, `EpisodePhotoLinks` | `web/gallery.js`, Jellyfin image search (`episode-photos.md`) |
| `Theme/projection.css` (anonymous GET, ETag, revalidated on every load) | `ThemeController` → `theme/projection.css` embedded in the DLL | the server's Custom CSS (`@import url("../CustomMetadata/Theme/projection.css")`) |
| `Theme/projection.js` (anonymous GET, ETag, revalidated on every load) | `ThemeController` → `theme/projection.js` embedded in the DLL (`ThemeAssets`) | jellyfin-web's index.html, where `ThemeScriptMiddleware` (an `IStartupFilter` registered by `PluginServiceRegistrator`) inserts the script tag when `EnableThemeScript` is on |
| `Theme/projection-index.js` (same as `projection.js`) | `ThemeController` → `theme/projection-index.js` | inserted with `projection.js`; the « Index » screens (`theme-index.md`) |
| `Index` (POST, any signed-in user, over what that user sees) | `LibraryIndexController` → `LibraryIndexService` (in-memory snapshot of the library read from Jellyfin's database, read only), `LibraryIndexQuery` | the theme's « Index » screens: a page of ids, counts, filters on offer |
| `Preview/{itemId}` (any signed-in user, `api_key` in the URL; Range forwarded) | `PreviewController` → `EpisodePreviewService` (first a local episode's own preview, `LocalSourceStore.FindEpisodePreview`, read live; else the season episode list, 30 min cache) | the theme script's hover preview of a real episode: its source's `preview.url`, streamed with the source's headers |
| `Preview/{itemId}/Clip` (same access; Range) | `PreviewController` → `PreviewClipService` ({DataFolderPath}/episode-previews, built by `ScheduledTasks/PreviewClipsTask` with ffmpeg) | the theme script's hover preview when the source has no preview |
| `Preview/{itemId}/Info` (same access) | `PreviewController` → `{ Source, Clip }` | the theme script's « Aperçu » button (episode page, season rows) |
| `MissingEpisodes`, `/Analyze`, `/Ignore` | `MissingEpisodesController` → `MissingEpisodeService`, task manager | `missing.html`, `config.html` |
| `Dump/Test`, `Reload`, `Sources`, `Coverage`, `Series`, `Export`, `Image` | `DumpController` → `DumpStore`, `DumpCoverageService` | `config.html`, `missing.html` (Catalogues) |
| `People`, `Networks/*`, `EpisodeCast`, `Merge`, `Apply`, `Children`, `Library`, `Fetch`, `Photo/{file}` | `PeopleController` → `PeopleStore`, `PersonNetworkStore`, `CastOverlayStore`, `CastCatalog`, `CastApplyService` | `people.html`, `config.html` |
| `Taxonomy`, `Names`, `Impact`, `Apply`, `Apply/Status` | `TaxonomyController` → `TaxonomyStore`, `TaxonomyService` | `taxonomy.html`, `config.html` (term suggestions) |
| `Downloads`, `/Cancel`, `/Retry`, `/Clear` | `DownloadsController` → `VideoDownloadService` | `downloads.html` |

Plugin config itself goes through Jellyfin's own `Plugins/{id}/Configuration` (`ApiClient.getPluginConfiguration`/`updatePluginConfiguration`).

## 7. Pages (`Configuration/*.html`, list in `Plugin.GetPages`)

| Page name | File | Main areas (grep the function names) |
|---|---|---|
| `Custom Metadata` (menu) | `config.html` (~4.6k lines, never read whole) | styles inside the page div; one `<script>`: setup/library check (`checkLibraryProviders`, `renderSetupGuide`), list/editor views (`showTab`, `readSourceFromForm`, `persistSources`), test + click-to-fill (`renderTestResult`, `buildPickRows`, `pathFor`), in-page confirm (`ask`, `needConfirm`), targeted refresh (`pollRefresh`), dump form (`fillDumpFields`, `readDumpSettings`), people source form (`fillPersonFields`), local editor (`renderLocalTree`, `renderLocalDetail`, `galleryBlock`, `detectGalleryType`, `uploadGallery`, `importFolderImages`), episode cast (`castBlock`), post-save notice |
| `CustomMetadataMissingEpisodes` (menu) | `missing.html` | series/season cards, list view, ignore; Catalogues view of dumps (`#dumpView`, own script) |
| `CustomMetadataPeople` (menu) | `people.html` | directory, photo cropper (`openCropper`), networks tab, distribution tab |
| `CustomMetadataDiagnostic` | `diagnostic.html` | six-step replay, library check |
| `CustomMetadataTaxonomy` | `taxonomy.html` | rules table, impact, apply |
| `CustomMetadataDownloads` | `downloads.html` | download queue |

The tab bar and the in-page confirmation are copied in several pages (no shared JS file is possible for embedded pages): change every copy. UI rules: skill `.claude/skills/jellyfin-ui`, `ui-pages.md`, `config-ui.md`.

## 8. Configuration model

- Global (`PluginConfiguration`): feature flags (`EnableMetadata`, `EnableImages`, `EnableCasting`), cache, `DefaultSourceName`, `MaxParallelDownloads`, `Sources`, and the legacy single-source fields kept for XML compatibility.
- Per source (`SourceConfiguration`): `SourceType` (`Api` default / `Local` / `People` / `Dump`, helpers `IsLocal`, `IsPeople`, `IsDump`), prefix, base URL, key, endpoints, `FieldMapping` ×3, `EpisodeListMapping`, `CustomHeaders` (key/value list, not a dictionary), HTTP options (`TimeoutSeconds`, `MaxRetries`, `MaxConcurrentRequests`, `SendOriginReferer` + overrides), image options (`LandscapePortraitThumbs`, `LandscapePosterCrop`), `AutoDownloadVideos`, `DumpSettings`, person mappings, `LocalDataId`.
- Adding a mapping field touches C#, the mapper, the page and the tests: follow skill `add-mapping-field`.

## 9. Change recipes

| To… | Touch |
|---|---|
| add a service | class in `Services/` + registration in `PluginServiceRegistrator` + §2.2 here |
| add an endpoint | controller in `Api/` (route `CustomMetadata/…`, `[Authorize(Policy = "RequiresElevation")]`) + §6 here + the page that calls it |
| add a page | `Configuration/x.html` (styles inside `data-role="page"`), entry in `Plugin.GetPages`, tab bar copy in every page, §7 here |
| store a new file | a store class with atomic write; §5 here |
| change how a tag is found | `TagParser` only (single regex), then the per-provider order of §3.1; D003 |
| change numbering of virtual episodes | `MissingEpisodeService` (`FillMissingIndexNumbers`, `AssignIndexNumbers`, `ComputeIndexNumbers`, `EnforceNumbersAsync`) + `CustomEpisodeProvider` (`TryGetAssignedNumber`); D009; test `missing-number-real.mjs` |
| verify a change | skill `verify-change`; bench `tools/ui-test/ui-test.sh` (`--fast` for the inner loop) |

## 10. Test scripts → area

| Area | Scripts (`tools/ui-test/`) |
|---|---|
| Config page | `config-e2e.mjs`, `config-local-ui.mjs` (no Docker), `config-local-real.mjs`, `config-menu-real.mjs`, `local-series-id-real.mjs`, `note-shots.mjs` |
| Local sources and images | `local-override-real.mjs` (incl. targeted refresh, CORS), `local-gallery-real.mjs`, `local-image-orientation-real.mjs`, `local-image-cleanup-real.mjs`, `local-gallery-shots.mjs`, `local-folder-images-real.mjs`, `refresh-persist-real.mjs` (D006), `poster-crop-real.mjs`, `poster-crop-local-real.mjs`, `poster-shots.mjs` |
| Missing episodes | `missing-number-real.mjs` (D009), `episode-number-real.mjs`, `download-real.mjs`, `download-parallel-real.mjs`, `downloads-real.mjs`, `local-preview-video-real.mjs`, `chapters-real.mjs` |
| People and cast | `config-people-real.mjs` (`--quick`: cropper), `config-people-episodes-real.mjs` |
| Dump | `dump-real.mjs` |
| Taxonomy | `taxonomy-real.mjs`, `taxonomy-shots.mjs` |
| Shared UI | `diagnostic-real.mjs`, `improvements-real.mjs`, `shoot.mjs` |
| Harness | `ui-test.sh`, `server.py`, `data.py`, `gen.py`, `chrome-session.mjs` (lock + Chrome cleanup), `quiet.mjs` |

## 11. Known drift and debt (audit of 2026-11-24)

- The movie provider keeps the legacy path and a case-sensitive `TryGetValue("custom")` (left on purpose, D004).
- Five near-identical tag wrappers across the providers (§3.1): behaviour differs only by the lookup order.
- Fixed since the audit: French comments in the code, the csproj and `data.py`; `CustomExternalId`'s summary; the series/season cast contradiction in `people-cast.md`.
