// A real episode whose file name has no number (only its {prefix-id} tag) and whose API gives none either: its metadata gets the
// position of the episode in the season's list sorted by release date. Season "Saison 1 {dm-b1}" of the mock API (b1v2 is 2nd by date).
// Usage: node episode-number-real.mjs <base_url> <season_dir> [user] [password]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const [J, DIR, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !DIR) { console.error('usage: node episode-number-real.mjs <base_url> <season_dir> [user] [password]'); process.exit(2); }
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-number", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url) => { const r = await fetch(J + url, { method, headers: H }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
const file = path.join(DIR, 'Sans numero {dm-b1v2}.mkv');
const find = async () => (await api('GET', '/Items?IncludeItemTypes=Episode&Recursive=true&Fields=Path&Limit=500')).Items.find(i => (i.Path || '').includes('Sans numero {dm-b1v2}'));
try {
  if (fs.existsSync(file)) fs.unlinkSync(file);
  execFileSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=gray:s=320x180:d=2', '-c:v', 'libx264', '-t', '2', file]);
  await scan();
  let item = null;
  for (let i = 0; i < 20 && !item; i++) { item = await find(); if (!item) await wait(2000); }
  check('the file became an episode', !!item);
  check('its metadata number is its position by release date (2)', item && item.IndexNumber === 2, JSON.stringify(item && item.IndexNumber));
} finally {
  if (fs.existsSync(file)) fs.unlinkSync(file);
  await scan();
}
console.log(failed ? `\n${failed} FAILED` : '\nALL PASSED');
process.exit(failed ? 1 : 0);
