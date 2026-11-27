# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Jellyfin metadata/image provider plugin (`Jellyfin.Plugin.CustomMetadata`) that enriches media from user-configured REST APIs. Media files and folders carry an ID tag like `{PREFIX-ID}` in their names (e.g. `{mv1-123}`, `{yt-abc}`); the prefix selects which configured API "source" to query. 

## Language policy

- **Everything in the repository is written in English** so that anyone can read it: code comments (C#, HTML/CSS/JS, shell, scripts), log messages, documentation (`README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `docs/`, `deploy/`), commit messages, pull request texts, GitHub description.
- **The plugin's user interface text is French for now** (buttons, labels, help texts and the messages the server returns to the UI). Keep it French and consistent; an English or bilingual interface is a possible later step (a translation table), not something to mix in piecemeal.
- **French is only for the conversation with the maintainer.** Never leave a French comment, doc or commit message behind. Demo data of the test bench (`tools/ui-test/data.py`) is French on purpose: it exercises the French UI.
- When a test asserts on a UI string, the string stays French inside the test, while the test's own descriptions and comments are English.

## Build

```bash
dotnet build --configuration Release      # output: bin/Release/net9.0/Jellyfin.Plugin.CustomMetadata.dll
./build.sh                                # clean + Release build
```

- The target server is **Jellyfin 10.11.6**. The project targets **net9.0** against `Jellyfin.Controller`/`Jellyfin.Model` **10.11.6** (`ExcludeAssets=runtime`). Keep the package version equal to the server version: building against a newer 10.11.x than the server can break loading.
- `Microsoft.Extensions.Http`/`Caching.Memory` must be ≥ the version Jellyfin pulls in transitively (9.0.11 for 10.11.6). Otherwise restore fails with NU1605.
- **Visual/end-to-end test bench: `tools/ui-test/ui-test.sh`.**
  - It builds the plugin and starts a Jellyfin **10.11.6** container (`cm-uitest`, http://localhost:8097, admin/admin) with a mock API (`server.py`, port 8765), tagged demo media (`gen.py`) and source prefix `dm`.
  - It then runs "Tout retraiter" and saves screenshots of the missing-episodes screen and of the config page (full page + source modal) in `~/.cache/cm-ui-test/shots`.
  - Options: `--no-build` redeploys the DLL already built (HTML pages are embedded in it, so use it only when no page changed), `--fast` builds, swaps the DLL and restarts the container only (no analysis, no screenshots: the inner loop), `--stop` stops everything.
  - Test scripts share the container and take a lock: run one at a time. `config-people-real.mjs … --quick` runs only the photo-cropper checks (~10 s). Pitfalls: skill `.claude/skills/jellyfin-ui` section 3b.
  - If `docker ps` says permission denied (your user is no longer in the `docker` group), the container tests can't run: use `tools/ui-test/config-local-ui.mjs` (no Docker) and ask the user to fix the group.
  - `tools/ui-test/config-e2e.mjs <url> <out_dir>` is an end-to-end test of the config page against that container: edit, toggle, add, delete, global settings, and it checks what the server actually stored (including fields the form doesn't show). It restores the original config.
  - Work data lives outside the repo on purpose: the SDK-style csproj would compile the `*.cs` of a build copy placed inside it.
  - The container on port 8096 belongs to another project: don't touch it.
  - UI work follows the project skill `.claude/skills/jellyfin-ui`.
- There are no tests and no linter. A `Tests/` folder once existed but was deleted because its Moq/xUnit references didn't compile.
- To verify a change, build it and copy the DLL to `/var/lib/jellyfin/plugins/CustomMetadata/`. Then restart Jellyfin and check the log with `journalctl -u jellyfin -f`, grepping on the emoji tags below.
- The plugin config is stored as XML at `/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml`.
- `Nullable` is disabled and warnings are not errors.

- **Test scripts and Chrome**: the raw-CDP scripts of `tools/ui-test` start Chrome with `detached: true` and `env: { ...process.env, TMPDIR: chromeTmp() }`, then call `trackChrome(chrome, profile)` (`chrome-session.mjs`): the whole process group is killed and the profile and temp folders removed at the end of the script, on an error, Ctrl-C or a SIGTERM from `timeout`. A plain `chrome.kill()` left hundreds of 60 MB profiles and Chrome processes behind. The temp folder must be SHORT (a Unix socket path is limited to ~108 characters: a TMPDIR inside the long profile path stops Chrome from starting). `ui-test.sh` also sweeps leftovers of an interrupted run. New CDP scripts must follow the same pattern.

## Architecture

- Jellyfin discovers the providers in `Providers/` by reflection.

- `CustomMetadataProvider` (Movie), `CustomSeriesProvider`, `CustomSeasonProvider` and `CustomEpisodeProvider` implement `IRemoteMetadataProvider<T, TInfo>`. `CustomImageProvider` implements `IRemoteImageProvider`, and `CustomExternalId` implements `IExternalId`.
- `PluginServiceRegistrator` (`IPluginServiceRegistrator`) registers **every service as a singleton** (stores, `CustomApiClient` which owns its `JsonFieldMapper`, `SourceResolver`, `MissingEpisodeService`…; some also as hosted services). Providers, the scheduled task and the controllers get them by constructor injection. Don't `new` them up again. The full list, with who uses what, is in `docs/architecture/code-map.md`.
- The helper classes take a non-generic `ILogger`. The registrator passes each one its own `ILogger<T>`, so `📡 [API CLIENT]`/`💾 [CACHE]` lines are logged under the `CustomApiClient`/`MetadataCacheService` category, not the calling provider's.

**Request flow (same in each provider):**
1. Extract `PREFIX-ID` with `Services/TagParser` (`Extract`, `GetOwnName`, `Strip`; the single copy of the regex `\{([\w\-]+-[\w\-]+)\}`). Run it only on the item's **own** file or folder name, from `Path.GetFileName(...)` on `info.Path`/`item.Path`. Don't run it on the full path: a season or episode would then pick up the series ID from a parent folder. Don't use `info.Name` either: it is Jellyfin's parsed title and gets truncated at things like `(2025)`, so the tag is lost.
   - **A file may carry several tags** (`Ep {dm-1} {dm-2}.mkv`) when it concatenates several API episodes: `TagParser.ExtractAll` (ordered, case-insensitive dedupe). **Episodes only**; seasons, series and movies keep the first tag.
     - The **first tag is the reference** (it is also the one stored in `ProviderIds["custom"]`). `CustomEpisodeProvider` fetches the other ids (each through its own source) and `Services/MetadataMerger` merges: tags, genres and cast are unioned without duplicates; title, overview, date, rating and number come from the first, and an empty field falls back to the next id. An unreachable extra id is skipped, an unreachable first id hands the reference role to the next.
     - `IndexNumberEnd` is set only when the API numbers are consecutive (3 and 4 → "3-4"); otherwise the provider returns the item's current `IndexNumberEnd`.
     - `CustomImageProvider` appends the other ids' images **after** the first id's, with `ProviderName` "Custom Image Provider (dm-2)", so the first stays selected by default and the rest can be picked in the image editor.
     - Missing episodes: `GetEpisodeKeys` counts every tag of a physical file as present, so `dm-2` is no longer reported and its virtual episode is deleted at the next analysis.
2. `SourceResolver.ExtractPrefixFromId` splits at the **first** `-`, so a source prefix cannot itself contain a hyphen (an old naming note wrongly said `mon-app` was valid). Then `GetSourceByPrefix` (case-insensitive, enabled sources only) → `ExtractIdWithoutPrefix`.
3. If there is no prefix, or no enabled source matches it, **return an empty result early**. Files without a matching tag are left to other providers.
4. Call `CustomApiClient.GetMetadataAsync(itemId, itemType, source, ct)`. `itemType` picks both the endpoint and the field mapping:
   - `series` → `SeriesEndpoint` + `SeriesFieldMappings`
   - `season`/`source` → `SourceEndpoint` + `SourceFieldMappings` (Jellyfin seasons represent the API's "sources")
   - everything else → `VideoEndpoint` + `VideoFieldMappings`

   The URL is built by `ResilientHttp.BuildUrl(BaseUrl, endpoint, id)`: exactly one `/` between base and endpoint whatever the user typed, and `{id}` URL-escaped.

**Jellyfin 10.11 API constraints.** `People` no longer exists on items, and `EpisodeInfo.SeriesName`/`SeasonName` were removed. Cast goes through `MetadataResult<T>.AddPerson(new PersonInfo { Type = PersonKind.X })` instead, via `Services/CastMapper.cs`. Several French docs wrongly say `PersonInfo` was removed and that casting is impossible.

**Feature flags.** Each metadata provider returns early when `EnableMetadata` is off. `CustomImageProvider` checks `EnableImages`, and `CastMapper` checks `EnableCasting`. The season name falls back as follows: API title → folder name with the `{PREFIX-ID}` tag stripped → `DefaultSourceName`. All of these are editable in `config.html`.

**Configuration (multi-source vs. legacy).** `PluginConfiguration.Sources` (a `List<SourceConfiguration>`) is the current model. Each source has its own prefix, base URL, API key, three endpoints, three `FieldMapping`s and `CustomHeaders`.
- The top-level fields (`PatternPrefix`, `ApiBaseUrl`, `VideoEndpoint`, `*FieldMappings`, …) are the legacy single-source config. When `Sources` is empty, `SourceResolver.GetDefaultSource()` builds a `SourceConfiguration` from them, and the `PluginConfiguration` constructor tries to auto-migrate them. `CustomMetadataProvider` (movies) still uses only this legacy path: it takes the ID from `ProviderIds["custom"]`, then IMDb, then the name, and never resolves a source from the prefix.
- The config is serialized with `XmlSerializer` and has no `[XmlElement]` attributes. Element names are therefore the C# names: `<Sources><SourceConfiguration><Name>…`. The hand-written XML examples in `docs/MULTI_SOURCE_GUIDE.md` (`<Source>`) don't match, and their contents would be silently ignored.
- `Dictionary` can't be XML-serialized: it caused an HTTP 500 on the config page, which is why `CustomHeader` exists as a key/value class. Keep new config types to public get/set properties with defaults.
- The only remaining legacy overload is `CustomApiClient.GetMetadataAsync(itemId, itemType, ct)` (no source), used by the movie provider. It builds the source with `SourceConfiguration.FromLegacyConfig`, as `SourceResolver.GetDefaultSource` does. Use the source-based overloads in new code. The legacy top-level config properties (including `FieldMappings`) must stay for XML compatibility.

**Provider ids: always read them case-insensitively, through `Services/CustomId`.**
- `BaseItem` creates `ProviderIds` with `OrdinalIgnoreCase`, but items re-read from the DB get a case-sensitive dictionary (`BaseItemRepository`: `Provider.ToDictionary(...)`).
- The user's DB held `Custom=…` (capital C) on virtual episodes. So `TryGetValue("custom")`, `GetProviderId("custom")` and `k == "custom"` silently failed. That broke the old lookup and orphan detection, and is why a case-sensitive SQL check showed "0 custom ids".
- Use `CustomId.Get(item.ProviderIds)` / `CustomId.IsCustomKey`. The movie provider still uses the old lookup; the user asked not to touch movies.
- Bookkeeping still does not depend on `ProviderIds` (deterministic ids, `OwnerId` for previews). `CustomEpisodeProvider` skips virtual episodes (no `Path`) without a custom id.

**Provider id key.** The plugin always stores its ID under `ProviderIds["custom"]`, and `CustomExternalId.Key` must stay `"custom"` so the id shows under "External IDs" in the metadata editor (it used to be `PatternPrefix`, which hid it). Don't make internal bookkeeping depend on ProviderIds when a structural check works: preview trailers are identified by `OwnerId` + `ExtraType.Trailer` + http(s) `Path`.

**Logging conventions.** Logs are very verbose and tagged per component with an emoji prefix, e.g. `🎬 [EPISODE PROVIDER]`, `📺 [SERIES PROVIDER]`, `📁 [SEASON PROVIDER]`, `🖼️ [IMAGE PROVIDER]`/`[IMAGE FETCH]`/`[IMAGE MAPPER]`, `🔄 [JSON MAPPER]`, `📡 [API CLIENT]`, `💾 [CACHE]`, `🔍 [SOURCE RESOLVER]`, `📋 [HEADERS]`. Follow this style in new code. Debugging relies on grepping these tags.

## Detailed architecture (read on demand)

The per-feature detail lives in `docs/architecture/`. **Read the matching file before changing that area**; do not read them all.

| File | Read when working on |
|---|---|
| `docs/architecture/code-map.md` | **start here** to find where something lives: wiring (DI, discovered types), metadata pipeline, background jobs and locks, stored files, REST routes → pages, change recipes, test scripts |
| `docs/architecture/config-ui.md` | `config.html`: list/editor views, save models, library check |
| `docs/architecture/images.md` | image provider, downloads, portrait→16:9 thumbnails |
| `docs/architecture/json-mapping.md` | `JsonFieldMapper` / `JsonPathExtractor`, mapping fields, entities, overview as tags |
| `docs/architecture/missing-episodes.md` | missing/virtual episodes, their cache, `missing.html`, preview trailers |
| `docs/architecture/local-sources.md` | local sources, uploads, gallery, folder images, targeted refresh, overrides |
| `docs/architecture/dump-sources.md` | dump sources and their coverage view |
| `docs/architecture/people-cast.md` | people directory, cast overlay, cascade, apply |
| `docs/architecture/ui-pages.md` | tab bar, Diagnostic, click-to-fill, confirmations, setup guide, left menu |
| `docs/architecture/episode-media.md` | local episode preview video, chapters |
| `docs/architecture/episode-photos.md` | episode photo gallery: photo folders, local « Photo » images, signed links, `web/gallery.js` |
| `docs/architecture/theme-index.md` | the theme's filtered lists and library screens, `theme/projection-index.js`, the server search `CustomMetadata/Index` (`LibraryIndex*`) |
| `docs/architecture/taxonomy.md` | genres and tags rules |
| `docs/architecture/http-and-cache.md` | `ResilientHttp`, headers, CORS, source test, metadata cache |

Rules that apply everywhere (they stay here): the request flow, `CustomId`, the provider id key, the config/XML rules, the logging style and the Git rules below. Never read `Configuration/config.html` whole (it is about 350 KB): use `grep` then read a range.

## Git rules (agreed with the maintainer — follow them exactly)

Full human-facing version: `CONTRIBUTING.md`. What matters for the assistant:

- **Only when asked.** Commit or push only when the maintainer asks (or the request clearly includes delivering the change). Then: work on a branch, commit, push the branch, open a pull request with `gh`. Never proactively.
- **What the assistant may do:** create branches (`feat/…`, `fix/…`, `docs/…`, `refactor/…`, `ci/…`, `chore/…`, lowercase, kebab-case, English), commit, push **its own branches**, open pull requests. **Also: commit and push directly to `develop`** (see "Branch `develop`" below), one atomic commit per logical change.
- **What the assistant must NEVER do:** push to `main`, merge a pull request, create or push a tag, publish or edit a release, change repository settings or branch/tag protection, use `--no-verify`, force-push or rewrite published history **except** the two cases below. Those stay with the maintainer.
  - After rebasing **its own open pull-request branch**, it force-pushes that branch with `git push --force-with-lease` (never plain `--force`, never `main`, never a tag, never a branch it did not create).
  - When the maintainer says the `develop` → `main` release pull request is merged, it realigns `develop` (`git reset --hard origin/main`, then `git push --force-with-lease origin develop`).
- **One pull request per topic**, squash-merged by the maintainer into `main`: the PR title becomes the commit message on `main`, so it uses the commit format below. This is the path for a change that goes straight to `main`; the usual path is `develop`.
- **Branch `develop`** (the maintainer's integration branch, no one else uses it, no ruleset): the assistant commits to it directly, **one commit per logical change, never squashed**, each with its changelog line when user-visible. CI builds every push. It reaches `main` through one pull request `develop` → `main` that the **maintainer** merges with **"Rebase and merge"**, so the list of commits stays on `main`. The assistant opens that pull request when asked (title like `release: <summary>`, body listing the commits). Afterwards `develop` is realigned on `main` (see above), and the assistant never adds commits to `develop` before that is done. Before each push to `develop`: fetch, make sure it is based on the current `develop` and not behind `main`. A deployment of `develop` works with the signature check on: commits pushed directly keep the maintainer's signature.
- **Commit messages: Conventional Commits, in English**, first line at most 72 characters: `type(scope): subject` (types: `feat fix docs refactor perf test build ci chore revert`; `!` for a breaking change). Imperative mood, no final period, body explains the *why*. One logical change per commit, no "wip".
- **Attribution.** End every commit message with the trailer for the session's model, currently `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. End every pull request description with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Changelog.** Every user-visible change adds one English line to the "Unreleased" section at the top of `CHANGELOG.md`, in the same commit. A release is its own pull request (rename "Unreleased" to `Version 0.0.N alpha (Month Year)`, open a new empty "Unreleased", align `<Version>` in the csproj); the maintainer tags after merging.
- **Before committing:** hooks active (`git config core.hooksPath .githooks`; `pre-commit` refuses bin/obj/DLL, local settings, personal paths/e-mails/tokens/private IPs, files over 1 MB, shell/JS syntax errors; `commit-msg` checks the format), build clean, relevant tests run (say which and the result), embedded HTML pages rebuilt, no screenshots or test output in the commit.
- **Personal words** (the maintainer's server name, login…) go in the untracked `.git/forbidden-words`, never in a tracked file.

## Repository, versions and releases

- **Public repository.** Keep it clean and portable: no secrets, no host names, no paths of the maintainer's machine (`/home/<user>`), nothing specific to one server. Machine-specific helpers (for example a personal deploy script) live in the PARENT directory of the repo, never inside it. `.claude/settings.local.json` is git-ignored.
- **Docs.** `README.md` (user-facing, accurate), `CHANGELOG.md` (the history), `docs/` (guides that are still mostly current: JSON mapping, tags, images (`IMAGE_MAPPING.md`, `GUIDE_IMAGES.md`), multi-source, missing/skip fields; each starts with a banner saying README and this file win). The ~40 historical fix-log and status notes of the early development were moved OUT of the repo (parent directory, `JellyfinCustomPlugin-archive-notes/`) and their content is summarised in the changelog: don't recreate such per-fix files, add a changelog line instead.
- **Version numbers.** `0.0.x alpha`, set after the fact to match the Git history (the project had no version before): the old "1.0.0–1.5.0" entries are 0.0.1–0.0.6, the unnumbered Feb–Oct work is 0.0.7, the current release is 0.0.8. The version lives in `Jellyfin.Plugin.CustomMetadata.csproj` (`<Version>`); CI overrides it with the tag (`-p:Version=`). Git tags are `vX.Y.Z` (the updater and the workflow only accept that shape).
- **Every user-visible change gets a `CHANGELOG.md` line** in the newest entry (new section when a tag is cut). Older entries are history, don't rewrite them.
- **Release flow.** `git tag vX.Y.Z && git push --tags` → `.github/workflows/build.yml` builds with `dotnet build -c Release -p:Version=X.Y.Z`, writes `SHA256SUMS`, and only on a tag publishes a GitHub release (`Jellyfin.Plugin.CustomMetadata.dll` + `SHA256SUMS`, titled "vX.Y.Z (alpha)", not a pre-release so that `/releases/latest` returns it). Pull requests and plain pushes only build. No self-hosted runner for this public repo (an outside pull request could run code on the server).
- **Server side.** `deploy/custom-metadata-updater.sh` (+ `.service`, `.timer`, `.env.example`, `deploy/README.md`) polls `/releases/latest`, checks the tag shape and the SHA-256, backs up the DLL and the config XML, stops Jellyfin, installs, restarts, waits for `/health`, and rolls back (and never retries that version) on failure. `deploy/test/test-updater.sh` tests it in a sandbox (fake GitHub API, fake `/health`, fake `systemctl`; 17 checks).

## Project memory

At startup, read `docs/memoire/ETAT.md` and the index of `docs/memoire/DECISIONS.md` (if the folder exists; otherwise run the `memoire-init` skill when asked). After any lasting technical decision, record it through the `archiviste` agent. At the end of a session, run `memoire-fin-session`. Reference format: `docs/memoire/README.md`.
