// People screen in the REAL jellyfin-web (theme included) against the real server, through headless Chrome + the DevTools protocol.
// Usage: node config-people-real.mjs <base_url> <out_dir> [user] [password]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node shoot.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const port = 9336;
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
  // Two people sources whose answer is a LIST (a search url taking the identifier as a parameter): root list, and list inside an object
  cfg.Sources.push({ Name: 'Personnes liste', Prefix: 'pl', SourceType: 'People', BaseUrl: 'http://172.17.0.1:8765', PersonEndpoint: '/people-list?user={id}', PersonMappings: { Name: '[0].profile.displayName', Photo: '[0].profile.avatar', Bio: '[0].profile.about' }, Enabled: true, SendOriginReferer: false });
  cfg.Sources.push({ Name: 'Personnes recherche', Prefix: 'pw', SourceType: 'People', BaseUrl: 'http://172.17.0.1:8765', PersonEndpoint: '/people-wrapped?user={id}', PersonMappings: { Name: 'results[0].name', Photo: 'results[0].photo', Bio: 'results[0].bio' }, Enabled: true, SendOriginReferer: false });
  cfg.Sources.push({ Name: 'Personnes liste sans chemin', Prefix: 'px', SourceType: 'People', BaseUrl: 'http://172.17.0.1:8765', PersonEndpoint: '/people-list?user={id}', PersonMappings: { Name: 'profile.displayName', Photo: '', Bio: '' }, Enabled: true, SendOriginReferer: false });
  await ApiClient.updatePluginConfiguration(pid, cfg);
  async function put(path, body) { await fetch(ApiClient.getUrl(path), { method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Emby-Token': tok }, body: JSON.stringify(body) }); }
  await put('CustomMetadata/People', { Version: 1, People: [
    { Id: 'pers01', Name: 'Camille Durand', Photo: 'http://172.17.0.1:8765/img/p1.jpg', Bio: 'Comédienne.', Links: [{ Prefix: 'pe', ExternalId: 'p1' }] },
    { Id: 'manu01', Name: 'Invité Maison', Photo: '', Bio: 'Saisi à la main.', Links: [] },
    ] });
  await put('CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [] });
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
const api = async (path, init = {}) => evalJs(`(async function(){var r=await fetch(ApiClient.getUrl(${JSON.stringify(path)}),{headers:{'X-Emby-Token':ApiClient.accessToken()}}); return JSON.stringify(await r.json());})()`);
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

  // A people source whose answer is a LIST (search url with the identifier as a parameter): paths with a position, "[0]"
  const fetchPerson = prefix => evalJs(`(async function(){var r=await fetch(ApiClient.getUrl('CustomMetadata/People/Fetch'),{method:'POST',headers:{'Content-Type':'application/json','X-Emby-Token':ApiClient.accessToken()},body:JSON.stringify({Prefix:${JSON.stringify(prefix)},ExternalId:'p1'})}); return JSON.stringify(await r.json());})()`).then(JSON.parse);
  const rootList = await fetchPerson('pl');
  check('list source (root list): name read with [0]', rootList.Name === 'Camille Durand', JSON.stringify(rootList));
  check('list source (root list): photo and bio read too', !!rootList.Photo && !!rootList.Bio, JSON.stringify(rootList));
  const wrapped = await fetchPerson('pw');
  check('list source (list inside an object): name read with results[0]', wrapped.Name === 'Camille Durand', JSON.stringify(wrapped));
  const noPath = await fetchPerson('px');
  check('list source without [0]: a clear hint instead of a silent miss', !noPath.Name && /\[0\]/.test(noPath.Error || ''), JSON.stringify(noPath));
  const plain = await fetchPerson('pe');
  check('plain object source unchanged', plain.Name === 'Camille Durand', JSON.stringify(plain));
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=CustomMetadataPeople` });
  await until(`!!document.querySelector('${P} .pp-item')`, 30000); await wait(1200);
  check('directory loaded', (await evalJs(`document.querySelectorAll('${P} .pp-item').length`)) === 2);
  check('people source offered', (await evalJs(`document.querySelector('${P} #ppAddSource').value`)) === 'pe');
  await shot('P1-people-1440');

  // Fetch a new person by identifier, then add them
  await type(`${P} #ppAddId`, 'pe-p2'); await click(`${P} #ppFetch`);
  check('fetch: entry found', await until(`!!document.querySelector('${P} [data-found-add]')`, 10000));
  check('fetch: bio decoded', (await evalJs(`document.querySelector('${P} #ppFound').textContent`)).includes('Réalisateur & scénariste'));
  await shot('P2-found-1440');
  await click(`${P} [data-found-add]`); await wait(400);
  check('added to the directory: selected and unsaved', (await evalJs(`document.querySelector('${P} #ppDetail h3').textContent`)) === 'Émile Lefèvre' && (await evalJs(`document.querySelector('${P} #ppDirty').textContent`)).includes('non enregistrées'));

  // Deduplication: p3 has the same name as Camille Durand
  await type(`${P} #ppAddId`, 'p3'); await click(`${P} #ppFetch`);
  await until(`!!document.querySelector('${P} [data-found-merge]')`, 10000);
  check('duplicate by name detected', (await evalJs(`document.querySelector('${P} #ppFound').textContent`)).includes('Même nom que la fiche'));
  await shot('P3-duplicate-1440');
  await click(`${P} [data-found-merge]`); await wait(400);
  check('identifier added to the existing entry', (await evalJs(`document.querySelector('${P} #ppDetail h3').textContent`)) === 'Camille Durand' && (await evalJs(`document.querySelectorAll('${P} .pp-link-row').length`)) === 2);
  check('no new entry created', (await evalJs(`document.querySelectorAll('${P} .pp-item').length`)) === 3);

  // Complete / Overwrite
  await click(`${P} [data-link-fill="1"]`); await until(`!document.querySelector('${P} #ppLinkMsg').hidden`, 8000); await wait(300);
  check('"Compléter": nothing to complete', (await evalJs(`document.querySelector('${P} #ppLinkMsg').textContent`)).includes('Rien à compléter'));

  // Photo uploaded for Émile
  await evalJs(`(function(){var b=Array.from(document.querySelectorAll('${P} .pp-item')).find(function(x){return x.textContent.indexOf('Émile')>=0;}); b.click(); return true;})()`); await wait(300);
  await evalJs(`(async function(){
    var c=document.createElement('canvas'); c.width=300; c.height=300; var g=c.getContext('2d'); g.fillStyle='#8a5cd0'; g.fillRect(0,0,300,300); g.fillStyle='#fff'; g.font='120px sans-serif'; g.fillText('É',100,190);
    var blob=await new Promise(function(r){c.toBlob(r,'image/png');});
    var dt=new DataTransfer(); dt.items.add(new File([blob],'emile.png',{type:'image/png'}));
    var i=document.querySelector('${P} [data-file]'); i.files=dt.files; i.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await wait(1500);
  check('uploaded photo: plugin file', (await evalJs(`document.querySelector('${P} [data-f="Photo"]').disabled`)) === true && (await evalJs(`document.querySelector('${P} #ppDetail .pp-avatar img').complete && document.querySelector('${P} #ppDetail .pp-avatar img').naturalWidth > 0`)) === true);
  await shot('P4-detail-1440');

  // Save and read back from the server
  await click(`${P} #ppSave`); await wait(1200);
  const saved = JSON.parse(await api('CustomMetadata/People'));
  const camille = saved.People.find(p => p.Name === 'Camille Durand');
  const emile = saved.People.find(p => p.Name === 'Émile Lefèvre');
  check('saved: 3 people', saved.People.length === 3, String(saved.People.length));
  check('saved: Camille has two identifiers (pe-p1, pe-p3)', camille && camille.Links.map(l => l.Prefix + '-' + l.ExternalId).sort().join() === 'pe-p1,pe-p3', JSON.stringify(camille && camille.Links));
  check('saved: Émile with uploaded photo', emile && /^local:\/\/img\/people-directory\//.test(emile.Photo), JSON.stringify(emile));
  check('indicator reset', (await evalJs(`document.querySelector('${P} #ppDirty').textContent`)) === 'Aucune modification');

  // Manual entry (unsaved)
  await click(`${P} #ppAddManual`); await wait(300);
  check('manual entry: name field ready', (await evalJs(`document.activeElement && document.activeElement.id`)) === 'ppName');
  await type(`${P} #ppName`, 'Zoé Martin'); await wait(200);
  await shot('P5-manual-1440');
  await size(1920, 1100); await wait(600);
  await evalJs(`window.scrollTo(0,0); true`); await shot('P7-people-1920');
  const w = await evalJs(`Math.round(document.querySelector('${P} #ppDetail').getBoundingClientRect().width)`);
  check('form: width capped at 1920 px (' + w + ' px)', w <= 981, String(w));
  await size(412, 900, true); await wait(600);
  await evalJs(`window.scrollTo(0,0); true`); await shot('P6-mobile');
  await size(1440, 1000);
  // Casting disabled in the plugin settings: the screen says so
  const setCasting = value => evalJs(`(async function(){var pid='12345678-1234-1234-1234-123456789abc'; var cfg=await ApiClient.getPluginConfiguration(pid); cfg.EnableCasting=${value}; await ApiClient.updatePluginConfiguration(pid, cfg); return true;})()`);
  check('casting on: no warning', (await evalJs(`document.querySelector('${P} #ppCastOff').hidden`)) === true);
  await setCasting(false);
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=CustomMetadataPeople` }); await send('Page.reload'); await wait(2500);
  await until(`!!document.querySelector('${P} .pp-item')`, 20000);
  check('casting off: warning shown', (await evalJs(`!document.querySelector('${P} #ppCastOff').hidden`)) === true);
  await shot('P8-casting-off-1440');
  await setCasting(true);
  console.log(`\n${pass} PASS, ${fail} FAIL`);
  await evalJs(RESTORE);
} finally { ws.close(); chrome.kill(); try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) {} process.exit(fail ? 1 : 0); }
