// Option "Recadrer les posters paysage en portrait 2:3" with LOCAL sources, in the REAL Jellyfin (REST only):
//  A. a series that belongs to a local source: its uploaded landscape poster reaches Jellyfin as a 720x1080 crop;
//  B. a local series that overrides an API series (tag "dm-aventuriers"): the option of the LOCAL source is the one that counts.
// Restores the configuration, deletes the local data and the test folder.
// Usage: node poster-crop-local-real.mjs <series_media_dir> <base_url> [user] [password]
//   series_media_dir: the host folder mounted as /media/series (~/.cache/cm-ui-test/media/series)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const [SERIES_DIR, J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!SERIES_DIR || !J) { console.error('usage: node poster-crop-local-real.mjs <series_media_dir> <base_url> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-posterlocal", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body, raw) => {
  const r = await fetch(J + url, { method, headers: raw ? { Authorization: H.Authorization } : H, body: body === undefined ? undefined : (raw ? body : JSON.stringify(body)) });
  const t = await r.text(); try { return JSON.parse(t); } catch { return t; }
};
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
const refresh = async id => { await api('POST', `/Items/${id}/Refresh?Recursive=false&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=false&ReplaceAllMetadata=false`); await wait(8000); };
const size = async id => {
  const r = await fetch(`${J}/Items/${id}/Images/Primary`, { headers: H });
  if (!r.ok) return { w: 0, h: 0 };
  const file = path.join(os.tmpdir(), 'cm-poster-local.jpg');
  fs.writeFileSync(file, Buffer.from(await r.arrayBuffer()));
  const [w, h] = execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', file]).toString().trim().split(',').map(Number);
  fs.unlinkSync(file);
  return { w, h };
};
// by folder or by name: the title may already be the source's one
const findSeries = async fragment => (await api('GET', '/Items?IncludeItemTypes=Series&Recursive=true&Fields=Path')).Items.find(i => (i.Path || '').includes(fragment) || i.Name.includes(fragment));
const upload = async (dataId, node, file) => pick(await api('POST', `/CustomMetadata/Local/${dataId}/Image?node=${encodeURIComponent(node)}`, fs.readFileSync(file), true), 'Url');

const wide = path.join(os.tmpdir(), 'cm-wide-poster.jpg');
execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=1920x1080:d=1', '-frames:v', '1', wide]);
const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
const folder = path.join(SERIES_DIR, 'Serie locale posters {lp-s1}');
const DATA_A = 'lposter00000001', DATA_B = 'lposter00000002';
try {
  // ---- A. the series belongs to a local source
  fs.mkdirSync(path.join(folder, 'Saison 1'), { recursive: true });
  execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'color=c=gray:s=320x180:d=2', '-c:v', 'libx264', '-t', '2', path.join(folder, 'Saison 1', 'E01 Premier.mkv')]);
  const posterA = await upload(DATA_A, 's1', wide);
  await api('PUT', `/CustomMetadata/Local/${DATA_A}`, { Version: 1, Series: [{ Id: 's1', Title: 'Série locale', Poster: posterA, Seasons: [] }] });
  const config = JSON.parse(JSON.stringify(original));
  config.Sources.push({ Name: 'Locale posters', Prefix: 'lp', Enabled: true, SourceType: 'Local', LocalDataId: DATA_A, BaseUrl: 'local://local', LandscapePosterCrop: true });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await scan();
  const a = await findSeries('Serie locale');
  check('A: the local series exists', !!a);
  if (a) {
    await refresh(a.Id);
    const s = await size(a.Id);
    check('A: pure local source, option on: the poster is a 2:3 crop (720x1080)', s.w === 720 && s.h === 1080, JSON.stringify(s));
  }

  // ---- B. a local source overrides an API series; the API source does NOT have the option, the local one does
  const posterB = await upload(DATA_B, 'dm-aventuriers', wide);
  await api('PUT', `/CustomMetadata/Local/${DATA_B}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Poster: posterB, Seasons: [] }] });
  const config2 = JSON.parse(JSON.stringify(original));
  config2.Sources.push({ Name: 'Surcharge posters', Prefix: 'ov', Enabled: true, SourceType: 'Local', LocalDataId: DATA_B, BaseUrl: 'local://local', LandscapePosterCrop: true });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config2);
  const b = await findSeries('Aventuriers');
  await api('POST', `/Items/${b.Id}/Refresh?Recursive=false&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=true&ReplaceAllMetadata=false`);
  await wait(8000);
  const sb = await size(b.Id);
  check('B: local source overriding an API series, option on the local source: 2:3 crop (720x1080)', sb.w === 720 && sb.h === 1080, JSON.stringify(sb));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA_A}`);
  await api('DELETE', `/CustomMetadata/Local/${DATA_B}`);
  fs.rmSync(folder, { recursive: true, force: true });
  fs.unlinkSync(wide);
  const b = await findSeries('Aventuriers');
  if (b) { await api('POST', `/Items/${b.Id}/Refresh?Recursive=false&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=true&ReplaceAllMetadata=false`); await wait(6000); }
  await scan();
}
console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
process.exit(failed ? 1 : 0);
