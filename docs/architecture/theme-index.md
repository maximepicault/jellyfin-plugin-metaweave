# The theme's "Index" (filtered lists and library screens)

`theme/projection-index.js` rebuilds jellyfin-web's genre, tag, studio and person lists (`#/list`) and the Series, Episodes and Films views of a library (`#/tv`, `#/movies`). The plugin does the searching: `CustomMetadata/Index` (`Api/LibraryIndexController.cs`, `Services/LibraryIndexService.cs`, `Services/LibraryIndexQuery.cs`). Styles: section 18 of `theme/projection.css`. Tests: `tools/ui-test/index-real.mjs`; timing: `big-library.sh` then `index-perf.mjs`.

## Why

On the stock list (10.11.6, checked on the test bench): the experimental layout hides the page title, so nothing says which genre, tag or person is shown; a genre link asks for `Movie,Series,Video` only, so episodes never appear; a tag list mixes series, seasons and episodes in one grid.

Why a server search: the screens need counts for every filter, genres and tags that narrow each other, and episodes grouped by series. Jellyfin's `/Items` gives none of the counts and matches *any* of several tags. Reading the whole library in the browser took about 56 s with the cast for 12 600 items (6 s without) on the test bench.

## Activation

- Served as `CustomMetadata/Theme/projection-index.js`, inserted after `projection.js` by `ThemeScriptMiddleware` (setting `EnableThemeScript`).
- Does nothing unless the stylesheet defines `--pj-index: 1` (section 18): an older theme copy (jsDelivr `@main`) leaves stock screens.
- Not in the TV layout (`layout-tv`).
- Lists: `listRoute()` takes `genreId`, `studioId`, `personId`, `type=tag&tag=`. Another `type` (music, programmes), `IsFavorite`, or an arrival with no video item keeps jellyfin-web's list.
- Libraries: `libraryTarget()`. Experimental layout: the view comes from `tab=` in the address (0 = series or films, 5 = episodes); without it, from what the stock page shows. Classic layout: the open `.pageTabContent` (`seriesTab`, `episodesTab`, `moviesTab`), each with its own screen and memory. The other views stay stock. The screen hides jellyfin-web 12's second app bar row (its view menu; the page moves up by itself) and offers the same views from its title, or from the line above it once filtered (`VIEWS`, `viewMenu`).

## The server search

`POST CustomMetadata/Index`, any signed-in user (400 for an API key: no user). Body (`IndexRequest`): `ParentId`, `Filters` (genres, tags, studios, people, years, state, letter), `Type`, `Sort`, `Seed`, then what is wanted: `StartIndex`/`Limit` (a page of ids with their section), `Summary` (counts per type, suggestions, letters), `Facets` + `FacetQuery`/`FacetLimit`, `Play` (`all`/`shuffle`, at most 200 ids).

- **Snapshot**: every video item (genres, tags, studios, cast, season order, backdrop) read from Jellyfin's database in three queries through `IDbContextFactory<JellyfinDbContext>`, kept in memory (about 0.8 s for 12 960 items). **Read only.** Built again after library events, at most every 20 s, and at least every 10 minutes: an edit can take up to 20 s to show in counts. The entity names come from Jellyfin 10.11 (D001): check them on a server upgrade.
- **Rights**: the ids a user may see come from `ILibraryManager.GetItemIds` with that user (kept 60 s). A library (`CollectionFolder`) is narrowed by its folders (`TopParentId`); a view grouping several libraries answers 404 and keeps the stock screen. Missing episodes show only with `DisplayMissingEpisodes`. Watched states are read at each request.
- **Filtering**: genres, tags, studios and people narrow each other (all of them); years, playback states and the letter widen within their own kind. Counts are "results if chosen". A series or season is "seen" when all its episodes with a file are.
- **Seasons order**: number, else the number in the season's name, else first release date; specials last (Jellyfin often leaves episodes without a season number).
- The answer holds ids only: the browser reads the page's items from `/Items?Ids=` (so their images, user data and rights are Jellyfin's own).

## The screen

- The script appends `.pj-ix` inside jellyfin-web's page or tab and hides its other children (`pj-ix-on`). A list or classic page is built once `viewshow` fired for the current address (`__pjView`); the experimental library page changes view without that event.
- **Pages** of 60: the next one when the end comes near (`IntersectionObserver`), or up to 600 at once when "back" restores a screen. The current results stay, dimmed, while a new search runs (`pj-ix-busy`).
- **Memory**: filters, type, order and number of results are kept per history entry (`history.state.key`, plus the view of a library), so "back" restores them. Nothing is written to the address or to storage.
- **Actions** go through jellyfin-web: cards sit in an `emby-itemscontainer`, so `itemAction` buttons (`resume`, `menu`) and right click work; « Tout lire » / « Aléatoire » click a hidden `playallfromhere` queue built from the server's ids. The cards carry `.pj-ix-item` / `.pj-ix-frame`, which `projection.js`'s hover preview accepts.
- **Panel** (« Tous les filtres » or `/`): appended to `body`; it takes the keyboard at window capture (as the preview viewer does), so Enter and Escape are handled there. People come 240 at a time (600 at most), then by search.
- Library names, tags and people are only ever written as text nodes, never as HTML.
- **Header**: the detail pages' « Affiche » (section 8 of the CSS) around a picture of the results: whole (`contain`, `--pj-ix-ph` high, below the app bar), the picture's ambient light on both sides (`.pj-aff-fill`), the title on the veiled lower left. `projection.js` exposes its colour reading as `window.__projectionColours`; `paint()` keeps the result and `followBar()` gives the page that ground (`html.pj-ix-art-on`, variables `--pj-ix-bg` / `--pj-ix-veil`, so a detail page's own colours are never touched). Without `projection.js` the screen keeps the theme's ink. While a screen shows (`html.pj-ix-shown`), jellyfin-web's own backdrop (« Backdrops » display setting) is hidden.

## Jellyfin 12

The plugin is built for 12.4 only (D026). Check these screens with `ui-test.sh`, the theme script setting turned on, `theme-seed.mjs`, then `index-real.mjs`.

- `BaseItemEntity.PrimaryVersionId` changed type between 10.11 and 12: it is read with `EF.Property<object>`, which does not bind to the type (a build made for the other version threw `MissingMethodException` on a typed access, at run time only).
- jellyfin-web 12 has one layout (the « classic » setting too), draws the library toolbar in a second row of the app bar (found by its view menu `[aria-controls="library-view-menu"]`, hidden under `html.pj-ix-libon`), starts pages below the bar (`--pj-ix-covered`) and makes every `.itemsContainer` a flex box.

## Depends on jellyfin-web

`.alphaPicker` (marks the list page), `viewshow`, `itemShortcuts` data attributes, `#tvshowsPage` / `#moviesPage` and `.MuiButtonGroup-root` (experimental library), the tab ids (classic), `.MuiAppBar-root` / `.skinHeader` (ribbon offset). The stock page under the screen still loads its own first items. Re-run `index-real.mjs` after a server upgrade.
