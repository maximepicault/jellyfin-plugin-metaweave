# The theme's "Index" (filtered lists)

`theme/projection-index.js` rebuilds jellyfin-web's genre, tag, studio and person lists (`#/list`), and adds a « Tous les filtres » link to library toolbars. Styles: section 18 of `theme/projection.css`. Test: `tools/ui-test/index-real.mjs`.

## Why

On the stock list (10.11.6, checked on the test bench): the experimental layout hides the page title, so nothing says which genre, tag or person is shown; a genre link asks for `Movie,Series,Video` only, so episodes never appear; a tag list mixes series, seasons and episodes in one grid.

## Activation

- Served as `CustomMetadata/Theme/projection-index.js`, inserted after `projection.js` by `ThemeScriptMiddleware` (setting `EnableThemeScript`).
- Not in the TV layout (`layout-tv`).
- Does nothing unless the stylesheet defines `--pj-index: 1` (section 18): an older theme copy (jsDelivr `@main`) leaves stock lists.
- `route()` decides: `genreId`, `studioId`, `personId`, `type=tag&tag=`, or `pj=index` (library link). Another `type` (music, programmes), `IsFavorite`, or an arrival with no video item keeps jellyfin-web's list.

## How it works

- The script appends `.pj-ix` inside jellyfin-web's list `.page` and hides the stock children (`pj-ix-on`). It builds only once `viewshow` fired for the current address (`__pjView`), not on the page being left.
- **Data**: `ApiClient.getItems`, recursive, `Series,Season,Episode,Movie,Video,MusicVideo`, pages of 1000. Only one chosen genre / tag / studio / person (the **anchor**) is sent to the server; the others are applied in the browser. Removing the anchor asks again with another one, or for everything. People come in a second pass (`Fields=People`), unless a chosen person is not the anchor.
- **Filtering**: genres, tags, studios and people narrow each other (all of them); years and playback states widen within their own kind. Counts are "results if chosen", cached until something changes (`ix.memo`).
- **Missing episodes** show only if the user displays them (`DisplayMissingEpisodes`).
- **Seasons order**: number, else the number in the season's name, else first release date; specials last (Jellyfin often leaves episodes without a season number).
- **Memory**: filters, type and order are kept per history entry (`history.state.key`), so "back" restores them. Nothing is written to the address or to storage.
- **Actions** go through jellyfin-web: cards sit in an `emby-itemscontainer`, so `itemAction` buttons (`resume`, `menu`) and right click work; « Tout lire » / « Aléatoire » click a hidden `playallfromhere` queue (first 200 playable items). The cards carry `.pj-ix-item` / `.pj-ix-frame`, which `projection.js`'s hover preview accepts.
- **Panel** (« Tous les filtres » or `/`): appended to `body`; it takes the keyboard at window capture (as the preview viewer does), so Enter and Escape are handled there.
- Library names, tags and people are only ever written as text nodes, never as HTML.

## Depends on jellyfin-web

`.alphaPicker` (marks the list page), `viewshow`, `itemShortcuts` data attributes, `.btnFilter` / `.MuiButtonGroup-root` + `FilterAltIcon` (library toolbar), `.MuiAppBar-root` / `.skinHeader` (ribbon offset). Re-run `index-real.mjs` after a server upgrade.
