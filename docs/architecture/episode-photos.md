# Episode photo gallery

> Read it when touching the photo folders, the local « Photo » type, `web/gallery.js` or the photos in the image search. Ported from the separate « Episode Gallery » plugin.

- **Setting** `EnableEpisodePhotos` (off by default). It gates everything: lists are empty (`EpisodePhotoService.List`), `ThemeScriptMiddleware` adds no script, the image search offers nothing, the ignore rule skips photo folders.
- **Sources of photos** (`Services/EpisodePhotoService`), in this order: (1) `Gallery` entries of type `Photo` (`LocalSourceStore.PhotoType`) of the local episode(s) found by `FindOverride` for every tag of the file name then the item's custom id (so a missing episode has them too); (2) the numbered folder next to the video (`Services/EpisodePhotoFolders`: `IndexNumber`/`IndexNumberEnd` of Jellyfin, ranges, natural order, symlinks must stay inside the folder).
- **A « Photo » is never an image to apply**: `LocalSourceStore.NodeJson` leaves it out of the served `images`; the editor offers the type on episodes only, with no default badge or button.
- **Keys**: a folder photo's key is its file name; a local one starts with `.local-` (hidden files are skipped, so no clash). A client-supplied key is only compared with the list, never joined to a path.
- **Access** (`Api/PhotosController`): the item must be visible to the user (`GetItemById<T>(id, user)`; an empty or unknown user is refused first, because the library manager skips the check when the user is null). 404 otherwise.
- **Image search** (`Providers/EpisodePhotoImageProvider`, only for `…/RemoteImages` requests): the dialog and Jellyfin's download send no token, so file photos get HMAC links valid 1 h (`EpisodePhotoLinks`, key per process). `EpisodePhotoDownloadStartupFilter` puts the chosen photo in Jellyfin's memory cache (key = URL) so the server need not reach the browser's host. EXIF rotation is baked in there (`ExifOrientation`).
- **Thumbnails** (`EpisodePhotoThumbnails`): WebP, widths in steps of 80, cache key = path, size, date.
- **Ignore rule** (`ImageFolderIgnoreRule`): a numbered folder with at least one photo, no video and no sub-folder, below a series or season. Without it Jellyfin made a season of such a folder in a series folder, and a folder item inside a season.
- **Web** (`web/gallery.js`): independent of the theme script (it uses `--pj-*` colours when present). Button in `.mainDetailButtons` after « Lire » / « Aperçu », wall in justified rows, viewer. Images are fetched with the token in a header and shown from blob URLs. `eg-` prefix.
- **Test**: `tools/ui-test/photos-real.mjs <url> <out_dir>` (container 8097; writes in the mounted media folder and restores); editor checks in `config-local-ui.mjs`.
- Not done: uploads stay limited to 8 MB per image (local source); no purge of old thumbnails; photos of seasons or series.
