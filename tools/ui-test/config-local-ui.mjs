// Test of the LOCAL SOURCE editor of config.html, without Jellyfin or Docker:
// the real page is loaded in headless Chrome with an in-memory fake ApiClient/Dashboard
// (playing the role of LocalSourceController: ids generated for elements without one, GET/PUT/DELETE).
// The Jellyfin style and the emby-* elements do not exist here: logic and layout are checked,
// not the final look (see ui-test.sh / shoot.mjs for the real jellyfin-web).
// Usage : node config-local-ui.mjs <out_dir>
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const outDir = process.argv[2];
if (!outDir) { console.error('usage: node config-local-ui.mjs <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });

const shim = `
window.__mock = { calls: [], alerts: [], local: {}, people: [{ Id: 'p1', Name: 'Camille Durand', Photo: '', Links: [] }, { Id: 'p2', Name: 'Émile Lefèvre', Photo: '', Links: [] }, { Id: 'p3', Name: 'Zoé Martin', Photo: '', Links: [] }], config: { EnableMetadata: true, EnableImages: true, EnableCasting: true, CreateVirtualEpisodes: true, EnableCache: true, CacheTTLHours: 24, PatternPrefix: 'custom',
  Sources: [{ Name: 'API démo', Prefix: 'dm', BaseUrl: 'http://api.test', VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos', Enabled: true,
    VideoFieldMappings: { Title: 'title' }, SourceFieldMappings: {}, SeriesFieldMappings: {}, EpisodeListMappings: {}, CustomHeaders: [] }] } };
var clone = function (o) { return JSON.parse(JSON.stringify(o)); };
var rid = function () { return Math.random().toString(36).slice(2, 8); };
window.ApiClient = {
  getUrl: function (p, q) { if (p.indexOf('/Image/') > 0) { return 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7#/' + p + '?api_key=' + (q && q.api_key); } return '/' + p + (q ? '?' + Object.keys(q).map(function (k) { return k + '=' + q[k]; }).join('&') : ''); },
  accessToken: function () { return 'TOKEN'; },
  getPluginConfiguration: function () { return Promise.resolve(clone(__mock.config)); },
  updatePluginConfiguration: function (id, c) { __mock.config = clone(c); __mock.calls.push('PLUGIN'); return Promise.resolve({}); },
  getJSON: function (url) {
    if (/CustomMetadata\\/People/.test(url)) { __mock.calls.push('GET people'); return Promise.resolve(clone({ Version: 1, People: __mock.people })); }
    var m = url.match(/CustomMetadata\\/Local\\/(.+)$/);
    __mock.calls.push('GET ' + m[1]);
    return Promise.resolve(clone(__mock.local[m[1]] || { Version: 1, Series: [] }));
  },
  ajax: function (o) {
    var m = o.url.match(/CustomMetadata\\/Local\\/(.+)$/);
    if (o.type === 'PUT') {
      var c = JSON.parse(o.data);
      c.Series.forEach(function (s) { s.Id = s.Id || rid(); (s.Seasons || []).forEach(function (se) { se.Id = se.Id || rid(); (se.Episodes || []).forEach(function (e) { e.Id = e.Id || rid(); }); }); });
      __mock.local[m[1]] = c; __mock.calls.push('PUT ' + m[1]); return Promise.resolve(clone(c));
    }
    if (o.type === 'DELETE') { delete __mock.local[m[1]]; __mock.calls.push('DELETE ' + m[1]); return Promise.resolve(); }
    __mock.calls.push(o.type + ' ' + o.url); return Promise.resolve({});
  }
};
window.fetch = function (url, o) {
  if (o && o.method === 'POST' && url.indexOf('/Image') >= 0) {
    var id = url.match(/CustomMetadata\\/Local\\/([^/]+)\\/Image/)[1];
    __mock.calls.push('UPLOAD ' + url + ' ' + o.headers['Content-Type'] + ' ' + o.headers['X-Emby-Token'] + ' ' + (o.body && o.body.size));
    return Promise.resolve({ ok: true, json: function () { return Promise.resolve({ Url: 'local://img/' + id + '/ep-' + rid() + '.jpg' }); } });
  }
  return Promise.reject(new Error('fetch inattendu'));
};
window.Dashboard = { showLoadingMsg: function () {}, hideLoadingMsg: function () {}, processPluginConfigurationUpdateResult: function () {}, alert: function (m) { __mock.alerts.push(m); } };
window.confirm = function () { return true; };
`;
let html = fs.readFileSync(new URL('../../Configuration/config.html', import.meta.url), 'utf8');
html = html.replace('<head>', `<head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{background:#101010;color:#fff;font:16px/1.4 sans-serif;margin:0;padding:0 16px} input,textarea,select{background:#1e1e1e;color:#fff;border:1px solid #555;padding:8px;width:100%;box-sizing:border-box;font:inherit} button{font:inherit;color:#fff;background:#333;border:1px solid #555;padding:8px 14px;border-radius:4px} .inputContainer{margin:0 0 12px} .inputLabel{display:block;margin-bottom:4px}</style><script>${shim}</script>`);
const harness = path.join(outDir, 'harness-config.html');
fs.writeFileSync(harness, html);

const port = 9334;
const profile = fs.mkdtempSync(path.join(outDir, '.profile-'));
const chrome = spawn('google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', '--hide-scrollbars', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
const wait = ms => new Promise(r => setTimeout(r, ms));
let targets = [];
for (let i = 0; i < 50 && !targets.length; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch {} await wait(200); }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => (ws.onopen = r));
let id = 0; const pending = {};
ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending[d.id]) { pending[d.id](d); delete pending[d.id]; } };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async expr => { const r = (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result; if (r?.exceptionDetails) console.log('⚠️ JS:', r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r?.result?.value; };
const size = (w, h, mobile = false) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true }); const f = path.join(outDir, `${name}.png`); fs.writeFileSync(f, Buffer.from(r.result.data, 'base64')); console.log('📸', f); };
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { if (ok) { pass++; console.log('PASS', name); } else { fail++; console.log('FAIL', name, detail); } };
const P = '#CustomMetadataConfigPage';
const type = (sel, value) => ev(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); e.value=${JSON.stringify(value)}; e.dispatchEvent(new Event('input',{bubbles:true})); return true;})()`);
const click = sel => ev(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return false; e.click(); return true;})()`);
const vis = sel => ev(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); return !!e && e.offsetParent!==null;})()`);

try {
  await send('Page.enable'); await send('Runtime.enable');
  await size(1440, 1000);
  await send('Page.navigate', { url: 'file://' + harness }); await wait(800);
  await ev(`document.querySelector('${P}').dispatchEvent(new Event('pageshow')); true`); await wait(400);

  // 1. List + type choice
  check('list: 1 API source', (await ev(`document.querySelectorAll('${P} #sourcesList .cm-card').length`)) === 1);
  check('list: no type choice at the start', !(await vis('#typeChooserWrap')));
  await click(`${P} .cm-toolbar .button-submit`); await wait(200);
  check('"Ajouter une source" offers API / Locale / Personnes', (await vis('#typeChooser')) && (await ev(`document.querySelectorAll('#typeChooser [data-new-type]').length`)) === 3);
  await shot('L1-chooser-1440');

  // 2. Local source: the editor only shows Général / Contenu / Miniatures
  await click('#typeChooser [data-new-type="local"]'); await wait(300);
  const nav = await ev(`Array.from(document.querySelectorAll('${P} .cm-nav-item')).filter(function(b){return b.offsetParent!==null;}).map(function(b){return b.getAttribute('data-go');}).join(',')`);
  check('local editor: visible sections = general,content,thumbs', nav === 'general,content,thumbs', nav);
  check('local editor: base URL and API key hidden', !(await vis('#srcBaseUrl')) && !(await vis('#srcApiKey')));
  check('local editor: test panel hidden', !(await vis('.cm-test')));
  check('local editor: base URL not required', (await ev(`document.querySelector('#srcBaseUrl').required`)) === false);
  await type('#srcName', 'Mes vidéos'); await type('#srcPrefix', 'loc');
  await shot('L2-general-local-1440');

  // 3. Content: series → season → episode + bulk add
  await click(`${P} [data-go="content"]`); await wait(200);
  check('content: empty state with an "Ajouter une série" button', await vis('#localDetail [data-add="series"]'));
  await shot('L3-content-empty-1440');
  await click('#localDetail [data-add="series"]'); await wait(200);
  await type('#lf-Title', 'Ma série');
  await click('#localTree [data-add="season"]'); await wait(200);
  await type('#lf-Title', 'Saison 1');
  await click('#localTree [data-add="episode"]'); await wait(200);
  await type('#lf-Title', 'Le pilote'); await type('#lf-Duration', '24:30'); await type('#lf-Thumb', 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7'); await type('#lf-Tags', 'a, b, A');
  // image: web address (preview), then uploaded file (replaces the address), then "Retirer"
  await ev(`document.querySelector('#lf-Thumb').dispatchEvent(new Event('change',{bubbles:true})); true`); await wait(200);
  check('image: preview of the web address', (await ev(`(document.querySelector('.cm-imgprev img')||{}).src`)) === 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7');
  check('image: "Retirer" button offered', await vis('[data-clear-img="Thumb"]'));
  await ev(`(function(){var dt=new DataTransfer(); dt.items.add(new File([new Uint8Array([255,216,255,224,0,16,74,70,73,70,0,1])],'a.jpg',{type:'image/jpeg'})); var i=document.querySelector('[data-file="Thumb"]'); i.files=dt.files; i.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`); await wait(400);
  check('image: file sent to the server', (await ev(`__mock.calls.filter(function(c){return c.indexOf('UPLOAD')===0;}).length`)) === 1);
  const uploadCall = await ev(`__mock.calls.filter(function(c){return c.indexOf('UPLOAD')===0;})[0]`);
  check('image: upload with the element id and the file type', /\/Image\?node=[a-z0-9]{6}/.test(uploadCall) && uploadCall.includes('image/jpeg') && uploadCall.includes('TOKEN'), uploadCall);
  check('image: the address field is greyed out and the preview goes through the plugin', (await ev(`document.querySelector('#lf-Thumb').disabled`)) === true && (await ev(`document.querySelector('.cm-imgprev img').src`)).includes('/Image/') && (await ev(`document.querySelector('.cm-imgprev img').src`)).includes('api_key=TOKEN'));
  await shot('L3b-image-uploaded-1440');
  await click('[data-clear-img="Thumb"]'); await wait(200);
  check('image: "Retirer" empties the field', (await ev(`document.querySelector('#lf-Thumb').value`)) === '' && !(await vis('[data-clear-img="Thumb"]')));
  await ev(`(function(){var dt=new DataTransfer(); dt.items.add(new File([new Uint8Array([255,216,255,224,0,16,74,70,73,70,0,1])],'b.jpg',{type:'image/jpeg'})); var i=document.querySelector('[data-file="Thumb"]'); i.files=dt.files; i.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`); await wait(400);
  const epId = await ev(`document.querySelector('#localTree [aria-current="true"]').getAttribute('data-node')`);
  check('episode: {loc-id} tag shown', (await ev(`document.querySelector('#lfTag').textContent`)) === '{loc-' + epId + '}');
  check('episode: number 1 assigned', (await ev(`document.querySelector('#lf-IndexNumber').value`)) === '1');
  // saison : ajout en lot
  const seasonId = await ev(`document.querySelector('#localTree ul ul > li > .cm-t-row .cm-t-item').getAttribute('data-node')`);
  await click(`#localTree [data-node="${seasonId}"]`); await wait(200);
  await type('#lfBulk', 'Deuxième\n\n  Troisième  \nQuatrième');
  await click(`#localDetail [data-bulk="${seasonId}"]`); await wait(300);
  const labels = await ev(`Array.from(document.querySelectorAll('#localTree .cm-t-item .t')).map(function(s){return s.textContent;}).join(' | ')`);
  check('bulk add: 3 episodes (empty lines ignored)', labels === 'Ma série | Saison 1 | Le pilote | Deuxième | Troisième | Quatrième', labels);
  const nums = await ev(`Array.from(document.querySelectorAll('#localTree .cm-t-item .n.num')).map(function(s){return s.textContent;}).join(',')`);
  check('bulk add: numbers 01..04', nums === '01,02,03,04', nums);
  check('Contenu section counter', (await ev(`document.querySelector('[data-count-for="content"]').textContent`)) === '4 épisodes');
  await click(`#localTree [data-node="${epId}"]`); await wait(200);
  await shot('L4-content-1440');

  // 3b. Cast: people of the directory, role, type, "whole season"
  check('cast: block shown for an episode', await vis('.cm-cast'));
  const addCast = async name => { await type('#lfCastAdd', name); await click('[data-cast-add]'); await wait(150); };
  await addCast('camille DURAND');
  check('cast: add by name (case-insensitive)', (await ev(`document.querySelectorAll('.cm-cast-row').length`)) === 1 && (await ev(`document.querySelector('.cm-cast-name').textContent`)) === 'Camille Durand');
  await addCast('Inconnu Total');
  check('cast: unknown person refused with a message', (await ev(`document.querySelector('#lfCastMsg').textContent`)).includes('pas dans l\'annuaire') && (await ev(`document.querySelectorAll('.cm-cast-row').length`)) === 1);
  await addCast('emile lefevre');
  check('cast: add without accents', (await ev(`document.querySelectorAll('.cm-cast-row').length`)) === 2);
  await addCast('Émile Lefèvre');
  check('cast: duplicate refused', (await ev(`document.querySelector('#lfCastMsg').textContent`)).includes('déjà') && (await ev(`document.querySelectorAll('.cm-cast-row').length`)) === 2);
  await type('[data-cast-role="0"]', 'Elle-même');
  await ev(`(function(){var s=document.querySelector('[data-cast-type="1"]'); s.value='Director'; s.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await addCast('Zoé Martin'); await click('[data-cast-remove="2"]'); await wait(150);
  check('cast: removal', (await ev(`document.querySelectorAll('.cm-cast-row').length`)) === 2);
  await shot('L4b-cast-1440');
  // casting disabled in the settings: the cast block says so
  check('cast: no warning when casting is on', !(await vis('.cm-cast-msg[role="alert"]')));
  await ev(`document.querySelector('#CustomMetadataConfigPage').setAttribute('data-casting','off'); true`);
  await click(`#localTree [data-node="${seasonId}"]`); await wait(150); await click(`#localTree [data-node="${epId}"]`); await wait(250);
  check('cast: warning when casting is off', (await vis('.cm-cast-msg[role="alert"]')) && (await ev(`document.querySelector('.cm-cast-msg[role="alert"]').textContent`)).includes('Importer le casting'));
  await ev(`document.querySelector('#CustomMetadataConfigPage').setAttribute('data-casting','on'); true`);
  await click(`#localTree [data-node="${seasonId}"]`); await wait(150); await click(`#localTree [data-node="${epId}"]`); await wait(250);
  await click('[data-cast-season]'); await wait(200);
  check('cast: applied to the whole season', (await ev(`document.querySelector('#lfCastMsg').textContent`)).startsWith('6 ajout'));

  // 4. Saving: content first, then the source
  check('unsaved changes flagged', (await ev(`document.querySelector('#dirtyState').textContent`)).includes('non enregistrées'));
  await click('#sourceForm button[type=submit]'); await wait(600);
  const mock = await ev(`JSON.stringify({calls: __mock.calls, alerts: __mock.alerts, sources: __mock.config.Sources, local: __mock.local})`);
  const m = JSON.parse(mock);
  const src = m.sources.find(s => s.SourceType === 'Local') || {};
  check('content PUT before the config', m.calls.findIndex(c => c.startsWith('PUT ')) >= 0 && m.calls.findIndex(c => c.startsWith('PUT ')) < m.calls.indexOf('PLUGIN'), m.calls.join(','));
  check('source saved: type, prefix, name', src.SourceType === 'Local' && src.Prefix === 'loc' && src.Name === 'Mes vidéos');
  check('source saved: imposed endpoints and mappings', src.BaseUrl === 'local://local' && src.VideoEndpoint === '/videos/{id}' && src.EpisodeListEndpoint === '/sources/{id}/videos'
    && src.VideoFieldMappings.Title === 'title' && src.EpisodeListMappings.PreviewUrl === 'preview.url' && src.EpisodeListMappings.Id === 'id');
  check('source saved: LocalDataId (16 hex)', /^[0-9a-f]{16}$/.test(src.LocalDataId || ''));
  const content = m.local[src.LocalDataId];
  const ep = content && content.Series[0].Seasons[0].Episodes.find(e => e.Title === 'Le pilote');
  check('content saved: 1 series, 1 season, 4 episodes', content && content.Series.length === 1 && content.Series[0].Seasons[0].Episodes.length === 4);
  check('content saved: episode fields', ep && ep.Duration === '24:30' && /^local:\/\/img\/[0-9a-f]{16}\/ep-[a-z0-9]+\.jpg$/.test(ep.Thumb) && ep.IndexNumber === 1 && ep.Tags.join() === 'a,b,A', JSON.stringify(ep));
  check('the API already present is untouched', m.sources[0].Name === 'API démo' && !m.sources[0].SourceType);
  check('aucune alerte d\'erreur', m.alerts.length === 0, m.alerts.join(' / '));

  const castSaved = JSON.parse(await ev(`JSON.stringify(Object.values(__mock.local)[0].Series[0].Seasons[0].Episodes.map(function(e){return {t:e.Title,c:e.Cast};}))`));
  const pilot = castSaved.find(e => e.t === 'Le pilote');
  check('cast saved: role and type', pilot && pilot.c.length === 2 && pilot.c[0].PersonId === 'p1' && pilot.c[0].Role === 'Elle-même' && pilot.c[1].Type === 'Director', JSON.stringify(pilot));
  check('cast saved: copied to the 3 other episodes', castSaved.filter(e => e.c && e.c.length === 2).length === 4, JSON.stringify(castSaved.map(e => e.c && e.c.length)));

  // 5. Back on the list: "Locale" card, statistics, banner
  check('back on the list', await vis('#listView'));
  check('local card: "Locale" badge, no Tester link', (await ev(`(function(){var c=document.querySelectorAll('${P} #sourcesList .cm-card')[1]; return !!c.querySelector('.cm-kind') && !Array.from(c.querySelectorAll('button')).some(function(b){return b.textContent.trim()==='Tester';});})()`)));
  check('local card: statistics', (await ev(`document.querySelector('#sourcesList [data-local-stats]').textContent`)) === '1 série · 1 saison · 4 épisodes');
  check('banner after saving', await vis('#postSaveNotice'));
  check('banner: "Appliquer aux épisodes" offered', await vis('#btnNoticeApply'));
  await click('#btnNoticeApply'); await wait(300);
  check('"Appliquer aux épisodes" calls the server for this source', (await ev(`__mock.calls.filter(function(c){return c.indexOf('POST')===0 && c.indexOf('People/Apply')>0;}).length`)) === 1);
  await shot('L5-list-1440');

  // 6. Reopening: the content is reloaded, deleting an episode, saving
  await click(`${P} #sourcesList .cm-card:nth-child(2) .raised`); await wait(500);
  check('reopening: local type and content reloaded', (await ev(`document.querySelector('#editorView').getAttribute('data-source-type')`)) === 'local' && (await ev(`document.querySelectorAll('#localTree .cm-t-item').length`)) === 6);
  await click(`${P} [data-go="content"]`); await wait(200);
  const quatriemeId = await ev(`Array.from(document.querySelectorAll('#localTree .cm-t-item')).find(function(b){return b.textContent.indexOf('Quatrième')>=0;}).getAttribute('data-node')`);
  await click(`#localTree [data-node="${quatriemeId}"]`); await wait(150);
  await click(`#localDetail [data-del="${quatriemeId}"]`); await wait(250);
  check('deleting an episode', (await ev(`document.querySelectorAll('#localTree .cm-t-item').length`)) === 5);
  await click('#sourceForm button[type=submit]'); await wait(600);
  const after = JSON.parse(await ev(`JSON.stringify(__mock.local)`));
  const key = Object.keys(after)[0];
  check('deletion saved', after[key].Series[0].Seasons[0].Episodes.length === 3);

  // 7. Mobile
  await size(390, 900, true);
  await click(`${P} #sourcesList .cm-card:nth-child(2) .raised`); await wait(400);
  await click(`${P} [data-go="content"]`); await wait(200);
  await click(`#localTree [data-node="${epId}"]`); await wait(300);
  const overflow = await ev(`document.documentElement.scrollWidth > window.innerWidth + 2`);
  check('mobile: no horizontal scroll', !overflow);
  await shot('L6-content-mobile');
  await size(1440, 1000);

  // 8. Deleting the source: also deletes its content
  await click('#btnDeleteSource'); await wait(500);
  const end = JSON.parse(await ev(`JSON.stringify({calls: __mock.calls, sources: __mock.config.Sources.length, local: Object.keys(__mock.local).length})`));
  check('deletion: source removed and content deleted', end.sources === 1 && end.local === 0 && end.calls.some(c => c.startsWith('DELETE ')), JSON.stringify(end));

  // 8b. People source: third type, dedicated editor
  await click(`${P} .cm-toolbar .button-submit`); await wait(200);
  check('type choice: 3 options', (await ev(`document.querySelectorAll('#typeChooser [data-new-type]').length`)) === 3);
  await click('#typeChooser [data-new-type="people"]'); await wait(300);
  const pnav = await ev(`Array.from(document.querySelectorAll('${P} .cm-nav-item')).filter(function(b){return b.offsetParent!==null;}).map(function(b){return b.getAttribute('data-go');}).join(',')`);
  check('people source: sections = general,person,network,thumbs', pnav === 'general,person,network,thumbs', pnav);
  check('people source: URL visible, endpoint and mapping pre-filled', (await vis('#srcBaseUrl')) && (await ev(`document.querySelector('#srcPersonEndpoint').value`)) === '/people/{id}' && (await ev(`document.querySelector('#srcPersonName').value`)) === 'name');
  check('people source: the test only offers "Personne"', (await ev(`Array.from(document.querySelector('#testItemType').options).filter(function(o){return !o.hidden;}).map(function(o){return o.value;}).join(',')`)) === 'person' && (await ev(`document.querySelector('#testItemType').value`)) === 'person');
  await type('#srcName', 'Personnes démo'); await type('#srcPrefix', 'pe'); await type('#srcBaseUrl', 'http://people.test');
  await shot('L8-people-source-1440');
  await click('#sourceForm button[type=submit]'); await wait(600);
  const ps = JSON.parse(await ev(`JSON.stringify(__mock.config.Sources)`)).find(x => x.SourceType === 'People') || {};
  check('people source saved', ps.Prefix === 'pe' && ps.PersonEndpoint === '/people/{id}' && ps.PersonMappings.Photo === 'photo' && ps.BaseUrl === 'http://people.test' && !ps.EpisodeListEndpoint, JSON.stringify(ps));
  check('card: "Personnes" badge', (await ev(`Array.from(document.querySelectorAll('${P} #sourcesList .cm-kind')).map(function(b){return b.textContent;}).join()`)).includes('Personnes'));
  await click(`${P} #sourcesList .cm-card:nth-child(2) .raised`); await wait(300);
  await click('#btnDeleteSource'); await wait(400);

  // 9. API source: the editor is unchanged
  await click(`${P} #sourcesList .cm-card:nth-child(1) .raised`); await wait(300);
  const apiNav = await ev(`Array.from(document.querySelectorAll('${P} .cm-nav-item')).filter(function(b){return b.offsetParent!==null;}).length`);
  check('API source: the 8 sections are still there', apiNav === 8, String(apiNav));
  check('source API : URL de base visible et obligatoire', (await vis('#srcBaseUrl')) && (await ev(`document.querySelector('#srcBaseUrl').required`)) === true);
  check('source API : panneau de test visible', await vis('.cm-test'));
  // changing the prefix of an existing source: warning then confirmation
  check('prefix unchanged: no warning', !(await vis('#prefixWarn')));
  await type('#srcPrefix', 'zz');
  check('prefix changed: warning with the old and the new tag', (await vis('#prefixWarn')) && (await ev(`document.querySelector('#prefixWarn').textContent`)).includes('{dm-…}') && (await ev(`document.querySelector('#prefixWarn').textContent`)).includes('{zz-…}'));
  await shot('L9-prefix-warning-1440');
  await type('#srcPrefix', 'dm');
  check('prefix restored: warning removed', !(await vis('#prefixWarn')));
  await type('#srcPrefix', 'zz');
  await ev(`window.confirm = function () { return false; }; true`);
  await click('#sourceForm button[type=submit]'); await wait(400);
  check('prefix: confirmation refused = stay in the editor, nothing saved', (await vis('#editorView')) && JSON.parse(await ev(`JSON.stringify(__mock.config.Sources.map(function(x){return x.Prefix;}))`))[0] === 'dm');
  await ev(`window.confirm = function () { return true; }; true`);
  await click('#sourceForm button[type=submit]'); await wait(500);
  check('prefix: confirmation = saved', JSON.parse(await ev(`JSON.stringify(__mock.config.Sources.map(function(x){return x.Prefix;}))`))[0] === 'zz');
  await shot('L7-api-editor-1440');
} finally {
  console.log(`\n${pass} PASS, ${fail} FAIL`);
  ws.close(); chrome.kill(); fs.rmSync(profile, { recursive: true, force: true });
  process.exit(fail ? 1 : 0);
}
