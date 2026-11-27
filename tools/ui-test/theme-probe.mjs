// Opens one jellyfin-web page with the Projection theme and evaluates expressions in it (theme debugging aid).
// Usage: node theme-probe.mjs <base_url> <hash> <width> '<js expression>' ['<js expression>' ...] [--layout=experimental] [--shot=/path.png] [--served: import the theme from the plugin's route instead of pasting it] [--hover=<selector> [--hoverwait=ms], then 'after:<expr>' expressions]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { trackChrome, chromeTmp } from './chrome-session.mjs';

const args = process.argv.slice(2);
const opt = name => (args.find(a => a.startsWith(`--${name}=`)) || '').slice(name.length + 3);
const [J, hash, width, ...exprs] = args.filter(a => !a.startsWith('--'));
const layout = opt('layout') || 'experimental';
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const css = fs.readFileSync(path.join(repo, 'theme/projection.css'), 'utf8');
const port = 9335;
const profile = fs.mkdtempSync('/tmp/tp-');
const chrome = spawn('google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore', detached: true, env: { ...process.env, TMPDIR: chromeTmp() } });
trackChrome(chrome, profile);
const UA_MOBILE = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36';
const UA_DESKTOP = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';
const wait = ms => new Promise(r => setTimeout(r, ms));
let targets = [];
for (let i = 0; i < 50 && !targets.length; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch {} await wait(200); }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => (ws.onopen = r));
let id = 0; const pending = {};
ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending[d.id]) { pending[d.id](d); delete pending[d.id]; }
  if (d.method === 'Runtime.exceptionThrown') console.log('❌', d.params.exceptionDetails?.exception?.description);
  if (d.method === 'Runtime.consoleAPICalled' && /projection/i.test(JSON.stringify(d.params.args))) console.log('🖥️ ', d.params.args.map(a => a.value ?? a.description).join(' ')); };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); return r.result?.exceptionDetails ? 'ERR ' + r.result.exceptionDetails.exception?.description : r.result?.result?.value; };
const until = async (expr, ms = 20000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await evalJs(expr)) return true; await wait(300); } return false; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="theme-probe", Device="cli", DeviceId="theme-probe", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const before = await (await fetch(`${J}/System/Configuration/branding`, { headers: H })).json();
await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify({ ...before, CustomCss: args.includes('--no-css') ? '' : args.includes('--served') ? '@import url("../CustomMetadata/Theme/projection.css");' : css }) });
try {
  await send('Page.enable'); await send('Runtime.enable');
  const w = Number(width); const mobile = w < 600;
  await send('Emulation.setUserAgentOverride', { userAgent: mobile ? UA_MOBILE : UA_DESKTOP });
  await send('Emulation.setDeviceMetricsOverride', { width: w, height: mobile ? 860 : 900, deviceScaleFactor: 1, mobile });
  await send('Page.navigate', { url: `${J}/web/#/login` });
  await until(`!!document.querySelector('#txtManualName')`);
  await evalJs(`(function(){var n=document.querySelector('#txtManualName'); n.value='admin'; n.dispatchEvent(new Event('input',{bubbles:true}));
    var p=document.querySelector('#txtManualPassword'); p.value='admin'; p.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
  await until(`!location.hash.includes('login')`); await wait(1000);
  await evalJs(`localStorage.setItem('layout', ${JSON.stringify(layout)}); true`);
  await send('Page.reload'); await wait(2500);
  let h = hash;
  if (h.includes('{')) { // {series} {season} {episode}: first matching item
    const ids = await evalJs(`(async function(){var u=ApiClient.getCurrentUserId(); var s=(await ApiClient.getItems(u,{IncludeItemTypes:'Series',Recursive:true,SortBy:'SortName'})).Items.find(i=>i.Name==='Les Aventuriers');
      var se=(await ApiClient.getItems(u,{ParentId:s.Id,IncludeItemTypes:'Season'})).Items[0]; var e=(await ApiClient.getItems(u,{ParentId:se.Id,IncludeItemTypes:'Episode'})).Items[0];
      var op=(await ApiClient.getItems(u,{IncludeItemTypes:'Series',Recursive:true})).Items.find(i=>i.Name.startsWith('One Piece')); return {series:s.Id, season:se.Id, episode:e.Id, op: op && op.Id};})()`);
    h = h.replace('{op}', ids.op).replace('{series}', ids.series).replace('{season}', ids.season).replace('{episode}', ids.episode);
  }
  await send('Page.navigate', { url: `${J}/web/${h}` });
  await wait(4500);
  for (const e of exprs.filter(x => !x.startsWith('after:'))) console.log('▶', e.slice(0, 80), '\n ', JSON.stringify(await evalJs(e), null, 1));
  const hover = opt('hover');   // --hover=<selector>: a real mouse move onto its centre, then a pause
  if (hover) {
    const pos = await evalJs(`(function(){var e=document.querySelector(${JSON.stringify(hover)}); if(!e) return null; e.scrollIntoView({block:'center'}); var r=e.getBoundingClientRect(); return {x:r.x+r.width/2, y:r.y+r.height/2};})()`);
    if (pos) { await wait(300); await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pos.x, y: pos.y }); await wait(Number(opt('hoverwait') || 2200)); for (const e of exprs) { if (e.startsWith('after:')) console.log('▶', e, '\n ', JSON.stringify(await evalJs(e.slice(6)), null, 1)); } } else { console.log('⚠️ hover target not found:', hover); }
  }
  const shotPath = opt('shot');
  if (shotPath) { const r = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(shotPath, Buffer.from(r.result.data, 'base64')); console.log('📸', shotPath); }
} finally {
  await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify(before) }).catch(() => {});
  ws.close();
}
process.exit(0);
