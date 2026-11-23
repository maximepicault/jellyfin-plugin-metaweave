// Number of a missing episode when a real episode already holds it, in the REAL Jellyfin (REST only, no browser).
// Usage: node missing-number-real.mjs <base_url> <media_dir> [user] [password]
//   e.g. node missing-number-real.mjs http://localhost:8097 ~/.cache/cm-ui-test/media
// Jellyfin reads episode numbers from file names, tags included ("Bonus {ab-1-6149542}.mkv" is episode 6149542, "Clip 09.mkv"
// is 9). Missing episodes 9-11 of a local source whose numbers are held by real files used to get 6149543, 6149544… and kept them
// (cached). Checks: they get the next free numbers, the report names the file holding each number, and once the files are
// renamed the virtual episodes take 9-11 back. Removes its source, data and files at the end. Prints PASS / FAIL.
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2).filter(a => !a.startsWith('--'));
const [J, mediaDir, user = 'admin', password = 'admin'] = args;
if (!J || !mediaDir) { console.error('usage: node missing-number-real.mjs <base_url> <media_dir> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'numbertest0001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-number", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password })
})).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  try { return JSON.parse(text); } catch { return null; }
};
const scan = async () => {
  await api('POST', '/Library/Refresh');
  for (let i = 0; i < 120; i++) { await wait(1000); if (i > 5 && !((await api('GET', '/ScheduledTasks?isHidden=false')) || []).some(t => t.State === 'Running')) return; }
};
const analyze = async () => {
  const at = async () => pick(pick(await api('GET', '/CustomMetadata/MissingEpisodes'), 'Report'), 'GeneratedAt');
  const before = await at();
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze');
  for (let i = 0; i < 120; i++) { await wait(1000); if ((await at()) !== before) return; }
  console.log('⚠️ the analysis did not finish in time');
};
const missing = async () => {
  const seasons = pick(pick(await api('GET', '/CustomMetadata/MissingEpisodes'), 'Report'), 'Seasons') || [];
  const season = seasons.find(s => pick(s, 'SourceItemId') === 'nb-k3m9xw');
  const rows = [];
  for (const m of pick(season, 'Missing') || []) {
    const e = await api('GET', `/Users/${auth.User.Id}/Items/${pick(m, 'EpisodeId')}`);
    rows.push({ tag: pick(m, 'Tag'), list: pick(m, 'IndexNumber'), assigned: pick(m, 'AssignedNumber'), heldBy: pick(m, 'NumberHeldBy'), jellyfin: e?.IndexNumber });
  }
  return rows;
};

// Any bench video will do as a file: the content does not matter, only the name
const sample = (function find(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) { const f = find(full); if (f) return f; } else if (entry.name.endsWith('.mkv')) return full;
  }
  return null;
})(path.join(mediaDir, 'series'));
const seriesDir = path.join(mediaDir, 'series', 'Numéros {nb-t4p8nw}');
const seasonDir = path.join(seriesDir, 'Saison 1 {nb-k3m9xw}');
const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  check('a bench video to copy', !!sample);
  fs.mkdirSync(seasonDir, { recursive: true });
  for (const name of ['Clip 09', 'Clip 10', 'Clip 11', 'Bonus {ab-1-6149542}']) fs.copyFileSync(sample, path.join(seasonDir, name + '.mkv'));
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.Prefix !== 'nb');
  config.Sources.push({ Name: 'Numéros', Prefix: 'nb', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 't4p8nw', Title: 'Numéros', Seasons: [{ Id: 'k3m9xw', Title: 'Saison 1',
    Episodes: [{ Id: 'ab23cd', Title: 'Neuf', IndexNumber: 9 }, { Id: 'pq5r7t', Title: 'Dix', IndexNumber: 10 }, { Id: 'xw9y2z', Title: 'Onze', IndexNumber: 11 }] }] }] });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await scan();
  await analyze();

  const held = await missing();
  check('three missing episodes', held.length === 3, JSON.stringify(held));
  check('held numbers: the next free ones, never millions', held.every(r => r.jellyfin >= 12 && r.jellyfin < 100), JSON.stringify(held));
  check('the report gives the number Jellyfin shows', held.every(r => r.assigned === r.jellyfin), JSON.stringify(held));
  check('the report names the real file holding each number', held.every(r => r.heldBy === `Clip ${String(r.list).padStart(2, '0')}.mkv`), JSON.stringify(held));

  for (const [from, to] of [['Clip 09', 'Clip neuf'], ['Clip 10', 'Clip dix'], ['Clip 11', 'Clip onze']]) fs.renameSync(path.join(seasonDir, from + '.mkv'), path.join(seasonDir, to + '.mkv'));
  await scan();
  await analyze();
  const freed = await missing();
  check('numbers freed: the virtual episodes take their own numbers back', freed.length === 3 && freed.every(r => r.jellyfin === r.list && r.assigned == null), JSON.stringify(freed));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  fs.rmSync(seriesDir, { recursive: true, force: true });
  await scan();
  console.log('configuration restored');
}
console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
process.exit(failed ? 1 : 0);
