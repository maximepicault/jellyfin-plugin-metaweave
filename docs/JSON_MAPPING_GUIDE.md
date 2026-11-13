> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# Guide: JSON field mapping

The plugin maps **any JSON structure** onto Jellyfin's fields through a flexible configuration.

## 🎯 Concept

Instead of requiring a fixed JSON structure, you configure how to extract each field from **your** format.

### Without mapping (fixed structure)

Your API **MUST** return exactly:
```json
{
  "title": "My title",
  "overview": "My description",
  "tags": ["Tag1", "Tag2"]
}
```

### With mapping (flexible)

Your API can return **anything**:
```json
{
  "data": {
    "video": {
      "name": "My title",
      "description": "My description"
    }
  },
  "categories": ["Tag1", "Tag2"]
}
```

and you configure:
- **Title** = `data.video.name`
- **Overview** = `data.video.description`
- **Tags** = `categories`

---

## 📝 Dot notation

Paths use **dot notation** to navigate the JSON:

```
"field"              → root.field
"data.field"         → root.data.field
"info.video.title"   → root.info.video.title
```

For `{"metadata": {"content": {"title": "Matrix"}}}`, the path `metadata.content.title` gives "Matrix".

Paths are **case-sensitive** (`Title` ≠ `title`). Arrays of objects use `array[].field` (one level only): see [GUIDE_TAGS.md](GUIDE_TAGS.md).

---

## ⚙️ Where to configure it

**Administration menu → Extensions → Custom Metadata → edit the source.** The mappings are in the "video / episode", "source / season" and "series" sections. Each of the three has its own set of fields (and its own image fields).

### Available fields

| Jellyfin field | Config field | Type | Example paths |
|----------------|--------------|------|---------------|
| Title | `Title` | string | `title`, `data.name`, `video.title` |
| Overview | `Overview` | string | `overview`, `description`, `data.synopsis` |
| Tags | `Tags` | array | `tags`, `categories`, `metadata.genres` |
| Release date | `PremiereDate` | datetime | `premiereDate`, `releaseDate`, `data.date` |
| Rating | `CommunityRating` | float | `communityRating`, `rating`, `score` |
| Cast | `Cast` | array | `cast`, `actors`, `credits.cast` |

### Cast fields (inside each cast object)

| Field | Config | Example |
|-------|--------|---------|
| Name | `CastName` | `name`, `actor_name` |
| Role | `CastRole` | `role`, `character` |
| Type | `CastType` | `type`, `job` |
| Order | `CastOrder` | `order`, `priority` |
| Photo URL | `CastImage` | `image`, `photo` |

### Image fields

| Field | Config | Example |
|-------|--------|---------|
| URL | `ImageUrl` | `url`, `image_url`, `src` |
| Type | `ImageType` | `type`, `image_type` |
| Width | `ImageWidth` | `width`, `w` |
| Height | `ImageHeight` | `height`, `h` |

---

## 📊 Worked examples

### Example 1: a simple API (the defaults)

**Your API returns:**
```json
{
  "title": "Matrix",
  "overview": "A programmer discovers...",
  "tags": ["Science-Fiction", "Action"],
  "premiereDate": "1999-03-31",
  "communityRating": 8.7,
  "cast": [
    {"name": "Keanu Reeves", "role": "Neo", "type": "Actor", "order": 1}
  ]
}
```

**Configuration (the defaults, nothing to change):** Title `title`, Overview `overview`, Tags `tags`, PremiereDate `premiereDate`, CommunityRating `communityRating`, Cast `cast`, with `name`, `role`, `type`, `order` inside each cast object.

### Example 2: a nested API

**Your API returns:**
```json
{
  "status": "success",
  "data": {
    "video": {
      "name": "Matrix",
      "description": "A programmer discovers...",
      "release": "1999-03-31",
      "score": 8.7
    },
    "categories": ["Science-Fiction", "Action"],
    "people": [
      {"person_name": "Keanu Reeves", "character": "Neo", "job": "Actor"}
    ]
  }
}
```

**Configuration:** Title `data.video.name`, Overview `data.video.description`, Tags `data.categories`, PremiereDate `data.video.release`, CommunityRating `data.video.score`, Cast `data.people` with CastName `person_name`, CastRole `character`, CastType `job`.

### Example 3: a TMDB/IMDb-style API

**Your API returns:**
```json
{
  "original_title": "The Matrix",
  "overview": "Set in the 22nd century...",
  "genres": [{"name": "Science Fiction"}, {"name": "Action"}],
  "release_date": "1999-03-31",
  "vote_average": 8.7,
  "credits": {
    "cast": [{"name": "Keanu Reeves", "character": "Neo", "cast_id": 1}]
  }
}
```

**Configuration:** Title `original_title`, Overview `overview`, Tags `genres[].name` (or `genres` with the tag field `name`), PremiereDate `release_date`, CommunityRating `vote_average`, Cast `credits.cast` with CastName `name`, CastRole `character`, CastOrder `cast_id`.

### Example 4: your own custom API

**Your API returns:**
```json
{
  "metadata": {
    "title_en": "Matrix",
    "synopsis_en": "A programmer...",
    "release_date": "1999-03-31",
    "rating": 8.7
  },
  "tags_list": ["SF", "Action"],
  "actors": [
    {"full_name": "Keanu Reeves", "character_name": "Neo", "role_type": "Actor"}
  ]
}
```

**Configuration:** Title `metadata.title_en`, Overview `metadata.synopsis_en`, Tags `tags_list`, PremiereDate `metadata.release_date`, CommunityRating `metadata.rating`, Cast `actors` with CastName `full_name`, CastRole `character_name`, CastType `role_type`.

---

## 🔧 Special cases

### Tags as objects

```json
"genres": [
  {"id": 1, "name": "Action"},
  {"id": 2, "name": "SF"}
]
```

Use `Tags = genres[].name` (or `Tags = genres` with `TagsItemField = name`). Details in [GUIDE_TAGS.md](GUIDE_TAGS.md).

### Images at the root (an array)

If your images endpoint returns an array directly:
```json
[
  {"url": "...", "type": "Primary"},
  {"url": "...", "type": "Backdrop"}
]
```
the mappings apply to each element: `ImageUrl = url`, `ImageType = type`.

### Nested images

If your endpoint returns:
```json
{
  "images": {
    "posters": [
      {"src": "...", "kind": "primary"}
    ]
  }
}
```
set the path to the array (`ImagesArray = images.posters`), then `ImageUrl = src` and `ImageType = kind`.

---

## ✅ Validating a mapping

1. **Configure the paths** in the source's editor.
2. Use the source's **test button**: it calls your API with the current form values (even unsaved) and shows the fields extracted, the images found and the raw JSON.
3. Scan one item and **check the logs**:
   ```bash
   sudo journalctl -u jellyfin -f | grep "JSON MAPPER"
   ```
   A field that is not found is reported with its path.

To look at the structure first: `curl -H "X-API-Key: your-key" https://your-api.example.com/videos/test-id`.

### Checklist

- [ ] The API answers and returns JSON
- [ ] The JSON structure is identified
- [ ] The paths are configured
- [ ] A test (button) and a scan were done
- [ ] The logs were checked
- [ ] The metadata shows correctly in Jellyfin

---

## 💡 Tips

1. **Start simple**: configure the title and overview first, test, then add the rest.
2. **Use the defaults**: if your API already uses `title`, `overview`, `tags`…, there is nothing to configure.
3. **Skip unused fields**: an **empty path is ignored** by the plugin. See [SKIP_FIELDS_GUIDE.md](SKIP_FIELDS_GUIDE.md).
4. **Test with a single item** before scanning the whole library.
5. **Keep the logs open** (`journalctl -u jellyfin -f`) to see right away when a field is a problem.

---

## 🐛 Troubleshooting

- **"No metadata retrieved"**: check that the API returns JSON, check the configured paths, and read the exact error in the logs.
- **"Field X is empty"**: the path is probably wrong. Check the JSON structure, that the path matches exactly and the case.
- **"Tags do not show"**: tags written as objects need `tagList[].label`-style paths (see [GUIDE_TAGS.md](GUIDE_TAGS.md)).

---

## 📚 Complete configurations

### YouTube Data API v3
```
Title: snippet.title
Overview: snippet.description
Tags: snippet.tags
PremiereDate: snippet.publishedAt
```

### TMDB
```
Title: title
Overview: overview
Tags: genres[].name
PremiereDate: release_date
CommunityRating: vote_average
Cast: credits.cast
```

### A custom nested API
```
Title: data.content.name
Overview: data.content.desc
Tags: data.metadata.categories
PremiereDate: data.content.published
CommunityRating: data.ratings.average
Cast: data.people.actors
```
