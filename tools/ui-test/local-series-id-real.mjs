// A series added in a LOCAL source, its id changed, "Enregistrer" clicked with the real mouse, then the source reopened:
// the new id must be there. Real jellyfin-web, headless Chrome + DevTools, real typing (Input.insertText) and real clicks.
// Usage: node local-series-id-real.mjs <base_url> <out_dir> [user] [password]
// Creates a local source in the test container's configuration and removes it at the end. Prints PASS / FAIL.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { trackChrome, chromeTmp } from './chrome-session.mjs';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node local-series-id-real.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'aaaa0000bbbb1111';
const port = 9336;
const profile = fs.mkdtempSync(path.join(outDir, '.profile-'));
const chrome = spawn('google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore', detached: true, env: { ...process.env, TMPDIR: chromeTmp() } });
trackChrome(chrome, profile);
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

let targets = [];
for (let i = 0; i < 50 && !targets.length; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch {} await wait(200); }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => (ws.onopen = r));
let id = 0; const pending = {};
ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending[d.id]) { pending[d.id](d); delete pending[d.id]; } };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png' }); const f = path.join(outDir, `${name}.png`); fs.writeFileSync(f, Buffer.from(r.result.data, 'base64')); console.log('📸', f); };
const until = async (expr, ms = 20000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await evalJs(expr)) return true; await wait(300); } console.log('⚠️ timeout:', expr); return false; };
const C = '#CustomMetadataConfigPage';
const center = async sel => JSON.parse(await evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return 'null'; e.scrollIntoView({block:'center'}); var r=e.getBoundingClientRect(); return JSON.stringify({x:r.x+r.width/2,y:r.y+r.height/2});})()`));
const mouseClick = async sel => { const p = await center(sel); if (!p) { console.log('⚠️ not found:', sel); return false; }
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) await send('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: 'left', clickCount: 1 }); return true; };
// Real typing into a field: click it (real focus), select all, insert the text like a keyboard would
const typeInto = async (sel, text) => { await mouseClick(sel); await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'a', code: 'KeyA', modifiers: 2, commands: ['selectAll'] }); await send('Input.insertText', { text }); };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-seriesid", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const rest = async (m, u, b) => { const r = await fetch(J + u, { method: m, headers: H, body: b === undefined ? undefined : JSON.stringify(b) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const original = await rest('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  const cfg = JSON.parse(JSON.stringify(original));
  cfg.Sources = cfg.Sources.filter(s => s.LocalDataId !== DATA);
  cfg.Sources.push({ Name: 'Locale test', Prefix: 'lt', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await rest('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [] });
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, cfg);

  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await send('Page.navigate', { url: `${J}/web/#/login` });
  if (await until(`!!document.querySelector('#txtManualName')`)) {
    await evalJs(`(function(){var n=document.querySelector('#txtManualName'); n.value=${JSON.stringify(user)}; n.dispatchEvent(new Event('input',{bubbles:true}));
      var p=document.querySelector('#txtManualPassword'); p.value=${JSON.stringify(password)}; p.dispatchEvent(new Event('input',{bubbles:true}));
      document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
    await until(`!location.hash.includes('login')`); await wait(1500);
  }
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider` });
  await until(`!!document.querySelector('${C} .cm-card')`, 30000); await wait(1000);
  // open the local source
  await evalJs(`(function(){var c=Array.from(document.querySelectorAll('${C} .cm-card')).find(function(x){return x.textContent.indexOf('Locale test')>=0;}); c.querySelector('.raised').click(); return true;})()`); await wait(900);
  await mouseClick(`${C} [data-go="content"]`); await wait(500);
  await mouseClick(`${C} #localDetail [data-add="series"]`); await wait(400);
  await typeInto(`${C} #lf-Title`, 'Ma série réelle'); await wait(200);
  const before = await evalJs(`document.querySelector('${C} #lf-NodeId').value`);
  await typeInto(`${C} #lf-NodeId`, 'abc123'); await wait(200);
  check('the id field shows what was typed', (await evalJs(`document.querySelector('${C} #lf-NodeId').value`)) === 'abc123', await evalJs(`document.querySelector('${C} #lf-NodeId').value`));
  await shot('S1-id-typed');
  // "Enregistrer" with a real mouse click (the field loses focus on the way)
  await mouseClick(`${C} #sourceForm button[type=submit]`); await wait(1500);
  const stored = await rest('GET', `/CustomMetadata/Local/${DATA}`);
  const storedId = (stored.Series || stored.series || [])[0]?.Id ?? (stored.Series || stored.series || [])[0]?.id;
  check('what the server stored: the typed id (not the generated one)', storedId === 'abc123', `stored=${storedId} generated=${before}`);
  const config = await rest('GET', `/Plugins/${PLUGIN}/Configuration`);
  check('the source is still in the configuration', (config.Sources || []).some(s => s.LocalDataId === DATA));
  await shot('S2-after-save');
  // reopen the source: the tree shows the saved id
  await until(`!!document.querySelector('${C} .cm-card')`, 15000);
  await evalJs(`(function(){var c=Array.from(document.querySelectorAll('${C} .cm-card')).find(function(x){return x.textContent.indexOf('Locale test')>=0;}); if(c) c.querySelector('.raised').click(); return true;})()`); await wait(900);
  await mouseClick(`${C} [data-go="content"]`); await wait(600);
  const shown = await evalJs(`(Array.from(document.querySelectorAll('${C} #localTree .cm-t-item'))[0]||{getAttribute:function(){return null;}}).getAttribute('data-node')`);
  check('reopened: the tree shows the saved id', shown === 's:abc123', String(shown));
  await shot('S3-reopened');
} finally {
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await rest('DELETE', `/CustomMetadata/Local/${DATA}`);
  ws.close(); chrome.kill(); try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5 }); } catch { /* chrome still flushing */ }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
