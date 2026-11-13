# Contributing

How we work in this repository. It comes down to one idea: **`main` is always buildable and deployable**, because a tag on `main` triggers a release and then an automatic update on the server (see [`deploy/README.md`](deploy/README.md)).

Everything in the repository is written in **English** (code comments, docs, commits, changelog). The plugin's user interface is French for now (see [`CLAUDE.md`](CLAUDE.md), "Language policy").

## Workflow

1. Start from an up-to-date `main` and create a **short-lived branch**: `feat/…`, `fix/…`, `docs/…`, `refactor/…`, `ci/…` or `chore/…`, lowercase, hyphenated, in English (`fix/season-tag-from-path`).
2. Make clean commits (see below), push the branch and open a **pull request** into `main`.
3. CI (`build`) must be green. The merge is a **squash**: the pull request title becomes the commit message on `main`, so it follows the same format as commits.
4. **Never** push directly to `main`, never force-push `main`, never rewrite its history.

### The `develop` branch (maintainer's integration branch)

`develop` is the maintainer's own branch, to try several changes together. Nobody else uses it (the repository has a single
collaborator; outside contributors only work through forks and pull requests, which never touch it).

- Changes are committed to `develop` **directly, one commit per logical change** (no pull request, no squash), so the
  list of commits is kept. The maintainer's assistant does the same. CI builds every push (`build`).
- `develop` reaches `main` through **one pull request `develop` → `main`**, merged with **"Rebase and merge"**: `main`
  gets the same commits one by one (linear history), not a single squashed commit. Each commit keeps its
  Conventional Commits message, so the history of `main` stays readable and each change keeps its own changelog line.
- **After that merge, realign `develop` on `main`** (GitHub recreates the commits with new identifiers, so the old ones
  would otherwise look like duplicates in the next release pull request):
  ```bash
  git switch develop && git fetch origin && git reset --hard origin/main && git push --force-with-lease origin develop
  ```
  Nothing is lost: the content is identical. Do it before adding new commits to `develop`.
- A pull request opened directly against `main` (small fix, documentation) is still **squash-merged** as described above.
- Tags and releases are cut from `main` only, never from `develop`.
- A branch can be deployed for testing from `develop` as well as from any `feat/…` branch: its tip is signed by the
  maintainer's key because commits pushed directly are not rewritten.

## Commit messages

*Conventional Commits*, **in English**, first line at most 72 characters:

```
type(scope): subject in the imperative mood, no final period
```

| Type | For |
|---|---|
| `feat` | a user-visible feature |
| `fix` | a bug fix |
| `docs` | documentation only (README, `docs/`, changelog) |
| `refactor` | restructuring without behaviour change |
| `perf` | performance improvement |
| `test` | tests and the test bench |
| `build` | project file, dependencies, `build.sh` |
| `ci` | GitHub workflows, `deploy/` |
| `chore` | everything else (housekeeping, `.gitignore`…) |

The optional *scope* is short: `people`, `season`, `missing`, `local-source`, `config-ui`, `cache`, `deploy`… An exclamation mark (`feat(api)!:`) flags a breaking change.

```
fix(season): read the tag from the folder name
feat(people): add a cast overlay for series and seasons
docs: summarise the early fix logs in the changelog
```

The optional body explains the **why** when it is not obvious: the symptom, the cause, what was ruled out. One commit = one logical change; no "wip".

## Changelog

Every **user-visible** change adds a line to the **"Unreleased"** section at the top of [`CHANGELOG.md`](CHANGELOG.md), in the same commit (in English). Older entries are history: do not rewrite them.

## Versions and releases

- Numbering is `0.0.x alpha`: `v0.0.9`, `v0.0.10`… (the `vX.Y.Z` shape is mandatory: CI and the update script check it).
- A release is prepared in **its own pull request**: it renames "Unreleased" to `Version 0.0.N alpha (Month Year)`, opens a new empty "Unreleased" section and aligns `<Version>` in `Jellyfin.Plugin.CustomMetadata.csproj`.
- After the merge: `git switch main && git pull && git tag v0.0.N && git push origin v0.0.N`. The tag triggers the release and the server installs it within 15 minutes. **So only tag from `main`, after the checklist below.**

### Before a tag

- [ ] Clean build without warnings (`./build.sh`)
- [ ] Test bench passed for what changed (`tools/ui-test/ui-test.sh`, the `*.mjs` scripts, `deploy/test/test-updater.sh`); the HTML pages are embedded in the DLL, so they must be rebuilt
- [ ] `CHANGELOG.md` up to date
- [ ] Added files reviewed: no secret, machine path, server name or personal address

## Hygiene: the pre-commit checks

The repository ships two *hooks* that prevent silly mistakes. Enable them **once per clone**:

```bash
git config core.hooksPath .githooks
```

- `pre-commit` refuses: `bin/`, `obj/`, DLLs, `.claude/settings.local.json`, files over 1 MB, `/home/<user>` paths, e-mail addresses, tokens and private keys, private IP addresses, and syntax errors in `.sh` and `.mjs` files. To add your own words that must never be published (your server's name, your login…), put them one per line in `.git/forbidden-words`: that file is not versioned.
- `commit-msg` refuses a message that does not follow the format above.

`git commit --no-verify` bypasses both: only use it when you know why.

## GitHub settings to apply once

After creating the repository (Settings):

- **Squash and rebase merging**, no merge commit, with automatic deletion of merged branches. Squash is for ordinary pull
  requests, rebase for the `develop` → `main` release pull request (see above):
  ```bash
  gh repo edit --enable-squash-merge --enable-rebase-merge --enable-merge-commit=false --delete-branch-on-merge
  ```
  (`--delete-branch-on-merge` would delete `develop` after the release pull request: keep a rule or a habit that restores
  it, or turn this option off.)
- **Protect `main`**: pull request required (0 approvals if you work alone), `build` check required, no force-push, no deletion:
  ```bash
  gh api -X PUT repos/OWNER/REPO/branches/main/protection --input - <<'JSON'
  {"required_status_checks":{"strict":true,"contexts":["build"]},"enforce_admins":false,
   "required_pull_request_reviews":{"required_approving_review_count":0},
   "restrictions":null,"allow_force_pushes":false,"allow_deletions":false}
  JSON
  ```
- **Protect `v*` tags** (Settings → Rules → Rulesets → New tag ruleset: target `v*`, block deletion and update): a published tag must never move, since the server deploys it.
- Two-factor authentication on the account that can publish releases.

## Working with an assistant (Claude Code)

The same rules apply, and [`CLAUDE.md`](CLAUDE.md) repeats them for the assistant. In practice: it may create branches, make commits (with a `Co-Authored-By` trailer), push **its own branches** and open pull requests; it never touches `main`, does not merge, does not create tags or releases and does not change repository settings.
