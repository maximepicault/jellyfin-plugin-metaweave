---
name: jellyfin-ui
description: Use whenever you create or change a page of this plugin's UI (Configuration/*.html: config.html, missing.html) or anything it renders. It covers jellyfin-web 10.11 constraints, the user's UI preferences, the propose-variants-then-build workflow, and a mandatory self-critique on real screenshots taken with tools/ui-test.
---

# UI of the Jellyfin plugin pages

The user is French and reviews UI from screenshots, giving precise feedback (e.g. "the card is too wide on large screens, I want 2 per row with a bigger image"). Everything shown to them is in French.

## 1. Before coding: propose, don't guess
- For any non-trivial screen, describe or mock **2–3 variants** (e.g. compact list / medium cards / large cards) and let the user pick. Use the Artifact tool for clickable mockups when visual comparison matters.
- Ask about the target only if it's unknown: main screen resolution, whether the page is also used on mobile or a TV. Check the memory first; the answer may already be there.
- Design for the task, not for decoration. For each screen, write down what the user comes there to do, and make that the most prominent thing:
  - missing episodes → which series and sources are incomplete, then which files to add (hence the "copy file name" button).

## 2. jellyfin-web 10.11 constraints (verified in a 10.11.6 container)
- **Put `<style>` inside the `data-role="page"` div.** jellyfin-web keeps only that div, so a `<head>` style is silently dropped.
- **Jellyfin caps every `<form>` at ~800 px** (`max-width` ≈ 50 em). A multi-column layout inside a form is squeezed unless the page sets `form { max-width: none }`.
- **`[data-role="content"]` has `overflow: hidden`**, which makes `position: sticky` a no-op for a save bar. Set `overflow: visible` on it, scoped under the page id. Offset sticky elements by about 72 px (Jellyfin's fixed header), and give scroll targets `scroll-margin-top`, or `scrollIntoView` hides the title under the header.
- **Choose layouts by available width, not window width.** The drawer takes 240 px. Use `container-type: inline-size` plus `@container` rather than `@media`.
- **Plugin pages are embedded resources**: rebuild after any HTML change (`ui-test.sh --no-build` would redeploy the old page).
- A `<select is="emby-select">` centres its arrow on its `.selectContainer`: keep help text outside that container or the arrow floats up.
- A `required` field in a hidden section blocks the form silently: switch to its section on the `invalid` event.
- Plain checkboxes styled as switches (`appearance: none`) keep working with `.checked`, so existing JS needs no change.
- Scope all CSS under the page id (`#CustomMetadataMissingPage …`) and prefix classes (`me-…`) so they don't leak into Jellyfin.
- Don't put `is="emby-linkbutton"` on text links inside content: it applies button styling (padding, block layout). Use a plain `<a>`. Keep it only for standalone link buttons.
- `emby-input`/`emby-select` generate their own `<label for=…>`, so change the label through that element.
- Jellyfin keeps a page instance alive between visits. JS state (filters, current view) survives navigation, so reset it in `pageshow` when needed.
- `navigator.clipboard` exists only in a secure context (HTTPS or localhost), and the user's Jellyfin is likely served over `http://IP`. Always provide the `execCommand('copy')` fallback, plus "select the text" as a last resort.
- Images: `ApiClient.getImageUrl(itemId, { type, maxHeight })`. Handle a 404 with a delegated `error` listener (capture phase): fall back to another image, then to an initial or icon placeholder.
- Match Jellyfin's dark theme: translucent white surfaces (`rgba(255,255,255,.04–.08)`), accent `#00a4dc`, muted text at 60% opacity. Don't impose a foreign visual identity on an admin page.

## 3. Layout rules learned with this user
- Big screens matter. Never let a card stretch to the full width of a 1920 px screen. Cap columns or the card width, and check at 1440 **and** 1920.
- Episode cards: thumbnail on top in 16:9, 2 per row (3 at ≥ 1800 px, 1 on mobile). Pin the bottom row (file name + copy button) with `margin-top: auto` so it lines up across a row whatever the overview length.
- Drill-down beats walls of data. Show an overview first (cards with a macro recap), and reveal detail only after a click, with a breadcrumb back.
- Every list needs empty, error and "no search match" states with a sentence that says what to do.

## 4. Build → screenshot → self-critique (mandatory before showing anything)
1. `tools/ui-test/ui-test.sh`: build, deploy into a Jellyfin 10.11.6 container (http://localhost:8097, admin/admin), run a full missing-episodes analysis, and take screenshots in `~/.cache/cm-ui-test/shots` at 1440, 1920 and 412 px (mobile). Use `--no-build` to redeploy only. `--stop` stops everything.
   - Never touch the container on port 8096: it belongs to another project.
   - If the screen you changed isn't covered by `tools/ui-test/shoot.mjs`, extend that scenario rather than taking ad-hoc screenshots.
2. **Read every screenshot**, then go through this checklist, fix what fails and take the screenshots again:
   - Alignment: same elements at the same position across cards in a row; baselines of headers and badges.
   - Hierarchy: is the most important information the most visible? Is anything competing with it?
   - Density: no huge empty areas on wide screens, nothing cramped on mobile, no horizontal scroll.
   - Text: no wrong pluralisation ("5 épisode manquants"), no truncation that hides meaning, consistent French wording.
   - States: empty, error and long-text cases (a long title, no overview, no image).
   - Interaction: test clicks with **real mouse events** (CDP `Input.dispatchMouseEvent`). A JS `.click()` is not a user gesture, so clipboard and similar APIs will refuse it.
3. Only then show the user: the screenshots (full paths), what changed, what you checked, and any remaining doubt.
