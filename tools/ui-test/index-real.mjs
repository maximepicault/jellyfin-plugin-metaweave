// End-to-end test of the theme's "Index" (theme/projection-index.js) in the real jellyfin-web: genre, tag, studio and
// person lists, adding and removing filters, play and item menu, back navigation, the library entry, and the pages it
// must leave alone. Counts are compared with the server's own answers.
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
  await page.evaluate(l => localStorage.setItem('layout', l), layout);
  await page.reload(); await wait(2500);
  return { ctx, page };
}
// in-app navigation, as a click on a link does
const go = (page, hash) => page.evaluate(h => { location.hash = h; }, hash);
const ix = '.page:not(.hide) .pj-ix';
const ready = async page => { await page.waitForFunction(() => { const p = document.querySelector('.page:not(.hide) .pj-ix-under-title p'); return p && !p.textContent.includes('Chargement'); }, null, { timeout: 20000 }); await wait(700); };
const state = page => page.evaluate(() => {
  const r = document.querySelector('.page:not(.hide) .pj-ix');
  if (!r) return null;
  const t = e => e.textContent.replace(/\s+/g, ' ').trim();
  return {
    title: t(r.querySelector('h1')), terms: [...r.querySelectorAll('.pj-ix-term b')].map(t), sum: t(r.querySelector('.pj-ix-under-title p')),
    pressed: t(r.querySelector('.pj-ix-type[aria-pressed=true]') || { textContent: '' }),
    types: Object.fromEntries([...r.querySelectorAll('.pj-ix-type')].map(b => [b.dataset.type || 'all', Number(b.querySelector('small').textContent)])),
    cards: r.querySelectorAll('.pj-ix-item').length, groups: [...r.querySelectorAll('.pj-ix-group h2')].map(t),
    stockShown: [...r.parentElement.children].filter(c => c !== r && getComputedStyle(c).display !== 'none').length,
    sideways: document.documentElement.scrollWidth - innerWidth,
  };
});
const shot = (page, name) => page.screenshot({ path: `${outDir}/${name}.png` });

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
  for (const [name, selector, term] of [['tag', '.itemTags a', null], ['genre', '.genresGroup a, .itemGenres a, .genres a', null]]) {
    await go(page, `#/details?id=${tagged.Id}&serverId=${S}`); await wait(3000);
    const link = page.locator(`.page:not(.hide) :is(${selector})`).filter({ visible: true }).first();
    if (!await link.count()) { check(`episode page: a ${name} link to follow`, false); continue; }
    const wantedTerm = term || (await link.innerText()).trim();
    await link.click(); await ready(page);
    s = await state(page);
    check(`episode page → ${name} link: the index of "${wantedTerm}", with episodes`, s?.terms.join() === wantedTerm && (s.types.Episode || 0) > 0, `${s?.title} ${JSON.stringify(s?.types)}`);
  }

  // ---------- the index panel ----------
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=1`); await ready(page);
  await page.keyboard.press('/'); await wait(600);
  check('"/" opens the index', await page.locator('.pj-ix-panel').isVisible());
  await page.waitForFunction(() => !document.querySelector('.pj-ix-pbody').textContent.includes('arrivent'), null, { timeout: 15000 });
  await shot(page, 'panel-overview-1440');
  check('overview fits 1440x900', await page.evaluate(() => { const b = document.querySelector('.pj-ix-pbody'); return b.scrollHeight <= b.clientHeight + 2; }),
    await page.evaluate(() => { const b = document.querySelector('.pj-ix-pbody'); return `${b.scrollHeight} in ${b.clientHeight}`; }));
  for (const tab of ['genres', 'tags', 'people', 'years', 'state']) {
    await page.click(`.pj-ix-tab[data-tab=${tab}]`); await wait(350); await shot(page, `panel-${tab}-1440`);
    check(`panel tab ${tab} offers choices`, await page.locator('.pj-ix-pbody [data-k]').count() > 0);
  }
  await page.keyboard.press('Escape'); await wait(300);
  check('Escape closes the index', await page.locator('.pj-ix-panel').count() === 0);

  // two filters through the search, then the base removed
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=2`); await ready(page);
  const before = (await state(page)).types.all;
  await page.click(`${ix} .pj-ix-open`); await wait(400);
  await page.fill('.pj-ix-search', 'hiv'); await wait(300); await shot(page, 'panel-search-1440');
  await page.press('.pj-ix-search', 'Enter'); await wait(500);
  await page.click('.pj-ix-pfoot .pj-ix-go'); await wait(500);
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

  // a tag on a card, the sort order, then back from an item
  await go(page, `#/list?type=tag&tag=Montagne&serverId=${S}&n=3`); await ready(page);
  await page.click(`${ix} .pj-ix-type[data-type=Episode]`); await wait(400);
  const first = page.locator(`${ix} .pj-ix-item[data-type=Episode]`).first();
  await first.hover(); await wait(300); await shot(page, 'card-hover-1440');
  const tagButton = first.locator('.pj-ix-tag').first();
  const added = await tagButton.getAttribute('data-v');
  await tagButton.click(); await wait(500);
  s = await state(page);
  check('a card\'s tag adds a filter', s.terms.includes(added) && s.terms.includes('Montagne'), s.title);
  await page.selectOption(`${ix} .pj-ix-sort`, 'name'); await wait(400);
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
  await page.click(`${ix} .pj-ix-type[data-type=Episode]`); await wait(300);
  await page.click(`${ix} [data-play=all]`); await wait(4000);
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

  // ---------- the library entry ----------
  for (const [name, hash, type] of [['series tab', `#/tv?topParentId=${tv.Id}&collectionType=tvshows`, 'Series'], ['episodes tab', `#/tv?topParentId=${tv.Id}&collectionType=tvshows&tab=5`, 'Episode'], ['movies', `#/movies?topParentId=${movies.Id}&collectionType=movies`, 'Movie']]) {
    await go(page, hash); await wait(3500);
    const entry = page.locator('.pj-ix-entry:visible');
    check(`library ${name}: one "Tous les filtres" entry`, await entry.count() === 1, await entry.count());
    if (!await entry.count()) continue;
    await shot(page, `entry-${type}-1440`);
    await entry.first().click(); await ready(page);
    s = await state(page);
    const lib = type === 'Movie' ? movies : tv;
    const n = await apiCount({ ParentId: lib.Id, IncludeItemTypes: type });
    check(`library ${name}: the index of that library`, s.types[type] === n && s.terms.length === 0, `${s.title} — ${s.types[type]} / ${n}`);
    await shot(page, `library-${type}-1440`);
  }

  // ---------- pages left alone ----------
  await go(page, `#/list?type=Movie&IsFavorite=true&serverId=${S}`); await wait(2500);
  check('a favourites list stays jellyfin-web\'s', await page.locator(ix).count() === 0);
  await go(page, `#/details?id=${person.Id}&serverId=${S}`); await wait(3000);
  check('a detail page gets no index', await page.locator(ix).count() === 0);
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
