// Episodes and photo albums sharing an identifier (SourceConfiguration.LinkEpisodeAlbums) on a real Jellyfin: which
// albums and photos reach the episode's gallery, who may see them, the image search, the "Photos" button and the source
// editor's check box. node episode-albums-real.mjs <url> <out_dir> <host_media_dir>
// <host_media_dir> is the folder the container mounts as /media. Needs the demo series of ui-test.sh. Writes its own
// albums and library, a temporary user; restores the plugin configuration and removes everything.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [url = 'http://localhost:8097', out = '/tmp/episode-albums-shots', media = path.join(process.env.HOME, '.cache/cm-ui-test/media')] = process.argv.slice(2);
const AUTH = 'MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-episode-albums", Version="1.0"';
const LIBRARY = 'Albums liés (test)';
const ROOT = path.join(media, 'episode-albums-test');
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const check = (label, ok, detail) => { if (ok) { passed++; console.log('PASS', label); } else { failed++; console.log('FAIL', label, detail === undefined ? '' : JSON.stringify(detail)); } };

function picture(file, color, size = '1200x800') {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `color=c=${color}:s=${size}`, '-frames:v', '1', file]);
}
fs.rmSync(ROOT, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
const TAGGED = `${ROOT}/Séance {DM-A1V1}`;                       // the tag in another case than the episode's
picture(`${TAGGED}/img10.jpg`, 'teal');
picture(`${TAGGED}/img2.jpg`, 'olive', '800x1200');
picture(`${TAGGED}/Coulisses/sous-album.jpg`, 'maroon');           // a sub-album's photo: left out
execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=2', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', `${TAGGED}/clip.mp4`]);
picture(`${ROOT}/Zoé {dm-a1v1}/deuxieme.jpg`, 'navy');           // a second album with the same identifier
picture(`${ROOT}/Sans identifiant/saisi.jpg`, 'purple');           // linked by an external id typed in Jellyfin
picture(`${ROOT}/Autre {dm-zzz}/etranger.jpg`, 'gray');            // linked to nothing

const login = async (name, pw) => (await fetch(`${url}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH.replace('cm-episode-albums', 'cm-ea-' + name) }, body: JSON.stringify({ Username: name, Pw: pw }) })).json();
const admin = await login('admin', 'admin');
const headers = token => ({ 'Content-Type': 'application/json', Authorization: `${AUTH}, Token="${token}"` });
const H = headers(admin.AccessToken);
H.Authorization = `MediaBrowser Token="${admin.AccessToken}"`;
async function api(method, route, body) {
    const r = await fetch(url + route, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await r.text();
    if (!r.ok) throw new Error(`${method} ${route} → ${r.status} ${text.slice(0, 300)}`);
    return text ? JSON.parse(text) : null;
}
const raw = (route, h = H) => fetch(route.startsWith('http') ? route : `${url}/${route.replace(/^\//, '')}`, { headers: h });
async function idle() {
    await sleep(4000);
    for (let i = 0; i < 120; i++) {
        const tasks = await api('GET', '/ScheduledTasks?isHidden=false');
        if (!tasks.some(t => t.State === 'Running')) { await sleep(3000); return; }
        await sleep(2000);
    }
}
const items = async query => (await api('GET', `/Items?userId=${admin.User.Id}&Recursive=true&Fields=Path&${query}`)).Items;

const plugin = (await api('GET', '/Plugins')).find(p => /custom ?metadata/i.test(p.Name));
const saved = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
const configure = async (linked, extra = {}) => {
    const config = structuredClone(saved);
    config.EnableEpisodePhotos = true;
    Object.assign(config, extra);
    config.Sources.find(s => s.Prefix === 'dm').LinkEpisodeAlbums = linked;
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, config);
};
let guest = null, browser = null;
try {
    await configure(false);
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); await idle(); } catch { /* not there */ }
    await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&collectionType=homevideos&refreshLibrary=true`, { LibraryOptions: { EnablePhotos: true, EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/episode-albums-test' }] } });
    await idle();

    const episodes = await items('IncludeItemTypes=Episode&IsMissing=false&Limit=1000');
    const episode = episodes.find(e => /\{dm-a1v1\}/.test(e.Path || '')), other = episodes.find(e => /\{dm-a1v2\}/.test(e.Path || ''));
    check('demo episodes found', !!episode && !!other);
    const library = await items('IncludeItemTypes=Photo,PhotoAlbum,Folder');
    const photo = name => library.find(i => (i.Path || '').endsWith('/' + name));
    check('test albums are in the library', ['img2.jpg', 'img10.jpg', 'deuxieme.jpg', 'saisi.jpg', 'etranger.jpg', 'sous-album.jpg'].every(photo), library.map(i => i.Path));

    const list = async (item, h) => { const r = await raw(`CustomMetadata/Photos/${item.Id}`, h); return r.ok ? (await r.json()).Photos : r.status; };
    const names = photos => photos.map(p => p.Name).join('|');

    const before = await list(episode);
    check('check box off: no album photo', !before.some(p => p.Key.startsWith('.album-')), names(before));

    await configure(true);
    let photos = await list(episode);
    let fromAlbums = photos.filter(p => p.Key.startsWith('.album-'));
    check('check box on: the photos of both albums, natural order, no sub-album, no video, any case of the tag',
        names(fromAlbums) === 'img2.jpg|img10.jpg|deuxieme.jpg', names(photos));
    check('album photos come after the episode\'s own', names(photos) === names([...before, ...fromAlbums]), names(photos));
    check('sizes are given', fromAlbums[0].Width === 800 && fromAlbums[0].Height === 1200, fromAlbums[0]);
    check('another episode gets nothing from these albums', !(await list(other)).some(p => p.Key.startsWith('.album-')));

    const untagged = library.find(i => (i.Path || '').endsWith('/Sans identifiant'));
    const full = await api('GET', `/Items/${untagged.Id}?userId=${admin.User.Id}`);
    await api('POST', `/Items/${untagged.Id}`, { ...full, ProviderIds: { ...full.ProviderIds, custom: 'dm-a1v1' } });
    photos = await list(episode);
    fromAlbums = photos.filter(p => p.Key.startsWith('.album-'));
    check('an untagged album linked by its external id', fromAlbums.some(p => p.Name === 'saisi.jpg') && fromAlbums.length === 4, names(photos));

    const first = fromAlbums[0];
    const thumb = await raw(first.ThumbnailUrl + '?width=320');
    check('thumbnail is a WebP', thumb.status === 200 && thumb.headers.get('content-type') === 'image/webp', thumb.status);
    check('original is served', (await raw(first.ImageUrl)).status === 200);
    check('without a session: 401', (await raw(first.ImageUrl, {})).status === 401);
    const id = item => item.Id.replace(/-/g, '');
    const route = (item, key) => `CustomMetadata/Photos/${item.Id}/Image/${key}`;
    for (const [label, item, key] of [
        ['a photo of an unrelated album', episode, `.album-${id(photo('etranger.jpg'))}`],
        ['a sub-album photo', episode, `.album-${id(photo('sous-album.jpg'))}`],
        ['a video of the album', episode, `.album-${id((await items('IncludeItemTypes=Video')).find(v => (v.Path || '').endsWith('/clip.mp4')))}`],
        ['an item that is no photo', episode, `.album-${id(other)}`],
        ['a malformed key', episode, '.album-..%2F..%2Fetc'],
        ['the album photo asked through another episode', other, first.Key]]) {
        const status = (await raw(route(item, key))).status;
        check(`refused ${label}: 404`, status === 404, status);
    }

    // ---- a user who sees the series but not the albums' library
    const folders = await api('GET', '/Library/VirtualFolders');
    guest = await api('POST', '/Users/New', { Name: 'albums-guest', Password: 'albums-guest' });
    await api('POST', `/Users/${guest.Id}/Policy`, { ...guest.Policy, EnableAllFolders: false, EnabledFolders: folders.filter(f => f.Name !== LIBRARY).map(f => f.ItemId) });
    const session = await login('albums-guest', 'albums-guest');
    const G = { Authorization: `MediaBrowser Token="${session.AccessToken}"` };
    const seen = await list(episode, G);
    check('a user without the albums library: the episode\'s own photos only', Array.isArray(seen) && names(seen) === names(before), seen);
    check('a user without the albums library: 404 on an album photo and its thumbnail', (await raw(first.ImageUrl, G)).status === 404 && (await raw(first.ThumbnailUrl, G)).status === 404);
    const guestSearch = await raw(`Items/${episode.Id}/RemoteImages?providerName=${encodeURIComponent("Photos de l'épisode")}&IncludeAllLanguages=true`, G);
    const guestImages = guestSearch.ok ? (await guestSearch.json()).Images : [];
    check('a user without the albums library: no album photo through the image search', !guestImages.some(i => i.Url.includes('.album-')), guestImages.map(i => i.Url));
    await api('POST', `/Users/${guest.Id}/Policy`, { ...guest.Policy, EnableAllFolders: true });
    check('the same user with the library sees them', (await list(episode, G)).length === photos.length && (await raw(first.ImageUrl, G)).status === 200);

    const search = (await api('GET', `/Items/${episode.Id}/RemoteImages?providerName=${encodeURIComponent("Photos de l'épisode")}&IncludeAllLanguages=true`)).Images;
    const linked = search.filter(i => i.Url.includes('.album-'));
    check('image search offers the album photos', linked.length === 4 && search.length === photos.length, search.map(i => i.Url));
    check('their signed link opens without a session', !!linked.length && (await raw(linked[0].Url, {})).status === 200);

    // ---- jellyfin-web: the button, the wall, the source editor
    browser = await chromium.launch({ channel: 'chrome' });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    page.on('console', m => { if (m.text().includes('[photos]')) { errors.push(m.text()); } });
    await page.goto(url + '/web/#/login');
    await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin'); await page.keyboard.press('Enter');
    await page.waitForFunction(() => !location.hash.includes('login'));
    await sleep(1500);
    await page.goto(`${url}/web/#/details?id=${episode.Id}`);
    const button = page.locator('.itemDetailPage:not(.hide) .eg-btn');
    await button.waitFor({ timeout: 20000 });
    check('the button counts the album photos', (await button.textContent()).includes(String(photos.length)), await button.textContent());
    await sleep(1500);
    await page.screenshot({ path: `${out}/1-episode.png` });
    await button.click();
    await page.waitForFunction(n => document.querySelectorAll('.eg-tile img.eg-loaded').length === n, photos.length, { timeout: 20000 });
    await sleep(800);
    await page.screenshot({ path: `${out}/2-wall.png` });
    await page.keyboard.press('Escape');

    const root = '#CustomMetadataConfigPage';
    const openSource = async () => {
        await page.goto(url + '/web/#/dashboard'); await sleep(800);
        await page.goto(url + '/web/#/configurationpage?name=Custom%20Metadata%20Provider');
        await page.locator(`${root} article.cm-card`, { hasText: 'Démo API' }).getByRole('button', { name: 'Modifier' }).click();
        await sleep(900);
    };
    const stored = async () => (await api('GET', `/Plugins/${plugin.Id}/Configuration`)).Sources.find(s => s.Prefix === 'dm').LinkEpisodeAlbums;
    await openSource();
    const box = page.locator(`${root} #srcLinkEpisodeAlbums`);
    check('page: the box shows the stored choice', await box.isChecked());
    await box.scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${out}/3-source-1440.png` });
    await box.uncheck();
    await page.click(`${root} #btnSaveStay`); await sleep(2500);
    check('page: unticking is saved', await stored() === false, await stored());
    await openSource();
    check('page: and shown unticked again', !(await box.isChecked()));
    await box.check();
    await page.click(`${root} #btnSaveStay`); await sleep(2500);
    check('page: ticking is saved', await stored() === true, await stored());
    await page.setViewportSize({ width: 412, height: 880 }); await sleep(600);
    await box.scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${out}/4-source-mobile.png` });
    const wide = await page.$eval(`${root} #srcLinkEpisodeAlbums`, e => { const r = e.closest('label').getBoundingClientRect(); return [r.left, r.right, innerWidth]; });
    check('page: the box fits the mobile screen', wide[0] >= 0 && wide[1] <= wide[2] + 1, wide);
    check('page: no script error', errors.filter(e => !/player cannot be null|reading 'selectedIndex'/.test(e)).length === 0, errors);

    await configure(true, { EnableEpisodePhotos: false });
    check('gallery off: nothing served', (await list(episode)).length === 0 && (await raw(first.ImageUrl)).status === 404);
} finally {
    await browser?.close();
    if (guest) { await api('DELETE', `/Users/${guest.Id}`); }
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, saved);
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); } catch { /* not created */ }
    fs.rmSync(ROOT, { recursive: true, force: true });
}
console.log(`\n${passed} PASS, ${failed} FAIL`);
process.exit(failed ? 1 : 0);
