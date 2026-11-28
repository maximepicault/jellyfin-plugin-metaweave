/*
 * Projection theme - the "Index": jellyfin-web 10.11's filtered lists (genre, tag, studio, person) and its series,
 * episode and film library screens, rebuilt.
 *
 * Served by the Custom Metadata plugin (CustomMetadata/Theme/projection-index.js) and added to jellyfin-web's page with
 * projection.js when the plugin's "Améliorations du thème Projection" setting is on. It only acts when the theme's
 * stylesheet is the one that styles it (--pj-index), only reads, and leaves the stock page as it is for anything it
 * does not handle (music, live TV, favourites, the libraries' other views).
 *
 * The screen:
 * - the filter is the title, written over a picture of the results; a click on a word removes it;
 * - one ribbon: item types, a few useful refinements, "Tous les filtres", the sort order;
 * - results by type, episodes grouped by series, with jellyfin-web's own play and item menu actions;
 * - the index ("Tous les filtres" or "/"): every genre, tag, studio, person, year and playback state across the whole
 *   width, with one search field for all of them.
 * Genres, tags, studios and people narrow each other; years and playback states widen within their own kind.
 * Missing episodes are left out until the ribbon's « Manquants » switch (or the « Manquants » playback state) asks for them.
 *
 * Nothing is filtered in the browser: the plugin (CustomMetadata/Index) answers one page of results and the filters on
 * offer, and the items of that page are then read from Jellyfin; the next page comes as one scrolls.
 * Labels are French, like the rest of the theme.
 */
(function () {
    'use strict';
    if (window.__projectionIndex) { return; }
    window.__projectionIndex = '1';

    var warn = function (where, error) { try { console.warn('[projection index] ' + where, error); } catch (e) { /* no console */ } };

    // ---------- vocabulary ----------
    // tab order, and the order of the sections of "Tout": [label, singular, plural]
    var TYPES = { '': ['Tout', 'élément', 'éléments'], Series: ['Séries', 'série', 'séries'], Movie: ['Films', 'film', 'films'], Season: ['Saisons', 'saison', 'saisons'], Episode: ['Épisodes', 'épisode', 'épisodes'], Video: ['Vidéos', 'vidéo', 'vidéos'] };
    var TYPE_ORDER = ['', 'Series', 'Movie', 'Season', 'Episode', 'Video'];
    var ALL_TITLES = { '': 'Toute la bibliothèque', Series: 'Toutes les séries', Movie: 'Tous les films', Season: 'Toutes les saisons', Episode: 'Tous les épisodes', Video: 'Toutes les vidéos' };
    var FACETS = { genres: 'Genres', tags: 'Étiquettes', studios: 'Studios', people: 'Personnes', years: 'Années', state: 'Lecture' };
    var TITLE_ORDER = ['genres', 'tags', 'studios', 'people', 'years', 'state', 'letter'];
    var JOIN = { genres: '+', tags: '+', studios: 'chez', people: 'avec', years: 'en', state: '', letter: 'en' };
    var STATES = { unseen: 'Non vus', resume: 'En cours', seen: 'Vus', miss: 'Manquants' };
    var SORTS = [['serie', 'Par série'], ['name', 'Par nom'], ['date', 'Plus récents'], ['oldest', 'Plus anciens'], ['added', 'Ajoutés récemment'], ['rating', 'Les mieux notés'], ['random', 'Au hasard']];
    // the other views a user may choose as a library's default one: they stay jellyfin-web's
    var STOCK_VIEWS = ['suggestions', 'favorites', 'collections', 'genres', 'studios', 'upcoming', 'playlists'];
    // the views of a library that stay jellyfin-web's, with their tab number (experimental layout)
    // a library's views and their `tab=` number, as jellyfin-web's own view menu lists them
    var VIEWS = {
        tv: [['Séries', 0], ['Épisodes', 5], ['Suggestions', 1], ['À venir', 2], ['Genres', 3], ['Réseaux TV', 4], ['Collections', 6], ['Listes de lecture', 7]],
        movies: [['Films', 0], ['Suggestions', 1], ['Favoris', 2], ['Collections', 3], ['Genres', 4], ['Studios', 5], ['Listes de lecture', 6]]
    };
    var LETTERS = '#ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
    var PAGE = 60, MAX_PAGE = 600, PEOPLE_STEP = 240;

    // ---------- helpers ----------
    function client() { return window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId() ? window.ApiClient : null; }
    // elements are always built with text nodes: names and tags come from the library and are never read as HTML
    function h(tag, attrs, children) {
        var e = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) {
            var v = attrs[k];
            if (v == null || v === false) { return; }
            if (k === 'text') { e.textContent = v; } else { e.setAttribute(k, v === true ? '' : v); }
        });
        [].concat(children || []).forEach(function (c) { if (c != null && c !== false && c !== '') { e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); } });
        return e;
    }
    function button(cls, attrs, children) { return h('button', Object.assign({ type: 'button', 'class': cls }, attrs), children); }
    function icon(name) { return h('span', { 'class': 'material-icons ' + name, 'aria-hidden': 'true' }); }
    function nf(n) { try { return Number(n).toLocaleString('fr-FR'); } catch (e) { return String(n); } }
    function count(n, type) { var t = TYPES[type] || TYPES['']; return nf(n) + ' ' + (n > 1 ? t[2] : t[1]); }
    function plain(s) { return String(s).normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase(); }
    function byName(a, b) { return String(a).localeCompare(String(b), 'fr', { numeric: true }); }
    // not in the TV layout: jellyfin-web's own list is made for a remote control
    function themed() { var root = document.documentElement; return !root.classList.contains('layout-tv') && getComputedStyle(root).getPropertyValue('--pj-index').trim() === '1'; }
    function currentPage() { return document.querySelector('.page:not(.hide)'); }
    function serverId() { var c = client(); return c && c.serverId ? c.serverId() : ''; }
    function detailsHref(id) { return '#/details?id=' + id + (serverId() ? '&serverId=' + serverId() : ''); }
    function image(id, type, tag, width) { var o = { type: type, maxWidth: width, quality: 90 }; if (tag) { o.tag = tag; } return client().getImageUrl(id, o); }
    function params() { var at = location.hash.indexOf('?'); return new URLSearchParams(at < 0 ? '' : location.hash.slice(at + 1)); }
    function item(id) { var c = client(); return c.getJSON(c.getUrl('Items/' + id, { userId: c.getCurrentUserId() })); }

    // ---------- which pages are ours ----------
    // a genre, tag, studio or person list of videos...
    function listRoute() {
        if (!/^#\/list(\.html)?\?/.test(location.hash)) { return null; }
        var q = params(), type = q.get('type'), base = null;
        if (q.get('genreId')) { base = { k: 'genres', id: q.get('genreId') }; }
        else if (q.get('studioId')) { base = { k: 'studios', id: q.get('studioId') }; }
        else if (q.get('personId')) { base = { k: 'people', id: q.get('personId') }; }
        else if (type === 'tag' && q.get('tag')) { base = { k: 'tags', v: q.get('tag') }; }
        else { return null; }
        if (type === 'MusicVideo') { type = 'Video'; }
        if (type && type !== 'tag' && !TYPES[type]) { return null; }   // music, programmes...: jellyfin-web's list
        if (q.get('IsFavorite')) { return null; }
        return { base: base, type: type && TYPES[type] ? type : '', parentId: q.get('parentId'), library: null };
    }
    // the view an address of the experimental layout opens: the one it names, else the user's default one for this
    // library (jellyfin-web's own « landing » setting, kept by the browser). null: a view left to jellyfin-web
    function addressedType(library, parent) {
        var main = library === 'tv' ? 'Series' : 'Movie', number = params().get('tab');
        if (number !== null) { return number === '0' ? main : number === '5' && library === 'tv' ? 'Episode' : null; }
        var landing = null, c = client();
        try { landing = c && localStorage.getItem(c.getCurrentUserId() + '-landing-' + parent); } catch (e) { /* no storage */ }
        if (landing === 'episodes' && library === 'tv') { return 'Episode'; }
        return STOCK_VIEWS.indexOf(landing) < 0 ? main : null;
    }
    // ...and the series, episode and film views of a library. Returns the element that holds the screen: the page
    // (experimental layout, the view in the address) or the open tab (classic layout)
    function libraryTarget(page) {
        var m = /^#\/(tv|movies)(\.html)?\?/.exec(location.hash);
        var parent = m && params().get('topParentId');
        if (!parent || !page) { return null; }
        var library = m[1], type = null, host = null;
        var tab = page.querySelector('.pageTabContent.is-active');
        if (page.querySelector('.pageTabContent')) {
            if (!tab) { return null; }
            type = library === 'tv' ? (tab.id === 'seriesTab' ? 'Series' : tab.id === 'episodesTab' ? 'Episode' : null) : tab.id === 'moviesTab' ? 'Movie' : null;
            // the page being left may still be shown for a moment
            host = page.__pjView && page.__pjView !== location.hash ? null : tab;
        } else if (page.id === (library === 'tv' ? 'tvshowsPage' : 'moviesPage')) {
            type = addressedType(library, parent);
            host = page;
        }
        return type ? { host: host, route: { base: null, type: type, parentId: parent, library: library, tabbed: !!tab } } : { host: null, route: null };
    }

    // ---------- data ----------
    // the filters as the plugin reads them
    function filters(ix) {
        var out = {};
        Object.keys(ix.sel).forEach(function (k) { if (ix.sel[k].size) { out[k] = Array.from(ix.sel[k]); } });
        return out;
    }
    function query(ix, more) {
        var c = client();
        var body = Object.assign({ ParentId: ix.route.parentId || null, Filters: filters(ix), Type: ix.type, Sort: ix.sort, Seed: ix.seed, Missing: ix.missing }, more);
        if (body.Limit) { body.Details = true; }
        return c.ajax({ type: 'POST', url: c.getUrl('CustomMetadata/Index'), data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' }).then(function (answer) {
            Object.keys(answer.People || {}).forEach(function (name) { ix.people.set(name, answer.People[name]); });
            return answer;
        });
    }
    // the items of a page of results, sent with it, in the page's order (one the user may no longer read is not among them)
    function details(answer) {
        var byId = new Map();
        (answer.Details || []).forEach(function (it) { byId.set(it.Id.replace(/-/g, ''), it); });
        return (answer.Items || []).map(function (r) { return { group: r.Group, raw: byId.get(r.Id.replace(/-/g, '')) }; }).filter(function (r) { return r.raw; });
    }
    function signature(rows) { return JSON.stringify(rows); }

    // ---------- seen before ----------
    // a screen shown again draws at once what it showed last (kept in memory only), then asks again and redraws what changed
    var SEEN_MAX = 30, seen = new Map(), headings = new Map();
    function hold(map, key, value) { map.delete(key); map.set(key, value); if (map.size > SEEN_MAX) { map.delete(map.keys().next().value); } }
    function seenKey(ix, wanted) {
        var r = ix.route;
        return JSON.stringify([client().getCurrentUserId(), r.parentId, r.library, r.base && r.base.k, filters(ix), ix.type, ix.sort, ix.sort === 'random' ? ix.seed : 0, ix.missing, wanted]);
    }
    // an item the header names: the one already read at once, read again for the next time
    function heading(id) {
        var key = client().getCurrentUserId() + ' ' + id, had = headings.get(key);
        var asked = item(id).then(function (it) { hold(headings, key, it); return it; });
        if (!had) { return asked; }
        asked.catch(function () { headings.delete(key); });
        return Promise.resolve(had);
    }

    // asks for the first page under the current filters. `keep`: as many results as are shown (back on a screen);
    // `quiet`: the screen is only redrawn if something changed
    // `named`: on the first load, the requests for the names the header shows, made beside the search
    function load(ix, keep, quiet, named) {
        var run = ++ix.run, alive = function () { return run === ix.run && ix.host.__pjIx === ix; };
        var wanted = keep ? Math.min(MAX_PAGE, Math.max(PAGE, keep)) : PAGE;
        ix.growing = false;   // a next page on its way no longer belongs here
        ix.refreshing = false;
        var had = named && seen.get(seenKey(ix, wanted));
        if (had) {
            return named.then(function () {
                if (!alive()) { return null; }
                show(ix, had.answer, had.rows, had.asked, true);
                var again = load(ix, wanted, true);
                ix.refreshing = true;   // no next page until the first ones are checked
                return again.catch(function (e) {
                    // no longer allowed or gone: nothing kept may stay on screen; a passing failure leaves it there
                    if (e && [401, 403, 404].indexOf(e.status) >= 0) { seen.clear(); throw e; }
                    ix.refreshing = false;
                    warn('refresh', e);
                });
            });
        }
        if (!quiet) { ix.busy = true; ix.root.classList.add('pj-ix-busy'); header(ix); ribbon(ix); }
        return Promise.all([query(ix, { Summary: true, StartIndex: 0, Limit: wanted }), named]).then(function (both) {
            var answer = both[0];
            if (!alive()) { return null; }
            // a type with nothing left under these filters: show everything (a library keeps its type)
            if (ix.type && !answer.Total && answer.Types[''] && !ix.route.library) { ix.type = ''; return load(ix, keep, quiet); }
            var rows = details(answer);
            var changed = !quiet || signature(rows) !== signature(ix.rows) || answer.Total !== ix.answer.Total;
            show(ix, answer, rows, (answer.Items || []).length, changed);
        });
    }
    function show(ix, answer, rows, asked, redraw) {
        ix.answer = answer; ix.rows = rows.slice(); ix.groups = Object.assign({}, answer.Groups); ix.asked = asked;
        ix.busy = false; ix.loaded = true; ix.refreshing = false; ix.root.classList.remove('pj-ix-busy');
        header(ix); ribbon(ix); letters(ix);
        if (redraw) { drawResults(ix); }
        remember(ix);
        if (panel && panel.ix === ix) { refreshPanel(); }
        // the end may be in view already: watch again
        if (ix.watch) { ix.watch.unobserve(ix.more); ix.watch.observe(ix.more); }
    }
    // the next page, appended
    function grow(ix) {
        if (ix.busy || ix.growing || ix.refreshing || !ix.loaded || ix.asked >= ix.answer.Total) { return; }
        var run = ix.run, alive = function () { return run === ix.run && ix.host.__pjIx === ix; };
        ix.growing = true;
        query(ix, { StartIndex: ix.asked, Limit: PAGE }).then(function (answer) {
            if (!alive()) { return; }
            var asked = (answer.Items || []).length, rows = details(answer);
            // an empty page: nothing more to ask for, whatever the total said
            ix.asked = asked ? ix.asked + asked : ix.answer.Total;
            Object.assign(ix.groups, answer.Groups || {});
            ix.rows = ix.rows.concat(rows);
            append(ix, rows);
            remember(ix);
            ix.growing = false;
            // still in view after this page: watch again
            if (ix.watch) { ix.watch.unobserve(ix.more); ix.watch.observe(ix.more); }
        }).catch(function (e) { warn('next page', e); }).then(function () { if (run === ix.run) { ix.growing = false; } });
    }

    // ---------- filtering ----------
    function has(ix, k, v) { return !!(ix.sel[k] && ix.sel[k].has(v)); }
    function chosen(ix) { var out = []; TITLE_ORDER.forEach(function (k) { if (ix.sel[k]) { ix.sel[k].forEach(function (v) { out.push([k, v]); }); } }); return out; }
    function label(k, v) { return k === 'state' ? STATES[v] || v : v; }
    function toggle(ix, k, v) {
        var set = ix.sel[k] || new Set(), had = set.has(v);
        if (k === 'letter') { set.clear(); }   // one letter at a time, as in jellyfin-web
        if (had) { set.delete(v); } else { set.add(v); }
        ix.sel[k] = set;
        changed(ix);
    }
    function changed(ix) {
        // further down than the new results begin: back to their top
        var top = Math.max(0, ix.heroBox.offsetHeight - parseFloat(getComputedStyle(ix.ribbonBox).top || 0));
        if (window.scrollY > top && !panel) { window.scrollTo(0, top); }
        load(ix).catch(function (e) { failed(ix, e); });
        if (panel && panel.ix === ix) { panelChosen(); }
    }

    // ---------- the screen ----------
    var memory = new Map();   // history entry → filters, so that "back" finds the screen as it was left
    // (the classic layout keeps one address for all of a library's tabs: each has its own memory)
    function visitKey(r) { return ((history.state && history.state.key) || location.hash) + '|' + location.hash + '|' + (r.library ? r.library + r.type : ''); }

    function build(host, r) {
        if (host.__pjIx) { drop(host.__pjIx); }
        var root = h('div', { 'class': 'pj-ix' + (r.library ? ' pj-ix-library' : '') });
        var ix = { host: host, route: r, hash: location.hash, key: visitKey(r), keyed: !!(history.state && history.state.key), root: root, run: 0, loaded: false, busy: true, rows: [], groups: {}, asked: 0, answer: null, ids: {}, people: new Map(), person: null, scope: null, sel: {}, type: r.type, sort: r.library && r.type !== 'Episode' ? 'name' : 'serie', seed: Math.floor(Math.random() * 100000), missing: false, picture: null };
        root.__pjIx = ix;
        ix.heroBox = root.appendChild(h('header', { 'class': 'pj-ix-hero' }));
        ix.picBox = ix.heroBox.appendChild(h('div', { 'class': 'pj-ix-pic', 'aria-hidden': 'true' }));
        ix.titleBox = ix.heroBox.appendChild(h('div', { 'class': 'pj-ix-title' }));
        ix.ribbonBox = root.appendChild(h('div', { 'class': 'pj-ix-ribbon' }));
        // jellyfin-web's own container element: its item actions (play, menu, right click) work on our cards
        var holder = root.appendChild(h('div', { 'class': 'pj-ix-results' }));
        holder.innerHTML = '<div is="emby-itemscontainer" class="pj-ix-list"></div>';
        ix.list = holder.firstChild;
        ix.list.notifyRefreshNeeded = function () { load(ix, ix.rows.length, true).catch(function (e) { warn('refresh', e); }); };
        ix.more = root.appendChild(h('div', { 'class': 'pj-ix-more', 'aria-hidden': 'true' }));
        ix.letterBox = holder.appendChild(h('nav', { 'class': 'pj-ix-alpha', 'aria-label': 'Première lettre' }));
        root.addEventListener('click', function (ev) { onClick(ix, ev); });
        root.addEventListener('change', function (ev) {
            if (!ev.target.classList.contains('pj-ix-sort')) { return; }
            ix.sort = ev.target.value;
            if (ix.sort === 'random') { ix.seed = Math.floor(Math.random() * 100000); }
            changed(ix);
        });
        host.__pjIx = ix;
        host.classList.add('pj-ix-on');
        host.appendChild(root);
        place(ix);
        if (window.IntersectionObserver) {
            ix.stuck = new IntersectionObserver(function (e) { ix.ribbonBox.classList.toggle('pj-ix-stuck', !e[e.length - 1].isIntersecting); }, { rootMargin: '-90px 0px 0px' });
            ix.stuck.observe(ix.heroBox);
            ix.watch = new IntersectionObserver(function (e) { if (e[e.length - 1].isIntersecting) { grow(ix); } }, { rootMargin: '0px 0px 900px' });
            ix.watch.observe(ix.more);
        }
        window.scrollTo(0, 0);
        header(ix); ribbon(ix);
        ix.list.appendChild(h('p', { 'class': 'pj-ix-empty', text: 'Chargement…' }));
        start(ix).catch(function (e) { failed(ix, e); });
    }
    function drop(ix) {
        ix.run++;
        if (ix.stuck) { ix.stuck.disconnect(); }
        if (ix.watch) { ix.watch.disconnect(); }
        ix.root.remove();
        ix.host.classList.remove('pj-ix-on');
        ix.host.__pjIx = null;
    }
    // where the header ends, and how much of the screen it covers (Jellyfin 12 starts pages below its app bar: nothing)
    function place(ix) {
        var mui = document.querySelector('.MuiAppBar-root');
        var bar = mui || document.querySelector('.skinHeader');
        var height = bar ? Math.round(bar.getBoundingClientRect().height) : 0;
        var start = ix.root.offsetParent ? Math.round(ix.root.getBoundingClientRect().top + window.scrollY) : 0;
        var covered = Math.max(0, height - start);
        var wanted = height + 'px|' + covered + 'px';
        if (ix.placed !== wanted) {
            ix.placed = wanted;
            ix.root.style.setProperty('--pj-ix-top', height + 'px');
            ix.root.style.setProperty('--pj-ix-covered', covered + 'px');
        }
    }
    var ignored = new Set();   // screens left to jellyfin-web: a list with no video, or one that could not be built
    function failed(ix, error) {
        warn('load', error);
        ignored.add(ix.hash);
        if (panel && panel.ix === ix) { closePanel(); }
        drop(ix);
        veil();
    }
    // names the base filter of the address, restores the filters of a visit already made, then loads
    function start(ix) {
        var r = ix.route, waits = [];
        if (r.base && r.base.id) {
            waits.push(heading(r.base.id).then(function (it) {
                if (/^Music/.test(it.Type)) { throw new Error('music list'); }
                r.base.v = it.Name;
                if (r.base.k === 'people') { ix.person = it; ix.people.set(it.Name, { Id: it.Id, HasImage: !!(it.ImageTags && it.ImageTags.Primary) }); }
            }));
        }
        if (r.parentId) {
            waits.push(heading(r.parentId).then(function (it) { ix.scope = it.Name; }).catch(function () { /* unnamed */ }));
        }
        var named = Promise.all(waits);
        var search = function () {
            if (ix.host.__pjIx !== ix) { return null; }
            var kept = memory.get(ix.key);
            if (kept) {
                Object.keys(kept.sel).forEach(function (k) { ix.sel[k] = new Set(kept.sel[k]); });
                ix.type = kept.type; ix.sort = kept.sort; ix.seed = kept.seed; ix.missing = kept.missing;
            } else if (r.base) {
                ix.sel[r.base.k] = new Set([r.base.v]);
            }
            return load(ix, kept && kept.shown, false, named).then(function () {
                // nothing at all on arrival: not a list of videos
                if (ix.host.__pjIx === ix && r.base && !kept && ix.answer && !ix.answer.Types[''] && !ix.answer.Missing) { throw new Error('no video in this list'); }
            });
        };
        // a genre, studio or person is filtered by its name, which must come first; nothing else waits for one
        return r.base && r.base.id ? named.then(search) : search();
    }
    function remember(ix) {
        var sel = {};
        Object.keys(ix.sel).forEach(function (k) { sel[k] = Array.from(ix.sel[k]); });
        memory.set(ix.key, { sel: sel, type: ix.type, sort: ix.sort, seed: ix.seed, missing: ix.missing, shown: ix.rows.length });
        if (ix.answer && !ix.busy && ix.rows.length <= MAX_PAGE) {
            hold(seen, seenKey(ix, Math.max(PAGE, ix.rows.length)), { answer: Object.assign({}, ix.answer, { Groups: Object.assign({}, ix.groups) }), rows: ix.rows.slice(), asked: ix.asked });
        }
    }

    // ---------- header ----------
    function personFace(ix, name, width) {
        var p = ix.people.get(name);
        if (p && p.HasImage) { return h('img', { 'class': 'pj-ix-face-img', alt: '', loading: 'lazy', src: image(p.Id, 'Primary', null, width) }); }
        return h('span', { 'class': 'pj-ix-face-img pj-ix-initials', 'aria-hidden': 'true', text: name.split(/\s+/).map(function (w) { return w.charAt(0); }).slice(0, 2).join('') });
    }
    function header(ix) {
        var terms = chosen(ix);
        var title = h('h1', { 'class': 'pj-ix-h1' + (terms.length > 2 || terms.some(function (t) { return t[1].length > 18; }) ? ' pj-ix-long' : '') });
        var last = null;
        terms.forEach(function (t, i) {
            var k = t[0], join = i === 0 ? (k === 'people' || k === 'studios' || k === 'years' || k === 'letter' ? JOIN[k] : '') : (k === last && (k === 'years' || k === 'state')) ? 'ou' : JOIN[k] || ',';
            last = k;
            if (join) { title.appendChild(h('span', { 'class': 'pj-ix-join', text: join })); }
            title.appendChild(button('pj-ix-term', { 'data-k': k, 'data-v': t[1], title: 'Retirer ce filtre', 'aria-label': 'Retirer le filtre ' + label(k, t[1]) }, [
                k === 'people' ? personFace(ix, t[1], 160) : null, h('b', { text: label(k, t[1]) }), h('i', { 'aria-hidden': 'true', text: '✕' })]));
        });
        // jellyfin-web 12 gives the library's view menu a whole row of its bar: the title offers the views instead
        var views = !!ix.route.library && !ix.route.tabbed;
        if (!terms.length) {
            var name = !ix.route.library ? (!ix.loaded && ix.route.base ? ix.route.base.v || '…' : ALL_TITLES[ix.type]) : views && ix.route.type === 'Episode' ? 'Épisodes' : ix.scope || '…';
            title.appendChild(views ? viewSwitch(name, 'pj-ix-viewtitle') : h('span', { text: name }));
        }
        title.appendChild(button('pj-ix-plus', { 'data-open': 'all', 'aria-label': 'Ajouter un filtre', title: 'Ajouter un filtre', text: '+' }));
        ix.heroBox.classList.toggle('pj-ix-plain', !!ix.route.library && !terms.length);
        var under = h('div', { 'class': 'pj-ix-under-title' });
        if (!ix.busy && ix.answer.Playable) {
            under.appendChild(button('pj-ix-btn pj-ix-go', { 'data-play': 'all' }, [icon('play_arrow'), 'Tout lire']));
            under.appendChild(button('pj-ix-btn', { 'data-play': 'shuffle' }, [icon('shuffle'), 'Aléatoire']));
        }
        var sum = 'Chargement…';
        if (!ix.busy) {
            sum = count(ix.answer.Total, ix.type);
            var series = (ix.type === 'Episode' || ix.type === 'Season') && ix.answer.SeriesCount;
            if (series) { sum += ' dans ' + count(series, 'Series'); }
            if (ix.scope && !ix.route.library) { sum += (series ? ', bibliothèque ' : ' dans la bibliothèque ') + ix.scope; }
        }
        under.appendChild(h('p', { 'aria-live': 'polite', text: sum }));
        var head = title;
        if (views) {
            // filtered, the title says the filter: the view it applies to stands above it
            head = h('div', { 'class': 'pj-ix-viewbox' }, terms.length
                ? [viewSwitch((ix.scope || '…') + (ix.route.type === 'Episode' ? ' · Épisodes' : ''), 'pj-ix-eyebrow'), viewMenu(ix), title]
                : [title, viewMenu(ix)]);
        }
        ix.titleBox.replaceChildren(head, under);
        picture(ix);
    }
    // ---------- the library's views, in the header ----------
    function viewSwitch(text, cls) {
        return button('pj-ix-view ' + cls, { 'data-views': true, 'aria-expanded': 'false', title: 'Changer de vue' }, [h('span', { text: text }), icon('expand_more')]);
    }
    function viewMenu(ix) {
        var r = ix.route, here = r.type === 'Episode' ? 5 : 0;
        var address = '#/' + r.library + '?topParentId=' + r.parentId + '&collectionType=' + (r.library === 'tv' ? 'tvshows' : 'movies') + '&tab=';
        return h('nav', { 'class': 'pj-ix-menu', 'aria-label': 'Vues de la bibliothèque', hidden: true }, VIEWS[r.library].map(function (v) {
            return h('a', { href: address + v[1], 'aria-current': v[1] === here ? 'page' : null, text: v[0] });
        }));
    }
    function openViewBox() {
        var menu = document.querySelector('.pj-ix-menu:not([hidden])');
        return menu ? { menu: menu, box: menu.parentElement, button: menu.parentElement.querySelector('.pj-ix-view') } : null;
    }
    function closeViews(refocus) {
        var open = openViewBox();
        if (!open) { return; }
        open.menu.hidden = true;
        open.button.setAttribute('aria-expanded', 'false');
        if (refocus) { open.button.focus(); }
    }
    function toggleViews(opener, byKeyboard) {
        var menu = opener.closest('.pj-ix-viewbox').querySelector('.pj-ix-menu'), show = menu.hidden;
        closeViews();
        menu.hidden = !show;
        opener.setAttribute('aria-expanded', String(show));
        // under the button that opened it, whether that is the title or the line above it
        menu.style.top = (opener.offsetTop + opener.offsetHeight + 6) + 'px';
        if (show && byKeyboard) { (menu.querySelector('[aria-current]') || menu.firstChild).focus(); }
    }
    document.addEventListener('click', function (ev) {
        var open = openViewBox();
        if (open && (ev.target.closest('.pj-ix-menu a') || !(ev.target.closest('.pj-ix-view') || open.menu.contains(ev.target)))) { closeViews(); }
    }, true);
    document.addEventListener('keydown', function (ev) {
        var open = openViewBox();
        if (!open) { return; }
        if (ev.key === 'Escape') { ev.preventDefault(); closeViews(true); return; }
        if (ev.key !== 'ArrowDown' && ev.key !== 'ArrowUp') { return; }
        ev.preventDefault(); ev.stopPropagation();
        var links = Array.prototype.slice.call(open.menu.children), at = links.indexOf(document.activeElement), step = ev.key === 'ArrowDown' ? 1 : -1;
        links[at < 0 ? 0 : (at + step + links.length) % links.length].focus();
    }, true);

    // the picture behind the title: the person's own, else a landscape backdrop of a result, else an episode's still
    function pictures(ix) {
        var out = [], seen = new Set();
        var add = function (id, type, tag, owner) { if (id && tag && !seen.has(id + tag)) { seen.add(id + tag); out.push({ key: id + tag, owner: owner, id: id, type: type, tag: tag }); } };
        var person = ix.person;
        if (person && has(ix, 'people', person.Name) && person.BackdropImageTags && person.BackdropImageTags.length) { add(person.Id, 'Backdrop', person.BackdropImageTags[0]); }
        ix.rows.slice(0, PAGE).forEach(function (row) {
            var r = row.raw;
            if (r.BackdropImageTags && r.BackdropImageTags.length) { add(r.Id, 'Backdrop', r.BackdropImageTags[0]); }
            if (r.ParentBackdropImageTags && r.ParentBackdropImageTags.length) { add(r.ParentBackdropItemId, 'Backdrop', r.ParentBackdropImageTags[0]); }
        });
        ix.rows.slice(0, 12).forEach(function (row) { if (row.raw.Type === 'Episode' && row.raw.ImageTags) { add(row.raw.Id, 'Primary', row.raw.ImageTags.Primary); } });
        return out;
    }
    function picture(ix) {
        if (ix.busy) { return; }
        var candidates = pictures(ix);
        // the same picture stays while it still belongs to the results
        if (ix.picture && candidates.some(function (c) { return c.key === ix.picture; })) { return; }
        var turn = ix.pictureTurn = (ix.pictureTurn || 0) + 1;
        var next = function (k) {
            var c = candidates[k];
            if (turn !== ix.pictureTurn) { return; }
            if (!c || k > 7) { ix.picture = null; ix.picBox.replaceChildren(); ix.heroBox.classList.add('pj-ix-bare'); paint(ix, null); return; }
            var img = new Image();
            img.alt = '';
            img.onload = function () {
                if (turn !== ix.pictureTurn) { return; }
                // never a portrait or square picture stretched across the screen
                if (img.naturalWidth / img.naturalHeight < 1.25) { next(k + 1); return; }
                ix.picture = c.key; ix.heroBox.classList.remove('pj-ix-bare'); paint(ix, img);
            };
            img.onerror = function () { next(k + 1); };
            img.src = image(c.id, c.type, c.tag, Math.min(1920, Math.round(window.innerWidth * (window.devicePixelRatio || 1))));
        };
        next(0);
    }

    // the header of a detail page, around the results' picture: the picture whole in the middle, the ambient light of its
    // own colours on both sides, and the page's ground in its deep shade (projection.js reads the colours)
    function paint(ix, img) {
        var colours = null;
        try { colours = img && window.__projectionColours ? window.__projectionColours(img) : null; } catch (e) { warn('colours', e); }
        ix.art = colours;
        ix.picBox.removeAttribute('style');
        if (img) {
            ix.heroBox.style.setProperty('--pj-ix-ratio', String(img.naturalWidth / img.naturalHeight));
            Object.keys(colours || {}).forEach(function (name) { if (name.indexOf('--pj-amb-') === 0) { ix.picBox.style.setProperty(name, colours[name]); } });
            ix.picBox.replaceChildren(h('div', { 'class': 'pj-aff-fill pj-aff-fill-l' }), h('div', { 'class': 'pj-aff-fill pj-aff-fill-r' }), h('div', { 'class': 'pj-ix-art' }, img));
        } else {
            ix.heroBox.style.removeProperty('--pj-ix-ratio');
        }
        followBar();
    }

    // ---------- ribbon and letters ----------
    function ribbon(ix) {
        var answer = ix.answer || {};
        var types = h('div', { 'class': 'pj-ix-types', role: 'group', 'aria-label': 'Type' });
        var per = answer.Types || {};
        // a films library has nothing but films
        var offered = ix.route.library === 'movies' ? [] : TYPE_ORDER.filter(function (t) { return per[t] || ix.type === t; });
        offered.forEach(function (t) {
            types.appendChild(button('pj-ix-type', { 'data-type': t, 'aria-pressed': String(ix.type === t) }, [TYPES[t][0], ix.answer ? h('small', { text: nf(per[t] || 0) }) : null]));
        });
        types.hidden = !offered.length;
        var sugg = h('div', { 'class': 'pj-ix-sugg', role: 'group', 'aria-label': 'Affiner' });
        (ix.busy ? [] : answer.Suggestions || []).forEach(function (s) {
            sugg.appendChild(button('pj-ix-suggest', { 'data-k': s.Facet, 'data-v': s.Value }, [s.Facet === 'people' ? personFace(ix, s.Value, 60) : null, s.Value, h('small', { text: nf(s.Count) })]));
        });
        var mini = button('pj-ix-mini', { 'data-top': true, title: 'Revenir en haut', text: chosen(ix).map(function (c) { return label(c[0], c[1]); }).join(' + ') });
        var open = button('pj-ix-open', { 'data-open': 'all', 'aria-haspopup': 'dialog' }, [icon('tune'), 'Tous les filtres', h('kbd', { 'aria-hidden': 'true', text: '/' })]);
        var sort = h('select', { 'class': 'pj-ix-sort', 'aria-label': 'Trier' }, SORTS.filter(function (s) { return s[0] !== 'serie' || ix.type === '' || ix.type === 'Episode' || ix.type === 'Season' || ix.sort === 'serie' && !ix.route.library; }).map(function (s) { return h('option', { value: s[0], text: s[1] }); }));
        sort.value = ix.sort;
        // missing episodes are left out until asked for; a chosen playback state decides by itself
        var missing = !(ix.sel.state && ix.sel.state.size) && (ix.missing || answer.Missing) ? button('pj-ix-missing', { 'data-missing': true, 'aria-pressed': String(ix.missing), title: ix.missing ? 'Masquer les manquants' : 'Afficher aussi les manquants' }, [
            icon(ix.missing ? 'visibility' : 'visibility_off'), 'Manquants', h('small', { text: nf(answer.Missing || 0) })]) : null;
        var focused = ix.ribbonBox.contains(document.activeElement) ? document.activeElement : null;
        var again = focused && (focused.getAttribute('data-type') != null ? '[data-type="' + focused.getAttribute('data-type') + '"]' : focused.classList.contains('pj-ix-sort') ? '.pj-ix-sort' : focused.classList.contains('pj-ix-open') ? '.pj-ix-open' : focused.classList.contains('pj-ix-missing') ? '.pj-ix-missing' : null);
        ix.ribbonBox.replaceChildren.apply(ix.ribbonBox, [mini, types, sugg, missing, open, sort].filter(Boolean));
        if (again) { var node = ix.ribbonBox.querySelector(again); if (node) { node.focus({ preventScroll: true }); } }
    }
    // a library sorted by name keeps jellyfin-web's letter column
    function letters(ix) {
        var shown = !!ix.route.library && (ix.sort === 'name' || ix.sort === 'serie');
        ix.root.classList.toggle('pj-ix-hasletters', shown);
        var counts = ix.answer.Letters || {};
        ix.letterBox.replaceChildren.apply(ix.letterBox, !shown ? [] : LETTERS.map(function (l) {
            return button('pj-ix-alphaletter', { 'data-k': 'letter', 'data-v': l, 'aria-pressed': String(has(ix, 'letter', l)), disabled: !counts[l] && !has(ix, 'letter', l), text: l });
        }));
    }

    // ---------- results ----------
    function episodeNumber(r) {
        var s = r.ParentIndexNumber, e = r.IndexNumber;
        var season = s != null && s > 0 ? 'Saison ' + s : (r.SeasonName || '');
        var ep = e != null ? 'épisode ' + e + (r.IndexNumberEnd ? '-' + r.IndexNumberEnd : '') : '';
        var out = [season, ep].filter(Boolean).join(', ');
        return out.charAt(0).toUpperCase() + out.slice(1);
    }
    function cardImage(r, wide) {
        var tags = r.ImageTags || {};
        if (wide) {
            if (tags.Primary) { return image(r.Id, 'Primary', tags.Primary, 640); }
            if (tags.Thumb) { return image(r.Id, 'Thumb', tags.Thumb, 640); }
            if (r.ParentBackdropImageTags && r.ParentBackdropImageTags.length) { return image(r.ParentBackdropItemId, 'Backdrop', r.ParentBackdropImageTags[0], 640); }
            return null;
        }
        if (tags.Primary) { return image(r.Id, 'Primary', tags.Primary, 400); }
        if (r.SeriesPrimaryImageTag) { return image(r.SeriesId, 'Primary', r.SeriesPrimaryImageTag, 400); }
        return null;
    }
    function isWide(r) { return r.Type === 'Episode' || r.Type === 'Video' || r.Type === 'MusicVideo'; }
    function card(ix, r, inSeries) {
        var ud = r.UserData || {}, href = detailsHref(r.Id), wide = isWide(r), miss = r.LocationType === 'Virtual', folder = r.Type === 'Series' || r.Type === 'Season';
        var sub = r.Type === 'Episode' ? (inSeries ? episodeNumber(r) : [r.SeriesName, episodeNumber(r)].filter(Boolean).join(', ')) : r.Type === 'Season' ? r.SeriesName || '' : r.ProductionYear ? String(r.ProductionYear) : '';
        var url = cardImage(r, wide);
        var link = h('a', { 'class': 'pj-ix-link', href: href, 'aria-label': [r.Name, sub].filter(Boolean).join(', ') }, url ? h('img', { alt: '', loading: 'lazy', src: url }) : h('span', { 'class': 'pj-ix-noimg', text: r.Name }));
        var frame = h('div', { 'class': 'pj-ix-frame' }, link);
        if (miss) { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: 'Manquant' })); }
        else if (ud.Played) { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: '✓ Vu' })); }
        else if (folder && ud.UnplayedItemCount) { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: nf(ud.UnplayedItemCount) + ' à voir' })); }
        var minutes = wide || r.Type === 'Movie' ? Math.round((r.RunTimeTicks || 0) / 600000000) : 0;
        if (minutes) { frame.appendChild(h('u', { 'class': 'pj-ix-time', text: minutes >= 60 ? Math.floor(minutes / 60) + ' h ' + String(minutes % 60).padStart(2, '0') : minutes + ' min' })); }
        if (!ud.Played && ud.PlayedPercentage) { frame.appendChild(h('span', { 'class': 'pj-ix-progress' }, h('i', { style: 'width:' + Math.round(ud.PlayedPercentage) + '%' }))); }
        var tags = h('div', { 'class': 'pj-ix-tags' }, (r.Tags || []).filter(function (t) { return !has(ix, 'tags', t); }).slice(0, 4).map(function (t) { return button('pj-ix-tag', { 'data-k': 'tags', 'data-v': t, title: 'Ajouter ce filtre', text: t }); }));
        var actions = h('div', { 'class': 'pj-ix-actions' }, [
            !miss ? button('itemAction pj-ix-act', { 'data-action': 'resume', title: 'Lire', 'aria-label': 'Lire ' + r.Name }, icon('play_arrow')) : null,
            button('itemAction pj-ix-act', { 'data-action': 'menu', title: 'Plus', 'aria-label': 'Autres actions pour ' + r.Name }, icon('more_vert'))]);
        frame.appendChild(h('div', { 'class': 'pj-ix-over' }, [tags, actions]));
        return h('article', {
            'class': 'pj-ix-item' + (miss ? ' pj-ix-miss' : ''), 'data-id': r.Id, 'data-type': r.Type, 'data-serverid': r.ServerId || serverId(),
            'data-mediatype': r.MediaType || '', 'data-isfolder': String(!!r.IsFolder), 'data-positionticks': String(ud.PlaybackPositionTicks || 0), 'data-seriesid': r.SeriesId || null
        }, [frame, h('a', { 'class': 'pj-ix-name', href: href, tabindex: '-1', text: r.Name }), h('div', { 'class': 'pj-ix-sub', text: sub })]);
    }
    function drawResults(ix) {
        ix.list.replaceChildren();
        ix.sections = new Map();
        if (!ix.rows.length) {
            var why = !ix.missing && ix.answer.Missing && !(ix.sel.state && ix.sel.state.size) ? 'Seuls des épisodes manquants correspondent : activez « Manquants » pour les voir.'
                : chosen(ix).length ? 'Rien ne réunit tous ces filtres. Cliquez un mot du titre pour le retirer.' : 'Rien à afficher ici.';
            ix.list.appendChild(h('p', { 'class': 'pj-ix-empty', text: why }));
            return;
        }
        append(ix, ix.rows);
    }
    // adds rows to their sections: one per type, and one per series for episodes sorted by series
    function append(ix, rows) {
        rows.forEach(function (row) {
            var group = ix.groups[row.group] || { Type: row.raw.Type, Count: 0 };
            var grid = ix.sections.get(row.group);
            if (!grid) {
                var box = h('section', { 'class': 'pj-ix-group' });
                var title = group.SeriesId ? group.SeriesName || TYPES.Episode[0] : ix.type ? '' : (TYPES[group.Type] || TYPES.Video)[0];
                if (title) { box.appendChild(h('h2', null, [group.SeriesId ? h('a', { href: detailsHref(group.SeriesId.replace(/-/g, '')), text: title }) : title, h('small', { text: nf(group.Count) })])); }
                grid = box.appendChild(h('div', { 'class': 'pj-ix-grid ' + (isWide(row.raw) ? 'pj-ix-wide' : 'pj-ix-posters') }));
                ix.sections.set(row.group, grid);
                ix.list.appendChild(box);
            }
            grid.appendChild(card(ix, row.raw, !!group.SeriesId));
        });
    }

    // ---------- playing: jellyfin-web's "play all from here" on a hidden queue ----------
    function play(ix, shuffle) {
        query(ix, { Play: shuffle ? 'shuffle' : 'all' }).then(function (answer) {
            var ids = answer.PlayIds || [];
            if (!ids.length || ix.host.__pjIx !== ix) { return; }
            var holder = h('div', { hidden: true });
            holder.innerHTML = '<div is="emby-itemscontainer"></div>';
            var queue = holder.firstChild;
            ids.forEach(function (id) { queue.appendChild(h('div', { 'class': 'pj-ix-q itemAction', 'data-action': 'playallfromhere', 'data-id': id.replace(/-/g, ''), 'data-serverid': serverId(), 'data-type': 'Video', 'data-mediatype': 'Video' })); });
            ix.root.appendChild(holder);
            // the container element listens once it is in the page
            setTimeout(function () { if (queue.firstChild) { queue.firstChild.click(); } setTimeout(function () { holder.remove(); }, 1000); }, 50);
        }).catch(function (e) { warn('play', e); });
    }

    function onClick(ix, ev) {
        var b = ev.target.closest('button');
        if (!b || b.classList.contains('itemAction') || !ix.root.contains(b)) { return; }
        if (b.hasAttribute('data-open')) { openPanel(ix, b.getAttribute('data-open'), b); }
        else if (b.hasAttribute('data-views')) { toggleViews(b, ev.detail === 0); }
        else if (b.hasAttribute('data-top')) { window.scrollTo({ top: 0, behavior: 'smooth' }); }
        else if (b.hasAttribute('data-play')) { play(ix, b.getAttribute('data-play') === 'shuffle'); }
        else if (b.hasAttribute('data-type')) {
            ix.type = b.getAttribute('data-type');
            if (ix.sort === 'serie' && ix.route.library && ix.type !== 'Episode' && ix.type !== 'Season' && ix.type) { ix.sort = 'name'; }
            else if (ix.route.library && ix.type === 'Episode' && ix.sort === 'name') { ix.sort = 'serie'; }
            changed(ix);
        }
        else if (b.hasAttribute('data-missing')) { ix.missing = !ix.missing; changed(ix); }
        else if (b.hasAttribute('data-k')) { ev.preventDefault(); toggle(ix, b.getAttribute('data-k'), b.getAttribute('data-v')); }
    }

    // ---------- the index: every filter across the whole width ----------
    var panel = null;
    function openPanel(ix, tab, opener) {
        closePanel();
        var box = h('div', { 'class': 'pj-ix-panelbox' });
        box.appendChild(h('div', { 'class': 'pj-ix-scrim', 'data-close': true }));
        var sheet = box.appendChild(h('section', { 'class': 'pj-ix-panel', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Tous les filtres' }));
        var head = sheet.appendChild(h('div', { 'class': 'pj-ix-phead' }));
        var search = head.appendChild(h('input', { 'class': 'pj-ix-search', type: 'search', autocomplete: 'off', 'aria-label': 'Chercher un filtre', placeholder: window.innerWidth < 600 ? 'Chercher un filtre' : 'Chercher un genre, une étiquette, une personne' }));
        head.appendChild(button('pj-ix-btn', { 'data-close': true, text: 'Fermer' }));
        var tabs = sheet.appendChild(h('nav', { 'class': 'pj-ix-tabs', 'aria-label': 'Sortes de filtres' }));
        var body = sheet.appendChild(h('div', { 'class': 'pj-ix-pbody' }));
        var foot = sheet.appendChild(h('div', { 'class': 'pj-ix-pfoot' }));
        panel = { ix: ix, box: box, search: search, tabs: tabs, body: body, foot: foot, tab: tab, q: '', alpha: true, people: PEOPLE_STEP, opener: opener, run: 0, data: null, shown: '' };
        box.addEventListener('click', panelClick);
        var typing = null;
        search.addEventListener('input', function () {
            clearTimeout(typing);
            typing = setTimeout(function () { if (panel && panel.search === search) { panel.q = search.value.trim(); panel.people = PEOPLE_STEP; refreshPanel(); } }, 180);
        });
        window.addEventListener('keydown', panelKeys, true);
        document.documentElement.classList.add('pj-ix-locked');
        document.body.appendChild(box);
        panelTabs(); panelChosen();
        body.appendChild(note('Chargement…'));
        refreshPanel();
        requestAnimationFrame(function () { box.classList.add('pj-ix-in'); if (window.innerWidth > 900 && panel) { panel.search.focus({ preventScroll: true }); } else if (panel) { sheet.querySelector('button').focus({ preventScroll: true, focusVisible: false }); } });
    }
    function closePanel() {
        if (!panel) { return; }
        var p = panel;
        panel = null;
        window.removeEventListener('keydown', panelKeys, true);
        document.documentElement.classList.remove('pj-ix-locked');
        p.box.remove();
        var back = p.opener && p.opener.isConnected ? p.opener : p.ix.root.querySelector('.pj-ix-open');
        if (back && back.offsetParent) { back.focus({ preventScroll: true }); }
    }
    // the keyboard belongs to the index while it is open
    function panelKeys(ev) {
        ev.stopPropagation();
        if (ev.key === 'Escape') { ev.preventDefault(); closePanel(); return; }
        if (ev.key === 'Enter' && ev.target === panel.search && panel.search.value.trim()) {
            ev.preventDefault();
            // typed faster than the answers: choose from the answer to what is in the field
            var wanted = panel.search.value.trim(), choose = function () { var first = panel && panel.body.querySelector('[data-k]'); if (first) { first.click(); } };
            if (panel.shown === wanted) { choose(); } else { panel.q = wanted; refreshPanel().then(choose); }
            return;
        }
        if (ev.key !== 'Tab') { return; }
        var stops = Array.prototype.filter.call(panel.box.querySelectorAll('button, input'), function (n) { return n.offsetParent !== null; });
        var at = stops.indexOf(document.activeElement);
        if (at < 0 || (ev.shiftKey && at === 0) || (!ev.shiftKey && at === stops.length - 1)) {
            ev.preventDefault();
            if (stops.length) { stops[ev.shiftKey ? stops.length - 1 : 0].focus(); }
        }
    }
    function panelClick(ev) {
        ev.stopPropagation();
        if (ev.target.hasAttribute('data-close')) { closePanel(); return; }
        var b = ev.target.closest('button');
        if (!b || !panel) { return; }
        var ix = panel.ix;
        var reset = function () { panel.q = ''; panel.search.value = ''; };
        if (b.hasAttribute('data-close')) { closePanel(); }
        else if (b.hasAttribute('data-tab')) { panel.tab = b.getAttribute('data-tab'); panel.people = PEOPLE_STEP; reset(); panelTabs(); refreshPanel().then(function () { if (panel) { panel.body.scrollTop = 0; } }); }
        else if (b.hasAttribute('data-alpha')) { panel.alpha = !panel.alpha; drawPanel(); }
        else if (b.hasAttribute('data-more')) { panel.people += PEOPLE_STEP; refreshPanel(); }
        else if (b.hasAttribute('data-clear')) { ix.sel = {}; changed(ix); }
        else if (b.hasAttribute('data-letter')) { var l = panel.body.querySelector('[data-at="' + b.getAttribute('data-letter') + '"]'); if (l) { l.scrollIntoView({ block: 'start', behavior: 'smooth' }); } }
        else if (b.hasAttribute('data-k')) {
            var searched = !!panel.q;
            if (searched) { reset(); }
            toggle(ix, b.getAttribute('data-k'), b.getAttribute('data-v'));
            if (searched && panel) { panel.search.focus({ preventScroll: true }); }
        }
    }
    // what the panel shows: the overview (the first values of every facet), and the whole list of the open tab or the
    // values holding the searched text
    function refreshPanel() {
        var p = panel, ix = p.ix, run = ++p.run, q = p.q, tab = p.tab;
        var overview = query(ix, { Facets: Object.keys(FACETS), FacetLimit: 30 });
        var detail = q ? query(ix, { Facets: ['genres', 'tags', 'studios', 'people'], FacetQuery: q, FacetLimit: 120 })
            : tab === 'all' || tab === 'years' || tab === 'state' ? Promise.resolve(null)
            : query(ix, { Facets: [tab], FacetLimit: tab === 'people' ? p.people : 0 });
        p.body.classList.add('pj-ix-busy');
        return Promise.all([overview, detail]).then(function (answers) {
            if (panel !== p || run !== p.run) { return; }
            p.data = { total: answers[0].Total, overview: answers[0].Facets, detail: answers[1] && answers[1].Facets };
            p.shown = q;
            p.body.classList.remove('pj-ix-busy');
            drawPanel();
        }).catch(function (e) {
            warn('filters', e);
            if (panel === p && run === p.run) { p.body.classList.remove('pj-ix-busy'); p.body.replaceChildren(note('Les filtres n’ont pas pu être lus. Fermez ce panneau et réessayez.')); }
        });
    }
    function marked(text) {
        var q = panel.shown, at = q ? plain(text).indexOf(plain(q)) : -1;
        if (at < 0 || plain(text).length !== text.length) { return [text]; }
        return [text.slice(0, at), h('mark', { text: text.slice(at, at + q.length) }), text.slice(at + q.length)];
    }
    function choice(cls, k, v, children, style) {
        var on = has(panel.ix, k, v);
        return button(cls + (on ? ' pj-ix-chosen' : ''), { 'data-k': k, 'data-v': v, 'aria-pressed': String(on), style: style }, children);
    }
    function genreTiles(list) {
        return h('div', { 'class': 'pj-ix-tiles' }, list.map(function (e) {
            return choice('pj-ix-tile', 'genres', e.Value, [e.Picture ? h('img', { alt: '', loading: 'lazy', src: image(e.Picture.replace(/-/g, ''), 'Backdrop', null, 480) }) : null, h('b', null, marked(e.Value)), h('small', { text: count(e.Count, panel.ix.type) })]);
        }));
    }
    function faces(list, cls) {
        return h('div', { 'class': 'pj-ix-faces' + (cls || '') }, list.map(function (e) {
            return choice('pj-ix-facechoice', 'people', e.Value, [personFace(panel.ix, e.Value, 160), h('b', null, marked(e.Value)), h('small', { text: nf(e.Count) })]);
        }));
    }
    function words(k, list) {
        var max = Math.max.apply(null, list.map(function (e) { return e.Count; }).concat(1));
        return h('div', { 'class': 'pj-ix-words' }, list.map(function (e) {
            return choice('pj-ix-word', k, e.Value, marked(e.Value).concat(h('small', { text: nf(e.Count) })), 'font-size:' + (0.88 + 0.5 * e.Count / max).toFixed(2) + 'rem');
        }));
    }
    function alphabetical(k, list) {
        var row = function (e) { return choice('pj-ix-entryrow', k, e.Value, [h('span', null, marked(e.Value)), h('small', { text: nf(e.Count) })]); };
        var toggleOrder = button('pj-ix-textbtn', { 'data-alpha': true, text: panel.alpha ? 'Les plus fréquentes d’abord' : 'Classer de A à Z' });
        var bar = h('div', { 'class': 'pj-ix-letters' });
        if (!panel.alpha) {
            bar.appendChild(toggleOrder);
            return [bar, h('div', { 'class': 'pj-ix-columns' }, list.slice().sort(function (a, b) { return b.Count - a.Count || byName(a.Value, b.Value); }).map(row))];
        }
        var initials = new Map();
        list.slice().sort(function (a, b) { return byName(a.Value, b.Value); }).forEach(function (e) {
            var l = plain(e.Value).charAt(0).toUpperCase();
            if (!/[A-Z]/.test(l)) { l = '#'; }
            if (!initials.has(l)) { initials.set(l, []); }
            initials.get(l).push(e);
        });
        var columns = h('div', { 'class': 'pj-ix-columns' });
        initials.forEach(function (entries, l) {
            bar.appendChild(button('pj-ix-letter', { 'data-letter': l, text: l }));
            columns.appendChild(h('div', { 'class': 'pj-ix-block' + (entries.length <= 12 ? ' pj-ix-keep' : ''), 'data-at': l }, [h('h4', { text: l })].concat(entries.map(row))));
        });
        bar.appendChild(toggleOrder);
        return [bar, columns];
    }
    function histogram(list) {
        var max = Math.max.apply(null, list.map(function (e) { return e.Count; }).concat(1));
        return h('div', { 'class': 'pj-ix-years' }, list.map(function (e) {
            var b = choice('pj-ix-year', 'years', e.Value, [h('i', { style: 'height:' + Math.max(3, Math.round(78 * e.Count / max)) + '%' }), h('span', { text: e.Value })]);
            b.title = count(e.Count, panel.ix.type);
            return b;
        }));
    }
    function states(list) {
        var n = new Map(list.map(function (e) { return [e.Value, e.Count]; }));
        return h('div', { 'class': 'pj-ix-states' }, Object.keys(STATES).filter(function (s) { return n.has(s); }).map(function (s) {
            return choice('pj-ix-state', 'state', s, [h('b', { text: STATES[s] }), h('small', { text: count(n.get(s), panel.ix.type) })]);
        }));
    }
    function block(title, content, tab, more) {
        return h('section', { 'class': 'pj-ix-sec' }, [h('header', null, [h('h3', { text: title }), tab ? button('pj-ix-textbtn', { 'data-tab': tab, text: more }) : null])].concat(content));
    }
    function note(text) { return h('p', { 'class': 'pj-ix-note', text: text }); }
    function peopleList(facet) {
        var out = [faces(facet.Values)], left = facet.Total - facet.Values.length;
        if (left > 0) {
            out.push(panel.people < MAX_PAGE && !panel.shown ? button('pj-ix-btn pj-ix-moreppl', { 'data-more': true, text: 'Afficher plus de personnes (' + nf(left) + ' autres)' })
                : note(nf(left) + ' autres personnes : cherchez leur nom dans le champ ci-dessus.'));
        }
        return out;
    }
    function panelContent() {
        var data = panel.data, ix = panel.ix, all = data.overview;
        if (panel.shown) {
            var d = data.detail, g = d.genres.Values, t = d.tags.Values, s = d.studios.Values, p = d.people.Values;
            var first = g[0] || t[0] || s[0] || p[0], out = [];
            if (first) { out.push(h('p', { 'class': 'pj-ix-note pj-ix-enter', text: 'La touche Entrée choisit « ' + first.Value + ' »' })); }
            if (g.length) { out.push(block(FACETS.genres, [genreTiles(g)])); }
            if (t.length) { out.push(block(FACETS.tags, [words('tags', t)])); }
            if (s.length) { out.push(block(FACETS.studios, [words('studios', s)])); }
            if (p.length) { out.push(block(FACETS.people, peopleList(d.people))); }
            return out.length ? out : [note('Aucun filtre ne contient « ' + panel.shown + ' » parmi les résultats actuels.')];
        }
        var none = [note('Rien de plus à choisir dans les résultats actuels.')];
        var tab = panel.tab;
        if (tab === 'years') { return [block(FACETS.years, [histogram(all.years.Values)])]; }
        if (tab === 'state') { return [block(FACETS.state, [states(all.state.Values)])]; }
        if (tab !== 'all') {
            var facet = data.detail && data.detail[tab];
            if (!facet || !facet.Values.length) { return none; }
            return tab === 'genres' ? [genreTiles(facet.Values)] : tab === 'people' ? peopleList(facet) : alphabetical(tab, facet.Values);
        }
        var overview = h('div', { 'class': 'pj-ix-overview' });
        var add = function (k, title, shown, content, word) {
            var facet = all[k];
            if (!facet.Total) { return; }
            var more = facet.Total > shown;
            overview.appendChild(block(title, [content(facet.Values.slice(0, shown))], more ? k : null, more ? 'Voir les ' + nf(facet.Total) + ' ' + word : null));
        };
        add('genres', FACETS.genres, 6, genreTiles, 'genres');
        add('tags', all.tags.Total > 30 ? 'Étiquettes les plus fréquentes' : FACETS.tags, 30, function (v) { return words('tags', v.slice().sort(function (a, b) { return byName(a.Value, b.Value); })); }, 'étiquettes');
        // people: one line of portraits, whatever fits
        if (all.people.Total) { overview.appendChild(block(FACETS.people, [faces(all.people.Values.slice(0, 16), ' pj-ix-oneline')], all.people.Total > 6 ? 'people' : null, 'Voir les ' + nf(all.people.Total) + ' personnes')); }
        overview.appendChild(h('div', { 'class': 'pj-ix-pair' }, [all.years.Total ? block(FACETS.years, [histogram(all.years.Values)]) : null, block(FACETS.state, [states(all.state.Values)])]));
        return [overview];
    }
    function panelTabs() {
        var data = panel.data;
        var offered = function (k) { return !data || data.overview[k].Total > 0; };
        if (data && panel.tab !== 'all' && !offered(panel.tab)) { panel.tab = 'all'; }
        var kept = panel.tabs.contains(document.activeElement) ? document.activeElement.getAttribute('data-tab') : null;
        panel.tabs.replaceChildren.apply(panel.tabs, [['all', 'Aperçu']].concat(Object.keys(FACETS).filter(function (k) { return k === 'studios' ? data && offered(k) : offered(k); }).map(function (k) { return [k, FACETS[k]]; })).map(function (t) {
            var sized = data && (t[0] === 'genres' || t[0] === 'tags' || t[0] === 'studios' || t[0] === 'people');
            return button('pj-ix-tab', { 'data-tab': t[0], 'aria-pressed': String(!panel.q && panel.tab === t[0]) }, [t[1], sized ? h('small', { text: nf(data.overview[t[0]].Total) }) : null]);
        }));
        if (kept) { var tabButton = panel.tabs.querySelector('[data-tab="' + kept + '"]'); if (tabButton) { tabButton.focus({ preventScroll: true }); } }
    }
    // the chosen filters at the foot of the panel, and the number of results they leave
    function panelChosen() {
        var ix = panel.ix;
        var picked = chosen(ix).map(function (c) { return button('pj-ix-picked', { 'data-k': c[0], 'data-v': c[1], 'aria-label': 'Retirer le filtre ' + label(c[0], c[1]), text: label(c[0], c[1]) + ' ✕' }); });
        if (picked.length) { picked.push(button('pj-ix-textbtn', { 'data-clear': true, text: 'Tout effacer' })); } else { picked.push(note('Aucun filtre choisi')); }
        var total = ix.busy ? 'Chargement…' : 'Afficher ' + count(ix.answer.Total, ix.type);
        panel.foot.replaceChildren(h('div', { 'class': 'pj-ix-pickedbar' }, picked), button('pj-ix-btn pj-ix-go', { 'data-close': true, text: total }));
        // the chosen state shows at once, before the new counts arrive
        Array.prototype.forEach.call(panel.body.querySelectorAll('[data-k]'), function (n) {
            var on = has(ix, n.getAttribute('data-k'), n.getAttribute('data-v'));
            n.classList.toggle('pj-ix-chosen', on); n.setAttribute('aria-pressed', String(on));
        });
    }
    function drawPanel() {
        if (!panel.data) { return; }
        var focus = panel.body.contains(document.activeElement) && document.activeElement.getAttribute('data-k') != null ? [document.activeElement.getAttribute('data-k'), document.activeElement.getAttribute('data-v')] : null;
        panelTabs();
        var top = panel.body.scrollTop;
        panel.body.replaceChildren.apply(panel.body, panelContent());
        panel.body.scrollTop = top;
        if (panel.shown) { var first = panel.body.querySelector('[data-k]'); if (first) { first.classList.add('pj-ix-first'); } }
        panelChosen();
        // the buttons were rebuilt: keep the keyboard where it was
        if (focus) {
            var same = Array.prototype.find.call(panel.body.querySelectorAll('[data-k]'), function (n) { return n.getAttribute('data-k') === focus[0] && n.getAttribute('data-v') === focus[1]; });
            if (same) { same.focus({ preventScroll: true }); }
        }
    }

    // ---------- while a screen is on its way ----------
    // jellyfin-web draws its own page first: it stays unseen until our screen is in it (CSS, html.pj-ix-wait), or it
    // would show for a moment. Decided from the address alone, before the next paint. Never for longer than VEIL_MAX
    var VEIL_MAX = 8000, veiled = { hash: '', at: 0 };
    function coming() {
        var m = /^#\/(tv|movies)(\.html)?\?/.exec(location.hash), parent = m && params().get('topParentId');
        if (ignored.has(location.hash) || !(parent ? addressedType(m[1], parent) : listRoute())) { return false; }
        var there = Array.prototype.some.call(document.querySelectorAll('.page:not(.hide) .pj-ix'), function (r) { return r.__pjIx.hash === location.hash; });
        return !there && themed();
    }
    function veil() {
        var wanted = coming();
        if (!wanted) { veiled.hash = ''; }
        else if (veiled.hash !== location.hash) { veiled = { hash: location.hash, at: Date.now() }; setTimeout(veil, VEIL_MAX + 50); }
        return document.documentElement.classList.toggle('pj-ix-wait', wanted && Date.now() - veiled.at < VEIL_MAX);
    }
    // jellyfin-web's page of the address being waited for is there: the screen can be built without delay
    function ready() {
        var page = currentPage();
        if (!page || !client()) { return false; }
        var library = libraryTarget(page);
        if (library) { return !!library.host && !(library.host.__pjIx && library.host.__pjIx.hash === location.hash); }
        // a list page: only once it said it shows this address
        return page.__pjView === location.hash && !!page.querySelector('.alphaPicker');
    }

    // ---------- runner: jellyfin-web is a single-page app, so run after every change of view ----------
    var scheduled = null, soon = false, lastKey = '';
    function shownIndex() {
        var roots = document.querySelectorAll('.pj-ix');
        for (var i = 0; i < roots.length; i++) { if (roots[i].offsetParent !== null) { return roots[i].__pjIx; } }
        return null;
    }
    // while a screen shows: Jellyfin's own backdrop leaves the page, a library's own row leaves the app bar (both CSS;
    // the page moves up with it), and the screen makes room for the bar as it now is
    function followBar() {
        var ix = shownIndex();
        var root = document.documentElement;
        root.classList.toggle('pj-ix-shown', !!ix);
        // the picture's ground, under names of our own: a detail page's colours are never overwritten
        var art = ix && ix.art;
        if (art) { root.style.setProperty('--pj-ix-bg', art['--pj-bg']); root.style.setProperty('--pj-ix-veil', art['--pj-veil']); }
        root.classList.toggle('pj-ix-art-on', !!art);
        document.documentElement.classList.toggle('pj-ix-libon', !!(ix && ix.route.library));
        if (!ix) { return; }
        place(ix);
    }
    function run() {
        scheduled = null; soon = false;
        if (!client() || !themed()) { return; }
        try { views(); } finally { followBar(); veil(); }
    }
    function views() {
        var page = currentPage();
        if (panel && panel.ix.hash !== location.hash) { closePanel(); }
        if (!page) { return; }
        var host = null, r = null, library = libraryTarget(page);
        if (library) { host = library.host; r = library.route; }
        else {
            r = listRoute();
            // jellyfin-web's list page, once it shows this address (the page being left stays shown for a moment; the
            // experimental library pages change view without saying so, lists do say it)
            if (r && !page.classList.contains('itemDetailPage') && page.querySelector('.alphaPicker') && (!page.__pjView || page.__pjView === location.hash)) { host = page; }
        }
        // a page jellyfin-web uses again for a view we leave alone: give it back
        if (page.__pjIx && page.__pjIx.hash !== location.hash && host !== page) { drop(page.__pjIx); }
        if (!host || ignored.has(location.hash)) { lastKey = ''; return; }
        var ix = host.__pjIx, key = visitKey(r);
        if (!ix || ix.hash !== location.hash || !host.contains(ix.root)) { build(host, r); }
        // a new visit of the same address (its link clicked again) starts from its own filter; "back" keeps the key
        else if (ix.keyed && ix.key !== key && history.state && history.state.key) { build(host, r); }
        // back on a screen already built: what was watched meanwhile shows
        else if (lastKey !== key && ix.loaded && !ix.busy) { load(ix, ix.rows.length, true).catch(function (e) { warn('refresh', e); }); }
        lastKey = key;
    }
    // changes are gathered for a moment, except when a screen waits for the page that just arrived
    function schedule(waiting) {
        if (soon) { return; }
        if (waiting && ready()) { clearTimeout(scheduled); soon = true; scheduled = setTimeout(run, 0); }
        else if (!scheduled) { scheduled = setTimeout(run, 120); }
    }
    window.addEventListener('hashchange', function () { closePanel(); lastKey = ''; veil(); schedule(); });
    document.addEventListener('viewshow', function (ev) { if (ev.target && ev.target.classList) { ev.target.__pjView = location.hash; } schedule(veil()); }, true);
    window.addEventListener('resize', function () { var ix = shownIndex(); if (ix) { place(ix); } });
    document.addEventListener('keydown', function (ev) {
        if (ev.key !== '/' || panel || ev.ctrlKey || ev.metaKey || ev.altKey || /INPUT|SELECT|TEXTAREA/.test((document.activeElement || {}).tagName || '')) { return; }
        var ix = shownIndex();
        if (!ix) { return; }
        ev.preventDefault();
        openPanel(ix, 'all', document.activeElement);
    });
    new MutationObserver(function (records) {
        var waiting = veil();   // at once: this runs before the new content is painted
        // our own additions do not count
        for (var i = 0; i < records.length; i++) {
            var t = records[i].target;
            if (!(t.closest && t.closest('.pj-ix, .pj-ix-panelbox, .pj-preview'))) { schedule(waiting); return; }
        }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    veil();
    schedule();
})();
