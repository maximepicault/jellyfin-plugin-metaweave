/*
 * Projection theme - the "Index": jellyfin-web 10.11's filtered lists (genre, tag, studio, person), rebuilt.
 *
 * Served by the Custom Metadata plugin (CustomMetadata/Theme/projection-index.js) and added to jellyfin-web's page with
 * projection.js when the plugin's "Améliorations du thème Projection" setting is on. It only acts when the theme's
 * stylesheet is the one that styles it (--pj-index), only reads through the web client's ApiClient, and leaves the
 * stock page as it is for anything it does not handle (music, live TV, favourites).
 *
 * The screen:
 * - the filter is the title, written over a picture of the results; a click on a word removes it;
 * - one ribbon: item types, a few useful refinements, "Tous les filtres", the sort order;
 * - results by type, episodes grouped by series, with jellyfin-web's own play and item menu actions;
 * - the index ("Tous les filtres" or "/"): every genre, tag, studio, person, year and playback state across the whole
 *   width, with one search field for all of them.
 * Genres, tags, studios and people narrow each other; years and playback states widen within their own kind.
 * Labels are French, like the rest of the theme.
 */
(function () {
    'use strict';
    if (window.__projectionIndex) { return; }
    window.__projectionIndex = '1';

    var warn = function (where, error) { try { console.warn('[projection index] ' + where, error); } catch (e) { /* no console */ } };

    // ---------- vocabulary ----------
    var ITEM_TYPES = 'Series,Season,Episode,Movie,Video,MusicVideo';
    // tab order, and the order of the sections of "Tout": [label, singular, plural]
    var TYPES = { '': ['Tout', 'élément', 'éléments'], Series: ['Séries', 'série', 'séries'], Movie: ['Films', 'film', 'films'], Season: ['Saisons', 'saison', 'saisons'], Episode: ['Épisodes', 'épisode', 'épisodes'], Video: ['Vidéos', 'vidéo', 'vidéos'] };
    var TYPE_ORDER = ['', 'Series', 'Movie', 'Season', 'Episode', 'Video'];
    var ALL_TITLES = { '': 'Toute la bibliothèque', Series: 'Toutes les séries', Movie: 'Tous les films', Season: 'Toutes les saisons', Episode: 'Tous les épisodes', Video: 'Toutes les vidéos' };
    var FACETS = { genres: 'Genres', tags: 'Étiquettes', studios: 'Studios', people: 'Personnes', years: 'Années', state: 'Lecture' };
    var TITLE_ORDER = ['genres', 'tags', 'studios', 'people', 'years', 'state'];
    var NARROWING = ['genres', 'tags', 'studios', 'people'];   // two values of these: items holding both
    var JOIN = { genres: '+', tags: '+', studios: 'chez', people: 'avec', years: 'en', state: '' };
    var STATES = { unseen: 'Non vus', resume: 'En cours', seen: 'Vus', miss: 'Manquants' };
    var SORTS = [['serie', 'Par série'], ['date', 'Plus récents'], ['added', 'Ajoutés récemment'], ['name', 'Par nom']];
    var PAGE_SIZE = 1000, CARD_STEP = 120, PLAY_LIMIT = 200, PEOPLE_STEP = 240;

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
    function button(cls, attrs, children) { var b = h('button', Object.assign({ type: 'button', 'class': cls }, attrs), children); return b; }
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
    function image(id, type, tag, width) { return client().getImageUrl(id, { type: type, tag: tag, maxWidth: width, quality: 90 }); }
    function params() { var at = location.hash.indexOf('?'); return new URLSearchParams(at < 0 ? '' : location.hash.slice(at + 1)); }

    // ---------- which pages are ours ----------
    // a genre, tag, studio or person list of videos, or the index of a library (the entry added to library pages)
    function route() {
        if (!/^#\/list(\.html)?\?/.test(location.hash)) { return null; }
        var q = params(), type = q.get('type'), base = null;
        if (q.get('genreId')) { base = { k: 'genres', id: q.get('genreId') }; }
        else if (q.get('studioId')) { base = { k: 'studios', id: q.get('studioId') }; }
        else if (q.get('personId')) { base = { k: 'people', id: q.get('personId') }; }
        else if (type === 'tag' && q.get('tag')) { base = { k: 'tags', v: q.get('tag') }; }
        else if (q.get('pj') !== 'index') { return null; }
        if (type === 'MusicVideo') { type = 'Video'; }
        if (type && type !== 'tag' && !TYPES[type]) { return null; }   // music, programmes...: jellyfin-web's list
        if (q.get('IsFavorite')) { return null; }
        return { base: base, type: type && TYPES[type] ? type : '', parentId: q.get('parentId') };
    }

    // ---------- data ----------
    var userConfig = null;
    function showsMissing() {
        if (!userConfig) { userConfig = client().getCurrentUser().then(function (u) { return !!(u.Configuration && u.Configuration.DisplayMissingEpisodes); }).catch(function () { userConfig = null; return false; }); }
        return userConfig;
    }
    function baseQuery(ix, anchor) {
        var q = { Recursive: true, IncludeItemTypes: ITEM_TYPES, SortBy: 'SortName', EnableTotalRecordCount: false };
        if (ix.route.parentId) { q.ParentId = ix.route.parentId; }
        // one chosen filter narrows the request; the others are applied here
        if (anchor) {
            var id = anchor.k === 'people' ? (ix.people.get(anchor.v) || {}).id : ix.ids[anchor.k + '|' + anchor.v];
            if (anchor.k === 'genres') { if (id) { q.GenreIds = id; } else { q.Genres = anchor.v; } }
            if (anchor.k === 'studios') { if (id) { q.StudioIds = id; } else { q.Studios = anchor.v; } }
            if (anchor.k === 'people') { if (id) { q.PersonIds = id; } else { q.Person = anchor.v; } }
            if (anchor.k === 'tags') { q.Tags = anchor.v; }
        }
        return q;
    }
    function fetchAll(query, onPage, alive) {
        var c = client(), start = 0;
        var next = function () {
            return c.getItems(c.getCurrentUserId(), Object.assign({}, query, { StartIndex: start, Limit: PAGE_SIZE })).then(function (r) {
                if (!alive()) { return null; }
                var list = r.Items || [];
                onPage(list, start);
                start += list.length;
                return list.length === PAGE_SIZE ? next() : null;
            });
        };
        return next();
    }
    function normalise(i) {
        var ud = i.UserData || {}, virtual = i.LocationType === 'Virtual';
        return {
            raw: i, id: i.Id, type: i.Type === 'MusicVideo' ? 'Video' : i.Type, name: i.Name || '', serie: i.SeriesName || '',
            genres: i.Genres || [], tags: i.Tags || [], studios: (i.Studios || []).map(function (s) { return s.Name; }), people: null,
            years: i.ProductionYear ? String(i.ProductionYear) : '',
            state: virtual ? 'miss' : ud.Played ? 'seen' : ud.PlaybackPositionTicks > 0 ? 'resume' : 'unseen'
        };
    }
    function readPeople(ix, item, raw) {
        item.people = (raw.People || []).map(function (p) {
            if (!ix.people.has(p.Name) || (!ix.people.get(p.Name).tag && p.PrimaryImageTag)) { ix.people.set(p.Name, { id: p.Id, tag: p.PrimaryImageTag }); }
            return p.Name;
        }).filter(function (n, k, all) { return all.indexOf(n) === k; });
    }
    // the items of the current anchor, then their people (heavier to ask for: fetched after the first display, unless a
    // chosen person has to be looked for in them)
    function load(ix, anchor, silent) {
        var run = ++ix.run, alive = function () { return run === ix.run; };
        var items = [], query = baseQuery(ix, anchor);
        var wanted = Array.from(ix.sel.people || []);
        var needPeople = wanted.some(function (v) { return !(anchor && anchor.k === 'people' && anchor.v === v); });
        if (!silent) { ix.loading = true; ix.items = []; ix.peopleReady = false; ix.anchor = anchor; }
        return showsMissing().then(function (missing) {
            return fetchAll(Object.assign({ Fields: 'Genres,Tags,Studios,DateCreated' + (needPeople ? ',People' : ''), EnableImageTypes: 'Primary,Backdrop,Thumb', ImageTypeLimit: 1 }, query), function (list, start) {
                list.forEach(function (i) {
                    if (!missing && i.LocationType === 'Virtual') { return; }
                    var item = normalise(i);
                    if (needPeople) { readPeople(ix, item, i); }
                    items.push(item);
                });
                // a big library shows its first page at once
                if (!silent && start === 0 && list.length === PAGE_SIZE) { ix.items = items.slice(); ix.loading = false; render(ix); }
            }, alive);
        }).then(function () {
            if (!alive()) { return null; }
            var before = signature(ix.items), known = new Map(ix.items.map(function (i) { return [i.id, i.people]; }));
            items.forEach(function (i) { if (known.get(i.id)) { i.people = known.get(i.id); } });
            ix.items = items; ix.anchor = anchor; ix.loading = false;
            if (!silent || before !== signature(items)) { render(ix); }
            if (items.every(function (i) { return i.people; })) {
                if (!ix.peopleReady) { peopleArrived(ix); }
                return null;
            }
            var byId = new Map(items.map(function (i) { return [i.id, i]; }));
            return fetchAll(Object.assign({ Fields: 'People', EnableImages: false, EnableUserData: false }, query), function (list) {
                list.forEach(function (raw) {
                    var item = byId.get(raw.Id);
                    if (item) { readPeople(ix, item, raw); }
                });
            }, alive).then(function () {
                if (!alive()) { return; }
                items.forEach(function (i) { if (!i.people) { i.people = []; } });
                peopleArrived(ix);
            });
        });
    }
    function peopleArrived(ix) {
        ix.peopleReady = true;
        ix.memo.clear();
        header(ix); ribbon(ix);
        if (panel && panel.ix === ix) { drawPanel(); }
    }
    function signature(items) { return items.map(function (i) { return i.id + i.state; }).join(); }

    // ---------- filtering ----------
    function vals(it, k) { var v = it[k]; return v == null || v === '' ? [] : [].concat(v); }
    function has(ix, k, v) { return !!(ix.sel[k] && ix.sel[k].has(v)); }
    function passes(ix, it, skip) {
        return Object.keys(ix.sel).every(function (k) {
            var set = ix.sel[k];
            if (k === skip || !set.size) { return true; }
            // people not fetched yet: the request already narrowed by the one in the anchor
            if (k === 'people' && !it.people) { return !!ix.anchor && ix.anchor.k === 'people' && set.size === 1 && set.has(ix.anchor.v); }
            var held = vals(it, k), wanted = Array.from(set);
            return NARROWING.indexOf(k) >= 0 ? wanted.every(function (v) { return held.indexOf(v) >= 0; }) : wanted.some(function (v) { return held.indexOf(v) >= 0; });
        });
    }
    function ofType(ix, it) { return !ix.type || it.type === ix.type; }
    function results(ix) { return ix.items.filter(function (i) { return ofType(ix, i) && passes(ix, i); }); }
    // value → the number of results one gets by choosing it
    function counts(ix, k) {
        var key = k + '|' + ix.type, n = ix.memo.get(key);
        if (n) { return n; }
        n = new Map();
        var skip = NARROWING.indexOf(k) >= 0 ? null : k;
        ix.memo.set(key, n);
        ix.items.forEach(function (i) {
            if (!ofType(ix, i) || !passes(ix, i, skip)) { return; }
            vals(i, k).forEach(function (v) { n.set(v, (n.get(v) || 0) + 1); });
        });
        if (ix.sel[k]) { ix.sel[k].forEach(function (v) { if (!n.has(v)) { n.set(v, 0); } }); }
        return n;
    }
    function ranked(ix, k) {
        return Array.from(counts(ix, k)).sort(function (a, b) { return (has(ix, k, b[0]) - has(ix, k, a[0])) || b[1] - a[1] || byName(a[0], b[0]); });
    }
    function chosen(ix) { var out = []; TITLE_ORDER.forEach(function (k) { if (ix.sel[k]) { ix.sel[k].forEach(function (v) { out.push([k, v]); }); } }); return out; }
    function label(k, v) { return k === 'state' ? STATES[v] || v : v; }

    function toggle(ix, k, v) {
        var set = ix.sel[k] || (ix.sel[k] = new Set());
        if (set.has(v)) { set.delete(v); } else { set.add(v); }
        changed(ix);
    }
    // the items at hand answer any filter as long as the one they were asked with is still chosen
    function changed(ix) {
        ix.limit = CARD_STEP;
        ix.memo.clear();
        if (ix.anchor && !has(ix, ix.anchor.k, ix.anchor.v)) {
            var next = chosen(ix).filter(function (c) { return NARROWING.indexOf(c[0]) >= 0; })[0];
            load(ix, next ? { k: next[0], v: next[1] } : null).catch(function (e) { failed(ix, e); });
        }
        // a type with nothing left under these filters: show everything
        if (ix.type && !ix.loading && !results(ix).length && ix.items.some(function (i) { return passes(ix, i); })) { ix.type = ''; }
        render(ix);
    }

    // ---------- the screen ----------
    var memory = new Map();   // history entry → filters, so that "back" finds the screen as it was left
    function visitKey() { return (history.state && history.state.key) || location.hash; }

    function build(page, r) {
        var old = page.__pjIx;
        if (old) { drop(old); }
        var root = h('div', { 'class': 'pj-ix' });
        var ix = { page: page, route: r, hash: location.hash, key: visitKey(), root: root, run: 0, items: [], loading: true, memo: new Map(), anchor: null, ids: {}, people: new Map(), peopleReady: false, person: null, scope: null, sel: {}, type: r.type, sort: 'serie', limit: CARD_STEP, picture: null };
        ix.heroBox = root.appendChild(h('header', { 'class': 'pj-ix-hero' }));
        ix.picBox = ix.heroBox.appendChild(h('div', { 'class': 'pj-ix-pic', 'aria-hidden': 'true' }));
        ix.titleBox = ix.heroBox.appendChild(h('div', { 'class': 'pj-ix-title' }));
        ix.ribbonBox = root.appendChild(h('div', { 'class': 'pj-ix-ribbon' }));
        // jellyfin-web's own container element: its item actions (play, menu, right click) work on our cards
        var holder = root.appendChild(h('div', { 'class': 'pj-ix-results' }));
        holder.innerHTML = '<div is="emby-itemscontainer" class="pj-ix-list"></div>';
        ix.list = holder.firstChild;
        ix.list.notifyRefreshNeeded = function () { load(ix, ix.anchor, true).catch(function (e) { warn('refresh', e); }); };
        ix.more = root.appendChild(h('div', { 'class': 'pj-ix-more', 'aria-hidden': 'true' }));
        root.addEventListener('click', function (ev) { onClick(ix, ev); });
        root.addEventListener('change', function (ev) {
            if (ev.target.classList.contains('pj-ix-sort')) { ix.sort = ev.target.value; ix.limit = CARD_STEP; remember(ix); drawResults(ix); }
        });
        page.__pjIx = ix;
        page.classList.add('pj-ix-on');
        page.appendChild(root);
        place(ix);
        if (window.IntersectionObserver) {
            ix.stuck = new IntersectionObserver(function (e) { ix.ribbonBox.classList.toggle('pj-ix-stuck', !e[e.length - 1].isIntersecting); }, { rootMargin: '-90px 0px 0px' });
            ix.stuck.observe(ix.heroBox);
            ix.grow = new IntersectionObserver(function (e) { if (e[e.length - 1].isIntersecting) { grow(ix); } }, { rootMargin: '0px 0px 900px' });
            ix.grow.observe(ix.more);
        } else {
            ix.limit = Infinity;
        }
        window.scrollTo(0, 0);
        render(ix);
        start(ix).catch(function (e) { failed(ix, e); });
    }
    function drop(ix) {
        ix.run++;
        if (ix.stuck) { ix.stuck.disconnect(); }
        if (ix.grow) { ix.grow.disconnect(); }
        ix.root.remove();
        ix.page.classList.remove('pj-ix-on');
        ix.page.__pjIx = null;
    }
    // under the experimental layout's see-through bar the picture goes up to the top; the classic header is solid
    function place(ix) {
        var mui = document.querySelector('.MuiAppBar-root');
        var bar = mui || document.querySelector('.skinHeader');
        var height = bar ? Math.round(bar.getBoundingClientRect().height) : 0;
        ix.root.style.setProperty('--pj-ix-top', height + 'px');
        ix.root.classList.toggle('pj-ix-under', !!mui);
    }
    var ignored = new Set();   // lists with no video in them (a music genre...): jellyfin-web's own page
    function failed(ix, error) {
        warn('load', error);
        ignored.add(ix.hash);
        if (panel && panel.ix === ix) { closePanel(); }
        drop(ix);
    }
    // names the base filter of the address, restores the filters of a visit already made, then loads
    function start(ix) {
        var c = client(), r = ix.route, waits = [];
        if (r.base) {
            if (r.base.id) {
                waits.push(c.getJSON(c.getUrl('Items/' + r.base.id, { userId: c.getCurrentUserId() })).then(function (item) {
                    if (/^Music/.test(item.Type)) { throw new Error('music list'); }
                    r.base.v = item.Name;
                    ix.ids[r.base.k + '|' + item.Name] = item.Id;
                    if (r.base.k === 'people') { ix.person = item; ix.people.set(item.Name, { id: item.Id, tag: item.ImageTags && item.ImageTags.Primary }); }
                }));
            }
        }
        if (r.parentId) {
            waits.push(c.getJSON(c.getUrl('Items/' + r.parentId, { userId: c.getCurrentUserId() })).then(function (item) { ix.scope = item.Name; }).catch(function () { /* unnamed */ }));
        }
        return Promise.all(waits).then(function () {
            if (ix.page.__pjIx !== ix) { return null; }
            var kept = memory.get(ix.key);
            if (kept && kept.hash === ix.hash) {
                Object.keys(kept.sel).forEach(function (k) { ix.sel[k] = new Set(kept.sel[k]); });
                ix.type = kept.type; ix.sort = kept.sort; ix.ids = Object.assign(ix.ids, kept.ids);
                kept.people.forEach(function (v, name) { ix.people.set(name, v); });
            } else if (r.base) {
                ix.sel[r.base.k] = new Set([r.base.v]);
            }
            var first = chosen(ix).filter(function (f) { return NARROWING.indexOf(f[0]) >= 0; })[0];
            if (r.base && has(ix, r.base.k, r.base.v)) { first = [r.base.k, r.base.v]; }
            var anchor = first ? { k: first[0], v: first[1] } : null;
            ix.anchor = anchor;
            return load(ix, anchor).then(function () {
                // nothing at all on arrival: not a list of videos
                if (ix.page.__pjIx === ix && !ix.items.length && !kept) { throw new Error('no video in this list'); }
            });
        });
    }
    function remember(ix) {
        var sel = {};
        Object.keys(ix.sel).forEach(function (k) { sel[k] = Array.from(ix.sel[k]); });
        memory.set(ix.key, { hash: ix.hash, sel: sel, type: ix.type, sort: ix.sort, ids: ix.ids, people: ix.people });
    }
    function render(ix) {
        if (ix.page.__pjIx !== ix) { return; }
        ix.memo.clear();
        header(ix); ribbon(ix); drawResults(ix);
        if (panel && panel.ix === ix) { drawPanel(); }
        if (!ix.loading) { remember(ix); }
    }

    // ---------- header ----------
    function personFace(ix, name, width) {
        var p = ix.people.get(name);
        if (p && p.tag) { return h('img', { 'class': 'pj-ix-face-img', alt: '', loading: 'lazy', src: image(p.id, 'Primary', p.tag, width) }); }
        return h('span', { 'class': 'pj-ix-face-img pj-ix-initials', 'aria-hidden': 'true', text: name.split(/\s+/).map(function (w) { return w.charAt(0); }).slice(0, 2).join('') });
    }
    function playable(ix) {
        var can = function (i) { return (i.type === 'Episode' || i.type === 'Movie' || i.type === 'Video') && i.state !== 'miss'; };
        var shown = results(ix).filter(can);
        return shown.length ? shown : ix.items.filter(function (i) { return can(i) && passes(ix, i); });
    }
    function header(ix) {
        var list = results(ix), terms = chosen(ix);
        var title = h('h1', { 'class': 'pj-ix-h1' + (terms.length > 2 || terms.some(function (t) { return t[1].length > 18; }) ? ' pj-ix-long' : '') });
        var last = null;
        terms.forEach(function (t, i) {
            var k = t[0], join = i === 0 ? (k === 'people' || k === 'studios' || k === 'years' ? JOIN[k] : '') : (k === last && (k === 'years' || k === 'state')) ? 'ou' : JOIN[k] || ',';
            last = k;
            if (join) { title.appendChild(h('span', { 'class': 'pj-ix-join', text: join })); }
            title.appendChild(button('pj-ix-term', { 'data-k': k, 'data-v': t[1], title: 'Retirer ce filtre', 'aria-label': 'Retirer le filtre ' + label(k, t[1]) }, [
                k === 'people' ? personFace(ix, t[1], 160) : null, h('b', { text: label(k, t[1]) }), h('i', { 'aria-hidden': 'true', text: '✕' })]));
        });
        if (!terms.length) { title.appendChild(h('span', { text: ix.loading && ix.route.base ? ix.route.base.v || '…' : ALL_TITLES[ix.type] })); }
        title.appendChild(button('pj-ix-plus', { 'data-open': 'all', 'aria-label': 'Ajouter un filtre', title: 'Ajouter un filtre', text: '+' }));
        var under = h('div', { 'class': 'pj-ix-under-title' });
        if (!ix.loading && playable(ix).length) {
            under.appendChild(button('pj-ix-btn pj-ix-go', { 'data-play': 'all' }, [icon('play_arrow'), 'Tout lire']));
            under.appendChild(button('pj-ix-btn', { 'data-play': 'shuffle' }, [icon('shuffle'), 'Aléatoire']));
        }
        var sum = 'Chargement…';
        if (!ix.loading) {
            sum = count(list.length, ix.type);
            if (ix.type === 'Episode' || ix.type === 'Season') {
                var n = new Set(list.map(function (i) { return i.raw.SeriesId; }).filter(Boolean)).size;
                if (n) { sum += ' dans ' + count(n, 'Series'); }
            }
            if (ix.scope) { sum += (/ dans /.test(sum) ? ', bibliothèque ' : ' dans la bibliothèque ') + ix.scope; }
        }
        under.appendChild(h('p', { 'aria-live': 'polite', text: sum }));
        ix.titleBox.replaceChildren(title, under);
        picture(ix, list);
    }
    // the picture behind the title: the person's own, else a landscape backdrop of a result, else an episode's still
    function pictures(ix, list) {
        var out = [], seen = new Set();
        var add = function (id, type, tag, owner) { if (id && tag && !seen.has(id + tag)) { seen.add(id + tag); out.push({ key: id + tag, owner: owner, id: id, type: type, tag: tag }); } };
        var person = ix.person;
        if (person && has(ix, 'people', person.Name) && person.BackdropImageTags && person.BackdropImageTags.length) { add(person.Id, 'Backdrop', person.BackdropImageTags[0], null); }
        list.slice(0, 60).forEach(function (i) {
            var r = i.raw;
            if (r.BackdropImageTags && r.BackdropImageTags.length) { add(r.Id, 'Backdrop', r.BackdropImageTags[0], i.id); }
            if (r.ParentBackdropImageTags && r.ParentBackdropImageTags.length) { add(r.ParentBackdropItemId, 'Backdrop', r.ParentBackdropImageTags[0], i.id); }
        });
        list.slice(0, 12).forEach(function (i) { if (i.type === 'Episode' && i.raw.ImageTags) { add(i.id, 'Primary', i.raw.ImageTags.Primary, i.id); } });
        return out;
    }
    function picture(ix, list) {
        if (ix.loading && !list.length) { return; }
        var candidates = pictures(ix, list);
        // the same picture stays while it still belongs to the results
        if (ix.picture && candidates.some(function (c) { return c.key === ix.picture; })) { return; }
        var turn = ix.pictureTurn = (ix.pictureTurn || 0) + 1;
        var next = function (k) {
            var c = candidates[k];
            if (turn !== ix.pictureTurn) { return; }
            if (!c || k > 7) { ix.picture = null; ix.picBox.replaceChildren(); ix.heroBox.classList.add('pj-ix-bare'); return; }
            var img = new Image();
            img.alt = '';
            img.onload = function () {
                if (turn !== ix.pictureTurn) { return; }
                // never a portrait or square picture stretched across the screen
                if (img.naturalWidth / img.naturalHeight < 1.25) { next(k + 1); return; }
                ix.picture = c.key; ix.picBox.replaceChildren(img); ix.heroBox.classList.remove('pj-ix-bare');
            };
            img.onerror = function () { next(k + 1); };
            img.src = image(c.id, c.type, c.tag, Math.min(1920, Math.round(window.innerWidth * (window.devicePixelRatio || 1))));
        };
        next(0);
    }

    // ---------- ribbon ----------
    function ribbon(ix) {
        var n = results(ix).length;
        var types = h('div', { 'class': 'pj-ix-types', role: 'group', 'aria-label': 'Type' });
        var per = {};
        ix.items.forEach(function (i) { if (passes(ix, i)) { per[i.type] = (per[i.type] || 0) + 1; per[''] = (per[''] || 0) + 1; } });
        TYPE_ORDER.forEach(function (t) {
            if (!per[t] && ix.type !== t) { return; }
            types.appendChild(button('pj-ix-type', { 'data-type': t, 'aria-pressed': String(ix.type === t) }, [TYPES[t][0], h('small', { text: nf(per[t] || 0) })]));
        });
        var sugg = h('div', { 'class': 'pj-ix-sugg', role: 'group', 'aria-label': 'Affiner' });
        if (!ix.loading) {
            var useful = [];
            [['tags', 4], ['genres', 3], ['people', 3]].forEach(function (f) {
                if (f[0] === 'people' && !ix.peopleReady) { return; }
                Array.from(counts(ix, f[0])).filter(function (e) { return !has(ix, f[0], e[0]) && e[1] > 0 && e[1] < n; })
                    .sort(function (a, b) { return b[1] - a[1] || byName(a[0], b[0]); }).slice(0, f[1]).forEach(function (e) { useful.push([f[0], e[0], e[1]]); });
            });
            useful.sort(function (a, b) { return b[2] - a[2]; }).forEach(function (s) {
                sugg.appendChild(button('pj-ix-suggest', { 'data-k': s[0], 'data-v': s[1] }, [s[0] === 'people' ? personFace(ix, s[1], 60) : null, s[1], h('small', { text: nf(s[2]) })]));
            });
        }
        var mini = button('pj-ix-mini', { 'data-top': true, title: 'Revenir en haut', text: chosen(ix).map(function (c) { return label(c[0], c[1]); }).join(' + ') });
        var open = button('pj-ix-open', { 'data-open': 'all', 'aria-haspopup': 'dialog' }, [icon('tune'), 'Tous les filtres', h('kbd', { 'aria-hidden': 'true', text: '/' })]);
        var sort = h('select', { 'class': 'pj-ix-sort', 'aria-label': 'Trier' }, SORTS.map(function (s) { return h('option', { value: s[0], text: s[1] }); }));
        sort.value = ix.sort;
        var focused = ix.ribbonBox.contains(document.activeElement) ? document.activeElement : null;
        var again = focused && (focused.getAttribute('data-type') != null ? '[data-type="' + focused.getAttribute('data-type') + '"]' : focused.classList.contains('pj-ix-sort') ? '.pj-ix-sort' : focused.classList.contains('pj-ix-open') ? '.pj-ix-open' : null);
        ix.ribbonBox.replaceChildren(mini, types, sugg, open, sort);
        if (again) { var node = ix.ribbonBox.querySelector(again); if (node) { node.focus({ preventScroll: true }); } }
    }

    // ---------- results ----------
    function episodeNumber(i) {
        var r = i.raw, s = r.ParentIndexNumber, e = r.IndexNumber;
        var season = s != null && s > 0 ? 'Saison ' + s : (r.SeasonName || '');
        var ep = e != null ? 'épisode ' + e + (r.IndexNumberEnd ? '-' + r.IndexNumberEnd : '') : '';
        var out = [season, ep].filter(Boolean).join(', ');
        return out.charAt(0).toUpperCase() + out.slice(1);
    }
    function cardImage(i) {
        var r = i.raw, tags = r.ImageTags || {};
        if (i.type === 'Episode' || i.type === 'Video') {
            if (tags.Primary) { return image(i.id, 'Primary', tags.Primary, 640); }
            if (tags.Thumb) { return image(i.id, 'Thumb', tags.Thumb, 640); }
            if (r.ParentBackdropImageTags && r.ParentBackdropImageTags.length) { return image(r.ParentBackdropItemId, 'Backdrop', r.ParentBackdropImageTags[0], 640); }
            return null;
        }
        if (tags.Primary) { return image(i.id, 'Primary', tags.Primary, 400); }
        if (r.SeriesPrimaryImageTag) { return image(r.SeriesId, 'Primary', r.SeriesPrimaryImageTag, 400); }
        return null;
    }
    function card(ix, i, inSeries) {
        var r = i.raw, ud = r.UserData || {}, href = detailsHref(i.id);
        var wide = i.type === 'Episode' || i.type === 'Video';
        var sub = i.type === 'Episode' ? (inSeries ? episodeNumber(i) : [i.serie, episodeNumber(i)].filter(Boolean).join(', ')) : i.type === 'Season' ? i.serie : i.years;
        var url = cardImage(i);
        var link = h('a', { 'class': 'pj-ix-link', href: href, 'aria-label': [i.name, sub].filter(Boolean).join(', ') }, url ? h('img', { alt: '', loading: 'lazy', src: url }) : h('span', { 'class': 'pj-ix-noimg', text: i.name }));
        var frame = h('div', { 'class': 'pj-ix-frame' }, link);
        if (i.state === 'seen') { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: '✓ Vu' })); }
        else if (i.state === 'miss') { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: 'Manquant' })); }
        else if ((i.type === 'Series' || i.type === 'Season') && ud.UnplayedItemCount) { frame.appendChild(h('em', { 'class': 'pj-ix-badge', text: nf(ud.UnplayedItemCount) + ' à voir' })); }
        var minutes = wide || i.type === 'Movie' ? Math.round((r.RunTimeTicks || 0) / 600000000) : 0;
        if (minutes) { frame.appendChild(h('u', { 'class': 'pj-ix-time', text: minutes >= 60 ? Math.floor(minutes / 60) + ' h ' + String(minutes % 60).padStart(2, '0') : minutes + ' min' })); }
        if (i.state === 'resume' && ud.PlayedPercentage) { frame.appendChild(h('span', { 'class': 'pj-ix-progress' }, h('i', { style: 'width:' + Math.round(ud.PlayedPercentage) + '%' }))); }
        var tags = h('div', { 'class': 'pj-ix-tags' }, i.tags.filter(function (t) { return !has(ix, 'tags', t); }).slice(0, 4).map(function (t) { return button('pj-ix-tag', { 'data-k': 'tags', 'data-v': t, title: 'Ajouter ce filtre', text: t }); }));
        var actions = h('div', { 'class': 'pj-ix-actions' }, [
            i.state !== 'miss' ? button('itemAction pj-ix-act', { 'data-action': 'resume', title: 'Lire', 'aria-label': 'Lire ' + i.name }, icon('play_arrow')) : null,
            button('itemAction pj-ix-act', { 'data-action': 'menu', title: 'Plus', 'aria-label': 'Autres actions pour ' + i.name }, icon('more_vert'))]);
        frame.appendChild(h('div', { 'class': 'pj-ix-over' }, [tags, actions]));
        return h('article', {
            'class': 'pj-ix-item' + (i.state === 'miss' ? ' pj-ix-miss' : ''), 'data-id': i.id, 'data-type': r.Type, 'data-serverid': r.ServerId || serverId(),
            'data-mediatype': r.MediaType || '', 'data-isfolder': String(!!r.IsFolder), 'data-positionticks': String(ud.PlaybackPositionTicks || 0), 'data-seriesid': r.SeriesId || null
        }, [frame, h('a', { 'class': 'pj-ix-name', href: href, tabindex: '-1', text: i.name }), h('div', { 'class': 'pj-ix-sub', text: sub })]);
    }
    // where each season comes in its series: its number, else the one in its name, else (arcs...) its first release;
    // specials last. Jellyfin often leaves episodes without a season number, so the season is judged as a whole
    function seasonKey(i) { return (i.raw.SeriesId || i.serie) + '|' + (i.raw.SeasonId || i.raw.SeasonName || (i.type === 'Season' ? i.id : '')); }
    function seasonRanks(items) {
        var ranks = new Map();
        items.forEach(function (i) {
            if (i.type !== 'Episode' && i.type !== 'Season') { return; }
            var r = i.raw, key = seasonKey(i), rank = ranks.get(key);
            if (!rank) { ranks.set(key, rank = { number: null, special: false, date: '9999', name: i.type === 'Season' ? i.name : r.SeasonName || '' }); }
            var n = i.type === 'Season' ? r.IndexNumber : r.ParentIndexNumber;
            if (n === 0 || n < 0) { rank.special = true; }
            else if (n > 0) { rank.number = n; }
            if (r.PremiereDate && r.PremiereDate < rank.date) { rank.date = r.PremiereDate; }
        });
        ranks.forEach(function (rank) {
            var named = /\d+/.exec(rank.name);
            if (rank.number == null && named) { rank.number = Number(named[0]); }
            if (rank.special) { rank.number = Infinity; }
        });
        return ranks;
    }
    function order(ix) {
        var date = function (i) { return i.raw.PremiereDate || (i.years ? i.years + '-00' : ''); };
        var num = function (v) { return v == null || v < 0 ? 1e9 : v; };
        if (ix.sort === 'date') { return function (a, b) { return date(b).localeCompare(date(a)) || byName(a.name, b.name); }; }
        if (ix.sort === 'added') { return function (a, b) { return String(b.raw.DateCreated || '').localeCompare(String(a.raw.DateCreated || '')) || byName(a.name, b.name); }; }
        if (ix.sort === 'name') { return function (a, b) { return byName(a.raw.SortName || a.name, b.raw.SortName || b.name); }; }
        var ranks = seasonRanks(ix.items);
        var season = function (a, b) {
            var x = ranks.get(seasonKey(a)), y = ranks.get(seasonKey(b));
            if (!x || !y || x === y) { return 0; }
            if (x.number !== y.number) { return x.number == null ? 1 : y.number == null ? -1 : x.number - y.number; }
            return x.date.localeCompare(y.date) || byName(x.name, y.name);
        };
        return function (a, b) {
            return byName(a.serie || a.name, b.serie || b.name) || season(a, b)
                || num(a.raw.IndexNumber) - num(b.raw.IndexNumber) || date(a).localeCompare(date(b)) || byName(a.name, b.name);
        };
    }
    // the sections in display order: one per type, and one per series for episodes sorted by series
    function sections(ix, list) {
        var out = [], by = {};
        list.slice().sort(order(ix)).forEach(function (i) { (by[i.type] || (by[i.type] = [])).push(i); });
        TYPE_ORDER.forEach(function (t) {
            if (!by[t]) { return; }
            if (t === 'Episode' && ix.sort === 'serie') {
                var series = new Map();
                by[t].forEach(function (i) { var key = i.raw.SeriesId || i.serie; if (!series.has(key)) { series.set(key, []); } series.get(key).push(i); });
                series.forEach(function (items) { out.push({ title: items[0].serie || TYPES[t][0], href: items[0].raw.SeriesId ? detailsHref(items[0].raw.SeriesId) : null, wide: true, inSeries: true, items: items }); });
            } else {
                out.push({ title: ix.type ? '' : TYPES[t][0], wide: t === 'Episode' || t === 'Video', items: by[t] });
            }
        });
        return out;
    }
    function drawResults(ix) {
        ix.list.replaceChildren();
        ix.queue = [];
        ix.drawn = 0;
        if (ix.loading) { ix.list.appendChild(h('p', { 'class': 'pj-ix-empty', text: 'Chargement…' })); return; }
        var list = results(ix);
        if (!list.length) { ix.list.appendChild(h('p', { 'class': 'pj-ix-empty', text: 'Rien ne réunit tous ces filtres. Cliquez un mot du titre pour le retirer.' })); return; }
        ix.queue = sections(ix, list).map(function (s) { return { section: s, at: 0, grid: null }; });
        fill(ix, ix.limit);
    }
    // adds cards until `limit` of them are on the page (a library of thousands is drawn as one scrolls)
    function fill(ix, limit) {
        for (var q = 0; q < ix.queue.length && ix.drawn < limit; q++) {
            var e = ix.queue[q], s = e.section;
            if (e.at >= s.items.length) { continue; }
            if (!e.grid) {
                var box = h('section', { 'class': 'pj-ix-group' });
                if (s.title) { box.appendChild(h('h2', null, [s.href ? h('a', { href: s.href, text: s.title }) : s.title, h('small', { text: nf(s.items.length) })])); }
                e.grid = box.appendChild(h('div', { 'class': 'pj-ix-grid ' + (s.wide ? 'pj-ix-wide' : 'pj-ix-posters') }));
                ix.list.appendChild(box);
            }
            while (e.at < s.items.length && ix.drawn < limit) { e.grid.appendChild(card(ix, s.items[e.at++], s.inSeries)); ix.drawn++; }
        }
    }
    function grow(ix) {
        if (ix.loading || !ix.queue || !ix.queue.some(function (e) { return e.at < e.section.items.length; })) { return; }
        ix.limit = ix.drawn + CARD_STEP;
        fill(ix, ix.limit);
        // still in view after this step: watch again
        ix.grow.unobserve(ix.more); ix.grow.observe(ix.more);
    }

    // ---------- playing: jellyfin-web's "play all from here" on a hidden queue ----------
    function play(ix, shuffle) {
        var items = playable(ix).slice().sort(order(ix));
        if (shuffle) { for (var i = items.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)), t = items[i]; items[i] = items[j]; items[j] = t; } }
        if (!items.length) { return; }
        var holder = h('div', { hidden: true });
        holder.innerHTML = '<div is="emby-itemscontainer"></div>';
        var queue = holder.firstChild;
        items.slice(0, PLAY_LIMIT).forEach(function (item) {
            queue.appendChild(h('div', { 'class': 'pj-ix-q itemAction', 'data-action': 'playallfromhere', 'data-id': item.id, 'data-serverid': item.raw.ServerId || serverId(), 'data-type': item.raw.Type, 'data-mediatype': item.raw.MediaType || 'Video' }));
        });
        ix.root.appendChild(holder);
        // the container element listens once it is in the page
        setTimeout(function () { if (queue.firstChild) { queue.firstChild.click(); } setTimeout(function () { holder.remove(); }, 1000); }, 50);
    }

    function onClick(ix, ev) {
        var b = ev.target.closest('button');
        if (!b || b.classList.contains('itemAction') || !ix.root.contains(b)) { return; }
        if (b.hasAttribute('data-open')) { openPanel(ix, b.getAttribute('data-open'), b); }
        else if (b.hasAttribute('data-top')) { window.scrollTo({ top: 0, behavior: 'smooth' }); }
        else if (b.hasAttribute('data-play')) { play(ix, b.getAttribute('data-play') === 'shuffle'); }
        else if (b.hasAttribute('data-type')) { ix.type = b.getAttribute('data-type'); ix.limit = CARD_STEP; render(ix); }
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
        panel = { ix: ix, box: box, search: search, tabs: tabs, body: body, foot: foot, tab: tab, q: '', alpha: true, people: PEOPLE_STEP, opener: opener };
        box.addEventListener('click', panelClick);
        search.addEventListener('input', function () { panel.q = search.value.trim(); panel.people = PEOPLE_STEP; drawPanel(); panel.body.scrollTop = 0; });
        window.addEventListener('keydown', panelKeys, true);
        document.documentElement.classList.add('pj-ix-locked');
        document.body.appendChild(box);
        drawPanel();
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
        if (ev.key === 'Enter' && ev.target === panel.search && panel.q) {
            ev.preventDefault();
            var first = panel.body.querySelector('[data-k]');
            if (first) { first.click(); }
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
        else if (b.hasAttribute('data-tab')) { panel.tab = b.getAttribute('data-tab'); panel.people = PEOPLE_STEP; reset(); drawPanel(); panel.body.scrollTop = 0; }
        else if (b.hasAttribute('data-alpha')) { panel.alpha = !panel.alpha; drawPanel(); }
        else if (b.hasAttribute('data-more')) { panel.people += PEOPLE_STEP; drawPanel(); }
        else if (b.hasAttribute('data-clear')) { ix.sel = {}; changed(ix); }
        else if (b.hasAttribute('data-letter')) { var l = panel.body.querySelector('[data-at="' + b.getAttribute('data-letter') + '"]'); if (l) { l.scrollIntoView({ block: 'start', behavior: 'smooth' }); } }
        else if (b.hasAttribute('data-k')) {
            var searched = !!panel.q;
            if (searched) { reset(); }
            toggle(ix, b.getAttribute('data-k'), b.getAttribute('data-v'));
            if (searched && panel) { panel.search.focus({ preventScroll: true }); }
        }
    }
    function marked(text) {
        var q = panel.q, at = q ? plain(text).indexOf(plain(q)) : -1;
        if (at < 0 || plain(text).length !== text.length) { return [text]; }
        return [text.slice(0, at), h('mark', { text: text.slice(at, at + q.length) }), text.slice(at + q.length)];
    }
    function choice(cls, k, v, children, style) {
        var on = has(panel.ix, k, v);
        return button(cls + (on ? ' pj-ix-chosen' : ''), { 'data-k': k, 'data-v': v, 'aria-pressed': String(on), style: style }, children);
    }
    function genreTiles(list) {
        var items = panel.ix.items;
        return h('div', { 'class': 'pj-ix-tiles' }, list.map(function (e) {
            // a picture of the genre: a series or film that has one, else any of its items
            var holder = null, src = null;
            for (var i = 0; i < items.length && !(src && holder.type !== 'Episode'); i++) {
                var it = items[i], r = it.raw;
                if (it.genres.indexOf(e[0]) < 0) { continue; }
                var s = r.BackdropImageTags && r.BackdropImageTags.length ? image(r.Id, 'Backdrop', r.BackdropImageTags[0], 480)
                    : r.ParentBackdropImageTags && r.ParentBackdropImageTags.length ? image(r.ParentBackdropItemId, 'Backdrop', r.ParentBackdropImageTags[0], 480) : null;
                if (s) { src = s; holder = it; }
            }
            return choice('pj-ix-tile', 'genres', e[0], [src ? h('img', { alt: '', loading: 'lazy', src: src }) : null, h('b', null, marked(e[0])), h('small', { text: count(e[1], panel.ix.type) })]);
        }));
    }
    function faces(list, cls) {
        return h('div', { 'class': 'pj-ix-faces' + (cls || '') }, list.map(function (e) {
            return choice('pj-ix-facechoice', 'people', e[0], [personFace(panel.ix, e[0], 160), h('b', null, marked(e[0])), h('small', { text: nf(e[1]) })]);
        }));
    }
    function words(k, list) {
        var max = Math.max.apply(null, list.map(function (e) { return e[1]; }).concat(1));
        return h('div', { 'class': 'pj-ix-words' }, list.map(function (e) {
            return choice('pj-ix-word', k, e[0], marked(e[0]).concat(h('small', { text: nf(e[1]) })), 'font-size:' + (0.88 + 0.5 * e[1] / max).toFixed(2) + 'rem');
        }));
    }
    function alphabetical(k, list) {
        var row = function (e) { return choice('pj-ix-entryrow', k, e[0], [h('span', null, marked(e[0])), h('small', { text: nf(e[1]) })]); };
        var toggleOrder = button('pj-ix-textbtn', { 'data-alpha': true, text: panel.alpha ? 'Les plus fréquentes d’abord' : 'Classer de A à Z' });
        var bar = h('div', { 'class': 'pj-ix-letters' });
        if (!panel.alpha) {
            bar.appendChild(toggleOrder);
            return [bar, h('div', { 'class': 'pj-ix-columns' }, list.slice().sort(function (a, b) { return b[1] - a[1] || byName(a[0], b[0]); }).map(row))];
        }
        var letters = new Map();
        list.slice().sort(function (a, b) { return byName(a[0], b[0]); }).forEach(function (e) {
            var l = plain(e[0]).charAt(0).toUpperCase();
            if (!/[A-Z]/.test(l)) { l = '#'; }
            if (!letters.has(l)) { letters.set(l, []); }
            letters.get(l).push(e);
        });
        var columns = h('div', { 'class': 'pj-ix-columns' });
        letters.forEach(function (entries, l) {
            bar.appendChild(button('pj-ix-letter', { 'data-letter': l, text: l }));
            columns.appendChild(h('div', { 'class': 'pj-ix-block' + (entries.length <= 12 ? ' pj-ix-keep' : ''), 'data-at': l }, [h('h4', { text: l })].concat(entries.map(row))));
        });
        bar.appendChild(toggleOrder);
        return [bar, columns];
    }
    function histogram() {
        var list = Array.from(counts(panel.ix, 'years')).sort(function (a, b) { return a[0] - b[0]; });
        var max = Math.max.apply(null, list.map(function (e) { return e[1]; }).concat(1));
        return h('div', { 'class': 'pj-ix-years' }, list.map(function (e) {
            var b = choice('pj-ix-year', 'years', e[0], [h('i', { style: 'height:' + Math.max(3, Math.round(78 * e[1] / max)) + '%' }), h('span', { text: e[0] })]);
            b.title = count(e[1], panel.ix.type);
            return b;
        }));
    }
    function states() {
        var n = counts(panel.ix, 'state');
        return h('div', { 'class': 'pj-ix-states' }, Object.keys(STATES).filter(function (s) { return n.has(s); }).map(function (s) {
            return choice('pj-ix-state', 'state', s, [h('b', { text: STATES[s] }), h('small', { text: count(n.get(s), panel.ix.type) })]);
        }));
    }
    function block(title, content, tab, more) {
        return h('section', { 'class': 'pj-ix-sec' }, [h('header', null, [h('h3', { text: title }), tab ? button('pj-ix-textbtn', { 'data-tab': tab, text: more }) : null])].concat(content));
    }
    function note(text) { return h('p', { 'class': 'pj-ix-note', text: text }); }
    function peopleList(list) {
        var out = [faces(list.slice(0, panel.people))];
        if (list.length > panel.people) { out.push(button('pj-ix-btn pj-ix-moreppl', { 'data-more': true, text: 'Afficher plus de personnes (' + nf(list.length - panel.people) + ' autres)' })); }
        return out;
    }
    function panelContent() {
        var ix = panel.ix, waiting = 'Les personnes arrivent…';
        if (ix.loading) { return [note('Chargement…')]; }
        if (panel.q) {
            var found = function (k) { return Array.from(counts(ix, k)).filter(function (e) { return plain(label(k, e[0])).indexOf(plain(panel.q)) >= 0; }).sort(function (a, b) { return b[1] - a[1] || byName(a[0], b[0]); }); };
            var g = found('genres'), t = found('tags'), s = found('studios'), p = found('people');
            var first = g[0] || t[0] || s[0] || p[0], out = [];
            if (first) { out.push(h('p', { 'class': 'pj-ix-note pj-ix-enter', text: 'La touche Entrée choisit « ' + first[0] + ' »' })); }
            if (g.length) { out.push(block(FACETS.genres, [genreTiles(g.slice(0, 40))])); }
            if (t.length) { out.push(block(FACETS.tags, [words('tags', t.slice(0, 200))])); }
            if (s.length) { out.push(block(FACETS.studios, [words('studios', s.slice(0, 100))])); }
            if (p.length) { out.push(block(FACETS.people, peopleList(p))); }
            if (!ix.peopleReady) { out.push(note(waiting)); }
            return out.length ? out : [note('Aucun filtre ne contient « ' + panel.q + ' » parmi les résultats actuels.')];
        }
        var none = [note('Rien de plus à choisir dans les résultats actuels.')];
        if (panel.tab === 'genres') { var genres = ranked(ix, 'genres'); return genres.length ? [genreTiles(genres)] : none; }
        if (panel.tab === 'tags' || panel.tab === 'studios') { var list = Array.from(counts(ix, panel.tab)); return list.length ? alphabetical(panel.tab, list) : none; }
        if (panel.tab === 'people') { if (!ix.peopleReady) { return [note(waiting)]; } var ppl = ranked(ix, 'people'); return ppl.length ? peopleList(ppl) : none; }
        if (panel.tab === 'years') { return [block(FACETS.years, [histogram()])]; }
        if (panel.tab === 'state') { return [block(FACETS.state, [states()])]; }
        var all = h('div', { 'class': 'pj-ix-overview' });
        var size = function (k) { return counts(ix, k).size; };
        var more = function (k, shown, text) { return size(k) > shown ? [k, 'Voir les ' + nf(size(k)) + ' ' + text] : [null, null]; };
        var section = function (title, content, link) { return block(title, content, link[0], link[1]); };
        if (size('genres')) { all.appendChild(section(FACETS.genres, [genreTiles(ranked(ix, 'genres').slice(0, 6))], more('genres', 6, 'genres'))); }
        if (size('tags')) { all.appendChild(section(size('tags') > 30 ? 'Étiquettes les plus fréquentes' : FACETS.tags, [words('tags', ranked(ix, 'tags').slice(0, 30).sort(function (a, b) { return byName(a[0], b[0]); }))], more('tags', 30, 'étiquettes'))); }
        if (!ix.peopleReady) { all.appendChild(block(FACETS.people, [note(waiting)])); }
        else if (size('people')) { all.appendChild(section(FACETS.people, [faces(ranked(ix, 'people').slice(0, 16), ' pj-ix-oneline')], more('people', 6, 'personnes'))); }
        all.appendChild(h('div', { 'class': 'pj-ix-pair' }, [size('years') ? block(FACETS.years, [histogram()]) : null, block(FACETS.state, [states()])]));
        return [all];
    }
    function drawPanel() {
        var ix = panel.ix;
        var offered = function (k) { return ix.loading || (k === 'people' && !ix.peopleReady) || counts(ix, k).size > 0; };
        var tabs = [['all', 'Aperçu']].concat(Object.keys(FACETS).filter(offered).map(function (k) { return [k, FACETS[k]]; }));
        if (!ix.loading && panel.tab !== 'all' && !offered(panel.tab)) { panel.tab = 'all'; }
        var kept = panel.tabs.contains(document.activeElement) ? document.activeElement.getAttribute('data-tab') : null;
        var focus = panel.body.contains(document.activeElement) && document.activeElement.getAttribute('data-k') != null ? [document.activeElement.getAttribute('data-k'), document.activeElement.getAttribute('data-v')] : null;
        panel.tabs.replaceChildren.apply(panel.tabs, tabs.map(function (t) {
            var sized = !ix.loading && (t[0] === 'genres' || t[0] === 'tags' || t[0] === 'studios' || (t[0] === 'people' && ix.peopleReady));
            return button('pj-ix-tab', { 'data-tab': t[0], 'aria-pressed': String(!panel.q && panel.tab === t[0]) }, [t[1], sized ? h('small', { text: nf(counts(ix, t[0]).size) }) : null]);
        }));
        var top = panel.body.scrollTop;
        panel.body.replaceChildren.apply(panel.body, panelContent());
        panel.body.scrollTop = top;
        if (panel.q) { var first = panel.body.querySelector('[data-k]'); if (first) { first.classList.add('pj-ix-first'); } }
        var picked = chosen(ix).map(function (c) { return button('pj-ix-picked', { 'data-k': c[0], 'data-v': c[1], 'aria-label': 'Retirer le filtre ' + label(c[0], c[1]), text: label(c[0], c[1]) + ' ✕' }); });
        if (picked.length) { picked.push(button('pj-ix-textbtn', { 'data-clear': true, text: 'Tout effacer' })); } else { picked.push(note('Aucun filtre choisi')); }
        panel.foot.replaceChildren(h('div', { 'class': 'pj-ix-pickedbar' }, picked), button('pj-ix-btn pj-ix-go', { 'data-close': true, text: ix.loading ? 'Chargement…' : 'Afficher ' + count(results(ix).length, ix.type) }));
        // the buttons were rebuilt: keep the keyboard where it was
        if (kept) { var tabButton = panel.tabs.querySelector('[data-tab="' + kept + '"]'); if (tabButton) { tabButton.focus({ preventScroll: true }); } }
        if (focus) {
            var same = Array.prototype.find.call(panel.body.querySelectorAll('[data-k]'), function (n) { return n.getAttribute('data-k') === focus[0] && n.getAttribute('data-v') === focus[1]; });
            if (same) { same.focus({ preventScroll: true }); }
        }
    }

    // ---------- the way in from a library page ----------
    function libraryEntry(page) {
        var m = /^#\/(tv|tv\.html|movies|movies\.html)\?/.exec(location.hash);
        var parent = m && params().get('topParentId');
        if (!parent || !page) { return; }
        // the toolbar of the shown tab: beside jellyfin-web's own filter button (experimental or classic layout)
        var scope = page.querySelector('.pageTabContent.is-active') || page;
        var anchor = scope.querySelector('.btnFilter-wrapper, .btnFilter');
        if (!anchor) {
            var group = scope.querySelector('.MuiButtonGroup-root');
            anchor = group && group.parentNode.querySelector('[data-testid="FilterAltIcon"]') ? group : null;
        }
        var type = m[1].indexOf('movies') === 0 ? 'Movie' : params().get('tab') === '5' ? 'Episode' : 'Series';
        var href = '#/list?parentId=' + parent + '&type=' + type + '&pj=index&serverId=' + serverId();
        var link = scope.querySelector('.pj-ix-entry');
        if (!anchor) { if (link) { link.remove(); } return; }
        if (!link) { link = h('a', { 'class': 'pj-ix-entry' }, [icon('tune'), 'Tous les filtres']); anchor.parentNode.insertBefore(link, anchor.nextSibling); }
        if (link.getAttribute('href') !== href) { link.setAttribute('href', href); }
    }

    // ---------- runner: jellyfin-web is a single-page app, so run after every change of view ----------
    var scheduled = null, lastHash = '';
    function run() {
        scheduled = null;
        if (!client() || !themed()) { return; }
        var page = currentPage();
        try { libraryEntry(page); } catch (e) { warn('library entry', e); }
        var r = route();
        if (panel && (!r || panel.ix.hash !== location.hash)) { closePanel(); }
        if (!r || !page || ignored.has(location.hash)) {
            // a list jellyfin-web reuses for something we leave alone: give it back
            if (page && page.__pjIx && page.__pjIx.hash !== location.hash && /^#\/list/.test(location.hash)) { drop(page.__pjIx); }
            lastHash = location.hash;
            return;
        }
        // jellyfin-web's list page (not the page being left)
        if (page.classList.contains('itemDetailPage') || !page.querySelector('.alphaPicker')) { return; }
        // the page being left is still shown for a moment: wait for the new one
        if (page.__pjView && page.__pjView !== location.hash) { return; }
        var ix = page.__pjIx;
        // a new visit of the same address (its link clicked again) starts from its own filter; "back" keeps the key
        var revisit = ix && ix.key !== ix.hash && ix.key !== visitKey();
        if (!ix || ix.hash !== location.hash || revisit || !page.contains(ix.root)) { build(page, r); }
        else if (lastHash !== location.hash && !ix.loading) {
            // back on a page already built: what was watched meanwhile shows
            load(ix, ix.anchor, true).catch(function (e) { warn('refresh', e); });
        }
        lastHash = location.hash;
    }
    function schedule() { if (!scheduled) { scheduled = setTimeout(run, 120); } }
    window.addEventListener('hashchange', function () { closePanel(); schedule(); });
    document.addEventListener('viewshow', function (ev) { if (ev.target && ev.target.classList) { ev.target.__pjView = location.hash; } schedule(); }, true);
    window.addEventListener('resize', function () { var page = currentPage(); if (page && page.__pjIx) { place(page.__pjIx); } });
    document.addEventListener('keydown', function (ev) {
        if (ev.key !== '/' || panel || ev.ctrlKey || ev.metaKey || ev.altKey || /INPUT|SELECT|TEXTAREA/.test((document.activeElement || {}).tagName || '')) { return; }
        var page = currentPage();
        if (!page || !page.__pjIx) { return; }
        ev.preventDefault();
        openPanel(page.__pjIx, 'all', document.activeElement);
    });
    new MutationObserver(function (records) {
        // our own additions do not count
        for (var i = 0; i < records.length; i++) {
            var t = records[i].target;
            if (!(t.closest && t.closest('.pj-ix, .pj-ix-panelbox, .pj-preview'))) { schedule(); return; }
        }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    schedule();
})();
