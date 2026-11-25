/*
 * Projection theme - script for jellyfin-web 10.11 (optional; the theme's CSS works without it).
 *
 * Served by the Custom Metadata plugin (CustomMetadata/Theme/projection.js) and added to jellyfin-web's page when the
 * plugin's "Améliorations du thème Projection" setting is on. It only reads through the web client's own ApiClient
 * (the signed-in user's rights) and only adds elements prefixed "pj-": turning the setting off restores stock pages.
 *
 * What it adds:
 * - backdrop at its own ratio: measures the picture so the CSS shows it whole (3:2, 16:9...), never cropped;
 * - episode page: "S1 · É3" above the title, previous / next episode, the episode's own picture as the backdrop
 *   when it is large enough, the current episode marked in "Plus de Saison N";
 * - series page: "625 / 1 076 vus · 17 manquants" with a bar, "Reprendre · S27 É2" on the play button, a progress
 *   bar on each season poster;
 * - season page: the episode number before each title;
 * - hover preview of an episode (pointer devices): its trickplay thumbnails scrubbed, or its preview video (missing
 *   episodes with a preview trailer).
 * Labels are French, like the rest of the theme.
 */
(function () {
    'use strict';
    if (window.__projectionTheme) { return; }
    window.__projectionTheme = '1';

    var NS = 'pj-';
    var warn = function (where, error) { try { console.warn('[projection] ' + where, error); } catch (e) { /* no console */ } };

    // ---------- data, through the web client's ApiClient (cached per id for the life of the page) ----------
    function client() { return window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId() ? window.ApiClient : null; }
    var itemCache = new Map();
    function getItem(id, fields) {
        var key = id + '|' + (fields || '');
        if (!itemCache.has(key)) {
            var c = client();
            itemCache.set(key, c.getJSON(c.getUrl('Items/' + id, { userId: c.getCurrentUserId(), fields: fields || '' })).catch(function (e) { itemCache.delete(key); throw e; }));
        }
        return itemCache.get(key);
    }
    function getItems(query) {
        var c = client();
        return c.getItems(c.getCurrentUserId(), query);
    }
    function token() { var c = client(); return c && c.accessToken ? c.accessToken() : ''; }
    function detailsHref(id) { var c = client(); return '#/details?id=' + id + (c && c.serverId ? '&serverId=' + c.serverId() : ''); }
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) { e.className = cls; } if (text != null) { e.textContent = text; } return e; }
    function nf(n) { try { return Number(n).toLocaleString('fr-FR'); } catch (e) { return String(n); } }
    function epCode(item) {
        var s = item.ParentIndexNumber, e = item.IndexNumber;
        var season = s != null ? 'S' + s : (item.SeasonName || '');
        var ep = e != null ? 'É' + e + (item.IndexNumberEnd ? '-' + item.IndexNumberEnd : '') : '';
        return [season, ep].filter(Boolean).join(' · ');
    }
    function currentId() { var m = /[?&]id=([0-9a-f]{32})/i.exec(location.hash); return m ? m[1].toLowerCase() : null; }
    function currentPage() { return document.querySelector('.page:not(.hide)'); }

    // ---------- 1. backdrop ratio ----------
    var ratioByUrl = new Map();
    function backdropRatio() {
        var img = document.querySelector('.backdropContainer .backdropImage.displayingBackdropImage') || document.querySelector('.backdropContainer .backdropImage');
        if (!img) { return; }
        var m = /url\(["']?([^"')]+)["']?\)/.exec(img.style.backgroundImage || '');
        if (!m) { return; }
        var url = m[1];
        var apply = function (r) { if (r) { document.documentElement.style.setProperty('--pj-bd-ratio', String(r)); } };
        if (ratioByUrl.has(url)) { apply(ratioByUrl.get(url)); return; }
        ratioByUrl.set(url, null);
        var probe = new Image();
        probe.onload = function () { if (probe.naturalWidth && probe.naturalHeight) { var r = probe.naturalWidth / probe.naturalHeight; ratioByUrl.set(url, r); apply(r); } };
        probe.src = url;
    }

    // ---------- 2. detail pages ----------
    function clearOld(page) { page.querySelectorAll('[data-pj]').forEach(function (n) { n.remove(); }); }
    function mark(node) { node.setAttribute('data-pj', ''); return node; }
    // counts what a page got, so a re-render that wiped it is noticed (see detail)
    function counted(page, node) { page.setAttribute('data-pj-count', String(Number(page.getAttribute('data-pj-count') || 0) + 1)); return mark(node); }

    function detail(page) {
        var id = currentId();
        if (!id || !page.classList.contains('itemDetailPage')) { return; }
        var ribbon = page.querySelector('.detailRibbon');
        if (!ribbon || !page.querySelector('.detailRibbon .itemName')) { return; }   // not rendered yet
        // same item and our additions still there: nothing to do (jellyfin-web re-renders the ribbon when the user
        // marks an item watched, which wipes them: then they are added again)
        if (page.getAttribute('data-pj-for') === id && !(Number(page.getAttribute('data-pj-count') || 0) > page.querySelectorAll('[data-pj]').length)) { return; }
        page.setAttribute('data-pj-for', id);
        page.removeAttribute('data-pj-count');
        page.__pjRows = null;
        clearOld(page);
        getItem(id, 'RecursiveItemCount,ChildCount').then(function (item) {
            if (page.getAttribute('data-pj-for') !== id) { return; }
            page.setAttribute('data-pj-type', item.Type);
            if (item.Type === 'Episode') { episodePage(page, item).catch(function (e) { warn('episode page', e); }); }
            if (item.Type === 'Series') { seriesPage(page, item).catch(function (e) { warn('series page', e); }); }
            if (item.Type === 'Season') { seasonPage(page, item).catch(function (e) { warn('season page', e); }); }
        }).catch(function (e) { warn('detail', e); });
    }

    // --- episode page
    function episodePage(page, item) {
        // "S1 · É3" above the title
        var name = page.querySelector('.detailRibbon .itemName');
        var code = epCode(item);
        if (name && code) { name.parentNode.insertBefore(counted(page, el('div', NS + 'epcode', code)), name); }

        // the episode's own picture as the backdrop when it is large enough to fill the header
        ownBackdrop(page, item);

        // previous / next in the whole series, in broadcast order
        if (!item.SeriesId) { return Promise.resolve(); }
        var c = client();
        return c.getEpisodes(item.SeriesId, { userId: c.getCurrentUserId(), fields: 'ParentIndexNumber' }).then(function (res) {
            if (page.getAttribute('data-pj-for') !== item.Id) { return; }
            var list = (res.Items || []);
            var at = list.findIndex(function (e) { return e.Id === item.Id; });
            if (at < 0) { return; }
            var nav = counted(page, el('nav', NS + 'epnav'));
            nav.setAttribute('aria-label', 'Épisodes');
            var link = function (e, dir) {
                var a = el('a', NS + 'epnav-' + dir);
                a.href = detailsHref(e.Id);
                a.appendChild(el('span', NS + 'epnav-dir', dir === 'prev' ? '← Précédent' : 'Suivant →'));
                a.appendChild(el('span', NS + 'epnav-title', [epCode(e), e.Name].filter(Boolean).join(' · ')));
                if (e.UserData && e.UserData.Played) { a.classList.add(NS + 'played'); }
                if (e.LocationType === 'Virtual') { a.classList.add(NS + 'missing'); }
                return a;
            };
            if (list[at - 1]) { nav.appendChild(link(list[at - 1], 'prev')); }
            if (list[at + 1]) { nav.appendChild(link(list[at + 1], 'next')); }
            var buttons = page.querySelector('.mainDetailButtons');
            if (buttons) { buttons.parentNode.insertBefore(nav, buttons.nextSibling); }
            markCurrentInRow(page, item.Id);
        });
    }

    function ownBackdrop(page, item) {
        if (!item.ImageTags || !item.ImageTags.Primary) { return; }
        var c = client();
        c.getJSON(c.getUrl('Items/' + item.Id + '/Images')).then(function (images) {
            var primary = (images || []).find(function (i) { return i.ImageType === 'Primary'; });
            if (!primary || !(primary.Width >= 1280) || page.getAttribute('data-pj-for') !== item.Id) { return; }
            var url = c.getImageUrl(item.Id, { type: 'Primary', maxWidth: 1920, tag: item.ImageTags.Primary, quality: 90 });
            var box = document.querySelector('.backdropContainer');
            if (!box) { return; }
            var layer = box.querySelector('.' + NS + 'ownbackdrop') || box.appendChild(el('div', 'backdropImage displayingBackdropImage ' + NS + 'ownbackdrop'));
            layer.style.backgroundImage = 'url("' + url + '")';
            document.documentElement.classList.add(NS + 'has-own-backdrop');
            ratioByUrl.delete(url);
            backdropRatio();
        }).catch(function (e) { warn('episode backdrop', e); });
    }
    // the episode's own backdrop belongs to its page only
    function dropOwnBackdrop() {
        var page = currentPage();
        if (page && page.getAttribute('data-pj-type') === 'Episode') { return; }
        document.querySelectorAll('.' + NS + 'ownbackdrop').forEach(function (n) { n.remove(); });
        document.documentElement.classList.remove(NS + 'has-own-backdrop');
    }

    function markCurrentInRow(page, id) {
        page.querySelectorAll('.moreFromSeasonSection .card[data-id]').forEach(function (card) {
            card.classList.toggle(NS + 'current', card.getAttribute('data-id') === id);
        });
    }

    // --- series page
    function seriesPage(page, series) {
        var c = client();
        // every episode with its state, in one call (/Items ignores IsMissing under a series: it counted the missing
        // ones as files); a missing episode is a virtual one
        var counts = c.getEpisodes(series.Id, { userId: c.getCurrentUserId(), fields: '', enableImages: false }).then(function (r) {
            var total = 0, played = 0, missing = 0;
            (r.Items || []).forEach(function (e) {
                if (e.LocationType === 'Virtual') { missing++; return; }
                total++;
                if (e.UserData && e.UserData.Played) { played++; }
            });
            return [total, played, missing];
        });
        var nextUp = c.getNextUpEpisodes({ SeriesId: series.Id, UserId: c.getCurrentUserId(), Limit: 1, Fields: 'ParentIndexNumber' }).catch(function () { return { Items: [] }; });
        var seasons = getItems({ ParentId: series.Id, IncludeItemTypes: 'Season', Fields: 'ChildCount' }).catch(function () { return { Items: [] }; });
        return Promise.all([counts, nextUp, seasons]).then(function (all) {
            if (page.getAttribute('data-pj-for') !== series.Id) { return; }
            var total = all[0][0], played = all[0][1], missing = all[0][2];
            // "625 / 1 076 vus · 17 manquants" and a bar, under the facts line
            if (total > 0) {
                var box = counted(page, el('div', NS + 'progress'));
                var bar = box.appendChild(el('span', NS + 'bar'));
                bar.appendChild(el('i')).style.width = Math.round(100 * played / total) + '%';
                var text = nf(played) + ' / ' + nf(total) + ' vus';
                if (missing > 0) { text += ' · ' + nf(missing) + ' manquant' + (missing > 1 ? 's' : ''); }
                box.appendChild(el('span', NS + 'progress-text', text));
                var facts = page.querySelector('.detailRibbon .itemMiscInfo-primary') || page.querySelector('.detailRibbon .itemMiscInfo');
                if (facts) { facts.parentNode.insertBefore(box, facts.nextSibling); }
            }
            // "Reprendre · S27 É2" on the play button once something was watched (the button already starts there)
            var next = (all[1].Items || [])[0];
            var play = page.querySelector('.mainDetailButtons .btnPlay');
            if (next && play && played > 0) {
                if (!play.hasAttribute('data-pj-title')) { play.setAttribute('data-pj-title', play.getAttribute('title') || ''); }
                play.setAttribute('title', 'Reprendre · ' + epCode(next).replace(' · ', ' '));
            }
            // a progress bar on each season poster
            var bySeason = new Map((all[2].Items || []).map(function (s) { return [s.Id, s]; }));
            page.__pjRows = function () { seasonBars(page, bySeason); };
            page.__pjRows();
        });
    }
    function seasonBars(page, bySeason) {
        page.querySelectorAll('#listChildrenCollapsible .card[data-type="Season"][data-id]').forEach(function (card) {
            var s = bySeason.get(card.getAttribute('data-id'));
            // this route gives no PlayedPercentage: episodes seen out of the season's episodes
            var n = s && s.ChildCount, left = s && s.UserData ? s.UserData.UnplayedItemCount : null;
            var pct = n && left != null ? 100 * (n - left) / n : 0;
            if (!s || !pct || pct >= 100 || s.UserData.Played) { return; }   // watched seasons have their tick; untouched ones no bar
            var scal = card.querySelector('.cardScalable');
            if (!scal || scal.querySelector('.' + NS + 'seasonbar')) { return; }
            var bar = mark(el('span', NS + 'seasonbar'));
            bar.appendChild(el('i')).style.width = Math.max(4, Math.round(pct)) + '%';
            scal.appendChild(bar);
        });
    }

    // --- season page: the episode number before each title
    function seasonPage(page, season) {
        return getItems({ ParentId: season.Id, IncludeItemTypes: 'Episode', Fields: 'ParentIndexNumber' }).then(function (res) {
            if (page.getAttribute('data-pj-for') !== season.Id) { return; }
            var byId = new Map((res.Items || []).map(function (e) { return [e.Id, e]; }));
            page.__pjRows = function () { numberEpisodeRows(page, byId); };
            page.__pjRows();
        });
    }
    function numberEpisodeRows(page, byId) {
        page.querySelectorAll('#listChildrenCollapsible .listItem[data-id]').forEach(function (row) {
            var e = byId.get(row.getAttribute('data-id'));
            var title = row.querySelector('.listItemBodyText');
            if (!e || e.IndexNumber == null || !title || title.querySelector('.' + NS + 'num')) { return; }
            if (/^\s*\d+\s*[.\-:]/.test(title.textContent)) { return; }   // jellyfin-web already wrote it ("1. S01E01")
            title.insertBefore(mark(el('span', NS + 'num', String(e.IndexNumber) + (e.IndexNumberEnd ? '-' + e.IndexNumberEnd : ''))), title.firstChild);
        });
    }

    // ---------- 3. hover preview of an episode ----------
    // not on touch screens (a tap opens the episode); any other device can point at a card
    var canHover = !(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
    var hoverTimer = null, preview = null;
    function previewHost(target) {
        // an episode card (rows, next up, continue watching) or a row of a season page
        var card = target.closest && target.closest('.card[data-type="Episode"][data-id], .listItem[data-type="Episode"][data-id]');
        if (!card) { return null; }
        var frame = card.querySelector('.cardScalable, .listItemImage');
        return frame ? { id: card.getAttribute('data-id'), card: card, frame: frame } : null;
    }
    function stopPreview() {
        clearTimeout(hoverTimer); hoverTimer = null;
        if (preview) { preview.stop(); preview = null; }
    }
    function startPreview(host) {
        getItem(host.id, 'Trickplay').then(function (item) {
            if (!host.card.matches(':hover')) { return; }
            if (item.LocationType === 'Virtual' || item.LocalTrailerCount) { return trailerPreview(host, item); }
            if (item.Trickplay) { return trickplayPreview(host, item); }
            return null;
        }).catch(function (e) { warn('preview', e); });
    }
    function overlay(host) {
        var layer = el('div', NS + 'preview');
        layer.setAttribute('aria-hidden', 'true');
        host.frame.appendChild(layer);
        requestAnimationFrame(function () { layer.classList.add(NS + 'preview-on'); });
        return layer;
    }
    function trickplayPreview(host, item) {
        var sources = item.Trickplay, msId = Object.keys(sources)[0];
        if (!msId) { return; }
        var widths = Object.keys(sources[msId]).map(Number).sort(function (a, b) { return a - b; });
        var w = widths.find(function (x) { return x >= 320; }) || widths[widths.length - 1];
        var info = sources[msId][w];
        if (!info || !info.ThumbnailCount) { return; }
        var perTile = info.TileWidth * info.TileHeight;
        var c = client();
        var layer = overlay(host);
        var bar = layer.appendChild(el('span', NS + 'preview-bar'));
        var i = 0, timer = null;
        var show = function () {
            var tile = Math.floor(i / perTile), inTile = i % perTile;
            var col = inTile % info.TileWidth, row = Math.floor(inTile / info.TileWidth);
            var url = c.getUrl('Videos/' + item.Id + '/Trickplay/' + w + '/' + tile + '.jpg', { MediaSourceId: msId, api_key: token() });
            layer.style.backgroundImage = 'url("' + url + '")';
            layer.style.backgroundSize = (info.TileWidth * 100) + '% ' + (info.TileHeight * 100) + '%';
            layer.style.backgroundPosition = (info.TileWidth > 1 ? col * 100 / (info.TileWidth - 1) : 0) + '% ' + (info.TileHeight > 1 ? row * 100 / (info.TileHeight - 1) : 0) + '%';
            bar.style.width = Math.round(100 * (i + 1) / info.ThumbnailCount) + '%';
            // about 10 s for the whole episode, whatever its length
            i = (i + Math.max(1, Math.floor(info.ThumbnailCount / 25))) % info.ThumbnailCount;
        };
        show();
        timer = setInterval(show, 400);
        preview = { stop: function () { clearInterval(timer); layer.remove(); } };
    }
    function trailerPreview(host, item) {
        var c = client();
        return c.getLocalTrailers(c.getCurrentUserId(), item.Id).then(function (trailers) {
            var t = (trailers || [])[0];
            if (!t || !host.card.matches(':hover')) { return; }
            var layer = overlay(host);
            var video = layer.appendChild(el('video', NS + 'preview-video'));
            video.muted = true; video.loop = true; video.playsInline = true; video.preload = 'auto';
            video.src = c.getUrl('Videos/' + t.Id + '/stream', { static: true, api_key: token() });
            var p = video.play(); if (p && p.catch) { p.catch(function () { /* autoplay refused: the frame stays */ }); }
            preview = { stop: function () { try { video.pause(); video.removeAttribute('src'); video.load(); } catch (e) { /* gone */ } layer.remove(); } };
        });
    }
    if (canHover) {
        document.addEventListener('pointerover', function (ev) {
            var host = previewHost(ev.target);
            if (!host) { return; }
            if (host.card.__pjHover) { return; }
            host.card.__pjHover = true;
            stopPreview();
            hoverTimer = setTimeout(function () { hoverTimer = null; startPreview(host); }, 650);
            var leave = function (e) {
                if (e.relatedTarget && host.card.contains(e.relatedTarget)) { return; }
                host.card.__pjHover = false;
                host.card.removeEventListener('pointerout', leave);
                stopPreview();
            };
            host.card.addEventListener('pointerout', leave);
        }, true);
    }

    // ---------- runner: jellyfin-web is a single-page app, so run after every change of view ----------
    var scheduled = null;
    function run() {
        scheduled = null;
        if (!client()) { return; }
        try { backdropRatio(); } catch (e) { warn('backdrop', e); }
        try { dropOwnBackdrop(); } catch (e) { warn('backdrop reset', e); }
        var page = currentPage();
        if (!page) { return; }
        try { detail(page); } catch (e) { warn('detail', e); }
        var id = currentId();
        if (id && page.getAttribute('data-pj-type') === 'Episode') { try { markCurrentInRow(page, id); } catch (e) { warn('row', e); } }
        // lists rendered after the data arrived (seasons, episode rows): idempotent
        if (page.__pjRows && page.getAttribute('data-pj-for') === id) { try { page.__pjRows(); } catch (e) { warn('rows', e); } }
    }
    function schedule() { if (!scheduled) { scheduled = setTimeout(run, 200); } }
    window.addEventListener('hashchange', function () { stopPreview(); schedule(); });
    document.addEventListener('viewshow', schedule, true);
    new MutationObserver(function (records) {
        // our own additions do not count
        for (var i = 0; i < records.length; i++) {
            var t = records[i].target;
            if (!(t.closest && t.closest('[data-pj], .' + NS + 'preview'))) { schedule(); return; }
        }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
    schedule();
})();
