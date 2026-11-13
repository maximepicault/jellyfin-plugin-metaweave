> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# 🖼️ Images configuration guide

## 📍 Overview

The plugin reads images from your API's JSON. The images are in **the same JSON response** as the metadata, not a separate endpoint: the image provider calls the same endpoint again (under its own cache key) and extracts the images with the image fields of the same mapping.

Each of the three mappings (video / episode, source / season, series) has **its own** image fields: see [IMAGE_MAPPING.md](IMAGE_MAPPING.md).

---

## ⚙️ Where to configure it

**Administration menu → Extensions → Custom Metadata → edit the source.** Each mapping section has its own image fields.

---

## 🎬 JSON structure of your API

### Option 1: direct URLs (recommended)

**Example for an episode** (`GET /videos/EP001`):

```json
{
  "title": "Episode 1",
  "overview": "Description",
  "poster": "https://your-api.example.com/images/ep1-poster.jpg",
  "thumb": "https://your-api.example.com/images/ep1-thumb.jpg",
  "backdrop": "https://your-api.example.com/images/ep1-backdrop.jpg"
}
```

**Configuration:** `PosterUrl = poster`, `ThumbUrl = thumb`, `BackdropUrl = backdrop`.

### Option 2: an array of images

```json
{
  "title": "Episode 1",
  "images": [
    {"url": "https://your-api.example.com/poster.jpg", "type": "Primary", "width": 1000, "height": 1500},
    {"url": "https://your-api.example.com/backdrop.jpg", "type": "Backdrop", "width": 1920, "height": 1080}
  ]
}
```

**Configuration:** `ImagesArray = images`, `ImageUrl = url`, `ImageType = type`, `ImageWidth = width`, `ImageHeight = height`.

Both approaches can be combined: the direct URLs are read first, then the array.

---

## 🎯 Supported image types

| For | Types |
|---|---|
| Videos / episodes | Poster, Backdrop, **Thumb** (the episode thumbnail: very important), Logo |
| Sources / seasons | Poster, Backdrop |
| Series | Poster, Backdrop, Logo, Banner |

An episode that has a Thumb but no Primary image also gets its Thumb offered as Primary (jellyfin-web shows Primary in episode lists).

---

## 📝 Worked examples

**A simple structure per type** — video `{"title": "My Episode", "poster": "https://cdn.example.com/posters/ep123.jpg", "thumb": "https://cdn.example.com/thumbs/ep123.jpg"}` with `PosterUrl = poster`, `ThumbUrl = thumb`.

**Dot notation** — `{"metadata": {"images": {"poster_url": "https://...", "backdrop_url": "https://..."}}}` with `PosterUrl = metadata.images.poster_url`, `BackdropUrl = metadata.images.backdrop_url`.

**A source with avatar and banner** — `{"name": "My channel", "channel_avatar": "https://...", "channel_banner": "https://..."}` with `PosterUrl = channel_avatar`, `BackdropUrl = channel_banner`.

**A series with several images** — `{"artwork": {"poster": "...", "fanart": "...", "logo": "...", "banner": "..."}}` with `PosterUrl = artwork.poster`, `BackdropUrl = artwork.fanart`, `LogoUrl = artwork.logo`, `BannerUrl = artwork.banner`.

---

## 🧪 Testing

1. Configure the paths, then use the source's **test button** with an item identifier: it lists the images found.
2. Check the JSON your API returns really contains the image URLs: `curl https://your-api.example.com/videos/123 | jq`.
3. Run a library scan and look at the logs:
   ```bash
   sudo journalctl -u jellyfin -f | grep -i "image"
   ```

---

## 🔍 Troubleshooting

- **No image appears.** Check that "Récupérer les images" (fetch images) is on, that the paths match your API, and that the URLs are reachable by the Jellyfin server.
- **Some images are missing** (posters fine, no thumbs): configure the **Thumb** field in the video mapping.
- **An image is refused (HTTP 403/404).** Jellyfin silently drops such responses and keeps the old image; the plugin logs them as warnings (`[IMAGE DOWNLOAD] ⚠️ HTTP 403 …`). See the network settings of the source (User-Agent, `Origin`, `Referer`).
- **`Error mapping images from JSON`** in the logs: your JSON paths are wrong (dot notation).

---

## 💡 Good practices

- **Full URLs only**: ❌ `/images/poster.jpg` ✅ `https://cdn.example.com/images/poster.jpg` (a relative address is completed with the base URL only for the missing-episodes list).
- **Thumbs for every episode** make a big difference in the episode lists.
- **Recommended sizes**: poster 1000×1500 (2:3), backdrop 1920×1080 (16:9), thumb 640×360 or 1280×720 (16:9), logo transparent PNG (max 800 px wide), banner 1000×185.
- **Formats**: JPEG for posters, backdrops and thumbs, PNG for logos; keep images under about 500 KB.

---

## ❓ FAQ

**My API returns an array of images: how do I configure it?** Set `ImagesArray` to the array's path (for example `images`), then `ImageUrl`, `ImageType`…

**Where are the images stored?** Jellyfin downloads them and keeps them in its metadata folder.

**Can I configure only some image types?** Yes: an empty field is ignored.

**Are images cached?** Yes, like the metadata (duration set by `CacheTTLHours`); a manual refresh ignores the cache.
