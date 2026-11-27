// Uploaded images rotated only by their EXIF tag show the right way in Jellyfin (REST only). The test image is stored
// PORTRAIT (32x48 pixels) with an EXIF Orientation 6, so browsers show it landscape (48x32), like a picture turned in a
// viewer that only writes the tag. Jellyfin's resizer ignores the tag: without the plugin's fix every resized copy
// (thumbnails of "Modifier les images", the theme's header) came out portrait. Checks: the address Jellyfin downloads
// serves the pixels turned (48x32, no tag), "Appliquer les images" stores the backdrop and the poster turned, and a
// resized copy made by Jellyfin is landscape. The uploaded file itself stays untouched.
// Usage: node local-image-orientation-real.mjs <base_url> [user] [password]. Seeds a local source over "dm-aventuriers"; restores everything.
const [J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J) { console.error('usage: node local-image-orientation-real.mjs <base_url> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'orienttest00001';
const JPEG = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/4QBiRXhpZgAATU0AKgAAAAgABQESAAMAAAABAAYAAAEaAAUAAAABAAAASgEbAAUAAAABAAAAUgEoAAMAAAABAAEAAAITAAMAAAABAAEAAAAAAAAAAAABAAAAAQAAAAEAAAAB/9sAQwAKBwcIBwYKCAgICwoKCw4YEA4NDQ4dFRYRGCMfJSQiHyIhJis3LyYpNCkhIjBBMTQ5Oz4+PiUuRElDPEg3PT47/9sAQwEKCwsODQ4cEBAcOygiKDs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7/8AAEQgAMAAgAwEiAAIRAQMRAf/EABUAAQEAAAAAAAAAAAAAAAAAAAAF/8QAFhAAAwAAAAAAAAAAAAAAAAAAABRh/8QAFgEBAQEAAAAAAAAAAAAAAAAAAAYF/8QAFhEBAQEAAAAAAAAAAAAAAAAAABMU/9oADAMBAAIRAxEAPwC2zQzSSzQzSGyKaqszQzSSzQzRkKpLNDNJTNDNKzIzKqrNDNJTNDNGQqks0M0lM0M0q8jNqqs0M0lM0M0ZCr//2Q==', 'base64');
// The same picture as a PNG whose eXIf chunk carries Orientation 6 (Chrome honours it there too)
const PNG6 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAACAAAAAwEAIAAACvXjN7AAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAAGYktHRP///////wlY99wAAAAHdElNRQfqCxoSNhpYowu/AAAAJXRFWHRkYXRlOmNyZWF0ZQAyMDI2LTExLTI2VDE4OjU0OjI2KzAwOjAw1QByKwAAACV0RVh0ZGF0ZTptb2RpZnkAMjAyNi0xMS0yNlQxODo1NDoyNiswMDowMKRdypcAAAAodEVYdGRhdGU6dGltZXN0YW1wADIwMjYtMTEtMjZUMTg6NTQ6MjYrMDA6MDDzSOtIAAAAJmVYSWZNTQAqAAAACAACARIAAwAAAAEABgAAAhMAAwAAAAEAAQAAAAAAABQQ4ecAAAErSURBVGje3ZihTkNBEEV326F9acDQVNTQJqSygoRv4CvwWCyPpOkHsKvqqptUEDQJuOIJEkMQVKMQ7L6XIviDqZicd77gTrJn5+763W61cmAkx7psPVvH2GeAUJVt8AD+e708Ls6tY+iRHCv0EfJfm0Xn6N46hh7JsSrbT9Yx9Pj3j7vf/ql1DD38azRF9jUqOdToAfzLYPZ50reOoUcS3QF6lZAU2Q5IjtUNeoAU6A7Q90AKNboL4eu05FjTJYY7kOl7IOEdCHQH6HuA70CgO0Avc404QvAuBK/TTagSaIn9xev07fLHOoYe/MdWI6oE24Gzg9Hj1dY6hp5GfGyhB/Djh8HkemgdQ8//twq5CyX6HsA70Nt2D28L6xh6JMOflN455+Zz6xh6/gBV+6coAum2pAAAAABJRU5ErkJggg==', 'base64');
const pngInfo = d => d.readUInt32BE(16) + 'x' + d.readUInt32BE(20) + (d.includes(Buffer.from('eXIf')) ? ' eXIf' : '');
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
// EXIF Orientation of a JPEG (0 when none): APP1 "Exif", TIFF header, IFD0 entry 0x0112
// Pixel size of a JPEG (SOF marker)
const size = (d) => {
  for (let p = 2; p + 4 <= d.length && d[p] === 0xFF;) {
    const m = d[p + 1], len = d.readUInt16BE(p + 2);
    if (m >= 0xC0 && m <= 0xCF && m !== 0xC4 && m !== 0xC8 && m !== 0xCC) return d.readUInt16BE(p + 7) + 'x' + d.readUInt16BE(p + 5);
    p += 2 + len;
  }
  return '?';
};
const orientation = (d) => {
  for (let p = 2; p + 4 <= d.length && d[p] === 0xFF;) {
    const m = d[p + 1], len = d.readUInt16BE(p + 2);
    if (m === 0xDA) break;
    if (m === 0xE1 && d.toString('latin1', p + 4, p + 8) === 'Exif') {
      const t = p + 10, be = d[t] === 0x4D, u16 = a => be ? d.readUInt16BE(a) : d.readUInt16LE(a), u32 = a => be ? d.readUInt32BE(a) : d.readUInt32LE(a);
      const ifd = t + u32(t + 4);
      for (let i = 0, n = u16(ifd); i < n; i++) { const e = ifd + 2 + i * 12; if (u16(e) === 0x0112) return u16(e + 8); }
    }
    p += 2 + len;
  }
  return 0;
};

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-orient", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return null; } })() : null };
};
const bytes = async url => Buffer.from(await (await fetch(url.startsWith('http') ? url : J + url)).arrayBuffer());
const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
try {
  check('the test image is portrait pixels with Orientation 6', orientation(JPEG) === 6 && size(JPEG) === '32x48', orientation(JPEG) + ' ' + size(JPEG));
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.LocalDataId !== DATA);
  config.Sources.push({ Name: 'Orientation test', Prefix: 'or', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos',
    SeriesFieldMappings: { Title: 'title', Overview: 'overview', PosterUrl: 'poster', BackdropUrl: 'backdrop', LogoUrl: 'logo', BannerUrl: 'banner', ThumbUrl: 'thumb', ImagesArray: '' } });
  const upload = async () => pick(await (await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/jpeg', Authorization: H.Authorization }, body: JPEG })).json(), 'Url');
  const poster = await upload(), backdrop = await upload();
  check('two images are uploaded', /^local:\/\/img\//.test(poster || '') && /^local:\/\/img\//.test(backdrop || ''), poster + ' ' + backdrop);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Série orientation', Seasons: [],
    Gallery: [{ Type: 'Primary', Url: poster }, { Type: 'Backdrop', Url: backdrop }] }] });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  const file = backdrop.split('/').pop();
  const editorCopy = await bytes(`/CustomMetadata/Local/${DATA}/Image/${file}?ApiKey=${auth.AccessToken}`);
  check('the uploaded file is untouched (editor preview: Orientation 6)', orientation(editorCopy) === 6, String(orientation(editorCopy)));
  const served = await bytes(`/CustomMetadata/Local/${DATA}/Public/${file}`);
  check('the address Jellyfin downloads serves the pixels turned (48x32, no tag)', size(served) === '48x32' && orientation(served) === 0, size(served) + ' ' + orientation(served));

  const pngUp = pick(await (await fetch(`${J}/CustomMetadata/Local/${DATA}/Image?node=dm-aventuriers`, { method: 'POST', headers: { 'Content-Type': 'image/png', Authorization: H.Authorization }, body: PNG6 })).json(), 'Url');
  const pngServed = await bytes(`/CustomMetadata/Local/${DATA}/Public/${(pngUp || '').split('/').pop()}`);
  check('a PNG with an eXIf rotation is served turned, still a PNG (48x32, no eXIf)', pngServed[1] === 0x50 && pngInfo(pngServed) === '48x32', pngInfo(pngServed));

  const series = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|orientation/i.test(i.Name));
  check('series found', !!series);

  // "Appliquer les images": targeted refresh by tag, images replaced
  await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: [], ReplaceImages: true, Tag: 'dm-aventuriers', IncludeChildren: false });
  for (let i = 0; i < 60; i++) { await wait(1000); if (!pick((await api('GET', '/CustomMetadata/Refresh/Status')).json, 'Running')) break; }
  const after = (await api('GET', `/Items/${series.Id}?userId=${auth.User.Id}`)).json;
  check('apply images: one backdrop and a poster', (after.BackdropImageTags || []).length === 1 && !!(after.ImageTags || {}).Primary, JSON.stringify([after.BackdropImageTags, after.ImageTags]));
  const token = `ApiKey=${auth.AccessToken}`;
  const back = await bytes(`/Items/${series.Id}/Images/Backdrop/0?${token}`), post = await bytes(`/Items/${series.Id}/Images/Primary?${token}`);
  check('apply images: the stored backdrop is turned (48x32, no tag)', size(back) === '48x32' && orientation(back) === 0, size(back) + ' ' + orientation(back));
  check('apply images: the stored poster is turned too', size(post) === '48x32' && orientation(post) === 0, size(post) + ' ' + orientation(post));
  const resized = await bytes(`/Items/${series.Id}/Images/Backdrop/0?maxWidth=40&format=Jpg&${token}`);
  const [rw, rh] = size(resized).split('x').map(Number);
  check('a copy resized by Jellyfin is landscape (what "Modifier les images" and the theme show)', rw > rh, size(resized));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  // the series was refreshed with the local title and images: refresh it so it follows the API again
  const back = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|orientation/i.test(i.Name));
  if (back) { await api('POST', `/Items/${back.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true`); await wait(4000); }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
