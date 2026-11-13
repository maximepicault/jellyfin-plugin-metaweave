// Screenshots of the real jellyfin-web through headless Chrome + the DevTools protocol (Node >= 22 global WebSocket).
// Usage : node shoot.mjs <base_url> <out_dir> [user] [password]
// Scenario: Missing episodes screen → series with several sources (level 2) → the most incomplete series
// (a single source: jumps straight to the episodes),
// at 1440 px, 1920 px and mobile 412 px, + a test of the "Copier le nom" button with a real mouse click.
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
  if (await clickAt('[data-copy]')) console.log('📋 bouton :', await evalJs(`document.querySelector('${P} [data-copy]').textContent`), '| presse-papiers :', await clip());
  if (await clickAt('[data-copy-all]')) console.log('📋 tout copier :', await evalJs(`document.querySelector('${P} [data-copy-all]').textContent`), '| presse-papiers :', JSON.stringify(await clip()));
  await evalJs(`window.scrollTo(0,0); document.querySelector('.mainAnimatedPage, .page:not(.hide)') && window.scrollTo(0,0); true`); await wait(300);

  await size(1920, 1700); await wait(1000); await shot('4-detail-1920');
  await size(412, 1500, true); await wait(1000); await shot('5-detail-mobile');
  await evalJs(`document.querySelector('${P} .me-crumbs [data-nav=root]').click(); true`); await wait(1000);
  await shot('6-series-mobile');
  await size(1920, 1300); await wait(1000); await shot('7-series-1920');
  await evalJs(`document.querySelector('${P} #btnAbout').click(); true`); await wait(400); await size(1440, 900); await wait(800); await shot('8-about-1440');

  // ---- Configuration page: list of sources, editor (side navigation + test), settings, legacy, mobile
  const C = '#CustomMetadataConfigPage';
  const cfgUrl = `${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`;
  const openConfig = async () => {
    await send('Page.navigate', { url: 'about:blank' }); await wait(300);
    await send('Page.navigate', { url: cfgUrl });
    await until(`!!document.querySelector('${C} #sourcesList .cm-card')`, 30000); await wait(1200);
  };
  const click = sel => evalJs(`(function(){var e=document.querySelector(${JSON.stringify(sel)}); if(!e) return false; e.click(); return true;})()`);
  const editFirstSource = async () => {
    await evalJs(`document.querySelector('${C} .cm-card button.raised').click(); true`); await wait(900);
  };

  await size(1440, 900);
  await openConfig();
  console.log('🧩 config: editor view hidden =', await evalJs(`document.querySelector('${C} #editorView').hidden`),
    '| carte de source', await evalJs(`getComputedStyle(document.querySelector('${C} .cm-card')).borderTopStyle`),
    '| sources', await evalJs(`document.querySelectorAll('${C} .cm-card').length`));
  await shot('9-config-sources-1440');

  await editFirstSource();
  await shot('10-editor-general-1440');
  console.log('🧭 compteurs :', await evalJs(`[...document.querySelectorAll('${C} .cm-count')].map(function(c){return c.parentNode.firstChild.textContent+'='+c.textContent}).join(' | ')`));

  // Video mappings + test (episode list of the dm-d1 source)
  await click(`${C} [data-go=video]`); await wait(400);
  await evalJs(`(function(){var t=document.querySelector('${C} #testItemType'); t.value='episodelist'; t.dispatchEvent(new Event('change',{bubbles:true}));
    var i=document.querySelector('${C} #testItemId'); i.value='d1'; i.dispatchEvent(new Event('input',{bubbles:true})); return true;})()`);
  await click(`${C} #btnTestSource`);
  await until(`!!document.querySelector('${C} #testResult .cm-test-status')`, 20000); await wait(600);
  await size(1440, 1300); await wait(500);
  await shot('11-editor-video-test-1440');
  console.log('🧪 test :', (await evalJs(`document.querySelector('${C} #testResult .cm-test-status').textContent`) || '').slice(0, 120));

  // Empty required field while in another section: we must be taken back to « Général »
  await evalJs(`(function(){var n=document.querySelector('${C} #srcName'); n.value=''; n.dispatchEvent(new Event('input',{bubbles:true})); return true;})()`);
  await click(`${C} [data-go=series]`); await wait(300);
  await click(`${C} .cm-savebar button[type=submit]`); await wait(700);
  console.log('✅ validation: section shown =', await evalJs(`document.querySelector('${C} .cm-nav-item[aria-current]').getAttribute('data-go')`),
    '(expected: general) | state:', await evalJs(`document.querySelector('${C} #dirtyState').textContent`));
  await shot('12-editor-validation-1440');

  // Settings and legacy configuration
  await size(1440, 1000); await openConfig();
  await click(`${C} [data-tab=settings]`); await wait(500); await shot('13-config-settings-1440');
  await click(`${C} [data-tab=legacy]`); await wait(500); await shot('14-config-legacy-1440');

  // 1920: editor in 3 columns
  await size(1920, 1100); await openConfig(); await editFirstSource(); await click(`${C} [data-go=episodes]`); await wait(500);
  await shot('15-editor-episodes-1920');

  // Mobile
  await size(412, 1500, true); await openConfig(); await shot('16-config-sources-mobile');
  await editFirstSource(); await click(`${C} [data-go=network]`); await wait(500); await shot('17-editor-network-mobile');
} finally {
  ws.close();
  // wait for Chrome to end before deleting its profile (it still writes to it when quitting)
  await new Promise(r => { chrome.once('exit', r); chrome.kill(); setTimeout(r, 5000); });
  try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch {}
}
