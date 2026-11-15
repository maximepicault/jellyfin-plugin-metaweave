// Ignore a missing episode, compact list, in-page confirmation, merge of two people, setup guide: in the REAL jellyfin-web,
// with Playwright (system Google Chrome). Needs the test container of ui-test.sh. Restores what it changes.
// Usage: node improvements-real.mjs <base_url> <out_dir> [user] [password]
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node improvements-real.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });

let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };
const C = '#CustomMetadataConfigPage', M = '#CustomMetadataMissingPage', P = '#CustomMetadataPeoplePage';
const url = name => `${J}/web/#/configurationpage?name=${name}`;

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-improvements", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const rest = async (method, route, body) => { const r = await fetch(J + route, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };

const originalPeople = await rest('GET', '/CustomMetadata/People');
const originalCast = await rest('GET', '/CustomMetadata/People/EpisodeCast');
const browser = await chromium.launch({ channel: 'chrome' });
let ignoredTag = null;
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 } });
  const page = await context.newPage();
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  const open = async (name, root) => { await page.goto('about:blank'); await page.goto(url(name)); await page.waitForSelector(`${root} .cmn-nav`, { state: 'visible', timeout: 30000 }); };
  const text = sel => page.$eval(sel, e => e.textContent.trim());

  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user);
  await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));

  // ---- 1. ignore a missing episode, take it back
  await open('CustomMetadataMissingEpisodes', M);
  await page.waitForSelector(`${M} .me-card`, { state: 'visible' });
  await page.waitForFunction(sel => document.querySelector(sel).textContent !== '', `${M} [data-cmn-count="missing"]`);
  const before = Number(await text(`${M} [data-cmn-count="missing"]`));
  await page.click(`${M} [data-series]`);   // the most incomplete series: a single source, straight to its episodes
  await page.waitForSelector(`${M} .me-episode`, { state: 'visible' });
  const cards = await page.$$eval(`${M} .me-episode`, els => els.length);
  ignoredTag = await page.$eval(`${M} [data-ignore]`, e => e.getAttribute('data-ignore'));
  await page.click(`${M} [data-ignore]`);
  await page.waitForFunction(([sel, n]) => document.querySelectorAll(sel + ' .me-episode').length === n - 1, [M, cards]);
  check('ignore: the episode leaves the list', (await page.$$eval(`${M} [data-ignore]`, els => els.map(e => e.getAttribute('data-ignore')))).indexOf(ignoredTag) < 0, ignoredTag);
  check('ignore: listed apart, with a way back', (await text(`${M} .me-ignored summary`)) === '1 épisode ignoré' && (await page.$$eval(`${M} [data-restore]`, els => els.length)) === 1);
  await page.waitForFunction(([sel, n]) => document.querySelector(sel).textContent === String(n - 1), [`${M} [data-cmn-count="missing"]`, before]);
  check('ignore: the tab counter goes down by one', Number(await text(`${M} [data-cmn-count="missing"]`)) === before - 1, `${before} → ${await text(`${M} [data-cmn-count="missing"]`)}`);
  const report = await rest('GET', '/CustomMetadata/MissingEpisodes');
  check('ignore: stored in the report (Ignored, not Missing)', report.Report.Seasons.some(s => (s.Ignored || []).some(e => e.Tag === ignoredTag)) && !report.Report.Seasons.some(s => (s.Missing || []).some(e => e.Tag === ignoredTag)));
  await page.click(`${M} .me-ignored summary`);
  await shot('I1-ignored-1440');

  // ---- 2. compact list
  await page.click(`${M} #viewToggle [data-view="list"]`);
  check('list view: one line per episode', await page.$eval(`${M} .me-episodes`, e => e.classList.contains('me-list')) && (await page.$eval(`${M} .me-episode`, e => e.getBoundingClientRect().height)) < 140);
  await shot('I2-list-view-1440');
  await page.click(`${M} #viewToggle [data-view="cards"]`);
  await page.click(`${M} .me-ignored summary`);   // the page was redrawn: the block is closed again
  await page.click(`${M} [data-restore]`);
  await page.waitForFunction(([sel, n]) => document.querySelectorAll(sel + ' .me-episode').length === n, [M, cards]);
  check('restore: the episode is back and the counter too', Number(await text(`${M} [data-cmn-count="missing"]`)) === before || await page.waitForFunction(([sel, n]) => document.querySelector(sel).textContent === String(n), [`${M} [data-cmn-count="missing"]`, before]).then(() => true));

  // ---- 3. in-page confirmation instead of the browser's
  let nativeDialog = false;
  page.on('dialog', d => { nativeDialog = true; d.dismiss(); });
  await page.click(`${M} #btnAnalyzeFull`);
  await page.waitForSelector(`${M} .cmd-box`, { state: 'visible' });
  check('confirm: an in-page box, not a browser dialog', !nativeDialog && (await text(`${M} .cmd-box h3`)) === 'Réanalyser sans cache ?');
  check('confirm: the action button names the action', (await text(`${M} .cmd-box [data-cmd="ok"]`)) === 'Réanalyser');
  await shot('I3-confirm-1440');
  await page.keyboard.press('Escape');
  await page.waitForSelector(`${M} .cmd-box`, { state: 'detached' });
  const running = (await rest('GET', '/CustomMetadata/MissingEpisodes')).Running;
  check('confirm: Escape cancels, nothing is started', running === false);

  // ---- 4. merge two people (same name), the cast follows
  await rest('PUT', '/CustomMetadata/People', { Version: 1, People: [
    { Id: 'mrgaa1', Name: 'Alix Fusion', Bio: '', Links: [] },
    { Id: 'mrgaa2', Name: 'Alix Fusion', Bio: 'Bio de la seconde fiche', Links: [{ Prefix: 'pe', ExternalId: 'p9' }] }] });
  await rest('PUT', '/CustomMetadata/People/EpisodeCast', { Episodes: [{ Type: 'episode', Key: 'dm-a1v1', Cast: [{ PersonId: 'mrgaa2', Role: 'Guide', Type: 'Actor' }] }] });
  await open('CustomMetadataPeople', P);
  await page.waitForSelector(`${P} .pp-item[data-person]`, { state: 'visible' });
  await page.click(`${P} .pp-item[data-person]`);
  await page.waitForSelector(`${P} .pp-merge`, { state: 'visible' });
  check('merge: offered, the entry with the same name comes first', (await text(`${P} #ppMergeWith option`)).includes('même nom'));
  await page.$eval(`${P} .pp-merge`, e => e.scrollIntoView({ block: 'center' }));
  await shot('I4-merge-block-1440');
  const keptId = (await rest('GET', '/CustomMetadata/People')).People[0].Id;
  await page.click(`${P} [data-merge]`);
  await page.waitForSelector(`${P} .cmd-box`, { state: 'visible' });
  await page.click(`${P} .cmd-box [data-cmd="ok"]`);
  await page.waitForSelector(`${P} #ppMergeDone`, { state: 'visible', timeout: 20000 });
  check('merge: the result is announced', (await text(`${P} #ppMergeDone`)).includes('1 distribution mise à jour'), await text(`${P} #ppMergeDone`));
  const people = (await rest('GET', '/CustomMetadata/People')).People;
  const kept = people.find(p => p.Id === keptId);
  check('merge: one entry left, with the other\'s identifier and biography', people.length === 1 && !!kept && (kept.Links || []).some(l => l.ExternalId === 'p9') && kept.Bio === 'Bio de la seconde fiche', JSON.stringify(people.map(p => p.Id)));
  const cast = await rest('GET', '/CustomMetadata/People/EpisodeCast');
  check('merge: the cast now names the kept entry', (cast.Episodes || []).some(e => e.Key === 'dm-a1v1' && e.Cast.length === 1 && e.Cast[0].PersonId === keptId), JSON.stringify(cast.Episodes));
  check('merge: "Appliquer aux épisodes" is offered', await page.isVisible(`${P} #ppApplyBtn`));
  await page.waitForTimeout(600);   // jellyfin-web upgrades the emby-* elements of a redrawn form a moment later
  await shot('I5-merged-1440');

  // ---- 5. setup guide: everything is in place in the test container, so it stays out of the way
  await open('Custom%20Metadata%20Provider', C);
  await page.waitForSelector(`${C} #sourcesList .cm-card`, { state: 'visible' });
  await page.waitForFunction(sel => document.querySelector(sel).textContent !== '', `${C} [data-cmn-count="sources"]`);
  await page.waitForTimeout(800);
  check('setup guide: hidden once every step is done', !(await page.isVisible(`${C} #setupGuide`)));
} catch (e) {
  console.log('ERROR', e.message.split('\n')[0]); fail++;
} finally {
  if (ignoredTag) { await rest('POST', '/CustomMetadata/MissingEpisodes/Ignore', { Tag: ignoredTag, Ignored: false }); }
  // Put the directory and the casts back (directory first: a cast naming an unknown person is dropped)
  await rest('PUT', '/CustomMetadata/People', originalPeople);
  await rest('PUT', '/CustomMetadata/People/EpisodeCast', originalCast);
  console.log(`\n${pass} PASS, ${fail} FAIL`);
  await browser.close();
  process.exit(fail ? 1 : 0);
}
