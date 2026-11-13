> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# Guide: fields missing from the JSON

## 🎯 The issue

Depending on the source or the kind of content, your API may return **different** JSON.

**Source 1 (complete):**
```json
{
  "title": "Matrix",
  "overview": "Description...",
  "rating": 8.7,
  "tags": ["SF", "Action"]
}
```

**Source 2 (partial):**
```json
{
  "title": "YouTube video",
  "overview": "Description..."
}
```
(no rating, no tags)

## ✅ How the plugin handles it

Missing fields are handled **automatically**.

### Case 1: path configured, field present

**Config:** `Title = title` — **JSON:** `{ "title": "Matrix" }` — **Result:** ✅ "Matrix" is imported.

### Case 2: path configured, field ABSENT from the JSON

**Config:** `CommunityRating = rating` — **JSON:** `{ "title": "Matrix" }` (no "rating") — **Result:** ✅ `null`, **no error**. The field stays empty in Jellyfin and you can fill it by hand.

### Case 3: empty path (deliberately skipped)

**Config:** `Tags =` (empty) — **JSON:** `{ "tags": ["Action"] }` — **Result:** ✅ the tags are ignored even though they exist (see [SKIP_FIELDS_GUIDE.md](SKIP_FIELDS_GUIDE.md)).

---

## 🔍 Detailed behaviour

### A missing simple path

**Config:** `Title = title` — **JSON:** `{ "name": "Matrix" }` → `null`.

### A partially missing nested path

**Config:** `Title = data.video.name`

- `{ "data": {} }` → `null` ✅
- `{ "other": "value" }` → `null` ✅
- `{ "data": { "video": {} } }` → `null` ✅

All cases are handled gracefully.

### `null` versus absent

`{"title": "Matrix", "rating": null}` and `{"title": "Matrix"}` behave identically: `rating` is `null` in both cases.

---

## 📊 Examples

### Example 1: an API with optional fields

**Your config:** Title `title`, Overview `overview`, Tags `tags`, Rating `rating`.

- A complete video (`title`, `overview`, `tags`, `rating`) → everything is imported ✅
- A partial video (`title`, `overview` only) → title and overview imported, tags and rating = `null` ✅

No error: everything works.

### Example 2: different sources, different structures

**YouTube-style source:** `{"data": {"snippet": {"title": "Title", "description": "Desc"}}}`
**Custom source:** `{"title": "Title", "description": "Desc"}`

Best: give each its own **source** (own prefix, own mapping) — the plugin is multi-source for exactly this reason. If you configure a single source for the most common structure (`Title: data.snippet.title`), the other structure simply returns `null` for those nested fields.

---

## 🛡️ Robustness

### ✅ Handled automatically

Missing fields, partially missing paths, `null` values, wrong types (for example a string where a number is expected), empty arrays and empty objects.

### ❌ It never crashes

Even if your API returns `{}`, the plugin returns `null` for every field. **No exception is raised.**

---

## 🔧 Logs and debugging

```bash
sudo journalctl -u jellyfin -f | grep "JSON MAPPER"
```

A found field is logged at debug level; a missing one is reported (with the path) as a warning such as `CommunityRating: path 'communityRating' NOT FOUND in JSON`. It is information, not a failure.

---

## 💡 Good practices

1. **Configure for the most common case.** If 80% of your videos have structure A and 20% structure B, configure A; the 20% get `null` fields you can fill by hand.
2. **Use separate sources** for completely different APIs (one prefix and one mapping each).
3. **Check the logs after a scan**: `journalctl -u jellyfin -f | grep "NOT FOUND"` shows which fields are often missing.
4. **Optional fields are fine**: do not worry if some fields are sometimes missing.

### Real-world cases

**A YouTube-like API where some videos have no tags.** Config `Tags: snippet.tags`: a video with `{ "snippet": { "tags": ["Tech"] } }` imports its tags, a video with `{ "snippet": {} }` simply has none.

**A custom API with mixed sources.** Config `Title: metadata.title`, `Rating: metadata.rating`, `Cast: metadata.speakers`: a professional source (with rating and speakers) imports everything, a personal source (title only) imports just the title.

---

## ✅ Summary

- ✅ Absent field → `null`
- ✅ Partial path → `null`
- ✅ Absent array → `[]`
- ✅ No exception raised
- ✅ Informative logs, not errors

Configure your paths once; the plugin adapts to whatever the API returns for each item.
