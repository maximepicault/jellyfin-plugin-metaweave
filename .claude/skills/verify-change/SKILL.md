---
name: verify-change
description: Pick and run the right checks of this plugin's test bench for a change (build, ui-test container, tools/ui-test/*.mjs scripts) and report what passed. Use after changing code or an embedded page, before committing or saying a change is done.
---

# Verify a change with the test bench

Goal: map what changed to the cheapest checks that really exercise it, run them, and report honestly (which ran, result, what was NOT covered).

## Rules that always apply
- Build first: `./build.sh` (or `dotnet build -c Release`). Warnings are not errors, but report new ones.
- **HTML pages are embedded in the DLL**: after editing anything in `Configuration/*.html`, rebuild and redeploy WITHOUT `--no-build`.
- Test Jellyfin is the `cm-uitest` container on **http://localhost:8097** (admin/admin), started by `tools/ui-test/ui-test.sh`. **Never touch the container on port 8096** (another project).
- If `docker ps` says permission denied, container tests cannot run: use `tools/ui-test/config-local-ui.mjs` (no Docker) and tell the user their user is no longer in the `docker` group.
- Scripts live in `tools/ui-test/`; run them from the repo root as `node tools/ui-test/<script>.mjs <args>`. Output dirs go in the scratchpad or `~/.cache/cm-ui-test/shots`, never in the repo. Most scripts seed their own state and restore it.
- Never leave screenshots or test output in a commit.

## Which check for which change

| What changed | Run |
|---|---|
| Any `.cs` file | build; then `ui-test.sh` (deploys, "Tout retraiter", screenshots) |
| `config.html` (list, editor, tabs) | `ui-test.sh` (rebuild), `config-local-ui.mjs <out>` (no Docker), `config-e2e.mjs <url> <out>` |
| Local sources, images, gallery | `config-local-real.mjs`, `local-gallery-real.mjs <url>`, `local-override-real.mjs <url>`, `local-series-id-real.mjs`, `local-folder-images-real.mjs <url> <series_dir> <images_dir>` |
| Image persistence / `ApplyImagesAsync` | `refresh-persist-real.mjs <url> <series_root> <images_dir> <jellyfin.db>` (reads the DB read-only) |
| Preview video of local episodes | `local-preview-video-real.mjs` (needs ffmpeg) |
| People directory / cast | `config-people-real.mjs`, `config-people-episodes-real.mjs` |
| Dump sources | `dump-real.mjs` |
| Chapters | `chapters-real.mjs` |
| Genres and tags rules | `taxonomy-real.mjs` |
| Diagnostic page, tab bar | `diagnostic-real.mjs` |
| Left-menu shortcuts, `Plugin.GetPages()` | `config-menu-real.mjs` |
| Missing episodes, `missing.html` | `ui-test.sh`, `improvements-real.mjs` |
| `deploy/` | `deploy/test/test-updater.sh` |
| Shell or `.mjs` syntax | `bash -n` / `node --check` (also done by the pre-commit hook) |

Scripts taking `<url>` use `http://localhost:8097`. Optional `[user] [password]` default to admin/admin.

## Procedure
1. `git status` / `git diff --stat` to see what changed; choose checks from the table (when unsure, ask or take the broader set).
2. Build. Fix errors before anything else.
3. Run `ui-test.sh` when server code or an embedded page changed; then the targeted scripts, one at a time. Long runs go in the background.
4. For UI work, also follow the `jellyfin-ui` skill (real screenshots, self-critique).
5. Report: checks run with counts (e.g. "34/34"), failures with the relevant output, and what was not verified (theme, mobile, real library scan...).

## Cleanup
- `ui-test.sh --stop` stops the container and the mock API when the user is done.
- Never leave Chrome profiles behind: CDP scripts must follow the `trackChrome` pattern (see CLAUDE.md).
