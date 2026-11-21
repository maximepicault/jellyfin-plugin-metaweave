// Setting "Téléchargements simultanés" (PluginConfiguration.MaxParallelDownloads): with 1 the downloads of the missing episodes run one at a
// time, with 2 the direct MP4 (served slowly by the mock API) and the HLS stream run together. Restores the configuration and deletes
// the downloaded files.
// Usage: node download-parallel-real.mjs <base_url> <season_dir> [user] [password]
//   season_dir: the host folder of "Saison 1 {dm-b1}"
import fs from 'node:fs';
import path from 'node:path';

const [J, DIR, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !DIR) { console.error('usage: node download-parallel-real.mjs <base_url> <season_dir> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-parallel", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => { const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const downloaded = () => fs.readdirSync(DIR).filter(f => /\{dm-b1v[2-4]\}.*\.(mp4|part)$/.test(f));
const cleanup = () => { for (const f of downloaded()) { fs.unlinkSync(path.join(DIR, f)); } };
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
// The service scans and analyses by itself after the downloads: wait for all of it before the next phase
const settle = async () => {
  for (let round = 0; round < 2; round++) {
    for (let i = 0; i < 80; i++) {
      const d = await api('GET', '/CustomMetadata/Downloads');
      const m = await api('GET', '/CustomMetadata/MissingEpisodes');
      const scanning = (await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State !== 'Idle';
      if (!d.Items.some(x => ['queued', 'downloading', 'converting'].includes(x.Status)) && !m.Running && !scanning) break;
      await wait(3000);
    }
    await wait(8000);
  }
};
const config = async (original, parallel) => {
  const c = JSON.parse(JSON.stringify(original));
  c.MaxParallelDownloads = parallel;
  const source = c.Sources.find(s => s.Prefix === 'dm');
  source.AutoDownloadVideos = true;
  source.EpisodeListMappings = { ...(source.EpisodeListMappings || {}), VideoUrl: 'video.url' };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, c);
};
// Starts the analysis and watches the downloads every 400 ms: the highest number of downloads running at the same time
const run = async () => {
  await api('POST', '/CustomMetadata/Downloads/Clear');
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  let most = 0, seenDone = 0;
  for (let i = 0; i < 300; i++) {
    await wait(400);
    const items = (await api('GET', '/CustomMetadata/Downloads')).Items || [];
    most = Math.max(most, items.filter(x => ['downloading', 'converting'].includes(x.Status)).length);
    seenDone = items.filter(x => x.Status === 'done').length;
    if (seenDone >= 2 && !items.some(x => ['queued', 'downloading', 'converting'].includes(x.Status))) break;
  }
  return { most, seenDone };
};

const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  cleanup(); await scan();

  await config(original, 1);
  const stored = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
  check('the setting is stored', stored.MaxParallelDownloads === 1, JSON.stringify(stored.MaxParallelDownloads));
  const one = await run();
  check('limit 1: never more than one download at a time', one.most === 1 && one.seenDone >= 2, JSON.stringify(one));
  await settle();
  cleanup(); await scan(); await settle();

  await config(original, 2);
  const two = await run();
  check('limit 2: two downloads ran together', two.most === 2 && two.seenDone >= 2, JSON.stringify(two));
  check('limit 2: both files are there, no .part left', downloaded().filter(f => f.endsWith('.mp4')).length === 2 && !downloaded().some(f => f.endsWith('.part')), JSON.stringify(downloaded()));
} finally {
  await settle().catch(() => {});
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  cleanup(); await scan();
}
console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
process.exit(failed ? 1 : 0);
