// Chapters (time codes) of episodes in the REAL Jellyfin: REST for what Jellyfin holds, Playwright for the editor.
// Usage: node chapters-real.mjs <base_url> <out_dir> [user] [password]
// The mock API gives chapters in several shapes for Talk #1 (text and numeric time codes, one past the end of the 2 s video),
// time codes only in the synopsis for Talk #2, and chapters for the missing Talk #3. Restores the configuration it changes.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node chapters-real.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc', DATA = 'chtest00000001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-chapters", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null };
};
const refreshDm = async () => {
  await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: ['episode'], ReplaceImages: false });
  for (let i = 0; i < 60; i++) { await wait(2000); const s = (await api('GET', '/CustomMetadata/Refresh/Status')).json; if (s && !pick(s, 'Running')) break; }
  await wait(3000);   // the chapters are written once the refresh of each item is over
};
const analyze = async () => {
  const before = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  for (let i = 0; i < 120; i++) {
    await wait(1000);
    const now = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
    if (now && now !== before) { return; }
  }
};
const episodes = async () => (await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&Fields=Chapters,Path,Overview&userId=${auth.User.Id}`)).json?.Items || [];
const marks = item => (item?.Chapters || []).map(c => `${(c.StartPositionTicks / 1e7).toFixed(1)}=${c.Name}`).join(' | ');

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const config = JSON.parse(JSON.stringify(original));
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const dm = config.Sources.find(s => (s.Prefix || '').toLowerCase() === 'dm');
  dm.VideoFieldMappings.ChaptersFromOverview = true;   // Talk #2 has its time codes in the synopsis only
  dm.VideoFieldMappings.ChapterEnd = 'end';            // Talk #1 and #3 give an end for their chapters
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  // ---- API source: an episode with a file
  await refreshDm();
  let eps = await episodes();
  const t1 = eps.find(e => e.Name === 'Talk #1' && (e.Path || '').includes('d1v1'));
  check('API: time codes as text, as a fraction of a second and as "0:01" are read, and the ends close the chapters that stop early', marks(t1) === '0.0=Introduction | 0.4=Hors chapitre | 1.0=Milieu en secondes | 1.5=Fin | 1.7=Hors chapitre', marks(t1));
  check('API: a chapter ending exactly where the next starts gets no closing chapter (1→1.5)', !!t1 && (t1.Chapters || []).filter(c => c.Name === 'Hors chapitre').length === 2 && !marks(t1).includes('1.5=Hors'), marks(t1));
  check('API: a time code past the end of the 2 s video is dropped', !!t1 && !marks(t1).includes('Au-delà'), marks(t1));
  const t2 = eps.find(e => e.Name === 'Talk #2' && (e.Path || '').includes('d1v2'));
  check('API: without a chapters field, the lines of the synopsis starting with a time code are read', marks(t2) === '0.0=Ouverture | 1.0=Suite du texte', marks(t2));
  // A refresh with the media probe wipes chapters Jellyfin does not know: they must come back
  await refreshDm();
  eps = await episodes();
  check('API: chapters are still there after a second full refresh', marks(eps.find(e => e.Name === 'Talk #1' && (e.Path || '').includes('d1v1'))).startsWith('0.0=Introduction'));

  // ---- a missing episode has no video: its time codes are written in the synopsis, once
  await analyze();
  const report = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const missing = (pick(report, 'Seasons') || []).flatMap(s => pick(s, 'Missing') || []).find(e => pick(e, 'Tag') === 'dm-d1v3');
  const virtual = missing ? (await api('GET', `/Items/${pick(missing, 'EpisodeId')}?userId=${auth.User.Id}`)).json : null;
  const overview = virtual?.Overview || '';
  check('missing episode: the time codes are appended to the synopsis with their ends (754 s → 12:34)', overview.includes('Repères\n0:00–12:34 Début\n12:34–1:02:03 Le débat\n1:02:03 Conclusion'), overview.slice(-90));
  check('missing episode: appended once, not on every analysis', (overview.match(/Repères/g) || []).length === 1);

  // ---- local source: chapters typed in the editor, bare seconds and unit forms included
  dm.EpisodeListEndpoint = '';
  config.Sources = config.Sources.filter(s => s.Prefix !== 'ov');
  config.Sources.push({ Name: 'Repères', Prefix: 'ov', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  const put = await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Seasons: [{ Id: 'dm-a1', Title: 'Saison 2024',
    Episodes: [{ Id: 'ch1', Title: 'Épisode avec repères', IndexNumber: 9, Chapters: '0:00-0:30 Introduction\\n359 La crevasse\\n6m12s Suite\\n1h02m03s Fin'.replaceAll('\\n', '\n') }] }] }] });
  check('local: the typed chapters are saved with the episode', put.status === 200 && JSON.stringify(put.json).includes('La crevasse'), String(put.status));
  await analyze();
  const rep2 = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const localMissing = (pick(rep2, 'Seasons') || []).flatMap(s => pick(s, 'Missing') || []).find(e => pick(e, 'Tag') === 'ov-ch1');
  const localVirtual = localMissing ? (await api('GET', `/Items/${pick(localMissing, 'EpisodeId')}?userId=${auth.User.Id}`)).json : null;
  check('local: a range (0:00-0:30), seconds (359), "6m12s" and "1h02m03s" are read', (localVirtual?.Overview || '').includes('Repères\n0:00–0:30 Introduction\n5:59 La crevasse\n6:12 Suite\n1:02:03 Fin'), (localVirtual?.Overview || '').slice(-120));

  // ---- the editor, in the real jellyfin-web
  const context = await browser.newContext({ viewport: { width: 1440, height: 1300 } });
  const page = await context.newPage();
  page.on('pageerror', e => { if (!/pause is not a function/.test(e.message)) console.log('PAGEERROR', e.message.slice(0, 200)); });
  page.setDefaultTimeout(60000);
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user); await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  await page.goto(`${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`);
  const C = '#CustomMetadataConfigPage';
  await page.waitForSelector(`${C} #sourcesList .cm-card`, { state: 'visible' });
  await page.evaluate(() => { window.__cmdAnswer = 'ok'; new MutationObserver(() => { const b = document.querySelector('.cmd-backdrop [data-cmd=ok]'); if (b) b.click(); }).observe(document.documentElement, { childList: true, subtree: true }); });
  // the API source: mapping fields, filled from the configuration
  await page.evaluate(() => { const c = Array.from(document.querySelectorAll('#CustomMetadataConfigPage .cm-card')).find(x => x.textContent.includes('Démo API')); c.querySelector('.raised').click(); });
  await page.click(`${C} [data-go="video"]`);
  check('editor: the chapters mapping is filled (list, time, title)', (await page.$eval(`${C} #srcVideoChapters`, e => e.value)) === 'chapters' && (await page.$eval(`${C} #srcVideoChapterTime`, e => e.value)) === 'time' && (await page.$eval(`${C} #srcVideoChapterTitle`, e => e.value)) === 'title');
  check('editor: the optional end field shows the saved path', (await page.$eval(`${C} #srcVideoChapterEnd`, e => e.value)) === 'end');
  check('editor: the option "read the chapters in the synopsis" shows the saved choice', await page.$eval(`${C} #srcVideoChaptersFromOverview`, e => e.checked === true));
  await page.$eval(`${C} #srcVideoChapters`, e => e.scrollIntoView({ block: 'center' })); await page.waitForTimeout(400);
  await shot('C1-mapping-1440');
  await page.selectOption(`${C} #srcVideoChapterUnit`, 'milliseconds');
  await page.click(`${C} .cm-savebar button[type=submit]`);
  await page.waitForSelector(`${C} #listView:not([hidden])`);
  await page.waitForTimeout(1500);
  const saved = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json.Sources.find(s => s.Prefix === 'dm').VideoFieldMappings;
  check('editor: the unit and the option are saved with the source', saved.ChapterTimeUnit === 'milliseconds' && saved.ChaptersFromOverview === true && saved.ChaptersArray === 'chapters', JSON.stringify([saved.ChapterTimeUnit, saved.ChaptersFromOverview]));
  // the local source: the field of the episode form
  await page.evaluate(() => { const c = Array.from(document.querySelectorAll('#CustomMetadataConfigPage .cm-card')).find(x => x.textContent.includes('Repères')); c.querySelector('.raised').click(); });
  await page.click(`${C} [data-go="content"]`);
  await page.click(`${C} #localTree .cm-t-item:has-text("Épisode avec repères")`);
  check('editor: the episode form has its chapters, one per line', (await page.$eval(`${C} #lf-Chapters`, e => e.value)).split('\n').length === 4);
  await page.fill(`${C} #lf-Chapters`, '0:00 Nouveau départ\n90 Rebondissement');
  await page.$eval(`${C} #lf-Chapters`, e => e.scrollIntoView({ block: 'center' })); await page.waitForTimeout(400);
  await shot('C2-local-episode-1440');
  await page.click(`${C} .cm-savebar button[type=submit]`);
  await page.waitForSelector(`${C} #listView:not([hidden])`);
  await page.waitForTimeout(1500);
  const stored = (await api('GET', `/CustomMetadata/Local/${DATA}`)).json;
  check('editor: what is typed is stored with the episode', JSON.stringify(stored).includes('Rebondissement') && JSON.stringify(stored).includes('Nouveau départ'));
  await page.setViewportSize({ width: 412, height: 1500 });
  check('phone: no horizontal scroll on the configuration page', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
} catch (e) {
  console.log('ERROR', e.message.split('\n').slice(0, 3).join(' | ')); fail++;
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  await analyze();
  console.log(`\n${pass} PASS, ${fail} FAIL\nconfiguration restored`);
  await browser.close();
  process.exit(fail ? 1 : 0);
}
