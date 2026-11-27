// End-to-end test of photo albums and photos on a real Jellyfin: API and local sources, nested albums, targeted refresh, the
// configuration page. node albums-real.mjs <url> <out_dir> <host_media_dir>
// <host_media_dir> is the folder the container mounts as /media. The script makes its own media (ffmpeg, exiftool), its own fake
// API (port 8766 on the Docker gateway), its own library and sources, and puts the plugin configuration back at the end.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [url = 'http://localhost:8097', out = '/tmp/albums-shots', media = path.join(process.env.HOME, '.cache/cm-ui-test/media')] = process.argv.slice(2);
const API_HOST = '172.17.0.1', API_PORT = 8766;
const AUTH = 'MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-albums", Version="1.0"';
const LIBRARY = 'Albums (test)';
const ROOT = path.join(media, 'albums-test');
const DATA_ID = 'a1b2c3d4e5f60718';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const check = (label, ok, detail) => { if (ok) { passed++; console.log('PASS', label); } else { failed++; console.log('FAIL', label, detail === undefined ? '' : JSON.stringify(detail)); } };

// ---- media ---------------------------------------------------------------------------------------------------------
function picture(file, color, size = '1200x800') {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `color=c=${color}:s=${size}`, '-frames:v', '1', file]);
}
fs.rmSync(ROOT, { recursive: true, force: true });
const API_TOP = 'Voyage {al-v}', API_SUB = 'Bretagne {al-b}';
picture(`${ROOT}/${API_TOP}/${API_SUB}/phare.jpg`, 'teal');
picture(`${ROOT}/${API_TOP}/${API_SUB}/inconnue.jpg`, 'olive');
picture(`${ROOT}/${API_TOP}/${API_SUB}/plage {al-p1}.jpg`, 'maroon');
picture(`${ROOT}/Racine {lt-racine}/accueil.jpg`, 'navy');
picture(`${ROOT}/Racine {lt-racine}/Jour {lt-jour}/jetee.jpg`, 'purple');
picture(`${ROOT}/Racine {lt-racine}/Jour {lt-jour}/port.jpg`, 'gray');
picture(`${ROOT}/Sans identifiant/libre.jpg`, 'black');
for (const file of [`${ROOT}/${API_TOP}/${API_SUB}/inconnue.jpg`, `${ROOT}/${API_TOP}/${API_SUB}/phare.jpg`]) {
    execFileSync('exiftool', ['-q', '-overwrite_original', '-XMP-dc:Title=Titre EXIF', '-XMP-dc:Description=Légende EXIF', '-XMP-dc:Subject=mot-exif', '-DateTimeOriginal=2019:03:02 10:11:12', file]);
}

// ---- fake API --------------------------------------------------------------------------------------------------------
let version = 1;
const hits = {};
const server = http.createServer((req, res) => {
    const p = decodeURIComponent(req.url.split('?')[0]);
    hits[p] = (hits[p] || 0) + 1;
    const send = body => { res.writeHead(body ? 200 : 404, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body || {})); };
    const album = (title, extra = {}) => ({ title: `${title} v${version}`, overview: `Résumé ${title}`, premiereDate: '2025-07-14', tags: ['voyage'], genres: ['Paysage'], studio: 'Atelier', communityRating: 8, ...extra });
    if (p === '/albums/v') return send(album('Grand voyage'));
    if (p === '/albums/b') return send(album('La Bretagne', { tags: ['mer'], photos: [{ file: 'PHARE.JPG', title: `Le phare v${version}`, overview: 'Au couchant', premiereDate: '2025-07-15', tags: ['phare'], genres: ['Nuit'] }, { file: 'absente.jpg', title: 'Pas de fichier' }] }));
    if (p === '/photos/p1') return send({ title: `La plage v${version}`, overview: 'Venue de /photos', tags: ['sable'] });
    send(null);
});
await new Promise(r => server.listen(API_PORT, API_HOST, r));

// ---- Jellyfin ----------------------------------------------------------------------------------------------------------
const login = await (await fetch(`${url}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `${AUTH}, Token="${login.AccessToken}"` };
async function api(method, route, body) {
    const r = await fetch(url + route, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await r.text();
    if (!r.ok) throw new Error(`${method} ${route} → ${r.status} ${text.slice(0, 300)}`);
    return text ? JSON.parse(text) : null;
}
async function idle() {
    await sleep(4000);
    for (let i = 0; i < 120; i++) {
        const tasks = await api('GET', '/ScheduledTasks?isHidden=false');
        if (!tasks.some(t => t.State === 'Running')) { await sleep(4000); return; }
        await sleep(2000);
    }
}
async function refreshDone() {
    for (let i = 0; i < 90; i++) { await sleep(1500); const st = await api('GET', '/CustomMetadata/Refresh/Status'); if (!st.Running) return st; }
    throw new Error('targeted refresh never ended');
}
const items = async () => (await api('GET', `/Items?userId=${login.User.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum,Photo,Folder&Fields=Path,Overview,Genres,Tags,Studios,ProviderIds`)).Items.filter(i => (i.Path || '').includes('/albums-test/'));
const byEnd = (list, end) => list.find(i => i.Path.endsWith(end)) || {};

const plugin = (await api('GET', '/Plugins')).find(p => /custom ?metadata/i.test(p.Name));
const saved = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
const localContent = {
    Albums: [{ Id: 'racine', Title: 'Racine locale', Overview: 'Saisi dans le plugin', Studio: 'Maison', Tags: ['local'], PremiereDate: '2024-05-01', CommunityRating: 7.5, Photos: [],
        Albums: [{ Id: 'jour', Title: 'Premier jour', Tags: ['jour'], Albums: [], Photos: [{ File: 'jetee.jpg', Title: 'La jetée', Overview: 'Tôt le matin', Tags: ['aube'], Genres: ['Calme'] }, { File: 'port.jpg' }] }] }],
    Series: []
};

try {
    const config = structuredClone(saved);
    config.Sources = (config.Sources || []).filter(s => !['al', 'lt'].includes(s.Prefix));
    config.Sources.push(
        { Name: 'Albums API (test)', Prefix: 'al', SourceType: 'Api', Enabled: true, BaseUrl: `http://${API_HOST}:${API_PORT}`, ContentVideo: false, ContentAlbums: true, MaxRetries: 0, PhotoInheritAlbumTags: true },
        { Name: 'Albums locaux (test)', Prefix: 'lt', SourceType: 'Local', Enabled: true, BaseUrl: 'local://local', LocalDataId: DATA_ID, ContentVideo: false, ContentAlbums: true });
    config.EnableMetadata = true; config.EnableImages = true; config.EnableCache = false;
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, config);
    const stored = await api('PUT', `/CustomMetadata/Local/${DATA_ID}`, localContent);
    check('local content: nested albums and photos are stored', stored.Albums?.[0]?.Albums?.[0]?.Photos?.length === 2, stored);

    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); await idle(); } catch { /* not there */ }
    await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&collectionType=homevideos&refreshLibrary=true`, { LibraryOptions: { EnablePhotos: true, EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/albums-test' }] } });
    await idle();

    let list = await items();
    const top = byEnd(list, API_TOP), sub = byEnd(list, API_SUB);
    check('API: a folder holding only sub-albums is enriched', top.Type === 'Folder' && top.Name === 'Grand voyage v1' && top.Studios?.[0]?.Name === 'Atelier', top);
    check('API: the sub-album is enriched', sub.Type === 'PhotoAlbum' && sub.Name === 'La Bretagne v1' && sub.Tags?.includes('mer') && sub.Genres?.includes('Paysage'), sub);
    check('API: the album keeps its tag as external id', Object.entries(sub.ProviderIds || {}).some(([k, v]) => k.toLowerCase() === 'custom' && v === 'al-b'), sub.ProviderIds);
    const phare = byEnd(list, 'phare.jpg');
    check('API: a photo listed by its album (any case) gets the source over EXIF', phare.Name === 'Le phare v1' && phare.Overview === 'Au couchant' && phare.Genres?.includes('Nuit'), phare);
    check('API: its tags are the source\'s plus its album\'s', phare.Tags?.includes('phare') && phare.Tags?.includes('mer') && !phare.Tags?.includes('mot-exif'), phare.Tags);
    const unknown = byEnd(list, 'inconnue.jpg');
    check('API: an unlisted photo keeps its EXIF, plus the album tags', unknown.Name === 'Titre EXIF' && unknown.Overview === 'Légende EXIF' && unknown.Tags?.includes('mot-exif') && unknown.Tags?.includes('mer'), unknown);
    const beach = byEnd(list, '{al-p1}.jpg');
    check('API: a photo with its own tag uses /photos/{id}', beach.Name === 'La plage v1' && beach.Tags?.includes('sable') && hits['/photos/p1'] > 0, beach);
    check('untagged folder and photo are left alone', byEnd(list, 'Sans identifiant').Overview === undefined && byEnd(list, 'libre.jpg').Name === 'libre');
    const racine = byEnd(list, 'Racine {lt-racine}'), jour = byEnd(list, 'Jour {lt-jour}'), jetee = byEnd(list, 'jetee.jpg');
    check('local: the top album', racine.Name === 'Racine locale' && racine.Studios?.[0]?.Name === 'Maison' && racine.CommunityRating === 7.5, racine);
    check('local: the sub-album', jour.Name === 'Premier jour' && jour.Tags?.includes('jour'), jour);
    check('local: a described photo', jetee.Name === 'La jetée' && jetee.Overview === 'Tôt le matin' && jetee.Genres?.includes('Calme'), jetee);
    check('local: a photo listed without information is left alone', byEnd(list, 'port.jpg').Name === 'port', byEnd(list, 'port.jpg'));

    const before = hits['/albums/b'];
    version = 2;
    let started = await api('POST', '/CustomMetadata/Refresh', { Prefix: 'al', ReplaceImages: true });
    check('targeted refresh of an albums source counts albums and photos', started.Albums === 2 && started.Photos === 3, started);
    let done = await refreshDone();
    check('targeted refresh ends without failure', done.Failed === 0 && done.Done === done.Total, done);
    list = await items();
    check('API: new values after the targeted refresh', byEnd(list, API_SUB).Name === 'La Bretagne v2' && byEnd(list, 'phare.jpg').Name === 'Le phare v2' && byEnd(list, '{al-p1}.jpg').Name === 'La plage v2', byEnd(list, 'phare.jpg').Name);
    check('API: a refresh asks for an album only a few times, not once per photo', hits['/albums/b'] - before <= 5, hits['/albums/b'] - before);

    localContent.Albums[0].Title = 'Racine corrigée';
    localContent.Albums[0].Albums[0].Photos[1] = { File: 'port.jpg', Title: 'Le port' };
    await api('PUT', `/CustomMetadata/Local/${DATA_ID}`, localContent);
    await api('POST', '/CustomMetadata/Refresh', { Prefix: 'lt', Types: ['album', 'photo'], ReplaceImages: false, Tag: 'lt-racine', IncludeChildren: true });
    await refreshDone();
    list = await items();
    check('local: "Appliquer" updates the album, its sub-album and their photos', byEnd(list, 'Racine {lt-racine}').Name === 'Racine corrigée' && byEnd(list, 'port.jpg').Name === 'Le port', [byEnd(list, 'Racine {lt-racine}').Name, byEnd(list, 'port.jpg').Name]);

    const photos = await api('GET', '/CustomMetadata/Local/LibraryPhotos?tag=lt-jour');
    check('LibraryPhotos lists the files of the album folder', photos.Found && photos.Photos.map(p => p.File).join() === 'jetee.jpg,port.jpg', photos);
    check('LibraryPhotos says when no folder carries the tag', (await api('GET', '/CustomMetadata/Local/LibraryPhotos?tag=lt-rien')).Found === false);
    const source = (await api('GET', `/Plugins/${plugin.Id}/Configuration`)).Sources.find(s => s.Prefix === 'al');
    const testAlbum = await api('POST', '/CustomMetadata/TestSource', { Source: source, ItemType: 'album', Id: 'b' });
    check('source test: an album', testAlbum.Success && testAlbum.Metadata?.Title === 'La Bretagne v2', testAlbum.Error);
    const testList = await api('POST', '/CustomMetadata/TestSource', { Source: source, ItemType: 'albumphotos', Id: 'b' });
    check('source test: the photos of an album', testList.Success && testList.PhotoCount === 2 && testList.Photos[0].File === 'PHARE.JPG', testList);

    // ---- configuration page ----------------------------------------------------------------------------------------
    fs.mkdirSync(out, { recursive: true });
    const browser = await chromium.launch({ channel: 'chrome' });
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        await page.goto(url + '/web/');
        await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin'); await page.keyboard.press('Enter');
        await page.waitForTimeout(4000);
        const root = '#CustomMetadataConfigPage';
        const openSource = async name => {
            await page.goto(url + '/web/#/dashboard'); await page.waitForTimeout(800);
            await page.goto(url + '/web/#/configurationpage?name=Custom%20Metadata%20Provider');
            await page.locator(`${root} article.cm-card`, { hasText: name }).getByRole('button', { name: 'Modifier' }).click();
            await page.waitForTimeout(900);
        };
        const shot = async name => { await page.screenshot({ path: `${out}/${name}.png` }); console.log('📸', `${out}/${name}.png`); };
        const visibleNav = () => page.$$eval(`${root} .cm-nav-item`, all => all.filter(e => e.offsetParent).map(e => e.getAttribute('data-go')));

        await openSource('Albums API (test)');
        check('page: an albums-only API source shows only the album sections', (await visibleNav()).join() === 'general,album,photo,network', await visibleNav());
        await page.locator(`${root} #srcContentVideo`).check();
        check('page: ticking videos brings their sections back', (await visibleNav()).includes('endpoints'), await visibleNav());
        await shot('A1-general-1440');
        await page.locator(`${root} #srcContentVideo`).uncheck();
        await page.locator(`${root} #srcContentAlbums`).click();
        check('page: the last content stays ticked', await page.locator(`${root} #srcContentAlbums`).isChecked());
        await page.click(`${root} [data-go="photo"]`); await shot('A2-photo-mapping-1440');
        await page.click(`${root} [data-go="album"]`);
        await page.selectOption(`${root} #testItemType`, 'albumphotos'); await page.fill(`${root} #testItemId`, 'b'); await page.click(`${root} #btnTestSource`);
        await page.waitForSelector(`${root} .cm-photo-list li`, { timeout: 15000 });
        check('page: the test lists the photos of the album', (await page.locator(`${root} .cm-photo-list li`).count()) === 2);
        await shot('A3-album-test-1440');

        await openSource('Albums locaux (test)');
        check('page: a local albums-only source has no empty « Avancé » group', !(await page.locator(`${root} #navAdvanced`).isVisible()));
        await page.click(`${root} [data-go="albums"]`);
        await page.click(`${root} [data-node="a:racine"]`);
        await page.click(`${root} [data-node="a:jour"]`);
        check('page: the path of a sub-album is shown', (await page.textContent(`${root} .cm-path`)).includes('Racine corrigée › Premier jour'));
        await shot('A4-local-album-1440');
        await page.click(`${root} [data-ltab-btn="photos"]`);
        await page.click(`${root} [data-photos-read]`);
        await page.waitForSelector(`${root} .cm-photo img`, { timeout: 15000 });
        check('page: reading the folder keeps one card per photo', (await page.locator(`${root} .cm-photo`).count()) === 2);
        await page.click(`${root} [data-photo="1"]`);
        await page.fill(`${root} #lp-Overview`, 'Écrit depuis la page');
        await page.waitForTimeout(800);
        await shot('A5-local-photos-1440');
        await page.setViewportSize({ width: 1920, height: 1080 }); await page.waitForTimeout(500); await shot('A6-local-photos-1920');
        await page.setViewportSize({ width: 412, height: 880 }); await page.waitForTimeout(500);
        // Elements of the album editor sticking out of the screen (what scrolls inside its own box does not count)
        const sticking = await page.$$eval(`${root} #localDetail, ${root} #localDetail *`, all => all.filter(e => e.offsetParent && (e.getBoundingClientRect().right > innerWidth + 1 || e.getBoundingClientRect().left < -1))
            .map(e => `${e.tagName}.${e.className} ${Math.round(e.getBoundingClientRect().left)}-${Math.round(e.getBoundingClientRect().right)}`).slice(0, 5));
        const widths = await page.evaluate(() => [document.documentElement.scrollWidth, innerWidth]);
        check('page: the album editor fits the mobile screen', sticking.length === 0, sticking);
        console.log('mobile: document width', widths.join(' / '));
        await page.locator(`${root} .cm-photos`).scrollIntoViewIfNeeded(); await shot('A7-local-photos-mobile');
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.click(`${root} [data-ltab-btn="info"]`);
        await page.click(`${root} [data-node="a:jour"]`);
        await page.click(`${root} [data-add="album"][data-parent="a:jour"]`);
        await page.fill(`${root} #lf-Title`, 'Ajouté par la page');
        await page.fill(`${root} #lf-CommunityRating`, '6,5');
        await page.click(`${root} #btnSaveStay`);
        await page.waitForTimeout(3000);
        const after = await api('GET', `/CustomMetadata/Local/${DATA_ID}`);
        const day = after.Albums[0].Albums[0];
        check('page: saving stores the photo text and the new sub-album', day.Photos.find(p => p.File === 'port.jpg')?.Overview === 'Écrit depuis la page' && day.Albums[0]?.Title === 'Ajouté par la page' && day.Albums[0]?.CommunityRating === 6.5, day);
        const kept = (await api('GET', `/Plugins/${plugin.Id}/Configuration`)).Sources.find(s => s.Prefix === 'lt');
        check('page: saving keeps the content choice', kept.ContentAlbums === true && kept.ContentVideo === false, kept);
        check('page: no script error', errors.length === 0, errors);
    } finally {
        await browser.close();
    }
} finally {
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, saved);
    try { await api('DELETE', `/CustomMetadata/Local/${DATA_ID}`); } catch { /* nothing stored */ }
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); } catch { /* not created */ }
    fs.rmSync(ROOT, { recursive: true, force: true });
    server.close();
}
console.log(`\n${passed} PASS, ${failed} FAIL`);
process.exit(failed ? 1 : 0);
