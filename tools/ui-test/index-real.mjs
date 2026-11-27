// End-to-end test of the theme's "Index" (theme/projection-index.js and the plugin's CustomMetadata/Index) in the real
// jellyfin-web: genre, tag, studio and person lists, adding and removing filters, paging, play and item menu, back
// navigation, the library screens in both layouts, a user limited to one library, and the pages it must leave alone.
// Counts are compared with Jellyfin's own answers.
// Usage: node index-real.mjs <base_url> <out_dir>
// Needs the theme script setting on (ui-test.sh container) and theme-seed.mjs run once (tags, genres, people).
// The server's custom CSS is replaced by the plugin's theme for the run, then restored.
import { createRequire } from 'node:module';
import fs from 'node:fs';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const [J, outDir] = process.argv.slice(2).filter(a => !a.startsWith('--'));
if (!J || !outDir) { console.error('usage: node index-real.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const wait = ms => new Promise(r => setTimeout(r, ms));
const UA_MOBILE = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36';

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="index-real", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const U = auth.User.Id, S = auth.ServerId;
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const get = url => fetch(J + url, { headers: H }).then(r => r.json());
const TYPES = 'Series,Season,Episode,Movie,Video,MusicVideo';
// Jellyfin 12 has one layout for both settings, and draws the library toolbar in the app bar
const version = (await get('/System/Info/Public')).Version;
const legacy = version.startsWith('10.');
const apiCount = async q => (await get(`/Items?userId=${U}&Recursive=true&${new URLSearchParams({ IncludeItemTypes: TYPES, ...q })}`)).Items.length;

const views = (await get(`/UserViews?userId=${U}`)).Items;
const tv = views.find(v => v.Name === 'Séries'), movies = views.find(v => v.CollectionType === 'movies');
const genre = await get(`/Genres/Documentaire?userId=${U}`);
const studio = await get(`/Studios/${encodeURIComponent('Nord Films')}?userId=${U}`);
const person = await get(`/Persons/${encodeURIComponent('Maëlle Roussel')}?userId=${U}`);

let passed = 0, failed = 0;
const check = (name, ok, detail = '') => { ok ? passed++ : failed++; console.log(`${ok ? '✅' : '❌'} ${name}${detail === '' ? '' : ' — ' + detail}`); };

const brandingBefore = await get('/System/Configuration/branding');
const setCss = c => fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify({ ...brandingBefore, CustomCss: c }) });
const THEME = '@import url("../CustomMetadata/Theme/projection.css");';

const browser = await chromium.launch({ channel: 'chrome', args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'] });
const errors = [];
async function open(layout, w, h, mobile = false) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, ...(mobile ? { userAgent: UA_MOBILE, isMobile: true, hasTouch: true } : {}) });
  const page = await ctx.newPage();
  // "player cannot be null" is jellyfin-web's own, thrown when its video page is left (also with stock lists)
  page.on('pageerror', e => { if (e.message !== 'player cannot be null') errors.push(`${layout} ${w}: ${e.message}`); });
  page.on('console', m => { if (/\[projection/.test(m.text())) errors.push(`${layout} ${w}: ${m.text()}`); });
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin');
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  // with Jellyfin's backdrops on (a display setting, per browser): the screen must not let them show
  await page.evaluate(([l, u]) => { localStorage.setItem('layout', l); localStorage.setItem(`${u}-enableBackdrops`, 'true'); }, [layout, U]);
  await page.reload(); await wait(2500);
  return { ctx, page };
}
// in-app navigation, as a click on a link does
const go = (page, hash) => page.evaluate(h => { location.hash = h; }, hash);
const ix = '.page:not(.hide) .pj-ix';
// the shown screen: in the classic layout a library keeps one per tab it showed
const shown = () => [...document.querySelectorAll('.page:not(.hide) .pj-ix')].find(r => r.offsetParent !== null);
const ready = async page => { await wait(450); await page.waitForFunction(`(() => { const r = (${shown})(); const p = r && r.querySelector('.pj-ix-under-title p'); return p && !p.textContent.includes('Chargement') && !r.classList.contains('pj-ix-busy'); })()`, null, { timeout: 20000 }); await wait(500); };
const panelReady = page => page.waitForFunction(() => { const b = document.querySelector('.pj-ix-pbody'); return b && !b.classList.contains('pj-ix-busy') && b.querySelector('[data-k]'); }, null, { timeout: 15000 });
const state = page => page.evaluate(`(${((find) => {
  const r = find();
  if (!r) return null;
  const t = e => e.textContent.replace(/\s+/g, ' ').trim();
  return {
    title: t(r.querySelector('h1')), terms: [...r.querySelectorAll('.pj-ix-term b')].map(t), sum: t(r.querySelector('.pj-ix-under-title p')),
    pressed: t(r.querySelector('.pj-ix-type[aria-pressed=true]') || { textContent: '' }),
    types: Object.fromEntries([...r.querySelectorAll('.pj-ix-type')].map(b => [b.dataset.type || 'all', Number(b.querySelector('small').textContent)])),
    cards: r.querySelectorAll('.pj-ix-item').length, groups: [...r.querySelectorAll('.pj-ix-group h2')].map(t),
    names: [...r.querySelectorAll('.pj-ix-name')].map(t),
    stockShown: [...r.parentElement.children].filter(c => c !== r && getComputedStyle(c).display !== 'none').length,
    sideways: document.documentElement.scrollWidth - innerWidth,
  };
}).toString()})(${shown})`);
const shot = (page, name) => page.screenshot({ path: `${outDir}/${name}.png` });
// the header of a detail page: the results' picture whole, below the app bar (nothing hides it), and none of Jellyfin's
const ownPicture = page => page.evaluate(`(() => { scrollTo(0, 0); const r = (${shown})(), pic = r.querySelector('.pj-ix-pic'), img = pic.querySelector('img'), back = document.querySelector('.backdropContainer');
  const bar = document.querySelector('.MuiAppBar-root').getBoundingClientRect().bottom, theirs = !!back && getComputedStyle(back).display !== 'none';
  if (!img) return { img: false, theirs };
  const b = img.getBoundingClientRect(), first = r.querySelector('.pj-ix-item');
  return { img: true, theirs, under: Math.round(bar - b.top), whole: getComputedStyle(img).objectFit === 'contain' && b.left >= -1 && b.right <= innerWidth + 1 && b.height <= pic.getBoundingClientRect().height + 1,
    height: Math.round(b.height / innerHeight * 100), art: document.documentElement.classList.contains('pj-ix-art-on'), ground: getComputedStyle(document.documentElement).getPropertyValue('--pj-veil').trim(),
    cards: first ? Math.round(first.getBoundingClientRect().top / innerHeight * 100) : 0 }; })()`);
const checkPicture = async (name, page) => {
  await page.waitForFunction(`(() => { const r = (${shown})(); return r && (r.querySelector('.pj-ix-pic img') || r.querySelector('.pj-ix-bare')); })()`, null, { timeout: 10000 });
  const p = await ownPicture(page);
  check(`${name}: the picture whole, below the app bar, Jellyfin's backdrop hidden`, !p.theirs && (!p.img || (p.whole && p.under <= 1 && p.height <= 57)), JSON.stringify(p));
  check(`${name}: the page takes the picture's colours, the results start on the first screen`, !p.img || (p.art && p.ground !== '13, 20, 21' && p.cards < 90), JSON.stringify(p));
};

try {
  await setCss(THEME);
  const { ctx, page } = await open('experimental', 1440, 900);

  // ---------- the four ways in ----------
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}`); await ready(page);
  let s = await state(page);
  check('tag list: the tag is the title', s?.terms.join() === 'Montagne', s?.title);
  check('tag list: stock list hidden', s.stockShown === 0, s.stockShown);
  check('tag list: every video item with the tag', s.types.all === await apiCount({ Tags: 'Montagne' }), s.types.all);
  check('tag list: no sideways scroll', s.sideways <= 0, s.sideways);
  await checkPicture('tag list', page);
  await shot(page, 'tag-1440');

  await go(page, `#/list?genreId=${genre.Id}&serverId=${S}`); await ready(page);
  s = await state(page);
  check('genre list: the genre is the title', s.terms.join() === 'Documentaire', s.title);
  check('genre list: episodes are there (stock shows none)', (s.types.Episode || 0) > 0, JSON.stringify(s.types));
  check('genre list: count matches the server', s.types.all === await apiCount({ GenreIds: genre.Id }), s.types.all);

  await go(page, `#/list?studioId=${studio.Id}&serverId=${S}`); await ready(page);
  s = await state(page);
  check('studio list: "chez <studio>"', /chez/.test(s.title) && s.terms.join() === 'Nord Films', s.title);
  check('studio list: count matches the server', s.types.all === await apiCount({ StudioIds: studio.Id }), s.types.all);

  await go(page, `#/list?type=Episode&personId=${person.Id}&serverId=${S}`); await ready(page);
  s = await state(page);
  const epCount = await apiCount({ PersonIds: person.Id, IncludeItemTypes: 'Episode' });
  check('person list: name in the title', s.terms.join() === 'Maëlle Roussel', s.title);
  check('person list: the link\'s type is chosen', s.pressed.startsWith('Épisodes'), s.pressed);
  check('person list: episode count matches the server', s.types.Episode === epCount, `${s.types.Episode} / ${epCount}`);
  check('person list: episodes grouped by series', s.groups.length >= 1, s.groups.join(' | '));
  await shot(page, 'person-1440');

  // the real way in: the links of an episode's page
  const tagged = (await get(`/Items?userId=${U}&Recursive=true&IncludeItemTypes=Episode&Tags=Montagne&Genres=Aventure&Limit=1`)).Items[0];
  for (const [name, selector, term] of [['tag', '.itemTags a', null], ['genre', '.genresGroup a, .itemGenres a, .genres a, a[href*="genreId="]', null]]) {
    await go(page, `#/details?id=${tagged.Id}&serverId=${S}`); await wait(3000);
    const link = page.locator(`.page:not(.hide) :is(${selector})`).filter({ visible: true }).first();
    // on 12 the genre links of an episode's page are not on screen: their address is followed as it is
    const hidden = !await link.count();
    const any = page.locator(`.page:not(.hide) :is(${selector})`).first();
    if (hidden && (legacy || !await any.count())) { check(`episode page: a ${name} link to follow`, false); continue; }
    const wantedTerm = term || (await (hidden ? any.textContent() : link.innerText())).trim();
    if (hidden) { await go(page, await any.getAttribute('href')); } else { await link.click(); }
    await ready(page);
    s = await state(page);
    check(`episode page → ${name} link: the index of "${wantedTerm}", with episodes`, s?.terms.join() === wantedTerm && (s.types.Episode || 0) > 0, `${s?.title} ${JSON.stringify(s?.types)}`);
  }

  // ---------- the index panel ----------
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=1`); await ready(page);
  await page.keyboard.press('/'); await wait(600);
  check('"/" opens the index', await page.locator('.pj-ix-panel').isVisible());
  await panelReady(page); await wait(400);
  await shot(page, 'panel-overview-1440');
  check('overview fits 1440x900', await page.evaluate(() => { const b = document.querySelector('.pj-ix-pbody'); return b.scrollHeight <= b.clientHeight + 2; }),
    await page.evaluate(() => { const b = document.querySelector('.pj-ix-pbody'); return `${b.scrollHeight} in ${b.clientHeight}`; }));
  for (const tab of ['genres', 'tags', 'people', 'years', 'state']) {
    await page.click(`.pj-ix-tab[data-tab=${tab}]`); await wait(150); await panelReady(page); await wait(250); await shot(page, `panel-${tab}-1440`);
    check(`panel tab ${tab} offers choices`, await page.locator('.pj-ix-pbody [data-k]').count() > 0);
  }
  await page.keyboard.press('Escape'); await wait(300);
  check('Escape closes the index', await page.locator('.pj-ix-panel').count() === 0);

  // two filters through the search, then the base removed
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=2`); await ready(page);
  const before = (await state(page)).types.all;
  await page.click(`${ix} .pj-ix-open`); await wait(400);
  await page.fill('.pj-ix-search', 'hiv'); await wait(600); await panelReady(page); await shot(page, 'panel-search-1440');
  await page.press('.pj-ix-search', 'Enter'); await ready(page);
  await page.click('.pj-ix-pfoot .pj-ix-go'); await wait(400);
  s = await state(page);
  check('search + Enter adds the filter', s.terms.join() === 'Montagne,Hiver', s.title);
  check('two tags narrow the list', s.types.all > 0 && s.types.all <= before, `${before} → ${s.types.all}`);
  await shot(page, 'two-filters-1440');
  await page.click(`${ix} .pj-ix-term[data-v=Montagne]`); await ready(page);
  s = await state(page);
  const hiver = await apiCount({ Tags: 'Hiver' });
  check('removing the first filter asks the server again', s.terms.join() === 'Hiver' && s.types.all === hiver, `${s.types.all} / ${hiver}`);
  await page.click(`${ix} .pj-ix-term[data-v=Hiver]`); await ready(page);
  s = await state(page);
  const everything = await apiCount({});
  check('no filter left: the whole library', s.terms.length === 0 && s.types.all === everything, `${s.title} — ${s.types.all} / ${everything}`);
  await shot(page, 'all-1440');
  // paging: the first page only, the next ones as one scrolls
  check('first page only', s.cards === 60 && everything > 60, `${s.cards} / ${everything}`);
  const asked = [];
  const listenPages = r => { if (r.url().endsWith('/CustomMetadata/Index')) asked.push(r.postDataJSON()); };
  page.on('request', listenPages);
  for (let i = 0; i < 12 && (await state(page)).cards < everything; i++) { await page.evaluate(() => scrollTo(0, document.documentElement.scrollHeight)); await wait(900); }
  page.off('request', listenPages);
  s = await state(page);
  const ids = await page.evaluate(`[...(${shown})().querySelectorAll('.pj-ix-item')].map(a => a.dataset.id)`);
  check('scrolling brings every result, each once', s.cards === everything && new Set(ids).size === everything, `${s.cards} cards, ${new Set(ids).size} ids / ${everything}`);
  check('the next pages are asked 60 at a time', asked.length >= 2 && asked.every(a => a.Limit === 60 && a.StartIndex > 0), JSON.stringify(asked.map(a => [a.StartIndex, a.Limit])));

  // a tag on a card, the sort order, then back from an item
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=3`); await ready(page);
  await page.click(`${ix} .pj-ix-type[data-type=Episode]`); await ready(page);
  const first = page.locator(`${ix} .pj-ix-item[data-type=Episode]`).first();
  await first.hover(); await wait(300); await shot(page, 'card-hover-1440');
  const tagButton = first.locator('.pj-ix-tag').first();
  const added = await tagButton.getAttribute('data-v');
  await tagButton.click(); await ready(page);
  s = await state(page);
  check('a card\'s tag adds a filter', s.terms.includes(added) && s.terms.includes('Montagne'), s.title);
  await page.selectOption(`${ix} .pj-ix-sort`, 'name'); await ready(page);
  const wanted = await state(page);
  await page.locator(`${ix} .pj-ix-item .pj-ix-name`).first().click(); await wait(2500);
  check('a title opens the item', /#\/details/.test(await page.evaluate(() => location.hash)));
  await page.goBack(); await ready(page);
  s = await state(page);
  check('back: filters and order kept', s.terms.join() === wanted.terms.join() && await page.locator(`${ix} .pj-ix-sort`).inputValue() === 'name', s.title);

  // jellyfin-web's own item menu and playback
  await page.locator(`${ix} .pj-ix-item`).first().hover(); await wait(300);
  await page.locator(`${ix} .pj-ix-item .pj-ix-act[data-action=menu]`).first().click(); await wait(1200);
  check('"Plus" opens jellyfin-web\'s item menu', await page.locator('.actionSheet').count() > 0);
  await page.keyboard.press('Escape'); await wait(800);
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=4`); await ready(page);
  const real = `${ix} .pj-ix-item[data-type=Episode]:not(.pj-ix-miss)`;
  await page.hover(`${real} >> nth=0`); await wait(3500);
  check('hover preview on an Index card', await page.locator(`${ix} .pj-preview`).count() > 0);
  await page.mouse.move(5, 5);
  // the demo videos last two seconds: what is checked is the file the player asks for, not the page it ends on
  const played = [];
  page.on('request', r => { const m = /\/Videos\/([0-9a-f]{32})\/stream\./.exec(r.url()); if (m) played.push(m[1]); });
  // a reload ends the playback and its queue, which goes on in the background between two short files
  const leavePlayer = async () => { await page.reload(); await wait(3000); };
  const firstFile = await page.evaluate(() => document.querySelector('.page:not(.hide) .pj-ix-item[data-type=Episode]:not(.pj-ix-miss)').dataset.id);
  await page.click(`${ix} .pj-ix-type[data-type=Episode]`); await ready(page);
  await page.click(`${ix} [data-play=all]`); await wait(4500);
  check('"Tout lire" plays the first episode of the list', played[0] === firstFile, `${played[0]} / ${firstFile}`);
  await leavePlayer();
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=5`); await ready(page);
  await page.hover(`${real} >> nth=1`); await wait(300);
  const cardFile = await page.locator(real).nth(1).getAttribute('data-id');
  played.length = 0;
  await page.locator(real).nth(1).locator('.pj-ix-act[data-action=resume]').click(); await wait(4000);
  check('a card\'s play button plays that episode', played.includes(cardFile), `${played[0]} / ${cardFile}`);
  await leavePlayer();

  // scrolled: the ribbon stays and names the filter
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=6`); await ready(page);
  await page.evaluate(() => scrollTo(0, 700)); await wait(700); await shot(page, 'scrolled-1440');
  const rib = await page.evaluate(() => { const r = document.querySelector('.page:not(.hide) .pj-ix-ribbon'), m = r.querySelector('.pj-ix-mini'); return { top: Math.round(r.getBoundingClientRect().top), mini: getComputedStyle(m).display !== 'none' && m.textContent }; });
  check('scrolled: ribbon stuck under the bar, with the filter', rib.top >= 0 && rib.top <= 60 && rib.mini === 'Montagne', JSON.stringify(rib));

  // ---------- the library screens (experimental layout: the view is in the address) ----------
  const tvAddress = `#/tv?topParentId=${tv.Id}&collectionType=tvshows`, movieAddress = `#/movies?topParentId=${movies.Id}&collectionType=movies`;
  const libraryCount = (lib, type, extra = {}) => apiCount({ ParentId: lib.Id, IncludeItemTypes: type, ...extra });
  for (const [name, hash, type, lib] of [['series view', tvAddress, 'Series', tv], ['episodes view', `${tvAddress}&tab=5`, 'Episode', tv], ['films', movieAddress, 'Movie', movies]]) {
    await go(page, hash); await wait(600); await ready(page);
    s = await state(page);
    const n = await libraryCount(lib, type);
    check(`library ${name}: the library (or the episodes view) is the title`, s.title.replace('+', '') === (type === 'Episode' ? 'Épisodes' : lib.Name) && s.terms.length === 0, s.title);
    check(`library ${name}: count matches the server`, (s.types[type] ?? s.cards) === n && s.cards === Math.min(60, n), `${s.types[type]} / ${n}, ${s.cards} cards`);
    check(`library ${name}: stock view hidden`, s.stockShown === 0, s.stockShown);
    const columns = await page.evaluate(`new Set([...(${shown})().querySelector('.pj-ix-grid').children].map(c => Math.round(c.getBoundingClientRect().left))).size`);
    check(`library ${name}: cards side by side`, columns >= Math.min(2, await page.evaluate(`(${shown})().querySelector('.pj-ix-grid').children.length`)), columns);
    const bar = await page.evaluate(() => ({ height: Math.round(document.querySelector('.MuiAppBar-root').getBoundingClientRect().height), page: Math.round(document.querySelector('.page:not(.hide)').getBoundingClientRect().top), row: [...document.querySelectorAll('.MuiAppBar-root [aria-controls="library-view-menu"]')].some(b => b.offsetParent !== null) }));
    check(`library ${name}: jellyfin-web's second bar row is gone and the page moved up`, !bar.row && bar.page === bar.height && bar.height <= 48, JSON.stringify(bar));
    check(`library ${name}: the header covers neither title nor ribbon`, await page.evaluate(`(() => { const r = (${shown})(), b = document.querySelector('.MuiAppBar-root').getBoundingClientRect().bottom; return r.querySelector('h1').getBoundingClientRect().top >= b && parseFloat(getComputedStyle(r.querySelector('.pj-ix-ribbon')).top) >= b - 1; })()`));
    await checkPicture(`library ${name}`, page);
    await shot(page, `library-${type}-1440`);
  }
  // sorted by name, the letter column filters like jellyfin-web's
  await go(page, tvAddress); await wait(600); await ready(page);
  await page.click(`${ix} .pj-ix-alphaletter[data-v=L]`); await ready(page);
  s = await state(page);
  const withL = await libraryCount(tv, 'Series', { NameStartsWith: 'L' });
  check('library: a letter keeps the series starting with it', s.types.Series === withL && s.names.every(n => n.startsWith('Les') || n.startsWith('L')), `${s.types.Series} / ${withL}: ${s.names.join(' | ')}`);
  await page.click(`${ix} .pj-ix-term[data-v=L]`); await ready(page);
  check('library: the letter is removed from the title', (await state(page)).terms.length === 0);
  // a filter on the library, from the panel
  await page.click(`${ix} .pj-ix-open`); await panelReady(page);
  await page.click('.pj-ix-pbody [data-k=genres][data-v=Documentaire]'); await ready(page);
  await page.keyboard.press('Escape'); await wait(300);
  s = await state(page);
  const docs = await libraryCount(tv, 'Series', { Genres: 'Documentaire' });
  check('library: a genre narrows the library', s.terms.join() === 'Documentaire' && s.types.Series === docs, `${s.types.Series} / ${docs}`);
  check('library: filtered, the library is named above the filter', (await page.locator(`${ix} .pj-ix-eyebrow`).first().innerText()).trim() === tv.Name);
  await shot(page, 'library-filtered-1440');
  // the other views stay jellyfin-web's, and one comes back from them
  await go(page, tvAddress + '&tab=0'); await wait(600); await ready(page);
  // the title offers the library's views: by keyboard, then a click
  await page.focus(`${ix} .pj-ix-view`); await page.keyboard.press('Enter'); await wait(300);
  const menu = await page.evaluate(`(() => { const r = (${shown})(), m = r.querySelector('.pj-ix-menu'), b = m.getBoundingClientRect(), last = m.lastElementChild, l = last.getBoundingClientRect(); return { open: !m.hidden, items: [...m.children].map(a => a.textContent), current: m.querySelector('[aria-current]')?.textContent, focus: document.activeElement.textContent, onTop: document.elementFromPoint(b.left + 30, l.top + l.height / 2) === last }; })()`);
  check('library: the title opens the views, above the ribbon', menu.open && menu.items.length === 8 && menu.current === 'Séries' && menu.focus === 'Séries' && menu.onTop, JSON.stringify(menu));
  await shot(page, 'views-1440');
  await page.keyboard.press('Escape'); await wait(200);
  check('library: Escape closes the views', await page.locator(`${ix} .pj-ix-menu`).first().isHidden() && await page.locator(ix).count() > 0);
  await page.locator(`${ix} .pj-ix-view`).first().click(); await wait(300);
  await page.locator(`${ix} .pj-ix-menu a`, { hasText: /^Suggestions$/ }).click(); await wait(2500);
  check('library: leaving it, the header has jellyfin-web\'s controls again', await page.evaluate(() => !document.documentElement.classList.contains('pj-ix-libon') && document.querySelector('.MuiAppBar-root [aria-controls="library-view-menu"]')?.offsetParent !== null));
  check('library: "Suggestions" gives jellyfin-web its view back', await page.locator(ix).count() === 0 && await page.evaluate(() => document.querySelector('.page:not(.hide)').innerText.length > 0));
  await go(page, tvAddress + '&tab=0&n=back'); await wait(600); await ready(page);
  check('library: back to the series view', (await state(page))?.types.Series === await libraryCount(tv, 'Series'));

  // ---------- pages left alone ----------
  await go(page, `#/list?type=Movie&IsFavorite=true&serverId=${S}`); await wait(2500);
  check('a favourites list stays jellyfin-web\'s', await page.locator(ix).count() === 0);
  await go(page, `#/details?id=${person.Id}&serverId=${S}`); await wait(3000);
  check('a detail page gets no index', await page.locator(ix).count() === 0);
  check('away from the screens, Jellyfin\'s backdrop and colours are left alone', await page.evaluate(() => !document.documentElement.matches('.pj-ix-shown, .pj-ix-art-on')));
  await ctx.close();

  // ---------- other screens ----------
  for (const [layout, w, h, mobile] of [['experimental', 1920, 1080], ['desktop', 1440, 900], ['experimental', 1024, 768], ['experimental', 390, 844, true], ['experimental', 320, 640, true]]) {
    const o = await open(layout, w, h, mobile);
    const tag = `${layout === 'desktop' ? 'classic' : 'exp'}-${w}`;
    await go(o.page, `#/list?type=tag&tag=Montagne&serverId=${S}`); await ready(o.page);
    s = await state(o.page);
    check(`${tag}: built, no sideways scroll`, s.cards > 0 && s.sideways <= 0, `${s.cards} cards, ${s.sideways}px`);
    await shot(o.page, `tag-${tag}`);
    await o.page.click(`${ix} .pj-ix-open`); await wait(800);
    const over = await o.page.evaluate(() => { const b = document.querySelector('.pj-ix-pbody'); return b.scrollWidth - b.clientWidth; });
    check(`${tag}: panel without sideways scroll`, over <= 0, over);
    await shot(o.page, `panel-${tag}`);
    await o.page.click('.pj-ix-pfoot .pj-ix-go'); await wait(300);
    await o.page.evaluate(() => scrollTo(0, 600)); await wait(600); await shot(o.page, `scrolled-${tag}`);
    await o.ctx.close();
  }

  // ---------- the library screens (classic layout: the view is an open tab; 10.11 only) ----------
  if (legacy) {
    const c = await open('desktop', 1440, 900);
    await go(c.page, `#/tv?topParentId=${tv.Id}&collectionType=tvshows`); await wait(1500); await ready(c.page);
    s = await state(c.page);
    check('classic: the series tab holds the screen', s?.title.replace('+', '') === tv.Name && s.types.Series === await libraryCount(tv, 'Series') && s.stockShown === 0, `${s?.title} ${JSON.stringify(s?.types)}`);
    await shot(c.page, 'library-classic-1440');
    const tabButton = name => c.page.locator('.headerTabs .emby-tab-button:visible', { hasText: new RegExp(`^${name}$`) });
    await tabButton('Suggestions').click(); await wait(2000);
    check('classic: another tab is jellyfin-web\'s', await c.page.evaluate(`!(${shown})()`));
    await tabButton('Épisodes').click(); await wait(1500);
    // the series tab is still on screen while jellyfin-web slides to the next one
    await c.page.waitForFunction(`(() => { const r = (${shown})(); return r && r.querySelector('.pj-ix-type[aria-pressed=true]')?.textContent.startsWith('Épisodes'); })()`, null, { timeout: 15000 }).catch(() => {});
    await ready(c.page);
    s = await state(c.page);
    check('classic: the episodes tab', s.types.Episode === await libraryCount(tv, 'Episode') && s.pressed.startsWith('Épisodes'), `${s.pressed} ${JSON.stringify(s.types)}`);
    await tabButton('Séries').click(); await wait(2000); await ready(c.page);
    check('classic: back to the series tab', (await state(c.page)).pressed.startsWith('Séries'));
    await go(c.page, `#/movies?topParentId=${movies.Id}&collectionType=movies`); await wait(1500); await ready(c.page);
    s = await state(c.page);
    check('classic: the films tab', s.title.replace('+', '') === movies.Name && s.cards === await libraryCount(movies, 'Movie'), `${s.title} ${s.cards}`);
    await c.ctx.close();
  }

  // ---------- a user limited to the films ----------
  {
    const made = await (await fetch(`${J}/Users/New`, { method: 'POST', headers: H, body: JSON.stringify({ Name: 'index-limited', Password: 'limited' }) })).json();
    try {
      const films = (await get('/Library/VirtualFolders')).find(f => f.Name === movies.Name);
      await fetch(`${J}/Users/${made.Id}/Policy`, { method: 'POST', headers: H, body: JSON.stringify({ ...made.Policy, EnableAllFolders: false, EnabledFolders: [films.ItemId] }) });
      const login = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="index-limited", Version="1"' }, body: JSON.stringify({ Username: 'index-limited', Pw: 'limited' }) })).json();
      const ask = async body => (await fetch(`${J}/CustomMetadata/Index`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${login.AccessToken}"` }, body: JSON.stringify(body) })).json();
      const all = await ask({ Summary: true, Limit: 60 }), series = await ask({ ParentId: tv.Id, Summary: true, Limit: 60 });
      check('limited user: sees only the allowed library', all.Total === await libraryCount(movies, 'Movie') && Object.keys(all.Types).filter(t => t).join() === 'Movie', JSON.stringify(all.Types));
      check('limited user: nothing from a library not allowed', series.Total === 0 && (series.Items || []).length === 0, series.Total);
    } finally {
      await fetch(`${J}/Users/${made.Id}`, { method: 'DELETE', headers: H });
    }
    const anonymous = await fetch(`${J}/CustomMetadata/Index`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    check('without a signed-in user: refused', anonymous.status === 401, anonymous.status);
  }

  // ---------- without the theme's stylesheet ----------
  await setCss('');
  const o = await open('experimental', 1440, 900);
  await go(o.page, `#/list?type=tag&tag=Montagne&serverId=${S}`); await wait(3500);
  check('without the theme: stock list', await o.page.locator('.pj-ix').count() === 0 && await o.page.locator('.page:not(.hide) .card').count() > 0);
  await o.ctx.close();

  check('no script error', errors.length === 0, errors.slice(0, 5).join(' || '));
} finally {
  await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify(brandingBefore) });
  await browser.close();
}
console.log(`\n${passed} passed, ${failed} failed. Screenshots: ${outDir}`);
process.exit(failed ? 1 : 0);
