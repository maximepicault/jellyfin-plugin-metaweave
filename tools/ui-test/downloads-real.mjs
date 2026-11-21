// The "Téléchargements" page in the REAL jellyfin-web, with Playwright (system Google Chrome): empty state, a running download with
// its progress bar, the queue, a failure with its reason, cancel, retry, clear, the tab counter, screenshots at 1440 / 1920 / 412 px.
// Needs the test container of ui-test.sh and its mock API (b1v2 is served slowly on purpose). Restores the plugin configuration and
// deletes the downloaded files.
// Usage: node downloads-real.mjs <base_url> <out_dir> <season_dir> [user] [password]
//   season_dir: the host folder of "Saison 1 {dm-b1}"
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, DIR, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir || !DIR) { console.error('usage: node downloads-real.mjs <base_url> <out_dir> <season_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const D = '#CustomMetadataDownloadsPage';
const wait = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail && !ok ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-downloads", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const rest = async (method, route, body) => { const r = await fetch(J + route, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const downloaded = () => fs.readdirSync(DIR).filter(f => /\{dm-b1v[2-4]\}.*\.(mp4|part)$/.test(f));
const cleanup = () => { for (const f of downloaded()) { fs.unlinkSync(path.join(DIR, f)); } };
const scan = async () => { await rest('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await rest('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
const setOption = async (original, on) => {
  const config = JSON.parse(JSON.stringify(original));
  const source = config.Sources.find(s => s.Prefix === 'dm');
  source.AutoDownloadVideos = on;
  source.EpisodeListMappings = { ...(source.EpisodeListMappings || {}), VideoUrl: 'video.url' };
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, config);
};
const state = async () => rest('GET', '/CustomMetadata/Downloads');
const forget = async () => { await rest('POST', '/CustomMetadata/Downloads/Clear'); };

const original = await rest('GET', `/Plugins/${PLUGIN}/Configuration`);
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  const open = async () => { await page.goto('about:blank'); await page.goto(`${J}/web/#/configurationpage?name=CustomMetadataDownloads`); await page.waitForSelector(`${D} .cmn-nav`, { state: 'visible', timeout: 30000 }); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user);
  await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));

  cleanup(); await scan();
  await forget();

  // ---- 1. nothing enabled: the page says how to turn it on
  await setOption(original, false);
  await open();
  await page.waitForSelector(`${D} .dl-empty`);
  check('empty state without the option: explains where to enable it', (await page.textContent(`${D} .dl-empty`)).includes('activé sur aucune source'));
  check('the tab bar has the Téléchargements tab, current', await page.$eval(`${D} .cmn-tab[aria-current="page"]`, e => e.textContent.includes('Téléchargements')));
  await shot('D1-empty-no-option-1440');

  // ---- 2. option on, nothing queued yet
  await setOption(original, true);
  await open();
  await page.waitForSelector(`${D} .dl-empty [data-act="analyze"]`);
  check('empty state with the option: offers to run the analysis', true);

  // ---- 3. a running download, the queue
  await rest('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  await page.waitForSelector(`${D} .dl-row[data-status="downloading"] [role="progressbar"][aria-valuenow]`, { timeout: 90000 });
  check('running: a progress bar with a percentage', true);
  await page.waitForFunction(sel => document.querySelector(sel + ' .dl-row[data-status="downloading"] .dl-detail')?.textContent.includes('/s'), D, { timeout: 20000 });
  check('running: shows the speed', true);
  check('queued: the next episodes wait', (await page.$$(`${D} .dl-row[data-status="queued"]`)).length >= 1);
  await page.waitForFunction(sel => document.querySelector(sel + ' [data-cmn-count="downloads"]')?.textContent.trim() !== '', D, { timeout: 15000 });
  check('the tab counter shows the pending downloads', Number(await page.textContent(`${D} [data-cmn-count="downloads"]`)) >= 1);
  await shot('D2-running-1440');

  // ---- 4. cancel the running one: its row says cancelled, no .part is left, the queue goes on
  await page.click(`${D} .dl-row[data-status="downloading"] [data-act="cancel"]`);
  await page.waitForSelector(`${D} .dl-row[data-status="cancelled"]`, { timeout: 20000 });
  check('cancel: the row becomes "Annulé"', true);
  check('cancel: the partial file is deleted', !downloaded().some(f => f.endsWith('.part') && f.includes('{dm-b1v2}')), JSON.stringify(downloaded()));
  await page.waitForFunction(sel => document.querySelectorAll(sel + ' .dl-row[data-status="done"]').length >= 1 && document.querySelectorAll(sel + ' .dl-row[data-status="failed"]').length >= 1, D, { timeout: 120000 });
  check('the queue went on: the HLS one is done, the HTML page failed', true);
  const failed = await page.$eval(`${D} .dl-row[data-status="failed"] .dl-why`, e => e.textContent);
  check('failure: the reason is shown in French', failed.includes('ne mène pas à une vidéo'), failed);
  await shot('D3-done-failed-cancelled-1440');

  // ---- 5. filter and retry
  await page.click(`${D} .dl-tile[data-filter="problem"]`);
  check('filter "en échec ou annulés" keeps only those rows', (await page.$$eval(`${D} .dl-row`, rows => rows.map(r => r.getAttribute('data-status')))).every(s => s === 'failed' || s === 'cancelled'));
  await page.click(`${D} .dl-tile[data-filter=""]`);
  await page.click(`${D} .dl-row[data-status="cancelled"] [data-act="retry"]`);
  await page.waitForFunction(sel => ['downloading', 'queued'].some(s => document.querySelector(sel + ` .dl-row[data-status="${s}"]`)), D, { timeout: 20000 });
  check('retry: the cancelled download starts again', true);
  await page.waitForFunction(sel => document.querySelectorAll(sel + ' .dl-row[data-status="done"]').length >= 2, D, { timeout: 120000 });
  check('retry: it finishes', downloaded().some(f => f.includes('{dm-b1v2}') && f.endsWith('.mp4')), JSON.stringify(downloaded()));
  await shot('D4-all-done-1440');
  await page.setViewportSize({ width: 1920, height: 1000 }); await wait(500); await shot('D4-all-done-1920');
  await page.setViewportSize({ width: 412, height: 900 }); await wait(500); await shot('D4-all-done-412');
  check('mobile: no horizontal scroll', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
  await page.setViewportSize({ width: 1440, height: 1000 });

  // ---- 6. clear
  await page.click(`${D} #dlClear`);
  await page.waitForSelector(`${D} .dl-empty`, { timeout: 10000 });
  check('clear: the list is emptied', (await state()).Items.length === 0);
} finally {
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await browser.close();
  cleanup(); await scan();
}
console.log(fail ? `\n${fail} FAILED` : '\nALL PASSED');
process.exit(fail ? 1 : 0);
