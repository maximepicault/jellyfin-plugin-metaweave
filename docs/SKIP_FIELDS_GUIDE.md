> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# Guide: skipping fields with empty paths

## 🎯 Behaviour

If you **leave a JSON path empty** in the mapping, the plugin **skips that field**: it is not read from the API and nothing is ever defaulted. This is why a new source starts with the standard paths pre-filled: an empty field is not "use the default", it is "ignore".

## ✅ Usage

In the source's editor, leave a mapping field **empty** to ignore that piece of data:

```
Title: title                    ← used
Overview: overview              ← used
Tags:                           ← EMPTY = skipped
Release date: premiereDate      ← used
Rating:                         ← EMPTY = skipped
```

**Result:**
- Jellyfin gets the title, the overview and the date.
- Tags and rating are NOT taken from the API.

---

## 💡 Use cases

### 1. Your API does not have the field

If your API gives no rating:
```json
{
  "title": "Matrix",
  "overview": "A programmer..."
}
```
leave the **rating** path empty.

### 2. You do not want to use the field

Even if your API provides tags, you may prefer to manage them by hand in Jellyfin: leave `Tags` empty.

### 3. The field is irrelevant for you

You only use the plugin for the title and the description:
```
Title: title
Overview: overview
Tags:                  ← empty
Release date:          ← empty
Rating:                ← empty
Cast:                  ← empty
```

---

## 📊 Examples

### Example 1: a minimal API

**Your API only returns:** `{"name": "Matrix", "desc": "Description..."}`

**Configuration:** Title `name`, Overview `desc`, everything else empty. **Result:** only the title and overview are imported.

### Example 2: skip the cast

**Your API returns** `title`, `overview`, `actors`, `rating`, but you want to manage the cast by hand in Jellyfin.

**Configuration:** Title `title`, Overview `overview`, Rating `rating`, and `Cast`, `CastName`, `CastRole` empty. **Result:** the cast is not imported.

### Example 3: skip the images

If you only want metadata, leave the image fields empty (`ImageUrl`, `ImageType`, `ImageWidth`, `ImageHeight`, the poster/backdrop/thumb/logo/banner paths and the images array): no image is taken from the API.

---

## 🔍 Technical details

### Values returned for an empty path

| Field type | Value |
|------------|-------|
| String | `null` |
| Int / Float | `null` |
| DateTime | `null` |
| Array | empty list `[]` |

### Effect in Jellyfin

- **Empty fields** do not overwrite existing data.
- **Filled in by hand**: the manual data is kept.
- **New scan**: empty fields stay empty.

---

## ✅ Good practices

1. **Configure only what you use**: only the fields your API really provides.
2. **Test progressively**: title and overview first, test, then tags and date, test, then cast and rating.
3. **Keep track of your configuration**, for example:
   ```
   # My API
   Title: data.video.name
   Overview: data.video.description
   Tags:                      # my API provides no tags
   Rating: data.ratings.score
   ```

---

## 🐛 Troubleshooting

### "Field X is always empty"

1. Check that the path is **not empty** in the configuration.
2. Check that the path is **correct** (use the source's test button).
3. Read the logs to see whether an error is raised.

### "I want to ignore a field but it is still filled"

Another provider (TMDB, IMDb, …) may be filling it. Disable the other providers for that library.

### "My manual values are overwritten"

If a path is **configured** (not empty), the plugin overwrites the value. Leave the path **empty** to keep the manual values.

---

## 📝 Summary

**Empty path = skip.** Leave a path empty to ignore a field: it is useful when your API does not provide it, it keeps the data you entered by hand in Jellyfin, and it raises no error.
