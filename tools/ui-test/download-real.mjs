// Automatic download of the videos of missing episodes (source option AutoDownloadVideos + EpisodeListMapping.VideoUrl).
// The mock API offers, for the season "Cuisine du monde / Saison 1": a direct MP4 served as application/octet-stream (b1v2),
// an HLS playlist with MPEG-TS segments (b1v3) and an HTML page that is not a video (b1v4).
// Expected: b1v2 and b1v3 end up as "E{n} {title} {dm-id}.mp4" in the season folder (the HLS one converted to a real MP4 by ffmpeg),
// b1v4 is refused, and once the scan has run the two downloaded episodes are no longer reported as missing.
// Usage: node download-real.mjs <base_url> <season_dir> [user] [password]
//   season_dir: the host folder of "Saison 1 {dm-b1}" (the container must be able to write in it)
// Restores the plugin configuration and removes the downloaded files. Prints PASS / FAIL.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const [J, DIR, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !DIR) { console.error('usage: node download-real.mjs <base_url> <season_dir> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-download", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => { const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const analyze = async () => {
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  for (let i = 0; i < 120; i++) { await wait(1500); const s = await api('GET', '/CustomMetadata/MissingEpisodes'); if (s && s.Running === false && s.Report) return s.Report; }
  return null;
};
const missingTags = report => (report?.Seasons || []).filter(s => s.SourceItemId === 'dm-b1').flatMap(s => (s.Missing || []).map(m => m.Tag));
const downloaded = () => fs.readdirSync(DIR).filter(f => /\{dm-b1v[2-4]\}.*\.(mp4|part)$/.test(f));
const cleanup = () => { for (const f of downloaded()) fs.rmSync(path.join(DIR, f), { force: true }); };
// After the files are removed the library still holds their episodes: a scan drops them
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
const probe = file => JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_type:format=format_name', '-of', 'json', file]).toString());

const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  cleanup();
  await scan();
  const config = JSON.parse(JSON.stringify(original));
  const source = config.Sources.find(s => s.Prefix === 'dm');
  source.AutoDownloadVideos = true;
  source.EpisodeListMappings = { ...(source.EpisodeListMappings || {}), VideoUrl: 'video.url' };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  const stored = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).Sources.find(s => s.Prefix === 'dm');
  check('config stored: option on and VideoUrl mapping kept', stored.AutoDownloadVideos === true && stored.EpisodeListMappings.VideoUrl === 'video.url', JSON.stringify(stored.EpisodeListMappings));

  const first = await analyze();
  check('analysis lists the three missing episodes', ['dm-b1v2', 'dm-b1v3', 'dm-b1v4'].every(t => missingTags(first).includes(t)), JSON.stringify(missingTags(first)));

  let files = [];
  for (let i = 0; i < 60; i++) { files = downloaded().filter(f => !f.endsWith('.part')); if (files.length >= 2) break; await wait(2000); }
  const direct = files.find(f => f.includes('{dm-b1v2}')), hls = files.find(f => f.includes('{dm-b1v3}'));
  check('direct MP4 downloaded as "E{n} {title} {tag}.mp4"', direct === 'E02 Le Japon {dm-b1v2}.mp4', JSON.stringify(files));
  check('HLS playlist converted to "E{n} {title} {tag}.mp4"', hls === 'E03 Le Pérou {dm-b1v3}.mp4', JSON.stringify(files));
  check('the HTML page was refused (no file for b1v4)', !downloaded().some(f => f.includes('{dm-b1v4}')), JSON.stringify(downloaded()));
  check('no .part file left behind', !downloaded().some(f => f.endsWith('.part')), JSON.stringify(downloaded()));
  for (const f of [direct, hls].filter(Boolean)) {
    const info = probe(path.join(DIR, f));
    check(`${f} is a real MP4 with video and audio`, /mp4/.test(info.format.format_name) && info.streams.some(s => s.codec_type === 'video') && info.streams.some(s => s.codec_type === 'audio'), JSON.stringify(info));
  }

  // The scan the service requested turns the files into real episodes: the next analysis no longer reports them
  let second = null, still = [];
  for (let i = 0; i < 8; i++) {
    await wait(10000);
    second = await analyze();
    still = missingTags(second);
    if (!still.includes('dm-b1v2') && !still.includes('dm-b1v3')) break;
  }
  check('after the scan, the downloaded episodes are no longer missing', !still.includes('dm-b1v2') && !still.includes('dm-b1v3') && still.includes('dm-b1v4'), JSON.stringify(still));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  cleanup();
  await scan();
}
console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
process.exit(failed ? 1 : 0);
