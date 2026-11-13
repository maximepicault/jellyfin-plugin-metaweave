// Plugin shortcuts in the left menu (Extensions section) of the REAL jellyfin-web (theme included) against the real server, through headless Chrome + the DevTools protocol.
// Usage: node config-menu-real.mjs <base_url> <out_dir> [user] [password]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node shoot.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const port = 9338;
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

let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { if (ok) { pass++; console.log('PASS', name); } else { fail++; console.log('FAIL', name, detail); } };
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
  await send('Page.navigate', { url: `${J}/web/#/dashboard` });
  await until(`Array.from(document.querySelectorAll('a')).some(function(a){return a.textContent.trim()==='Personnes';})`, 20000); await wait(800);
  const links = await evalJs(`JSON.stringify(Array.from(document.querySelectorAll('.MuiDrawer-root a, nav a')).map(function(a){return a.textContent.trim()+' -> '+a.getAttribute('href');}).filter(function(t){return /Custom|Personnes|manquants|Extensions/i.test(t);}))`);
  console.log('menu :', links);
  check('raccourci « Custom Metadata »', links.includes('Custom Metadata'));
  check('shortcut « Épisodes manquants »', links.includes('Épisodes manquants'));
  check('raccourci « Personnes »', links.includes('Personnes'));
  await shot('M1-drawer-1440');

  // The plugin's "Settings" page must stay the configuration. jellyfin-web (findBestConfigurationPage) takes the FIRST page flagged
  // EnableInMainMenu in the order returned by the server: we check that order.
  const pages = JSON.parse(await evalJs(`(async function(){var r=await fetch(ApiClient.getUrl('web/ConfigurationPages'),{headers:{'X-Emby-Token':ApiClient.accessToken()}}); return JSON.stringify((await r.json()).filter(function(p){return p.PluginId.replace(/-/g,'')==='12345678123412341234123456789abc';}).map(function(p){return {n:p.Name,m:p.EnableInMainMenu};}));})()`));
  console.log('plugin pages, in the server order:', pages.map(p => p.n + (p.m ? ' (menu)' : '')).join(' | '));
  const first = pages.find(p => p.m);
  check('the first page flagged "menu" is the configuration', first && first.n === 'Custom Metadata Provider', JSON.stringify(first));
  // a menu shortcut does open the targeted page
  await send('Page.navigate', { url: `${J}/web/#/dashboard` });
  await until(`Array.from(document.querySelectorAll('a')).some(function(a){return a.textContent.trim()==='Personnes';})`, 20000);
  await evalJs(`Array.from(document.querySelectorAll('a')).find(function(a){return a.textContent.trim()==='Personnes';}).click(); true`); await wait(2500);
  check('the "Personnes" shortcut opens the People screen', (await evalJs(`location.hash`)).includes('CustomMetadataPeople') && (await evalJs(`!!document.querySelector('#CustomMetadataPeoplePage')`)) === true);
  await shot('M3-people-from-menu-1440');
  console.log(`\n${pass} PASS, ${fail} FAIL`);
} finally { ws.close(); chrome.kill(); try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) {} process.exit(fail ? 1 : 0); }
