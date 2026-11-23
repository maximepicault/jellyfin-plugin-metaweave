// Number of a missing episode when a real episode already holds it, in the REAL Jellyfin (REST only, no browser).
// Usage: node missing-number-real.mjs <base_url> <media_dir> [user] [password]
//   e.g. node missing-number-real.mjs http://localhost:8097 ~/.cache/cm-ui-test/media
// Jellyfin reads episode numbers from file names, tags included ("Bonus {ab-1-6149542}.mkv" is episode 6149542, "Clip 09.mkv"
// is 9), and groups episodes by SEASON NUMBER: every season without one shares a single group, and a series refresh deletes a
// virtual episode whose number a real episode of its group holds. Setup: a series with two seasons without a number, the local
// season "Saison locale {nb-k3m9xw}" (missing episodes 9, 10, 11; real "Clip 09.mkv" and "Bonus {ab-1-6149542}.mkv") and
// "Autre source" (real "Clip 10.mkv", "Clip 11.mkv"). Checks: the virtual episodes keep 9, 10, 11 (never 12+, never millions),
// survive a full series refresh, stay listed in their own season in order, and get their normal season number back once the
// files are renamed. Removes its source, data and files at the end. Prints PASS / FAIL.
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
const idle = async () => {
  for (let i = 0; i < 120; i++) { await wait(1000); if (i > 5 && !((await api('GET', '/ScheduledTasks?isHidden=false')) || []).some(t => t.State === 'Running')) return; }
};
const scan = async () => { await api('POST', '/Library/Refresh'); await idle(); };
const analyze = async () => {
  const at = async () => pick(pick(await api('GET', '/CustomMetadata/MissingEpisodes'), 'Report'), 'GeneratedAt');
  const before = await at();
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze');
  for (let i = 0; i < 120; i++) { await wait(1000); if ((await at()) !== before) return; }
  console.log('⚠️ the analysis did not finish in time');
};
const reportSeason = async () => (pick(pick(await api('GET', '/CustomMetadata/MissingEpisodes'), 'Report'), 'Seasons') || [])
  .find(s => pick(s, 'SourceItemId') === 'nb-k3m9xw');
// The virtual episodes as Jellyfin stores them, looked up by id (an episode Jellyfin deleted comes back as null)
const missing = async ids => {
  const rows = [];
  for (const [tag, id, list, assigned] of ids) {
    const e = await api('GET', `/Users/${auth.User.Id}/Items/${id}`);
    rows.push({ tag, list, assigned, jellyfin: e?.IndexNumber, season: e?.ParentIndexNumber ?? null, exists: !!e?.Id });
  }
  return rows;
};
const reportIds = async () => (pick(await reportSeason(), 'Missing') || []).map(m => [pick(m, 'Tag'), pick(m, 'EpisodeId'), pick(m, 'IndexNumber'), pick(m, 'AssignedNumber')]);
// Episodes Jellyfin lists in the local season, in its own order. Missing episodes are listed only for a user who displays
// them: the option is switched on for the call, then restored
const seasonList = async seasonId => {
  const season = await api('GET', `/Users/${auth.User.Id}/Items/${seasonId}`);
  const userConfig = (await api('GET', `/Users/${auth.User.Id}`)).Configuration;
  await api('POST', `/Users/${auth.User.Id}/Configuration`, { ...userConfig, DisplayMissingEpisodes: true });
  try {
    const list = await api('GET', `/Shows/${season.SeriesId}/Episodes?seasonId=${seasonId}&userId=${auth.User.Id}`);
    return { seriesId: season.SeriesId, numbers: (pick(list, 'Items') || []).map(e => e.IndexNumber) };
  } finally {
    await api('POST', `/Users/${auth.User.Id}/Configuration`, userConfig);
  }
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
const seasonDir = path.join(seriesDir, 'Saison locale {nb-k3m9xw}');
const otherDir = path.join(seriesDir, 'Autre source');
const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  check('a bench video to copy', !!sample);
  fs.mkdirSync(seasonDir, { recursive: true });
  fs.mkdirSync(otherDir, { recursive: true });
  for (const name of ['Clip 09', 'Bonus {ab-1-6149542}']) fs.copyFileSync(sample, path.join(seasonDir, name + '.mkv'));
  for (const name of ['Clip 10', 'Clip 11']) fs.copyFileSync(sample, path.join(otherDir, name + '.mkv'));
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.Prefix !== 'nb');
  config.Sources.push({ Name: 'Numéros', Prefix: 'nb', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 't4p8nw', Title: 'Numéros', Seasons: [{ Id: 'k3m9xw', Title: 'Saison locale',
    Episodes: [{ Id: 'ab23cd', Title: 'Neuf', IndexNumber: 9 }, { Id: 'pq5r7t', Title: 'Dix', IndexNumber: 10 }, { Id: 'xw9y2z', Title: 'Onze', IndexNumber: 11 }] }] }] });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await scan();
  await analyze();

  const ids = await reportIds();
  const held = await missing(ids);
  check('three missing episodes', held.length === 3, JSON.stringify(held));
  check('each keeps its own number although real files hold 9, 10, 11', held.every(r => r.jellyfin === r.list), JSON.stringify(held));
  check('the report shows no other number', held.every(r => r.assigned == null), JSON.stringify(held));
  check('held numbers: in a group of their own (season number -1)', held.every(r => r.season === -1), JSON.stringify(held));

  const seasonId = pick(await reportSeason(), 'SeasonId');
  const { seriesId, numbers } = await seasonList(seasonId);
  check('listed in their own season, in order', JSON.stringify(numbers.filter(n => n < 100)) === '[9,9,10,11]', JSON.stringify(numbers));

  // A full series refresh is when Jellyfin deletes a virtual episode that shares a number with a real one
  await api('POST', `/Items/${seriesId}/Refresh?Recursive=true&MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=true&ReplaceAllImages=false`);
  await wait(3000);
  await idle();
  const afterRefresh = await missing(ids);
  check('they survive a full series refresh (replace all metadata) with their numbers', afterRefresh.every(r => r.exists && r.jellyfin === r.list && r.season === -1), JSON.stringify(afterRefresh));

  fs.renameSync(path.join(seasonDir, 'Clip 09.mkv'), path.join(seasonDir, 'Clip neuf.mkv'));
  for (const [from, to] of [['Clip 10', 'Clip dix'], ['Clip 11', 'Clip onze']]) fs.renameSync(path.join(otherDir, from + '.mkv'), path.join(otherDir, to + '.mkv'));
  await scan();
  await analyze();
  const freed = await missing(await reportIds());
  check('numbers freed: same numbers, normal season number again', freed.length === 3 && freed.every(r => r.jellyfin === r.list && r.season === null && r.assigned == null), JSON.stringify(freed));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  fs.rmSync(seriesDir, { recursive: true, force: true });
  await scan();
  console.log('configuration restored');
}
console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
process.exit(failed ? 1 : 0);
