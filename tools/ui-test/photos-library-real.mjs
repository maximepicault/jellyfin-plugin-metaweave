// The theme's photo library screens (theme/projection-photos.js) on a real Jellyfin: wall of albums, search and tags, an
// album's head whatever the shape of its picture, its sub-albums and photos, the viewer, phone width.
// node photos-library-real.mjs <url> <out_dir> <host_media_dir>
// <host_media_dir> is the folder the container mounts as /media. The script makes its own media (ffmpeg), local source and
// library, turns the theme and its script on, and puts the configuration and the Custom CSS back at the end.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [url = 'http://localhost:8097', out = '/tmp/photos-library-shots', media = path.join(process.env.HOME, '.cache/cm-ui-test/media')] = process.argv.slice(2);
const AUTH = 'MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-photos-library", Version="1.0"';
const LIBRARY = 'Photothèque (test)';
const ROOT = path.join(media, 'photos-library-test');
const DATA_ID = 'b1b2c3d4e5f60719';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const check = (label, ok, detail) => { if (ok) { passed++; console.log('PASS', label); } else { failed++; console.log('FAIL', label, detail === undefined ? '' : JSON.stringify(detail)); } };

function picture(file, size, c0, c1) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const [w, h] = size.split('x');
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `gradients=s=${size}:c0=${c0}:c1=${c1}:x0=0:y0=0:x1=${w}:y1=${h}:nb_colors=2:speed=0.00001`, '-frames:v', '1', file]);
}
fs.rmSync(ROOT, { recursive: true, force: true });
const TOP = 'Voyages {pt-voyages}', SEA = 'Bretagne {pt-bretagne}', DAY = 'Jour 1 {pt-jour}', TALL = 'Portraits {pt-portraits}', BIG = 'Grande série {pt-grande}';
[['phare.jpg', '1200x800', '0x2f7d89', '0x123240'], ['jetee.jpg', '800x1200', '0xb4603a', '0x3b1d2e'], ['port.jpg', '1600x900', '0x8a7a2a', '0x223a22'], ['maree.jpg', '1000x1000', '0x4a568f', '0x171c3a'], ['filets.jpg', '800x1100', '0x8a3d62', '0x2a1838']]
    .forEach(([n, size, a, b]) => picture(`${ROOT}/${TOP}/${SEA}/${n}`, size, a, b));
picture(`${ROOT}/${TOP}/${SEA}/${DAY}/aube.jpg`, '1200x800', '0xc9863a', '0x31203a');
for (let i = 1; i <= 3; i++) picture(`${ROOT}/${TALL}/portrait${i}.jpg`, '800x1200', '0x3d7a55', '0x10251f');
picture(`${ROOT}/${BIG}/0001.jpg`, '600x400', '0x9a7048', '0x2d2118');
for (let i = 2; i <= 150; i++) fs.copyFileSync(`${ROOT}/${BIG}/0001.jpg`, `${ROOT}/${BIG}/${String(i).padStart(4, '0')}.jpg`);
// videos: one the browser plays, one it cannot (AVI), and an album holding videos only
function video(file, codec) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=3', '-c:v', codec, '-pix_fmt', 'yuv420p', file]);
}
video(`${ROOT}/${TOP}/${SEA}/clip.mp4`, 'libx264');
video(`${ROOT}/${TOP}/${SEA}/vieux.avi`, 'mpeg4');
const FILMS = 'Films {pt-films}';
video(`${ROOT}/${TALL}/${FILMS}/dunes.mp4`, 'libx264');
video(`${ROOT}/${TALL}/${FILMS}/vagues.mp4`, 'libx264');
const art = `${ROOT}.art`;
// test cards: their shape and edges are plain to see
fs.mkdirSync(art, { recursive: true });
for (const [name, size] of [['tall', '900x1350'], ['wide', '1920x1080']]) execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `testsrc2=s=${size}`, '-frames:v', '1', `${art}/${name}.jpg`]);

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
    for (let i = 0; i < 150; i++) {
        if (!(await api('GET', '/ScheduledTasks?isHidden=false')).some(t => t.State === 'Running')) { await sleep(5000); return; }
        await sleep(2000);
    }
}
async function upload(node, file) {
    const r = await fetch(`${url}/CustomMetadata/Local/${DATA_ID}/Image?node=${node}`, { method: 'POST', headers: { ...H, 'Content-Type': 'application/octet-stream' }, body: fs.readFileSync(file) });
    return (await r.json()).Url;
}

const plugin = (await api('GET', '/Plugins')).find(p => /custom ?metadata/i.test(p.Name));
const saved = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
const branding = await api('GET', '/System/Configuration/branding');
try {
    const config = structuredClone(saved);
    config.Sources = (config.Sources || []).filter(s => s.Prefix !== 'pt');
    config.Sources.push({ Name: 'Photothèque locale (test)', Prefix: 'pt', SourceType: 'Local', Enabled: true, BaseUrl: 'local://local', LocalDataId: DATA_ID, ContentVideo: false, ContentAlbums: true });
    Object.assign(config, { EnableMetadata: true, EnableImages: true, EnableThemeScript: true });
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, config);
    await api('POST', '/System/Configuration/branding', { ...branding, CustomCss: '@import url("../CustomMetadata/Theme/projection.css");' });
    const wide = await upload('bretagne', `${art}/wide.jpg`), tall = await upload('voyages', `${art}/tall.jpg`);
    await api('PUT', `/CustomMetadata/Local/${DATA_ID}`, { Series: [], Albums: [
        { Id: 'voyages', Title: 'Voyages', Overview: 'Une image de fond en hauteur.', Backdrop: tall, Tags: ['voyage'], Albums: [
            { Id: 'bretagne', Title: 'Bretagne', Overview: 'Quatre jours sur la côte, du phare à la jetée.\nDes images prises tôt le matin.\n' + 'La marche suit le sentier des douaniers, entre la lande et les falaises, et s\'arrête chaque soir dans un port différent. '.repeat(8) + '\nFin du carnet.', PremiereDate: '2025-07-14', CommunityRating: 8.4, Studio: 'Atelier Démo', Backdrop: wide, Tags: ['mer', 'été'], Genres: ['Paysage'],
              Photos: [{ File: 'clip.mp4', Title: 'Le tour du phare', Tags: ['film'] }, { File: 'phare.jpg', Title: 'Le phare au couchant', Overview: 'Au pied de la falaise.', PremiereDate: '2025-07-15', Tags: ['phare', 'soir'], Genres: ['Paysage'] }],
              Albums: [{ Id: 'jour', Title: 'Premier jour', Tags: ['aube'] }] }] },
        { Id: 'portraits', Title: 'Portraits', Tags: ['portrait'], Albums: [{ Id: 'films', Title: 'Films' }] },
        { Id: 'grande', Title: 'Grande série', Tags: ['série'] }] });
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); await idle(); } catch { /* not there */ }
    await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&collectionType=homevideos&refreshLibrary=true`, { LibraryOptions: { EnablePhotos: true, EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/photos-library-test' }] } });
    await idle();
    const lib = (await api('GET', `/UserViews?userId=${login.User.Id}`)).Items.find(v => v.Name === LIBRARY);

    fs.mkdirSync(out, { recursive: true });
    const browser = await chromium.launch({ channel: 'chrome' });
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
        const errors = [], warnings = [];
        page.on('pageerror', e => errors.push(e.message));
        page.on('console', m => m.text().startsWith('[projection') && warnings.push(m.text()));
        await page.goto(url + '/web/');
        await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin'); await page.keyboard.press('Enter');
        await page.waitForTimeout(4000);
        const P = '.page:not(.hide) .pj-ph';
        const shot = async name => { await page.waitForFunction(() => !document.querySelector('.mdlSpinnerActive'), null, { timeout: 8000 }).catch(() => check(`${name}: jellyfin-web's spinner does not stay`, false)); await page.waitForTimeout(900); await page.screenshot({ path: `${out}/${name}.png` }); console.log('📸', `${out}/${name}.png`); };
        const texts = sel => page.$$eval(sel, all => all.map(e => e.textContent));
        const fits = () => page.$$eval(`${P}, ${P} *`, all => all.filter(e => e.offsetParent && !e.closest('.pj-ph-ribbon, .pj-ph-pic') && (e.getBoundingClientRect().right > innerWidth + 1 || e.getBoundingClientRect().left < -1)).map(e => e.className).slice(0, 5));

        await page.goto(`${url}/web/#/homevideos?topParentId=${lib.Id}`);
        await page.waitForSelector(`${P} .pj-ph-card`, { timeout: 20000 });
        const cards = await texts(`${P} .pj-ph-card strong`);
        check('library: the wall shows the top albums only', cards.join() === 'Grande série,Portraits,Voyages', cards);
        check('library: jellyfin-web\'s own cards are hidden', await page.$$eval('.page:not(.hide) > :not(.pj-ph)', all => all.every(e => !e.offsetParent)));
        const chips = await texts(`${P} .pj-ph-ribbon .pj-ph-chip`);
        check('library: tags and genres are offered', ['Tout', 'mer', 'voyage', 'Paysage'].every(t => chips.includes(t)), chips);
        check('library: covers for albums Jellyfin gave none', (await page.waitForFunction(() => document.querySelectorAll('.page:not(.hide) .pj-ph-frame img').length === 3, null, { timeout: 8000 }).then(() => true, () => false)));
        check('library: the app bar\'s stock row is gone, the views are offered', !(await page.locator('[aria-controls="library-view-menu"]').isVisible()) && (await texts(`${P} .pj-ph-views a`)).length === 3);
        await shot('P1-library-1440');
        await page.locator(`${P} .pj-ph-ribbon .pj-ph-chip`, { hasText: /^mer$/ }).click();
        await page.waitForTimeout(1500);
        check('library: a tag finds albums at any depth', (await texts(`${P} .pj-ph-card strong`)).join() === 'Bretagne', await texts(`${P} .pj-ph-card strong`));
        await page.locator(`${P} .pj-ph-ribbon .pj-ph-chip`, { hasText: /^Tout$/ }).click();
        await page.fill(`${P} .pj-ph-search`, 'phare'); await page.waitForTimeout(1800);
        check('library: the search finds a photo', (await texts(`${P} .pj-ph-tile .pj-ph-cap`)).includes('Le phare au couchant'), await texts(`${P} .pj-ph-tile .pj-ph-cap`));
        await shot('P2-search-1440');
        await page.fill(`${P} .pj-ph-search`, 'zzzz'); await page.waitForTimeout(1500);
        check('library: nothing found says what to do', (await texts(`${P} .pj-ph-note`)).join().includes('Rien ne correspond'));
        await page.fill(`${P} .pj-ph-search`, ''); await page.waitForTimeout(1500);

        // an album holding only a sub-album, with a portrait backdrop
        await page.locator(`${P} .pj-ph-card`, { hasText: 'Voyages' }).click();
        await page.waitForSelector(`${P} .pj-ph-art img`, { timeout: 20000 });
        const shape = () => page.$eval(`${P} .pj-ph-art img`, img => { const box = img.getBoundingClientRect(); return { natural: img.naturalWidth / img.naturalHeight, fit: getComputedStyle(img).objectFit, art: img.parentElement.getBoundingClientRect().width, w: box.width, h: box.height }; });
        let s = await shape();
        check('album: a short description has no « Lire la suite »', !(await page.locator(`${P} .pj-ph-more`).isVisible()));
        check('album: a portrait picture is shown whole, centred', s.natural < 0.8 && s.fit === 'contain' && s.art < 700, s);
        check('album: ambient light beside it', (await page.$$(`${P} .pj-aff-fill`)).length === 2);
        check('album: sheet and sub-album', (await page.textContent(`${P} h1`)) === 'Voyages' && (await texts(`${P} .pj-ph-card strong`)).join() === 'Bretagne');
        await shot('P3-album-portrait-1440');
        await page.locator(`${P} .pj-ph-card`, { hasText: 'Bretagne' }).click();
        await page.waitForFunction(() => document.querySelector('.page:not(.hide) .pj-ph h1')?.textContent === 'Bretagne' && document.querySelector('.page:not(.hide) .pj-ph-art img'), null, { timeout: 20000 });
        s = await shape();
        check('album: a landscape picture takes the width', s.natural > 1.7 && s.art > 800, s);
        const meta = await page.textContent(`${P} .pj-ph-under .pj-ph-meta`);
        check('album: date, counts, rating and studio', ['14 juillet 2025', '1 sous-album', '5 photos', '2 vidéos', '8,4', 'Atelier Démo'].every(t => meta.includes(t)), meta);
        check('album: the path goes back to the library', (await texts(`${P} .pj-ph-crumbs a`)).join() === `${LIBRARY},Voyages`, await texts(`${P} .pj-ph-crumbs a`));
        check('album: photos at their own ratios', await page.$$eval(`${P} .pj-ph-tile`, all => { const r = all.map(e => e.getBoundingClientRect()).map(b => b.width / b.height); return Math.min(...r) < 0.85 && Math.max(...r) > 1.5; }));
        const folded = () => page.$eval(`${P} .pj-ph-overview`, e => { const t = document.querySelector('.page:not(.hide) .pj-ph h1').getBoundingClientRect(), b = e.getBoundingClientRect(); return { height: e.clientHeight, full: e.scrollHeight, line: parseFloat(getComputedStyle(e).lineHeight), left: b.left, top: b.top, titleLeft: t.left, titleBottom: t.bottom }; });
        const text = await folded();
        check('album: the description is under the title, on its left edge', Math.abs(text.left - text.titleLeft) < 2 && text.top > text.titleBottom, text);
        check('album: a long description is folded to four lines', text.height <= text.line * 4 + 2 && text.full > text.height + text.line, text);
        await shot('P4-album-1440');
        await page.click(`${P} .pj-ph-more`);
        const unfolded = await folded();
        check('album: « Lire la suite » shows it whole', unfolded.height > text.height * 2 && (await page.textContent(`${P} .pj-ph-more`)) === 'Réduire', unfolded);
        await shot('P4b-album-open-1440');
        await page.click(`${P} .pj-ph-more`);
        check('album: « Réduire » folds it again', (await folded()).height === text.height);
        await page.setViewportSize({ width: 1920, height: 1080 }); await shot('P5-album-1920');
        await page.setViewportSize({ width: 1440, height: 900 });

        check('album: the section names both', (await texts(`${P} .pj-ph-section h2`)).includes('Photos et vidéos'), await texts(`${P} .pj-ph-section h2`));
        check('album: a video tile shows its length', (await texts(`${P} .pj-ph-video .pj-ph-length`)).join() === '0:03,0:03', await texts(`${P} .pj-ph-video .pj-ph-length`));
        await page.locator(`${P} .pj-ph-video`, { hasText: 'Le tour du phare' }).click();
        const playable = await page.waitForFunction(() => document.querySelector('.pj-ph-viewer video')?.readyState >= 2, null, { timeout: 15000 }).then(() => true, () => false);
        check('viewer: a video plays in it', playable);
        await page.waitForTimeout(900);
        const facts = await page.textContent('.pj-ph-panel');
        check('viewer: the column of a video has its length and the source\'s words', ['Le tour du phare', '0:03', 'film', 'clip.mp4'].every(t => facts.includes(t)), facts);
        await page.screenshot({ path: `${out}/P6b-video-1440.png` });
        await page.evaluate(() => document.activeElement.blur());
        await page.keyboard.press('Space'); await page.waitForTimeout(500);
        check('viewer: space plays the video', await page.$eval('.pj-ph-viewer video', v => !v.paused));
        await page.keyboard.press('Space'); await page.waitForTimeout(300);
        check('viewer: space pauses it', await page.$eval('.pj-ph-viewer video', v => v.paused));
        const onVideo = await page.textContent('.pj-ph-counter');
        await page.click('.pj-ph-viewer button[aria-label="Diaporama"]');
        const moved = await page.waitForFunction(was => document.querySelector('.pj-ph-counter').textContent !== was, onVideo, { timeout: 9000 }).then(() => true, () => false);
        check('viewer: the slideshow moves on when the video ends', moved);
        await page.keyboard.press('Escape'); await page.waitForTimeout(300);
        await page.locator(`${P} .pj-ph-video`, { hasText: 'vieux' }).click();
        const told = await page.waitForSelector('.pj-ph-viewer .pj-ph-unplayable a', { timeout: 15000 }).then(() => true, () => false);
        check('viewer: a format the browser cannot read sends to Jellyfin\'s player', told);
        await page.screenshot({ path: `${out}/P6c-unplayable-1440.png` });
        await page.keyboard.press('Escape'); await page.waitForTimeout(300);

        const favourite = page.locator(`${P} .pj-ph-actions button[aria-label="Favori"]`);
        await favourite.click(); await page.waitForTimeout(1200);
        const item = (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum,Folder&searchTerm=Bretagne`)).Items[0];
        check('album: the favourite button is stored', item?.UserData?.IsFavorite === true, item?.UserData);
        await favourite.click(); await page.waitForTimeout(800);

        await page.locator(`${P} .pj-ph-tile`, { hasText: 'Le phare au couchant' }).click();
        await page.waitForSelector('.pj-ph-viewer .pj-ph-stage img');
        await page.waitForTimeout(1500);
        const panel = await page.textContent('.pj-ph-panel');
        check('viewer: the column says what is known of the photo', ['Le phare au couchant', 'Au pied de la falaise.', '15 juillet 2025', 'phare', 'Paysage', 'phare.jpg', '1200 × 800'].every(t => panel.includes(t)) && !panel.includes('/media/'), panel);
        await shot('P6-viewer-1440');
        const first = await page.textContent('.pj-ph-counter');
        await page.keyboard.press('ArrowRight'); await page.waitForTimeout(500);
        const second = await page.textContent('.pj-ph-counter');
        await page.keyboard.press('ArrowLeft'); await page.waitForTimeout(500);
        check('viewer: arrows move and come back', first !== second && (await page.textContent('.pj-ph-counter')) === first, [first, second]);
        await page.click('.pj-ph-viewer button[aria-label="Diaporama"]'); await page.waitForTimeout(5600);
        check('viewer: the slideshow moves on', (await page.textContent('.pj-ph-counter')) !== first);
        await page.keyboard.press('Escape'); await page.waitForTimeout(400);
        check('viewer: Escape closes it, the page stays', (await page.$$('.pj-ph-viewer')).length === 0 && (await page.textContent(`${P} h1`)) === 'Bretagne');

        await page.goBack(); await page.waitForTimeout(2500);
        check('back: the parent album again', (await page.textContent(`${P} h1`)) === 'Voyages');

        const films = (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum,Folder&searchTerm=Films`)).Items[0];
        await page.goto(`${url}/web/#/list?parentId=${films?.Id}&serverId=${login.ServerId}`);
        await page.waitForSelector(`${P} .pj-ph-video`, { timeout: 20000 });
        await page.waitForTimeout(2500);
        const videoMeta = await page.textContent(`${P} .pj-ph-under .pj-ph-meta`);
        check('an album of videos only: counted as videos', films?.Type === 'Folder' && videoMeta.includes('2 vidéos') && !videoMeta.includes('photo') && (await texts(`${P} .pj-ph-section h2`)).join() === 'Vidéos', [films?.Type, videoMeta]);
        check('an album of videos only: a frame heads it', (await page.$$(`${P} .pj-ph-art img`)).length === 1);
        await page.screenshot({ path: `${out}/P6d-videos-album-1440.png` });

        const big = (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum&searchTerm=Grande`)).Items[0];
        await page.goto(`${url}/web/#/list?parentId=${big.Id}&serverId=${login.ServerId}`);
        // jellyfin-web uses the same page again: wait for this album, not the tiles of the one just left
        await page.waitForFunction(() => document.querySelector('.page:not(.hide) .pj-ph h1')?.textContent === 'Grande série' && document.querySelector('.page:not(.hide) .pj-ph-tile'), null, { timeout: 20000 });
        const before =await page.$$eval(`${P} .pj-ph-tile`, a => a.length);
        for (let i = 0; i < 6; i++) { await page.mouse.wheel(0, 4000); await page.waitForTimeout(700); }
        const after = await page.$$eval(`${P} .pj-ph-tile`, a => a.length);
        check('a large album comes a page at a time', before === 120 && after === 150, [before, after]);

        await page.setViewportSize({ width: 412, height: 880 });
        const sea = (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum&searchTerm=Bretagne`)).Items[0];
        await page.goto(`${url}/web/#/list?parentId=${sea.Id}&serverId=${login.ServerId}`);
        await page.waitForSelector(`${P} .pj-ph-art img`, { timeout: 20000 });
        check('phone: the album fits', (await fits()).length === 0, await fits());
        await shot('P7-album-mobile');
        await page.locator(`${P} .pj-ph-tile`).first().click(); await page.waitForSelector('.pj-ph-viewer .pj-ph-stage img');
        check('phone: the viewer fits, picture and column', await page.$$eval('.pj-ph-viewer *', all => all.filter(e => e.offsetParent && e.getBoundingClientRect().right > innerWidth + 1).length) === 0);
        await shot('P8-viewer-mobile');
        await page.keyboard.press('Escape');
        await page.goto(`${url}/web/#/homevideos?topParentId=${lib.Id}`);
        await page.waitForSelector(`${P} .pj-ph-card`, { timeout: 20000 });
        check('phone: the library fits', (await fits()).length === 0, await fits());
        await shot('P9-library-mobile');
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto(`${url}/web/#/homevideos?topParentId=${lib.Id}&tab=1`); await page.waitForTimeout(3000);
        check('another view: the app bar row is back', await page.locator('[aria-controls="library-view-menu"]').isVisible());
        check('the other views of the library stay jellyfin-web\'s', (await page.$$(`${P}`)).length === 0 && (await page.$$('.page:not(.hide) .card')).length > 0);
        check('no script error nor warning', errors.length === 0 && warnings.length === 0, [errors, warnings]);
    } finally {
        await browser.close();
    }
} finally {
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, saved);
    await api('POST', '/System/Configuration/branding', branding);
    try { await api('DELETE', `/CustomMetadata/Local/${DATA_ID}`); } catch { /* nothing stored */ }
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); } catch { /* not created */ }
    fs.rmSync(ROOT, { recursive: true, force: true });
    fs.rmSync(art, { recursive: true, force: true });
}
console.log(`\n${passed} PASS, ${failed} FAIL`);
process.exit(failed ? 1 : 0);
