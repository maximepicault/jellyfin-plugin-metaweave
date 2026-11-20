---
name: debug-jellyfin
description: Method to investigate a bug in this plugin's behaviour inside Jellyfin (metadata or images not applied, items vanishing, refresh not changing anything): reproduce in the test container, read the tagged logs, check the database read-only. Use when the user reports a behaviour bug.
---

# Investigating a bug in Jellyfin

Trust the report: the user observed something real. Reproduce it before theorising, and prove the cause before fixing.

## Method
1. **Restate** the symptom in one line: which item type, which source, which action (scan, refresh, "Tout retraiter", edit in the UI).
2. **Reproduce in the test container** (`tools/ui-test/ui-test.sh`, http://localhost:8097, never the 8096 one). Use the mock API (`tools/ui-test/server.py`) and the tagged demo media (`gen.py`); add a case there if the bug needs a new shape. For anything visual, take REAL screenshots after an F5 reload, not just REST answers: the REST API answers from memory and can hide persistence bugs.
3. **Read the logs by tag.** In the container: `docker logs cm-uitest 2>&1 | grep -E '<tag>'`; on a real server: `journalctl -u jellyfin`. Tags are emoji + name: `🎬 [EPISODE PROVIDER]`, `📺 [SERIES PROVIDER]`, `📁 [SEASON PROVIDER]`, `🖼️ [IMAGE PROVIDER]`, `🖼️ [IMAGE DOWNLOAD]`, `🔄 [JSON MAPPER]`, `📡 [API CLIENT]` / `[API CALL]`, `💾 [CACHE]`, `🔍 [SOURCE RESOLVER]`, `📋 [HEADERS]`, `🔎 [MISSING]`, `📃 [EPISODE LIST]`, `🔁 [SOURCE REFRESH]`, `📦 [DUMP]`, `🧪 [SOURCE TEST]`. Absence of a log is information: Jellyfin never calls a provider that is unticked in the library, and drops 403/404 image answers at Debug level.
4. **Check the database, read-only.** Copy `jellyfin.db` and open the COPY (or `node:sqlite` read-only). Never write into Jellyfin's database. Typical questions: are the images in the item's image list after the refresh? (a bug where `SaveImage` was not persisted only showed there); provider ids (`custom`, read case-insensitively: it may be `Custom`); duplicate or orphan rows.
5. **Narrow it down** with the request flow in CLAUDE.md: tag extraction (own file/folder name only) → source resolution (enabled sources, first `-`) → endpoint/mapping by item type → cache (manual refresh bypasses it) → Jellyfin's own merge rules (local images and existing tags win or merge).
6. **Prove the cause**: change one thing, re-run the same reproduction, see the symptom go away; and see it come back without the change.
7. **Fix, then add a regression check** to a `tools/ui-test` script when practical. Run the `verify-change` skill.
8. **Record the investigation** (symptom, cause, how it was proven, what was ruled out) in the commit body for a fix, and in the project's memory notes if the user keeps them.

## Known traps (details in CLAUDE.md and `docs/architecture/`)
- Jellyfin 10.11 APIs differ from older docs (no `People` on items, no `EpisodeInfo.SeriesName`).
- Providers returning the item's current `IndexNumber`: otherwise a replace wipes numbers and virtual episodes get deleted.
- Never use `IProviderManager.QueueRefresh` for several items at once.
- Do not run the tag regex on a full path.
- Do not turn an API error into an empty list (it would delete every virtual episode of the season).
- Do not touch the other project's container or the user's real library.
