// Episode photo gallery in the real server: the numbered folder next to a video and the local source's "Photo" images,
// access rights, signed links of the image search, the library scan (photo folders must create nothing), then the wall
// and the viewer in jellyfin-web at 1440, 1920 and mobile, with and without the Projection theme.
// Usage: node photos-real.mjs <base_url> <out_dir> [media_dir]   (media_dir: the folder mounted as /media).
// Writes photo folders in the media folder, a temporary local source and a user; restores and removes everything.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [J, outDir, mediaDir = path.join(process.env.CM_UI_WORK || path.join(os.homedir(), '.cache/cm-ui-test'), 'media')] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node photos-real.mjs <base_url> <out_dir> [media_dir]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'photostest00001';
// stored portrait (32x48) with EXIF Orientation 6: shown landscape
const ROTATED = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/4QBiRXhpZgAATU0AKgAAAAgABQESAAMAAAABAAYAAAEaAAUAAAABAAAASgEbAAUAAAABAAAAUgEoAAMAAAABAAEAAAITAAMAAAABAAEAAAAAAAAAAAABAAAAAQAAAAEAAAAB/9sAQwAKBwcIBwYKCAgICwoKCw4YEA4NDQ4dFRYRGCMfJSQiHyIhJis3LyYpNCkhIjBBMTQ5Oz4+PiUuRElDPEg3PT47/9sAQwEKCwsODQ4cEBAcOygiKDs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7/8AAEQgAMAAgAwEiAAIRAQMRAf/EABUAAQEAAAAAAAAAAAAAAAAAAAAF/8QAFhAAAwAAAAAAAAAAAAAAAAAAABRh/8QAFgEBAQEAAAAAAAAAAAAAAAAAAAYF/8QAFhEBAQEAAAAAAAAAAAAAAAAAABMU/9oADAMBAAIRAxEAPwC2zQzSSzQzSGyKaqszQzSSzQzRkKpLNDNJTNDNKzIzKqrNDNJTNDNGQqks0M0lM0M0q8jNqqs0M0lM0M0ZCr//2Q==', 'base64');
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

const login = async (name, pw) => (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="t", Device="cli", DeviceId="cm-photos-${name}", Version="1.0"` },
  body: JSON.stringify({ Username: name, Pw: pw }) })).json();
const auth = await login('admin', 'admin');
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const raw = (url, headers = H, method = 'GET', body) => fetch(url.startsWith('http') ? url : `${J}/${url.replace(/^\//, '')}`, { method, headers, body });
const api = async (method, url, body) => {
  const r = await raw(url, H, method, body === undefined ? undefined : JSON.stringify(body));
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch {}
  return { status: r.status, json };
};
const items = async query => (await api('GET', `/Items?userId=${auth.User.Id}&Recursive=true&${query}`)).json?.Items || [];
const counts = async () => JSON.stringify([(await items('IncludeItemTypes=Season&Fields=Path')).length, (await items('IncludeItemTypes=Episode&IsMissing=false')).length, (await items('IncludeItemTypes=Photo,PhotoAlbum,Folder')).length]);
const scan = async () => {
  await api('POST', '/Library/Refresh');
  await wait(3000);
  for (let i = 0; i < 90; i++) {
    const task = (await api('GET', '/ScheduledTasks')).json.find(t => t.Key === 'RefreshLibrary');
    if (task.State === 'Idle') { break; }
    await wait(1000);
  }
  await wait(2000);
};

const series = path.join(mediaDir, 'series/Les Aventuriers {dm-aventuriers}');
const season = path.join(series, 'Saison 2024 {dm-a1}');
const folders = [path.join(season, '01'), path.join(season, '0002-0003'), path.join(series, '9')];
const outside = path.join(mediaDir, 'photos-outside.jpg');
const picture = (file, size, from, to) => execFileSync('convert', ['-size', size, `gradient:${from}-${to}`, '-fill', 'rgba(255,245,215,0.9)', '-draw', 'circle 140,160 140,260', file]);

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const branding = (await api('GET', '/System/Configuration/branding')).json;
let guest = null, browser = null, chosen = null;
try {
  const before = await counts();
  folders.forEach(f => fs.mkdirSync(f, { recursive: true }));
  const sizes = ['1600x900', '800x1200', '1500x1000', '1920x1080', '900x900', '800x1200', '1500x1000', '1920x800', '1200x800', '700x1050', '1600x900'];
  const colors = ['#e0812b', '#d9bf2e', '#8ccf2a', '#2bd34a', '#27d6a0', '#28d3d3', '#2a9bd9', '#3156d9', '#5a34d9', '#9a2fd9', '#d92fc9'];
  sizes.forEach((s, i) => picture(path.join(folders[0], `img${i + 1}.jpg`), s, colors[i], '#101a38'));
  fs.writeFileSync(path.join(folders[0], 'zz rotated.jpg'), ROTATED);
  fs.writeFileSync(path.join(folders[0], 'notes.txt'), 'not a photo');
  fs.writeFileSync(path.join(folders[0], '.hidden.jpg'), ROTATED);
  fs.writeFileSync(outside, ROTATED);
  try { fs.symlinkSync(outside, path.join(folders[0], 'escape.jpg')); } catch {}
  picture(path.join(folders[1], 'double.jpg'), '1200x800', '#444444', '#bbbbbb');
  picture(path.join(folders[2], 'root.jpg'), '1200x800', '#222222', '#999999');

  // the gallery on: its ignore rule keeps photo folders out of the library
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, { ...original, EnableEpisodePhotos: true });
  await scan();
  check('a library scan creates nothing for the photo folders (seasons, episodes, photo items)', await counts() === before, before + ' -> ' + await counts());
  (await items('IncludeItemTypes=Season,Folder,Photo,PhotoAlbum&Fields=Path')).filter(s => /\/(9|01|0002-0003)(\/|$)/.test(s.Path || '') || /Photo/.test(s.Type)).forEach(s => console.log('   created:', s.Type, s.Path));

  const episodes = await items('IncludeItemTypes=Episode&Fields=Path&Limit=1000');
  const first = episodes.find(e => /\{dm-a1v1\}/.test(e.Path || '')), second = episodes.find(e => /\{dm-a1v2\}/.test(e.Path || ''));
  const bare = episodes.find(e => /\{dm-a2v1\}/.test(e.Path || ''));
  const missing = episodes.find(e => e.LocationType === 'Virtual' && e.ProviderIds === undefined) || episodes.find(e => e.LocationType === 'Virtual');
  const missingFull = missing && (await api('GET', `/Items/${missing.Id}?userId=${auth.User.Id}`)).json;
  const missingTag = missingFull && Object.entries(missingFull.ProviderIds || {}).find(([k]) => k.toLowerCase() === 'custom')?.[1];
  check('demo episodes found', !!first && !!second && !!bare);

  // ---- switched off: nothing is served
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, { ...original, EnableEpisodePhotos: false, EnableThemeScript: false });
  check('setting off: empty list', (await api('GET', `/CustomMetadata/Photos/${first.Id}`)).json?.Photos?.length === 0);
  check('setting off: no gallery script in the page', !(await (await raw('web/index.html', {})).text()).includes('data-episode-photos'));

  // ---- a local source with photos, one backdrop, laid over the first episode and over a missing one
  const config = structuredClone(original);
  Object.assign(config, { EnableEpisodePhotos: true, EnableThemeScript: false });
  config.Sources = config.Sources.filter(s => s.LocalDataId !== DATA).concat({ Name: 'Photos test', Prefix: 'ph', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  const upload = async (node, buffer) => (await (await raw(`CustomMetadata/Local/${DATA}/Image?node=${node}`, { 'Content-Type': 'image/jpeg', Authorization: H.Authorization }, 'POST', buffer)).json()).Url;
  const local = path.join(outDir, 'local.jpg');
  picture(local, '1400x900', '#f1de7b', '#29968c');
  const photo = await upload('dm-a1v1', fs.readFileSync(local));
  const backdrop = await upload('dm-a1v1', ROTATED);
  const episodeNodes = [{ Id: 'dm-a1v1', Gallery: [{ Type: 'Photo', Url: photo, Name: 'depuis la source' }, { Type: 'Backdrop', Url: backdrop }] }];
  if (missingTag) { episodeNodes.push({ Id: missingTag, Gallery: [{ Type: 'Photo', Url: photo }] }); }
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'serie', Title: 'Photos', Seasons: [{ Id: 'saison', Episodes: episodeNodes }] }] });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  const stored = (await api('GET', `/CustomMetadata/Local/${DATA}`)).json;
  check('the local source keeps the "Photo" type', stored.Series[0].Seasons[0].Episodes[0].Gallery.some(g => g.Type === 'Photo'));

  const list = (await api('GET', `/CustomMetadata/Photos/${first.Id}`)).json.Photos;
  const names = list.map(p => p.Name);
  check('local photo first, then the folder in natural order (13 photos; backdrop, text, hidden, escaping link excluded)',
    names.join('|') === ['depuis la source', ...sizes.map((_, i) => `img${i + 1}.jpg`), 'zz rotated.jpg'].join('|'), names.join('|'));
  check('sizes are given, EXIF rotation applied', list[1].Width === 1600 && list[1].Height === 900 && list.at(-1).Width === 48 && list.at(-1).Height === 32, JSON.stringify([list[1], list.at(-1)]));
  const thumb = await raw(list[1].ThumbnailUrl + '?width=300');
  check('thumbnail is a WebP', thumb.status === 200 && thumb.headers.get('content-type') === 'image/webp', thumb.status + ' ' + thumb.headers.get('content-type'));
  check('the local photo and its original are served', (await raw(list[0].ImageUrl)).status === 200 && (await raw(list[0].ThumbnailUrl)).status === 200);
  check('range folder "0002-0003" serves episode 2', (await api('GET', `/CustomMetadata/Photos/${second.Id}`)).json.Photos.map(p => p.Name).join() === 'double.jpg');
  check('an episode without photos: empty list', (await api('GET', `/CustomMetadata/Photos/${bare.Id}`)).json.Photos.length === 0);
  if (missingTag) { check('a missing episode shows its local photos', (await api('GET', `/CustomMetadata/Photos/${missing.Id}`)).json.Photos.length === 1, missingTag); }
  else { console.log('SKIP no missing episode in the library'); }

  for (const key of ['..%2F..%2Fphotos-outside.jpg', '.hidden.jpg', 'notes.txt', 'escape.jpg']) {
    const status = (await raw(`CustomMetadata/Photos/${first.Id}/Image/${key}`)).status;
    check(`refused file ${decodeURIComponent(key)}: 404`, status === 404, String(status));
  }
  check('without a session: 401', (await raw(`CustomMetadata/Photos/${first.Id}`, {})).status === 401 && (await raw(list[1].ImageUrl, {})).status === 401);
  guest = (await api('POST', '/Users/New', { Name: 'photos-guest', Password: 'photos-guest' })).json;
  await api('POST', `/Users/${guest.Id}/Policy`, { ...guest.Policy, EnableAllFolders: false, EnabledFolders: [] });
  const other = await login('photos-guest', 'photos-guest');
  const OH = { Authorization: `MediaBrowser Token="${other.AccessToken}"` };
  check('a user without the library: 404 on the list and on a photo', (await raw(`CustomMetadata/Photos/${first.Id}`, OH)).status === 404 && (await raw(list[1].ImageUrl, OH)).status === 404);

  // ---- the local source's served data never carries the photo as an image
  const offered = (await api('GET', `/Items/${first.Id}/RemoteImages?type=Backdrop&IncludeAllLanguages=true`)).json.Images;
  check('the photo is not offered as a backdrop', !offered.some(i => i.Url.endsWith(photo.split('/').pop())), JSON.stringify(offered.map(i => i.Url)));

  // ---- image search: signed links, download through Jellyfin (the server cannot reach the browser's host:port)
  const search = (await api('GET', `/Items/${first.Id}/RemoteImages?providerName=${encodeURIComponent("Photos de l'épisode")}&IncludeAllLanguages=true`)).json.Images;
  check('image search offers the 13 photos', search.length === 13, String(search.length));
  check('a signed link opens without a session', (await raw(search[1].Url, {})).status === 200);
  check('a tampered link: 404', (await raw(search[1].Url.replace('img1.jpg', 'img2.jpg'), {})).status === 404);
  chosen = first;
  const hadImage = (await api('GET', `/Items/${first.Id}?userId=${auth.User.Id}`)).json.ImageTags?.Primary;
  const download = await api('POST', `/Items/${first.Id}/RemoteImages/Download?type=Primary&imageUrl=${encodeURIComponent(search.at(-1).Url)}`);
  const hasImage = (await api('GET', `/Items/${first.Id}?userId=${auth.User.Id}`)).json.ImageTags?.Primary;
  check('choosing a photo sets the episode image', download.status === 204 && hasImage && hasImage !== hadImage, download.status + ' ' + hadImage + ' ' + hasImage);
  const applied = Buffer.from(await (await raw(`Items/${first.Id}/Images/Primary`)).arrayBuffer());
  check('the chosen rotated photo is stored upright', applied.includes(Buffer.from([0xFF, 0xC0, 0, 17, 8, 0, 32, 0, 48])), applied.length + ' bytes');

  // ---- jellyfin-web
  check('setting on: the gallery script is in the page', (await (await raw('web/index.html', {})).text()).includes('data-episode-photos'));
  browser = await chromium.launch({ channel: 'chrome' });
  for (const themed of [false, true]) {
    await api('POST', `/Plugins/${PLUGIN}/Configuration`, { ...config, EnableThemeScript: themed });
    await api('POST', '/System/Configuration/branding', { ...branding, CustomCss: themed ? '@import url("../CustomMetadata/Theme/projection.css");' : '' });
    for (const [width, height] of [[1440, 900], [1920, 1080], [412, 860]]) {
      const mobile = width < 600;
      const tag = `${themed ? 'theme' : 'stock'}-${width}`;
      // a phone gets an Android user agent, so jellyfin-web picks its mobile layout as on a real one
      const context = await browser.newContext({ viewport: { width, height }, isMobile: mobile, hasTouch: mobile,
        ...(mobile ? { userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36' } : {}) });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      page.on('console', m => { if (m.text().includes('[photos]')) { errors.push(m.text()); } });
      await page.goto(`${J}/web/#/login`);
      await page.fill('#txtManualName', 'admin');
      await page.fill('#txtManualPassword', 'admin');
      await page.click('.manualLoginForm button[type=submit]');
      await page.waitForFunction(() => !location.hash.includes('login'));
      await page.goto(`${J}/web/#/details?id=${first.Id}`);
      const button = page.locator('.itemDetailPage:not(.hide) .eg-btn');
      await button.waitFor({ timeout: 20000 });
      await wait(2500);
      check(`${tag}: one "Photos" button with its count`, await button.count() === 1 && (await button.textContent()).includes('13'));
      const box = await button.boundingBox();
      check(`${tag}: the button is one line`, box.height <= 48 && box.width > box.height, JSON.stringify(box));
      await page.screenshot({ path: path.join(outDir, `${tag}-1-episode.png`) });
      await button.click();
      await page.locator('.eg-tile img.eg-loaded').first().waitFor();
      await wait(2000);
      const wall = await page.evaluate(() => {
        const w = document.querySelector('.eg-wall'), t = [...document.querySelectorAll('.eg-tile')];
        const rows = {};
        t.forEach(x => { const r = x.getBoundingClientRect(); (rows[Math.round(r.top)] ||= []).push(r); });
        const grid = document.querySelector('.eg-grid').getBoundingClientRect();
        return { count: t.length, overflow: w.scrollWidth - w.clientWidth, loaded: document.querySelectorAll('.eg-tile img.eg-loaded').length,
          fill: Object.values(rows).slice(0, -1).map(r => Math.round(grid.right - Math.max(...r.map(x => x.right)))),
          heights: Object.values(rows).map(r => Math.round(r[0].height)), title: document.querySelector('.eg-title').textContent, sub: document.querySelector('.eg-sub').textContent };
      });
      console.log('   wall', JSON.stringify(wall));
      check(`${tag}: 13 tiles, no horizontal scroll, full rows fill the width`, wall.count === 13 && wall.overflow <= 0 && wall.fill.every(g => Math.abs(g) <= 2), JSON.stringify(wall));
      await page.screenshot({ path: path.join(outDir, `${tag}-2-wall.png`) });
      await page.locator('.eg-tile').nth(1).click();
      await page.waitForFunction(() => document.querySelector('.eg-big').naturalWidth === 1600);
      await wait(1200);
      check(`${tag}: the viewer covers the wall (counter and back button on top)`, await page.evaluate(() => ['.eg-counter', '.eg-back'].every(s => {
        const e = document.querySelector(s), r = e.getBoundingClientRect();
        return e.contains(document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2));
      })));
      await page.screenshot({ path: path.join(outDir, `${tag}-3-viewer.png`) });
      await page.keyboard.press('ArrowRight');
      await page.waitForFunction(() => document.querySelector('.eg-big').naturalWidth === 800);
      check(`${tag}: arrow goes to the next photo`, (await page.textContent('.eg-counter')).includes('3 / 13'));
      await wait(900);
      await page.screenshot({ path: path.join(outDir, `${tag}-4-portrait.png`) });
      await page.keyboard.press('Escape');
      check(`${tag}: Escape returns to the wall, focus on that photo`, await page.evaluate(() => document.querySelector('.eg-viewer').hidden && document.activeElement === document.querySelectorAll('.eg-tile')[2]));
      await page.keyboard.press('Escape');
      check(`${tag}: Escape closes the gallery`, await page.locator('.eg-root').count() === 0 && page.url().includes(first.Id));
      await button.click();
      await page.locator('.eg-root').waitFor();
      await page.goBack();
      await wait(1000);
      check(`${tag}: leaving the page closes the gallery`, await page.locator('.eg-root').count() === 0);
      await page.goto(`${J}/web/#/details?id=${bare.Id}`);
      await wait(3000);
      check(`${tag}: no button on an episode without photos`, await page.locator('.itemDetailPage:not(.hide) .eg-btn').count() === 0);
      check(`${tag}: no script error`, errors.filter(e => !/player cannot be null/.test(e)).length === 0, errors.join(' | '));
      await context.close();
    }
  }
} finally {
  await browser?.close();
  if (guest) { await api('DELETE', `/Users/${guest.Id}`); }
  await api('POST', '/System/Configuration/branding', branding);
  if (chosen) { await api('DELETE', `/Items/${chosen.Id}/Images/Primary`); }
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  folders.forEach(f => fs.rmSync(f, { recursive: true, force: true }));
  fs.rmSync(outside, { force: true });
  await scan();
  if (chosen) { await api('POST', `/Items/${chosen.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllImages=true`); }
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
