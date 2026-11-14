// Screenshots of the local image gallery in the REAL jellyfin-web: the editor block, then Jellyfin's own "Modifier les images" picker listing them.
// Usage: node local-gallery-shots.mjs <base_url> <out_dir> <images_dir> [user] [password]
// Optional env SERIES_DIR: the folder of the series on this machine; Principal/ and Fond/ sub-folders are created in it (and removed at the end).
// images_dir holds poster1..3.jpg and back1..2.jpg. Seeds a local source over the series "dm-aventuriers" and restores everything.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
const [J, outDir, imgDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir || !imgDir) { console.error('usage: node local-gallery-shots.mjs <base_url> <out_dir> <images_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc', DATA = 'galshots0000001', port = 9337;
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
const ev = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
const shot = async (name, clipSel) => {
  let clip;
  if (clipSel) { const r = JSON.parse(await ev(`(function(){var e=document.querySelector(${JSON.stringify(clipSel)}); if(!e) return 'null'; var b=e.getBoundingClientRect(); return JSON.stringify({x:Math.max(0,b.x+window.scrollX-12),y:Math.max(0,b.y+window.scrollY-12),width:b.width+24,height:b.height+24,scale:1});})()`)); if (r) clip = r; }
  const r = await send('Page.captureScreenshot', { format: 'png', ...(clip ? { clip, captureBeyondViewport: true } : {}) });
  const f = path.join(outDir, `${name}.png`); fs.writeFileSync(f, Buffer.from(r.result.data, 'base64')); console.log('📸', f);
};
const until = async (expr, ms = 20000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await ev(expr)) return true; await wait(300); } console.log('⚠️ timeout:', expr); return false; };
const C = '#CustomMetadataConfigPage';
const center = async sel => JSON.parse(await ev(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return 'null'; e.scrollIntoView({block:'center'}); var r=e.getBoundingClientRect(); return JSON.stringify({x:r.x+r.width/2,y:r.y+r.height/2});})()`));
const click = async sel => { const p = await center(sel); if (!p) { console.log('⚠️ not found:', sel); return false; } for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) await send('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: 'left', clickCount: 1 }); return true; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-galshots", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const rest = async (m, u, b) => { const r = await fetch(J + u, { method: m, headers: H, body: b === undefined ? undefined : JSON.stringify(b) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const seriesDir = process.env.SERIES_DIR;
const made = [];
if (seriesDir) {
  for (const [sub, files] of [['Principal', ['poster2.jpg', 'poster3.jpg']], ['Fond', ['back2.jpg']]]) {
    fs.mkdirSync(path.join(seriesDir, sub), { recursive: true }); made.push(path.join(seriesDir, sub));
    for (const f of files) fs.copyFileSync(path.join(imgDir, f), path.join(seriesDir, sub, 'dossier-' + f));
  }
  await rest('POST', '/Library/Refresh'); await wait(10000);
}
const original = await rest('GET', `/Plugins/${PLUGIN}/Configuration`);
try {
  const cfg = JSON.parse(JSON.stringify(original));
  cfg.Sources = cfg.Sources.filter(s => s.LocalDataId !== DATA);
  cfg.Sources.push({ Name: 'Galerie test', Prefix: 'gl', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local', VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  const upload = async file => { const r = await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/jpeg', Authorization: H.Authorization }, body: fs.readFileSync(path.join(imgDir, file)) }); const j = await r.json(); return j.Url ?? j.url; };
  const gallery = [];
  for (const f of ['poster1.jpg', 'poster2.jpg', 'poster3.jpg']) gallery.push({ Type: 'Primary', Url: await upload(f) });
  for (const f of ['back1.jpg', 'back2.jpg']) gallery.push({ Type: 'Backdrop', Url: await upload(f) });
  await rest('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Overview: 'Série de démonstration.', Poster: gallery[0].Url, Seasons: [], Gallery: gallery }] });
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, cfg);

  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1100, deviceScaleFactor: 1, mobile: false });
  await send('Page.navigate', { url: `${J}/web/#/login` });
  if (await until(`!!document.querySelector('#txtManualName')`)) {
    await ev(`(function(){var n=document.querySelector('#txtManualName'); n.value=${JSON.stringify(user)}; n.dispatchEvent(new Event('input',{bubbles:true})); var p=document.querySelector('#txtManualPassword'); p.value=${JSON.stringify(password)}; p.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
    await until(`!location.hash.includes('login')`); await wait(1500);
  }
  // 1. the editor
  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider` });
  await until(`!!document.querySelector('${C} .cm-card')`, 30000); await wait(1000);
  await ev(`(function(){var c=Array.from(document.querySelectorAll('${C} .cm-card')).find(function(x){return x.textContent.indexOf('Galerie test')>=0;}); c.querySelector('.raised').click(); return true;})()`); await wait(900);
  await click(`${C} [data-go="content"]`); await wait(500);
  await click(`${C} #localTree [data-node="s:dm-aventuriers"]`); await wait(600);
  await ev(`document.querySelector('${C} #lfGallery').scrollIntoView({block:'center'}); true`); await wait(500);
  await shot('G1-editor-gallery', `${C} #lfGallery`);
  await ev(`document.querySelector('${C} [data-apply-images]').scrollIntoView({block:'center'}); true`); await wait(300);
  await shot('G2-editor-full');
  if (seriesDir) {
    await click(`${C} [data-gal-folders]`); await wait(1500);
    await ev(`document.querySelector('${C} #lfGallery').scrollIntoView({block:'start'}); true`); await wait(400);
    await shot('G8-editor-after-folder-import');
  }

  // 2. Jellyfin's own picker
  const series = ((await rest('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).Items || []).find(i => /aventuriers/i.test(i.Name));
  await send('Page.navigate', { url: `${J}/web/#/details?id=${series.Id}` });
  await until(`!!document.querySelector('.btnMoreCommands')`, 20000); await wait(1500);
  await shot('G3-series-page');
  await click('.btnMoreCommands'); await wait(800);
  await shot('G4-more-menu');
  const editImages = await ev(`(function(){var b=document.querySelector('.actionSheetMenuItem[data-id="editimages"]'); if(!b) return false; b.click(); return true;})()`);
  console.log('edit images menu item:', editImages); await wait(1500);
  await shot('G5-image-editor');
  const browse = await ev(`(function(){var b=document.querySelector('.btnBrowseAllImages'); if(!b) return false; b.click(); return true;})()`);
  console.log('browse button:', browse); await wait(2500);
  await shot('G6-picker');
  await ev(`(function(){var s=document.querySelector('.selectImageType, #selectImageType, select[class*="ImageType"]'); if(!s) return false; s.value='Backdrop'; s.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`); await wait(2500);
  await shot('G7-picker-backdrops');
} finally {
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await rest('DELETE', `/CustomMetadata/Local/${DATA}`);
  for (const d of made) fs.rmSync(d, { recursive: true, force: true });
  ws.close(); chrome.kill(); try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5 }); } catch {}
  process.exit(0);
}
