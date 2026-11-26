/*
 * Episode photo gallery for jellyfin-web 10.11.
 *
 * Served by the Custom Metadata plugin (CustomMetadata/Photos/gallery.js) and added to jellyfin-web's page when the
 * "Galerie photos des épisodes" setting is on. It works with or without the Projection theme (it takes the theme's
 * colours when they are there). On an episode that has photos it adds a "Photos · N" button; the button opens a wall
 * of photos at their own ratios, and a photo opens full size in the light of its own colours.
 *
 * Every request carries the user's token in a header and images are shown from blob: URLs, so the token never appears
 * in a URL. Only elements prefixed "eg-" are added. Any error simply means "no button". Labels are French.
 */
(function () {
    'use strict';
    if (window.__episodePhotos) { return; }
    window.__episodePhotos = '1';

    var LIST_TTL = 30000;
    var PARALLEL = 4;
    var GAP = 12;

    var warn = function (where, error) { try { console.warn('[photos] ' + where, error); } catch (e) { /* no console */ } };
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) { e.className = cls; } if (text != null) { e.textContent = text; } return e; }
    function client() { var c = window.ApiClient; return c && c.getCurrentUserId && c.getCurrentUserId() && c.accessToken && c.accessToken() ? c : null; }
    function plural(n) { return n + (n > 1 ? ' photos' : ' photo'); }
    function currentId() { var m = /[?&]id=([0-9a-f]{32})/i.exec(location.hash); return m ? m[1].toLowerCase() : null; }

    // ---------------------------------------------------------------- data
    function request(path) {
        var c = client();
        if (!c) { return Promise.reject(new Error('not signed in')); }
        var headers = {};
        try { if (c.setRequestHeaders) { c.setRequestHeaders(headers); } } catch (e) { /* header below */ }
        if (!headers.Authorization) { headers.Authorization = 'MediaBrowser Token="' + c.accessToken() + '"'; }
        return fetch(c.getUrl(path), { headers: headers }).then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r; });
    }
    var lists = new Map();
    function photoList(id) {
        var cached = lists.get(id);
        if (cached && Date.now() - cached.time < LIST_TTL) { return cached.promise; }
        var promise = request('CustomMetadata/Photos/' + id).then(function (r) { return r.json(); }).then(function (res) { return res.Photos || []; });
        lists.set(id, { time: Date.now(), promise: promise });
        promise.catch(function () { lists.delete(id); });
        return promise;
    }

    // ---------------------------------------------------------------- the button on the episode page
    function check() {
        var page = document.querySelector('.itemDetailPage:not(.hide)'), id = currentId();
        if (!page || !id || !client()) { return; }
        var buttons = page.querySelector('.mainDetailButtons');
        if (!buttons) { return; }
        var old = buttons.querySelector('.eg-btn');
        if (old && old.getAttribute('data-eg-item') === id) { return; }
        if (old) { old.remove(); }
        photoList(id).then(function (photos) {
            if (!photos.length || currentId() !== id || !buttons.isConnected || buttons.querySelector('.eg-btn')) { return; }
            style();
            var button = el('button', 'eg-btn button-flat detailButton emby-button');
            button.type = 'button';
            button.title = plural(photos.length) + ' de l\'épisode';
            button.setAttribute('aria-label', button.title);
            button.setAttribute('data-eg-item', id);
            button.appendChild(el('span', 'material-icons photo_library')).setAttribute('aria-hidden', 'true');
            button.appendChild(el('span', 'eg-btn-word', 'Photos'));
            button.appendChild(el('span', 'eg-btn-count', String(photos.length)));
            button.addEventListener('click', function () { openWall(id, button); });
            // after "Lire" and the theme's "Aperçu" (which inserts itself right after the play button)
            var after = buttons.querySelector('.pj-previewbtn') || buttons.querySelector('.btnPlay:not(.hide), .btnResume:not(.hide)');
            buttons.insertBefore(button, after ? after.nextSibling : buttons.firstChild);
        }).catch(function (e) { warn('list', e); });
    }

    // ---------------------------------------------------------------- the wall
    var open = null;   // the open gallery

    function close() {
        var g = open;
        if (!g) { return; }
        open = null;
        window.removeEventListener('keydown', g.onKey, true);
        window.removeEventListener('resize', g.onResize);
        if (g.observer) { g.observer.disconnect(); }
        g.root.remove();
        document.documentElement.classList.remove('eg-open');
        g.blobs.forEach(function (u) { URL.revokeObjectURL(u); });
        if (g.opener && g.opener.isConnected) { g.opener.focus(); }
    }

    function iconButton(cls, icon, label, action) {
        var b = el('button', 'eg-ib ' + cls);
        b.type = 'button'; b.title = label; b.setAttribute('aria-label', label);
        b.appendChild(el('span', 'material-icons ' + icon)).setAttribute('aria-hidden', 'true');
        b.addEventListener('click', action);
        return b;
    }

    function openWall(id, opener) {
        // always the current list: photos may have been added since the button was drawn
        photoList(id).then(function (photos) {
            if (!photos.length || currentId() !== id) { return; }
            close();
            build(id, photos, opener);
        }).catch(function (e) { warn('open', e); });
    }

    function build(id, photos, opener) {
        var g = open = { photos: photos, opener: opener, blobs: [], queue: [], active: 0, index: -1, full: [], thumbs: [], ratios: [], line: plural(photos.length) };

        var root = g.root = el('div', 'eg-root');
        root.setAttribute('role', 'dialog');
        root.setAttribute('aria-modal', 'true');
        root.setAttribute('aria-label', 'Photos de l\'épisode');

        var wall = g.wall = el('div', 'eg-wall');
        var head = el('header', 'eg-head');
        var title = el('h2', 'eg-title', 'Photos');
        var sub = el('span', 'eg-sub', g.line);
        head.appendChild(title); head.appendChild(sub);
        head.appendChild(iconButton('eg-close', 'close', 'Fermer', close));
        var grid = g.grid = el('div', 'eg-grid');
        wall.appendChild(head); wall.appendChild(grid);
        root.appendChild(wall);

        g.observer = 'IntersectionObserver' in window ? new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) { g.observer.unobserve(entry.target); g.queue.push(entry.target); }
            });
            pump(g);
        }, { root: wall, rootMargin: '400px' }) : null;

        g.tiles = photos.map(function (p, i) {
            g.ratios[i] = p.Width && p.Height ? p.Width / p.Height : 1.5;
            var tile = el('button', 'eg-tile');
            tile.type = 'button';
            tile.title = p.Name || '';
            tile.setAttribute('aria-label', 'Ouvrir la photo ' + (i + 1) + ' sur ' + photos.length);
            var img = el('img'); img.alt = ''; img.decoding = 'async'; img.referrerPolicy = 'no-referrer';
            tile.appendChild(img);
            tile.__eg = i;
            tile.addEventListener('click', function () { show(g, i); });
            grid.appendChild(tile);
            return tile;
        });

        buildViewer(g);
        document.body.appendChild(root);
        document.documentElement.classList.add('eg-open');
        layout(g);
        g.tiles.forEach(function (t) { if (g.observer) { g.observer.observe(t); } else { g.queue.push(t); } });
        pump(g);

        g.onResize = function () { clearTimeout(g.resizing); g.resizing = setTimeout(function () { if (open === g) { layout(g); } }, 120); };
        g.onKey = function (e) { key(g, e); };
        window.addEventListener('resize', g.onResize);
        window.addEventListener('keydown', g.onKey, true);   // capture: jellyfin-web's own shortcuts stay quiet
        wall.querySelector('.eg-close').focus();

        // the episode's name above the wall
        var c = client();
        c.getItem(c.getCurrentUserId(), id).then(function (item) {
            if (open !== g) { return; }
            var code = [item.ParentIndexNumber != null && item.ParentIndexNumber >= 0 ? 'S' + item.ParentIndexNumber : '', item.IndexNumber != null ? 'É' + item.IndexNumber + (item.IndexNumberEnd ? '-' + item.IndexNumberEnd : '') : ''].filter(Boolean).join(' · ');
            title.textContent = item.Name || 'Photos';
            sub.textContent = [item.SeriesName, code, plural(photos.length)].filter(Boolean).join(' · ');
            g.line = [item.SeriesName, code].filter(Boolean).join(' · ') || item.Name;
        }).catch(function (e) { warn('item', e); });
    }

    // rows of equal height filling the width, each photo at its own ratio (a very wide or tall one is cropped a little)
    function layout(g) {
        // rounded DOWN: clientWidth rounds up a fractional width, and the last photo of each row would wrap
        var width = Math.floor(g.grid.getBoundingClientRect().width);
        if (!width) { return; }
        var target = width < 600 ? 130 : (width < 1500 ? 240 : 290);
        var row = [], sum = 0;
        var flush = function (last) {
            if (!row.length) { return; }
            var free = width - GAP * (row.length - 1);
            var height = last ? Math.min(target, free / sum) : free / sum;
            var used = 0;
            row.forEach(function (it, k) {
                var w = Math.floor(height * it.ratio);
                if (k === row.length - 1 && (!last || height < target)) { w = free - used; }
                used += w;
                g.tiles[it.i].style.width = w + 'px';
                g.tiles[it.i].style.height = Math.round(height) + 'px';
            });
            row = []; sum = 0;
        };
        var full = function () { return sum * target + GAP * (row.length - 1) >= width; };
        g.photos.forEach(function (p, i) {
            var ratio = Math.min(3.5, Math.max(0.4, g.ratios[i]));
            row.push({ i: i, ratio: ratio }); sum += ratio;
            if (!full()) { return; }
            // the photo that fills the row may squash it: without it, the row can be closer to the wanted height
            var withIt = (width - GAP * (row.length - 1)) / sum;
            var withoutIt = row.length > 1 ? (width - GAP * (row.length - 2)) / (sum - ratio) : Infinity;
            if (withoutIt - target < target - withIt) {
                var last = row.pop(); sum -= ratio;
                flush(false);
                row.push(last); sum = ratio;
                if (full()) { flush(false); }
            } else {
                flush(false);
            }
        });
        flush(true);
        g.target = target;
    }

    function blob(g, path) {
        return request(path).then(function (r) { return r.blob(); }).then(function (b) {
            var url = URL.createObjectURL(b);
            if (open !== g) { URL.revokeObjectURL(url); throw new Error('closed'); }
            g.blobs.push(url);
            return url;
        });
    }

    // thumbnails of the tiles in view, a few at a time
    function pump(g) {
        while (open === g && g.active < PARALLEL && g.queue.length) { loadThumb(g, g.queue.shift()); }
    }
    function loadThumb(g, tile) {
        var i = tile.__eg, p = g.photos[i], img = tile.firstChild;
        g.active++;
        var width = Math.min(1280, Math.round(g.target * Math.max(1, g.ratios[i]) * Math.min(2, window.devicePixelRatio || 1)));
        var source = p.ExternalUrl ? Promise.resolve(p.ExternalUrl) : blob(g, p.ThumbnailUrl + '?width=' + width);
        source.then(function (url) {
            return new Promise(function (done) {
                img.onload = function () {
                    img.classList.add('eg-loaded');
                    g.thumbs[i] = url;
                    // a size the server did not know (web address, unreadable header)
                    if (!(p.Width && p.Height) && img.naturalHeight) { g.ratios[i] = img.naturalWidth / img.naturalHeight; g.onResize(); }
                    done();
                };
                img.onerror = function () { tile.classList.add('eg-broken'); done(); };
                img.src = url;
            });
        }).catch(function (e) { if (open === g) { tile.classList.add('eg-broken'); warn('thumbnail ' + p.Name, e); } })
            .then(function () { g.active--; pump(g); });
    }

    // ---------------------------------------------------------------- the viewer
    function buildViewer(g) {
        var v = g.viewer = el('div', 'eg-viewer');
        v.hidden = true;
        g.glow = v.appendChild(el('div', 'eg-glow'));
        g.counter = v.appendChild(el('div', 'eg-counter'));
        g.counter.setAttribute('role', 'status');
        v.appendChild(iconButton('eg-back', 'close', 'Revenir au mur', function () { back(g); }));
        var stage = v.appendChild(el('div', 'eg-stage'));
        g.big = stage.appendChild(el('img', 'eg-big'));
        g.big.draggable = false; g.big.referrerPolicy = 'no-referrer';
        g.big.addEventListener('load', function () { light(g); });
        g.failed = stage.appendChild(el('p', 'eg-failed', 'Impossible d\'afficher cette photo.'));
        g.failed.hidden = true;
        if (g.photos.length > 1) {
            v.appendChild(iconButton('eg-prev', 'chevron_left', 'Photo précédente', function () { show(g, g.index - 1); }));
            v.appendChild(iconButton('eg-next', 'chevron_right', 'Photo suivante', function () { show(g, g.index + 1); }));
        }
        var swipe = null;
        v.addEventListener('pointerdown', function (e) { if (e.pointerType !== 'mouse' && e.isPrimary) { swipe = { x: e.clientX, y: e.clientY }; } });
        v.addEventListener('pointercancel', function () { swipe = null; });
        v.addEventListener('pointerup', function (e) {
            if (!swipe || !e.isPrimary) { return; }
            var dx = e.clientX - swipe.x, dy = e.clientY - swipe.y;
            swipe = null;
            if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) { show(g, g.index + (dx < 0 ? 1 : -1)); }
            else if (dy > 80 && Math.abs(dy) > Math.abs(dx) * 1.5) { back(g); }
        });
        g.root.appendChild(v);
    }

    function full(g, i) {
        if (!g.full[i]) {
            var p = g.photos[i];
            g.full[i] = p.ExternalUrl ? Promise.resolve(p.ExternalUrl) : blob(g, p.ImageUrl);
            g.full[i].catch(function () { g.full[i] = null; });
        }
        return g.full[i];
    }

    function show(g, index) {
        var n = g.photos.length;
        index = ((index % n) + n) % n;
        var entering = g.viewer.hidden;
        g.index = index;
        g.viewer.hidden = false;
        g.wall.setAttribute('aria-hidden', 'true');
        g.counter.textContent = [g.line, (index + 1) + ' / ' + n].filter(Boolean).join(' — ');
        g.failed.hidden = true;
        g.big.alt = g.photos[index].Name || 'Photo ' + (index + 1);
        // the thumbnail while the original comes
        if (g.thumbs[index]) { g.big.src = g.thumbs[index]; } else { g.big.removeAttribute('src'); }
        full(g, index).then(function (url) { if (open === g && g.index === index) { g.big.src = url; } })
            .catch(function (e) { if (open === g && g.index === index) { g.failed.hidden = false; warn('photo', e); } });
        if (n > 1) { full(g, (index + 1) % n).catch(function () {}); full(g, (index + n - 1) % n).catch(function () {}); }
        if (entering) { (g.viewer.querySelector('.eg-next') || g.viewer.querySelector('.eg-back')).focus(); }
    }

    function back(g) {
        g.viewer.hidden = true;
        g.wall.removeAttribute('aria-hidden');
        var tile = g.tiles[g.index];
        g.index = -1;
        if (tile) { tile.focus(); tile.scrollIntoView({ block: 'nearest' }); }
    }

    // the light around the photo takes the colours of its corners (a web image of another site cannot be read)
    function light(g) {
        try {
            var canvas = el('canvas'); canvas.width = canvas.height = 4;
            var ctx = canvas.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(g.big, 0, 0, 4, 4);
            [[0, 0], [0, 3], [3, 0], [3, 3]].forEach(function (at, k) {
                var d = ctx.getImageData(at[0], at[1], 1, 1).data;
                g.glow.style.setProperty('--eg-glow-' + (k + 1), 'rgb(' + d[0] + ',' + d[1] + ',' + d[2] + ')');
            });
        } catch (e) {
            [1, 2, 3, 4].forEach(function (k) { g.glow.style.removeProperty('--eg-glow-' + k); });
        }
    }

    function key(g, e) {
        var viewing = !g.viewer.hidden;
        if (e.key === 'Escape' || (e.key === 'Backspace' && viewing)) {
            if (viewing) { back(g); } else { close(); }
        } else if (viewing && e.key === 'ArrowLeft') {
            show(g, g.index - 1);
        } else if (viewing && e.key === 'ArrowRight') {
            show(g, g.index + 1);
        } else if (e.key === 'Tab') {
            // the focus stays inside the gallery
            var list = Array.prototype.slice.call((viewing ? g.viewer : g.wall).querySelectorAll('button'));
            var at = list.indexOf(document.activeElement);
            var next = list[(at + (e.shiftKey ? -1 : 1) + list.length) % list.length];
            if (next) { next.focus(); }
        } else {
            e.stopPropagation();
            return;
        }
        e.preventDefault();
        e.stopPropagation();
    }

    // ---------------------------------------------------------------- style
    function style() {
        if (document.getElementById('eg-style')) { return; }
        var accent = 'var(--pj-amber, var(--theme-primary-color, #00a4dc))';
        var text = 'var(--pj-text, #fff)';
        var dim = 'var(--pj-dim, rgba(255,255,255,.65))';
        var soft = 'rgba(235,230,217,.12)';
        var css = [
            '@property --eg-glow-1{syntax:"<color>";inherits:false;initial-value:transparent}',
            '@property --eg-glow-2{syntax:"<color>";inherits:false;initial-value:transparent}',
            '@property --eg-glow-3{syntax:"<color>";inherits:false;initial-value:transparent}',
            '@property --eg-glow-4{syntax:"<color>";inherits:false;initial-value:transparent}',
            // jellyfin-web stacks a detail button's content: one line here
            '.mainDetailButtons .eg-btn{display:inline-flex;flex-direction:row!important;align-items:center;justify-content:center;gap:.45em;flex:0 0 auto;width:auto;height:2.75rem;min-height:2.75rem;margin:0 .35em;padding:0 1.1em;border:1px solid var(--pj-line-2,rgba(255,255,255,.28));border-radius:2em;font-weight:600;white-space:nowrap}',
            '.mainDetailButtons .eg-btn .material-icons{font-size:1.35em}',
            '.eg-btn-count{color:' + dim + ';font-variant-numeric:tabular-nums}',
            '@media (max-width:600px){.mainDetailButtons .eg-btn{padding:0 .8em;margin:0 .15em}.eg-btn-word{display:none}}',
            'html.eg-open{overflow:hidden}',
            '.eg-root{position:fixed;inset:0;z-index:10000;color:' + text + '}',
            '.eg-wall,.eg-viewer{position:absolute;inset:0}',
            // the scrollbar is always there: appearing after the rows are sized, it would break them
            '.eg-wall{overflow-y:scroll;overscroll-behavior:contain;background:var(--pj-bg,#101010);animation:eg-in .2s ease-out}',
            '@keyframes eg-in{from{opacity:0}}',
            '.eg-head{position:sticky;top:0;z-index:1;display:flex;flex-wrap:wrap;align-items:baseline;column-gap:1rem;padding:1.4rem 5rem 1rem var(--pj-gutter,3.3vw);background:var(--pj-bg,#101010)}',
            '.eg-title{margin:0;font-family:var(--pj-serif,inherit);font-size:1.9rem;line-height:1.2;overflow-wrap:anywhere}',
            '.eg-sub{color:' + dim + '}',
            '.eg-grid{display:flex;flex-wrap:wrap;align-content:flex-start;gap:' + GAP + 'px;margin:0 var(--pj-gutter,3.3vw);padding-bottom:3rem}',
            '.eg-tile{position:relative;flex:0 0 auto;margin:0;padding:0;border:0;border-radius:var(--pj-r,4px);overflow:hidden;background:' + soft + ';cursor:pointer}',
            '.eg-tile img{display:block;width:100%;height:100%;object-fit:cover;opacity:0;transition:opacity .25s,transform .25s}',
            '.eg-tile img.eg-loaded{opacity:1}',
            '.eg-tile:hover img{transform:scale(1.03)}',
            '.eg-tile::after{content:"";position:absolute;inset:0;border-radius:inherit;pointer-events:none}',
            '.eg-tile:hover::after{box-shadow:inset 0 0 0 2px ' + soft + '}',
            '.eg-tile:focus-visible{outline:none}',
            '.eg-tile:focus-visible::after{box-shadow:inset 0 0 0 3px ' + accent + '}',
            '.eg-broken::before{content:"Image illisible";position:absolute;inset:0;display:grid;place-items:center;padding:.5em;font-size:.8rem;color:' + dim + '}',
            '.eg-ib{display:grid;place-items:center;width:2.75rem;height:2.75rem;margin:0;padding:0;border:0;border-radius:50%;background:' + soft + ';color:inherit;font-size:1.1rem;cursor:pointer}',
            '.eg-ib .material-icons{font-size:1.5rem}',
            '.eg-ib:hover{background:rgba(235,230,217,.24)}',
            '.eg-ib:focus-visible{outline:2px solid ' + accent + ';outline-offset:2px}',
            '.eg-close,.eg-back{position:absolute;top:1.1rem;right:1.25rem;z-index:2}',
            // above the wall's sticky header
            '.eg-viewer{z-index:3;background:#070c0d;touch-action:pan-y pinch-zoom;user-select:none;animation:eg-in .2s ease-out}',
            '.eg-viewer[hidden],.eg-failed[hidden]{display:none}',
            '.eg-glow{position:absolute;inset:0;pointer-events:none;opacity:.45;background:radial-gradient(ellipse 48% 55% at 18% 24%,var(--eg-glow-1),transparent 72%),radial-gradient(ellipse 48% 55% at 18% 76%,var(--eg-glow-2),transparent 72%),radial-gradient(ellipse 48% 55% at 82% 24%,var(--eg-glow-3),transparent 72%),radial-gradient(ellipse 48% 55% at 82% 76%,var(--eg-glow-4),transparent 72%);transition:--eg-glow-1 .8s,--eg-glow-2 .8s,--eg-glow-3 .8s,--eg-glow-4 .8s}',
            '.eg-counter{position:absolute;top:1.1rem;left:var(--pj-gutter,3.3vw);right:5rem;line-height:2.75rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:var(--pj-text-2,rgba(255,255,255,.8))}',
            '.eg-stage{position:absolute;inset:5rem 5.5rem 2rem;display:flex;align-items:center;justify-content:center}',
            // as large as the stage allows, even for a photo smaller than it, at its own ratio
            '.eg-big{width:100%;height:100%;object-fit:contain;filter:drop-shadow(0 24px 50px rgba(0,0,0,.6))}',
            '.eg-big:not([src]){visibility:hidden}',
            '.eg-failed{position:absolute;margin:0;color:' + dim + '}',
            '.eg-prev,.eg-next{position:absolute;top:50%;margin-top:-1.375rem}',
            '.eg-prev{left:1.25rem}.eg-next{right:1.25rem}',
            '@media (max-width:600px){.eg-title{font-size:1.35rem}.eg-stage{inset:4.5rem .5rem 5rem}.eg-prev,.eg-next{top:auto;bottom:1.25rem}.eg-prev{left:30%}.eg-next{right:30%}}',
            '@media (prefers-reduced-motion:reduce){.eg-wall,.eg-viewer{animation:none}.eg-tile img,.eg-glow{transition:none}.eg-tile:hover img{transform:none}}'
        ].join('\n');
        var node = el('style', null, css);
        node.id = 'eg-style';
        document.head.appendChild(node);
    }

    // ---------------------------------------------------------------- jellyfin-web is a single-page app
    var pending = null;
    function schedule() {
        if (pending) { return; }
        pending = setTimeout(function () { pending = null; try { check(); } catch (e) { warn('check', e); } }, 250);
    }
    function leave() { close(); schedule(); }
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', leave);
    document.addEventListener('viewshow', schedule);
    schedule();
})();
