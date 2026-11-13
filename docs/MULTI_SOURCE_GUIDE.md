> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# 🌐 Multi-source guide

## 🎯 Goal

Use several metadata sources with different APIs and JSON structures.

**Examples:** `mv1` your main API, `yt` a YouTube-like API, `tw` a Twitch-like API.

Each source has its own `BaseUrl`, endpoints, JSON mappings and API key.

> The easiest way to configure sources is the plugin's screen (Administration → Extensions → Custom Metadata). The XML below is what the screen stores; it is shown for reference or for editing by hand (restart Jellyfin afterwards). Element names are the C# names: `<Sources><SourceConfiguration>…`.

---

## 📋 File naming

```
Source 1: My Episode {mv1-123}.mkv
Source 2: My Video {yt-456}.mp4
Source 3: Stream {tw-789}.webm
                    prefix ↑   ↑ ID
```

The plugin picks the source from the prefix. The prefix is read up to the **first** hyphen, so it cannot itself contain one.

---

## ⚙️ Configuration file

`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <EnableMetadata>true</EnableMetadata>
  <EnableImages>true</EnableImages>
  <EnableCache>true</EnableCache>
  <CacheTTLHours>24</CacheTTLHours>
  <Sources>
    <SourceConfiguration>
      <Name>Main API</Name>
      <Prefix>mv1</Prefix>
      <BaseUrl>https://api.example.com</BaseUrl>
      <ApiKey>your-api-key-here</ApiKey>
      <Enabled>true</Enabled>
      <VideoEndpoint>/videos/{id}</VideoEndpoint>
      <SourceEndpoint>/sources/{id}</SourceEndpoint>
      <SeriesEndpoint>/series/{id}</SeriesEndpoint>
      <VideoFieldMappings>
        <Title>data.title</Title>
        <Overview>data.description</Overview>
        <PremiereDate>data.releaseDate</PremiereDate>
        <PosterUrl>data.screenshot</PosterUrl>
        <BackdropUrl>data.backdrop</BackdropUrl>
      </VideoFieldMappings>
      <SourceFieldMappings>
        <Title>data.name</Title>
        <Overview>data.description</Overview>
        <PosterUrl>data.screenshot</PosterUrl>
      </SourceFieldMappings>
      <SeriesFieldMappings>
        <Title>data.title</Title>
        <Overview>data.overview</Overview>
        <PosterUrl>data.screenshot</PosterUrl>
      </SeriesFieldMappings>
    </SourceConfiguration>
    <SourceConfiguration>
      <Name>YouTube API</Name>
      <Prefix>yt</Prefix>
      <BaseUrl>https://youtube-api.example.com</BaseUrl>
      <Enabled>true</Enabled>
      <VideoEndpoint>/video/{id}</VideoEndpoint>
      <SourceEndpoint>/channel/{id}</SourceEndpoint>
      <SeriesEndpoint>/channel/{id}</SeriesEndpoint>
      <VideoFieldMappings>
        <Title>title</Title>
        <Overview>description</Overview>
        <PremiereDate>publishedAt</PremiereDate>
        <PosterUrl>thumbnail</PosterUrl>
        <BackdropUrl>banner</BackdropUrl>
      </VideoFieldMappings>
      <SourceFieldMappings>
        <Title>channelName</Title>
        <Overview>channelDescription</Overview>
        <PosterUrl>channelIcon</PosterUrl>
      </SourceFieldMappings>
      <SeriesFieldMappings>
        <Title>channelName</Title>
        <Overview>channelDescription</Overview>
        <PosterUrl>channelIcon</PosterUrl>
      </SeriesFieldMappings>
    </SourceConfiguration>
    <SourceConfiguration>
      <Name>Twitch API</Name>
      <Prefix>tw</Prefix>
      <BaseUrl>https://twitch-api.example.com</BaseUrl>
      <ApiKey>twitch-key</ApiKey>
      <Enabled>true</Enabled>
      <VideoEndpoint>/videos/{id}</VideoEndpoint>
      <SourceEndpoint>/users/{id}</SourceEndpoint>
      <SeriesEndpoint>/users/{id}</SeriesEndpoint>
      <VideoFieldMappings>
        <Title>title</Title>
        <Overview>description</Overview>
        <PremiereDate>created_at</PremiereDate>
        <PosterUrl>thumbnail_url</PosterUrl>
      </VideoFieldMappings>
      <SourceFieldMappings>
        <Title>display_name</Title>
        <Overview>description</Overview>
        <PosterUrl>profile_image_url</PosterUrl>
      </SourceFieldMappings>
      <SeriesFieldMappings>
        <Title>display_name</Title>
        <Overview>description</Overview>
        <PosterUrl>profile_image_url</PosterUrl>
      </SeriesFieldMappings>
    </SourceConfiguration>
  </Sources>
</PluginConfiguration>
```

---

## 🏗️ Folder layout

```
/mnt/media/
├── API_Series/
│   └── My Series {mv1-1004855103}/            # source 1
│       ├── Source A {mv1-SOURCE-A}/
│       │   └── Episode 1 {mv1-6044108}.mkv
│       └── Source B {mv1-SOURCE-B}/
│           └── Episode 2 {mv1-6044109}.mkv
├── YouTube_Videos/
│   └── Tech Channel {yt-UC123456}/            # source 2
│       └── Playlist 1 {yt-PL789}/
│           └── Tutorial 1 {yt-dQw4w9WgXcQ}.mp4
└── Twitch_Streams/
    └── Streamer A {tw-streamer123}/           # source 3
        └── Archives {tw-ARCHIVE}/
            └── Stream 2024-01-15 {tw-98765}.mkv
```

---

## 🔍 How it works

1. **Source detection.** `Episode 1 {mv1-6044108}.mkv` → pattern `{mv1-6044108}` → prefix `mv1` → the enabled source with that prefix (case-insensitive) → its `BaseUrl`, endpoints and mappings. No matching source: the item is left to other providers.
2. **Per-source cache.** Files are named from the source prefix, so two sources returning the same raw ID never collide:
   ```
   cache/
   ├── MV1-SERIES-1004855103.json
   ├── MV1-EPISODE-6044108.json
   ├── YT-EPISODE-dQw4w9WgXcQ.json
   └── IMAGES-YT-EPISODE-dQw4w9WgXcQ.json
   ```
3. **Per-source API calls.** Source 1: `https://api.example.com/videos/6044108` with `X-API-Key`, mapping `data.title`…; source 2: `https://youtube-api.example.com/video/dQw4w9WgXcQ` without key, mapping `title`…

---

## 🧪 Step-by-step test

1. Configure two sources (screen or XML), then restart Jellyfin if you edited the XML: `sudo systemctl restart jellyfin`.
2. Use each source's **test button** with a real identifier.
3. Check the logs:
   ```bash
   sudo journalctl -u jellyfin -f | grep "SOURCE RESOLVER"
   ```
   Expected lines such as `🔍 [SOURCE RESOLVER] ✅ Found source 'Main API' for prefix 'mv1'`.
4. Scan one file of each source, then look at the API calls and the cache:
   ```bash
   sudo journalctl -u jellyfin -f | grep "API CLIENT"
   ls -la /var/lib/jellyfin/plugins/Jellyfin.Plugin.CustomMetadata/cache/
   ```

---

## ⚠️ Backward compatibility

If **no source** is configured (`<Sources></Sources>` or absent), the plugin uses the legacy single-source settings (`PatternPrefix`, `ApiBaseUrl`, `VideoEndpoint`, `VideoFieldMappings`…). Files already named `{custom-123}` keep working.

---

## 🎯 Use cases

- **One API, several environments**: prefixes `prod` and `stg` with different base URLs → `Episode {prod-123}.mkv`, `Episode {stg-123}.mkv`.
- **Several external APIs**: `yt`, `vim`, `dm`, each with its own mapping.
- **Custom API + public APIs**: `mine` (with API key) and `ext`.

---

## 🚨 Troubleshooting

- **"No source found for prefix"**: the prefix is not configured, or the source is disabled. Check the available prefixes listed in the log.
- **Wrong source used**: two sources share a prefix. Each prefix must be unique.
- **Mixed-up cache** (`MV1-SERIES-123.json` holds another source's data): the prefix was changed without clearing the cache.
  ```bash
  sudo rm -rf /var/lib/jellyfin/plugins/Jellyfin.Plugin.CustomMetadata/cache/*
  sudo systemctl restart jellyfin
  ```
  (A manual refresh in Jellyfin also bypasses the cache.)

---

## 📋 Checklist

- [ ] Every source has a **unique** prefix (no hyphen) and is enabled
- [ ] Base URL and endpoints are correct for each source
- [ ] Mappings match each API's JSON structure (images too: see [IMAGE_MAPPING.md](IMAGE_MAPPING.md))
- [ ] Files are named `Name {prefix-id}.ext`
- [ ] Logs show the right source detected and the metadata and images load
