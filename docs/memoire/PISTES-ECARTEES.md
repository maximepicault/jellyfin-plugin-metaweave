# Pistes écartées
P001 — 2026-11-14 — Plugin-side repair of Jellyfin UserData clashes (placeholder rows): built, then disproved by experiments (Jellyfin already handles it); discarded. See D005.
P002 — 2026-11-11 — Dense list layout for missing.html: the maintainer chose cards with a 16:9 thumbnail (variant A) instead.
P003 — 2026-11-23 — Renumber a missing episode whose number a real episode of another unnumbered season holds (first huge numbers like 6149543… from tag-parsed file names, then 44, 45, 46 behind the other seasons' episodes), and tell the user to put a number in the season folder name: rejected by the maintainer, the typed number must win and no workaround on their side (renumbering and its explanation in commits 4ba6180, 9d7d312, f3e7c13 [vérifié, commit bodies read 2026-11-24: next free number after its own, cached 6149543… numbers dropped, season-folder advice on the card]). See D009.
P004 — 2026-11-23 — Move a clashing missing episode to a positive unused season number: it sorts after every real episode of the season and would match a numbered season with that number. See D009.
P005 — 2026-11-25 — Projection theme direction B "Cinéma" (full-bleed backdrop, text left, season tabs): season tabs need JS, and it looks weak with missing or bright backdrops. See D017.
P006 — 2026-11-25 — Fold watched seasons on the series page: needs JS; jellyfin custom CSS cannot run scripts, the plugin would have to inject into index.html. See D017.
P007 — 2026-11-25 — Seasons as rows of small thumbnails: the maintainer wants large season images. See D017.
P008 — 2026-11-25 — The previous "sleeve" wide season cards of the Projection theme: replaced by the wall of 2:3 posters. See D017.
P009 — 2026-11-25 — Blurred backdrop (`blur(22px)`) behind the detail header of the Projection theme: the maintainer wants to see the picture itself. See D017.
P010 — 2026-11-25 — Glow from the poster for items without backdrop (`background-image: inherit` on a pseudo-element of the poster): stock's `contain: content` clipped it and its `z-index: 3` on the detail card made it paint over the poster and title. See D017.
