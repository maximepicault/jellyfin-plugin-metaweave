// Uploaded backdrops keep their landscape shape in the REAL Jellyfin (REST only). The test image is a landscape JPEG (48x32
// pixels) with an EXIF rotation of 90° (Orientation 6): browsers show it portrait, so the Projection theme would drop it.
// Checks: the provider gives backdrops the "?landscape=1" address (not posters), that address serves the tag reset to 1,
// "Appliquer les images" stores the backdrop with Orientation 1, and the poster keeps its tag (only backdrops change).
// Usage: node local-backdrop-orientation-real.mjs <base_url> [user] [password]. Seeds a local source over "dm-aventuriers"; restores everything.
const [J, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J) { console.error('usage: node local-backdrop-orientation-real.mjs <base_url> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'orienttest00001';
const JPEG = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/4QBiRXhpZgAATU0AKgAAAAgABQESAAMAAAABAAYAAAEaAAUAAAABAAAASgEbAAUAAAABAAAAUgEoAAMAAAABAAEAAAITAAMAAAABAAEAAAAAAAAAAAABAAAAAQAAAAEAAAAB/9sAQwAKBwcIBwYKCAgICwoKCw4YEA4NDQ4dFRYRGCMfJSQiHyIhJis3LyYpNCkhIjBBMTQ5Oz4+PiUuRElDPEg3PT47/9sAQwEKCwsODQ4cEBAcOygiKDs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7/8AAEQgAIAAwAwEiAAIRAQMRAf/EABYAAQEBAAAAAAAAAAAAAAAAAAAGBf/EABYQAAMAAAAAAAAAAAAAAAAAAAAUYf/EABcBAQEBAQAAAAAAAAAAAAAAAAAGBQf/xAAXEQEAAwAAAAAAAAAAAAAAAAAAExVh/9oADAMBAAIRAxEAPwDWdodpPO0O0kK7FDOoXaHaTztDtFdhOoXaHaTztDtFdhOnnaHaTztDtOh12MOdQu0O0nnaHaK7CdQu0O0nnaHaK7Cd/9k=', 'base64');
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };
// EXIF Orientation of a JPEG (0 when none): APP1 "Exif", TIFF header, IFD0 entry 0x0112
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
  check('the test image is landscape pixels with Orientation 6', orientation(JPEG) === 6, String(orientation(JPEG)));
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
  check('the stored file is untouched (Orientation 6)', orientation(await bytes(`/CustomMetadata/Local/${DATA}/Public/${file}`)) === 6);
  check('the "?landscape=1" address serves Orientation 1', orientation(await bytes(`/CustomMetadata/Local/${DATA}/Public/${file}?landscape=1`)) === 1);

  const series = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|orientation/i.test(i.Name));
  check('series found', !!series);
  const mine = ((await api('GET', `/Items/${series.Id}/RemoteImages/Providers`)).json || []).find(p => /Custom/i.test(p.Name))?.Name;
  const listed = async type => (pick((await api('GET', `/Items/${series.Id}/RemoteImages?type=${type}&providerName=${encodeURIComponent(mine)}&includeAllLanguages=true&limit=30`)).json, 'Images') || [])
    .map(i => pick(i, 'Url') || '').filter(u => /\/CustomMetadata\/Local\//.test(u));
  const backs = await listed('Backdrop'), posters = await listed('Primary');
  check('the picker offers the backdrop with "?landscape=1"', backs.length === 1 && backs[0].endsWith('?landscape=1'), JSON.stringify(backs));
  check('the picker offers the poster without it', posters.length === 1 && !posters[0].includes('landscape'), JSON.stringify(posters));

  // "Appliquer les images": targeted refresh by tag, images replaced
  await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: [], ReplaceImages: true, Tag: 'dm-aventuriers', IncludeChildren: false });
  for (let i = 0; i < 60; i++) { await wait(1000); if (!pick((await api('GET', '/CustomMetadata/Refresh/Status')).json, 'Running')) break; }
  const after = (await api('GET', `/Items/${series.Id}?userId=${auth.User.Id}`)).json;
  check('apply images: one backdrop and a poster', (after.BackdropImageTags || []).length === 1 && !!(after.ImageTags || {}).Primary, JSON.stringify([after.BackdropImageTags, after.ImageTags]));
  const token = `api_key=${auth.AccessToken}`;
  const stored = orientation(await bytes(`/Items/${series.Id}/Images/Backdrop/0?${token}`));
  check('apply images: the stored backdrop has Orientation 1 (shown landscape)', stored === 1, 'got ' + stored);
  check('apply images: the poster keeps Orientation 6 (only backdrops change)', orientation(await bytes(`/Items/${series.Id}/Images/Primary?${token}`)) === 6);
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  // the series was refreshed with the local title and images: refresh it so it follows the API again
  const back = ((await api('GET', `/Items?IncludeItemTypes=Series&Recursive=true&userId=${auth.User.Id}`)).json?.Items || []).find(i => /aventuriers|orientation/i.test(i.Name));
  if (back) { await api('POST', `/Items/${back.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true`); await wait(4000); }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
