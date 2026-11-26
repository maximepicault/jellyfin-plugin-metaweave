// Audit of the Projection theme (theme/projection.css) in the real jellyfin-web: every page, several layouts and widths,
// each screen of each page checked by machine, and a screenshot of each screen to read.
// Usage: node theme-audit.mjs <base_url> <out_dir> [--only=series,episode] [--configs=exp-1440,mobile-390] [--no-css]
// Run theme-seed.mjs first (long texts, tags, a movie library, items without artwork).
//
// Checks on every screen (the page is scrolled one screen at a time, to its end):
//   veiled     light text darkened by a layer drawn over it (a pseudo-element veil or fade): the text is compared in two
//              screenshots, with and without the pseudo-elements' backgrounds. The bug class of the tags under the fade.
//   contrast   axe-core's color-contrast rule (dim text on ink)
//   lowcontrast  the same measured on the screenshot (text pixels against their ground), on every screen: catches light
//              text over a bright picture, which axe-core skips
//   collide    two lines of text in one place, line box by line box (rules borrowed from taste-skill's consistency.mjs)
//   overflow   the page scrolls sideways, and what runs past the right edge
//   cut        text clipped by its box without an ellipsis or a line clamp
//   header     text under the fixed header with the page at its top
//   tap-size   (phones) a target under 32x24 px less than 8 px from another one (taste-skill's FAIL rule)
//   focus      (desktop, once per page) Tab moves focus to something that shows no ring
//   errors     exceptions thrown by the page
// The report (report.json, report.md) lists each finding with the element, its text and the screenshot it is on.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const AxeBuilder = require('@axe-core/playwright').default;

const args = process.argv.slice(2);
const opt = n => (args.find(a => a.startsWith(`--${n}=`)) || '').slice(n.length + 3);
const [J, outDir] = args.filter(a => !a.startsWith('--'));
if (!J || !outDir) { console.error('usage: node theme-audit.mjs <base_url> <out_dir> [--only=a,b] [--configs=exp-1440,mobile-390] [--no-css]'); process.exit(2); }
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const css = fs.readFileSync(path.join(repo, 'theme/projection.css'), 'utf8');
const UA_MOBILE = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36';
const CONFIGS = {
  'exp-1440': { layout: 'experimental', w: 1440, h: 900 },
  'exp-1920': { layout: 'experimental', w: 1920, h: 1080 },
  'exp-1024': { layout: 'experimental', w: 1024, h: 768 },
  'classic-1440': { layout: 'desktop', w: 1440, h: 900 },
  'mobile-390': { layout: 'experimental', w: 390, h: 844, mobile: true },
  'mobile-320': { layout: 'experimental', w: 320, h: 640, mobile: true },
};
const configs = (opt('configs') || 'exp-1440,exp-1920,exp-1024,classic-1440,mobile-390,mobile-320').split(',');
const only = (opt('only') || '').split(',').filter(Boolean);
const wait = ms => new Promise(r => setTimeout(r, ms));
fs.mkdirSync(outDir, { recursive: true });

// ---- server side: token, item ids, branding
// several audits may start together (one per configuration): a busy server answers some logins empty, so retry
let auth;
for (let i = 0; i < 10 && !auth; i++) {
  try { auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="t", Device="cli", DeviceId="theme-audit-${process.pid}", Version="1"` }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json(); } catch { await wait(1500 + Math.random() * 2000); }
}
if (!auth) { console.error('login failed'); process.exit(1); }
const U = auth.User.Id;
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const get = url => fetch(J + url, { headers: H }).then(r => r.json());
const items = q => get(`/Items?userId=${U}&Recursive=true&${new URLSearchParams(q)}`).then(r => r.Items);
const series = await items({ IncludeItemTypes: 'Series', Fields: 'BackdropImageTags' });
const adv = series.find(s => s.Name === 'Les Aventuriers');
const noBackdrop = series.find(s => s.Name === 'Cuisine du monde');
const longTitle = series.find(s => s.Name.startsWith('Une série au titre'));
const manySeasons = series.find(s => s.Name.startsWith('One Piece'));   // 32 arcs, 26 watched
const seasons = await items({ ParentId: adv.Id, IncludeItemTypes: 'Season' });
const season = seasons.find(s => s.Name === 'Saison 2023') || seasons[0];
const eps = await items({ ParentId: season.Id, IncludeItemTypes: 'Episode' });
const missingSeason = await items({ IncludeItemTypes: 'Episode', IsMissing: true, Fields: 'SeasonId' }).then(r => { const n = {}; r.forEach(i => (n[i.SeasonId] = (n[i.SeasonId] || 0) + 1)); return Object.keys(n).sort((a, b) => n[b] - n[a])[0]; });
const movies = await items({ IncludeItemTypes: 'Movie' });
// the two people theme-seed.mjs sets up: a full sheet and a bare name
const personNamed = n => get(`/Persons/${encodeURIComponent(n)}?userId=${U}`).catch(() => null);
const fullPerson = await personNamed('Maëlle Roussel'), barePerson = await personNamed('Noa Perrin');
const views = await get(`/UserViews?userId=${U}`).then(r => r.Items);
const genres = await get(`/Genres?userId=${U}&limit=3`).then(r => r.Items || []);
const tvLib = views.find(v => v.CollectionType === 'tvshows'), movieLib = views.find(v => v.CollectionType === 'movies');
const PAGES = [
  ['home', '#/home'],
  ['library-tv', tvLib && `#/tv?topParentId=${tvLib.Id}`],
  ['library-movies', movieLib && `#/movies?topParentId=${movieLib.Id}`],
  ['favorites', '#/home?tab=1'],
  ['series', `#/details?id=${adv.Id}`],
  ['series-nobackdrop', noBackdrop && `#/details?id=${noBackdrop.Id}`],
  ['series-longtitle', longTitle && `#/details?id=${longTitle.Id}`],
  ['series-many', manySeasons && `#/details?id=${manySeasons.Id}`],
  ['season', `#/details?id=${season.Id}`],
  ['season-missing', missingSeason && `#/details?id=${missingSeason}`],
  ['episode-tags', eps[0] && `#/details?id=${eps[0].Id}`],
  ['episode-longtext', eps[1] && `#/details?id=${eps[1].Id}`],
  ['movie', movies.find(m => m.Name.startsWith('Le Grand')) && `#/details?id=${movies.find(m => m.Name.startsWith('Le Grand')).Id}`],
  ['movie-longtitle', movies.find(m => m.Name.startsWith('Les Chroniques')) && `#/details?id=${movies.find(m => m.Name.startsWith('Les Chroniques')).Id}`],
  ['person', fullPerson?.Id && `#/details?id=${fullPerson.Id}`],
  ['person-bare', barePerson?.Id && `#/details?id=${barePerson.Id}`],
  ['genre', genres[0] && `#/list?genreId=${genres[0].Id}&serverId=${auth.ServerId}`],
  ['search', '#/search?query=a'],
  ['menu', `#/details?id=${adv.Id}`, 'menu'],
  ['preferences', '#/mypreferencesmenu'],
  ['login', '#/login', 'login'],
].filter(p => p[1] && (!only.length || only.includes(p[0])));

const brandingBefore = await get('/System/Configuration/branding');
const setCss = c => fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify({ ...brandingBefore, CustomCss: c }) });
const keepBranding = args.includes('--keep-branding');   // the caller set the CSS (parallel runs share one server)
if (!keepBranding) { await setCss(args.includes('--no-css') ? '' : css); }

// ---- in-page checks (one screen)
const SCREEN_CHECKS = ({ mobile }) => {
  const vw = innerWidth, vh = innerHeight, out = [];
  const CS = e => getComputedStyle(e);
  const label = e => (e.innerText || e.getAttribute('aria-label') || e.getAttribute('title') || '').replace(/\s+/g, ' ').trim().slice(0, 60);
  const sel = e => { const p = []; let x = e; for (let i = 0; x && x !== document.body && i < 4; i++) { p.unshift(x.tagName.toLowerCase() + (x.id ? '#' + x.id : '') + [...x.classList].slice(0, 2).map(c => '.' + c).join('')); x = x.parentElement; } return p.join(' > '); };
  const shown = e => { for (let x = e; x; x = x.parentElement) { const s = CS(x); if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity < 0.05) return false; } return true; };
  const inFixed = e => { for (let x = e; x; x = x.parentElement) { const p = CS(x).position; if (p === 'fixed' || p === 'sticky') return x; } return null; };
  const ownText = e => [...e.childNodes].some(n => n.nodeType === 3 && n.textContent.trim());
  // the line boxes of an element's text, cut to what its clipping ancestors let through (a clamped synopsis keeps
  // line boxes for its hidden lines)
  const clipOf = e => { let c = { left: 0, top: 0, right: vw, bottom: vh }; for (let x = e; x && x !== document.documentElement; x = x.parentElement) { const s = CS(x); if (/hidden|clip|auto|scroll/.test(s.overflow + s.overflowX + s.overflowY) || s.webkitLineClamp !== 'none') { const q = x.getBoundingClientRect(); c = { left: Math.max(c.left, q.left), top: Math.max(c.top, q.top), right: Math.min(c.right, q.right), bottom: Math.min(c.bottom, q.bottom) }; } } return c; };
  const lines = e => { const r = document.createRange(); r.selectNodeContents(e); const c = clipOf(e); return [...r.getClientRects()].map(q => ({ left: Math.max(q.left, c.left), top: Math.max(q.top, c.top), right: Math.min(q.right, c.right), bottom: Math.min(q.bottom, c.bottom), height: Math.min(q.bottom, c.bottom) - Math.max(q.top, c.top), width: Math.min(q.right, c.right) - Math.max(q.left, c.left) })).filter(q => q.width > 2 && q.height > 4); };
  const page = document.querySelector('.page:not(.hide)') || document.body;
  const texts = [];
  for (const e of document.querySelectorAll('body *')) {
    if (!ownText(e) || ['SCRIPT', 'STYLE', 'OPTION', 'NOSCRIPT'].includes(e.tagName) || !shown(e)) continue;
    const ls = lines(e); if (!ls.length) continue;
    texts.push({ e, ls, fixed: inFixed(e), inPage: page.contains(e) });
  }
  // collide
  const meet = (a, b) => [Math.min(a.right, b.right) - Math.max(a.left, b.left), Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top)];
  const told = new Set();
  for (let i = 0; i < texts.length; i++) for (let j = i + 1; j < texts.length; j++) {
    const a = texts[i], b = texts[j];
    if (a.e.contains(b.e) || b.e.contains(a.e) || !!a.fixed !== !!b.fixed) continue;
    const hit = a.ls.some(la => b.ls.some(lb => { const [ix, iy] = meet(la, lb); return ix >= 6 && iy >= Math.max(3, 0.35 * Math.min(la.height, lb.height)); }));
    if (hit && !told.has(a.e) && !told.has(b.e)) { told.add(a.e); out.push({ check: 'collide', sev: 'FAIL', el: sel(a.e), text: label(a.e), detail: `over "${label(b.e)}" (${sel(b.e)})` }); }
  }
  // overflow
  const sw = document.scrollingElement.scrollWidth;
  if (sw > vw + 1) {
    const past = [...document.querySelectorAll('body *')].filter(e => { const r = e.getBoundingClientRect(); return r.right > vw + 1 && r.width > 0 && r.width < sw && shown(e) && !inFixed(e); }).slice(0, 3);
    out.push({ check: 'overflow', sev: 'FAIL', el: past.map(sel).join(' | '), text: '', detail: `page is ${sw}px wide for a ${vw}px screen` });
  }
  // cut text (clipped without ellipsis or clamp)
  for (const t of texts) {
    const e = t.e, s = CS(e);
    if (!/hidden|clip/.test(s.overflow + s.overflowX + s.overflowY) || s.textOverflow === 'ellipsis' || s.webkitLineClamp !== 'none') continue;
    if (e.scrollWidth > e.clientWidth + 2 || e.scrollHeight > e.clientHeight + 4) out.push({ check: 'cut', sev: 'WARN', el: sel(e), text: label(e), detail: `content ${e.scrollWidth}x${e.scrollHeight} in ${e.clientWidth}x${e.clientHeight}` });
  }
  // text under the fixed header at the top of the page
  if (scrollY < 2) {
    const bars = [...document.querySelectorAll('.skinHeader, .MuiAppBar-root')].filter(b => shown(b) && CS(b).position === 'fixed');
    for (const b of bars) {
      const q = b.getBoundingClientRect(); if (q.height < 20 || q.height > vh / 3) continue;
      for (const t of texts) {
        if (t.fixed || !t.inPage) continue;
        if (t.ls.some(l => meet(l, q)[0] > 6 && meet(l, q)[1] > 4)) out.push({ check: 'header', sev: 'FAIL', el: sel(t.e), text: label(t.e), detail: 'under the fixed header at the top of the page' });
      }
    }
  }
  // tap size (phones)
  if (mobile) {
    const TAP = 'a[href], button, input:not([type=hidden]), select, [role=button], [role=tab]';
    const all = [...document.querySelectorAll(TAP)].filter(e => shown(e) && CS(e).pointerEvents !== 'none').map(e => ({ e, r: e.getBoundingClientRect() })).filter(o => o.r.width > 0 && o.r.bottom > 0 && o.r.top < vh);
    const sigs = new Map();
    for (const o of all) {
      const { e, r } = o;
      if (e.tagName === 'A' && CS(e).display === 'inline') continue;
      if (r.height >= 32 && r.width >= 24) continue;
      let d = Infinity, nb = null;
      for (const p of all) { if (p.e === e || p.e.contains(e) || e.contains(p.e)) continue; const gx = Math.max(0, p.r.left - r.right, r.left - p.r.right), gy = Math.max(0, p.r.top - r.bottom, r.top - p.r.bottom), dd = Math.hypot(gx, gy); if (dd < d) { d = dd; nb = p.e; } }
      if (d >= 8) continue;
      const sig = sel(e).split(' > ').slice(-2).join(' > ');
      if (sigs.has(sig)) { sigs.get(sig).n++; continue; }
      const f = { check: 'tap-size', sev: 'FAIL', el: sel(e), text: label(e), detail: `${Math.round(r.width)}x${Math.round(r.height)}px, ${Math.round(d)}px from "${label(nb)}"`, n: 1 };
      sigs.set(sig, f); out.push(f);
    }
  }
  // candidates for the veil check: light text inside the page
  const lum = c => { const m = c.match(/[\d.]+/g); if (!m) return 0; const [r, g, b] = m.map(Number); return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255; };
  // what the fixed header bar covers is not seen: cut the text boxes below it
  const barBottom = Math.max(0, ...[...document.querySelectorAll('.skinHeader, .MuiAppBar-root')].filter(b => shown(b) && CS(b).position === 'fixed').map(b => b.getBoundingClientRect()).filter(q => q.height > 20 && q.height < vh / 3 && q.top <= 0).map(q => q.bottom));
  const veil = texts.filter(t => t.inPage && !t.fixed && lum(CS(t.e).color) > 0.45).slice(0, 500).map(t => ({ el: sel(t.e), text: label(t.e), boxes: t.ls.slice(0, 6).filter(l => Math.min(vh, l.bottom) - Math.max(barBottom, l.top) > 0.6 * (l.bottom - l.top)).map(l => [Math.max(0, l.left), Math.max(barBottom, l.top), Math.min(vw, l.right), Math.min(vh, l.bottom)]) }));   // a line mostly under the bar is not read
  return { out, veil };
};

// compare the text boxes in two screenshots: A as shown, B with the pseudo-elements' backgrounds removed
const VEIL_COMPARE = async ({ a, b, cands }) => {
  const load = src => new Promise(r => { const i = new Image(); i.onload = () => r(i); i.src = 'data:image/png;base64,' + src; });
  const [ia, ib] = await Promise.all([load(a), load(b)]);
  const ctx = img => { const c = document.createElement('canvas'); c.width = img.width; c.height = img.height; const x = c.getContext('2d', { willReadFrequently: true }); x.drawImage(img, 0, 0); return x; };
  const ca = ctx(ia), cb = ctx(ib);
  const top = (x, [l, t, r, b]) => { const w = Math.max(1, Math.round(r - l)), h = Math.max(1, Math.round(b - t)); const d = x.getImageData(Math.round(l), Math.round(t), w, h).data; const v = []; for (let i = 0; i < d.length; i += 4) v.push(0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2]); v.sort((p, q) => q - p); return v[Math.floor(v.length * 0.04)] || 0; };
  // contrast read from the pixels: the text's brightest pixels against the box's median (its ground). axe-core gives
  // up on text over a picture ("incomplete"), which is exactly where a theme with a backdrop fails
  const lin = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  const stats = (x, [l, t, r, b]) => { const w = Math.max(1, Math.round(r - l)), h = Math.max(1, Math.round(b - t)); const d = x.getImageData(Math.round(l), Math.round(t), w, h).data; const v = []; for (let i = 0; i < d.length; i += 4) v.push(0.2126 * lin(d[i]) + 0.7152 * lin(d[i + 1]) + 0.0722 * lin(d[i + 2])); v.sort((p, q) => p - q); return { hi: v[Math.floor(v.length * 0.97)] || 0, mid: v[Math.floor(v.length * 0.5)] || 0 }; };
  const res = [];
  for (const c of cands) {
    let low = null;
    for (const bx of c.boxes) { if (bx[2] - bx[0] < 6 || bx[3] - bx[1] < 6) continue; const st = stats(ca, bx); const ratio = (st.hi + 0.05) / (st.mid + 0.05); if (ratio < 3 && (!low || ratio < low)) low = ratio; }
    if (low) res.push({ check: 'lowcontrast', sev: 'FAIL', el: c.el, text: c.text, detail: `about ${low.toFixed(1)}:1 against what is behind it (pixels; 3:1 needed at least)` });
    let worst = null;
    for (const bx of c.boxes) { if (bx[2] - bx[0] < 3 || bx[3] - bx[1] < 3) continue; const la = top(ca, bx), lb = top(cb, bx); if (lb > 60 && lb - la > 28 && la / lb < 0.82 && (!worst || la / lb < worst.ratio)) worst = { la: Math.round(la), lb: Math.round(lb), ratio: la / lb }; }
    if (worst) res.push({ check: 'veiled', sev: 'FAIL', el: c.el, text: c.text, detail: `text brightness ${worst.lb} → ${worst.la} under a layer (${Math.round((1 - worst.ratio) * 100)}% darker)` });
  }
  return res;
};

const findings = [];
function writeReport() {
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(findings, null, 1));
// summary grouped by check, then by element signature (one line per kind of problem, with where it shows)
const md = ['# Projection theme audit', '', `${findings.filter(f => f.sev === 'FAIL').length} FAIL, ${findings.filter(f => f.sev === 'WARN').length} WARN`, ''];
const byCheck = {};
for (const f of findings) (byCheck[f.check] ||= []).push(f);
for (const [check, list] of Object.entries(byCheck)) {
  md.push(`## ${check} (${list.length})`);
  const groups = {};
  for (const f of list) (groups[f.el.split(' > ').slice(-2).join(' > ') + ' | ' + f.detail.replace(/\d+/g, '#').slice(0, 60)] ||= []).push(f);
  for (const [k, g] of Object.entries(groups).sort((x, y) => y[1].length - x[1].length)) {
    const f = g[0];
    md.push(`- **${f.sev}** \`${f.el}\` "${f.text}" — ${f.detail}  `);
    md.push(`  ${g.length}× on ${[...new Set(g.map(x => `${x.config}/${x.page}`))].slice(0, 8).join(', ')} — e.g. ${f.shot || ''}`);
  }
  md.push('');
}
fs.writeFileSync(path.join(outDir, 'report.md'), md.join('\n'));
if (writeReport.final) console.log(`\n📋 ${path.join(outDir, 'report.md')}`);
}

const browser = await chromium.launch({ channel: 'chrome', args: ['--hide-scrollbars'] });
try {
  for (const name of configs) {
    const C = CONFIGS[name]; if (!C) { console.log('unknown config', name); continue; }
    const dir = path.join(outDir, name); fs.mkdirSync(dir, { recursive: true });
    const context = await browser.newContext({ viewport: { width: C.w, height: C.h }, deviceScaleFactor: 1, isMobile: !!C.mobile, hasTouch: !!C.mobile, userAgent: C.mobile ? UA_MOBILE : undefined });
    await context.addInitScript(l => { try { localStorage.setItem('layout', l); } catch {} }, C.layout);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(String(e.message || e).slice(0, 160)));
    const login = async () => {
      await page.goto(`${J}/web/#/login`); await page.waitForSelector('#txtManualName', { timeout: 30000 });
      await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin');
      await page.click('.manualLoginForm button[type=submit]'); await page.waitForFunction(() => !location.hash.includes('login'), null, { timeout: 30000 }); await wait(1500);
    };
    await login();
    for (const [pname, hash, mode] of PAGES) {
      const t0 = Date.now();
      try {
      errors.length = 0;
      if (mode === 'login') { await page.evaluate(() => { try { ApiClient.logout(); } catch {} }); await wait(800); }
      await page.goto('about:blank'); await page.goto(`${J}/web/${hash}`);
      try { await page.waitForSelector('.page:not(.hide) :is(.cardImageContainer, .listItem, .itemName, .searchFields, #txtManualName, .listItemBody, #favoritesTab .sections, .listItems)', { timeout: 40000, state: 'attached' }); } catch { findings.push({ config: name, page: pname, check: 'load', sev: 'FAIL', el: '', text: '', detail: 'page did not render' }); }
      await wait(2800);
      if (mode === 'menu') { await page.click('.page:not(.hide) .mainDetailButtons .btnMoreCommands').catch(() => {}); await wait(900); }
      const total = await page.evaluate(() => document.scrollingElement.scrollHeight);
      const steps = mode === 'menu' || mode === 'login' ? 1 : Math.min(6, Math.ceil(total / (C.h * 0.85)));
      for (let s = 0; s < steps; s++) {
        if (s) { await page.evaluate(y => window.scrollTo(0, y), Math.round(s * C.h * 0.85)); await wait(700); }
        const shot = path.join(dir, `${pname}-${s}.png`);
        const a = await page.screenshot({ path: shot });
        const { out, veil } = await page.evaluate(SCREEN_CHECKS, { mobile: !!C.mobile });
        await page.addStyleTag({ content: '*::before, *::after { background: none !important; box-shadow: none !important; }' }).then(h => h.evaluate(e => (e.id = 'pj-audit-noveil')));
        await wait(120);
        const b = await page.screenshot();
        await page.evaluate(() => document.getElementById('pj-audit-noveil')?.remove());
        const veiled = await page.evaluate(VEIL_COMPARE, { a: a.toString('base64'), b: b.toString('base64'), cands: veil });
        // axe contrast (the elements on this screen only would be ideal; axe checks the whole page, so once per page)
        let contrast = [];
        if (s === 0) {
          try {
            const r = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
            contrast = r.violations.flatMap(v => v.nodes.map(n => ({ check: 'contrast', sev: 'FAIL', el: n.target.join(' '), text: (n.html || '').replace(/<[^>]+>/g, '').trim().slice(0, 60), detail: (n.any[0]?.message || '').slice(0, 160) })));
          } catch (e) { contrast = [{ check: 'contrast', sev: 'WARN', el: '', text: '', detail: 'axe failed: ' + e.message.slice(0, 80) }]; }
        }
        for (const f of [...out, ...veiled, ...contrast]) findings.push({ config: name, page: pname, screen: s, shot, ...f });
      }
      // keyboard focus (desktop): four Tabs from the top, each focused element (or one of its 3 nearest ancestors: a card draws its ring on its frame) must show a ring
      if (!C.mobile && mode !== 'menu') {
        await page.evaluate(() => { window.scrollTo(0, 0); document.activeElement?.blur(); }); await wait(200);
        for (let k = 0; k < 4; k++) {
          await page.keyboard.press('Tab'); await wait(120);
          const f = await page.evaluate(() => { const e = document.activeElement; if (!e || e === document.body) return null; const s = getComputedStyle(e); let ring = false; for (let x = e, i = 0; x && i < 4 && !ring; x = x.parentElement, i++) { const t = getComputedStyle(x); ring = (t.outlineStyle !== 'none' && parseFloat(t.outlineWidth) > 0) || (t.boxShadow && t.boxShadow !== 'none'); } return ring ? null : { el: e.tagName.toLowerCase() + '.' + [...e.classList].slice(0, 2).join('.'), text: (e.innerText || e.title || e.getAttribute('aria-label') || '').trim().slice(0, 40) }; });
          if (f) findings.push({ config: name, page: pname, check: 'focus', sev: 'WARN', el: f.el, text: f.text, detail: 'focused by Tab without a visible ring' });
        }
      }
      if (mode === 'menu') { await page.keyboard.press('Escape'); }
      for (const e of [...new Set(errors)]) findings.push({ config: name, page: pname, check: 'errors', sev: 'WARN', el: '', text: '', detail: e });
      } catch (e) { findings.push({ config: name, page: pname, check: 'load', sev: 'FAIL', el: '', text: '', detail: 'audit error: ' + String(e.message).split('\n')[0].slice(0, 120) }); }
      const n = findings.filter(f => f.config === name && f.page === pname && f.sev === 'FAIL').length;
      console.log(`${n ? '❌' : '✅'} ${name.padEnd(13)} ${pname.padEnd(18)} ${n} FAIL  (${Math.round((Date.now() - t0) / 1000)} s)`);
      writeReport();
    }
    await context.close();
  }
} finally {
  await browser.close();
  if (!keepBranding) { await setCss(brandingBefore.CustomCss || ''); }
}

writeReport.final = true;
writeReport();
