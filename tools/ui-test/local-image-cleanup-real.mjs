// Images removed from a local source leave Jellyfin at the next "Remplacer les images" (REST only), and only those: an
// image the plugin never applied (one added by hand in Jellyfin) stays. Seeds a local source over "dm-aventuriers" with two backdrops, applies, removes
// one backdrop from the source, applies again. Usage: node local-image-cleanup-real.mjs <base_url> [user] [password]. Restores everything.
import zlib from 'node:zlib';
const [J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J) { console.error('usage: node local-image-cleanup-real.mjs <base_url> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'cleanuptest0001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
// A small PNG of one colour (backdrops are de-duplicated by content: each one needs its own picture)
const png = (r, g, b, w = 16) => { const chunk = (t, d) => { const len = Buffer.alloc(4); len.writeUInt32BE(d.length); const body = Buffer.concat([Buffer.from(t), d]); const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body)); return Buffer.concat([len, body, crc]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(9, 4); ihdr[8] = 8; ihdr[9] = 2;
  const raw = Buffer.concat(Array.from({ length: 9 }, () => Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: w }, () => [r, g, b]).flat())])));
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]); };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-cleanup", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return null; } })() : null };
};
const upload = async data => pick(await (await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: data })).json(), 'Url');
const content = backdrops => ({ Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Série nettoyage', Seasons: [], Gallery: backdrops.map(u => ({ Type: 'Backdrop', Url: u })) }] });
const applyImages = async () => {
  await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: [], ReplaceImages: true, Tag: 'dm-aventuriers', IncludeChildren: false });
  for (let i = 0; i < 60; i++) { await wait(1000); if (!pick((await api('GET', '/CustomMetadata/Refresh/Status')).json, 'Running')) break; }
};
const series = async () => ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}&Fields=BackdropImageTags`)).json?.Items || []).find(i => /aventuriers|nettoyage/i.test(i.Name));
const backdropSizes = async id => ((await api('GET', `/Items/${id}/Images`)).json || []).filter(i => i.ImageType === 'Backdrop').map(i => i.Size);
const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
try {
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.LocalDataId !== DATA);
  config.Sources.push({ Name: 'Nettoyage test', Prefix: 'cl', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos',
    SeriesFieldMappings: { Title: 'title', Overview: 'overview', PosterUrl: 'poster', BackdropUrl: 'backdrop', LogoUrl: 'logo', BannerUrl: 'banner', ThumbUrl: 'thumb', ImagesArray: '' } });
  const red = png(200, 30, 30), blue = png(30, 30, 200);
  const a = await upload(red), b = await upload(blue);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, content([a, b]));
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  await applyImages();
  const s1 = await series();
  check('first apply: the two backdrops of the source are in Jellyfin', (await backdropSizes(s1.Id)).sort().join() === [red.length, blue.length].sort().join(), JSON.stringify(await backdropSizes(s1.Id)));

  // the blue backdrop is removed from the source
  await api('PUT', `/CustomMetadata/Local/${DATA}`, content([a]));
  await applyImages();
  const after = await backdropSizes(s1.Id);
  check('after removing one from the source: only the kept backdrop is left', after.join() === String(red.length), JSON.stringify(after));

  // a backdrop added by hand in Jellyfin: the plugin never applied it, it must stay
  const green = png(30, 160, 30, 40);   // another size: backdrops are told apart by their byte size
  await fetch(`${J}/Items/${s1.Id}/Images/Backdrop`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: green.toString('base64') });
  check('a backdrop is added by hand', (await backdropSizes(s1.Id)).includes(green.length), JSON.stringify(await backdropSizes(s1.Id)));

  // the source gives no backdrop at all any more
  await api('PUT', `/CustomMetadata/Local/${DATA}`, content([]));
  await applyImages();
  const none = await backdropSizes(s1.Id);
  check('after removing them all: no backdrop of the source is left', !none.includes(red.length) && !none.includes(blue.length), JSON.stringify(none));
  check('the backdrop added by hand is still there', none.includes(green.length), JSON.stringify(none));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  // the series was refreshed with the local title and images: refresh it so it follows the API again
  const back = await series();
  if (back) { await api('POST', `/Items/${back.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true`); await wait(4000); }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
