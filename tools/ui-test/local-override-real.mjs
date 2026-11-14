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
  // The test button shows the images (downloaded by the plugin), not only their addresses
  const tested = (await api('POST', '/CustomMetadata/TestSource', { Source: dm, ItemType: 'season', Id: 'a1' })).json;
  const previews = pick(tested, 'ImagePreviews') || [];
  check('test button: images come with a displayable preview', previews.length > 0 && previews.every(p => /^data:image\//.test(pick(p, 'DataUri') || '')), JSON.stringify(previews).slice(0, 200));
  const heavy = (await api('POST', '/CustomMetadata/TestSource', { Source: dm, ItemType: 'series', Id: 'bigimg' })).json;
  const heavyPreview = (pick(heavy, 'ImagePreviews') || [])[0];
  const heavyUri = pick(heavyPreview, 'DataUri') || '';
  check('test button: a multi-MB poster is shown reduced, not refused', /^data:image\/jpeg/.test(heavyUri) && heavyUri.length < 200 * 1024, JSON.stringify(heavyPreview)?.slice(0, 200));
  // CORS-like rejections: the call is retried once with the opposite Origin/Referer choice
  const tryCors = async (id, origin) => (await api('POST', '/CustomMetadata/TestSource', { Source: { ...dm, SendOriginReferer: origin }, ItemType: 'series', Id: id })).json;
  const corsA = await tryCors('corsA', true);
  check('CORS: a backend refusing the Origin is retried without it', pick(corsA, 'Success') === true && !(pick(corsA, 'RequestHeaders') || []).some(h => /^Origin:/i.test(h)), JSON.stringify(pick(corsA, 'Error')));
  const corsB = await tryCors('corsB', false);
  check('CORS: a backend requiring the Origin is retried with it', pick(corsB, 'Success') === true && (pick(corsB, 'RequestHeaders') || []).some(h => /^Origin:/i.test(h)), JSON.stringify(pick(corsB, 'Error')));
  const corsC = await tryCors('corsC', true);
  check('CORS: a backend refusing both says what to change', pick(corsC, 'Success') === false && /CORS/.test(pick(corsC, 'Error') || '') && /Réseau/.test(pick(corsC, 'Error') || ''), JSON.stringify(pick(corsC, 'Error')));
  // Custom headers go out exactly as typed, quotes included (Cache-Control: max-age="10" used to be refused and dropped)
  const quotedHeaders = [{ Key: 'Cache-Control', Value: 'max-age="10", no-transform' }, { Key: 'Accept-Language', Value: 'fr;q="0.9"' }, { Key: 'X-Quoted', Value: 'say "hi"' }, { Key: 'X-Switched-Off', Value: 'nope', Enabled: false }];
  const echoed = (await api('POST', '/CustomMetadata/TestSource', { Source: { ...dm, CustomHeaders: quotedHeaders }, ItemType: 'series', Id: 'echoheaders' })).json;
  let wire = {};
  try { wire = JSON.parse(pick(pick(echoed, 'Metadata'), 'Title') || '{}'); } catch { /* shown below */ }
  const wireValue = n => Object.entries(wire).find(([k]) => k.toLowerCase() === n.toLowerCase())?.[1];
  check('headers: a switched-off header is not sent', wireValue('X-Switched-Off') === undefined && wireValue('X-Quoted') !== undefined, JSON.stringify(wire).slice(0, 300));
  check('headers: quotes survive on the wire, also for headers .NET validates', wireValue('Cache-Control') === 'max-age="10", no-transform' && wireValue('Accept-Language') === 'fr;q="0.9"' && wireValue('X-Quoted') === 'say "hi"', JSON.stringify(wire).slice(0, 300));
  // The button of the editor: the API season's list, as local episodes carrying the API's tags (the endpoint is still there)
  const imported = await api('POST', '/CustomMetadata/Local/ImportEpisodes', { Tag: '{dm-a1}' });
  const list = imported.json || [];
  check('import: the API list comes back as local episodes with API tags', imported.status === 200 && list.length >= 5 && list.every(e => /^dm-a1v\d+$/.test(pick(e, 'Id'))), JSON.stringify(imported.json)?.slice(0, 200));
  check('import: unknown prefix refused', (await api('POST', '/CustomMetadata/Local/ImportEpisodes', { Tag: 'zz-1' })).status === 400);
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
        Episodes: list.map(e => ({ ...e, ApiBase: undefined, Title: pick(e, 'Id') === 'dm-a1v1' ? 'Titre surchargé' : pick(e, 'Title') }))   // typed in full: no API list to follow
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
  check('its episodes come from the local source (prefix ov)', pick(rs, 'EpisodePrefix') === 'ov' && pick(rs, 'ApiCount') === list.length, JSON.stringify(rs)?.slice(0, 300));
  const present = 3; // the bench has the files dm-a1v1..v3 on disk: only the others are missing
  const missing = pick(rs, 'Missing') || [];
  check('only the episodes without a file are missing (API tags kept)', missing.length === list.length - present && missing.every(m => /^dm-a1v\d+$/.test(pick(m, 'Id'))), JSON.stringify(missing.map(m => pick(m, 'Id'))));
  const firstId = pick(missing[0], 'EpisodeId');
  const virtual = firstId ? await item(firstId) : null;
  check('a virtual episode exists for the first missing one', !!virtual?.Name, JSON.stringify(virtual?.Name));
  const real = await find('Episode', n => /d.part/i.test(n) || n === 'Titre surchargé');
  if (real) { await refresh(real.Id); }
  const after = real ? await item(real.Id) : null;
  check('a real episode takes the title typed in the plugin', after?.Name === 'Titre surchargé', JSON.stringify(after?.Name));
  // Phase B: the API HAS a list again; the local season only overrides one episode and adds one: nothing of the API goes away
  dm.EpisodeListEndpoint = original.Sources.find(x => (x.Prefix || '').toLowerCase() === 'dm').EpisodeListEndpoint;
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, {
    Version: 1,
    Series: [{ Id: 'dm-aventuriers', Title: 'Aventuriers surchargés', Seasons: [{
      Id: 'dm-a1', Title: 'Saison locale',
      Episodes: [{ Id: 'dm-a1v4', Title: 'Quatrième surchargé' }, { Id: 'x1', Title: 'Épisode ajouté' },
        { Id: 'dm-a1v5', Title: 'Ancien titre', ApiBase: { Title: 'Ancien titre' } }]   // imported earlier, untouched since
    }] }]
  });
  await analyze();
  const reportB = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const rb = (pick(reportB, 'Seasons') || []).find(x => pick(x, 'SourceItemId') === 'dm-a1');
  const missB = pick(rb, 'Missing') || [];
  const byTag = t => missB.find(m => pick(m, 'Tag') === t);
  check('B: the API episodes are all still there, plus the added one', pick(rb, 'ApiCount') === list.length + 1, JSON.stringify(pick(rb, 'ApiCount')));
  check('B: the overridden API episode keeps its API tag and takes the local title', pick(byTag('dm-a1v4'), 'Title') === 'Quatrième surchargé', JSON.stringify(missB.map(m => pick(m, 'Tag'))));
  check('B: an imported episode left untouched follows the live API title, not the old copy', !!byTag('dm-a1v5') && !!pick(byTag('dm-a1v5'), 'Title') && pick(byTag('dm-a1v5'), 'Title') !== 'Ancien titre', JSON.stringify(pick(byTag('dm-a1v5'), 'Title')));
  check('B: an API episode not named locally is untouched', !!byTag('dm-a1v6') && !!pick(byTag('dm-a1v6'), 'Title'));
  check('B: the added episode is listed under the local source tag', pick(byTag('ov-x1'), 'Title') === 'Épisode ajouté', JSON.stringify(missB.map(m => pick(m, 'Tag'))));
  // Phase C: the local source has the SAME prefix as the API ("dm") and plain ids (a1, a1v4): the API is called first, the local data goes over it
  config.Sources.find(x => x.Prefix === 'ov').Prefix = 'dm';
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, {
    Version: 1,
    Series: [{ Id: 'aventuriers', Title: 'Aventuriers même préfixe', Seasons: [{
      Id: 'a1', Overview: 'Résumé de la saison, même préfixe',
      Episodes: [{ Id: 'a1v4', Title: 'Quatrième même préfixe' }]
    }] }]
  });
  const seriesC = await find('Series', n => /aventuriers/i.test(n));
  const seasonC = await find('Season', n => /saison 2024|saison locale|2024/i.test(n));
  await refresh(seriesC.Id);
  await refresh(seasonC.Id);
  const sC = await item(seriesC.Id), nC = await item(seasonC.Id);
  check('C: same prefix - series takes the local title', sC.Name === 'Aventuriers même préfixe', sC.Name);
  check('C: same prefix - season keeps the API title where the local one is empty', nC.Name === 'Saison 2024', nC.Name);
  check('C: same prefix - season takes the local overview', /même préfixe/.test(nC.Overview || ''), nC.Overview);
  await analyze();
  const reportC = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const rc = (pick(reportC, 'Seasons') || []).find(x => pick(x, 'SourceItemId') === 'dm-a1');
  const missC = pick(rc, 'Missing') || [];
  check('C: same prefix - every API episode is still listed', pick(rc, 'ApiCount') === list.length, JSON.stringify(pick(rc, 'ApiCount')));
  check('C: same prefix - the plain local id overrides the API episode', pick(missC.find(m => pick(m, 'Tag') === 'dm-a1v4'), 'Title') === 'Quatrième même préfixe', JSON.stringify(missC.map(m => pick(m, 'Tag') + ':' + pick(m, 'Title'))));
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
