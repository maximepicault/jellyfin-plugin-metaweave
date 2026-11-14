// A local series/season whose id is the tag of an API element replaces it, in the REAL Jellyfin (REST only, no browser).
// Usage: node local-override-real.mjs <base_url> [user] [password]   (e.g. http://localhost:8097)
// Seeds a local source "ov" with the series "dm-aventuriers" and the season "dm-a1" of the test bench, removes the episode
// list endpoint of the API source "dm", then checks: metadata of the series and the season, and the missing episodes
// (taken from the local season, tagged "ov-…"). Restores the configuration and deletes the local data. Prints PASS / FAIL.
const [J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J) { console.error('usage: node local-override-real.mjs <base_url> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'ovtest00000001';
// The server answers in camelCase for plain controllers: read keys whatever their case
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-override", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password })
})).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return null; } })() : null };
};
const waitTasksIdle = async () => {
  for (let i = 0; i < 120; i++) {
    const t = await api('GET', '/ScheduledTasks?isHidden=false');
    if (!(t.json || []).some(x => x.State === 'Running')) return;
    await wait(1000);
  }
};
// Starts the analysis and waits until a NEW report exists (the task is queued: "idle" right after the call means nothing)
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
const item = async id => (await api('GET', `/Items/${id}?userId=${auth.User.Id}`)).json;
const find = async (type, name) => ((await api('GET', `/Items?IncludeItemTypes=${type}&Recursive=true&Fields=Overview&userId=${auth.User.Id}`)).json?.Items || []).find(i => name(i.Name));
const refresh = async id => { await api('POST', `/Items/${id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=true&ReplaceAllImages=false`); await wait(4000); await waitTasksIdle(); };

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const config = JSON.parse(JSON.stringify(original));
try {
  const dm = config.Sources.find(s => (s.Prefix || '').toLowerCase() === 'dm');
  check('API source "dm" present', !!dm);
  dm.EpisodeListEndpoint = '';   // an API without a list of episodes
  config.Sources = config.Sources.filter(s => s.Prefix !== 'ov');
  config.Sources.push({
    Name: 'Surcharges', Prefix: 'ov', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos'
  });
  const put = await api('PUT', `/CustomMetadata/Local/${DATA}`, {
    Version: 1,
    Series: [{
      Id: 'dm-aventuriers', Title: 'Aventuriers surchargés', Overview: 'Résumé de la série saisi dans le plugin.',
      Seasons: [{
        Id: 'dm-a1', Title: 'Saison locale', Overview: 'Résumé de la saison saisi dans le plugin.',
        Episodes: [1, 2, 3, 4, 5].map(n => ({ Id: 'e' + n, Title: 'Local ' + n, IndexNumber: n }))
      }]
    }]
  });
  check('local content saved, ids kept', put.status === 200 && put.json?.Series?.[0]?.Id === 'dm-aventuriers' && put.json.Series[0].Seasons[0].Id === 'dm-a1', JSON.stringify(put.json)?.slice(0, 200));
  const bad = await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'a-b-c', Title: 'x', Seasons: [] }] });
  check('an invalid id is replaced, not kept', bad.json?.Series?.[0]?.Id !== 'a-b-c', bad.json?.Series?.[0]?.Id);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, put.json);
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  const series = await find('Series', n => /aventuriers/i.test(n));
  const season = await find('Season', n => /saison 2024|saison locale/i.test(n));
  check('series and season found', !!series && !!season);
  await refresh(series.Id);
  await refresh(season.Id);
  const s = await item(series.Id), n = await item(season.Id);
  check('series: local title and overview win', s.Name === 'Aventuriers surchargés' && /saisi dans le plugin/.test(s.Overview || ''), `${s.Name} / ${s.Overview}`);
  check('season: local overview wins', /saisi dans le plugin/.test(n.Overview || ''), n.Overview);

  await analyze();
  const report = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const seasons = pick(report, 'Seasons') || [];
  const rs = seasons.find(x => pick(x, 'SourceItemId') === 'dm-a1');
  check('season dm-a1 analysed although the API has no list endpoint', !!rs, JSON.stringify(seasons.map(x => pick(x, 'SourceItemId'))));
  check('its episodes come from the local source (prefix ov)', pick(rs, 'EpisodePrefix') === 'ov' && pick(rs, 'ApiCount') === 5, JSON.stringify(rs)?.slice(0, 300));
  check('the 5 local episodes are missing (files carry dm tags)', (pick(rs, 'Missing') || []).length === 5 && pick(pick(rs, 'Missing')[3], 'Title') === 'Local 4', JSON.stringify((pick(rs, 'Missing') || []).map(m => pick(m, 'Title'))));
  const firstId = pick(pick(rs, 'Missing')?.[0], 'EpisodeId');
  const virtual = firstId ? await item(firstId) : null;
  check('a virtual episode exists for the first local episode', virtual?.Name === 'Local 1', JSON.stringify(virtual?.Name));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  await analyze();
  const series = await find('Series', n => /aventuriers/i.test(n));
  if (series) { await refresh(series.Id); }
  const season = await find('Season', n => /saison 2024|saison locale/i.test(n));
  if (season) { await refresh(season.Id); }
  console.log('configuration restored');
}
console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
process.exit(failed ? 1 : 0);
