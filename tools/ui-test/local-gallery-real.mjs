// Gallery of a local node in the REAL Jellyfin (REST only): the images show up in the image picker with a readable address,
// the address answers without a token, Jellyfin downloads it through the provider, and "Appliquer les images" (targeted refresh) works.
// Usage: node local-gallery-real.mjs <base_url> [user] [password]. Seeds a local source over the series "dm-aventuriers"; restores everything.
const [J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J) { console.error('usage: node local-gallery-real.mjs <base_url> [user] [password]'); process.exit(2); }
import http from 'node:http';
import zlib from 'node:zlib';
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'galtest00000001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
// A tiny valid PNG of one colour (backdrops are de-duplicated by content: the carousel needs different pictures)
const png = (r, g, b) => { const chunk = (t, d) => { const len = Buffer.alloc(4); len.writeUInt32BE(d.length); const body = Buffer.concat([Buffer.from(t), d]); const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body)); return Buffer.concat([len, body, crc]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(8, 0); ihdr.writeUInt32BE(8, 4); ihdr[8] = 8; ihdr[9] = 2;
  const raw = Buffer.concat(Array.from({ length: 8 }, () => Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: 8 }, () => [r, g, b]).flat())])));
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]); };
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-gallery", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return null; } })() : null };
};
// The picker's Url is built from the Host the browser used. The test container maps 8097 → 8096: ask with the in-container host so the server can reach its own address
const insideHost = new URL(J).hostname + ':8096';
const listWithHost = (url) => new Promise((resolve, reject) => {
  const u = new URL(J + url);
  http.get({ hostname: u.hostname, port: u.port, path: u.pathname + u.search, headers: { Host: insideHost, Authorization: H.Authorization } }, res => {
    let body = ''; res.on('data', d => body += d); res.on('end', () => resolve(JSON.parse(body)));
  }).on('error', reject);
});
const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
try {
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.LocalDataId !== DATA);
  config.Sources.push({ Name: 'Galerie test', Prefix: 'gl', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos',
    SeriesFieldMappings: { Title: 'title', Overview: 'overview', PosterUrl: 'poster', BackdropUrl: 'backdrop', LogoUrl: 'logo', BannerUrl: 'banner', ThumbUrl: 'thumb', ImagesArray: '' } });
  const up = async node => { const r = await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=${node}`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: PNG }); return (await r.json()).Url ?? (await r.json?.())?.url; };
  const rawUp = await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: PNG });
  const uploaded = pick(await rawUp.json(), 'Url');
  check('an image is uploaded', /^local:\/\/img\//.test(uploaded || ''), String(uploaded));
  const rawUp2 = await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: png(200, 30, 30) });
  const uploaded2 = pick(await rawUp2.json(), 'Url');
  const rawUp3 = await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: png(30, 30, 200) });
  const uploaded3 = pick(await rawUp3.json(), 'Url');
  const content = { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Série galerie', Seasons: [],
    Gallery: [{ Type: 'Primary', Url: uploaded }, { Type: 'Backdrop', Url: uploaded2 }, { Type: 'Backdrop', Url: uploaded3 }, { Type: 'Primary', Url: 'http://localhost:8765/series/poster.jpg' }] }] };
  await api('PUT', `/CustomMetadata/Local/${DATA}`, content);
  const saved = (await api('GET', `/CustomMetadata/Local/${DATA}`)).json;
  const gal = pick((pick(saved, 'Series') || [])[0], 'Gallery') || [];
  check('the gallery is stored (4 images, types kept)', gal.length === 4 && gal.map(g => pick(g, 'Type')).join() === 'Primary,Backdrop,Backdrop,Primary', JSON.stringify(gal));
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  const series = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|galerie/i.test(i.Name));
  check('series found', !!series);
  const providers = (await api('GET', `/Items/${series.Id}/RemoteImages/Providers`)).json || [];
  const mine = providers.find(p => /Custom/i.test(p.Name))?.Name;
  const listing = await listWithHost(`/Items/${series.Id}/RemoteImages?type=Primary&providerName=${encodeURIComponent(mine)}&includeAllLanguages=true&limit=30`);
  const imgs = pick(listing, 'Images') || [];
  const local = imgs.filter(i => /\/CustomMetadata\/Local\//.test(pick(i, 'Url') || ''));
  check('the picker lists the uploaded image with a readable address', local.length >= 1, JSON.stringify(imgs.map(i => pick(i, 'Url'))));
  if (local.length) {
    const url = pick(local[0], 'Url'); const thumb = new URL(url).pathname;
    const open = await fetch(J + thumb);
    check('thumbnail address answers without a token (browser preview)', open.status === 200 && /image\/png/.test(open.headers.get('content-type') || ''), String(open.status));
    check('Url is absolute and carries the host of the request (preview + download)', url.startsWith('http://' + insideHost + '/CustomMetadata/Local/'), url);
    const dl = await api('POST', `/Items/${series.Id}/RemoteImages/Download?type=Primary&imageUrl=${encodeURIComponent(url)}&providerName=${encodeURIComponent(mine)}`);
    check('Jellyfin downloads it through the provider', dl.status === 204 || dl.status === 200, String(dl.status));
  }
  const backs = (await api('GET', `/Items/${series.Id}/RemoteImages?type=Backdrop&providerName=${encodeURIComponent(mine)}&includeAllLanguages=true&limit=30`)).json;
  check('backdrops are offered too', (pick(backs, 'Images') || []).some(i => /\/CustomMetadata\/Local\//.test(pick(i, 'Url') || '')));

  // "Appliquer les images": targeted refresh by tag
  const st = (await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: [], ReplaceImages: true, Tag: 'dm-aventuriers', IncludeChildren: false })).json;
  check('apply images: the series is found by its tag', (pick(st, 'Total') || 0) >= 1, JSON.stringify(st));
  for (let i = 0; i < 60; i++) { await wait(1000); if (!pick((await api('GET', '/CustomMetadata/Refresh/Status')).json, 'Running')) break; }
  const after = (await api('GET', `/Items/${series.Id}?userId=${auth.User.Id}`)).json;
  check('apply images: the series has a Primary image', !!(after.ImageTags || {}).Primary, JSON.stringify(after.ImageTags));
  check('apply images: all the backdrops are applied (carousel)', (after.BackdropImageTags || []).length === 2, JSON.stringify(after.BackdropImageTags));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  // "Appliquer les images" refreshed the series with the local title: refresh it once more so it follows the API again
  const back = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|galerie/i.test(i.Name));
  if (back) { await api('POST', `/Items/${back.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=true`); await wait(3000); }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
