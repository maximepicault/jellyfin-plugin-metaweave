# HTTP, headers and cache

> Detail moved out of `CLAUDE.md` to keep the always-loaded file small. Read it when touching `ResilientHttp`, `SourceHeaders`, CORS handling, the source test, `MetadataCacheService`. `CLAUDE.md` still holds the rules that apply everywhere.

**HTTP.** Every API GET (metadata, images, episode list, source test) goes through `Api/ResilientHttp.GetStringAsync`, which never throws for HTTP/network/timeout errors and returns an `HttpFetchResult` instead. Only caller cancellation propagates.
- Each attempt has its own timeout (`SourceConfiguration.TimeoutSeconds`, default 30). `HttpClient.Timeout` is set to infinite because it would otherwise cap attempts at 100s.
- Up to `MaxRetries` retries (default 2) on timeout, network error, 408, 429 and 5xx. Backoff is 1s, 2s, 4s… (max 30s), or `Retry-After` when the API sends it. A `Retry-After` over 60s means give up. Other 4xx codes are not retried. Retries log `🔁` at Warning.
- `MaxConcurrentRequests` (default 4, 0 = unlimited) is enforced by a static semaphore keyed by `prefix|limit`, shared by every caller. The slot is released during backoff.
- These three settings are in the "⚙️ Réseau" section of the source modal in `config.html`.
- `Api/SourceTestController` (`POST CustomMetadata/TestSource`, admin-only) backs the modal's "🧪 Tester la source" button. It takes the source **as currently in the form** (unsaved), an `ItemType` (`video`/`season`/`series`/`episodelist`) and an id (`123`, `pfx-123` or `{pfx-123}`). It calls `CustomApiClient.TestSourceAsync` without cache and returns the URL, status, attempts, duration, mapped fields, images or episodes, and the raw JSON (kept up to 3 MB (`SourceTestResult.MaxRawLength`, `MaxEpisodes` 1000; the screen scrolls inside its boxes)). Log tag: `🧪 [SOURCE TEST]`.

Headers are set in one place, `Api/SourceHeaders.Apply`. It is used by `CustomApiClient.AddHeadersToClient` (Info logs, sends `X-API-Key`) and by the image downloads in `CustomImageProvider.GetImageResponse` (Debug logs, no key). Per-source options, all in "⚙️ Réseau":
- `SendBrowserUserAgent` (default true): a fake Chrome 120 UA. When off, the UA is `Jellyfin.Plugin.CustomMetadata/1.0`.
- `SendOriginReferer` (default true): `Origin` = `BaseUrl` authority, port kept; `Referer` = `BaseUrl`. `OriginOverride`/`RefererOverride` replace those derived values when non-empty. They only apply while `SendOriginReferer` is on, and the inputs are disabled in the UI otherwise. An invalid override is skipped with a Warning.
- **CORS 403** (`SourceHeaders.LooksLikeCorsRejection`/`FlipOrigin`/`RememberOrigin`, used by `ResilientHttp`, `CustomApiClient.DownloadImageAsync` and `CustomImageProvider.GetImageResponse`): one retry with Origin/Referer flipped, the working choice remembered in memory per source and current setting (`EffectiveSendOrigin`). Not done when the source's custom headers set Origin/Referer themselves. Mock routes `/series/corsA|B|C` and `tools/ui-test/local-override-real.mjs` cover it.
- `UseHttp2` (default false): `DefaultRequestVersion` 2.0 with `RequestVersionOrLower`. .NET then emits the `:method`/`:scheme`/`:authority`/`:path` pseudo-headers itself. They can't be added as headers.

A custom header has `CustomHeader.Enabled` (default true, so older configs keep working): a switched-off one is skipped by `SourceHeaders.Apply`, ignored by the CORS flip check and not counted in the Réseau counter. Values are added with `TryAddWithoutValidation` (quotes survive, `Add()` refused `max-age="10"`). Then come `Accept`, `X-API-Key` (the source key, falling back to the legacy global `ApiKey`), and finally the source's `CustomHeaders`, which override the defaults. A fake browser UA (WAF bot check) or an `Origin` the backend does not allow (Spring CORS) can produce a 403 that curl doesn't get. The test button shows the headers sent, the HTTP version and the start of the error body to diagnose this. URLs containing `votre-api.com` or `example.com` are treated as unconfigured placeholders and skipped.

**Cache.** `MetadataCacheService` is a two-tier cache: `IMemoryCache` plus JSON files in `Plugin.Instance.DataFolderPath/cache`. The directory path is logged at startup as `💾 [CACHE] Cache directory:`; if `Plugin.Instance` is null, the cache falls back to a temp dir. TTL comes from `CacheTTLHours`, the whole cache is gated by `EnableCache`, and null API results are not cached.
- **Manual refreshes bypass the cache.** The series, season and episode providers pass `bypassCache: !info.IsAutomated` to `GetMetadataAsync`. On a user-initiated refresh it invalidates both `metadata:{type}:…` and `images:…:{type}` before fetching. Jellyfin runs remote image providers after metadata providers, so "Remplacer les images existantes" really picks up a changed poster.
- Automated library scans still use the cache (verified: 0 API calls).
- Without this, a changed poster or title stayed stale until the TTL expired, even with "Replace".
- Movies (legacy path) don't do this.
- An old `poster.png` sitting in a media folder that Jellyfin can't write still wins on every scan. That is Jellyfin's local-image priority, and the plugin can't override it.
- Keys are `metadata:{type}:{prefix}-{id}` and `images:{prefix}-{id}:{type}`. `CustomApiClient.GetCacheId()` re-adds the source prefix, because providers pass the ID already stripped of it.
- `MetadataCacheService.GetCacheKey()` turns keys into readable file names by splitting at the first `-` (`MV1-SEASON-SOURCE-YT.json`). The same derived key is used for the memory cache.
- Without the prefix in the key, two sources returning the same raw ID would share an entry.
- Any note that describes MD5-hashed cache file names is obsolete (the old cache notes are archived outside the repo).
