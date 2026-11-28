// The « Photos » view of the People screen's « Distribution » tab, on a real Jellyfin: browsing the albums, linking people to
// albums and to several photos at once (folders and files WITHOUT any identifier included), saving, applying, and what the
// server then answers for each element.
// node people-photos-real.mjs <url> <out_dir> <host_media_dir>
// <host_media_dir> is the folder the container mounts as /media. Own media (ffmpeg), library and people; the configuration,
// the people directory and the People screen's casts are put back at the end.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [url = 'http://localhost:8097', out = '/tmp/people-photos-shots', media = path.join(process.env.HOME, '.cache/cm-ui-test/media')] = process.argv.slice(2);
const AUTH = 'MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-people-photos", Version="1.0"';
const LIBRARY = 'Albums à relier (test)';
const ROOT = path.join(media, 'people-photos-test');
const P = '#CustomMetadataPeoplePage';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const check = (label, ok, detail) => { if (ok) { passed++; console.log('PASS', label); } else { failed++; console.log('FAIL', label, detail === undefined ? '' : JSON.stringify(detail)); } };

fs.mkdirSync(out, { recursive: true });
const ffmpeg = (file, ...args) => { fs.mkdirSync(path.dirname(file), { recursive: true }); execFileSync('ffmpeg', ['-loglevel', 'error', '-y', ...args, file]); };
const picture = (file, hue) => ffmpeg(file, '-f', 'lavfi', '-i', `gradients=s=900x600:c0=0x${hue}:c1=0x101820:n=2:x0=700:y0=80:x1=120:y1=560`, '-frames:v', '1');
const video = file => ffmpeg(file, '-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=3', '-c:v', 'libx264', '-pix_fmt', 'yuv420p');
fs.rmSync(ROOT, { recursive: true, force: true });
const TOP = 'Vacances 2025', SEA = 'Bretagne', ISLAND = 'Corse {ph-co}', PARTY = 'Anniversaire de Chloé', LONG = 'Randonnées dans le Vercors, saison d’automne';
['4fa8b8', '3f9f7f', '4a7fb5', '6a5fb5', 'a85f9f'].forEach((h, i) => picture(`${ROOT}/${TOP}/${SEA}/IMG_210${i}.jpg`, h));
video(`${ROOT}/${TOP}/${SEA}/phare.mp4`);
['c0703f', 'b5563f'].forEach((h, i) => picture(`${ROOT}/${TOP}/${ISLAND}/IMG_220${i}.jpg`, h));
['c0508f', '8f50c0', '5060c0'].forEach((h, i) => picture(`${ROOT}/${PARTY}/IMG_230${i}.jpg`, h));
picture(`${ROOT}/${LONG}/IMG_2400.jpg`, '6fb54a');
// more than one page of files
for (let i = 0; i < 125; i++) picture(`${ROOT}/Grand album/IMG_${3000 + i}.jpg`, (0x305070 + i * 977).toString(16));
picture(`${ROOT}/à la racine.jpg`, '808080');

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

const plugin = (await api('GET', '/Plugins')).find(p => /custom ?metadata/i.test(p.Name));
const saved = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
const directory = await api('GET', '/CustomMetadata/People');
const casts = await api('GET', '/CustomMetadata/People/EpisodeCast');
const names = ['Alice Martin', 'Bastien Roy', 'Chloé Petit', 'David Leroy', 'Emma Faure'];
const ids = ['pal001', 'pba002', 'pch003', 'pda004', 'pem005'];
try {
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...structuredClone(saved), EnableCasting: true, EnableMetadata: true });
    await api('PUT', '/CustomMetadata/People', { Version: 1, People: names.map((Name, i) => ({ Id: ids[i], Name })) });
    await api('PUT', '/CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [] });
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); await idle(); } catch { /* not there */ }
    await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&collectionType=homevideos&refreshLibrary=true`, { LibraryOptions: { EnablePhotos: true, EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/people-photos-test' }] } });
    await idle();
    const lib = (await api('GET', `/UserViews?userId=${login.User.Id}`)).Items.find(v => v.Name === LIBRARY);
    const list = (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum,Folder,Photo,Video&Fields=Path`)).Items;
    const at = end => list.find(i => (i.Path || '').endsWith(end)) || {};
    const linked = async end => (await api('GET', `/CustomMetadata/AlbumCast/Item/${at(end).Id}`)).map(m => m.Name.split(' ')[0]).sort().join();
    const jellyfin = async end => ((await api('GET', `/Items/${at(end).Id}?userId=${login.User.Id}`)).People || []).map(p => p.Name.split(' ')[0]).sort().join();

    const catalog = (await api('GET', '/CustomMetadata/People/Albums')).filter(a => a.Library === LIBRARY);
    const album = name => catalog.find(a => a.Name.startsWith(name)) || {};
    check('catalog: the albums, their tree and their counts', album(TOP).Albums === 2 && album(SEA).ParentId === album(TOP).Id && album(TOP).Photos === 7 && album(TOP).Videos === 1 && album(SEA).Files === 6, catalog.map(a => [a.Name, a.Albums, a.Photos, a.Videos, a.Files]));
    check('catalog: a key for a folder without identifier, the tag otherwise', /^jf-[0-9a-f]{32}$/.test(album(SEA).Key) && album('Corse').Key === 'ph-co', [album(SEA).Key, album('Corse').Key]);
    const root = catalog.find(a => a.IsRoot);
    check('catalog: the files loose at the root of the library', root && root.Files === 1 && !root.Key, root);
    const page2 = await api('GET', `/CustomMetadata/People/Albums/${album('Grand album').Id}/Files?start=120&limit=120`);
    check('catalog: files a page at a time', page2.Total === 125 && page2.Files.length === 5, [page2.Total, page2.Files.length]);

    const browser = await chromium.launch({ channel: 'chrome' });
    const errors = [];
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
        page.on('pageerror', e => errors.push(e.message));
        const shot = async name => {
            await page.waitForTimeout(500);
            await page.screenshot({ path: `${out}/${name}.png` });
            const wide = await page.$$eval(`${P} #ppTabEpisodes *, ${P} .pp-bottom *`, all => all.filter(e => e.offsetParent && !e.closest('.pp-ac-list') && e.getBoundingClientRect().right > innerWidth + 1).slice(0, 3).map(e => [e.className, Math.round(e.getBoundingClientRect().left), Math.round(e.getBoundingClientRect().right), innerWidth]));
            check(`${name}: nothing wider than the screen`, wide.length === 0, wide);
        };
        const card = name => page.locator(`${P} #ppPhotos .pp-card`, { has: page.locator('strong', { hasText: name }) });
        const file = name => page.locator(`${P} #ppPhotos .pp-file[aria-label="${name}"]`);
        const add = async (field, name) => { await page.fill(`${P} ${field}`, name); await page.keyboard.press('Enter'); await page.waitForTimeout(300); await page.keyboard.press('Escape'); };
        const save = async () => { await page.click(`${P} #ppSave`); await page.waitForSelector(`${P} #ppApply:not([hidden])`, { timeout: 15000 }); };
        const apply = async () => { await page.click(`${P} #ppApplyBtn`); await page.waitForTimeout(5000); };

        await page.goto(`${url}/web/`);
        await page.fill('#txtManualName', 'admin'); await page.fill('#txtManualPassword', 'admin'); await page.keyboard.press('Enter');
        await page.waitForFunction(() => !location.hash.includes('login'), null, { timeout: 20000 });
        await page.goto(`${url}/web/#/configurationpage?name=CustomMetadataPeople`);
        await page.waitForSelector(`${P} [data-pp-tab="episodes"]`, { timeout: 20000 });
        await page.waitForTimeout(1500);
        await page.click(`${P} [data-pp-tab="episodes"]`);
        await page.click(`${P} [data-pp-kind="photos"]`);
        await card(TOP).waitFor({ timeout: 20000 });
        await page.waitForTimeout(1500);
        check('top: the albums without their sub-albums', (await card(SEA).count()) === 0 && (await card(PARTY).count()) === 1);
        await shot('1-albums-1440');

        await card(TOP).click(); await card(SEA).waitFor();
        await page.waitForTimeout(1200);
        check('album: a tagged sub-album shows its title, without the tag', (await card('Corse').locator('strong').innerText()) === 'Corse');
        await shot('1b-album-with-sub-albums-1440');
        await card(SEA).click();
        await file('IMG_2100.jpg').waitFor();
        check('album: breadcrumb', (await page.locator(`${P} .pp-crumb`).innerText()).replace(/\s+/g, ' ').includes(`Albums › ${TOP} › ${SEA}`));
        await add('#ppPhAddAlbum', 'Bastien');
        await page.click(`${P} #ppPhotos .pp-crumb [data-ph-open="${album(TOP).Id}"]`);
        await add('#ppPhAddAlbum', 'Alice');
        await page.locator(`${P} #ppPhCascade`).check();
        await card(SEA).click();
        await file('IMG_2100.jpg').waitFor();
        check('album: its own person, and the one given by the album above (read only)', (await page.locator(`${P} .pp-panel .pp-pchip:not(.ro)`).count()) === 1 && (await page.locator(`${P} .pp-panel .pp-pchip.ro`).innerText()).includes(`vient de « ${TOP} »`));

        await file('IMG_2101.jpg').click(); await file('IMG_2103.jpg').click();
        check('selection: the bar counts it', (await page.locator(`${P} #ppPhDock strong`).first().innerText()).includes('2 photos sélectionnées'));
        await add('#ppPhAddSel', 'Chlo');
        await file('IMG_2103.jpg').click();
        await add('#ppPhAddSel', 'Emma');
        await page.locator(`${P} #ppPhDock [data-ph-edit="1"]`).click();
        await page.fill(`${P} #ppPhDock [data-ph-role]`, 'Au barbecue');
        await page.selectOption(`${P} #ppPhDock [data-ph-type]`, 'GuestStar');
        await shot('2-one-photo-edit-1440');
        await file('IMG_2103.jpg').click();
        check('selection: how many of the selected files hold each person', (await page.locator(`${P} #ppPhDock .pp-pchips`).innerText()).includes('1 sur 2'));
        await file('phare.mp4').click({ modifiers: ['Shift'] });
        check('selection: Shift takes the range', (await page.locator(`${P} .pp-file[aria-pressed="true"]`).count()) >= 3);
        await shot('3-selection-1440');
        await page.setViewportSize({ width: 1920, height: 1080 }); await shot('4-selection-1920');
        await page.setViewportSize({ width: 412, height: 880 });
        await page.locator(`${P} .pp-wall`).scrollIntoViewIfNeeded(); await shot('5-selection-mobile');
        await page.click(`${P} [data-ph-clear]`);
        await page.setViewportSize({ width: 1440, height: 900 });
        await file('phare.mp4').click(); await add('#ppPhAddSel', 'David'); await page.click(`${P} [data-ph-clear]`);

        await save();
        await page.waitForTimeout(500);
        check('notice: in view after saving from down an album', await page.$eval(`${P} #ppApply`, e => { const r = e.getBoundingClientRect(); return r.top >= 40 && r.bottom <= innerHeight; }));
        check('notice: worded for photos', (await page.locator(`${P} #ppApplyBtn`).innerText()).includes('Appliquer aux photos'), await page.locator(`${P} #ppApplyBtn`).innerText());
        const stored = (await api('GET', '/CustomMetadata/People/EpisodeCast')).Episodes;
        check('saved: albums and files, by keys without identifier', stored.filter(e => e.Type === 'album').length === 2 && stored.filter(e => e.Type === 'media').length >= 3 && stored.every(e => e.Key.startsWith('jf-')), stored.map(e => [e.Type, e.Key.slice(0, 6), e.Cast.length]));
        check('saved: the role and the job', stored.some(e => e.Cast.some(c => c.Role === 'Au barbecue' && c.Type === 'GuestStar')));
        await apply();
        check('server: the album and the one given from above', (await linked(`/${SEA}`)) === 'Alice,Bastien', await linked(`/${SEA}`));
        check('server: a photo gets its own and the whole-content ones', (await linked('IMG_2101.jpg')) === 'Alice,Chloé,Emma', await linked('IMG_2101.jpg'));
        check('server: two people on one photo', (await linked('IMG_2103.jpg')) === 'Alice,Chloé', await linked('IMG_2103.jpg'));
        check('server: a photo of a sibling album', (await linked('IMG_2200.jpg')) === 'Alice', await linked('IMG_2200.jpg'));
        check('server: the video, also in Jellyfin\'s cast', (await linked('phare.mp4')) === 'Alice,David' && (await jellyfin('phare.mp4')) === 'Alice,David', [await linked('phare.mp4'), await jellyfin('phare.mp4')]);
        check('server: another album untouched', (await linked('IMG_2300.jpg')) === '');

        await page.click(`${P} #ppPhotos .pp-crumb [data-ph-open=""]`);
        await page.waitForTimeout(1500);
        check('top: an album whose people are only inside says so', (await card('Grand album').innerText()).includes('Aucune personne'));
        await page.click(`${P} [data-ph-filter="with"]`);
        check('filter: with people, at every depth', (await card(SEA).count()) === 1 && (await card(PARTY).count()) === 0);
        await page.click(`${P} [data-ph-filter="all"]`);
        await page.fill(`${P} #ppEpSearch`, 'bret');
        check('search: finds a sub-album and says where it is', (await card(SEA).innerText()).includes(`dans ${TOP}`));
        await page.fill(`${P} #ppEpSearch`, 'rien du tout');
        check('search: says when nothing matches', (await page.locator(`${P} #ppPhotos .pp-empty`).innerText()).includes('Aucun album ne correspond'));
        await page.fill(`${P} #ppEpSearch`, '');

        // paging, then the library's loose files
        await card('Grand album').click(); await page.locator(`${P} [data-ph-more]`).waitFor();
        check('big album: 120 files first', (await page.locator(`${P} .pp-file`).count()) === 120);
        await page.click(`${P} [data-ph-more]`); await page.waitForTimeout(1500);
        check('big album: then the rest', (await page.locator(`${P} .pp-file`).count()) === 125);
        await page.click(`${P} #ppPhotos .pp-crumb [data-ph-open=""]`);
        await card('Photos à la racine').click(); await file('à la racine.jpg').waitFor();
        check('library root: no people of its own', (await page.locator(`${P} .pp-panel`).count()) === 0);
        await shot('6-root-files-1440');

        // taking back: the cascade off, then a person removed from the selection
        await page.click(`${P} #ppPhotos .pp-crumb [data-ph-open=""]`);
        await card(TOP).click();
        await page.locator(`${P} #ppPhCascade`).uncheck();
        await card(SEA).click(); await file('IMG_2101.jpg').waitFor();
        await file('IMG_2101.jpg').click(); await file('IMG_2103.jpg').click();
        await page.locator(`${P} #ppPhDock .pp-pchip`, { hasText: 'Chloé' }).locator('[data-ph-removeall]').click();
        await save(); await apply();
        check('taking back: whole content off', (await linked('IMG_2200.jpg')) === '' && (await linked(`/${TOP}`)) === 'Alice', [await linked('IMG_2200.jpg'), await linked(`/${TOP}`)]);
        check('taking back: a person removed from several photos', (await linked('IMG_2101.jpg')) === 'Emma' && (await linked('IMG_2103.jpg')) === '', [await linked('IMG_2101.jpg'), await linked('IMG_2103.jpg')]);
        check('taking back: Jellyfin\'s cast of the video', (await jellyfin('phare.mp4')) === 'David', await jellyfin('phare.mp4'));

        // by identifier: the element linked above is not listed as a raw key, a typed one still is
        await page.click(`${P} [data-pp-kind="ids"]`);
        await page.selectOption(`${P} #ppIdType`, 'album'); await page.fill(`${P} #ppIdKey`, '{ph-co}'); await page.click(`${P} #ppIdAdd`);
        await page.waitForSelector(`${P} #ppCastAdd`);
        const idText = await page.locator(`${P} #ppEpDetail`).innerText();
        check('by identifier: the typed album under its title, no internal key', idText.includes('Corse') && !/jf-[0-9a-f]{8}/.test(idText), idText.slice(0, 400));
        await shot('7-by-identifier-1440');
        await page.click(`${P} [data-pp-kind="series"]`);
        check('series: still there', (await page.locator(`${P} [data-library-load]`).count()) === 1);
        await shot('8-series-1440');
        await page.click(`${P} [data-pp-kind="photos"]`);
        await page.setViewportSize({ width: 412, height: 880 }); await page.click(`${P} #ppPhotos .pp-crumb [data-ph-open=""]`); await card(TOP).waitFor(); await page.waitForTimeout(1200);
        await shot('9-albums-mobile');
        await page.setViewportSize({ width: 1920, height: 1080 }); await page.waitForTimeout(800); await shot('10-albums-1920');
        check('no script error', errors.length === 0, errors);
    } finally {
        await browser.close();
    }
} finally {
    await api('PUT', '/CustomMetadata/People', directory);
    await api('PUT', '/CustomMetadata/People/EpisodeCast', casts);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, saved);
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); } catch { /* not created */ }
    fs.rmSync(ROOT, { recursive: true, force: true });
}
console.log(`\n${passed} PASS, ${failed} FAIL`);
process.exit(failed ? 1 : 0);
