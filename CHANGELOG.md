# 📝 Changelog - Custom Metadata Plugin

Version history, newest first. It replaces the many fix notes written during development (kept outside the repository): each fix appears here in a few lines.

**Numbering:** `0.0.x alpha`, assigned after the fact to match the Git history. The project had no version number: the "1.0.0" to "1.5.0" entries of the old changelogs correspond to 0.0.1 to 0.0.6, and the work done between February and November, never numbered, is grouped as 0.0.7. Git tags are spelled `v0.0.8`. Ongoing changes go under "Unreleased" (rules: [`CONTRIBUTING.md`](CONTRIBUTING.md)).

## Unreleased

- **Dump refresh is local and fast (fix)**: "Actualiser les éléments" on a dump ran a full Jellyfin refresh of every item, which calls every other provider of the library again (TMDb… over the network), probes the video file and rebuilds the images: seconds per item although everything a dump gives is already in memory. The dump's data is now **written straight onto the item** by the plugin's own provider (same mapping, genre/tag rules, cast overlay), with no Jellyfin refresh: about 5 ms per item instead of 80 to 500 ms measured on 750 MB files, and far more with TMDb enabled. A field the dump does not give is left as it is, and what other providers wrote stays (tags, genres, studios and people are added, not replaced); a locked item is not touched. The log gives the time of each step (files listed, recognised, each item) and a dump entry is logged as "read from the catalogue in memory" instead of an "API CALL GET" that looked like a request.
- **Dump refresh finds files with no tag (fix)**: "Actualiser les éléments" on a dump looked for tagged items only, so it never reached the files the dump recognises by name, and its button did nothing at all (it still looked for the Séries / Saisons boxes a dump does not have, and failed silently). A dump group now offers **Épisodes** and **Films** and a **library picker** (all by default, the choice is remembered): step 1 looks at the files of the chosen libraries and keeps the ones the dump recognises (tag, stored id, bare id, name), step 2 refreshes only those. Files that match nothing, or are tagged for another source, are left alone. Progress shows both steps, and the log tells how many files were looked at, recognised and refreshed.
- **Dump catalogues, navigation by series**: the Catalogues view works like the Séries tab. Level 1 is one card per series inferred from the names (entries that only differ by a trailing number: "Title", "Title 2", "Title - Ep 3"), plus an "Épisodes seuls" card for the others, with search, group filter, sort and "only incomplete series"; a click opens the entries of that series (breadcrumb back, filters, cards or list, "Copier le nom", pagination). The minimum number of entries is configurable (0 = no series, the entries are listed directly). When one entry of a series is already in the library as an **episode of a series**, its card shows the library's series name and seasons. The trailing number is only a hint ("n° 2 d'après le titre": a season or an episode, the title cannot tell) and stays in the copied name (`Title 2 {dp-123}`), so two files never collide when renamed.
- **Dump preview format**: a "Format de l'aperçu" setting (16:9 by default, or 2:3) shapes the series cards, thumbnails and list so a landscape dump is never cropped.
- **Dump images kept apart from Jellyfin**: "Donner les images du dump à Jellyfin" can be switched off, and a dedicated preview picture path (default `cover_url || poster_url`) feeds the coverage screen, downloaded by the server with the source's headers. The refresh follow-up on the Sources tab no longer stops on a busy server.
- **Dump sources** (new source type, imported from the former `Jellyfin.Plugin.DumpMetadata` plugin and rebuilt on this one): ONE call downloads a whole catalogue (`{ "data": [ … ] }`), kept in memory for a configurable time (the previous copy keeps serving if a download fails) and served entry by entry like an API answer, so the mappings, images, headers/retries, taxonomy rules and cast overlay all work unchanged. A file is related to its entry by its `{prefix-id}` tag or, **without any tag, by its name**: a bare `{id}`, the slug, the exact name, then a fuzzy score on the name and on the glued "other titles" (an `S01E03` whose number the entry does not confirm is only proposed, never matched alone). The match is remembered as the item's `custom` id, so refreshes go straight to the entry. Works for **episodes and movie libraries** (the movie provider checks dump sources first, its legacy path is untouched), images included, and Jellyfin's **Identify** screen lists the best candidates.
- **Dump editor**: a "Catalogue" section (where the entries are in the response, id/name/slug/other titles/group/likes paths, cache duration, which matching steps are on, a minimum-score slider, ignored words) and a test panel that downloads the dump with the unsaved form and **shows the candidates and the score of any file name**, with advice when it is under the threshold. A new dump starts with the usual paths. The video mapping gains **Studio** and **"The description contains HTML"** (converted to text: line breaks, bullets, entities), usable by API sources too; the library warning also checks movie libraries when a dump is active.
- **Missing-episodes screen, "Catalogues" view**: a Séries / Catalogues switch (shown when a dump source exists) with totals and coverage, one card per group (brand…) with its owned / total and a progress bar, filters (search, tag, year, owned / missing / all, sort and order, cards or list), removable filter chips, pagination, "Copier le nom" (`Title {prefix-id}`), CSV export of the current filters and a "Recharger le dump" button. "Couverture" on a dump card opens it directly.
- **Genres et tags, rules shared between the two lists**: many terms come from the tags as well as the genres, so a "Reporter les N règles sur les tags" button (and the reverse on the Tags list) copies what you decided (ignore, rename) to the term of the same name on the other side, in one click, as unsaved changes. A term that already has its own rule there is left alone, and a term present in both lists carries an "aussi en tag / en genre" badge.
- **Genres et tags, a more comfortable screen**: the list starts higher (the explanation folds under "Comment s'appliquent ces règles ?", creating a term is one field next to the Genres / Tags pills, which now show their counts). Each line has one-click **Renommer** / **Ignorer** / **Rétablir** buttons instead of a drop-down, a line whose rule is not saved yet is marked in amber and the save bar counts them. The filters are chips with their counts (with a new "Modifiés"), the search bar stays in view while scrolling, and the merge bar is docked above the save bar so it is always visible. Fixed: the confirmation box of "Annuler les modifications" could not open on this page.
- **Genres et tags** (new page, fifth tab of the bar): one place to manage the genres and the tags the plugin gives to your series, seasons and episodes. **Create** a term (kept even if no item uses it yet, offered as a clickable suggestion under the Tags and Genres fields of the local editor), **ignore** it, **rename** it, **merge** several into one (select them, "Fusionner"), and **fix the spelling** of a term Jellyfin keeps several times (see below). Each line shows how many items carry the term and how many times a source sent it.
- **The rules have the highest authority**: they apply to everything the plugin imports, API sources, **local sources** (even a term you typed yourself) and **episodes that have a file**, and to the terms read in a synopsis. A rule acts as soon as a managed item is saved (Jellyfin MERGES the tags of a refreshed item with the old ones, so a dropped term would otherwise stay), and "Appliquer aux éléments" rewrites the items that already hold the old terms, in the background, after telling how many items, terms removed and renamed. Only the terms a rule hits are changed; items the plugin does not manage are never touched.
- **Jellyfin's duplicate genres, what is and is not repaired**: Jellyfin 10.11 stores "Docu", "DOCU" and "docu" as separate entries (same normalised value), so one genre shows several times. The screen groups the spellings of a term with their item counts and "Unifier sous « Docu »" fixes one spelling, rewriting the items. Rewriting links every item to a single entry, **but Jellyfin leaves the old unused entries in its database** (checked: its maintenance tasks do not remove them) and the plugin does not edit Jellyfin's database.
- **Genres: clicking one finds its episodes (fix)**: Jellyfin only creates a genre's own entry when something asks for it, and a click in the interface filters on that entry, so a genre set by the plugin listed no episode. The plugin now creates the entry of every genre of the items it manages (on each refresh, and at the end of "Appliquer"), including names with a `/` or a `-`. A name Jellyfin still cannot open would be marked "ouverture impossible" on the Genres et tags page.
- **Genres and tags: a rename merges with the term it targets (fix)**: renaming "Mauvais" to "zorro" now lands on the existing "Zorro" (spelled as the library has it), and when that term exists under several spellings ("Zorro", "ZORRO") a spelling rule is added so everything ends as one entry.
- **Genres and tags: merges are readable in a long list**: typing a new name shows at once whether it joins an existing term (and how many items it holds); nothing moves while you edit, and once saved the renamed term folds into the row of the term it joins ("Fusionné ici", with "Défaire").
- **Genres and tags: "Masquer les ignorés"** checkbox in the toolbar hides the terms whose ignore rule is saved (a term you have just set to ignore stays visible until saved).
- **Source test, no field left out of the example (fix)**: the example the tree shows for a list (an episode) only had the fields of its most complete element, so a field that exists on other episodes only (a sponsor, an award) was missing and could not be clicked. The example is now that element **completed with every field the other elements have**, nested ones included. The depth went from 6 to 12 levels, a long value (cut at 70 then 200 characters) is cut at 140 on screen and shown whole, up to 2000 characters, in its tooltip, and an object can hold 2000 fields instead of 200.
- **Source test, fold, hide and filter the fields**: in the clickable tree of the answer, an object or a list folds with its arrow (and "Tout replier" / "Tout déplier"), a field one does not care about is hidden with its "×" (it stays hidden at the next test, kept in this browser per source prefix, with a "N champs masqués · Réafficher" link), and a filter box keeps only the fields whose name, path or value match, with their parents. The tree can show up to 400 fields, six levels deep.
- **Source test, big answers come in full**: the "Réponse brute" was cut at 20 Ko and the list of episodes showed only the first 20, which hid the part of a large answer (a long synopsis, an episode with many fields, a whole season) one needs to write a mapping. The raw answer is now kept up to 3 Mo and the list of episodes up to 1000; the boxes scroll, the raw answer shows its size, and a "Copier la réponse brute" link copies it whole.
- **Chapters (time codes) of episodes**: an API or a local source can now give the chapters of an episode. For an episode **with a file**, they become real chapters in Jellyfin (names on the player's bar and in the item page); a time code past the end of the video is ignored. They are written once the refresh is over, because Jellyfin's media probe erases them during a refresh and a metadata provider cannot return chapters. For a **missing episode** (no video) the time codes are added to its synopsis as a "Repères" list, once.
- **Time codes accepted**: `mm:ss`, `m:ss`, `hh:mm:ss`, with a fraction (`12:34.5`), **seconds as a number or as text** (`359`, `"359"`, `359.5`), `6m12s` / `1h02m03s` / `6 min 12 s`, ISO 8601 (`PT6M12S`), and **`00:00` / `0`** for the first chapter (the duration parser refuses zero, a time code must not). A bare number can be read as milliseconds (option "Un nombre seul veut dire").
- **Chapters in the source mapping** (video / episode section, new "Chapitres" block): path of the list (objects with a time and a title field, a list of "12:34 Titre" texts, or one multi-line text), the time and title fields, the meaning of a bare number, and "À défaut, lire les repères dans la description" (the lines of the synopsis that start with a time code, two at least; the synopsis is not changed). The click-to-fill tree works on them like on the other fields.
- **End of a chapter**: Jellyfin only stores the start of a chapter and lets it last until the next one, so a chapter that stops early (a gap before the next one, or before the end of the video for the last) used to swallow the time after it. A source can now give an end: a new optional "Fin de chaque repère" path in the mapping, and `0:00-12:34 Titre` in the local field and in synopsis lines. When a chapter ends before the next one starts (by more than half a second), a **"Hors chapitre"** chapter is added at its end; for a missing episode the synopsis shows the range (`0:00–12:34 Début`). An end past the next start is cut there, an end before the start is ignored.
- **Local sources**: a "Repères (chapitres)" field on the episode form, one per line (`0:00 Introduction`, `359 La crevasse`, `6m12s Suite`). A local episode that overrides an API episode replaces its chapters.
- **Click-to-fill mappings, long answers, fix**: the clickable tree of the source test was built from the raw text of the answer, which the test screen cuts at 20 KB, so a real list of episodes (often far longer) showed "Réponse trop longue pour être cliquable" and the episode-list mapping could not be filled by clicking. The server now sends a trimmed copy of the WHOLE answer (every list cut to ONE element, the most complete one (the one with the most filled fields: the first can lack a synopsis or tags and would hide fields to map), long texts shortened) with the real length of each cut list, and the tree is built from it: a list of 120 episodes inside an object is clickable, the list itself fills "Tableau d'épisodes", and the other fields get their path relative to one episode. The raw text shown under "Réponse brute" is still cut.
- **Local sources, episode identifier**: the "Identifiant" field is now shown on an episode too (it was only offered for series and seasons). It is the end of the tag to put in the file name, and an episode given the identifier of an API episode (`a1v3`, or `dm-a1v3` when the prefixes differ) replaces that episode's fields. Same rules as for the other levels: letters without accent, digits, `_` and `-`, unique among the episodes of the source; an id already taken is refused under the field. Renaming an episode that is already in use means renaming its file, and its greyed virtual episode goes at the next analysis.
- **Local sources, preview video of an episode**: the "Vidéo d'aperçu" field of an episode (sub-tab Images) now takes either a web address or a **video file** sent to the plugin (MP4, WebM or MKV, 500 Mo at most, sent with a progress percentage). An uploaded file has a player in the editor and a "Retirer" link. For a missing episode it becomes the trailer, like a link does: the server reads the file itself through an anonymous endpoint with Range support (random file names only), so Jellyfin probes it (duration, codecs) and plays or transcodes it on every client. The type is checked from the first bytes, not from the file name; a file that is not a video is refused before anything is written. Unused uploads are removed one day after a save, and with their source.
- **Missing episodes, ignore an episode**: an "Ignorer" link on each missing episode ("I will never have it"). It is no longer counted as missing, it is listed apart under the source ("N épisodes ignorés", with "Rétablir"), and its greyed virtual episode leaves Jellyfin at the next analysis. The choice is kept in the plugin's data folder.
- **Missing episodes, compact list**: a "Cartes / Liste" switch on the episodes of a source; the list shows one line per episode (small thumbnail, title, date, file name to copy), for long seasons. Remembered on the browser.
- **Local sources, shorter element form**: the form of a series, season or episode is split into sub-tabs, "Informations", "Images" (with the number of images) and, for an episode, "Distribution" (with the number of people). The sub-tab is kept from one episode to the next and goes back to "Informations" when the kind of element changes.
- **Confirmations inside the page**: the browser's `confirm()` windows (delete a source, change a prefix, drop unsaved changes, delete an element or a person, overwrite with the API, reanalyse without cache…) are replaced by a box in the page, with a title, the consequence, and a button named after the action. A destructive action starts on "Annuler"; Escape cancels.
- **Setup guide**: on the Sources tab, a "Mise en route" list ticks itself (source created, source enabled, a series folder tagged, plugin ticked in the library, first analysis of the missing episodes) and explains the next step. It disappears once everything is done, or with "Masquer".
- **People, merge two entries**: "Fusionner avec une autre fiche" in a person's form (entries with the same name are offered first). The other entry disappears into this one: its identifiers come along, its photo and biography fill the empty fields, and every cast that named it (API episodes and local sources) now names the kept entry. Then "Appliquer aux épisodes" updates Jellyfin.
- **Diagnostic page** (new, fourth tab): "why is this element not enriched?". Paste a file or folder name and the plugin replays the chain in six steps (tag in the name, source of the prefix, library providers, call of the source without cache, fields found, images), stops at the first one that blocks and offers the matching action (open or test the source, open the libraries). Below, "Vérifier la médiathèque" lists what the plugin could not take in charge, with no call to the sources: an untagged episode or season inside a tagged series, an unknown or disabled prefix, a library where the plugin's providers are unticked, a series or season that was never enriched. Read-only.
- **Mappings filled by a click**: after a test in the source editor, the answer is shown as a clickable tree at the top of the result. Click in a mapping field, then on a value: its path is written into the field and the next empty field is selected. An episode-list field gets a path relative to one element of the list, and a tags or genres field takes every element (`[]`). An answer cut at 20 KB is not clickable.
- **One tab bar on every page of the plugin** (Configuration, Épisodes manquants, Personnes, Diagnostic) with counters: sources, missing episodes (red; amber when a source is in error), people, elements not enriched. It replaces the two buttons at the top of the configuration page and the "back to the configuration" links. The tabs of a page itself (Sources / Réglages / Configuration héritée, Annuaire / Distribution) are now second-level pills.
- **Interface review**: counts are written with real plurals instead of "épisode(s)" on the three pages; a required field left empty in the source editor now shows "Ce champ est obligatoire." under the field; the path syntax help of the four mapping sections is a short collapsible "Syntaxe des chemins" list instead of a five-line paragraph; the pagination field lines up with its two neighbours.
- **Sources tab**: the options of "Actualiser les éléments" are plain checkboxes introduced by "Actualiser dans Jellyfin :" (they looked like the "Activée" switches), "Images" is named "Remplacer les images", and the prefix is no longer repeated on each card of a group.
- **Missing episodes page**: the card lines no longer end with a lone "·", the "back to the configuration" link sits above the title as on the Personnes page, and the error of a source whose list could not be fetched says what to do instead of naming a log tag.
- **Personnes page**: the "Ajouter une personne" panel no longer stretches across a 1920 px screen, the cancel button fits on one line on a phone, and the notice button reads "Fermer" once the changes were applied.
- **Configuration page, fix**: the page no longer writes the whole configuration, API keys included, to the browser console (leftover debug traces).
- **Local sources, fix**: an image uploaded for an element whose identifier has an accented letter could be stored under a file name the plugin then refused to read.
- **Configuration page**: a warning at the top of the Sources tab when a TV (or mixed) library does not have the plugin's providers ticked for series, seasons or episodes (`Custom Metadata Provider`, `Custom Image Provider`). It names the library and the types, says what does not arrive (metadata never; images only through "Actualiser les éléments") and where to tick them. Jellyfin gives no hint of this, and a library created before the plugin was installed is in that state. Read-only: nothing is changed in the library.
- **Refresh with images, fix**: the images applied by "Actualiser les éléments" were set on the item in memory but **never written to Jellyfin's database**. They showed for a while, then the poster and the backdrops **vanished** as soon as Jellyfin reloaded the item (a restart, or simply a big library pushing it out of the cache), with nothing in the log. This hit every case where the plugin applies the images itself: a library whose image fetchers do not include the plugin, or an image file already in the media folder. They are now saved. An image of the source that can no longer be read is skipped for the next one (then the image sub-folder) instead of leaving the item without image.
- **Refresh with images**: when an old image file (`poster.jpg`, `backdrop.jpg`…) is still in a media folder although another image was applied, the result line under the button says so in amber, and the server log lists the paths: Jellyfin takes that file again at the next library scan, so it has to be removed or moved into the image sub-folder.
- **Logs**: for a local source, a field left empty is no longer logged as a warning (`path 'poster' NOT FOUND`, about ten lines per refreshed item); it is a Debug line. API sources keep the warning.
- **Refresh with images**: "Actualiser les éléments" now applies the plugin's images itself (Primary, Thumb, Logo, Banner, all the backdrops, and the images of the `Primary/`, `Logo/`… sub-folders when the API and the local source give none). Before, an image file already sitting in the series folder (`poster.jpg`) kept its old poster whatever the refresh did. The log says what was applied.
- **Local sources, image gallery**: the first image of each type is the **default** one, and another is chosen by **selecting it and clicking "Utiliser comme image par défaut"**, and the **backdrops are a carousel**: "Appliquer les images" (and a refresh with images ticked) now applies all the backdrops of a series or season (API, gallery, `Backdrop/` sub-folder), not just the first.
- **Image sub-folders**: images placed in sub-folders of a tagged series or season folder, named `Primary`, `Backdrop`, `Logo`, `Banner` or `Thumb` (or `Principal`, `Fond`, `Bannière`, `Miniature`), are now offered with a preview in Jellyfin's "Modifier les images", without any other step. Those folders no longer show up as empty seasons in the library. In a local source, an **"Importer depuis les dossiers"** button (never automatic) copies them into the gallery of the series or season.
- **Local sources**: a **gallery of images** per series, season or episode (several images per type: poster, backdrop, logo, banner, thumbnail; uploaded files or web addresses). They are all offered, with a preview, in Jellyfin's "Modifier les images", so image files no longer clutter the series folders. A new **"Appliquer les images"** button (never automatic) replaces the images of the element and of its children carrying the prefix with the default ones.
- **Targeted refresh by prefix**: the Sources tab now groups the cards **by prefix** (an API source and a local source that share one sit together), and each group has an **"Actualiser les éléments"** button with Séries / Saisons / Épisodes switches (all on) and an Images switch (on by default: the images are replaced too; untick it to replace only the metadata). It finds, in the library, the items that carry that prefix and refreshes only them, in the background, one at a time, with a progress line ("12 / 40 actualisés"): no scan of the media folders. The API is called first and the local source of the same prefix is laid over it, so one refresh updates both. It is never automatic. Refreshing seasons does not refresh their episodes: tick Épisodes for that. Not done: a per-element button in the local editor, and refreshing the children of one given season only (the service supports it: `Tag` + `IncludeChildren`).
- **Local sources**: a series and its season (or an episode) may now carry the **same id**. APIs number them separately (series `1234` and season `1234`), and the editor refused the second one. Ids are unique per level (two seasons cannot share one) instead of across the whole source, in the editor and on the server (where a duplicate used to be replaced by a generated id on save). The editor designates elements by level and id internally, so the two are told apart in the tree and in the form.
- **Local sources**: the id of a series, season or episode may now contain hyphens (`3f2a-91bc`, `ma-serie-2024`): API ids are often UUIDs or slugs, and the editor refused them (the id went back to the generated one, so the change looked ignored) and the server replaced them with a generated id when the content was saved. A hyphenated id whose first part is the prefix of a configured source is still read as that source's full tag (`dm-123`), anything else is the node's own id. A refused id is now explained under the field (with the reason) instead of in a toast that is easy to miss, and the field goes back to the id in use.
- **Missing episodes**: a refused removal no longer stops the analysis. When Jellyfin cannot delete a virtual episode (for example its database rejecting the move of a play history: `UNIQUE constraint failed: UserData…`), the plugin logs a warning with the reason, leaves that episode in place, goes on with the next ones, and the final summary line counts the failures. A season that fails for any other reason is logged and skipped instead of aborting the whole analysis. To delete the virtual episodes, switch off "Ajouter les épisodes manquants à la médiathèque", save, and run "Analyser maintenant".
- **Custom headers**: each header now has a **switch** (Réseau tab). A switched-off header stays in the configuration but is not sent, so it can be tried and put back without retyping it. The row is dimmed and the "en-têtes" counter only counts the headers that are on. A configuration saved before this option has every header on.
- **Custom headers**: a value containing quotes is now sent exactly as typed. .NET refused some of them (`Cache-Control: max-age="10"`, `Accept-Language: fr;q="0.9"`) and the plugin dropped the whole header, with only a warning in the log. Custom headers are now added without that validation; a header that cannot be set at all (invalid name) is reported in the log.
- **API calls**: a **CORS rejection** (HTTP 403 whose body talks about CORS, such as Spring's "Invalid CORS request") is now handled. The call is retried once with the opposite Origin/Referer choice (without them if they were sent, with them otherwise), and the choice that worked is kept for that source until its Origin/Referer setting is changed. It applies to the API calls, to the image downloads and to the images Jellyfin fetches through the plugin. When both choices are refused the source test says so and tells which setting to change (Réseau tab: Origin).
- **Local sources**: a local source can now share its **prefix with an API source** to complete or override it. The tag (`{fny-1234}`) is resolved to the API source first (it is called as usual), then the local series/season/episode with the **same plain id** (`1234`) is laid over its answer. Before, a local source needed an id in the `prefix-id` form to override anything, and two sources with the same prefix resolved to whichever came first (the local one, so the API was never called). The API source now always wins that tie; the "Synchroniser la liste" button also appears for a plain id when an API source shares the prefix; the distribution screen keeps listing the episodes of a prefix shared with an API source.
- **Local sources**: an episode imported with "Synchroniser la liste" no longer freezes a copy of the API data. It remembers the values the API had (`ApiBase`) and only the fields **you change** override the API; the others keep following the live API (before, the copy made at import hid every later API change, so only the local data showed after a refresh). A new synchronisation updates the untouched fields and keeps yours; the new "Remettre les valeurs de l'API" button discards your changes. Episodes imported with the previous version have no snapshot: use that button once to get back to the API values.
- **Local sources**: when a local series or season overrides an API one, tags, genres and cast are now **added together** (local first) instead of the local list replacing the API's; title, overview, date and rating are still the local ones when filled in. The log (`🗂️ [LOCAL SOURCE]`) says what the API and the local node each gave.
- **Source test**: a heavy image is no longer refused in the preview ("image trop lourde pour l'aperçu"): it is reduced to a small thumbnail by the server (320 px, JPEG) before being sent to the page.
- **Local sources**: a local season that overrides an API season no longer **replaces** the API's episode list when the API has one. The API list stays the base: a local episode named with an API tag (`dm-a1v4`) overrides that episode's fields, any other local episode is added under its own tag, and nothing of the API goes away. Only an API **without** a list endpoint still takes the local list as is. The missing-episodes screen copies the right tag for each episode.
- **Source test**: the test button of an API source now **shows the images** (the first six, downloaded by the plugin the way Jellyfin will, with the source's headers and a relative path completed with the base address) instead of listing their addresses only. An image that cannot be downloaded says why.
- **Local sources**: a local season whose id is an API season's tag gets a **"Synchroniser la liste"** button (never automatic). It fetches that season's episode list from the API and adds the missing episodes with their API tag (`dm-a1v1`), fills the empty fields of those already there and never overwrites what was typed; nothing is saved until "Enregistrer". A local episode with an API tag overrides that API episode's metadata (title, overview, date, number, thumbnail…), including for files already in the library, and stands for it in the missing-episodes analysis.
- **API sources**: the mapping panes now explain the `[0]` notation (an API that answers with a list), and a list answer logs a warning when the paths do not start with `[0]`. The paths themselves already handled `[n]` and `||` in every mapping.
- **Local sources**: the id of a local series or season can now be changed. Giving it the tag of an element of an API source (`dm-123`) makes the local element **replace** it: its title, overview, dates, tags, genres and images win over the API's (what it leaves empty keeps the API value), and for a season its episodes become the list of missing episodes, even when the API has no episode-list endpoint. The virtual episodes carry the local source's tags (`{loc-abc}`), and "Copier le nom" uses that prefix.
- **Cast distribution**: series, seasons and episodes **without any `{prefix-id}` tag** are now listed too (a series with no tag at any level used to be missing from the library list). Their cast is attached to a fallback key made from their Jellyfin id, and written straight into Jellyfin by "Appliquer aux épisodes" (and by the daily task), since no provider of the plugin ever sees them. The cascade works from them too. Their distribution follows the folder or file: if it is renamed or moved, it has to be redone.
- **Cast**: a person can now hold several jobs on the same element (actor AND director, for example). Cast entries are unique per person **and** job instead of per person, in the distribution screen, in the episodes of local sources and when a series or season cascades its cast: before, a person already present on an episode made the series' entry for them disappear from it, and adding them a second time was refused. Adding someone already there takes the next free job; the suggestions show what they already do.
- **Cast distribution**: the series are now real entries of the library list (a card with an arrow, their seasons listed under them) instead of small uppercase headings that did not look clickable. A series or a season that carries a tag but has no episode in the library is listed too, so its cast can be set.
- **JSON paths**: fallback between fields with `||`. `title || name || label` uses the first path that exists and is not null, empty or blank, so an API whose fields vary from one item to the next can be mapped (works for every field of every mapping, with list positions and the `[]` notation too). The editor says so in each mapping section.
- **Local sources**: the cast field of an episode now has the same dropdown as the distribution screen (photo, name, identifiers, filtered as you type, arrow keys, a click adds the person) instead of the browser's plain list. The directory is read again when the field gets the focus, so people added in the Personnes screen show up without reopening the source.
- **Local sources**: people are no longer shown as "(personne supprimée)" while the directory is loading or when it could not be read: the row says "Chargement…" or "(annuaire illisible)".
- **People sources**: a new optional "Adresse des photos" field for APIs that return the photo as a **path** (`/abc123.jpg`) hosted on another address than the API (an image server). A prefix (`https://images.example.com/p/w185`) or a template with `{value}` (`https://images.example.com/{value}?w=200`). Left empty, a path is still completed with the API's base URL, and a full address is used as it is.
- **Cast distribution**: adding a person now uses a dropdown with a preview (photo, name, identifiers) filtered as you type, with keyboard navigation, instead of the browser's plain list.
- **Cast distribution**: the library listing (slow on a large library) is no longer loaded when the tab opens. The tab starts on "Par identifiant", and a "Parcourir la médiathèque" button loads the series, seasons and episodes on demand.
- **Cast distribution**: a "cascade" checkbox on a series or a season (saved with its distribution). Everything below it, seasons and episodes, missing ones included, inherits its cast when enriched, without needing the library listing. The episodes' own cast is kept and comes first. A preview lists the season and episode tags it reaches, and "Appliquer aux épisodes" also refreshes them (and cleans them when the cascade is turned off).
- **Cast distribution**: the cascade also reaches seasons and episodes whose name has **no identifier**: their inherited cast is written straight into Jellyfin (nothing else of theirs is touched), kept up to date by "Appliquer aux épisodes" and by the daily missing-episodes task, and taken back when the cascade is turned off.
- **Cast distribution**: "Appliquer aux épisodes" now also refreshes an element whose distribution was emptied entirely, which was skipped before.
- **JSON paths**: a position in a list can now be written `[0]` (`[0].name` for a list at the root, `results[0].name` for a list inside an object). It makes an API usable when it answers a search URL with a list of one element, notably for people sources, and it applies to every mapped field. A people source that gets a list without `[0]` now says so instead of "no name found".

## Version 0.0.8 alpha (November 2026)

New interface, local sources, a people directory, cast distribution and automatic updates.

### ✨ New
- **Redesigned configuration screen**: Sources / Settings / Legacy tabs, source cards, a full-page editor with side navigation, a test panel on the right and a sticky save bar.
- **Redesigned "Missing episodes" screen**: series → sources → episodes, search, "Copy name" / "Copy the N names".
- **Local sources**: series, seasons and episodes typed in the plugin (including upcoming episodes), image upload, import of a list of titles. They are not erased by a forced Jellyfin refresh.
- **People directory** ("Personnes" screen): people sources queried by identifier, deduplication by identifier and by name, photos, cast for local episodes, and cast for series, seasons and episodes of API sources by tag or by a typed identifier, without modifying the API. "Apply to episodes" button.
- **Multi-tag files** (`Ep {dm-1} {dm-2}.mkv`): merged metadata, episode range, episodes already present are no longer reported as missing.
- **Shortcuts in the administration menu** (under Extensions).
- Optional **automatic update** from the repository's releases (`deploy/`), with backup and rollback.
- HTML entities are decoded (`&amp;`, `&quot;`, including without a semicolon).

### 🐛 Fixed
- Saving a source reset the fields missing from the form (cast, year, banner) to their defaults, and `ImagesArray` was never saved.
- The season provider looked for its tag in a name Jellyfin had already cleaned, so it never found an identifier. A season's API metadata (title, overview) now applies on refresh.
- Missing episodes pick up their cast again at analysis time when the directory changes.

### ⚠️ Good to know when updating
- Season names may change (API metadata is now applied).
- The first "Analyze" reprocesses every missing episode: pick a quiet moment.
- A people source is never selected by a media file's tag: its prefix is only used for its identifiers.
- Back up the plugin's data folder: local sources and the directory exist nowhere else.

## Version 0.0.7 alpha (February – October 2026)

Intermediate work that was never numbered at the time, grouped here.

- **Missing episodes**: per-source "episode list" endpoint; greyed-out virtual episodes with a free episode number (without which Jellyfin deletes them at every scan), thumbnail downloaded with the source's settings, duration (`mm:ss`, `hh:mm:ss`, seconds, ISO 8601), preview video playable as a local trailer, per-season cache, "reprocess everything", daily scheduled task.
- **Robust networking**: per-attempt timeout, retries with growing delays (timeout, network error, 408, 429, 5xx, `Retry-After`), per-source concurrent-request limit. The identity sent is adjustable (User-Agent, `Origin`, `Referer`, HTTP/2). "Test source" button.
- **Images**: an episode's thumbnail is also offered as its primary image (otherwise Jellyfin takes a frame of the video); images are downloaded with the source's headers; optional conversion of portrait thumbnails to 16:9; 403/404 responses are logged as warnings.
- **Cache**: a manual refresh ignores the cache (an API change finally shows up); cache keys are prefixed with the source.
- **Overview written as a tag list** (`OverviewListTarget`): detected and moved to the tags or genres.
- **Episode number** read from the API (configurable field); providers always return the numbers already known (a full replace used to reset them to null and delete the virtual episodes).
- **Cast restored** with Jellyfin 10.11's API (`AddPerson`), after its removal in 0.0.6.
- The plugin's identifier is read **case-insensitively** (`custom` / `Custom` key depending on where the item comes from) and stays visible under "External IDs".

## Version 0.0.6 alpha (February 2026)

Multi-source, Jellyfin 10.11 compatibility and a series of critical fixes.

### ✨ Multi-source
- **Several sources** in one installation: each has its own prefix, URL, endpoints, mappings, API key and headers. The prefix of the `{PREFIX-ID}` tag selects the source (case-insensitive; enabled sources only).
- The **cache** is split per source (`MV1-EPISODE-123.json`) and its files are **readable** (`SERIES-1004855103.json`, `IMAGES-SERIES-…`) instead of an MD5 hash.
- **Backward compatibility**: an old single-source configuration (global prefix, URL, endpoints) keeps working as long as no source is defined.

### 🔧 Jellyfin 10.11 compatibility
- `Jellyfin.Controller` / `Jellyfin.Model` packages moved to 10.11; `Nullable` disabled (nullable warnings blocked the build).
- `Episode.People`, `Series.People`, `Movie.People` and `EpisodeInfo.SeriesName` no longer exist: cast is suspended (restored since, see 0.0.7). The old "per-source" provider is removed.

### 🐛 Critical fixes
- **Wrong endpoints**: the season provider called a hard-coded route (`/api/metadata/…`) instead of the configured endpoint, hence 404s even though `curl` worked. Series, season and episode now use their own endpoints.
- **Wrong JSON mapping**: the series or season mapping was determined and then never passed on; everything went through the video mapping.
- **Episodes without an identifier**: the ID was not extracted from the file name (`Episode {mv-EP001}.mkv`).
- **Name truncated by `(2025)`**: the tag was looked up in `info.Name` (the title parsed by Jellyfin) instead of the file path.
- **Invalid image ID** for seasons and episodes (`1004855103/season//episode/` → 404): the ID now comes from the file or folder name, then from `ProviderIds`. Season/episode images ask for their own image fields (a series-only mapping is not enough).
- **Strict regular expression**: a tag only accepts letters, digits, `_` and `-` (`{mv-123 with text}` no longer matches).
- **HTTP 500 on the configuration page**: a `Dictionary` cannot be serialized to XML; custom headers are now a key/value list.
- Compatibility overload of `GetMetadataAsync` (calls without a source) and cleanup of orphaned code (89 compile errors); `async` and logging warnings.
- Much more detailed **logs** (cache, images, ID extraction, the URL called), prefixed by component.

## Version 0.0.5 alpha (February 2026)

### 🐛 CRITICAL BUG FIXED - ID extraction from the full path

**The problem:** every provider looked for `{PREFIX-ID}` in the **FULL path** instead of the file/folder name only, so they always found the **FIRST ID** in the path.

**Example:**
```
Path: /var/media/Series {mv-S123}/Season {mv-SOURCE}/Episode {mv-EP001}.mkv

BEFORE:
- Series provider  → searches the whole path → finds "S123" ✅
- Season provider  → searches the whole path → finds "S123" ❌ (should be "SOURCE")
- Episode provider → searches the whole path → finds "S123" ❌ (should be "EP001")
```

**The fix:** `Path.GetFileName()` extracts **only the last segment** of the path; each provider now searches its own file or folder name only.

```
- Series provider  → searches "Series {mv-S123}"          → finds "S123" ✅
- Season provider  → searches "Season {mv-SOURCE}"        → finds "SOURCE" ✅
- Episode provider → searches "Episode {mv-EP001}.mkv"    → finds "EP001" ✅
```

### ✨ Logging improvements

**One emoji per provider:**
- 📺 `[SERIES PROVIDER]` - series logs
- 📁 `[SEASON PROVIDER]` - season logs
- 🎬 `[EPISODE PROVIDER]` - episode logs

**Detailed extraction logs:**
```
📺 [SERIES PROVIDER] Extracted folder name: My Series {mv-S123} from path: /var/media/...
📺 [SERIES PROVIDER] ✅ Extracted series ID 'S123' from folder name
```

### 🔧 Other fixes

- **Stricter regex**: captures only valid IDs (letters, digits, hyphens, underscores)
  - BEFORE: `\{mv-([^}]+)\}` captured EVERYTHING between the braces (`{mv-123 with text}` → `"123 with text"` ❌)
  - NOW: `\{mv-([\w\-]+)\}` captures valid characters only (`{mv-123}` → `"123"` ✅, `{mv-123 text}` → no match)
- Added manual ID extraction for series (fallback when `CustomExternalId` does not work)
- Better logs to trace exactly what is extracted and from where
- Logs showing the regex pattern used and the exact match

---

## Version 0.0.4 alpha (February 2026)

### ✨ New: tags as an array of objects

**Tags can now be objects, with a field extracted from each.**

**Notation: `tagList[].label`**

**BEFORE:**
- ✅ Array of strings: `["fantasy", "sci-fi"]`
- ❌ Array of objects: `[{"label": "fantasy"}]` → did not work

**NOW, two notations are supported:**

**`[]` notation (recommended):**
```
Tags: tagList[].label
```

**Two-field notation (alternative):**
```
Tags: tagList
Tag field: label
```

**Both behave the same**; the `[]` notation is more intuitive.

**Supported formats:**
- ✅ Array of strings: `["fantasy", "sci-fi"]`
- ✅ Array of objects: `[{"label": "fantasy"}, {"id": 123, "label": "sci-fi"}]`
- ✅ Comma-separated string: `"fantasy, sci-fi"`
- ✅ JSONPath-like notation: `tagList[].label`, `metadata.tags[].name`

**Supported JSON example:**
```json
{
  "tagList": [
    {"id": 4420, "count": 1, "label": "fantasy"},
    {"id": 4421, "count": 5, "label": "sci-fi"}
  ]
}
```

**Documentation:** the `GUIDE_TAGS.md` guide was updated.

---

## Version 0.0.3 alpha (February 2026)

### ✨ New: full image configuration

**Image mappings were added to the configuration interface.**

**What is new:**
- ✅ Image URLs configured per item type (videos / sources / series)
- ✅ Direct URLs: `poster`, `backdrop`, `thumb`, `logo`, `banner`
- ✅ Image arrays with type/width/height
- ✅ Thumbnails for episodes
- ✅ Images extracted from the same JSON as the metadata
- ✅ Separate mappings per item type

**Configurable image types:**
- **Videos/Episodes**: Poster, Backdrop, Thumb, Logo
- **Sources/Seasons**: Poster, Backdrop
- **Series**: Poster, Backdrop, Logo, Banner

**Documentation:** new guide `GUIDE_IMAGES.md`.

---

## Version 0.0.2 alpha (February 2026)

### ✨ New: configurable prefix

**The tag's prefix can now be customised.**

**BEFORE:** fixed pattern `{custom-ID}`

**NOW:** configurable pattern: `{custom-ID}`, `{myappsource-ID}`, `{youtube-ID}`, etc.

---

## Version 0.0.1 alpha (February 2026)

### ✨ Initial features

- ✅ Metadata enrichment for movies / series / episodes
- ✅ Image download (posters, backdrops)
- ✅ Two-level cache (memory + disk)
- ✅ Endpoints configurable per type
- ✅ Configurable JSON mappings
- ✅ Three levels of IDs: video, source, series
- ✅ Configurable source name
