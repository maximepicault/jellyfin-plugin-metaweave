// Option "Recadrer les posters paysage en portrait 2:3": the poster of a series, served as a 1920x1080 landscape picture by the mock API,
// reaches Jellyfin as a 720x1080 portrait crop when the option is on, and untouched when it is off. Restores the image and the configuration.
// Usage: node poster-crop-real.mjs <base_url> <api_img_dir> [user] [password]
//   api_img_dir: the folder the mock API serves /img/ from (~/.cache/cm-ui-test/img)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const [J, IMG, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !IMG) { console.error('usage: node poster-crop-real.mjs <base_url> <api_img_dir> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-poster", Version="1.0"' }, body: JSON.stringify({ Username: user, Password: password, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => { const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const poster = path.join(IMG, 'aventuriers.jpg');
const backup = poster + '.orig';
const size = async id => {
  const r = await fetch(`${J}/Items/${id}/Images/Primary`, { headers: H });
  const file = path.join(os.tmpdir(), 'cm-poster-check.jpg');
  fs.writeFileSync(file, Buffer.from(await r.arrayBuffer()));
  const [w, h] = execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', file]).toString().trim().split(',').map(Number);
  fs.unlinkSync(file);
  return { w, h };
};
const refresh = async id => { await api('POST', `/Items/${id}/Refresh?Recursive=false&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=true&ReplaceAllMetadata=false`); await wait(8000); };
const setOption = async (original, on) => {
  const config = JSON.parse(JSON.stringify(original));
  config.Sources.find(s => s.Prefix === 'dm').LandscapePosterCrop = on;
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
};

const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
const series = (await api('GET', '/Items?IncludeItemTypes=Series&Recursive=true')).Items.find(i => i.Name.includes('Aventuriers'));
try {
  fs.copyFileSync(poster, backup);
  execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=1920x1080:d=1', '-frames:v', '1', poster]);
  await setOption(original, true);
  const stored = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).Sources.find(s => s.Prefix === 'dm');
  check('the option is stored', stored.LandscapePosterCrop === true);
  await refresh(series.Id);
  const on = await size(series.Id);
  check('option on: the landscape poster reaches Jellyfin as a 2:3 crop (720x1080)', on.w === 720 && on.h === 1080, JSON.stringify(on));
  await setOption(original, false);
  await refresh(series.Id);
  const off = await size(series.Id);
  check('option off: the poster is kept as it is (1920x1080)', off.w === 1920 && off.h === 1080, JSON.stringify(off));
} finally {
  if (fs.existsSync(backup)) { fs.renameSync(backup, poster); }
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await refresh(series.Id);
}
console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
process.exit(failed ? 1 : 0);
