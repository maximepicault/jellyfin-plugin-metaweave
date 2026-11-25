// Screenshots of jellyfin-web with the Projection theme (theme/projection.css) applied.
// Usage: node theme-shots.mjs <base_url> <out_dir> [--layout=experimental|desktop|auto] [--only=a,b] [--width=1440,1920,412]
//   The CSS is pasted into the server's branding (Dashboard > General > Custom CSS) at the start and restored at the end;
// Pages: home, library, series, season, missing (the season with the most missing episodes), episode, movie, search, login.
// Phones (width < 600) get an Android user agent, so jellyfin-web picks its mobile layout as on a real phone.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { trackChrome, chromeTmp } from './chrome-session.mjs';

const args = process.argv.slice(2);
const pos = args.filter(a => !a.startsWith('--'));
const opt = name => (args.find(a => a.startsWith(`--${name}=`)) || '').split('=')[1];
const [J, outDir] = pos;
if (!J || !outDir) { console.error('usage: node theme-shots.mjs <base_url> <out_dir> [--layout=experimental] [--only=series,season] [--width=1440,412]'); process.exit(2); }
const layout = opt('layout') || 'experimental';
const only = (opt('only') || '').split(',').filter(Boolean);
const widths = (opt('width') || '1440,1920,412').split(',').map(Number);
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const css = fs.readFileSync(path.join(repo, 'theme/projection.css'), 'utf8');

fs.mkdirSync(outDir, { recursive: true });
const port = 9334;
const profile = fs.mkdtempSync(path.join(outDir, '.profile-'));
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
const consoleErrors = [];
ws.onmessage = m => {
  const d = JSON.parse(m.data);
  if (d.id && pending[d.id]) { pending[d.id](d); delete pending[d.id]; }
  if (d.method === 'Runtime.exceptionThrown') { consoleErrors.push(d.params.exceptionDetails?.exception?.description || d.params.exceptionDetails?.text); }
  if (d.method === 'Runtime.consoleAPICalled' && /projection/i.test(JSON.stringify(d.params.args))) { console.log('🖥️ ', d.params.args.map(a => a.value ?? a.description).join(' ')); }
};
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) { console.log('⚠️ eval:', r.result.exceptionDetails.exception?.description); } return r.result?.result?.value; };
const size = (w, h, mobile = false) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
const until = async (expr, ms = 20000) => { const t = Date.now(); while (Date.now() - t < ms) { if (await evalJs(expr)) return true; await wait(300); } console.log('⚠️ timeout:', expr); return false; };
const shot = async (name, full = true) => {
  let clip;
  if (full) {
    const h = await evalJs(`Math.min(6000, Math.max(document.documentElement.scrollHeight, document.body.scrollHeight, innerHeight))`);
    const w = await evalJs('innerWidth');
    clip = { x: 0, y: 0, width: w, height: h, scale: 1 };
  }
  const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: !!full, ...(clip ? { clip } : {}) });
  const f = path.join(outDir, `${name}.png`); fs.writeFileSync(f, Buffer.from(r.result.data, 'base64')); console.log('📸', f);
};

// token for the API calls made from Node (branding)
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Emby-Authorization': 'MediaBrowser Client="theme-shots", Device="cli", DeviceId="theme-shots", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const H = { 'Content-Type': 'application/json', 'X-Emby-Token': auth.AccessToken };
const brandingBefore = await (await fetch(`${J}/System/Configuration/branding`, { headers: H })).json();
await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify({ ...brandingBefore, CustomCss: css }) });

const want = name => !only.length || only.includes(name);
try {
  await send('Page.enable'); await send('Runtime.enable');
  await size(1440, 900);

  await send('Page.navigate', { url: `${J}/web/#/login` });
  await until(`!!document.querySelector('#txtManualName')`);
  await wait(800);
  if (want('login')) { await shot('login-1440', false); }
  await evalJs(`(function(){var n=document.querySelector('#txtManualName'); n.value='admin'; n.dispatchEvent(new Event('input',{bubbles:true}));
    var p=document.querySelector('#txtManualPassword'); p.value='admin'; p.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('.manualLoginForm button[type=submit]').click(); return true;})()`);
  await until(`!location.hash.includes('login')`); await wait(1500);
  // the experimental layout is a per-browser setting
  await evalJs(`localStorage.setItem('layout', ${JSON.stringify(layout)}); true`);
  await send('Page.reload'); await wait(2500);

  const q = async (query) => evalJs(`ApiClient.getItems(ApiClient.getCurrentUserId(), ${JSON.stringify(query)}).then(r => r.Items.map(i => ({Id: i.Id, Name: i.Name, Type: i.Type, ChildCount: i.ChildCount})))`);
  const series = await q({ IncludeItemTypes: 'Series', Recursive: true, Fields: 'ChildCount', SortBy: 'SortName' });
  console.log('📺 series:', series.map(s => `${s.Name} (${s.ChildCount})`).join(' | '));
  const mainSeries = series.slice().sort((a, b) => (b.ChildCount || 0) - (a.ChildCount || 0))[0];
  const seasons = mainSeries ? await q({ ParentId: mainSeries.Id, IncludeItemTypes: 'Season' }) : [];
  const episodes = await q({ IncludeItemTypes: 'Episode', Recursive: true, Limit: 50, HasOverview: true });
  const season = seasons[0];
  const seasonEps = season ? await q({ ParentId: season.Id, IncludeItemTypes: 'Episode' }) : [];
  const episode = seasonEps.find(e => e) || episodes[0];
  // missing episodes are shown only when the user asked for them (the maintainer does)
  await evalJs(`(async function(){var u=ApiClient.getCurrentUserId(); var user=await ApiClient.getUser(u); if(!user.Configuration.DisplayMissingEpisodes){user.Configuration.DisplayMissingEpisodes=true; await ApiClient.updateUserConfiguration(u,user.Configuration);} return true;})()`);
  const missing = await evalJs(`ApiClient.getItems(ApiClient.getCurrentUserId(), {IncludeItemTypes:'Episode', Recursive:true, IsMissing:true, Fields:'SeasonId'}).then(r => { var n={}; r.Items.forEach(i => n[i.SeasonId]=(n[i.SeasonId]||0)+1); return Object.keys(n).sort((a,b)=>n[b]-n[a])[0]; })`);
  const movies = await q({ IncludeItemTypes: 'Movie', Recursive: true, Limit: 1 });
  const libs = await evalJs(`ApiClient.getUserViews({}, ApiClient.getCurrentUserId()).then(r => r.Items.map(i => ({Id: i.Id, Name: i.Name, CollectionType: i.CollectionType})))`);
  console.log('🧭', { series: mainSeries?.Name, seasons: seasons.length, season: season?.Name, episode: episode?.Name, movie: movies[0]?.Name, libs: libs.map(l => l.Name) });

  const pages = [];
  if (want('home')) pages.push(['home', '#/home']);
  if (want('library') && libs[0]) pages.push(['library', libs[0].CollectionType === 'tvshows' ? `#/tv?topParentId=${libs[0].Id}` : `#/list?parentId=${libs[0].Id}`]);
  if (want('series') && mainSeries) pages.push(['series', `#/details?id=${mainSeries.Id}`]);
  if (want('season') && season) pages.push(['season', `#/details?id=${season.Id}`]);
  if (want('missing') && missing) pages.push(['missing', `#/details?id=${missing}`]);
  if (want('episode') && episode) pages.push(['episode', `#/details?id=${episode.Id}`]);
  if (want('movie') && movies[0]) pages.push(['movie', `#/details?id=${movies[0].Id}`]);
  if (want('search')) pages.push(['search', '#/search?query=a']);

  for (const w of widths) {
    const mobile = w < 600;
    await send('Emulation.setUserAgentOverride', { userAgent: mobile ? UA_MOBILE : UA_DESKTOP });
    await size(w, mobile ? 860 : (w > 1500 ? 1080 : 900), mobile);
    for (const [name, hash] of pages) {
      await send('Page.navigate', { url: 'about:blank' }); await wait(200);
      await send('Page.navigate', { url: `${J}/web/${hash}` });
      await until(`!!document.querySelector('.page:not(.hide) .cardImageContainer, .page:not(.hide) .listItem, .page:not(.hide) .itemName, .page:not(.hide) .searchFields')`, 20000);
      await wait(2500);
      await shot(`${name}-${w}`, true);
      if (['series', 'season', 'missing', 'episode', 'movie'].includes(name)) {
        // the first screen as a user sees it, then half a screen down (the backdrop fade)
        await shot(`${name}-${w}-fold`, false);
        await evalJs(`window.scrollTo(0, innerHeight * .6); true`); await wait(600);
        await shot(`${name}-${w}-scrolled`, false);
      }
    }
  }
  if (consoleErrors.length) { console.log('❌ page errors:', [...new Set(consoleErrors)].slice(0, 10)); }
} finally {
  await fetch(`${J}/System/Configuration/branding`, { method: 'POST', headers: H, body: JSON.stringify(brandingBefore) }).catch(() => {});
  ws.close();
  await new Promise(r => { chrome.once('exit', r); chrome.kill(); setTimeout(r, 5000); });
  try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch {}
}
