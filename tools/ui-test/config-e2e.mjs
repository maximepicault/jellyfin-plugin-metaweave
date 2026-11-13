// End-to-end test of the configuration screen (real jellyfin-web, headless Chrome + DevTools).
// Usage: node config-e2e.mjs <base_url> <out_dir> [user] [password]   (e.g. http://localhost:8097 /tmp/e2e)
// Changes the plugin configuration of the test container (edit, enable/disable, add, delete a source,
// global settings), checks what is really stored on the server (including the fields the form
// does not show: Cast, ProductionYear, BannerUrl…), then restores the original configuration. Prints PASS / FAIL.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node shoot.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const port = 9333;
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
const P = '#CustomMetadataMissingPage';

try {
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setFocusEmulationEnabled', { enabled: true });
  await send('Browser.grantPermissions', { permissions: ['clipboardReadWrite', 'clipboardSanitizedWrite'], origin: J }).catch(() => {});
  await size(1440, 1300);

  // Log in through the form (injecting jellyfin_credentials alone is not enough)
  await send('Page.navigate', { url: `${J}/web/#/login` });
  if (await until(`!!document.querySelector('#txtManualName')`)) {
    await evalJs(`(function(){var n=document.querySelector('#txtManualName'); n.value=${JSON.stringify(user)}; n.dispatchEvent(new Event('input',{bubbles:true}));
      var p=document.querySelector('#txtManualPassword'); p.value=${JSON.stringify(password)}; p.dispatchEvent(new Event('input',{bubbles:true}));
      document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
    await until(`!location.hash.includes('login')`); await wait(1500);
  }

  await send('Page.navigate', { url: `${J}/web/#/configurationpage?name=CustomMetadataMissingEpisodes` });
  await until(`!!document.querySelector('${P} .me-card')`, 30000); await wait(1500);
  await shot('1-series-1440');

  // Level 2: a series with several sources (a single-source series jumps straight to the episodes)
  await evalJs(`(function(){var c=document.querySelector('${P} [data-series][data-sources]:not([data-sources="1"])'); if(c) c.click(); return !!c;})()`); await wait(1200);
  await shot('2-sources-1440');
  // Level 3: back to the series then the most incomplete one (Tech Talks, a single source → direct jump)
  await evalJs(`document.querySelector('${P} .me-crumbs [data-nav=root]').click(); true`); await wait(800);
  await evalJs(`document.querySelector('${P} [data-series]').click(); true`); await wait(2000);
  console.log('🧭 saut source unique :', await evalJs(`document.querySelector('${P} .me-crumbs').textContent`));
  await shot('3-detail-1440');

  // Copy buttons: real mouse click (a JS .click() does not allow clipboard access)
  const clickAt = async sel => {
    const pos = await evalJs(`(function(){var b=document.querySelector('${P} ${sel}'); if(!b) return null; b.scrollIntoView({block:'center'}); var r=b.getBoundingClientRect(); return {x:r.x+r.width/2, y:r.y+r.height/2};})()`);
    if (!pos) { console.log('⚠️ absent :', sel); return false; }
    await wait(200);
    for (const type of ['mousePressed', 'mouseReleased']) await send('Input.dispatchMouseEvent', { type, x: pos.x, y: pos.y, button: 'left', clickCount: 1 });
    await wait(300); return true;
  };
  const clip = () => evalJs(`navigator.clipboard ? navigator.clipboard.readText().catch(e => 'ERR ' + e) : '(insecure context)'`);
  if (await clickAt('[data-copy]')) console.log('📋 bouton :', await evalJs(`document.querySelector('${P} [data-copy]').textContent`), '| clipboard:', await clip());
  if (await clickAt('[data-copy-all]')) console.log('📋 copy all:', await evalJs(`document.querySelector('${P} [data-copy-all]').textContent`), '| clipboard:', JSON.stringify(await clip()));
  await evalJs(`window.scrollTo(0,0); document.querySelector('.mainAnimatedPage, .page:not(.hide)') && window.scrollTo(0,0); true`); await wait(300);

  await size(1920, 1700); await wait(1000); await shot('4-detail-1920');
  await size(412, 1500, true); await wait(1000); await shot('5-detail-mobile');
  await evalJs(`document.querySelector('${P} .me-crumbs [data-nav=root]').click(); true`); await wait(1000);
  await shot('6-series-mobile');
  await size(1920, 1300); await wait(1000); await shot('7-series-1920');
  await evalJs(`document.querySelector('${P} #btnAbout').click(); true`); await wait(400); await size(1440, 900); await wait(800); await shot('8-about-1440');


  const C = '#CustomMetadataConfigPage';
  const PID = '12345678-1234-1234-1234-123456789abc';
  const cfgUrl = `${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`;
  const open = async () => { await send('Page.navigate', { url: 'about:blank' }); await wait(300); await send('Page.navigate', { url: cfgUrl });
    await until(`!!document.querySelector('${C} #sourcesList .cm-card')`, 30000); await wait(1000);
    await evalJs(`window.confirm = function(){ return true; }; true`); };
  const getCfg = () => evalJs(`ApiClient.getPluginConfiguration('${PID}')`);
  const setCfg = cfg => evalJs(`ApiClient.updatePluginConfiguration('${PID}', ${JSON.stringify(cfg)}).then(function(){return true})`);
  const set = (id, v) => evalJs(`(function(){var e=document.querySelector('${C} #${id}'); e.value=${JSON.stringify(v)}; e.dispatchEvent(new Event('input',{bubbles:true})); return true;})()`);
  const click = sel => evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return false; e.click(); return true;})()`);
  const ok = (name, cond, extra='') => console.log(cond ? 'PASS' : 'FAIL', name, extra);

  await open();
  const original = await getCfg();
  // field missing from the form, customised "by hand" (as in the XML)
  const seeded = JSON.parse(JSON.stringify(original));
  seeded.Sources[0].VideoFieldMappings.Cast = 'castX'; seeded.Sources[0].VideoFieldMappings.ProductionYear = 'yearX'; seeded.Sources[0].SeriesFieldMappings.BannerUrl = 'bannerX';
  await setCfg(seeded); await open();

  // 1. edit a source through the UI
  await click(`${C} .cm-card button.raised`); await wait(600);
  await set('srcName', 'Démo API modifiée'); await set('srcVideoImagesArray', 'media.images');
  await click(`${C} .cm-savebar button[type=submit]`); await wait(2500);
  let c = await getCfg();
  ok('name saved', c.Sources[0].Name === 'Démo API modifiée', c.Sources[0].Name);
  ok('ImagesArray saved', c.Sources[0].VideoFieldMappings.ImagesArray === 'media.images', c.Sources[0].VideoFieldMappings.ImagesArray);
  ok('Cast kept', c.Sources[0].VideoFieldMappings.Cast === 'castX', c.Sources[0].VideoFieldMappings.Cast);
  ok('ProductionYear kept', c.Sources[0].VideoFieldMappings.ProductionYear === 'yearX');
  ok('BannerUrl (series) kept', c.Sources[0].SeriesFieldMappings.BannerUrl === 'bannerX');
  ok('back on the list', await evalJs(`document.querySelector('${C} #editorView').hidden && !document.querySelector('${C} #listView').hidden`));

  // 2. the card's switch
  await click(`${C} [data-toggle="0"]`); await wait(2000);
  c = await getCfg(); ok('disabled from the card', c.Sources[0].Enabled === false);
  await click(`${C} [data-toggle="0"]`); await wait(2000);
  c = await getCfg(); ok('re-enabled from the card', c.Sources[0].Enabled === true);

  // 3. new source: default values pre-filled, required fields
  await click(`${C} .cm-add`); await wait(300);
  await click(`${C} [data-new-type="api"]`); await wait(500);   // the source type is chosen first
  ok('mappings pre-filled', await evalJs(`document.querySelector('${C} #srcVideoTitle').value`) === 'title');
  await click(`${C} .cm-savebar button[type=submit]`); await wait(600);
  ok('name missing: stay on the editor', await evalJs(`!document.querySelector('${C} #editorView').hidden`));
  await set('srcName', 'Nouvelle'); await set('srcPrefix', 'nv'); await set('srcBaseUrl', 'http://172.17.0.1:8765');
  await click(`${C} .cm-savebar button[type=submit]`); await wait(2500);
  c = await getCfg();
  ok('source added', c.Sources.length === 2 && c.Sources[1].Prefix === 'nv', String(c.Sources.length));
  ok('default mapping saved', c.Sources[1].VideoFieldMappings.Title === 'title' && c.Sources[1].VideoFieldMappings.ImagesArray === 'images');

  // 4. cancel without saving, then delete
  await click(`${C} .cm-card:nth-of-type(2) button.raised`); await wait(500);
  await set('srcName', 'Ne doit pas être gardé'); await click(`${C} #btnCancelEdit`); await wait(500);
  c = await getCfg(); ok('cancel: nothing saved', c.Sources[1].Name === 'Nouvelle', c.Sources[1].Name);
  await click(`${C} .cm-card:nth-of-type(2) button.raised`); await wait(500);
  await click(`${C} #btnDeleteSource`); await wait(2500);
  c = await getCfg(); ok('source deleted', c.Sources.length === 1, String(c.Sources.length));

  // 5. global settings: saved with their own button, without touching the sources
  await open(); await click(`${C} [data-tab=settings]`); await wait(300);
  await evalJs(`(function(){var e=document.querySelector('${C} #chkEnableCasting'); e.checked=false; e.dispatchEvent(new Event('change',{bubbles:true})); return true;})()`);
  await click(`${C} #paneSettings button[type=submit]`); await wait(2500);
  c = await getCfg(); ok('setting saved', c.EnableCasting === false && c.Sources.length === 1);

  await setCfg(original);  // put the original configuration back
  console.log('original configuration restored');
} finally {
  ws.close();
  await new Promise(r => { chrome.once('exit', r); chrome.kill(); setTimeout(r, 5000); });
  try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch {}
}
