// Local source editor in the REAL jellyfin-web (theme included), through headless Chrome + the DevTools protocol.
// Usage: node config-local-real.mjs <base_url> <out_dir> [user] [password]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node shoot.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const port = 9335;
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
const C = '#CustomMetadataConfigPage';
const click = sel => evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return false; e.click(); return true;})()`);
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
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider` });
  await until(`!!document.querySelector('${C} .cm-card')`, 30000); await wait(1200);
  await shot('R1-sources-local-1440');
  await click(`${C} .cm-add`); await wait(400);
  await shot('R2-chooser-1440');
  await click(`${C} #btnCancelChooser`);
  // opens the local source (2nd « Locale » card)
  await evalJs(`(function(){var c=Array.from(document.querySelectorAll('${C} .cm-card')).find(function(x){return x.querySelector('.cm-kind');}); c.querySelector('.raised').click(); return true;})()`); await wait(900);
  await click(`${C} [data-go="content"]`); await wait(600);
  const first = await evalJs(`(function(){var b=Array.from(document.querySelectorAll('${C} #localTree .cm-t-item')).find(function(x){return x.textContent.indexOf('Deuxième')>=0;}); if(!b) return false; b.click(); return true;})()`);
  console.log('episode opened:', first); await wait(500);
  await shot('R3-editor-content-1440');
  // Upload of a real file from the browser (ApiClient.ajax with a Blob) on the « Quatrième » episode
  await evalJs(`(function(){var b=Array.from(document.querySelectorAll('${C} #localTree .cm-t-item')).find(function(x){return x.textContent.indexOf('Quatrième')>=0;}); b.click(); return true;})()`); await wait(400);
  await evalJs(`(function(){var b=document.querySelector('${C} [data-clear-img="Thumb"]'); if(b) b.click(); return true;})()`); await wait(300);
  console.log('thumbnail removed before upload:', await evalJs(`document.querySelector('${C} #lf-Thumb').disabled === false && !document.querySelector('${C} .cm-imgprev img')`));
  const up = await evalJs(`(async function(){
    var c=document.createElement('canvas'); c.width=640; c.height=360; var g=c.getContext('2d'); g.fillStyle='#2c7a4b'; g.fillRect(0,0,640,360); g.fillStyle='#fff'; g.font='48px sans-serif'; g.fillText('Miniature envoyée',120,190);
    var blob=await new Promise(function(r){c.toBlob(r,'image/png');});
    var dt=new DataTransfer(); dt.items.add(new File([blob],'mini.png',{type:'image/png'}));
    var i=document.querySelector('${C} [data-file="Thumb"]'); i.files=dt.files; i.dispatchEvent(new Event('change',{bubbles:true}));
    await new Promise(function(r){setTimeout(r,1500);});
    var img=document.querySelector('${C} .cm-imgprev img');
    return JSON.stringify({thumbDisabled: document.querySelector('${C} #lf-Thumb').disabled, placeholder: document.querySelector('${C} #lf-Thumb').placeholder, imgLoaded: !!img && img.complete && img.naturalWidth>0, size: img ? img.naturalWidth+'x'+img.naturalHeight : null, dirty: document.querySelector('${C} #dirtyState').textContent});
  })()`);
  console.log('real upload:', up);
  await evalJs(`document.querySelector('${C} .cm-imgfield').scrollIntoView({block:'center'}); true`); await wait(400);
  await shot('R7-upload-1440');
  // a season's form (« Ajouter plusieurs épisodes » area)
  await evalJs(`(function(){var b=Array.from(document.querySelectorAll('${C} #localTree .cm-t-item')).find(function(x){return x.textContent.indexOf('Saison locale')>=0;}); b.click(); return true;})()`); await wait(500);
  await evalJs(`document.querySelector('${C} #lfBulk').scrollIntoView({block:'center'}); true`); await wait(300);
  await shot('R6-season-bulk-1440');
  // Cast of an episode (real directory)
  await evalJs(`(function(){var b=Array.from(document.querySelectorAll('${C} #localTree .cm-t-item')).find(function(x){return x.textContent.indexOf('Premier')>=0;}); b.click(); return true;})()`); await wait(900);
  await evalJs(`document.querySelector('${C} .cm-cast').scrollIntoView({block:'center'}); true`); await wait(400);
  console.log('distribution :', await evalJs(`Array.from(document.querySelectorAll('${C} .cm-cast-row')).map(function(r){return r.querySelector('.cm-cast-name').textContent+' / '+r.querySelector('.cm-cast-type').value;}).join(' | ')`));
  await shot('R8-cast-1440');
  // Source type choice and editor of a people source
  await evalJs(`window.confirm=function(){return true;}; document.querySelector('${C} #btnBackToList').click(); true`); await wait(500);
  await click(`${C} .cm-toolbar .button-submit`); await wait(400);
  await shot('R9-chooser-3-types-1440');
  await click(`${C} [data-new-type="people"]`); await wait(700);
  await shot('R10-people-source-editor-1440');
  await size(1920, 1100); await wait(500); await shot('R4-editor-content-1920');
  await size(412, 900, true); await wait(600); await shot('R5-editor-content-mobile');
  console.log('page errors:', await evalJs(`document.querySelectorAll('${C} #localTree .cm-t-item').length + ' rows in the tree'`));
} finally { ws.close(); chrome.kill(); fs.rmSync(profile, { recursive: true, force: true }); process.exit(0); }
