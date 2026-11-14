// Images kept in sub-folders of a series folder (Primary, Backdrop…, also Principal, Fond…) in the REAL Jellyfin (REST only):
// offered in the image picker, readable without a token, nothing else reachable, downloaded by Jellyfin, imported into a gallery.
// Usage: node local-folder-images-real.mjs <base_url> <series_folder> <images_dir> [user] [password]
// series_folder = the folder of the series "dm-aventuriers" on THIS machine (it is mounted in the test container).
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
const [J, seriesDir, imgDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !seriesDir || !imgDir) { console.error('usage: node local-folder-images-real.mjs <base_url> <series_folder> <images_dir>'); process.exit(2); }
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-folderimg", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => { const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); return { status: r.status, json: t && t[0] !== '<' ? (() => { try { return JSON.parse(t); } catch { return null; } })() : null }; };
const insideHost = new URL(J).hostname + ':8096';
const listWithHost = url => new Promise((resolve, reject) => { const u = new URL(J + url);
  http.get({ hostname: u.hostname, port: u.port, path: u.pathname + u.search, headers: { Host: insideHost, Authorization: H.Authorization } }, res => { let b = ''; res.on('data', d => b += d); res.on('end', () => resolve(JSON.parse(b))); }).on('error', reject); });
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(8000); for (let i = 0; i < 60; i++) { const t = await api('GET', '/ScheduledTasks?isHidden=false'); if (!(t.json || []).some(x => x.State === 'Running')) break; await wait(1000); } };
const dirs = ['Principal', 'Backdrop', 'Logo'].map(d => path.join(seriesDir, d));
try {
  const countSeasons = async id => ((await api('GET', `/Shows/${id}/Seasons?userId=${auth.User.Id}`)).json?.Items || []).length;
  const series = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers/i.test(i.Name));
  check('series found', !!series);
  const before = await countSeasons(series.Id);
  fs.mkdirSync(dirs[0], { recursive: true }); fs.mkdirSync(dirs[1], { recursive: true }); fs.mkdirSync(dirs[2], { recursive: true });
  fs.copyFileSync(path.join(imgDir, 'poster1.jpg'), path.join(dirs[0], 'affiche-rouge.jpg'));
  fs.copyFileSync(path.join(imgDir, 'poster2.jpg'), path.join(dirs[0], 'affiche-bleue.jpg'));
  fs.copyFileSync(path.join(imgDir, 'back1.jpg'), path.join(dirs[1], 'fond-violet.jpg'));
  fs.copyFileSync(path.join(imgDir, 'back2.jpg'), path.join(dirs[1], 'fond-orange.jpg'));
  fs.writeFileSync(path.join(dirs[1], 'notes.txt'), 'not an image');
  await scan();
  check('image folders do not become seasons or items of the library', (await countSeasons(series.Id)) === before, `${await countSeasons(series.Id)} vs ${before}`);

  const providers = (await api('GET', `/Items/${series.Id}/RemoteImages/Providers`)).json || [];
  const mine = providers.find(p => /Custom/i.test(p.Name))?.Name;
  const list = async type => pick(await listWithHost(`/Items/${series.Id}/RemoteImages?type=${type}&providerName=${encodeURIComponent(mine)}&includeAllLanguages=true&limit=50`), 'Images') || [];
  const prim = (await list('Primary')).filter(i => /FolderImages/.test(pick(i, 'Url')));
  const back = (await list('Backdrop')).filter(i => /FolderImages/.test(pick(i, 'Url')));
  check('"Principal" images are offered as Primary (2 files)', prim.length === 2, JSON.stringify(prim.map(i => pick(i, 'Url'))));
  check('"Backdrop" images are offered as Backdrop (the .txt is ignored)', back.length === 2, JSON.stringify(back.map(i => pick(i, 'Url'))));
  const url = pick(prim[0], 'Url');
  check('the address carries the host of the request', url.startsWith(`http://${insideHost}/CustomMetadata/FolderImages/`), url);
  const open = await fetch(J + new URL(url).pathname);
  check('an image answers without a token (browser preview)', open.status === 200 && /image\/jpeg/.test(open.headers.get('content-type') || ''), String(open.status));
  const evil = ['..%2f..%2fetc%2fpasswd', '..', 'notes.txt'];
  for (const f of evil) { const r = await fetch(`${J}/CustomMetadata/FolderImages/${series.Id}/Principal/${f}`); check(`nothing else is reachable (${f})`, r.status === 404, String(r.status)); }
  const rf = await fetch(`${J}/CustomMetadata/FolderImages/${series.Id}/etc/passwd`); check('a non-image folder is refused', rf.status === 404, String(rf.status));
  const dl = await api('POST', `/Items/${series.Id}/RemoteImages/Download?type=Primary&imageUrl=${encodeURIComponent(url)}&providerName=${encodeURIComponent(mine)}`);
  check('Jellyfin downloads it', dl.status === 204 || dl.status === 200, String(dl.status));
  // import into the gallery of a local node
  const imp = (await api('POST', '/CustomMetadata/FolderImages/Import', { Tag: 'dm-aventuriers', DataId: 'fldtest00000001', NodeId: 'dm-aventuriers' })).json;
  const imgs = pick(imp, 'Images') || [];
  check('import: the series folder is found and its 4 images are copied', pick(imp, 'Items') === 1 && imgs.length === 4 && imgs.every(i => /^local:\/\/img\//.test(pick(i, 'Url'))), JSON.stringify(imp));
  check('import: each carries its type and name', imgs.some(i => pick(i, 'Type') === 'Backdrop' && pick(i, 'Name') === 'Backdrop/fond-orange.jpg'), JSON.stringify(imgs.map(i => [pick(i, 'Type'), pick(i, 'Name')])));
  const none = (await api('POST', '/CustomMetadata/FolderImages/Import', { Tag: 'dm-nexistepas', DataId: 'fldtest00000001', NodeId: 'x' })).json;
  check('import: an unknown tag finds nothing', pick(none, 'Items') === 0);
} finally {
  for (const d of dirs) fs.rmSync(d, { recursive: true, force: true });
  await api('DELETE', '/CustomMetadata/Local/fldtest00000001');
  await scan();
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
