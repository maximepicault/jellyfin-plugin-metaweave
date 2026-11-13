> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# 🖼️ Image mapping: one set per item type

## The pitfall

A source has **three** mappings, and **each has its own image fields**:

| Mapping | Used for | Config name |
|---|---|---|
| Series | series | `SeriesFieldMappings` |
| Source / season | seasons | `SourceFieldMappings` |
| Video / episode | episodes and movies | `VideoFieldMappings` |

If you configure the image fields only in the series mapping, **series have images but seasons and episodes do not**: the other two mappings keep their default paths (`poster`, `backdrop`, `images`…), which probably do not match your JSON.

## The fix

Configure the image fields (`PosterUrl`, `BackdropUrl`, `ThumbUrl`, `LogoUrl`, `BannerUrl`, or `ImagesArray` + `ImageUrl` + `ImageType`) in **each of the three sections** of the source's editor, using the path that matches the JSON of that type.

### Same JSON structure for every type

If your API returns the same structure for series, seasons and episodes (`{"data": {"name": "...", "screenshot": "https://..."}}`), use the same paths in the three sections (`PosterUrl = data.screenshot`, …).

### Different structures per type

| Type | JSON | Path |
|---|---|---|
| Series | `{"data": {"screenshot": "https://..."}}` | `PosterUrl = data.screenshot` |
| Season | `{"thumbnail": "https://..."}` | `PosterUrl = thumbnail` |
| Episode | `{"poster": "https://..."}` | `PosterUrl = poster` |

If you edit the configuration file by hand, the same fields go inside `<SeriesFieldMappings>`, `<SourceFieldMappings>` and `<VideoFieldMappings>` of the source (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml`); restart Jellyfin afterwards.

## Checking it

The source's **test button** (type "video", "season" or "series") shows the images found for an item. In the logs:

```bash
sudo journalctl -u jellyfin -f | grep "IMAGE FETCH"
```

You should see which mapping is used and its `PosterUrl`:

```
🖼️ [IMAGE FETCH] Using SOURCE/SEASON mapping
🖼️ [IMAGE FETCH]   SourceFieldMappings.PosterUrl: 'data.screenshot'
✅ [IMAGE MAPPER] Mapping completed: 1 images found
```

If you see the default (`'poster'`) instead of your path, that mapping is not configured. A new source created in the editor starts with the standard paths pre-filled; remember that an **empty path is ignored**, not defaulted.

### Checklist (for each of series, season, episode)

- [ ] The logs show which mapping is used
- [ ] Its `PosterUrl` matches the path in my JSON
- [ ] At least one image is found
