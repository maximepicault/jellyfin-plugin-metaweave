// Preview video of a local episode (uploaded file or link) in the REAL Jellyfin: REST for the server side, Playwright for the editor.
// Usage: node local-preview-video-real.mjs <base_url> <out_dir> [user] [password]   (needs ffmpeg on the host to make the test video)
// Seeds a local source "ov" with a declared episode of the season "dm-a1" (it becomes a virtual episode), uploads a video,
// checks the refusals, the ranged anonymous playback, that a REAL episode named by a local episode gets that video as its
// hover preview (over the API's), the trailer Jellyfin creates and that it probes (duration), then
// the editor (player, link, "Retirer"). Restores the configuration and deletes the local data.
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node local-preview-video-real.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc', DATA = 'pvtest00000001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };

const video = path.join(outDir, 'preview-test.mp4');
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=3:size=320x180:rate=15', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3',
  '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', video]);
const bytes = fs.readFileSync(video);

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-preview", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const AUTH = `MediaBrowser Token="${auth.AccessToken}"`;
const H = { 'Content-Type': 'application/json', Authorization: AUTH };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null };
};
const upload = async (body, node = 'pv1') => {
  const r = await fetch(`${J}/CustomMetadata/Local/${DATA}/Video?node=${node}`, { method: 'POST', headers: { 'Content-Type': 'application/octet-stream', Authorization: AUTH }, body });
  const text = await r.text();
  return { status: r.status, text, json: (() => { try { return JSON.parse(text); } catch { return null; } })() };
};
const analyze = async () => {
  const before = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  for (let i = 0; i < 120; i++) {
    await wait(1000);
    const now = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
    if (now && now !== before) { return; }
  }
  console.log('⚠️ the analysis did not finish in time');
};

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const config = JSON.parse(JSON.stringify(original));
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const dm = config.Sources.find(s => (s.Prefix || '').toLowerCase() === 'dm');
  config.Sources = config.Sources.filter(s => s.Prefix !== 'ov');
  config.Sources.push({ Name: 'Aperçus', Prefix: 'ov', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  // ---- server side: refusals, then the real file
  const notVideo = await upload(Buffer.from('ceci n\'est pas une vidéo, juste du texte assez long'));
  check('upload: a text file is refused', notVideo.status === 400 && notVideo.text.includes('Format non reconnu'), `${notVideo.status} ${notVideo.text}`);
  const empty = await upload(Buffer.alloc(0));
  check('upload: an empty body is refused', empty.status === 400, `${empty.status} ${empty.text}`);
  const ok = await upload(bytes);
  const url = ok.json?.Url || ok.json?.url;
  check('upload: an MP4 is accepted, the address is local://video/…', ok.status === 200 && /^local:\/\/video\/pvtest00000001\/pv1-[0-9a-f]{6}\.mp4$/.test(url || ''), `${ok.status} ${ok.text}`);
  const file = (url || '').split('/').pop();
  const plain = await fetch(`${J}/CustomMetadata/Local/${DATA}/Video/${file}`);   // no token: Jellyfin and the editor's player have none
  check('playback: served without a token, as video/mp4', plain.status === 200 && plain.headers.get('content-type') === 'video/mp4' && (await plain.arrayBuffer()).byteLength === bytes.length);
  const ranged = await fetch(`${J}/CustomMetadata/Local/${DATA}/Video/${file}`, { headers: { Range: 'bytes=0-99' } });
  check('playback: a Range request gets 206 (seeking works)', ranged.status === 206 && (await ranged.arrayBuffer()).byteLength === 100, String(ranged.status));
  check('playback: an unknown file or a path trick is a 404', (await fetch(`${J}/CustomMetadata/Local/${DATA}/Video/nope.mp4`)).status === 404 && (await fetch(`${J}/CustomMetadata/Local/${DATA}/Video/..%2Fpvtest00000001.json`)).status === 404);

  // Above ASP.NET's default body limit (30 MB): the attribute on the route must really lift it
  const big = await upload(Buffer.concat([bytes, Buffer.alloc(45 * 1024 * 1024)]), 'big');
  const bigUrl = big.json?.Url;
  check('upload: a 45 MB file goes through (the default 30 MB limit is lifted)', big.status === 200 && !!bigUrl, `${big.status} ${big.text.slice(0, 120)}`);
  if (bigUrl) {
    const head = await fetch(`${J}/CustomMetadata/Local/${DATA}/Video/${bigUrl.split('/').pop()}`, { headers: { Range: 'bytes=0-9' } });
    check('playback: the big file is served too', head.status === 206);
  }

  // ---- a REAL episode (it has its file) named by a local episode: the hover preview is the uploaded video (API list kept)
  const items = (await api('GET', `/Items?userId=${auth.User.Id}&IncludeItemTypes=Episode&Recursive=true&IsMissing=false&Fields=Path`)).json;
  const tagged = (pick(items, 'Items') || []).map(e => ({ id: pick(e, 'Id'), tag: (/\{(dm-[\w-]+)\}[^/]*$/.exec(pick(e, 'Path') || '') || [])[1] })).filter(e => e.tag);
  const real = tagged.find(e => e.tag === 'dm-a3v1') || tagged[0];   // a3v1 has a preview in the API: the local one must win
  check('real episode: one tagged file found in the demo library', !!real, String(tagged.length));
  if (real) {
    await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'pvs', Title: 'Série', Seasons: [{ Id: 'pvx', Title: 'Saison', Episodes: [{ Id: real.tag, PreviewUrl: url }] }] }] });
    const info = (await api('GET', `/CustomMetadata/Preview/${real.id}/Info`)).json;
    check(`real episode ${real.tag}: its preview comes from the local source`, pick(info, 'Source') === true && pick(info, 'SourceIsLocal') === true, JSON.stringify(info));
    const hover = await fetch(`${J}/CustomMetadata/Preview/${real.id}`, { headers: { Authorization: AUTH } });
    const body = Buffer.from(await hover.arrayBuffer());
    check(`real episode ${real.tag}: the hover preview is the uploaded video`, hover.status === 200 && body.equals(bytes), `${hover.status}, ${body.length} bytes (expected ${bytes.length})`);
    await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'pvs', Title: 'Série', Seasons: [{ Id: 'pvx', Title: 'Saison', Episodes: [{ Id: real.tag }] }] }] });
    const after = (await api('GET', `/CustomMetadata/Preview/${real.id}/Info`)).json;
    check(`real episode ${real.tag}: preview removed locally → no longer the local one, at once`, pick(after, 'SourceIsLocal') === false, JSON.stringify(after));
  }

  // ---- the episode becomes a virtual episode whose preview is the uploaded file
  dm.EpisodeListEndpoint = '';   // no API list: the local season's list is taken as is
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  const put = await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Seasons: [{ Id: 'dm-a1', Title: 'Saison 2024',
    Episodes: [{ Id: 'pv1', Title: 'Épisode avec aperçu', IndexNumber: 9, PreviewUrl: url }] }] }] });
  check('save: the episode keeps its local video address', put.status === 200 && JSON.stringify(put.json).includes(url), String(put.status));
  await analyze();
  const report = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const entry = (pick(report, 'Seasons') || []).flatMap(s => pick(s, 'Missing') || []).find(e => pick(e, 'Tag') === 'ov-pv1');
  check('missing: the declared episode is listed', !!entry, JSON.stringify((pick(report, 'Seasons') || []).map(s => (pick(s, 'Missing') || []).map(e => pick(e, 'Tag'))).flat()));
  const trailers = entry ? (await api('GET', `/Users/${auth.User.Id}/Items/${pick(entry, 'EpisodeId')}/LocalTrailers`)).json : null;
  const trailer = Array.isArray(trailers) ? trailers[0] : null;
  check('trailer: the virtual episode got one, read through this server', !!trailer && /\/CustomMetadata\/Local\/pvtest00000001\/Video\/pv1-[0-9a-f]{6}\.mp4$/.test(trailer.Path || ''), JSON.stringify(trailers)?.slice(0, 200));
  check('trailer: Jellyfin probed the file (duration ≈ 3 s)', !!trailer && trailer.RunTimeTicks > 25_000_000 && trailer.RunTimeTicks < 35_000_000, String(trailer?.RunTimeTicks));
  const sources = entry ? (await api('GET', `/Items/${pick(entry, 'EpisodeId')}/PlaybackInfo?userId=${auth.User.Id}`)).json : null;
  check('playback: the virtual episode itself offers the preview as its source', (pick(sources, 'MediaSources') || []).length > 0, JSON.stringify(sources)?.slice(0, 160));

  // ---- the editor, in the real jellyfin-web
  const context = await browser.newContext({ viewport: { width: 1440, height: 1200 } });
  const page = await context.newPage();
  page.setDefaultTimeout(60000);   // the first load after a configuration change can be slow
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user); await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  await page.goto(`${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`);
  const C = '#CustomMetadataConfigPage';
  await page.waitForSelector(`${C} #sourcesList .cm-card`, { state: 'visible', timeout: 120000 });
  await page.evaluate(() => { const c = Array.from(document.querySelectorAll('#CustomMetadataConfigPage .cm-card')).find(x => x.textContent.includes('Aperçus')); c.querySelector('.raised').click(); });
  await page.click(`${C} [data-go="content"]`);
  await page.click(`${C} #localTree .cm-t-item:has-text("Épisode avec aperçu")`);
  await page.click(`${C} [data-ltab-btn="images"]`);
  await page.waitForSelector(`${C} .cm-vidprev video`, { state: 'visible' });
  check('editor: the uploaded video has a player', (await page.$eval(`${C} .cm-vidprev video`, v => v.getAttribute('src'))).includes(`/Video/${file}`));
  check('editor: the link field says a file was sent, and is disabled', await page.$eval(`${C} #lf-PreviewUrl`, e => e.disabled && e.placeholder === 'Fichier envoyé au plugin'));
  await page.waitForFunction(sel => { const v = document.querySelector(sel); return v && v.readyState >= 1 && v.duration > 2; }, `${C} .cm-vidprev video`, { timeout: 15000 });
  check('editor: the browser can play it (duration read)', true);
  await page.$eval(`${C} .cm-vidfield`, e => e.scrollIntoView({ block: 'center' })); await page.waitForTimeout(500);
  await shot('V1-editor-uploaded-1440');
  // upload from the editor with the file picker
  await page.click(`${C} [data-clear-video]`);
  check('editor: "Retirer" empties the field', (await page.$eval(`${C} #lf-PreviewUrl`, e => !e.disabled && e.value === '')) && !(await page.$(`${C} .cm-vidprev video`)));
  await page.setInputFiles(`${C} [data-vfile]`, video);
  await page.waitForSelector(`${C} .cm-vidprev video`, { state: 'visible', timeout: 120000 });
  check('editor: choosing a file uploads it and shows the player', (await page.$eval(`${C} #lf-PreviewUrl`, e => e.disabled)) && (await page.$eval(`${C} #lfVideoMsg`, e => e.textContent)).includes('Vidéo envoyée'));
  const refused = path.join(outDir, 'not-a-video.mp4'); fs.writeFileSync(refused, 'not a video at all, only text');
  await page.setInputFiles(`${C} [data-vfile]`, refused);
  await page.waitForFunction(sel => document.querySelector(sel).textContent.includes('Format non reconnu'), `${C} #lfVideoMsg`, { timeout: 15000 });
  check('editor: a file that is not a video says why', true);
  await page.click(`${C} [data-clear-video]`);
  await page.fill(`${C} #lf-PreviewUrl`, 'https://example.com/preview.mp4');
  check('editor: a link can be typed instead', (await page.$eval(`${C} #lf-PreviewUrl`, e => e.value)) === 'https://example.com/preview.mp4');
  await shot('V2-editor-link-1440');
  await page.setViewportSize({ width: 412, height: 1500 }); await page.waitForTimeout(500);
  check('phone: no horizontal scroll', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
  await page.$eval(`${C} .cm-vidfield`, e => e.scrollIntoView({ block: 'center' })); await shot('V3-editor-mobile');
  await page.evaluate(() => { window.__cmdAnswer = 'ok'; new MutationObserver(() => { const b = document.querySelector('.cmd-backdrop [data-cmd=ok]'); if (b) b.click(); }).observe(document.documentElement, { childList: true, subtree: true }); });
  await page.click(`${C} #btnCancelEdit`);

  // ---- the preview's life: kept by a new analysis and a library scan, replaced with the address, removed with it
  if (entry && trailer) {
    const episodeId = pick(entry, 'EpisodeId');
    const trailersOf = async () => (await api('GET', `/Users/${auth.User.Id}/Items/${episodeId}/LocalTrailers`)).json || [];
    const exists = async id => ((await api('GET', `/Items?userId=${auth.User.Id}&Ids=${id}`)).json?.Items || []).length > 0;
    const ids = list => list.map(t => t.Id).join(',');
    await analyze();
    let now = await trailersOf();
    check('life: a second full analysis keeps the same single preview', ids(now) === trailer.Id, ids(now));
    await api('POST', '/Library/Refresh');
    for (let i = 0; i < 120; i++) { await wait(2000); const tasks = (await api('GET', '/ScheduledTasks?isHidden=false')).json || []; if (!tasks.some(t => t.State === 'Running')) { break; } }
    now = await trailersOf();
    check('life: a library scan keeps it', ids(now) === trailer.Id && await exists(trailer.Id), ids(now));
    const seed = preview => api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Seasons: [{ Id: 'dm-a1', Title: 'Saison 2024',
      Episodes: [{ Id: 'pv1', Title: 'Épisode avec aperçu', IndexNumber: 9, PreviewUrl: preview }] }] }] });
    const other = 'http://172.17.0.1:8765/previews/a3v1.mp4';   // the mock API's preview video
    await seed(other); await analyze();
    now = await trailersOf();
    check('life: a changed address replaces the preview', now.length === 1 && now[0].Path === other && now[0].Id !== trailer.Id, JSON.stringify(now.map(t => t.Path)));
    check('life: the old preview item is deleted', !(await exists(trailer.Id)));
    const second = now[0]?.Id;
    await seed(''); await analyze();
    now = await trailersOf();
    check('life: no address, no preview left', now.length === 0 && !!second && !(await exists(second)), ids(now));
    const none = (await api('GET', `/Items/${episodeId}/PlaybackInfo?userId=${auth.User.Id}`)).json;
    check('life: the episode no longer offers a video source', !(pick(none, 'MediaSources') || []).some(m => m.Name === 'Aperçu'), JSON.stringify(pick(none, 'MediaSources'))?.slice(0, 160));
    await seed(url); await analyze();
    const last = (await trailersOf())[0]?.Id;
    // another episode takes its place (an EMPTY list removes nothing: it is not trusted)
    await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Seasons: [{ Id: 'dm-a1', Title: 'Saison 2024',
      Episodes: [{ Id: 'pv2', Title: 'Épisode de remplacement', IndexNumber: 10 }] }] }] });
    await analyze();
    const left = [await exists(episodeId), !!last && await exists(last)];
    check('life: the episode removed from the source takes its preview with it', !!last && !left[0] && !left[1], `episode still there: ${left[0]}, preview ${last} still there: ${left[1]}`);
  }
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
