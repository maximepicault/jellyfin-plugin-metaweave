// Times the theme's Index on a large library (big-library.sh): the plugin's answers, then the screens in a browser.
// For comparison, also times reading the whole library from Jellyfin, which is what the screens must not do.
// Usage: node index-perf.mjs <base_url> <out_dir>
// The server's custom CSS is replaced by the plugin's theme for the run, then restored.
import { createRequire } from 'node:module';
import fs from 'node:fs';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const [J, outDir] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node index-perf.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const wait = ms => new Promise(r => setTimeout(r, ms));
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="index-perf", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const U = auth.User.Id;
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const views = (await (await fetch(`${J}/UserViews?userId=${U}`, { headers: H })).json()).Items;
const tv = views.find(v => v.CollectionType === 'tvshows'), movies = views.find(v => v.CollectionType === 'movies');

async function timed(label, run) {
  const t = performance.now();
  const r = await run();
  const text = await r.text();
  console.log(`${label.padEnd(58)} ${String(Math.round(performance.now() - t)).padStart(6)} ms  ${(text.length / 1024).toFixed(0).padStart(6)} KB  ${r.status}`);
  return JSON.parse(text || '{}');
}
const ask = (label, body) => timed(label, () => fetch(`${J}/CustomMetadata/Index`, { method: 'POST', headers: H, body: JSON.stringify(body) }));

console.log('— the plugin');
const facets = ['genres', 'tags', 'studios', 'people', 'years', 'state'];
await ask('first request (reads the library)', { ParentId: tv.Id, Type: 'Series', Sort: 'name', Summary: true, Limit: 60 });
await ask('series screen', { ParentId: tv.Id, Type: 'Series', Sort: 'name', Summary: true, Limit: 60 });
const page = await ask('episodes screen', { ParentId: tv.Id, Type: 'Episode', Summary: true, Limit: 60 });
await ask('episodes, a page far down', { ParentId: tv.Id, Type: 'Episode', StartIndex: 9000, Limit: 60 });
await ask('a tag on every item', { Filters: { tags: ['Commune'] }, Summary: true, Limit: 60 });
await ask('all filters of the panel', { Filters: { tags: ['Commune'] }, Facets: facets, FacetLimit: 30 });
await ask('a search in the filters', { Facets: ['genres', 'tags', 'studios', 'people'], FacetQuery: 'sonne 1', FacetLimit: 120 });
console.log(`   ${page.Total} episodes`);
console.log('— Jellyfin');
await timed('the 60 items of a page', () => fetch(`${J}/Items?userId=${U}&Ids=${page.Items.map(i => i.Id).join(',')}&Fields=Tags,DateCreated`, { headers: H }));
await timed('for comparison: the whole library', () => fetch(`${J}/Items?userId=${U}&Recursive=true&IncludeItemTypes=Series,Season,Episode,Movie&Fields=Genres,Tags,Studios,DateCreated`, { headers: H }));
await timed('for comparison: the whole library with its cast', () => fetch(`${J}/Items?userId=${U}&Recursive=true&IncludeItemTypes=Series,Season,Episode,Movie&Fields=Genres,Tags,Studios,DateCreated,People`, { headers: H }));

console.log('— the screens (1440 px)');
const before = await (await fetch(`${J}/System/Configuration/branding`, { headers: H })).json();
await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify({ ...before, CustomCss: '@import url("../CustomMetadata/Theme/projection.css");' }) });
const browser = await chromium.launch({ channel: 'chrome', args: ['--no-sandbox'] });
try {
  const p = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  p.on('pageerror', e => console.log('pageerror:', e.message));
  await p.goto(`${J}/web/#/login`);
  await p.fill('#txtManualName', 'admin'); await p.fill('#txtManualPassword', 'admin'); await p.click('.manualLoginForm button[type=submit]');
  await p.waitForFunction(() => !location.hash.includes('login'));
  await p.evaluate(() => localStorage.setItem('layout', 'experimental')); await p.reload(); await wait(3000);
  const shown = "[...document.querySelectorAll('.page:not(.hide) .pj-ix')].find(r => r.offsetParent !== null)";
  const loaded = `(() => { const r = ${shown}; const s = r && r.querySelector('.pj-ix-under-title p'); return s && !s.textContent.includes('Chargement') && !r.classList.contains('pj-ix-busy') && r.querySelector('.pj-ix-item'); })()`;
  const screen = async (label, act, until = loaded) => {
    const t = Date.now();
    await act();
    await p.waitForFunction(until, null, { timeout: 60000, polling: 50 });
    console.log(`${label.padEnd(58)} ${String(Date.now() - t).padStart(6)} ms`);
  };
  const go = hash => p.evaluate(h => { location.hash = h; }, hash);
  const type = t => `(() => { const r = ${shown}; return r && r.__pjIx.route.type === '${t}' && ${loaded}; })()`;
  await screen('series library', () => go(`#/tv?topParentId=${tv.Id}&collectionType=tvshows`), type('Series'));
  await wait(1000); await p.screenshot({ path: `${outDir}/series.png` });
  await screen('episodes view', () => go(`#/tv?topParentId=${tv.Id}&collectionType=tvshows&tab=5`), type('Episode'));
  await screen('films library', () => go(`#/movies?topParentId=${movies.Id}&collectionType=movies`), type('Movie'));
  await screen('tag list (every item)', () => go(`#/list?type=tag&tag=Commune&serverId=${auth.ServerId}`), `(() => { const r = ${shown}; return r && r.querySelector('h1')?.textContent.includes('Commune') && ${loaded}; })()`);
  await wait(800);
  await screen('open "Tous les filtres"', () => p.click('.page:not(.hide) .pj-ix-open'), "(() => { const b = document.querySelector('.pj-ix-pbody'); return b && !b.classList.contains('pj-ix-busy') && b.querySelector('[data-k]'); })()");
  await p.screenshot({ path: `${outDir}/panel.png` });
  await screen('choose a genre', () => p.click('.pj-ix-pbody [data-k=genres]'));
  await p.keyboard.press('Escape'); await wait(500);
  const cards = () => p.evaluate(`${shown}.querySelectorAll('.pj-ix-item').length`);
  const had = await cards();
  await screen('next page on scroll', () => p.evaluate(() => scrollTo(0, document.documentElement.scrollHeight)), `${shown}.querySelectorAll('.pj-ix-item').length > ${had}`);
  console.log(`   cards: ${had} → ${await cards()}`);
} finally {
  await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify(before) });
  await browser.close();
}
