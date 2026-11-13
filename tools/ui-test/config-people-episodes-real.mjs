// "Distribution des épisodes d'API" tab of the People screen in the REAL jellyfin-web (theme included) against the real server, through headless Chrome + the DevTools protocol.
// Usage: node config-people-episodes-real.mjs <base_url> <out_dir> [user] [password]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node shoot.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const port = 9337;
const profile = fs.mkdtempSync(path.join(outDir, '.profile-'));
const chrome = spawn('google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
const wait = ms => new Promise(r => setTimeout(r, ms));

let targets = [];
for (let i = 0; i < 50 && !targets.length; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch {} await wait(200); }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => (ws.onopen = r));
let id = 0; const pending = {};
ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending[d.id]) { pending[d.id](d); delete pending[d.id]; } };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
const size = (w, h, mobile = false) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png' }); const f = path.join(outDir, `${name}.png`); fs.writeFileSync(f, Buffer.from(r.result.data, 'base64')); console.log('📸', f); };
const until = async (expr, ms = 20000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await evalJs(expr)) return true; await wait(300); } console.log('⚠️ timeout:', expr); return false; };

// Starting state prepared by the script itself (people source « pe », directory, casts), restored at the end
const SEED = `(async function(){
  var tok = ApiClient.accessToken(), pid = '12345678-1234-1234-1234-123456789abc';
  var cfg = await ApiClient.getPluginConfiguration(pid);
  window.__seedBackup = { sources: JSON.parse(JSON.stringify(cfg.Sources || [])) };
  cfg.Sources = (cfg.Sources || []).filter(function(s){ return s.SourceType !== 'People'; });
  cfg.Sources.push({ Name: 'Personnes démo', Prefix: 'pe', SourceType: 'People', BaseUrl: 'http://172.17.0.1:8765', PersonEndpoint: '/people/{id}', PersonMappings: { Name: 'name', Photo: 'photo', Bio: 'bio' }, Enabled: true, SendOriginReferer: false });
  await ApiClient.updatePluginConfiguration(pid, cfg);
  async function put(path, body) { await fetch(ApiClient.getUrl(path), { method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Emby-Token': tok }, body: JSON.stringify(body) }); }
  await put('CustomMetadata/People', { Version: 1, People: [
    { Id: 'pers01', Name: 'Camille Durand', Photo: 'http://172.17.0.1:8765/img/p1.jpg', Bio: 'Comédienne.', Links: [{ Prefix: 'pe', ExternalId: 'p1' }] },
    { Id: 'manu01', Name: 'Invité Maison', Photo: '', Bio: 'Saisi à la main.', Links: [] },
    { Id: 'pers02', Name: 'Émile Lefèvre', Photo: '', Bio: 'Réalisateur.', Links: [{ Prefix: 'pe', ExternalId: 'p2' }] } ] });
  await put('CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [{ Key: 'dm-a1v1', Cast: [{ PersonId: 'pers01', Role: 'Elle-même', Type: 'Actor' }, { PersonId: 'manu01', Role: '', Type: 'Director' }] }, { Key: 'dm-a1v4', Cast: [{ PersonId: 'pers02', Role: 'Narrateur', Type: 'GuestStar' }] }] });
  return true; })()`;
const RESTORE = `(async function(){
  var tok = ApiClient.accessToken(), pid = '12345678-1234-1234-1234-123456789abc';
  var cfg = await ApiClient.getPluginConfiguration(pid);
  cfg.Sources = (cfg.Sources || []).filter(function(s){ return s.SourceType !== 'People' && s.SourceType !== 'Local'; });
  await ApiClient.updatePluginConfiguration(pid, cfg);
  async function put(path, body) { await fetch(ApiClient.getUrl(path), { method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Emby-Token': tok }, body: JSON.stringify(body) }); }
  await put('CustomMetadata/People', { Version: 1, People: [] });
  await put('CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [] });
  return true; })()`;
const P = '#CustomMetadataPeoplePage';
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { if (ok) { pass++; console.log('PASS', name); } else { fail++; console.log('FAIL', name, detail); } };
const click = sel => evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return false; e.click(); return true;})()`);
const type = (sel, v) => evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); e.value=${JSON.stringify(v)}; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
const api = async path => JSON.parse(await evalJs(`(async function(){var r=await fetch(ApiClient.getUrl(${JSON.stringify(path)}),{headers:{'X-Emby-Token':ApiClient.accessToken()}}); return JSON.stringify(await r.json());})()`));
try {
  await send('Page.enable'); await send('Runtime.enable');
  await size(1440, 1000);
  await send('Page.navigate', { url: `${J}/web/#/login` });
  if (await until(`!!document.querySelector('#txtManualName')`)) {
    await evalJs(`(function(){var n=document.querySelector('#txtManualName'); n.value=${JSON.stringify(user)}; n.dispatchEvent(new Event('input',{bubbles:true}));
      var p=document.querySelector('#txtManualPassword'); p.value=${JSON.stringify(password)}; p.dispatchEvent(new Event('input',{bubbles:true}));
      document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
    await until(`!location.hash.includes('login')`); await wait(1500);
  }
  await evalJs(SEED); await wait(500);
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=CustomMetadataPeople` });
  await until(`!!document.querySelector('${P} .pp-item')`, 30000); await wait(800);
  await evalJs(`window.confirm = function () { return true; }; true`);   // a real dialog would block the driving
  check('Episodes tab hidden at the start', (await evalJs(`document.querySelector('${P} #ppTabEpisodes').hidden`)) === true);
  await click(`${P} [data-pp-tab="episodes"]`);
  // The slow library listing is NOT loaded automatically: only "By identifier" and a button are offered
  check('library not loaded by default', await until(`!!document.querySelector('${P} [data-library-load]')`, 15000));
  check('library not loaded: no season in the tree', (await evalJs(`document.querySelectorAll('${P} [data-ep-season]').length`)) === 0);
  check('"By identifier" is the default view', (await evalJs(`!!document.querySelector('${P} #ppIdAdd')`)) === true);
  await click(`${P} [data-library-load]`);
  check('tree loaded (series and seasons)', await until(`document.querySelectorAll('${P} [data-ep-season]').length >= 4`, 15000));
  check('series listed', (await evalJs(`document.querySelector('${P} #ppEpTree').textContent`)).includes('Les Aventuriers'));
  await shot('E1-episodes-tab-1440');

  // recherche
  await type(`${P} #ppEpSearch`, 'aventuriers 2024');
  check('search: a single season', (await evalJs(`document.querySelectorAll('${P} [data-ep-season]').length`)) === 1);
  await click(`${P} [data-ep-season]`); await wait(400);
  check('season shown with its episodes', (await evalJs(`document.querySelectorAll('${P} .pp-ep').length`)) >= 4);
  check('missing episode flagged', (await evalJs(`document.querySelector('${P} #ppEpDetail').textContent`)).includes('manquant'));
  check('existing cast visible (Le départ)', (await evalJs(`document.querySelector('${P} [data-ep-open="episode:dm-a1v1"]').textContent`)).includes('Camille Durand'));

  // Add a cast to « Premier bivouac »
  await click(`${P} [data-ep-open="episode:dm-a1v2"]`); await wait(300);
  await type(`${P} #ppCastAdd`, 'camille durand'); await click(`${P} [data-cast-add]`); await wait(300);
  check('ajout par nom', (await evalJs(`document.querySelectorAll('${P} .pp-cast-row').length`)) === 1);
  await type(`${P} #ppCastAdd`, 'inconnu complet'); await click(`${P} [data-cast-add]`); await wait(200);
  check('unknown person refused', (await evalJs(`document.querySelector('${P} #ppCastMsg').textContent`)).includes('pas dans l\'annuaire'));
  // The same person can hold another job on the same element: a second row with the next free job
  await type(`${P} #ppCastAdd`, 'camille durand'); await click(`${P} [data-cast-add]`); await wait(300);
  check('same person, another job: second row with the next free job', (await evalJs(`document.querySelectorAll('${P} .pp-cast-row').length`)) === 2 && (await evalJs(`document.querySelector('${P} [data-cast-type="1"]').value`)) === 'Director');
  await click(`${P} [data-cast-remove="1"]`); await wait(250);
  check('same person, another job: back to one row', (await evalJs(`document.querySelectorAll('${P} .pp-cast-row').length`)) === 1);
  await type(`${P} [data-cast-role="0"]`, 'Présentatrice');
  await evalJs(`(function(){var s=document.querySelector('${P} [data-cast-type="0"]'); s.value='GuestStar'; s.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await shot('E2-episode-open-1440');
  await click(`${P} [data-cast-season]`); await wait(300);
  check('applied to the whole season', (await evalJs(`document.querySelector('${P} #ppCastMsg').textContent`)).includes('ajout'));
  check('indicator: unsaved changes', (await evalJs(`document.querySelector('${P} #ppDirty').textContent`)).includes('non enregistrées'));

  // Save and read back from the server
  await click(`${P} #ppSave`); await wait(1500);
  const saved = await api('CustomMetadata/People/EpisodeCast');
  const byKey = {}; saved.Episodes.forEach(e => { byKey[e.Type + ':' + e.Key] = e.Cast; });
  check('saved: role and type', byKey['episode:dm-a1v2'] && byKey['episode:dm-a1v2'][0].Role === 'Présentatrice' && byKey['episode:dm-a1v2'][0].Type === 'GuestStar', JSON.stringify(byKey['episode:dm-a1v2']));
  check('saved: copied to the other episodes of the season', ['dm-a1v3', 'dm-a1v4'].map(k => 'episode:' + k).every(k => byKey[k] && byKey[k].some(c => c.PersonId === 'pers01')), JSON.stringify(Object.keys(byKey)));
  check('saved: existing casts are kept', byKey['episode:dm-a1v1'] && byKey['episode:dm-a1v1'].length >= 2, JSON.stringify(byKey['episode:dm-a1v1']));
  check('indicator reset', (await evalJs(`document.querySelector('${P} #ppDirty').textContent`)) === 'Aucune modification');
  const treeInfo = await evalJs(`(function(){var b=document.querySelector('${P} [data-ep-season]'); return b.querySelector('.pp-item-ids').textContent + '|' + (b.querySelector('.pp-count')||{}).textContent;})()`);
  check('season counter in the tree = number of episodes with a cast', treeInfo.split('|')[0].startsWith(treeInfo.split('|')[1] + ' '), treeInfo);
  // Cast of the series, of the season, then « Par identifiant » (distinct types for one identifier)
  await click(`${P} [data-ep-season]`); await wait(300);
  await click(`${P} [data-ep-open="season:dm-a1"]`); await wait(300);
  await type(`${P} #ppCastAdd`, 'camille durand'); await click(`${P} [data-cast-add]`); await wait(300);
  check('season cast', (await evalJs(`document.querySelectorAll('${P} .pp-cast-row').length`)) === 1);
  await click(`${P} [data-ep-series]`); await wait(300);
  check('series cast offered', (await evalJs(`!!document.querySelector('${P} [data-ep-open="series:dm-aventuriers"]')`)) === true);
  await click(`${P} [data-ep-open="series:dm-aventuriers"]`); await wait(300);
  // Autocomplete with a preview, then cascade series -> seasons -> episodes
  // click() does not focus and a programmatic focus() is silent while the headless window is unfocused: dispatch the event the page listens to
  await evalJs(`(function(){var i=document.querySelector('${P} #ppCastAdd'); i.dispatchEvent(new FocusEvent('focusin',{bubbles:true})); return true;})()`); await wait(300);
  const acCount = await evalJs(`document.querySelectorAll('${P} .pp-ac-opt').length + '/' + (document.querySelector('${P} #ppCastSuggest')||{}).hidden`);
  check('autocomplete: suggestions shown on focus', parseInt(acCount, 10) >= 2, acCount);
  check('autocomplete: each suggestion has an avatar and a name', (await evalJs(`Array.from(document.querySelectorAll('${P} .pp-ac-opt')).every(function(o){return !!o.querySelector('.pp-avatar') && !!o.querySelector('strong');})`)) === true);
  await type(`${P} #ppCastAdd`, 'maison');
  check('autocomplete: filtered by the typed words', (await evalJs(`document.querySelectorAll('${P} .pp-ac-opt').length`)) === 1);
  await click(`${P} .pp-ac-opt`); await wait(400);
  check('autocomplete: click adds the person', (await evalJs(`document.querySelectorAll('${P} .pp-cast-row').length`)) === 1);
  // Cascade = a saved flag on the series, applied to every season/episode below it (no library needed). Preview = the tags it reaches.
  check('cascade box present on a series and unchecked by default', (await evalJs(`var c=document.querySelector('${P} #ppCascade'); !!c && !c.disabled && !c.checked`)) === true);
  await evalJs(`(function(){var c=document.querySelector('${P} #ppCascade'); c.checked=true; c.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  check('cascade preview lists the season and episode tags', await until(`(function(){var t=(document.querySelector('${P} #ppCascadePreview')||{}).textContent||''; return t.indexOf('{dm-a1}')>=0 && t.indexOf('{dm-a1v3}')>=0;})()`, 10000), await evalJs(`(document.querySelector('${P} #ppCascadePreview')||{}).textContent`));
  check('cascade preview says how many episodes', (await evalJs(`document.querySelector('${P} #ppCascadePreview').textContent`)).includes('épisode(s)'));
  await shot('E3-autocomplete-cascade-1440');
  await click(`${P} [data-ep-mode="ids"]`); await wait(300);
  await type(`${P} #ppIdKey`, 'pas un identifiant'); await click(`${P} #ppIdAdd`); await wait(200);
  check('invalid identifier refused', (await evalJs(`document.querySelector('${P} #ppIdMsg').textContent`)).includes('Format attendu'));
  await evalJs(`(function(){var s=document.querySelector('${P} #ppIdType'); s.value='episode'; s.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await type(`${P} #ppIdKey`, '{DV-1234}'); await click(`${P} #ppIdAdd`); await wait(300);
  await type(`${P} #ppCastAdd`, 'camille durand'); await click(`${P} [data-cast-add]`); await wait(300);
  await evalJs(`(function(){var s=document.querySelector('${P} #ppIdType'); s.value='series'; s.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await type(`${P} #ppIdKey`, 'dv-1234'); await click(`${P} #ppIdAdd`); await wait(300);
  await type(`${P} #ppCastAdd`, 'invité maison'); await click(`${P} [data-cast-add]`); await wait(300);
  check('by identifier: an episode and a series with the same identifier are two elements', (await evalJs(`!!document.querySelector('${P} [data-ep-open="episode:dv-1234"]') && !!document.querySelector('${P} [data-ep-open="series:dv-1234"]')`)) === true);
  check('by identifier: outside the library flagged', (await evalJs(`document.querySelector('${P} #ppEpDetail').textContent`)).includes('pas dans la médiathèque'));
  check('by identifier: the episode title is the row title', (await evalJs(`document.querySelector('${P} [data-ep-open="episode:dm-a1v1"] .pp-ep-title').textContent`)) === 'Le départ');
  check('by identifier: the tag stays as a landmark', (await evalJs(`document.querySelector('${P} [data-ep-open="episode:dm-a1v1"] .pp-ep-sum').textContent`)).includes('{dm-a1v1}'));
  check('by identifier: outside the library = the tag as title', (await evalJs(`document.querySelector('${P} [data-ep-open="episode:dv-1234"] .pp-ep-title').textContent`)) === '{dv-1234}');
  check('by identifier: no empty number column', (await evalJs(`document.querySelectorAll('${P} #ppEpDetail .pp-ep-num').length`)) === 0);
  await shot('E5-by-id-1440');
  await click(`${P} #ppSave`); await wait(1500);
  const typed = await api('CustomMetadata/People/EpisodeCast');
  const typedMap = {}; typed.Episodes.forEach(e => { typedMap[e.Type + ':' + e.Key] = e.Cast.map(c => c.PersonId).join(); });
  check('saved: series, season and episode', typedMap['series:dm-aventuriers'] === 'manu01' && typedMap['season:dm-a1'] === 'pers01', JSON.stringify(typedMap));
  check('saved: same identifier, two types', typedMap['episode:dv-1234'] === 'pers01' && typedMap['series:dv-1234'] === 'manu01', JSON.stringify(typedMap));

  const season2024 = `Array.from(document.querySelectorAll('${P} [data-ep-season]')).find(function(b){return b.textContent.indexOf('Saison 2024')>=0;})`;
  check('tree: the series shows its cast is clickable', (await evalJs(`document.querySelector('${P} [data-ep-series] .pp-group-sub').textContent`)).includes('Distribution de la série'));
  check('tree: "saison" badge when the season has a cast', (await evalJs(`!!${season2024}.querySelector('.pp-badge')`)) === true);
  check('tree: season text not truncated', (await evalJs(`${season2024}.querySelector('.pp-item-ids').textContent`)) === '8 épisode(s)');
  // "Appliquer aux épisodes": banner after saving, then update of the affected episodes
  check('"Appliquer aux épisodes" banner shown after saving', (await evalJs(`!document.querySelector('${P} #ppApply').hidden`)) === true);
  await shot('E4-apply-notice-1440');
  await click(`${P} #ppApplyBtn`);
  check('update started on episodes', await until(`/épisode\\(s\\) en cours de mise à jour/.test(document.querySelector('${P} #ppApplyText').textContent)`, 10000), await evalJs(`document.querySelector('${P} #ppApplyText').textContent`));
  const cascadeFlag = (await api('CustomMetadata/People/EpisodeCast')).Episodes.filter(e => e.Type === 'series' && e.Key === 'dm-aventuriers')[0];
  check('saved: cascade flag on the series', !!cascadeFlag && cascadeFlag.Cascade === true, JSON.stringify(cascadeFlag));
  // Server side: the episodes below the series inherit its cast once applied (the episode has no entry of its own for this person)
  let reached = false;
  for (let i = 0; i < 20 && !reached; i++) {
    const items = await api('Items?Recursive=true&IncludeItemTypes=Episode&Fields=People,Path');
    const ep = (items.Items || []).filter(x => (x.Path || '').includes('{dm-a1v3}'))[0];
    reached = !!ep && (ep.People || []).some(pp => pp.Name === 'Invité Maison');
    if (!reached) { await wait(2000); }
  }
  check('cascade: an episode of the series got the person in Jellyfin', reached);
  // An episode with NO identifier in its name is never enriched by the plugin: the cast is written straight into Jellyfin
  let untaggedReached = false;
  for (let i = 0; i < 20 && !untaggedReached; i++) {
    const items = await api('Items?Recursive=true&IncludeItemTypes=Episode&Fields=People,Path');
    const ep = (items.Items || []).filter(x => (x.Path || '').includes('Sans identifiant'))[0];
    untaggedReached = !!ep && (ep.People || []).some(pp => pp.Name === 'Invité Maison');
    if (!untaggedReached) { await wait(2000); }
  }
  check('cascade: an episode WITHOUT identifier got the person in Jellyfin', untaggedReached);
  await size(1920, 1100); await wait(600); await shot('E6-episodes-1920');
  await size(412, 900, true); await wait(600);
  await evalJs(`window.scrollTo(0,0); true`); await shot('E3-episodes-mobile');
  await size(1440, 1000);
  // Server side: a person who is Actor on the series (cascade) and Director on an episode keeps BOTH jobs on that episode
  const jsonCall = (method, path, body) => evalJs(`(async function(){var r=await fetch(ApiClient.getUrl(${JSON.stringify(path)}),{method:${JSON.stringify(method)},headers:{'Content-Type':'application/json','X-Emby-Token':ApiClient.accessToken()},body:${JSON.stringify(JSON.stringify(body))}}); return r.status;})()`);
  await jsonCall('PUT', 'CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [
    { Type: 'series', Key: 'dm-aventuriers', Cascade: true, Cast: [{ PersonId: 'pers01', Role: 'Rôle série', Type: 'Actor' }] },
    { Type: 'episode', Key: 'dm-a1v1', Cast: [{ PersonId: 'pers01', Role: 'Rôle épisode', Type: 'Director' }] }] });
  await jsonCall('POST', 'CustomMetadata/People/Apply', { SeriesKeys: ['dm-aventuriers'], EpisodeKeys: ['dm-a1v1'], ExpandKeys: ['series:dm-aventuriers'] });
  let twoJobs = false;
  for (let i = 0; i < 20 && !twoJobs; i++) {
    const items = await api('Items?Recursive=true&IncludeItemTypes=Episode&Fields=People,Path');
    const ep = (items.Items || []).filter(x => (x.Path || '').includes('{dm-a1v1}'))[0];
    const jobs = ep ? (ep.People || []).filter(pp => pp.Name === 'Camille Durand').map(pp => pp.Type + ':' + pp.Role).sort() : [];
    twoJobs = jobs.length === 2 && jobs.includes('Actor:Rôle série') && jobs.includes('Director:Rôle épisode');
    if (!twoJobs) { await wait(2000); }
  }
  check('cascade: the same person keeps two jobs on an episode (actor from the series, director of its own)', twoJobs);
  // A whole series WITHOUT any identifier is listed too, under a fallback key "jf-<item id>", and gets its cast written into Jellyfin
  const lib = await api('CustomMetadata/People/Library');
  const bare = lib.filter(x => x.Name === 'Série sans identifiant')[0];
  check('library: a series with no identifier at any level is listed', !!bare && /^jf-[0-9a-f]{32}$/.test(bare.Key || ''), JSON.stringify(lib.map(x => x.Name + ':' + x.Key)));
  check('library: its season and episodes are listed with fallback keys', !!bare && bare.Seasons.length === 1 && /^jf-/.test(bare.Seasons[0].Key || '') && bare.Seasons[0].Episodes.length === 2 && bare.Seasons[0].Episodes.every(e => /^jf-/.test(e.Key)), JSON.stringify(bare));
  check('library list on screen: the series is an entry of the list', (await evalJs(`Array.from(document.querySelectorAll('${P} [data-ep-series]')).some(function(b){return b.textContent.indexOf('Série sans identifiant')>=0;})`)) === true);
  if (bare) {
    const firstEp = bare.Seasons[0].Episodes[0];
    await jsonCall('PUT', 'CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [
      { Type: 'series', Key: bare.Key, Cascade: true, Cast: [{ PersonId: 'pers01', Role: 'Rôle série', Type: 'Actor' }] },
      { Type: 'episode', Key: firstEp.Key, Cast: [{ PersonId: 'manu01', Role: 'Réal', Type: 'Director' }] }] });
    await jsonCall('POST', 'CustomMetadata/People/Apply', { SeriesKeys: [bare.Key], EpisodeKeys: [firstEp.Key], ExpandKeys: ['series:' + bare.Key] });
    let bareOk = false, last = '';
    for (let i = 0; i < 20 && !bareOk; i++) {
      const eps = (await api('Items?Recursive=true&IncludeItemTypes=Episode&Fields=People,Path')).Items.filter(x => (x.Path || '').includes('Série sans identifiant'));
      const ser = (await api('Items?Recursive=true&IncludeItemTypes=Series&Fields=People,Path')).Items.filter(x => (x.Path || '').includes('Série sans identifiant'))[0];
      const names = it => (it.People || []).map(pp => pp.Name + ':' + pp.Type).sort().join(',');
      last = JSON.stringify({ ser: ser ? names(ser) : null, eps: eps.map(e => e.Name + '=' + names(e)) });
      const e1 = eps.filter(e => e.Path.includes('E01'))[0], e2 = eps.filter(e => e.Path.includes('E02'))[0];
      bareOk = !!e1 && !!e2 && !!ser && names(e1) === 'Camille Durand:Actor,Invité Maison:Director' && names(e2) === 'Camille Durand:Actor' && names(ser) === 'Camille Durand:Actor';
      if (!bareOk) { await wait(2000); }
    }
    check('no identifier at all: the series cast reaches its episodes (cascade), the episode keeps its own director', bareOk, last);
  }
  console.log(`\n${pass} PASS, ${fail} FAIL`);
  await evalJs(RESTORE);
} finally { ws.close(); chrome.kill(); try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) {} process.exit(fail ? 1 : 0); }
