// Screenshots of the "Genres et tags" page with a realistic amount of terms (1440, 1920, phone).
// Usage: node taxonomy-shots.mjs <base_url> <out_dir> [user] [password]
// Seeds genres, tags and rules on the demo episodes, takes the screenshots, then puts everything back.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node taxonomy-shots.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const wait = ms => new Promise(r => setTimeout(r, ms));

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-taxonomy-shots", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  try { return JSON.parse(text); } catch { return null; }
};
const setTerms = async (id, genres, tags) => {
  const d = await api('GET', `/Users/${auth.User.Id}/Items/${id}`);
  d.Genres = genres; d.GenreItems = genres.map(n => ({ Name: n, Id: '00000000000000000000000000000000' }));
  d.Tags = tags; d.TagItems = tags.map(n => ({ Name: n, Id: '00000000000000000000000000000000' }));
  await api('POST', `/Items/${id}`, d);
};

const GENRES = [
  ['Documentaire', 'Voyage', 'Aventure'], ['documentaire', 'Voyage', 'Cuisine'], ['DOCUMENTAIRE', 'Science', 'Histoire'],
  ['Docu', 'Voyage', 'Nature'], ['Science-fiction', 'Aventure', 'Sponsorisé'], ['Sci-Fi', 'Humour', 'Sponsorisé'],
  ['Histoire', 'Société', 'Interview'], ['Interview', 'Musique', 'Concert'], ['Voyage', 'Nature', 'Un genre au nom vraiment très long pour voir comment la ligne se comporte'],
  ['Cuisine', 'Humour', 'Vlog'], ['Vlog', 'Voyage'], ['Musique', 'Live']
];
const TAGS = [
  ['tech', 'conférence'], ['Tech', 'Perso'], ['sponsorisé', 'tuto'], ['Tuto', '4K'], ['4K', 'HDR'], ['perso', 'famille'],
  ['été 2024', 'voyage'], ['conférence', 'IA'], ['IA', 'tech'], ['making-of'], ['bonus', 'making-of'], ['bonus']
];

const items = ((await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&Fields=Path&userId=${auth.User.Id}`))?.Items || []).filter(e => e.LocationType !== 'Virtual').slice(0, GENRES.length);
const browser = await chromium.launch({ channel: 'chrome' });
try {
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  for (let i = 0; i < items.length; i++) { await setTerms(items[i].Id, GENRES[i], TAGS[i]); }
  await wait(2000);
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [
    { Kind: 'genre', Name: 'Sponsorisé', Ignored: true },
    { Kind: 'genre', Name: 'Docu', ReplaceWith: 'Documentaire' },
    { Kind: 'genre', Name: 'Sci-Fi', ReplaceWith: 'Science-fiction' },
    { Kind: 'genre', Name: 'Western', Created: true },
    { Kind: 'tag', Name: 'sponsorisé', Ignored: true },
    { Kind: 'tag', Name: 'tuto', ReplaceWith: 'Tutoriel' }
  ] });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  page.setDefaultTimeout(60000);
  page.on('pageerror', e => { if (!/pause is not a function/.test(e.message)) console.log('PAGEERROR', e.message.slice(0, 200)); });
  const shot = async (name, full = true) => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f, fullPage: full }); console.log('📸', f); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user); await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  await page.goto(`${J}/web/#/configurationpage?name=CustomMetadataTaxonomy`);
  const T = '#CustomMetadataTaxonomyPage';
  await page.waitForSelector(`${T} .tx-row[data-key]`, { state: 'visible' });
  await wait(800);
  await shot('T1-genres-1440');
  await shot('T1b-genres-1440-fold', false);
  await page.setViewportSize({ width: 1920, height: 1080 });
  await wait(500);
  await shot('T2-genres-1920', false);
  await page.setViewportSize({ width: 1440, height: 1000 });
  // An edit in progress: a rename, an ignore, a selection of two terms
  const row = key => `${T} .tx-row[data-key="${key}"]`;
  const act = async (key, action, value) => {
    await page.click(`${row(key)} [data-act="${action}"]`);
    if (value) { await page.fill(`${row(key)} .tx-target`, value); }
  };
  await act('vlog', 'replace', 'Voyage');
  await act('nature', 'ignore');
  await page.check(`${row('musique')} .tx-check`);
  await page.check(`${row('concert')} .tx-check`);
  await wait(400);
  await shot('T3-editing-1440');
  await page.evaluate(() => window.scrollTo(0, 500));
  await wait(300);
  await shot('T3b-editing-scrolled-1440', false);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.click(`${T} .tx-pill[data-kind="tag"]`);
  await wait(400);
  await shot('T4-tags-1440');
  await page.fill(`${T} #txSearch`, 'zzz');
  await wait(300);
  await shot('T4b-no-match-1440', false);
  await page.fill(`${T} #txSearch`, '');
  await page.click(`${T} .tx-pill[data-kind="genre"]`);
  await page.setViewportSize({ width: 412, height: 900 });
  await wait(600);
  await shot('T5-mobile');
  console.log('horizontal scroll on phone:', await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1));
} catch (e) {
  console.log('ERROR', e.message.split('\n').slice(0, 3).join(' | ')); process.exitCode = 1;
} finally {
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  for (const it of items) { await setTerms(it.Id, [], []); }
  await browser.close();
  console.log('state restored');
}
