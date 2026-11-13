> ⚠️ Guide written along the development. The [README](../README.md) and [CLAUDE.md](../CLAUDE.md) prevail if a detail differs (XML element names of the configuration, cache file names, old routes).

# 🏷️ Tags mapping guide

How to read the **tags** of an item from your API's JSON. The same logic applies to the genres (`Genres` + `GenresItemField`).

## 📍 Overview

The plugin supports **two notations** for tags:

### ✨ Recommended: `tagList[].label` (more intuitive)
```
Tags:      tagList[].label
Tag field: (leave empty)
```

### 📝 Alternative: two separate fields
```
Tags:      tagList
Tag field: label
```

**Both behave exactly the same.** The `[]` notation is just clearer.

The mapping lives in the source's editor, in the "video / episode" section (and in the series section for series tags).

---

## 🎯 Examples with the `[]` notation

### Example 1: an array of objects

**JSON returned by your API:**
```json
{
  "title": "Episode 1",
  "tagList": [
    {"id": 4420, "count": 1, "label": "fantasy"},
    {"id": 4421, "count": 5, "label": "sci-fi"}
  ]
}
```

**Configuration with `[]` (recommended):**
```
Tags:      tagList[].label
Tag field: (leave empty)
```

**Or with two fields:**
```
Tags:      tagList
Tag field: label
```

**Result:** ✅ tags = fantasy, sci-fi

---

### Example 2: tags in a nested object

**JSON:**
```json
{
  "metadata": {
    "categories": [
      {"name": "Fantasy"},
      {"name": "Adventure"}
    ]
  }
}
```

**With `[]`:** `Tags: metadata.categories[].name`

**With two fields:** `Tags: metadata.categories` and `Tag field: name`

---

### Example 3: a plain array of strings

**JSON:**
```json
{
  "tags": ["fantasy", "sci-fi", "adventure"]
}
```

**Configuration:**
```
Tags:      tags
Tag field: (leave empty)
```

No `[]` needed: it is already an array of strings.

---

### Example 4: deeper structures

**JSON:**
```json
{
  "video": {
    "meta": {
      "genres": [
        {"id": 1, "value": "action", "priority": 1},
        {"id": 2, "value": "thriller", "priority": 2}
      ]
    }
  }
}
```

**With `[]`:** `Tags: video.meta.genres[].value`

It reads naturally: take `value` from each element of the array.

### Example 5: objects with several fields (only one is extracted)

**JSON:**
```json
{
  "tags": [
    {"id": 123, "name": "Fantasy", "description": "Magical worlds", "count": 42},
    {"id": 456, "name": "Sci-Fi", "description": "Future tech", "count": 38}
  ]
}
```

**Configuration:** `Tags: tags` and `Tag field: name`

**Result:** ✅ only `name` is extracted, the other fields are ignored.

---

## 📊 Comparing the notations

| JSON | `[]` notation | Two-field notation |
|------|---------------|--------------------|
| `tagList: [{"label": "fantasy"}]` | `tagList[].label` | Tags: `tagList` / Field: `label` |
| `tags: ["fantasy"]` | `tags` | Tags: `tags` / Field: *(empty)* |
| `meta.tags: [{"name": "x"}]` | `meta.tags[].name` | Tags: `meta.tags` / Field: `name` |
| `"tag1, tag2"` (comma-separated string) | `tags` | Tags: `tags` / Field: *(empty)* |

### Why prefer `[]`

- **Intuitive**: "take each element of `tagList` and extract `label`", all in one expression.
- **Familiar** if you know `jq` (`jq '.tagList[].label'`).
- **Fewer mistakes**: one field to fill instead of two.

Use the two-field notation if you prefer to keep the concepts separate: they are technically equivalent. If you fill both, the `[]` notation takes precedence.

---

## 🔍 Troubleshooting

### No tag appears

1. ✅ **Correct syntax?**
   - `tagList[].label` ✅
   - `tagList.[].label` ❌ (dot before `[]`)
   - `tagList[].label.` ❌ (trailing dot)
   - `tagList [] . label` ❌ (no spaces)
2. ✅ **Correct path?**
   ```bash
   curl https://your-api.example.com/videos/123 | jq '.tagList[].label'
   ```
   It must print `"fantasy"`, `"sci-fi"`.
3. ✅ **Correct field?** If your JSON has `"label"`, configure `label` (without quotes).
4. ✅ **Jellyfin logs:**
   ```bash
   sudo journalctl -u jellyfin -f | grep -i tag
   ```

### Empty or null tags

Probably the wrong field. With `{"tags": [{"name": "fantasy"}]}`:

- ❌ `Tag field: label` finds nothing
- ✅ `Tag field: name` works

### Try it end to end

1. Check your JSON: `curl https://your-api.example.com/videos/123 | jq '.tagList'`.
2. Configure the mapping, then use the source's **test button**: it shows the extracted tags without saving anything.
3. Run a library scan and open the item: the tags should appear.

---

## 💡 Good practices

- **Be consistent with case**: Jellyfin treats `Fantasy` and `fantasy` as different tags.
- **Cleaning is automatic**: empty or null tags are ignored and leading/trailing spaces are removed.
- **Number of tags**: no limit, but 5 to 10 per video is reasonable.

---

## ❓ FAQ

**Can I use `[]` several times (`data.items[].tags[].label`)?**
Not at the moment: use a single level.

**Can I extract several fields from a tag object?**
No, one field per object. Pick the most relevant one (usually `label` or `name`).

**What if the field is missing in some objects?**
Those objects are ignored:
```json
[
  {"label": "fantasy"},  // ✅ kept
  {"name": "sci-fi"},    // ❌ ignored (no "label")
  {"label": "action"}    // ✅ kept
]
```
Result: `fantasy`, `action`.

**Can tags contain spaces?**
Yes: `"Science Fiction"` is a valid tag.

**Can I combine several formats?**
No, choose one format per endpoint, the one that matches your JSON structure.

**Is one notation faster?**
No, performance is identical.

**What if the description of an item is just a list of tags?**
See the "overview as a tag list" option (`OverviewListTarget`) in the README / CLAUDE.md: such an overview is detected and moved to the tags or genres.
