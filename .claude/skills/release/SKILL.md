---
name: release
description: Prepare a release of this plugin: checklist, changelog and version bump on develop, the develop -> main pull request, and the realignment of develop after the maintainer merges. Use only when the maintainer asks to prepare or open a release; never tags or merges.
---

# Releasing the plugin

Rules (CONTRIBUTING.md and CLAUDE.md win if they differ):
- The assistant **never** pushes to `main`, merges a pull request, creates or pushes a tag, publishes or edits a release. The maintainer merges ("Rebase and merge") and tags.
- Only act when asked. Commits attribution trailer and PR footer as in CLAUDE.md.

## 1. Prepare (on `develop`)
1. `git fetch`; `develop` must contain `origin/main` (not behind) and be pushed. `git status` clean.
2. Ask the maintainer for the version `0.0.N` if not given (`git tag --list 'v*' --sort=-v:refname | head -3` shows the last one).
3. Run the "Before a tag" checklist of CONTRIBUTING.md: clean `./build.sh`; test bench for what changed (use the `verify-change` skill; HTML is embedded, rebuild); `deploy/test/test-updater.sh` if `deploy/` changed; changelog up to date; review added files (no secret, machine path, server name, personal address).
4. One release commit on `develop`: rename "Unreleased" to `Version 0.0.N alpha (Month Year)` in `CHANGELOG.md`, open a new empty "Unreleased" above it, set `<Version>` in `Jellyfin.Plugin.CustomMetadata.csproj` to `0.0.N`. Message: `chore(release): prepare v0.0.N`. Hooks on, no `--no-verify`.
5. Push `develop` (fetch first; deploying to the test server goes through the `deploy-semaphore` skill if the user wants it).

## 2. Open the pull request
`gh pr create --base main --head develop --title "release: <summary>"` with a body listing the commits (`git log --oneline origin/main..develop`) grouped by type, and the changelog section. End the body with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then bind it with the ccd_pr tools as the app requires. Do NOT enable auto-merge. Tell the maintainer to merge with **"Rebase and merge"** and then tag from `main` themself.

## 3. After the maintainer says it is merged
Realign `develop` (the only allowed history rewrite besides one's own PR branches):
```bash
git switch develop && git fetch origin && git reset --hard origin/main && git push --force-with-lease origin develop
```
Check first that `origin/main` really contains the release commits. Add no commit to `develop` before this is done. If `develop` was deleted on GitHub: `git fetch --prune && git switch -C develop origin/main && git push -u origin develop`.

## Never
`git push origin main`, `git tag`, `git push --tags`, `gh release ...`, `gh pr merge`, repository settings, plain `--force`.
