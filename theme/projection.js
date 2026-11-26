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
 * - person page: the left column (portrait, birth, links, what the library holds), the jobs over the name, counts
 *   on the section titles, the backdrops as a banner;
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
        // same item and our additions still there: nothing to add (jellyfin-web re-renders the ribbon when the user
        // marks an item watched, which wipes them: then they are added again). The header is checked on its own:
        // there is one for the whole document, so on a page jellyfin-web kept and shows again (back button) it has
        // been removed, or built for another page, meanwhile
        if (page.getAttribute('data-pj-for') === id && !(Number(page.getAttribute('data-pj-count') || 0) > page.querySelectorAll('[data-pj]').length)) {
            if (page.__pjItem) { canvasHeader(page, page.__pjItem); }
            return;
        }
        page.setAttribute('data-pj-for', id);
        page.removeAttribute('data-pj-count');
        page.__pjRows = null;
        page.__pjItem = null;
        clearOld(page);
        page.classList.remove(NS + 'person');
        page.querySelectorAll('.' + NS + 'person-count').forEach(function (n) { n.remove(); });
        getItem(id, 'RecursiveItemCount,ChildCount').then(function (item) {
            if (page.getAttribute('data-pj-for') !== id) { return; }
            page.setAttribute('data-pj-type', item.Type);
            page.__pjItem = item;
            // a page already left gets its header when it is shown again (above), not now over the visible one's
            if (!page.classList.contains('hide')) { canvasHeader(page, item); }
            identity(page, item);
            if (item.Type === 'Episode') { episodePage(page, item).catch(function (e) { warn('episode page', e); }); }
            if (item.Type === 'Series') { seriesPage(page, item).catch(function (e) { warn('series page', e); }); }
            if (item.Type === 'Season') { seasonPage(page, item).catch(function (e) { warn('season page', e); }); }
            if (item.Type === 'Person') { personPage(page, item).catch(function (e) { warn('person page', e); }); }
        }).catch(function (e) { warn('detail', e); });
    }

    // --- episode page
    function episodePage(page, item) {
        // "S1 · É3" above the title
        var name = page.querySelector('.detailRibbon .itemName');
        var code = epCode(item);
        if (name && code) { name.parentNode.insertBefore(counted(page, el('div', NS + 'epcode', code)), name); }

        // "Aperçu" next to the play button when the episode has a preview video (its source's, or the clip built by the
        // plugin); a missing episode already has one through its play button
        if (item.LocationType !== 'Virtual') {
            previewInfo(item.Id).then(function (info) {
                var buttons = page.querySelector('.mainDetailButtons');
                if (!info || !(info.Source || info.Clip) || !buttons || buttons.querySelector('.' + NS + 'previewbtn') || page.getAttribute('data-pj-for') !== item.Id) { return; }
                var btn = counted(page, el('button', NS + 'previewbtn button-flat detailButton emby-button'));
                btn.type = 'button'; btn.title = 'Aperçu · ' + previewOrigin(info).label;
                btn.appendChild(el('span', 'material-icons detailButton-icon movie')).setAttribute('aria-hidden', 'true');
                btn.addEventListener('click', function () { openPreview(item, info); });
                var play = buttons.querySelector('.btnPlay:not(.hide), .btnResume:not(.hide)');
                buttons.insertBefore(btn, play ? play.nextSibling : buttons.firstChild);
            });
        }

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

    // --- person page ("Fiche d'artiste"): who on the left (CSS: .pj-person-card), what they did on the right
    var JOBS = [['Interprétation', ['Actor', 'GuestStar']], ['Réalisation', ['Director']], ['Scénario', ['Writer']], ['Production', ['Producer']], ['Musique', ['Composer']]];
    var WORKS = [['Movie', 'film', 'films'], ['Series', 'série', 'séries'], ['Episode', 'épisode', 'épisodes']];
    // a birth or death date is a calendar day: read in UTC, or it moves a day back west of Greenwich
    function day(value) { var d = value ? new Date(value) : null; return d && !isNaN(d.getTime()) ? d : null; }
    function dayText(d) { return d.toLocaleDateString('fr-FR', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }); }
    function years(from, to) {
        var n = to.getUTCFullYear() - from.getUTCFullYear();
        if (to.getUTCMonth() < from.getUTCMonth() || (to.getUTCMonth() === from.getUTCMonth() && to.getUTCDate() < from.getUTCDate())) { n--; }
        return n;
    }
    function personCard(person) {
        var card = el('aside', NS + 'person-card');
        card.setAttribute('aria-label', person.Name);
        var photo = card.appendChild(el('div', NS + 'person-photo'));
        var tag = person.ImageTags && person.ImageTags.Primary;
        if (tag) {
            // the photo's own shape, between a 2:3 portrait and a square
            if (person.PrimaryImageAspectRatio) { photo.style.setProperty('--pj-photo-ratio', String(Math.min(1, Math.max(0.667, person.PrimaryImageAspectRatio)))); }
            var img = photo.appendChild(el('img'));
            img.alt = ''; img.src = client().getImageUrl(person.Id, { type: 'Primary', tag: tag, maxHeight: 900, quality: 92 });
        } else {
            var initials = (person.Name || '').split(/\s+/).map(function (w) { return w.charAt(0); }).join('').slice(0, 2).toUpperCase();
            photo.appendChild(el('span', NS + 'person-initials', initials)).setAttribute('aria-hidden', 'true');
        }
        var facts = card.appendChild(el('dl', NS + 'person-facts'));
        var fact = function (label, text) {
            var row = facts.appendChild(el('div'));
            row.appendChild(el('dt', null, label));
            return row.appendChild(el('dd', null, text));
        };
        var born = day(person.PremiereDate), died = day(person.EndDate);
        if (born) { fact('Naissance', dayText(born) + (died ? '' : ' · ' + years(born, new Date()) + ' ans')); }
        if (died) { fact('Décès', dayText(died) + (born ? ' · à ' + years(born, died) + ' ans' : '')); }
        var place = (person.ProductionLocations || [])[0];
        if (place) { fact('Lieu de naissance', place); }
        var links = (person.ExternalUrls || []).filter(function (l) { return /^https?:\/\//i.test(l.Url || ''); });
        if (links.length) {
            // jellyfin-web's own classes: the theme's chips and the plugin's network icons apply
            var box = card.appendChild(el('div', NS + 'person-links itemExternalLinks'));
            links.forEach(function (l) {
                var a = box.appendChild(el('a', 'button-link', l.Name));
                a.href = l.Url; a.target = '_blank'; a.rel = 'noopener noreferrer';
            });
        }
        return { node: card, fact: fact };
    }
    function personPage(page, person) {
        var host = page.querySelector('.detailPagePrimaryContainer');
        if (!host) { return Promise.resolve(); }
        page.classList.add(NS + 'person');
        var card = personCard(person);
        // last in the container (the CSS places it): jellyfin-web's own lookups keep finding its elements first
        host.appendChild(counted(page, card.node));
        var query = function (types, more) {
            var q = { PersonIds: person.Id, Recursive: true, IncludeItemTypes: types, EnableTotalRecordCount: true, EnableImages: false };
            Object.keys(more).forEach(function (k) { q[k] = more[k]; });
            return getItems(q).catch(function () { return { Items: [], TotalRecordCount: 0 }; });
        };
        var counts = Promise.all(WORKS.map(function (w) { return query(w[0], { Limit: 1 }); }));
        // the jobs, from the credits of the latest works
        var credits = query('Movie,Series,Episode', { Limit: 80, Fields: 'People', SortBy: 'PremiereDate', SortOrder: 'Descending' });
        return Promise.all([counts, credits]).then(function (all) {
            if (page.getAttribute('data-pj-for') !== person.Id || !card.node.isConnected) { return; }
            var totals = {};
            WORKS.forEach(function (w, i) { totals[w[0]] = all[0][i].TotalRecordCount || 0; });
            var held = WORKS.filter(function (w) { return totals[w[0]] > 0; }).map(function (w) { return nf(totals[w[0]]) + ' ' + w[totals[w[0]] > 1 ? 2 : 1]; });
            if (held.length) { card.fact('Dans votre médiathèque', held.join(' · ')); }
            var plain = function (id) { return String(id || '').replace(/-/g, '').toLowerCase(); };
            var types = {};
            (all[1].Items || []).forEach(function (it) { (it.People || []).forEach(function (p) { if (plain(p.Id) === plain(person.Id)) { types[p.Type] = true; } }); });
            var jobs = JOBS.filter(function (j) { return j[1].some(function (t) { return types[t]; }); }).map(function (j) { return j[0]; });
            var name = page.querySelector('.detailRibbon .nameContainer');
            if (jobs.length && name) { name.parentNode.insertBefore(counted(page, el('p', NS + 'person-job', jobs.join(' · '))), name); }
            // "Films 5" on the sections, rendered by jellyfin-web in its own time
            page.__pjRows = function () {
                page.querySelectorAll('#childrenContent > [data-type]').forEach(function (section) {
                    var n = totals[section.getAttribute('data-type')], title = section.querySelector('.sectionTitle');
                    if (n && title && !title.querySelector('.' + NS + 'person-count')) { title.appendChild(el('span', NS + 'person-count', nf(n))); }
                });
            };
            page.__pjRows();
        });
    }

    // ---------- the header built around the item's picture ----------
    // The picture is the header: a LANDSCAPE image (portrait backdrops do not suit a desktop screen), shown whole at
    // its own ratio, as large as the screen allows; the page around it takes its colours (no blur, no black veil):
    // the margin beside it its edge colour, the ground below a deep shade of its bottom edge, the text's scrim the same.
    // Candidates, in order: the episode's own still (large enough), the item's backdrops, the series' backdrops,
    // the item's thumbnail, the series' thumbnail. None landscape: the compact header (html.pj-noimage).
    var MIN_RATIO = 1.25;   // landscape
    // sharp enough at the size it is really shown (the poster frame: 64% of the screen's height at the picture's ratio,
    // at most the screen's width; the full width on a phone), allowing a third of upscaling and never under 640 px.
    // A fixed 1200 px minimum turned down a 1080x720 backdrop that a 1080p screen shows at about 1040 px.
    function usable(img) {
        if (!img || img.naturalWidth / img.naturalHeight < MIN_RATIO) { return false; }
        var ratio = img.naturalWidth / img.naturalHeight, phone = document.documentElement.classList.contains('layout-mobile');
        var shown = phone ? window.innerWidth : Math.min(window.innerWidth, window.innerHeight * 0.64 * ratio);
        return img.naturalWidth >= Math.max(640, shown * 0.75);
    }
    function loadImage(url) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.onload = function () { resolve(img.naturalWidth && img.naturalHeight ? img : null); };
            img.onerror = function () { resolve(null); };
            img.src = url;
        });
    }
    function candidates(item) {
        var c = client(), out = [];
        var add = function (id, type, tag, index) { if (id && tag) { var u = new String(c.getImageUrl(id, { type: type, tag: tag, index: index, maxWidth: 2400, quality: 90 })); u.backdrop = type === 'Backdrop'; u.still = item.Type === 'Episode' && type === 'Primary' && id === item.Id; out.push(u); } };
        // an episode's own still first: it is what tells one episode from the next (the series' backdrop is the same on
        // all of them); then the backdrops (all of them: they take turns, like jellyfin-web's backdrop slideshow): the
        // item's own, else its parent's (a season's or an episode's series); then the thumbnails
        if (item.Type === 'Episode' && item.ImageTags && item.ImageTags.Primary) { add(item.Id, 'Primary', item.ImageTags.Primary); }
        (item.BackdropImageTags || []).forEach(function (tag, i) { add(item.Id, 'Backdrop', tag, i); });
        if (!(item.BackdropImageTags || []).length) { (item.ParentBackdropImageTags || []).forEach(function (tag, i) { add(item.ParentBackdropItemId, 'Backdrop', tag, i); }); }
        if (item.ImageTags && item.ImageTags.Thumb) { add(item.Id, 'Thumb', item.ImageTags.Thumb); }
        if (item.ParentThumbItemId && item.ParentThumbImageTag) { add(item.ParentThumbItemId, 'Thumb', item.ParentThumbImageTag); }
        return out;
    }
    // average colour of a region of the picture, as [r, g, b]
    function sample(img, x, y, w, h) {
        var cv = document.createElement('canvas'); cv.width = 32; cv.height = 32;
        var g = cv.getContext('2d', { willReadFrequently: true });
        g.drawImage(img, x * img.naturalWidth, y * img.naturalHeight, Math.max(1, w * img.naturalWidth), Math.max(1, h * img.naturalHeight), 0, 0, 32, 32);
        var d = g.getImageData(0, 0, 32, 32).data, r = 0, gr = 0, b = 0, n = d.length / 4;
        for (var i = 0; i < d.length; i += 4) { r += d[i]; gr += d[i + 1]; b += d[i + 2]; }
        return [r / n, gr / n, b / n];
    }
    function lum(c) { var f = function (v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }; return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]); }
    // a deep shade of a colour, keeping its hue: dark enough for the theme's paper text (contrast about 10:1 or more)
    function deep(c, target) {
        var ink = [13, 20, 21], mix = [0, 1, 2].map(function (k) { return ink[k] * 0.45 + c[k] * 0.55; });
        for (var i = 0; i < 40 && lum(mix) > (target || 0.03); i++) { mix = mix.map(function (v) { return v * 0.9; }); }
        return mix.map(function (v) { return Math.round(v); });
    }
    // the document's one header: { page, id, building }. A new object for each build: whatever was still loading for
    // an earlier one (even of the same item) sees it was replaced and stops
    var header = null;
    function canvasHeader(page, item) {
        var root = document.documentElement;
        // already being built, or built, for this item in this page (and its layer still there: a re-render may have
        // removed it)
        if (header && header.page === page && header.id === item.Id && (header.building || root.classList.contains(NS + 'noimage') || (root.classList.contains(NS + 'person-bd') && page.querySelector('.' + NS + 'person-banner')) || (root.classList.contains(NS + 'canvas') && page.querySelector('.' + NS + 'canvas-layer, .' + NS + 'affiche')))) { return; }
        var own = header = { page: page, id: item.Id, building: true };
        var list = candidates(item);
        var tryNext = function (i) {
            if (header !== own) { return null; }
            if (i >= list.length) {
                if (item.Type === 'Series' || item.Type === 'Movie' || item.Type === 'Season' || item.Type === 'Episode') { return posterAffiche(page, item, own); }
                setNoImage(); own.building = false; return null;
            }
            return loadImage(list[i]).then(function (img) {
                if (header !== own) { return null; }
                if (!usable(img)) { return tryNext(i + 1); }
                setCanvas(img, String(list[i]), page, item, !!list[i].still);
                own.building = false;
                // the other backdrops join a slideshow once they have loaded (and are landscape and large enough)
                if (list[i].backdrop) { slideshow(own, list.slice(i + 1).filter(function (u) { return u.backdrop; }), item.Type === 'Person' ? '.' + NS + 'person-banner' : null); }
                return null;
            });
        };
        tryNext(0);
    }
    function setNoImage() {
        clearCanvas();
        document.documentElement.classList.add(NS + 'noimage');
    }
    function setCanvas(img, url, page, item, isStill) {
        var root = document.documentElement;
        clearCanvas();
        paintColours(img);
        root.__pjImage = img;
        // a person: the picture as a banner across the top of the page (no poster, no side margins)
        if (page && item && item.Type === 'Person') {
            var banner = mark(el('div', NS + 'person-banner'));
            banner.setAttribute('aria-hidden', 'true');
            var picture = banner.appendChild(el('img', NS + 'aff-img'));
            picture.alt = ''; picture.src = url;
            page.insertBefore(banner, page.firstChild);
            root.classList.add(NS + 'person-bd');
            return;
        }
        // in the page itself, behind its content: the picture scrolls with the text (in jellyfin-web's fixed backdrop
        // layer it stayed while the synopsis scrolled up onto its bright sky)
        // a series or a movie: the poster ("Affiche"); a season or an episode: the picture behind the header
        if (page && item && (item.Type === 'Series' || item.Type === 'Movie' || item.Type === 'Season' || item.Type === 'Episode')) {
            page.insertBefore(affiche(item, url, isStill), page.querySelector('.detailPageWrapperContainer') || page.firstChild);
            root.classList.add(NS + 'affiche-on', NS + 'aff-a');
        } else if (page) {
            var layer = mark(el('div', NS + 'canvas-layer'));
            layer.setAttribute('aria-hidden', 'true');
            layer.appendChild(el('div', NS + 'canvas-fill'));
            layer.appendChild(el('div', NS + 'canvasimg')).style.backgroundImage = 'url("' + url + '")';
            page.insertBefore(layer, page.firstChild);
        }
        sizeCanvas();
        root.classList.add(NS + 'canvas');
    }
    // the colours the page takes from the picture: its edges (the margins), its bottom (the ground), its dark (the scrim)
    function paintColours(img) {
        var root = document.documentElement;
        var left = sample(img, 0.015, 0, 0.04, 1), bottom = sample(img, 0, 0.9, 1, 0.1), lowLeft = sample(img, 0, 0.6, 0.5, 0.4);
        var ground = deep(bottom, 0.028), scrim = deep(lowLeft, 0.02);
        root.style.setProperty('--pj-img-ratio', String(img.naturalWidth / img.naturalHeight));
        root.style.setProperty('--pj-edge-l', left.map(Math.round).join(', '));
        // the margin continues the picture's left edge as it goes down (sky, horizon, ground), band by band
        var stops = [];
        for (var k = 0; k < 10; k++) { stops.push('rgb(' + sample(img, 0, k / 10, 0.08, 0.1).map(Math.round).join(', ') + ') ' + (k * 10 + 5) + '%'); }
        root.style.setProperty('--pj-edge-grad', 'linear-gradient(to bottom, ' + stops.join(', ') + ')');
        var stopsR = [];
        for (var q = 0; q < 10; q++) { stopsR.push('rgb(' + sample(img, 0.92, q / 10, 0.08, 0.1).map(Math.round).join(', ') + ') ' + (q * 10 + 5) + '%'); }
        root.style.setProperty('--pj-edge-grad-r', 'linear-gradient(to bottom, ' + stopsR.join(', ') + ')');
        // the ambient light beside the poster's frame: the picture's side thirds, top, middle and bottom
        [['l', 0], ['r', 0.7]].forEach(function (side) {
            [0, 0.3, 0.65].forEach(function (y, k) { root.style.setProperty('--pj-amb-' + side[0] + (k + 1), 'rgb(' + sample(img, side[1], y, 0.3, 0.35).map(Math.round).join(', ') + ')'); });
        });
        root.style.setProperty('--pj-edge-b', bottom.map(Math.round).join(', '));
        root.style.setProperty('--pj-scrim', scrim.join(', '));
        // the whole theme's ink takes the picture's deep shade on this page
        root.style.setProperty('--pj-bg', 'rgb(' + ground.join(', ') + ')');
        root.style.setProperty('--pj-veil', ground.join(', '));
    }
    // the item's other backdrops take turns in the poster's frame, cross-fading every 9 s; the page's colours follow
    var slideTimer = null;
    function slideshow(own, urls, frameSelector) {
        clearInterval(slideTimer); slideTimer = null;
        if (!urls.length) { return; }
        Promise.all(urls.map(function (u) { return loadImage(String(u)).then(function (img) { return usable(img) ? { img: img, url: String(u) } : null; }); }))
            .then(function (found) {
                var frame = frameSelector ? own.page.querySelector(frameSelector) : document.querySelector('.' + NS + 'aff-frame');
                var first = frame && frame.querySelector('.' + NS + 'aff-img');
                found = found.filter(Boolean);
                if (!frame || !first || !found.length || header !== own) { return; }
                var slides = [{ img: document.documentElement.__pjImage, node: first }];
                found.forEach(function (f) {
                    var node = el('img', NS + 'aff-img ' + NS + 'aff-slide');
                    node.alt = ''; node.src = f.url;
                    first.parentNode.insertBefore(node, first.nextSibling);
                    slides.push({ img: f.img, node: node });
                });
                var at = 0;
                slideTimer = setInterval(function () {
                    if (header !== own || !document.contains(first)) { clearInterval(slideTimer); slideTimer = null; return; }
                    if (document.hidden) { return; }
                    at = (at + 1) % slides.length;
                    slides.forEach(function (sl, k) { sl.node.classList.toggle(NS + 'aff-shown', k === at); });
                    paintColours(slides[at].img);
                }, 9000);
                first.classList.add(NS + 'aff-shown');
            });
    }
    // the picture as large as the screen allows: full width, or full height (85% of it) when that is smaller
    function sizeCanvas() {
        var root = document.documentElement, img = root.__pjImage;
        if (!img) { return; }
        var r = img.naturalWidth / img.naturalHeight, w = window.innerWidth, h = Math.min(w / r, window.innerHeight * 0.85);
        root.style.setProperty('--pj-img-w', Math.round(h * r) + 'px');
        root.style.setProperty('--pj-img-h', Math.round(h) + 'px');
    }
    function clearCanvas() {
        var root = document.documentElement;
        root.classList.remove(NS + 'canvas', NS + 'noimage', NS + 'affiche-on', NS + 'aff-a', NS + 'aff-b', NS + 'aff-c', NS + 'person-bd');
        clearInterval(slideTimer); slideTimer = null;
        ['--pj-aff-a', '--pj-aff-b', '--pj-img-ratio', '--pj-edge-l', '--pj-edge-grad', '--pj-edge-grad-r', '--pj-edge-b', '--pj-scrim', '--pj-bg', '--pj-veil', '--pj-img-w', '--pj-img-h', '--pj-amb-l1', '--pj-amb-l2', '--pj-amb-l3', '--pj-amb-r1', '--pj-amb-r2', '--pj-amb-r3'].forEach(function (v) { root.style.removeProperty(v); });
        root.__pjImage = null;
        document.querySelectorAll('.' + NS + 'canvas-layer, .' + NS + 'affiche, .' + NS + 'person-banner').forEach(function (n) { n.remove(); });
    }
    // ---------- "Affiche" (series and movies): the header as a film poster, whatever pictures the item has ----------
    // a: a landscape backdrop whole on top, the logo on its lower third, the series' poster standing on its lower edge;
    // b: no backdrop, a poster: the poster itself is the affiche, large on a band lit by its own colours;
    // c: no picture at all: a typographic affiche, the title set large in the serif.
    // the poster: the item's own (series, movie, season); an episode's is its series' (its own picture is a still)
    function posterUrl(item, height) {
        if (item.Type === 'Episode') {
            return item.SeriesId && item.SeriesPrimaryImageTag ? client().getImageUrl(item.SeriesId, { type: 'Primary', tag: item.SeriesPrimaryImageTag, maxHeight: height || 900, quality: 92 }) : null;
        }
        var tag = item.ImageTags && item.ImageTags.Primary;
        return tag ? client().getImageUrl(item.Id, { type: 'Primary', tag: tag, maxHeight: height || 900, quality: 92 }) : null;
    }
    function brand(item, holder, cls) {
        if (item.Type === 'Season' || item.Type === 'Episode') { return subBrand(item, holder); }
        var logoTag = item.ImageTags && item.ImageTags.Logo;
        if (logoTag) {
            var logo = holder.appendChild(el('img', NS + 'aff-logo ' + (cls || '')));
            logo.alt = item.Name;
            logo.src = client().getImageUrl(item.Id, { type: 'Logo', tag: logoTag, maxWidth: 1000, quality: 95 });
        } else {
            // a long name is set smaller, so it stays a title and not a wall of letters
            // the year already shows in the facts: "Le Grand Voyage (2021)" is set as "Le Grand Voyage"
            var name = item.ProductionYear ? item.Name.replace(new RegExp('\\s*\\(' + item.ProductionYear + '\\)\\s*$'), '') : item.Name;
            holder.appendChild(el('h1', NS + 'aff-title ' + (name.length > 26 ? NS + 'aff-long ' : '') + (cls || ''), name));
        }
    }
    // a season or an episode: the series' logo (or name, small), linked to the series, then "S1 · É3" and the title
    function subBrand(item, holder) {
        var c = client();
        var box = holder.appendChild(el('div', NS + 'aff-brand'));
        var link = box.appendChild(el('a', NS + 'aff-series'));
        link.href = detailsHref(item.SeriesId);
        if (item.ParentLogoItemId && item.ParentLogoImageTag) {
            var logo = link.appendChild(el('img', NS + 'aff-serieslogo'));
            logo.alt = item.SeriesName || '';
            logo.src = c.getImageUrl(item.ParentLogoItemId, { type: 'Logo', tag: item.ParentLogoImageTag, maxWidth: 700, quality: 95 });
        } else {
            link.textContent = item.SeriesName || '';
        }
        if (item.Type === 'Episode') { var code = epCode(item); if (code) { box.appendChild(el('span', NS + 'aff-code', code)); } }
        box.appendChild(el('h1', NS + 'aff-title ' + NS + 'aff-subtitle' + (item.Name.length > 30 ? ' ' + NS + 'aff-long' : ''), item.Name));
    }
    function affiche(item, url, isStill) {
        var box = mark(el('section', NS + 'affiche ' + NS + 'aff-mode-a'));
        box.setAttribute('aria-label', item.Name);
        var stage = box.appendChild(el('div', NS + 'aff-stage'));
        stage.appendChild(el('div', NS + 'aff-fill ' + NS + 'aff-fill-l'));
        stage.appendChild(el('div', NS + 'aff-fill ' + NS + 'aff-fill-r'));
        var frame = stage.appendChild(el('div', NS + 'aff-frame'));
        var pic = frame.appendChild(el('img', NS + 'aff-img'));
        pic.alt = ''; pic.src = url;
        brand(item, frame);
        var bill = box.appendChild(billing(item));
        // the series' poster, standing on the picture's lower edge like a sleeve set before the screen; on an episode whose
        // still is too small to be the header (the series' backdrop is), the still itself, so the episode is recognised
        var still = item.Type === 'Episode' && !isStill && item.ImageTags && item.ImageTags.Primary
            ? client().getImageUrl(item.Id, { type: 'Primary', tag: item.ImageTags.Primary, maxWidth: 800, quality: 92 }) : null;
        var purl = still || posterUrl(item, 700);
        if (still) {
            // the still plays the episode's preview on hover, like an episode card (previewHost), and opens it large on a
            // click when there is a preview video
            var sleeve = bill.insertBefore(el('div', NS + 'aff-poster ' + NS + 'aff-still'), bill.firstChild);
            sleeve.setAttribute('data-id', item.Id);
            var shot = sleeve.appendChild(el('img'));
            shot.alt = item.Name; shot.src = still;
            previewInfo(item.Id).then(function (info) {
                if (!info || !(info.Source || info.Clip)) { return; }
                sleeve.classList.add(NS + 'aff-still-preview');
                sleeve.title = 'Aperçu · ' + previewOrigin(info).label;
                sleeve.tabIndex = 0; sleeve.setAttribute('role', 'button'); sleeve.setAttribute('aria-label', sleeve.title);
                sleeve.addEventListener('click', function () { openPreview(item, info); });
                sleeve.addEventListener('keydown', function (ev) { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); openPreview(item, info); } });
            });
            bill.classList.add(NS + 'has-poster', NS + 'has-still');
        } else if (purl) {
            var poster = bill.insertBefore(el('img', NS + 'aff-poster'), bill.firstChild);
            poster.alt = ''; poster.src = purl;
            bill.classList.add(NS + 'has-poster');
        }
        return box;
    }
    function posterAffiche(page, item, own) {
        var purl = posterUrl(item, 1000);
        var done = function (img) {
            if (header !== own) { return null; }
            var root = document.documentElement;
            clearCanvas();
            own.building = false;
            var box = mark(el('section', NS + 'affiche'));
            box.setAttribute('aria-label', item.Name);
            if (img) {
                // the band takes the poster's colours: its top and middle, then the page's deep shade of its bottom
                var top = sample(img, 0, 0, 1, 0.25), mid = sample(img, 0, 0.35, 1, 0.3), bottom = sample(img, 0, 0.85, 1, 0.15);
                var ground = deep(bottom, 0.028);
                root.style.setProperty('--pj-aff-a', deep(top, 0.09).join(', '));
                root.style.setProperty('--pj-aff-b', deep(mid, 0.05).join(', '));
                root.style.setProperty('--pj-bg', 'rgb(' + ground.join(', ') + ')');
                root.style.setProperty('--pj-veil', ground.join(', '));
                box.classList.add(NS + 'aff-mode-b');
                var poster = box.appendChild(el('img', NS + 'aff-poster'));
                poster.alt = ''; poster.src = purl;
                var side = box.appendChild(el('div', NS + 'aff-side'));
                brand(item, side);
                side.appendChild(billing(item));
                root.classList.add(NS + 'aff-b');
            } else {
                box.classList.add(NS + 'aff-mode-c');
                brand(item, box);
                box.appendChild(el('span', NS + 'aff-rule'));
                box.appendChild(billing(item));
                root.classList.add(NS + 'aff-c');
            }
            page.insertBefore(box, page.querySelector('.detailPageWrapperContainer') || page.firstChild);
            root.classList.add(NS + 'affiche-on', NS + 'canvas');
            return null;
        };
        if (!purl) { return done(null); }
        return loadImage(purl).then(done);
    }
    // the poster's billing block: tagline, credits, facts
    function billing(item) {
        var bill = el('div', NS + 'aff-billing');
        var tagline = (item.Taglines || [])[0];
        if (tagline) { bill.appendChild(el('p', NS + 'aff-tag', tagline)); }
        // the credits, as a poster prints them: who presents, who plays, who made it
        var people = item.People || [];
        var actors = people.filter(function (p) { return p.Type === 'Actor' || p.Type === 'GuestStar'; }).slice(0, 4);
        var makers = people.filter(function (p) { return p.Type === 'Director' || p.Type === 'Creator'; });
        if (!makers.length) { makers = people.filter(function (p) { return p.Type === 'Writer'; }); }
        makers = makers.slice(0, 2);
        var studios = (item.Studios || []).slice(0, 3).map(function (st) { return st.Name; });
        var credits = el('p', NS + 'aff-credits');
        var text = function (t) { credits.appendChild(document.createTextNode(t)); };
        var names = function (list) { list.forEach(function (p, i) { if (i) { text(i === list.length - 1 ? ' et ' : ', '); } credits.appendChild(el('b', null, p.Name)); }); };
        if (item.Type === 'Episode' || item.Type === 'Season') {
            // an episode's credits: who directed and wrote it, its guests
            var directors = people.filter(function (p) { return p.Type === 'Director'; }).slice(0, 2);
            var writers = people.filter(function (p) { return p.Type === 'Writer'; }).slice(0, 2);
            var guests = people.filter(function (p) { return p.Type === 'GuestStar'; }).slice(0, 3);
            var part = function (label, list) { if (!list.length) { return; } if (credits.childNodes.length) { text('   '); } text(label); names(list); };
            part('Réalisé par ', directors); part('Écrit par ', writers); part('Avec ', guests);
            if (credits.childNodes.length) { bill.appendChild(credits); }
            studios = []; actors = []; makers = [];
        }
        if (studios.length) { text(studios.join(' et ') + (studios.length > 1 ? ' présentent ' : ' présente ')); }
        if (actors.length) { names(actors); }
        if (makers.length) { text((actors.length || studios.length ? ' dans ' : '') + (item.Type === 'Movie' ? 'un film de ' : 'une série de ')); names(makers); }
        if (credits.childNodes.length) { bill.appendChild(credits); }
        // the facts line: status and years, length, rating, score
        var facts = bill.appendChild(el('div', NS + 'aff-facts'));
        var fact = function (t, cls) { if (t) { facts.appendChild(el('span', cls ? NS + cls : null, t)); } };
        if (item.Type === 'Season') {
            var n = item.ChildCount, left = item.UserData ? item.UserData.UnplayedItemCount : null;
            if (n) { fact(n + (n > 1 ? ' épisodes' : ' épisode')); }
            if (left) { fact(left + ' à voir', 'continuing'); } else if (item.UserData && item.UserData.Played) { fact('Vue'); }
            if (item.ProductionYear) { fact(String(item.ProductionYear)); }
        } else if (item.Type === 'Episode') {
            if (item.PremiereDate) { try { fact(new Date(item.PremiereDate).toLocaleDateString('fr-FR', { day: 'numeric', month: 'long', year: 'numeric' })); } catch (e) { /* no date */ } }
            if (item.RunTimeTicks >= 600000000) { var em = Math.round(item.RunTimeTicks / 600000000); fact(em >= 60 ? Math.floor(em / 60) + ' h ' + String(em % 60).padStart(2, '0') : em + ' min'); }
            if (item.UserData && item.UserData.Played) { fact('Vu', 'continuing'); }
        } else if (item.Type === 'Series') {
            var ended = item.Status === 'Ended', end = item.EndDate ? new Date(item.EndDate).getFullYear() : null;
            if (item.Status) { fact(ended ? 'Terminée' : 'En cours', ended ? 'ended' : 'continuing'); }
            if (item.ProductionYear) { fact(ended && end && end !== item.ProductionYear ? item.ProductionYear + '–' + end : 'Depuis ' + item.ProductionYear); }
            if (item.ChildCount) { fact(item.ChildCount + (item.ChildCount > 1 ? ' saisons' : ' saison')); }
        } else {
            if (item.ProductionYear) { fact(String(item.ProductionYear)); }
            if (item.RunTimeTicks >= 600000000) { var min = Math.round(item.RunTimeTicks / 600000000); fact(min >= 60 ? Math.floor(min / 60) + ' h ' + String(min % 60).padStart(2, '0') : min + ' min'); }
        }
        if (item.OfficialRating) { fact(item.OfficialRating, 'rating'); }
        if (item.CommunityRating) { fact('★ ' + String(Math.round(item.CommunityRating * 10) / 10).replace('.', ',')); }
        if (item.CriticRating) { fact('Critiques ' + Math.round(item.CriticRating) + ' %'); }
        return bill;
    }

    // the header belongs to the detail page it was built in, while that page shows that item: else it is removed
    function leaveCanvas() {
        var page = currentPage();
        if (!header || (header.page === page && page.getAttribute('data-pj-for') === currentId())) { return; }
        header = null;
        clearCanvas();
    }
    window.addEventListener('resize', function () { sizeCanvas(); });

    // ---------- what the item is: its logo instead of the title, its status ----------
    function identity(page, item) {
        var c = client();
        var name = page.querySelector('.detailRibbon .itemName');
        // the logo: the item's own (series, movie), or its series' (season, episode) in place of the series pill
        var own = item.ImageTags && item.ImageTags.Logo;
        if (own && name && (item.Type === 'Series' || item.Type === 'Movie') && !document.documentElement.classList.contains(NS + 'affiche-on')) {
            var logo = counted(page, el('img', NS + 'logo'));
            logo.alt = item.Name; logo.src = c.getImageUrl(item.Id, { type: 'Logo', tag: own, maxWidth: 900, quality: 95 });
            name.parentNode.insertBefore(logo, name);
            page.classList.add(NS + 'has-logo');
        } else {
            page.classList.remove(NS + 'has-logo');
        }
        var pill = page.querySelector('.detailRibbon .parentName');
        if (pill && item.ParentLogoItemId && item.ParentLogoImageTag && !pill.querySelector('.' + NS + 'parentlogo')) {
            var plogo = mark(el('img', NS + 'parentlogo'));
            plogo.alt = item.SeriesName || '';
            plogo.src = c.getImageUrl(item.ParentLogoItemId, { type: 'Logo', tag: item.ParentLogoImageTag, maxWidth: 500, quality: 95 });
            var a = pill.querySelector('a') || pill;
            a.insertBefore(plogo, a.firstChild);
            pill.classList.add(NS + 'has-parentlogo');
        }
        // a series' status: "En cours" (since the year) or "Terminée" (with its years)
        if (item.Type === 'Series' && item.Status) {
            var facts = page.querySelector('.detailRibbon .itemMiscInfo-primary') || page.querySelector('.detailRibbon .itemMiscInfo');
            if (facts) {
                var ended = item.Status === 'Ended';
                var end = item.EndDate ? new Date(item.EndDate).getFullYear() : null;
                var text = ended ? 'Terminée' + (item.ProductionYear && end && end !== item.ProductionYear ? ' · ' + item.ProductionYear + '–' + end : '') : 'En cours';
                var chip = counted(page, el('span', NS + 'status ' + NS + (ended ? 'ended' : 'continuing'), text));
                facts.insertBefore(chip, facts.firstChild);
            }
        }
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
            page.__pjRows = function () { numberEpisodeRows(page, byId); previewButtons(page, byId); };
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

    // ---------- the preview of an episode, on demand: a button and a viewer with sound ----------
    var infoCache = new Map();
    function previewInfo(id) {
        if (!infoCache.has(id)) {
            var c = client();
            infoCache.set(id, c.getJSON(c.getUrl('CustomMetadata/Preview/' + id + '/Info')).catch(function () { return null; }));
        }
        return infoCache.get(id);
    }
    // where a preview comes from, in words: the source's own video (with the source's name) or the plugin's clip
    function previewOrigin(info) {
        if (info.Source) { return { kind: 'source', label: 'Aperçu de la source « ' + (info.SourceName || 'source') + ' »' + (info.SourceIsLocal ? ' (vidéo envoyée dans la source locale)' : '') }; }
        return { kind: 'clip', label: 'Extraits créés par le plugin à partir du fichier' };
    }
    // a small "Aperçu" button on each episode row of a season page that has a preview
    function previewButtons(page, byId) {
        page.querySelectorAll('#listChildrenCollapsible .listItem[data-id]').forEach(function (row) {
            var e = byId.get(row.getAttribute('data-id'));
            if (!e || e.LocationType === 'Virtual' || row.__pjPreviewAsked) { return; }
            row.__pjPreviewAsked = true;
            previewInfo(e.Id).then(function (info) {
                var host = row.querySelector('.listViewUserDataButtons');
                if (!info || !(info.Source || info.Clip) || !host || host.querySelector('.' + NS + 'previewbtn')) { return; }
                var btn = mark(el('button', NS + 'previewbtn listItemButton paper-icon-button-light'));
                btn.type = 'button'; btn.title = 'Aperçu · ' + previewOrigin(info).label;
                btn.appendChild(el('span', 'material-icons movie')).setAttribute('aria-hidden', 'true');
                btn.addEventListener('click', function (ev) { ev.preventDefault(); ev.stopPropagation(); openPreview(e, info); });
                host.insertBefore(btn, host.firstChild);
            });
        });
    }
    // the viewer (« Salle »): the preview large in an ambient light of its own colours, the theme's controls under it,
    // the episode's title and its play button. Escape, the close button or a click outside closes it.
    var CLIP_SEGMENT_SECONDS = 1.5, CLIP_SEGMENTS = 8;   // as PreviewClipService builds the clips
    var closeViewer = null;
    function openPreview(item, info) {
        stopPreview();
        if (closeViewer) { closeViewer(); }
        var c = client(), opener = document.activeElement;
        var urls = [];
        if (info.Source) { urls.push({ src: sourcePreviewUrl(item), origin: previewOrigin(info) }); }
        if (info.Clip) { urls.push({ src: clipUrl(item), origin: previewOrigin({ Clip: true }) }); }
        if (!urls.length) { return; }
        var box = el('div', NS + 'viewer');
        box.setAttribute('role', 'dialog'); box.setAttribute('aria-modal', 'true'); box.setAttribute('aria-label', 'Aperçu de ' + item.Name);
        var glow = box.appendChild(el('div', NS + 'viewer-glow'));
        glow.setAttribute('aria-hidden', 'true');
        var button = function (parent, cls, icon, label) {
            var b = parent.appendChild(el('button', cls));
            b.type = 'button';
            b.appendChild(el('span', 'material-icons ' + icon)).setAttribute('aria-hidden', 'true');
            setButton(b, icon, label);
            return b;
        };
        // jellyfin-web's icon font draws the icon named by the class
        var setButton = function (b, icon, label) {
            b.firstChild.className = 'material-icons ' + icon;
            b.title = label; b.setAttribute('aria-label', label);
        };
        var close = button(box, NS + 'viewer-close', 'close', 'Fermer');
        var col = box.appendChild(el('div', NS + 'viewer-col'));
        var player = col.appendChild(el('div', NS + 'viewer-player'));
        var screen = player.appendChild(el('div', NS + 'viewer-screen'));
        var video = screen.appendChild(el('video', NS + 'viewer-video'));
        video.playsInline = true; video.preload = 'auto';
        var spinner = screen.appendChild(el('span', NS + 'viewer-spinner'));
        spinner.setAttribute('aria-hidden', 'true');
        var failed = screen.appendChild(el('p', NS + 'viewer-failed', 'Cet aperçu ne peut pas être lu pour le moment.'));
        failed.setAttribute('role', 'alert'); failed.hidden = true;

        var controls = player.appendChild(el('div', NS + 'viewer-controls'));
        var toggle = button(controls, NS + 'viewer-ib ' + NS + 'viewer-toggle', 'play_arrow', 'Lecture');
        var now = controls.appendChild(el('span', NS + 'viewer-time', '0:00'));
        var track = controls.appendChild(el('div', NS + 'viewer-track'));
        track.tabIndex = 0; track.setAttribute('role', 'slider'); track.setAttribute('aria-label', 'Position dans l’aperçu');
        track.setAttribute('aria-valuemin', '0');
        var total = controls.appendChild(el('span', NS + 'viewer-time', '–:––'));
        var mute = button(controls, NS + 'viewer-ib ' + NS + 'viewer-mute', 'volume_up', 'Couper le son');
        var volume = controls.appendChild(el('input', NS + 'viewer-volume'));
        volume.type = 'range'; volume.min = '0'; volume.max = '1'; volume.step = '0.05'; volume.value = '1';
        volume.setAttribute('aria-label', 'Volume');
        var canFull = player.requestFullscreen || player.webkitRequestFullscreen || video.webkitEnterFullscreen;
        var full = canFull ? button(controls, NS + 'viewer-ib', 'fullscreen', 'Plein écran') : null;

        var bill = col.appendChild(el('div', NS + 'viewer-bill'));
        var text = bill.appendChild(el('div', NS + 'viewer-text'));
        var overline = text.appendChild(el('div', NS + 'viewer-overline'));
        text.appendChild(el('h2', NS + 'viewer-title', item.Name));
        var note = text.appendChild(el('span', NS + 'viewer-note'));
        var playEpisodeButton = null;
        var describe = function (it) {
            var number = it.IndexNumber == null ? null : it.IndexNumberEnd ? 'Épisodes ' + it.IndexNumber + '-' + it.IndexNumberEnd : 'Épisode ' + it.IndexNumber;
            overline.textContent = [it.SeriesName, it.SeasonName, number].filter(Boolean).join(' · ');
            // a missing episode has nothing else to play than this preview
            if (it.LocationType === 'Virtual') { if (playEpisodeButton) { playEpisodeButton.remove(); playEpisodeButton = null; } return; }
            if (!playEpisodeButton) {
                playEpisodeButton = bill.appendChild(el('button', NS + 'viewer-play'));
                playEpisodeButton.type = 'button';
                playEpisodeButton.appendChild(el('span', 'material-icons play_arrow')).setAttribute('aria-hidden', 'true');
                playEpisodeButton.appendChild(el('span'));
                playEpisodeButton.addEventListener('click', function () { done(); playEpisode(item.Id); });
            }
            var resumes = it.UserData && it.UserData.PlaybackPositionTicks > 0;
            playEpisodeButton.lastChild.textContent = resumes ? 'Reprendre l’épisode' : 'Lire l’épisode';
        };
        describe(item);
        // the list the button came from may not say everything, and the position may have changed since it was read
        c.getItem(c.getCurrentUserId(), item.Id).then(function (fresh) { if (box.isConnected) { describe(fresh); } }).catch(function (e) { warn('viewer', e); });

        // the plugin's clip is a row of short moments of the episode: the bar shows them apart; a source's video is one piece
        var current = 0, segments = [], light = null, frame = null;
        var duration = function () { return isFinite(video.duration) && video.duration > 0 ? video.duration : 0; };
        var isClip = function () { return urls[current].origin.kind === 'clip'; };
        var buildTrack = function () {
            var count = isClip() && duration() ? Math.max(1, Math.min(CLIP_SEGMENTS, Math.round(duration() / CLIP_SEGMENT_SECONDS))) : 1;
            track.textContent = ''; segments = [];
            for (var k = 0; k < count; k++) { segments.push(track.appendChild(el('i')).appendChild(el('b'))); }
            track.classList.toggle(NS + 'viewer-segmented', count > 1);
        };
        var clock = function (s) {
            s = Math.floor(s);
            var h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = String(s % 60).padStart(2, '0');
            return h ? h + ':' + String(m).padStart(2, '0') + ':' + sec : m + ':' + sec;
        };
        var progress = function () {
            var d = duration(), at = d ? video.currentTime / d : 0;
            segments.forEach(function (fill, k) { fill.style.width = 100 * Math.max(0, Math.min(1, at * segments.length - k)) + '%'; });
            var t = clock(video.currentTime || 0);
            if (now.textContent !== t) {
                now.textContent = t;
                track.setAttribute('aria-valuenow', String(Math.floor(video.currentTime || 0)));
                track.setAttribute('aria-valuetext', t + (d ? ' sur ' + clock(d) : ''));
            }
        };
        var tick = function () { progress(); frame = video.paused ? null : requestAnimationFrame(tick); };
        var seek = function (seconds) { var d = duration(); if (d) { video.currentTime = Math.max(0, Math.min(d - 0.05, seconds)); progress(); } };
        var state = function () {
            setButton(toggle, video.ended ? 'replay' : video.paused ? 'play_arrow' : 'pause', video.ended ? 'Revoir' : video.paused ? 'Lecture' : 'Pause');
            var silent = video.muted || video.volume === 0;
            setButton(mute, silent ? 'volume_off' : 'volume_up', silent ? 'Remettre le son' : 'Couper le son');
            volume.value = String(silent ? 0 : video.volume);
        };
        var playPause = function () {
            if (!video.paused) { video.pause(); return; }
            var p = video.play(); if (p && p.catch) { p.catch(function () { /* the button stays */ }); }
        };
        // with sound (the click allows it); if the browser still refuses, muted, and the controls unmute it
        var load = function () {
            note.textContent = urls[current].origin.label;
            failed.hidden = true; box.classList.remove(NS + 'viewer-broken'); box.classList.add(NS + 'viewer-loading');
            buildTrack();
            video.src = urls[current].src;
            var p = video.play();
            if (p && p.catch) { p.catch(function (e) { if (e && e.name === 'NotAllowedError') { video.muted = true; video.play().catch(function () { /* the controls stay */ }); } }); }
        };
        video.addEventListener('loadedmetadata', function () {
            if (video.videoWidth && video.videoHeight) { box.style.setProperty('--pj-viewer-ratio', String(video.videoWidth / video.videoHeight)); }
            buildTrack();
            total.textContent = duration() ? clock(duration()) : '–:––';
            track.setAttribute('aria-valuemax', String(Math.floor(duration())));
            progress();
        });
        ['play', 'pause', 'ended', 'volumechange'].forEach(function (name) { video.addEventListener(name, state); });
        video.addEventListener('play', function () { if (!frame) { frame = requestAnimationFrame(tick); } });
        video.addEventListener('seeked', progress);
        video.addEventListener('waiting', function () { box.classList.add(NS + 'viewer-loading'); });
        ['playing', 'canplay', 'pause'].forEach(function (name) { video.addEventListener(name, function () { if (video.readyState > 2) { box.classList.remove(NS + 'viewer-loading'); } }); });
        video.addEventListener('error', function () {
            if (!video.getAttribute('src')) { return; }
            // the source's video does not play: the plugin's clip, when there is one
            if (current + 1 < urls.length) { current++; load(); return; }
            box.classList.remove(NS + 'viewer-loading'); box.classList.add(NS + 'viewer-broken'); failed.hidden = false;
            if (controls.contains(document.activeElement)) { close.focus({ preventScroll: true, focusVisible: false }); }
        });

        toggle.addEventListener('click', playPause);
        video.addEventListener('click', playPause);
        mute.addEventListener('click', function () {
            if (video.muted || video.volume === 0) { video.muted = false; if (video.volume === 0) { video.volume = 1; } } else { video.muted = true; }
        });
        volume.addEventListener('input', function () { video.volume = Number(volume.value); video.muted = video.volume === 0; });
        var seekTo = function (ev) { var r = track.getBoundingClientRect(); if (r.width) { seek(duration() * (ev.clientX - r.left) / r.width); } };
        track.addEventListener('pointerdown', function (ev) { ev.preventDefault(); track.focus({ preventScroll: true, focusVisible: false }); try { track.setPointerCapture(ev.pointerId); } catch (e) { /* no capture */ } seekTo(ev); });
        track.addEventListener('pointermove', function (ev) { if (track.hasPointerCapture && track.hasPointerCapture(ev.pointerId)) { seekTo(ev); } });
        // one arrow press: the previous or next moment of a clip, five seconds of a source's video
        var step = function (dir) {
            if (segments.length > 1) { var len = duration() / segments.length; seek((Math.floor(video.currentTime / len + 0.01) + dir) * len + 0.01); } else { seek(video.currentTime + 5 * dir); }
        };
        var fullElement = function () { return document.fullscreenElement || document.webkitFullscreenElement; };
        var toggleFull = function () {
            if (fullElement()) { (document.exitFullscreen || document.webkitExitFullscreen).call(document); return; }
            var ask = player.requestFullscreen || player.webkitRequestFullscreen;
            if (ask) { ask.call(player); } else if (video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); }   // iPhone
        };
        var onFull = function () { if (full) { setButton(full, fullElement() ? 'fullscreen_exit' : 'fullscreen', fullElement() ? 'Quitter le plein écran' : 'Plein écran'); } };
        if (full) { full.addEventListener('click', toggleFull); document.addEventListener('fullscreenchange', onFull); }

        var done = function () {
            if (closeViewer !== done) { return; }
            closeViewer = null;
            window.removeEventListener('keydown', onKey, true);
            window.removeEventListener('hashchange', done);
            document.removeEventListener('fullscreenchange', onFull);
            if (fullElement() && box.contains(fullElement())) { (document.exitFullscreen || document.webkitExitFullscreen).call(document); }
            if (frame) { cancelAnimationFrame(frame); }
            light.stop();
            try { video.pause(); video.removeAttribute('src'); video.load(); } catch (e) { /* gone */ }
            box.remove();
            document.documentElement.classList.remove(NS + 'viewer-open');
            if (opener && opener.isConnected && opener.focus) { opener.focus({ preventScroll: true }); }
        };
        // the keyboard belongs to the viewer while it is open (jellyfin-web has its own shortcuts)
        var onKey = function (ev) {
            ev.stopPropagation();
            if (ev.ctrlKey || ev.metaKey || ev.altKey) { return; }
            var key = ev.key, on = document.activeElement;
            if (key === 'Escape') { ev.preventDefault(); if (fullElement()) { toggleFull(); } else { done(); } return; }
            if (key === 'Tab') {
                var stops = Array.prototype.filter.call(box.querySelectorAll('button, input, [tabindex="0"]'), function (n) { return n.offsetParent !== null || n === close; });
                var at = stops.indexOf(on), next = at < 0 ? 0 : (at + (ev.shiftKey ? -1 : 1) + stops.length) % stops.length;
                ev.preventDefault(); stops[next].focus(); return;
            }
            if (key === 'k' || (key === ' ' && !(on && on.tagName === 'BUTTON'))) { ev.preventDefault(); playPause(); return; }
            if (on !== volume && (key === 'ArrowLeft' || key === 'ArrowRight')) { ev.preventDefault(); step(key === 'ArrowLeft' ? -1 : 1); return; }
            if (key === 'm') { mute.click(); return; }
            if (key === 'f' && full) { toggleFull(); }
        };
        closeViewer = done;
        close.addEventListener('click', done);
        box.addEventListener('click', function (ev) { if (ev.target === box) { done(); } });
        window.addEventListener('keydown', onKey, true);
        window.addEventListener('hashchange', done);
        document.documentElement.classList.add(NS + 'viewer-open');
        document.body.appendChild(box);
        light = glowFrom(video, glow);
        load();
        toggle.focus({ preventScroll: true });
    }
    // starts the episode itself, as jellyfin-web's own play buttons do: the page's button on the episode's page, else
    // a "resume" action in the episode's row, which jellyfin-web's list handles
    function playEpisode(id) {
        var page = currentPage();
        if (!page) { return; }
        var main = currentId() === id.toLowerCase() && page.querySelector('.mainDetailButtons .btnPlay:not(.hide)');
        if (main) { main.click(); return; }
        var row = page.querySelector('.listItem[data-id="' + id + '"]');
        if (!row) { location.hash = detailsHref(id); return; }
        var action = row.appendChild(el('button', 'itemAction'));
        action.type = 'button'; action.hidden = true; action.setAttribute('data-action', 'resume');
        action.click();
        action.remove();
    }

    // ---------- 3. hover preview of an episode ----------
    // not on touch screens (a tap opens the episode); any other device can point at a card
    var canHover = !(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
    var hoverTimer = null, preview = null;
    function previewHost(target) {
        // an episode card (rows, next up, continue watching) or a row of a season page
        var card = target.closest && target.closest('.card[data-type="Episode"][data-id], .listItem[data-type="Episode"][data-id], .' + NS + 'aff-still[data-id]');
        if (!card) { return null; }
        // an episode page's still, under the series' backdrop: the still itself is the frame
        if (card.classList.contains(NS + 'aff-still')) { return { id: card.getAttribute('data-id'), card: card, frame: card }; }
        var frame = card.querySelector('.cardScalable, .listItemImage');
        return frame ? { id: card.getAttribute('data-id'), card: card, frame: frame } : null;
    }
    function stopPreview() {
        clearTimeout(hoverTimer); hoverTimer = null;
        if (preview) { preview.stop(); preview = null; }
    }
    // A preview is a video whenever there is one: a missing episode's preview trailer, else the episode's own file
    // (muted, a few seconds taken at several points of the episode, like a teaser); trickplay thumbnails only when the
    // file cannot play in the browser. Whatever the picture's shape (landscape, portrait), it is shown whole in the
    // frame (contain), on the theme's ink, never stretched.
    function startPreview(host) {
        getItem(host.id, 'Trickplay,MediaSources').then(function (item) {
            if (!host.card.matches(':hover')) { return null; }
            var fallback = function () { if (item.Trickplay && host.card.matches(':hover')) { trickplayPreview(host, item); } };
            if (item.LocationType === 'Virtual' || item.LocalTrailerCount) { return trailerPreview(host, item, fallback); }
            // a real episode: its source's preview video (served by the plugin), then the clip the plugin built from the
            // file (8 short moments spread over the episode), then the file itself, then trickplay
            return videoPreview(host, sourcePreviewUrl(item), null, 'Aperçu de la source', function () {
                if (!host.card.matches(':hover')) { return; }
                videoPreview(host, clipUrl(item), null, 'Extraits (plugin)', function () { filePreview(host, item, fallback); });
            });
        }).catch(function (e) { warn('preview', e); });
    }
    function overlay(host, kind) {
        var layer = el('div', NS + 'preview ' + NS + 'preview-' + kind);
        layer.setAttribute('aria-hidden', 'true');
        host.frame.appendChild(layer);
        host.card.classList.add(NS + 'previewing');
        requestAnimationFrame(function () { layer.classList.add(NS + 'preview-on'); });
        return layer;
    }
    function endPreview(host, layer) { host.card.classList.remove(NS + 'previewing'); layer.remove(); }
    // a muted video in the frame; `segments` (fractions of the duration) makes it jump through the episode
    // `origin` is shown in a corner chip: where the video comes from
    function videoPreview(host, src, segments, origin, fallback) {
        var layer = overlay(host, 'video');
        if (origin) { layer.appendChild(el('span', NS + 'preview-origin', origin)); }
        var video = layer.appendChild(el('video', NS + 'preview-video'));
        var bar = layer.appendChild(el('span', NS + 'preview-bar'));
        video.muted = true; video.playsInline = true; video.preload = 'auto'; video.loop = !segments;
        var k = 0, timer = null, started = false;
        var jump = function () {
            if (!segments || !video.duration || !isFinite(video.duration)) { return; }
            video.currentTime = video.duration * segments[k % segments.length];
            k++;
        };
        var fills = null;
        video.addEventListener('loadedmetadata', function () {
            // the frame takes the video's shape: a portrait video is not cropped to a landscape frame
            if (video.videoWidth && video.videoHeight) { layer.style.setProperty('--pj-preview-ratio', String(video.videoWidth / video.videoHeight)); }
            // narrower than the frame (a portrait video): an ambient light in its own colours around it, not black bars
            var box = host.frame.getBoundingClientRect();
            if (video.videoWidth / video.videoHeight < box.width / box.height - 0.05) { fills = ambientLight(layer, video); }
            jump();
            if (segments) { timer = setInterval(jump, 3500); }
        });
        video.addEventListener('timeupdate', function () { if (video.duration) { bar.style.width = (100 * video.currentTime / video.duration) + '%'; } });
        video.addEventListener('playing', function () { started = true; });
        var giveUp = function () { if (started) { return; } stop(); if (fallback) { fallback(); } };
        video.addEventListener('error', giveUp);
        // a 404 or a codec the browser cannot decode fires "error" at once; the guard only covers a source that never
        // answers (a slow preview host or a first episode-list lookup can take several seconds: that is not a failure)
        var guard = setTimeout(giveUp, 15000);
        video.addEventListener('loadedmetadata', function () { clearTimeout(guard); guard = setTimeout(giveUp, 8000); });
        video.src = src;
        var p = video.play(); if (p && p.catch) { p.catch(function () { /* autoplay refused: the guard falls back */ }); }
        var stop = function () {
            clearInterval(timer); clearTimeout(guard);
            if (fills) { fills.stop(); }
            try { video.pause(); video.removeAttribute('src'); video.load(); } catch (e) { /* gone */ }
            endPreview(host, layer);
            if (preview && preview.layer === layer) { preview = null; }
        };
        preview = { layer: layer, stop: stop };
    }
    function sourcePreviewUrl(item) { return client().getUrl('CustomMetadata/Preview/' + item.Id, { api_key: token() }); }
    function clipUrl(item) { return client().getUrl('CustomMetadata/Preview/' + item.Id + '/Clip', { api_key: token() }); }
    // the file itself only when it is light: a Blu-ray or 4K file would cost tens or hundreds of MB per hover
    var MAX_PREVIEW_BITRATE = 12000000;
    // a narrow (portrait) preview: an ambient light around it, like YouTube's: no picture, only soft clouds of the
    // video's own colours (its four corners), which drift slowly from one colour to the next as the video plays
    function ambientLight(layer, video) {
        layer.classList.add(NS + 'preview-narrow');
        return glowFrom(video, layer.insertBefore(el('div', NS + 'preview-glow'), layer.firstChild));
    }
    // sets the glow's four colours from the video's four corners while it plays
    function glowFrom(video, glow) {
        var cv = document.createElement('canvas'); cv.width = 4; cv.height = 4;
        var g = cv.getContext('2d', { willReadFrequently: true });
        var avg = function (x, y, w, h) {
            var d = g.getImageData(x, y, w, h).data, r = 0, gr = 0, b = 0, n = d.length / 4;
            for (var i = 0; i < d.length; i += 4) { r += d[i]; gr += d[i + 1]; b += d[i + 2]; }
            return 'rgb(' + Math.round(r / n) + ', ' + Math.round(gr / n) + ', ' + Math.round(b / n) + ')';
        };
        var paint = function () {
            if (!video.videoWidth || video.readyState < 2) { return; }
            try {
                g.drawImage(video, 0, 0, 4, 4);
                glow.style.setProperty('--pj-glow-1', avg(0, 0, 2, 2));
                glow.style.setProperty('--pj-glow-2', avg(0, 2, 2, 2));
                glow.style.setProperty('--pj-glow-3', avg(2, 0, 2, 2));
                glow.style.setProperty('--pj-glow-4', avg(2, 2, 2, 2));
            } catch (e) { /* a cross-origin frame cannot be read: the ink stays */ }
        };
        var timer = setInterval(paint, 1500);
        video.addEventListener('playing', paint);
        return { stop: function () { clearInterval(timer); } };
    }
    function filePreview(host, item, fallback) {
        var c = client(), source = (item.MediaSources || [])[0];
        if (!source || item.LocationType === 'Virtual' || !host.card.matches(':hover')) { return fallback(); }
        if (source.Bitrate && source.Bitrate > MAX_PREVIEW_BITRATE) { return fallback(); }
        var src = c.getUrl('Videos/' + item.Id + '/stream', { static: true, MediaSourceId: source.Id, api_key: token() });
        videoPreview(host, src, [0.12, 0.3, 0.48, 0.66, 0.84], 'Fichier de l’épisode', fallback);
    }
    function trailerPreview(host, item, fallback) {
        var c = client();
        return c.getLocalTrailers(c.getCurrentUserId(), item.Id).then(function (trailers) {
            var t = (trailers || [])[0];
            if (!host.card.matches(':hover')) { return; }
            if (!t) { return item.LocationType === 'Virtual' ? fallback() : filePreview(host, item, fallback); }
            videoPreview(host, c.getUrl('Videos/' + t.Id + '/stream', { static: true, api_key: token() }), null, 'Aperçu de la source', fallback);
        });
    }
    // trickplay: picture by picture, so the progress is shown as steps, not as a running line
    function trickplayPreview(host, item) {
        var sources = item.Trickplay, msId = Object.keys(sources || {})[0];
        if (!msId) { return; }
        var widths = Object.keys(sources[msId]).map(Number).sort(function (a, b) { return a - b; });
        // the sharpest set for the frame's real size (screen pixels), else the largest there is
        var need = host.frame.getBoundingClientRect().width * (window.devicePixelRatio || 1);
        var w = widths.find(function (x) { return x >= need; }) || widths[widths.length - 1];
        var info = sources[msId][w];
        if (!info || !info.ThumbnailCount) { return; }
        var perTile = info.TileWidth * info.TileHeight;
        var c = client();
        var layer = overlay(host, 'trickplay');
        layer.appendChild(el('span', NS + 'preview-origin', 'Vignettes Jellyfin'));
        layer.style.setProperty('--pj-preview-ratio', String(info.Width / info.Height));
        var pic = layer.appendChild(el('div', NS + 'preview-pic'));
        var steps = layer.appendChild(el('div', NS + 'preview-steps'));
        var count = Math.min(info.ThumbnailCount, 20), dots = [];
        for (var d = 0; d < count; d++) { dots.push(steps.appendChild(el('i'))); }
        var stride = Math.max(1, Math.floor(info.ThumbnailCount / count)), n = 0;
        var show = function () {
            var i = (n * stride) % info.ThumbnailCount;
            var tile = Math.floor(i / perTile), inTile = i % perTile;
            var col = inTile % info.TileWidth, row = Math.floor(inTile / info.TileWidth);
            pic.style.backgroundImage = 'url("' + c.getUrl('Videos/' + item.Id + '/Trickplay/' + w + '/' + tile + '.jpg', { MediaSourceId: msId, api_key: token() }) + '")';
            pic.style.backgroundSize = (info.TileWidth * 100) + '% ' + (info.TileHeight * 100) + '%';
            pic.style.backgroundPosition = (info.TileWidth > 1 ? col * 100 / (info.TileWidth - 1) : 0) + '% ' + (info.TileHeight > 1 ? row * 100 / (info.TileHeight - 1) : 0) + '%';
            var at = Math.min(count - 1, Math.floor(i / info.ThumbnailCount * count));
            dots.forEach(function (dot, k) { dot.className = k < at ? NS + 'done' : k === at ? NS + 'now' : ''; });
            n = (n + 1) % count;
        };
        show();
        var timer = setInterval(show, 700);
        preview = { layer: layer, stop: function () { clearInterval(timer); endPreview(host, layer); } };
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
        try { leaveCanvas(); } catch (e) { warn('header reset', e); }
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
            if (!(t.closest && t.closest('[data-pj], .' + NS + 'preview, .' + NS + 'viewer'))) { schedule(); return; }
        }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
    schedule();
})();
