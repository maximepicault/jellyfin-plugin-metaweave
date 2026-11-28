/*
 * Projection theme - photo libraries: jellyfin-web 12's "home videos and photos" library (its « Dossiers » view) and its
 * albums, rebuilt.
 *
 * Served by the Custom Metadata plugin (CustomMetadata/Theme/projection-photos.js) and added to jellyfin-web's page with
 * projection.js when the plugin's "Améliorations du thème Projection" setting is on. It only acts when the theme's
 * stylesheet is the one that styles it (--pj-photos), only reads (the favourite button apart), and leaves the stock page
 * as it is for anything it does not handle (the library's other views, other libraries, the TV layout).
 *
 * The screens:
 * - the library: a wall of albums, one level at a time, with a search and the tags and genres one click away;
 * - an album: its picture at the head, whole whatever its shape (the ambient light of its colours beside it), its
 *   sheet, its sub-albums, then its photos and videos at their own ratios;
 * - a viewer: the photo or the video, and beside it what is known about it;
 * - the people linked to albums, photos and videos (kept by the plugin, CustomMetadata/AlbumCast): on the album, in the
 *   viewer, as a filter of the library, and as a section of jellyfin-web's person page.
 * Labels are French, like the rest of the theme.
 */
(function () {
    'use strict';
    if (window.__projectionPhotos) { return; }
    window.__projectionPhotos = '1';

    var warn = function (where, error) { try { console.warn('[projection photos] ' + where, error); } catch (e) { /* no console */ } };
    var PAGE = 120, SLIDE_MS = 5000, SHORT_RIBBON = 18, SHORT_PEOPLE = 10;
    var ALBUM_TYPES = 'PhotoAlbum,Folder', MEDIA_TYPES = 'Photo,Video';
    // the library's other views, as jellyfin-web's own view menu lists them (their `tab=` number)
    var VIEWS = [['Albums', 0], ['Toutes les photos', 1], ['Albums photo', 2], ['Vidéos', 3]];
    var SORTS = [['name', 'Par nom'], ['date', 'Plus récents'], ['added', 'Ajoutés récemment']];
    var ORDER = {
        name: { SortBy: 'SortName', SortOrder: 'Ascending' },
        date: { SortBy: 'PremiereDate,SortName', SortOrder: 'Descending,Ascending' },
        added: { SortBy: 'DateCreated,SortName', SortOrder: 'Descending,Ascending' }
    };

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
    function count(n, one, many) { return nf(n) + ' ' + (n > 1 ? many : one); }
    function day(value) {
        var d = value ? new Date(value) : null;
        return d && !isNaN(d.getTime()) ? d.toLocaleDateString('fr-FR', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }) : '';
    }
    function themed() { var root = document.documentElement; return !root.classList.contains('layout-tv') && getComputedStyle(root).getPropertyValue('--pj-photos').trim() === '1'; }
    function currentPage() { return document.querySelector('.page:not(.hide)'); }
    function params() { var at = location.hash.indexOf('?'); return new URLSearchParams(at < 0 ? '' : location.hash.slice(at + 1)); }
    function serverId() { var c = client(); return c && c.serverId ? c.serverId() : ''; }
    function href(path, id) { return '#/' + path + '=' + id + (serverId() ? '&serverId=' + serverId() : ''); }
    function albumHref(id) { return href('list?parentId', id); }
    function detailsHref(id) { return href('details?id', id); }
    function image(id, type, tag, width, height) {
        var o = { type: type, maxWidth: width, quality: 90 };
        if (height) { o.maxHeight = height; }
        if (tag) { o.tag = tag; }
        return client().getImageUrl(id, o);
    }
    // The server makes each size from the original file, which takes seconds on a large photo. Pictures are therefore asked
    // in a few fixed boxes: what the server made then serves again, and the plugin can make them in advance. These boxes
    // must stay those of Services/ImagePreparationService.
    var CARD = 640, TILE = [1200, 360], SCREEN = [1920, 1080], WIDE_SCREEN = [2560, 1440];
    // the box of a large picture: the wide one only for a place that clearly shows more than Full HD
    function screenBox(width, height) {
        var k = Math.min(window.devicePixelRatio || 1, 2);
        return width * k > 2000 || height * k > 1150 ? WIDE_SCREEN : SCREEN;
    }
    // priority 'high': ahead of the thumbnails the browser is still waiting for
    function fetchImage(url, priority) { var img = new Image(); img.fetchPriority = priority; img.src = url; return img; }
    function stopImage(img) { if (img && !img.complete) { img.onload = img.onerror = null; img.removeAttribute('src'); } }
    function get(path, query) { var c = client(); return c.getJSON(c.getUrl(path, Object.assign({ userId: c.getCurrentUserId() }, query || {}))); }
    function list(query) {
        return get('Items', Object.assign({ Fields: 'Overview,Tags,Genres,Studios,ChildCount,PrimaryImageAspectRatio,Width,Height', EnableImageTypes: 'Primary,Backdrop', ImageTypeLimit: 1 }, query));
    }
    function isAlbum(it) { return it.Type === 'PhotoAlbum' || it.Type === 'Folder'; }
    function ratioOf(it) { var r = it.PrimaryImageAspectRatio || (it.Width && it.Height ? it.Width / it.Height : 0); return r > 0.2 && r < 5 ? r : (it.Type === 'Video' ? 16 / 9 : 1.5); }
    // "1:02:03" or "4:05", from Jellyfin's ticks
    function length(ticks) {
        var t = Math.round((ticks || 0) / 1e7), s = t % 60, m = Math.floor(t / 60) % 60, hours = Math.floor(t / 3600), two = function (n) { return (n < 10 ? '0' : '') + n; };
        return t ? (hours ? hours + ':' + two(m) : m) + ':' + two(s) : '';
    }
    // "12 photos · 2 vidéos", from the number of media and how many of them are videos
    function mediaCounts(total, videos) {
        return [total - videos > 0 ? count(total - videos, 'photo', 'photos') : '', videos ? count(videos, 'vidéo', 'vidéos') : ''];
    }
    function mediaTitle(total, videos) { return !videos ? 'Photos' : (videos >= total ? 'Vidéos' : 'Photos et vidéos'); }
    function howMany(query) { return list(Object.assign({}, query, { IncludeItemTypes: 'Video', Limit: 0, StartIndex: 0 })).then(function (r) { return r.TotalRecordCount; }); }

    // ---------- people ----------
    function cast(path, query) { var c = client(); return c.getJSON(c.getUrl('CustomMetadata/AlbumCast/' + path, query || {})); }
    // a person as a chip (their photo when there is one): a link to their albums and photos in the library, or a button
    function personChip(person, attrs, extra) {
        var picture = person.Image ? (/^https?:/i.test(person.Image) ? person.Image : client().getUrl(person.Image)) : null;
        var face = h('span', { 'class': 'pj-ph-face', 'aria-hidden': 'true' }, picture ? h('img', { alt: '', loading: 'lazy', src: picture }) : (person.Name || '?').charAt(0).toUpperCase());
        var inside = [face, h('span', {}, [person.Name, person.Role ? h('small', { text: person.Role }) : null]), extra || null];
        return attrs.href ? h('a', Object.assign({ 'class': 'pj-ph-person' }, attrs), inside) : button('pj-ph-person' + (attrs['aria-pressed'] === 'true' ? ' pj-ph-on-chip' : ''), attrs, inside);
    }
    function personHref(libraryId, name) { return href('homevideos?topParentId', libraryId) + '&pjperson=' + encodeURIComponent(name); }
    // one entry per person (a person may hold several jobs)
    function once(members) {
        var seen = new Set();
        return (members || []).filter(function (m) { var k = String(m.Name).toLowerCase(); if (seen.has(k)) { return false; } seen.add(k); return true; });
    }
    // items by their ids, a hundred at a time (an address cannot hold more)
    function byIds(ids) {
        var out = [], next = function (from) {
            if (from >= Math.min(ids.length, 600)) { return Promise.resolve(out); }
            return list({ Ids: ids.slice(from, from + 100).join(',') }).then(function (r) { out = out.concat(r.Items); return next(from + 100); });
        };
        return next(0);
    }

    // ---------- which pages are ours ----------
    // the « Dossiers » view of a library (the default one, or tab=0), or the list of a folder
    function route() {
        var q = params();
        if (/^#\/homevideos(\.html)?\?/.test(location.hash)) {
            var tab = q.get('tab');
            return q.get('topParentId') && (tab === null || tab === '0') ? { kind: 'library', id: q.get('topParentId'), tag: q.get('pjtag'), person: q.get('pjperson') } : null;
        }
        if (/^#\/list(\.html)?\?/.test(location.hash) && q.get('parentId')) {
            var other = ['genreId', 'studioId', 'personId', 'type', 'IsFavorite', 'musicGenreId'].some(function (k) { return q.get(k); });
            return other ? null : { kind: 'album', id: q.get('parentId') };
        }
        return null;
    }

    // ---------- a screen ----------
    var ignored = new Set();     // screens left to jellyfin-web: not a photo library, or one that could not be built
    function build(host, r) {
        if (host.__pjPh) { drop(host.__pjPh); }
        var root = h('div', { 'class': 'pj-ph pj-ph-' + r.kind });
        var s = { host: host, route: r, hash: location.hash, root: root, run: 0, sort: 'name', photos: [], total: 0, tag: r.tag || null, genre: null, person: r.person || null, people: [], search: '' };
        host.__pjPh = s;
        // hidden until the screen is known to be a photo one: another folder's list stays jellyfin-web's, without a flash
        root.hidden = true;
        root.appendChild(h('p', { 'class': 'pj-ph-note', text: 'Chargement…' }));
        host.appendChild(root);
        (r.kind === 'library' ? library(s) : album(s)).catch(function (error) {
            warn('load', error);
            ignored.add(s.hash);
            drop(s);
        });
    }
    function drop(s) {
        s.run++;
        if (s.watch) { s.watch.disconnect(); }
        s.root.remove();
        s.host.classList.remove('pj-ph-on');
        s.host.__pjPh = null;
    }
    function alive(s) { return s.host.__pjPh === s; }
    function reveal(s) {
        if (!s.root.hidden) { return; }
        s.root.hidden = false;
        s.host.classList.add('pj-ph-on');
        window.scrollTo(0, 0);
        followBar();
    }

    // an album Jellyfin gave no cover: its first photo at any depth, else a frame of its first video
    function firstPhoto(id) {
        var first = function (type) {
            return list({ ParentId: id, Recursive: true, IncludeItemTypes: type, Limit: 5, SortBy: 'SortName' }).then(function (r) {
                var it = r.Items.filter(function (x) { return x.ImageTags && x.ImageTags.Primary; })[0];
                return it ? { id: it.Id, type: 'Primary', tag: it.ImageTags.Primary } : null;
            });
        };
        return first('Photo').then(function (found) { return found || first('Video'); });
    }
    function albumCard(it) {
        var tag = it.ImageTags && it.ImageTags.Primary;
        var frame = h('span', { 'class': 'pj-ph-frame' }, tag ? h('img', { alt: '', loading: 'lazy', fetchpriority: 'low', src: image(it.Id, 'Primary', tag, CARD) }) : icon('photo_library'));
        if (!tag) {
            firstPhoto(it.Id).then(function (c) { if (c) { frame.replaceChildren(h('img', { alt: '', loading: 'lazy', fetchpriority: 'low', src: image(c.id, c.type, c.tag, CARD) })); } }, function (e) { warn('cover', e); });
        }
        var meta = [it.ChildCount != null ? count(it.ChildCount, 'élément', 'éléments') : '', it.ProductionYear ? String(it.ProductionYear) : ''].filter(Boolean).join(' · ');
        return h('a', { 'class': 'pj-ph-card', href: albumHref(it.Id) }, [h('span', { 'class': 'pj-ph-stack' }, frame), h('strong', { text: it.Name }), h('span', { 'class': 'pj-ph-meta', text: meta })]);
    }
    // the wall's thumbnail of a photo or video, also what the viewer shows while the large picture is on its way
    function thumbnail(it) { return image(it.Id, 'Primary', it.ImageTags && it.ImageTags.Primary, TILE[0], TILE[1]); }
    function mediaTile(s, it) {
        var tag = it.ImageTags && it.ImageTags.Primary, ratio = ratioOf(it), video = it.Type === 'Video';
        var inside = [tag ? h('img', { alt: '', loading: 'lazy', fetchpriority: 'low', src: thumbnail(it) }) : icon(video ? 'movie' : 'photo'),
            h('span', { 'class': 'pj-ph-cap', text: it.Name })];
        if (video) { inside.push(icon('play_arrow'), h('span', { 'class': 'pj-ph-length', text: length(it.RunTimeTicks) })); }
        var tile = button('pj-ph-tile' + (video ? ' pj-ph-video' : ''), { 'aria-label': (video ? 'Vidéo : ' : '') + it.Name }, inside);
        tile.addEventListener('click', function () { openViewer(s, s.media.indexOf(it), tile, false); });
        tile.style.setProperty('--r', String(ratio));
        return tile;
    }
    function section(title, content) { return h('section', { 'class': 'pj-ph-section' }, [title ? h('h2', { text: title }) : null, content]); }

    // the photos and videos of a screen, a page at a time as one scrolls
    function mediaWall(s, query) {
        var wall = h('div', { 'class': 'pj-ph-wall' }), sentinel = h('div', { 'class': 'pj-ph-sentinel', 'aria-hidden': 'true' });
        var box = h('div', {}, [wall, sentinel]), busy = false, run = ++s.run;
        s.media = []; s.total = 0; s.query = query;
        s.more = function () {
            if (busy || run !== s.run || (s.total && s.loaded >= s.total)) { return Promise.resolve(); }
            busy = true;
            return list(Object.assign({ StartIndex: s.loaded, Limit: PAGE }, query)).then(function (answer) {
                busy = false;
                if (run !== s.run) { return; }
                s.total = answer.TotalRecordCount;
                s.loaded += answer.Items.length;
                answer.Items.forEach(function (it) {
                    s.media.push(it);
                    wall.appendChild(mediaTile(s, it));
                });
                if (!answer.Items.length) { s.total = s.loaded; }
            }, function (error) { busy = false; warn('photos', error); });
        };
        s.loaded = 0;
        if (s.watch) { s.watch.disconnect(); }
        if (window.IntersectionObserver) {
            s.watch = new IntersectionObserver(function (e) { if (e[e.length - 1].isIntersecting) { s.more(); } }, { rootMargin: '0px 0px 900px' });
            s.watch.observe(sentinel);
        }
        return { box: box, first: s.more() };
    }

    // ---------- the library: a wall of albums ----------
    function library(s) {
        var id = s.route.id;
        // the tags and the people take longer to gather on a large library: the screen does not wait for them
        var filtersAsked = get('Items/Filters', { ParentId: id, IncludeItemTypes: ALBUM_TYPES + ',Photo' }).catch(function () { return {}; });
        var peopleAsked = cast('People', { parentId: id }).catch(function (e) { warn('people', e); return []; });
        return Promise.all([
            get('Items/' + id),
            list({ ParentId: id, Recursive: true, IncludeItemTypes: 'Photo', Limit: 1 })
        ]).then(function (answers) {
            if (!alive(s)) { return; }
            var item = answers[0];
            s.homeId = id;
            if (item.CollectionType !== 'homevideos' || !answers[1].TotalRecordCount) { throw new Error('not a photo library'); }
            reveal(s);
            s.terms = [];
            s.countLine = h('span', { 'class': 'pj-ph-meta' });
            var search = h('input', { 'class': 'pj-ph-search', type: 'search', placeholder: 'Chercher un album, une photo…', 'aria-label': 'Chercher un album ou une photo' });
            var timer = null;
            search.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(function () { if (alive(s) && s.search !== search.value.trim()) { s.search = search.value.trim(); results(s); } }, 300); });
            var sort = h('select', { 'class': 'pj-ph-sort', 'aria-label': 'Trier' }, SORTS.map(function (o) { return h('option', { value: o[0], text: o[1] }); }));
            sort.addEventListener('change', function () { s.sort = sort.value; results(s); });
            s.ribbonBox = h('div', { 'class': 'pj-ph-ribbon', role: 'group', 'aria-label': 'Étiquettes et genres' });
            s.peopleBox = h('div', { 'class': 'pj-ph-ribbon pj-ph-peoplebar', role: 'group', 'aria-label': 'Personnes' });
            s.resultsBox = h('div', { 'class': 'pj-ph-results', 'aria-live': 'polite' });
            var views = h('nav', { 'class': 'pj-ph-views', 'aria-label': 'Vues de la médiathèque' }, VIEWS.map(function (view) {
                return view[1] === 0 ? h('span', { 'aria-current': 'page', text: view[0] }) : h('a', { href: href('homevideos?topParentId', id) + '&tab=' + view[1], text: view[0] });
            }));
            s.root.replaceChildren(h('header', { 'class': 'pj-ph-libhead' }, [h('div', {}, [views, h('h1', { text: item.Name }), s.countLine]), search, sort]), s.ribbonBox, s.peopleBox, s.resultsBox);
            ribbon(s);
            peopleRibbon(s);
            filtersAsked.then(function (filters) {
                if (!alive(s)) { return; }
                s.terms = ((filters || {}).Tags || []).map(function (t) { return ['tag', t]; }).concat(((filters || {}).Genres || []).map(function (g) { return ['genre', g]; }));
                ribbon(s);
            });
            peopleAsked.then(function (people) { if (alive(s)) { s.people = people || []; peopleRibbon(s); } });
            return results(s);
        });
    }
    function ribbon(s) {
        var open = !!s.allTerms, chip = function (label, on, apply) {
            var b = button('pj-ph-chip' + (on ? ' pj-ph-on-chip' : ''), { 'aria-pressed': on ? 'true' : 'false', text: label });
            b.addEventListener('click', function () { apply(); ribbon(s); peopleRibbon(s); results(s); });
            return b;
        };
        var shown = open ? s.terms : s.terms.slice(0, SHORT_RIBBON);
        // the chosen word stays in sight
        var chosen = s.terms.filter(function (t) { return (t[0] === 'tag' ? s.tag : s.genre) === t[1]; })[0];
        if (chosen && shown.indexOf(chosen) < 0) { shown = [chosen].concat(shown); }
        var chips = [chip('Tout', !s.tag && !s.genre && !s.person, function () { s.tag = null; s.genre = null; s.person = null; })].concat(shown.map(function (t) {
            var on = (t[0] === 'tag' ? s.tag : s.genre) === t[1];
            return chip(t[1], on, function () { s.tag = !on && t[0] === 'tag' ? t[1] : null; s.genre = !on && t[0] === 'genre' ? t[1] : null; });
        }));
        if (s.terms.length > SHORT_RIBBON) {
            var more = button('pj-ph-chip pj-ph-all', { 'aria-expanded': open ? 'true' : 'false', text: open ? 'Réduire' : 'Tous (' + nf(s.terms.length) + ')' });
            more.addEventListener('click', function () { s.allTerms = !open; ribbon(s); });
            chips.push(more);
        }
        // « Tout » also leaves a chosen person, even in a library without a single tag
        s.ribbonBox.replaceChildren.apply(s.ribbonBox, s.terms.length || s.person ? chips : []);
    }
    // the people of the library's albums and photos: one at a time
    function peopleRibbon(s) {
        var open = !!s.allPeople, shown = open ? s.people : s.people.slice(0, SHORT_PEOPLE);
        var chosen = s.people.filter(function (p) { return p.Name === s.person; })[0];
        if (chosen && shown.indexOf(chosen) < 0) { shown = [chosen].concat(shown); }
        var chips = shown.map(function (p) {
            var on = p.Name === s.person;
            var b = personChip(p, { 'aria-pressed': on ? 'true' : 'false' }, h('span', { 'class': 'pj-ph-count', text: nf(p.Count) }));
            b.addEventListener('click', function () { s.person = on ? null : p.Name; if (s.person) { s.tag = null; s.genre = null; } ribbon(s); peopleRibbon(s); results(s); });
            return b;
        });
        if (s.people.length > SHORT_PEOPLE) {
            var more = button('pj-ph-chip pj-ph-all', { 'aria-expanded': open ? 'true' : 'false', text: open ? 'Réduire' : 'Toutes les personnes (' + nf(s.people.length) + ')' });
            more.addEventListener('click', function () { s.allPeople = !open; peopleRibbon(s); });
            chips.push(more);
        }
        s.peopleBox.replaceChildren.apply(s.peopleBox, chips);
    }
    // everything one person is linked to in this library
    function personResults(s, turn) {
        return cast('Items', { person: s.person, parentId: s.route.id }).then(byIds).then(function (items) {
            if (!alive(s) || turn !== s.turn) { return; }
            var albums = items.filter(isAlbum), media = items.filter(function (it) { return !isAlbum(it); });
            var videos = media.filter(function (it) { return it.Type === 'Video'; }).length;
            s.run++; s.media = media; s.total = media.length; s.loaded = media.length; s.more = function () { return Promise.resolve(); };
            s.countLine.textContent = [albums.length ? count(albums.length, 'album', 'albums') : ''].concat(mediaCounts(media.length, videos)).filter(Boolean).join(' · ') || 'Aucun résultat';
            var parts = [];
            if (albums.length) { parts.push(section('Albums avec ' + s.person, h('div', { 'class': 'pj-ph-albums' }, albums.map(albumCard)))); }
            if (media.length) { parts.push(section(mediaTitle(media.length, videos) + ' avec ' + s.person, h('div', { 'class': 'pj-ph-wall' }, media.map(function (it) { return mediaTile(s, it); })))); }
            if (!parts.length) { parts.push(h('p', { 'class': 'pj-ph-note', text: 'Aucun album, photo ni vidéo avec ' + s.person + ' dans cette médiathèque.' })); }
            s.resultsBox.replaceChildren.apply(s.resultsBox, parts);
        });
    }
    // the library's own level, or, once something is searched or chosen, everything that matches, at any depth
    function results(s) {
        var id = s.route.id, filtered = !!(s.search || s.tag || s.genre);
        var query = { ParentId: id, Recursive: filtered };
        if (s.search) { query.SearchTerm = s.search; }
        if (s.tag) { query.Tags = s.tag; }
        if (s.genre) { query.Genres = s.genre; }
        var turn = s.turn = (s.turn || 0) + 1;
        // a person is a filter of its own: a search or a tag chosen afterwards leaves it
        if (s.person && !s.search && !s.tag && !s.genre) { return personResults(s, turn); }
        return list(Object.assign({ IncludeItemTypes: ALBUM_TYPES, Limit: 600 }, query, ORDER[s.sort])).then(function (albums) {
            if (!alive(s) || turn !== s.turn) { return null; }
            var media = mediaWall(s, Object.assign({ IncludeItemTypes: MEDIA_TYPES }, query, ORDER[s.sort]));
            return Promise.all([howMany(query), media.first]).then(function (counted) {
                if (!alive(s) || turn !== s.turn) { return; }
                var videos = counted[0];
                s.countLine.textContent = [albums.TotalRecordCount ? count(albums.TotalRecordCount, 'album', 'albums') : ''].concat(mediaCounts(s.total, videos)).filter(Boolean).join(' · ') || 'Aucun résultat';
                var parts = [];
                if (albums.Items.length) { parts.push(section(filtered ? 'Albums' : null, h('div', { 'class': 'pj-ph-albums' }, albums.Items.map(albumCard)))); }
                if (s.total) { parts.push(section(filtered || albums.Items.length ? mediaTitle(s.total, videos) : null, media.box)); }
                if (!parts.length) { parts.push(h('p', { 'class': 'pj-ph-note', text: filtered ? 'Rien ne correspond. Essayez un autre mot, ou « Tout ».' : 'Cette médiathèque est vide.' })); }
                s.resultsBox.replaceChildren.apply(s.resultsBox, parts);
            });
        });
    }

    // ---------- an album ----------
    function album(s) {
        var id = s.route.id;
        var self = get('Items/' + id).then(function (item) {
            // Jellyfin only makes a PhotoAlbum in a photo library: that screen is ours at once
            if (alive(s) && item.Type === 'PhotoAlbum') {
                reveal(s);
                // its head picture is asked for before the wall's thumbnails
                var own = ownPictures(item)[0];
                if (own) { fetchImage(headImage(own), 'high'); }
            }
            return item;
        });
        var peopleAsked = cast('Item/' + id).catch(function (e) { warn('people', e); return []; });
        return Promise.all([
            self,
            get('Items/' + id + '/Ancestors'),
            list(Object.assign({ ParentId: id, IncludeItemTypes: ALBUM_TYPES }, ORDER.name))
        ]).then(function (answers) {
            if (!alive(s)) { return null; }
            var item = answers[0], ancestors = answers[1] || [], albums = answers[2].Items;
            var home = ancestors.filter(function (a) { return a.CollectionType === 'homevideos'; })[0];
            s.homeId = home && home.Id;
            if (!isAlbum(item) || !home) { throw new Error('not a photo album'); }
            // a library of home videos without any photo keeps jellyfin-web's own screens
            var photos = item.Type === 'PhotoAlbum' ? Promise.resolve(true)
                : list({ ParentId: home.Id, Recursive: true, IncludeItemTypes: 'Photo', Limit: 1 }).then(function (r) { return r.TotalRecordCount > 0; });
            return photos.then(function (has) {
            if (!has) { throw new Error('not a photo library'); }
            if (!alive(s)) { return null; }
            s.item = item;
            reveal(s);
            var media = mediaWall(s, Object.assign({ ParentId: id, IncludeItemTypes: MEDIA_TYPES }, ORDER.name));
            return Promise.all([howMany({ ParentId: id }), media.first]).then(function (counted) {
                if (!alive(s)) { return; }
                var videos = counted[0];
                // from the library down to the parent: the folders above the library are not part of the path
                var path = ancestors.slice(0, ancestors.indexOf(home) + 1).reverse();
                var crumbs = h('nav', { 'class': 'pj-ph-crumbs', 'aria-label': 'Chemin' }, path.map(function (a) {
                    return h('a', { href: a === home ? href('homevideos?topParentId', a.Id) : albumHref(a.Id), text: a.Name });
                }).concat(h('span', { 'aria-current': 'page', text: item.Name })));
                var meta = [day(item.PremiereDate), albums.length ? count(albums.length, 'sous-album', 'sous-albums') : ''].concat(mediaCounts(s.total, videos), [
                    item.CommunityRating ? '★ ' + nf(Math.round(item.CommunityRating * 10) / 10) : '', (item.Studios || []).map(function (x) { return x.Name; }).join(', ')]).filter(Boolean).join(' · ');
                var actions = h('div', { 'class': 'pj-ph-actions' });
                if (s.media.length) {
                    var play = button('pj-ph-btn pj-ph-main', {}, [icon('play_arrow'), 'Diaporama']);
                    play.addEventListener('click', function () { openViewer(s, 0, play, true); });
                    var shuffle = button('pj-ph-btn', {}, [icon('shuffle'), 'Aléatoire']);
                    shuffle.addEventListener('click', function () {
                        list({ ParentId: id, IncludeItemTypes: MEDIA_TYPES, SortBy: 'Random', Limit: 200 }).then(function (r) { if (alive(s) && r.Items.length) { openViewer(s, 0, shuffle, true, r.Items); } }, function (e) { warn('shuffle', e); });
                    });
                    actions.append(play, shuffle);
                }
                actions.append(favouriteButton(item), h('a', { 'class': 'pj-ph-btn pj-ph-round', href: detailsHref(item.Id), title: 'Fiche Jellyfin', 'aria-label': 'Fiche Jellyfin' }, icon('info')));
                var words = (item.Tags || []).map(function (t) { return h('a', { 'class': 'pj-ph-chip', href: href('homevideos?topParentId', home.Id) + '&pjtag=' + encodeURIComponent(t), text: t }); })
                    .concat((item.Genres || []).map(function (g) { return h('span', { 'class': 'pj-ph-chip', text: g }); }));
                var picture = h('div', { 'class': 'pj-ph-pic', 'aria-hidden': 'true' });
                var wordsRow = words.length ? h('div', { 'class': 'pj-ph-words' }, words) : null;
                var under = h('div', { 'class': 'pj-ph-under' }, [
                    crumbs, h('h1', { text: item.Name }), h('p', { 'class': 'pj-ph-meta', text: meta }), actions,
                    item.Overview ? overview(item.Overview) : null,
                    wordsRow
                ]);
                var hero = h('header', { 'class': 'pj-ph-hero pj-ph-bare' }, [picture, under]);
                peopleAsked.then(function (people) {
                    if (!alive(s) || !people || !people.length) { return; }
                    under.insertBefore(h('div', { 'class': 'pj-ph-people', 'aria-label': 'Personnes' }, once(people).map(function (p) { return personChip(p, { href: personHref(home.Id, p.Name) }); })), wordsRow);
                });
                var parts = [hero];
                if (albums.length) { parts.push(section('Albums', h('div', { 'class': 'pj-ph-albums' }, albums.map(albumCard)))); }
                if (s.total) { parts.push(section(mediaTitle(s.total, videos), media.box)); }
                if (!albums.length && !s.total) { parts.push(h('p', { 'class': 'pj-ph-note', text: 'Cet album est vide.' })); }
                s.root.replaceChildren.apply(s.root, parts);
                headPicture(s, hero, picture, albums);
            });
            });
        });
    }
    // a long description is folded to a few lines: the albums and photos stay in reach
    function overview(text) {
        var words = h('p', { 'class': 'pj-ph-overview', text: text });
        var more = button('pj-ph-more', { 'aria-expanded': 'false', text: 'Lire la suite', hidden: true });
        more.addEventListener('click', function () {
            var open = words.classList.toggle('pj-ph-open');
            more.setAttribute('aria-expanded', open ? 'true' : 'false');
            more.textContent = open ? 'Réduire' : 'Lire la suite';
        });
        var measure = function () { if (!words.classList.contains('pj-ph-open')) { more.hidden = words.scrollHeight <= words.clientHeight + 1; } };
        // measured again whenever the text's box changes (first layout, window width)
        if (window.ResizeObserver) { new ResizeObserver(measure).observe(words); } else { requestAnimationFrame(measure); }
        return h('div', { 'class': 'pj-ph-text' }, [words, more]);
    }
    // an album's own pictures for its head: its backdrop, else its cover
    function ownPictures(item) {
        var out = [], add = function (type, tag) { if (tag) { out.push({ id: item.Id, type: type, tag: tag }); } };
        add('Backdrop', item.BackdropImageTags && item.BackdropImageTags[0]);
        add('Primary', item.ImageTags && item.ImageTags.Primary);
        return out;
    }
    // shown under 60 % of the window's height
    function headImage(c) { var box = screenBox(window.innerWidth, window.innerHeight * 0.6); return image(c.id, c.type, c.tag, box[0], box[1]); }
    // the picture at the head: the album's backdrop, else its cover, else its first photo or its first sub-album's cover
    function headPicture(s, hero, box, albums) {
        var candidates = ownPictures(s.item), add = function (it, type, tag) { if (tag) { candidates.push({ id: it.Id, type: type, tag: tag }); } };
        // a photo before a video's frame
        var photo = s.media.filter(function (it) { return it.Type === 'Photo'; })[0] || s.media[0];
        [photo].concat(albums.slice(0, 1)).forEach(function (it) { if (it) { add(it, 'Primary', it.ImageTags && it.ImageTags.Primary); } });
        var deeper = false;
        var next = function (k) {
            var c = candidates[k];
            if (!alive(s)) { return; }
            if (!c) {
                // an album of albums without any picture of its own: a photo from further down
                if (!deeper) { deeper = true; firstPhoto(s.item.Id).then(function (found) { if (found) { candidates.push(found); next(k); } }, function (e) { warn('picture', e); }); }
                return;
            }
            // the card's size first (the browser often has it already from the library), then the screen's
            var full = fetchImage(headImage(c), 'high'), small = fetchImage(image(c.id, c.type, c.tag, CARD), 'high'), done = false;
            full.alt = small.alt = '';
            var place = function (img) {
                if (!alive(s)) { return; }
                var colours = null;
                try { colours = window.__projectionColours ? window.__projectionColours(img) : null; } catch (e) { warn('colours', e); }
                hero.classList.remove('pj-ph-bare');
                hero.style.setProperty('--pj-ph-ratio', String(img.naturalWidth / img.naturalHeight));
                // a square or upright picture keeps its edges: the long fade would eat it
                hero.classList.toggle('pj-ph-narrow', img.naturalWidth / img.naturalHeight < 1.25);
                Object.keys(colours || {}).forEach(function (name) { if (name.indexOf('--pj-amb-') === 0) { box.style.setProperty(name, colours[name]); } });
                // the screen takes the picture's deep shade, under its own element only
                if (colours) { s.root.style.setProperty('--pj-bg', colours['--pj-bg']); s.root.style.setProperty('--pj-veil', colours['--pj-veil']); }
                box.replaceChildren(h('div', { 'class': 'pj-aff-fill pj-aff-fill-l' }), h('div', { 'class': 'pj-aff-fill pj-aff-fill-r' }), h('div', { 'class': 'pj-ph-art' }, img));
            };
            full.onload = function () { done = true; place(full); };
            small.onload = function () { if (!done) { place(small); } };
            full.onerror = function () { next(k + 1); };
        };
        next(0);
    }

    function favouriteButton(item) {
        var data = item.UserData || (item.UserData = {});
        var b = button('pj-ph-btn pj-ph-round', {}, icon('favorite'));
        var paint = function () {
            b.classList.toggle('pj-ph-liked', !!data.IsFavorite);
            b.setAttribute('aria-pressed', data.IsFavorite ? 'true' : 'false');
            b.setAttribute('aria-label', 'Favori');
            b.title = data.IsFavorite ? 'Retirer des favoris' : 'Ajouter aux favoris';
            b.firstChild.className = 'material-icons ' + (data.IsFavorite ? 'favorite' : 'favorite_border');
        };
        b.addEventListener('click', function () {
            var wanted = !data.IsFavorite;
            client().updateFavoriteStatus(client().getCurrentUserId(), item.Id, wanted).then(function () { data.IsFavorite = wanted; paint(); }, function (e) { warn('favourite', e); });
        });
        paint();
        return b;
    }

    // ---------- the viewer: the photo or the video, and beside it what is known about it ----------
    var viewer = null;
    function openViewer(s, index, opener, playing, own) {
        closeViewer();
        var v = viewer = { s: s, own: own || null, index: Math.max(0, index), opener: opener, hash: location.hash, details: new Map(), people: new Map(), timer: null, loading: null, ahead: new Map() };
        v.counter = h('span', { 'class': 'pj-ph-counter' });
        v.favourite = h('span', {});
        v.playButton = button('pj-ph-btn pj-ph-round', { 'aria-label': 'Diaporama', title: 'Diaporama (espace)' }, icon('play_arrow'));
        var close = button('pj-ph-btn', {}, [icon('close'), 'Fermer']);
        var previous = button('pj-ph-arrow pj-ph-prev', { 'aria-label': 'Précédent' }, icon('chevron_left'));
        var next = button('pj-ph-arrow pj-ph-next', { 'aria-label': 'Suivant' }, icon('chevron_right'));
        v.stage = h('div', { 'class': 'pj-ph-stage' });
        v.panel = h('aside', { 'class': 'pj-ph-panel', 'aria-live': 'polite' });
        v.box = h('div', { 'class': 'pj-ph-viewer', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Visionneuse' }, [
            h('div', { 'class': 'pj-ph-top' }, [v.counter, v.playButton, v.favourite, close]),
            h('div', { 'class': 'pj-ph-view' }, [previous, v.stage, next]), v.panel]);
        close.addEventListener('click', closeViewer);
        previous.addEventListener('click', function () { step(-1); });
        next.addEventListener('click', function () { step(1); });
        v.playButton.addEventListener('click', function () { slideshow(!v.timer); });
        var from = null;
        // a drag on a video's own controls is not a swipe
        v.stage.addEventListener('pointerdown', function (ev) { from = ev.target.tagName === 'VIDEO' ? null : ev.clientX; });
        v.stage.addEventListener('pointerup', function (ev) { if (from !== null && Math.abs(ev.clientX - from) > 50) { step(ev.clientX < from ? 1 : -1); } from = null; });
        document.body.appendChild(v.box);
        document.documentElement.classList.add('pj-ph-locked');
        show();
        slideshow(!!playing);
        close.focus();
    }
    function mediaOf(v) { return v.own || v.s.media; }
    function stopVideo(v) {
        var video = v.stage.querySelector('video');
        // the file stops being downloaded
        if (video) { video.pause(); video.removeAttribute('src'); video.load(); }
    }
    function closeViewer() {
        var v = viewer;
        if (!v) { return; }
        viewer = null;
        clearInterval(v.timer);
        stopVideo(v);
        stopImage(v.loading);
        v.ahead.forEach(stopImage);
        v.box.remove();
        document.documentElement.classList.remove('pj-ph-locked');
        if (v.opener && v.opener.isConnected) { v.opener.focus(); }
    }
    function slideshow(on) {
        var v = viewer;
        if (!v) { return; }
        clearInterval(v.timer);
        // a video is watched to its end: the slideshow moves on from its `ended` event instead
        v.timer = on ? setInterval(function () { if (!v.stage.querySelector('video')) { step(1); } }, SLIDE_MS) : null;
        v.playButton.firstChild.className = 'material-icons ' + (on ? 'pause' : 'play_arrow');
        v.playButton.setAttribute('aria-pressed', on ? 'true' : 'false');
        var video = v.stage.querySelector('video');
        if (video && on) { video.play().catch(function () { /* the person presses play */ }); }
    }
    function step(by) {
        var v = viewer;
        if (!v) { return; }
        var media = mediaOf(v), wanted = v.index + by;
        // past the last file read: read the next page first
        if (wanted >= media.length && !v.own && v.s.loaded < v.s.total) {
            v.s.more().then(function () { if (viewer === v && v.s.media.length > wanted) { v.index = wanted; show(); } });
            return;
        }
        v.index = (wanted + media.length) % media.length;
        show();
    }
    function show() {
        var v = viewer, media = mediaOf(v), it = media[v.index];
        if (!it) { closeViewer(); return; }
        var total = v.own ? media.length : Math.max(media.length, v.s.total);
        v.counter.textContent = (v.s.item ? v.s.item.Name + ' · ' : '') + nf(v.index + 1) + ' / ' + nf(total);
        stopVideo(v);
        if (it.Type === 'Video') { stopImage(v.loading); v.loading = null; v.stage.replaceChildren(player(v, it)); } else { photo(v, it); }
        v.favourite.replaceChildren(favouriteButton(it));
        describe(it);
        if (!v.people.has(it.Id)) {
            cast('Item/' + it.Id).then(function (members) {
                v.people.set(it.Id, once(members));
                if (viewer === v && mediaOf(v)[v.index] === it) { describe(it); }
            }, function (e) { warn('people', e); });
        }
        if (!v.details.has(it.Id)) {
            get('Items/' + it.Id).then(function (full) {
                v.details.set(it.Id, full);
                if (viewer === v && mediaOf(v)[v.index] === it) { describe(it); }
            }, function (e) { warn('photo', e); });
        }
    }
    function largeImage(v, it) {
        var shown = v.stage.getBoundingClientRect(), box = screenBox(shown.width, shown.height);
        return image(it.Id, 'Primary', it.ImageTags && it.ImageTags.Primary, box[0], box[1]);
    }
    // a picture already asked for, unless it was stopped or refused on the way (complete and empty): that one is asked again
    function asked(v, url) { var img = v.ahead.get(url); return img && !(img.complete && !img.naturalWidth) ? img : null; }
    // a photo: its thumbnail at once, the screen-sized picture in its place as soon as the server has made it
    function photo(v, it) {
        var url = largeImage(v, it), full = asked(v, url) || fetchImage(url, 'high');
        if (v.loading !== full) { stopImage(v.loading); }
        v.loading = full;
        v.ahead.set(url, full);
        full.alt = it.Name;
        full.draggable = false;
        var ready = function () {
            if (viewer !== v || v.loading !== full) { return; }
            v.stage.replaceChildren(full);
            lookAhead(v, url);
        };
        if (full.complete && full.naturalWidth) { ready(); return; }
        // never larger than the photo itself: a small one does not grow, then shrink when the large picture arrives
        var most = it.Width && it.Height ? 'max-width: min(100%, ' + it.Width + 'px); max-height: min(100%, ' + it.Height + 'px)' : null;
        v.stage.replaceChildren(h('img', { 'class': 'pj-ph-low', alt: it.Name, draggable: 'false', fetchpriority: 'high', style: most, src: thumbnail(it) }));
        full.onload = ready;
        full.onerror = function () { v.ahead.delete(url); };
    }
    // once the photo shown is there, its neighbours are asked for: the arrows and the slideshow then wait for nothing
    function lookAhead(v, url) {
        var media = mediaOf(v), keep = new Map([[url, v.loading]]);
        [1, -1].forEach(function (by) {
            var it = media[(v.index + by + media.length) % media.length];
            if (!it || it === media[v.index] || it.Type === 'Video') { return; }
            var near = largeImage(v, it);
            if (!keep.has(near)) { keep.set(near, asked(v, near) || fetchImage(near, 'low')); }
        });
        // the pictures further away are let go: a long slideshow does not hold them all
        v.ahead.forEach(function (img, key) { if (!keep.has(key)) { stopImage(img); } });
        v.ahead = keep;
    }
    // the file itself, as it is (no transcoding): a format the browser cannot read is left to jellyfin-web's player
    function player(v, it) {
        var c = client(), tag = it.ImageTags && it.ImageTags.Primary;
        var video = h('video', { controls: true, playsinline: true, preload: 'metadata', 'aria-label': it.Name, poster: tag ? largeImage(v, it) : null,
            src: c.getUrl('Videos/' + it.Id + '/stream', { static: true, mediaSourceId: it.Id, ApiKey: c.accessToken() }) });
        video.addEventListener('ended', function () { if (viewer === v && v.timer) { step(1); } });
        video.addEventListener('error', function () {
            if (viewer !== v || !video.isConnected) { return; }
            v.stage.replaceChildren(h('div', { 'class': 'pj-ph-unplayable' }, [h('p', { text: 'Ce navigateur ne lit pas ce format ici.' }),
                h('a', { 'class': 'pj-ph-btn pj-ph-main', href: detailsHref(it.Id) }, [icon('play_arrow'), 'Lire dans Jellyfin'])]));
        });
        if (v.timer) { video.autoplay = true; }
        return video;
    }
    function describe(it) {
        var v = viewer, full = v.details.get(it.Id) || it, rows = [];
        var row = function (label, value) { if (value) { rows.push(h('dt', { text: label }), h('dd', {}, value)); } };
        var chips = function (words) { return words && words.length ? h('div', { 'class': 'pj-ph-words' }, words.map(function (w) { return h('span', { 'class': 'pj-ph-chip', text: w }); })) : null; };
        var members = v.people.get(it.Id);
        row('Personnes', members && members.length ? h('div', { 'class': 'pj-ph-people' }, members.map(function (p) {
            return personChip(p, { href: v.s.homeId ? personHref(v.s.homeId, p.Name) : '#/search?query=' + encodeURIComponent(p.Name) });
        })) : null);
        row('Date', day(full.PremiereDate));
        if (full.Type === 'Video') { row('Durée', length(full.RunTimeTicks)); }
        row('Étiquettes', chips(full.Tags));
        row('Genres', (full.Genres || []).join(', '));
        row('Studio', (full.Studios || []).map(function (x) { return x.Name; }).join(', '));
        row('Note', full.CommunityRating ? '★ ' + nf(Math.round(full.CommunityRating * 10) / 10) : '');
        row('Appareil', [full.CameraMake, full.CameraModel].filter(Boolean).join(' '));
        // the file's own name, never the folders of the server
        row('Fichier', [full.Path ? full.Path.split(/[\\/]/).pop() : '', full.Width && full.Height ? full.Width + ' × ' + full.Height : ''].filter(Boolean).join(' · '));
        v.panel.replaceChildren(h('h2', { text: full.Name }), full.Overview ? h('p', { text: full.Overview }) : '', h('dl', {}, rows),
            h('a', { 'class': 'pj-ph-btn', href: detailsHref(full.Id) }, [icon('info'), 'Fiche Jellyfin']));
    }
    // the viewer takes the keyboard (window capture, before jellyfin-web's own shortcuts)
    window.addEventListener('keydown', function (ev) {
        var v = viewer;
        if (!v || ev.ctrlKey || ev.metaKey || ev.altKey) { return; }
        var done = true;
        if (ev.key === 'Escape') { closeViewer(); }
        else if (ev.key === 'ArrowRight') { step(1); }
        else if (ev.key === 'ArrowLeft') { step(-1); }
        else if (ev.key === ' ' && !/BUTTON|^A$|VIDEO/.test(ev.target.tagName)) {
            // space plays or pauses the video shown, else the slideshow
            var video = v.stage.querySelector('video');
            if (!video) { slideshow(!v.timer); } else if (video.paused) { video.play().catch(function () { /* unreadable */ }); } else { video.pause(); }
        }
        else if (ev.key === 'Tab') {
            var all = Array.prototype.slice.call(v.box.querySelectorAll('button, a[href], video'));
            var at = all.indexOf(document.activeElement);
            all[(at + (ev.shiftKey ? -1 : 1) + all.length) % all.length].focus();
        } else { done = false; }
        if (done) { ev.preventDefault(); ev.stopPropagation(); }
        else if (ev.key !== 'Enter' && ev.key !== ' ') { ev.stopPropagation(); }
    }, true);

    // ---------- jellyfin-web's person page: their albums, photos and videos ----------
    function personPage() {
        var page = currentPage(), id = /^#\/details\?/.test(location.hash) ? params().get('id') : null;
        if (!page || !id || !page.classList.contains('itemDetailPage')) { return; }
        if (page.__pjPhPerson === id) { return; }
        page.__pjPhPerson = id;
        page.querySelectorAll('.pj-ph-appears').forEach(function (n) { n.remove(); });
        get('Items/' + id).then(function (person) {
            if (person.Type !== 'Person' || page.__pjPhPerson !== id) { return null; }
            return cast('Items', { person: person.Name }).then(byIds).then(function (items) {
                if (page.__pjPhPerson !== id || !items.length) { return; }
                var albums = items.filter(isAlbum), media = items.filter(function (it) { return !isAlbum(it); });
                var s = { media: media, total: media.length, loaded: media.length, more: function () { return Promise.resolve(); } };
                var box = h('div', { 'class': 'pj-ph pj-ph-appears' });
                if (albums.length) { box.appendChild(section('Albums photo', h('div', { 'class': 'pj-ph-albums' }, albums.map(albumCard)))); }
                if (media.length) {
                    var videos = media.filter(function (it) { return it.Type === 'Video'; }).length;
                    box.appendChild(section(mediaTitle(media.length, videos) + ' d\u2019albums', h('div', { 'class': 'pj-ph-wall' }, media.map(function (it) { return mediaTile(s, it); }))));
                }
                var holder = page.querySelector('.detailPageContent') || page.querySelector('.detailPagePrimaryContent') || page;
                holder.appendChild(box);
            });
        }).catch(function (e) { warn('person', e); });
    }

    // ---------- run ----------
    var scheduled = null;
    function run() {
        scheduled = null;
        try { screens(); personPage(); } finally { followBar(); }
    }
    function screens() {
        var page = currentPage();
        if (viewer && viewer.hash !== location.hash) { closeViewer(); }
        if (!page || !client() || !themed()) { return; }
        var r = route(), host = null;
        if (r && r.kind === 'library' && page.id === 'homevideos') { host = page; }
        // jellyfin-web's list page, once it shows this address (the page being left stays shown for a moment)
        if (r && r.kind === 'album' && page.querySelector('.alphaPicker') && (!page.__pjPhView || page.__pjPhView === location.hash)) { host = page; }
        // a page jellyfin-web uses again for a view we leave alone: give it back
        if (page.__pjPh && page.__pjPh.hash !== location.hash) { drop(page.__pjPh); }
        if (host && !ignored.has(location.hash) && (!host.__pjPh || !host.contains(host.__pjPh.root))) { build(host, r); }
    }
    // while the library's screen shows, the app bar's second row (stock sort, filters and page arrows) acts on nothing
    // visible: it goes, and the page moves up with it
    function followBar() {
        var page = currentPage();
        document.documentElement.classList.toggle('pj-ph-libon', !!(page && page.__pjPh && page.__pjPh.route.kind === 'library' && page.__pjPh.hash === location.hash));
    }
    function schedule() { if (!scheduled) { scheduled = setTimeout(run, 120); } }
    window.addEventListener('hashchange', function () { closeViewer(); schedule(); });
    document.addEventListener('viewshow', function (ev) { if (ev.target && ev.target.classList) { ev.target.__pjPhView = location.hash; } schedule(); }, true);
    new MutationObserver(function (records) {
        // our own additions do not count
        for (var i = 0; i < records.length; i++) {
            var t = records[i].target;
            if (!(t.closest && t.closest('.pj-ph, .pj-ph-viewer'))) { schedule(); return; }
        }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    schedule();
})();
