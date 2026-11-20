---
name: add-mapping-field
description: Checklist for adding or changing a field of a FieldMapping / EpisodeListMapping / PersonMappings (a new JSON path option) or a new JSON path feature, so every place that must know about it is updated. Use before touching PluginConfiguration.cs mappings or JsonPathExtractor.
---

# Adding a mapping field (or a path feature)

A field that exists only in some of these places silently falls back to its C# default or is dropped on save. This already happened (`ImagesArray` was displayed but never saved; `Cast*`, `ProductionYear`, `BannerUrl` were reset by the old form). Walk the whole list.

## Checklist

1. **Configuration** (`PluginConfiguration.cs`, class `FieldMapping`, or `EpisodeListMapping` / `PersonMappings`): public get/set property with a default. No `Dictionary`, no `[XmlElement]` (the config is XML-serialized with C# names).
2. **Reading JSON**: add it to `Services/JsonFieldMapper.cs` (`MapToMetadata`, `MapToImages`, ...). Paths are read ONLY through `JsonPathExtractor` (dot notation, `||` alternatives, `[n]`, `[]`): never `TryGetProperty` elsewhere. A new path feature goes in `JsonPathExtractor` and works for EVERY mapping.
3. **Response model**: `CustomMetadataResponse` / `ImageInfo` at the bottom of `Api/CustomApiClient.cs`. If the value must reach Jellyfin, set it in the provider(s) that apply it (movie, series, season, episode) and respect `EnableMetadata` / `EnableImages` / `EnableCasting`.
4. **Merging**: `Services/MetadataMerger` (files with several tags), `LocalSourceStore.MergeOverride` (local override of an API element) and `DropUnchangedFromApi` if the field is user-editable.
5. **Local sources and dumps**: `LocalSourceStore.Serve` (default JSON shape), `CustomApiClient.ResolveEndpoint` (imposed mappings), `DumpStore` / `DUMP_VIDEO_MAPPING` in `config.html` if relevant.
6. **Missing episodes**: if it affects virtual episodes, add it to the hash in `MissingEpisodeCache.ComputeHash` (only when the option is on, so toggling it refreshes) and to `MissingEpisodeService`.
7. **Text fields**: decode entities once at mapping time (`HtmlText`), never for URLs, never twice.
8. **UI** (`Configuration/config.html`): the input in the right mapping pane, `readSourceFromForm` (it merges over the existing source: keep unknown fields), the fill-in side (`editSource` / `fillSourceForm`), `applyMappingDefaults` (an EMPTY path is ignored by the plugin, so a new source needs the default pre-filled), `applyLocalSourceDefaults`, the "filled / total" counters, the click-to-fill `pathFor` rules, and the help text. French UI text; follow the `jellyfin-ui` skill.
9. **Docs**: `docs/JSON_MAPPING_GUIDE.md` (and `IMAGE_MAPPING.md` for images), in English.
10. **Changelog**: one English line in "Unreleased".
11. **Tests**: extend `tools/ui-test/config-local-ui.mjs` / `config-e2e.mjs` (the e2e test checks what the server actually stored, including fields the form does not show) and the mock API (`server.py`) if a new JSON shape is needed. Run the `verify-change` skill.

## Pitfalls
- Rebuild after editing a page: HTML is embedded in the DLL.
- Existing saved configs have no value for the new field: the default must be harmless for them.
- Do not change a field's meaning for existing sources without a migration note in the changelog.
