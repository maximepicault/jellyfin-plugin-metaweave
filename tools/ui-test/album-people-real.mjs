// People linked to photo albums, photos and videos, on a real Jellyfin: from an API source, a local source and the People
// screen, inherited from the albums above, Jellyfin's own cast for videos, visibility, the theme's screens and the editors.
// node album-people-real.mjs <url> <out_dir> <host_media_dir>
// <host_media_dir> is the folder the container mounts as /media. Own media (ffmpeg), fake API (port 8768), library and
// sources; the configuration, the people directory, the People screen's casts and the Custom CSS are put back at the end.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const [url = 'http://localhost:8097', out = '/tmp/album-people-shots', media = path.join(process.env.HOME, '.cache/cm-ui-test/media')] = process.argv.slice(2);
const API_HOST = '172.17.0.1', API_PORT = 8768;
const AUTH = 'MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-album-people", Version="1.0"';
const LIBRARY = 'Personnes en photo (test)';
const ROOT = path.join(media, 'album-people-test');
const DATA_ID = 'c1b2c3d4e5f60720';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const check = (label, ok, detail) => { if (ok) { passed++; console.log('PASS', label); } else { failed++; console.log('FAIL', label, detail === undefined ? '' : JSON.stringify(detail)); } };

const ffmpeg = (file, ...args) => { fs.mkdirSync(path.dirname(file), { recursive: true }); execFileSync('ffmpeg', ['-loglevel', 'error', '-y', ...args, file]); };
const picture = (file, color) => ffmpeg(file, '-f', 'lavfi', '-i', `color=c=${color}:s=900x600`, '-frames:v', '1');
const video = file => ffmpeg(file, '-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=3', '-c:v', 'libx264', '-pix_fmt', 'yuv420p');
fs.rmSync(ROOT, { recursive: true, force: true });
const TOP = 'Voyage {al-v}', SEA = 'Bretagne {al-b}', PARTY = 'Fête {lt-fete}', NIGHT = 'Soir {lt-soir}';
picture(`${ROOT}/${TOP}/${SEA}/phare.jpg`, 'teal'); picture(`${ROOT}/${TOP}/${SEA}/seul.jpg`, 'olive'); picture(`${ROOT}/${TOP}/${SEA}/plage {al-p1}.jpg`, 'maroon'); video(`${ROOT}/${TOP}/${SEA}/clip.mp4`);
picture(`${ROOT}/${PARTY}/${NIGHT}/danse.jpg`, 'purple'); picture(`${ROOT}/${PARTY}/${NIGHT}/autre.jpg`, 'gray'); video(`${ROOT}/${PARTY}/${NIGHT}/bal.mp4`);

const server = http.createServer((req, res) => {
    const p = decodeURIComponent(req.url.split('?')[0]);
    const send = body => { res.writeHead(body ? 200 : 404, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body || {})); };
    if (p === '/albums/v') return send({ title: 'Grand voyage', cast: [{ name: 'Ana Test' }] });
    if (p === '/albums/b') return send({ title: 'La Bretagne', cast: [{ name: 'Bo Test', role: 'Barreur' }], photos: [{ file: 'phare.jpg', title: 'Le phare', cast: [{ name: 'Cy Test', role: 'Guide' }] }, { file: 'clip.mp4', title: 'Le clip', cast: ['Dee Test'] }] });
    send(null);
});
await new Promise(r => server.listen(API_PORT, API_HOST, r));

const login = await (await fetch(`${url}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `${AUTH}, Token="${login.AccessToken}"` };
async function api(method, route, body, headers = H) {
    const r = await fetch(url + route, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await r.text();
    if (!r.ok) { const e = new Error(`${method} ${route} → ${r.status} ${text.slice(0, 300)}`); e.status = r.status; throw e; }
    return text ? JSON.parse(text) : null;
}
async function idle() {
    await sleep(4000);
    for (let i = 0; i < 150; i++) {
        if (!(await api('GET', '/ScheduledTasks?isHidden=false')).some(t => t.State === 'Running')) { await sleep(5000); return; }
        await sleep(2000);
    }
}
async function refreshDone() { for (let i = 0; i < 90; i++) { await sleep(1500); const st = await api('GET', '/CustomMetadata/Refresh/Status'); if (!st.Running) return st; } throw new Error('refresh never ended'); }

const plugin = (await api('GET', '/Plugins')).find(p => /custom ?metadata/i.test(p.Name));
const saved = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
const branding = await api('GET', '/System/Configuration/branding');
const directory = await api('GET', '/CustomMetadata/People');
const casts = await api('GET', '/CustomMetadata/People/EpisodeCast');
const person = (id, name) => ({ Id: id, Name: name });
const entry = (id, extra = {}) => ({ PersonId: id, Role: '', Type: 'Actor', ...extra });
const content = { Series: [], Albums: [{ Id: 'fete', Title: 'La fête', Cast: [entry('plu001', { All: true }), entry('pmo001')], Albums: [
    { Id: 'soir', Title: 'Le soir', Cast: [entry('pni001')], Albums: [], Photos: [{ File: 'danse.jpg', Title: 'La danse', Cast: [entry('ppa001', { Role: 'Danse' })] }] }] }] };
let limited;
try {
    const config = structuredClone(saved);
    config.Sources = (config.Sources || []).filter(s => !['al', 'lt'].includes(s.Prefix));
    config.Sources.push(
        { Name: 'Personnes API (test)', Prefix: 'al', SourceType: 'Api', Enabled: true, BaseUrl: `http://${API_HOST}:${API_PORT}`, ContentVideo: false, ContentAlbums: true, MaxRetries: 0, PhotoInheritAlbumCast: true },
        { Name: 'Personnes locales (test)', Prefix: 'lt', SourceType: 'Local', Enabled: true, BaseUrl: 'local://local', LocalDataId: DATA_ID, ContentVideo: false, ContentAlbums: true });
    Object.assign(config, { EnableMetadata: true, EnableImages: true, EnableCache: false, EnableCasting: true, EnableThemeScript: true });
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, config);
    await api('POST', '/System/Configuration/branding', { ...branding, CustomCss: '@import url("../CustomMetadata/Theme/projection.css");' });
    await api('PUT', '/CustomMetadata/People', { Version: 1, People: [person('plu001', 'Lu Test'), person('pmo001', 'Mo Test'), person('pni001', 'Ni Test'), person('ppa001', 'Pa Test'), person('pqi001', 'Qi Test'), person('pzed01', 'Zed Test'), person('pyu001', 'Yu Test')] });
    await api('PUT', '/CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [
        { Type: 'album', Key: 'al-b', Cascade: true, Cast: [entry('pzed01')] }, { Type: 'album', Key: 'lt-soir', Cast: [entry('pqi001')] }, { Type: 'media', Key: 'al-p1', Cast: [entry('pyu001')] }] });
    await api('PUT', `/CustomMetadata/Local/${DATA_ID}`, content);
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); await idle(); } catch { /* not there */ }
    await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&collectionType=homevideos&refreshLibrary=true`, { LibraryOptions: { EnablePhotos: true, EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/album-people-test' }] } });
    await idle();
    const lib = (await api('GET', `/UserViews?userId=${login.User.Id}`)).Items.find(v => v.Name === LIBRARY);
    const items = async () => (await api('GET', `/Items?userId=${login.User.Id}&ParentId=${lib.Id}&Recursive=true&IncludeItemTypes=PhotoAlbum,Folder,Photo,Video&Fields=Path`)).Items;
    const list = await items();
    const at = end => list.find(i => (i.Path || '').endsWith(end)) || {};
    const names = async end => (await api('GET', `/CustomMetadata/AlbumCast/Item/${at(end).Id}`)).map(m => m.Name.replace(' Test', '')).sort().join();
    const expect = async (label, end, wanted) => { const got = await names(end); check(label, got === wanted, got); };
    const native = async end => ((await api('GET', `/Items/${at(end).Id}?userId=${login.User.Id}`)).People || []).map(p => p.Name.replace(' Test', '')).sort().join();

    await expect('API: an album has its own people', TOP, 'Ana');
    await expect('API: a sub-album gets its own and its parent\'s (source option)', SEA, 'Ana,Bo,Zed');
    await expect('API: a listed photo has its people and its albums\'', 'phare.jpg', 'Ana,Bo,Cy,Zed');
    await expect('API: an unlisted photo gets the albums\' people', 'seul.jpg', 'Ana,Bo,Zed');
    await expect('People screen: a photo by its own tag', '{al-p1}.jpg', 'Ana,Bo,Yu,Zed');
    await expect('API: a listed video', 'clip.mp4', 'Ana,Bo,Dee,Zed');
    check('video: they are in Jellyfin\'s own cast too', (await native('clip.mp4')) === 'Ana,Bo,Dee,Zed', await native('clip.mp4'));
    await expect('local: an album has the people typed on it', PARTY, 'Lu,Mo');
    await expect('local: a sub-album: its own, the whole-content ones above, the People screen\'s', NIGHT, 'Lu,Ni,Qi');
    await expect('local: a photo: its own and the whole-content ones only', 'danse.jpg', 'Lu,Pa');
    await expect('local: a photo with nothing typed', 'autre.jpg', 'Lu');
    await expect('local: a video', 'bal.mp4', 'Lu');
    check('local video: Jellyfin\'s cast', (await native('bal.mp4')) === 'Lu', await native('bal.mp4'));
    const roles = await api('GET', `/CustomMetadata/AlbumCast/Item/${at('danse.jpg').Id}`);
    check('the role is kept', roles.find(m => m.Name === 'Pa Test')?.Role === 'Danse', roles);

    const everyone = await api('GET', `/CustomMetadata/AlbumCast/People?parentId=${lib.Id}`);
    check('who appears in the library, with counts', everyone.find(p => p.Name === 'Lu Test')?.Count === 5, everyone.map(p => `${p.Name}:${p.Count}`));
    const ofBo = await api('GET', `/CustomMetadata/AlbumCast/Items?person=${encodeURIComponent('bo test')}`);
    check('the items of a person, any case, albums first', ofBo.length === 5 && ofBo[0] === at(SEA).Id, ofBo);

    // a user without access to the library sees nobody
    limited = await api('POST', '/Users/New', { Name: 'cm-people-limited', Password: 'cm-people-limited' });
    await api('POST', `/Users/${limited.Id}/Policy`, { ...limited.Policy, EnableAllFolders: false, EnabledFolders: [] });
    const other = await (await fetch(`${url}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH.replace('cm-album-people', 'cm-limited') }, body: JSON.stringify({ Username: 'cm-people-limited', Pw: 'cm-people-limited' }) })).json();
    const LH = { 'Content-Type': 'application/json', Authorization: `${AUTH.replace('cm-album-people', 'cm-limited')}, Token="${other.AccessToken}"` };
    const hidden = await api('GET', '/CustomMetadata/AlbumCast/Items?person=Bo%20Test', undefined, LH);
    const none = await api('GET', '/CustomMetadata/AlbumCast/People', undefined, LH);
    let status = 200; try { await api('GET', `/CustomMetadata/AlbumCast/Item/${at('phare.jpg').Id}`, undefined, LH); } catch (e) { status = e.status; }
    check('a user without the library: nothing listed, named or served', hidden.length === 0 && none.length === 0 && status === 404, [hidden, none, status]);
    check('without a signed-in user: refused', (await fetch(`${url}/CustomMetadata/AlbumCast/People`)).status === 401);

    // changes: the People screen's « Appliquer », then a local « Appliquer »
    await api('PUT', '/CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: [{ Type: 'album', Key: 'lt-soir', Cast: [entry('pqi001')] }, { Type: 'media', Key: 'al-p1', Cast: [entry('pyu001')] }] });
    const applied = await api('POST', '/CustomMetadata/People/Apply', { AlbumKeys: ['al-b'], PersonIds: [], EpisodeKeys: [], Prefixes: [] });
    await sleep(5000);
    check('People screen: applying reaches the album and what is below', applied.Queued === 5, applied);
    await expect('People screen: a removed link is gone', 'phare.jpg', 'Ana,Bo,Cy');
    await expect('and from a photo the source answers « unknown » for', '{al-p1}.jpg', 'Ana,Bo,Yu');
    check('and from the video\'s Jellyfin cast', (await native('clip.mp4')) === 'Ana,Bo,Dee', await native('clip.mp4'));
    content.Albums[0].Cast[0].All = false;
    await api('PUT', `/CustomMetadata/Local/${DATA_ID}`, content);
    await api('POST', '/CustomMetadata/Refresh', { Prefix: 'lt', Types: ['album', 'photo'], ReplaceImages: false, Tag: 'lt-fete', IncludeChildren: true });
    await refreshDone();
    await expect('local: unticking « whole content » takes the person back', 'danse.jpg', 'Pa');
    await expect('local: the album keeps them', PARTY, 'Lu,Mo');
    check('local: the video loses them in Jellyfin too', (await native('bal.mp4')) === '', await native('bal.mp4'));

    // ---- what must be left alone -----------------------------------------------------------------------------------
    const base = [{ Type: 'album', Key: 'lt-soir', Cast: [entry('pqi001')] }, { Type: 'media', Key: 'al-p1', Cast: [entry('pyu001')] }];
    const cascading = (key, ...ids) => [...base.filter(e => e.Key !== key), { Type: 'album', Key: key, Cascade: true, Cast: ids.map(id => entry(id)) }];
    const relink = async (episodes, key) => {
        await api('PUT', '/CustomMetadata/People/EpisodeCast', { Version: 1, Episodes: episodes });
        await api('POST', '/CustomMetadata/People/Apply', { AlbumKeys: [key], PersonIds: [], EpisodeKeys: [], Prefixes: [] });
        await sleep(5000);
    };
    const edit = async (end, change) => {
        const dto = await api('GET', `/Items/${at(end).Id}?userId=${login.User.Id}`);
        change(dto);
        await api('POST', `/Items/${at(end).Id}`, dto);
        await sleep(1500);
    };

    await edit('bal.mp4', dto => { dto.People = [{ Name: 'Qi Test', Type: 'Actor' }, { Name: 'Hand Typed', Type: 'Actor' }]; });
    await relink(cascading('lt-soir', 'pqi001', 'pzed01'), 'lt-soir');
    check('video: links are added beside the people typed in Jellyfin', (await native('bal.mp4')) === 'Hand Typed,Qi,Zed', await native('bal.mp4'));
    await relink(base.filter(e => e.Key !== 'lt-soir'), 'lt-soir');
    check('video: removing links never removes a person typed in Jellyfin, even a linked one', (await native('bal.mp4')) === 'Hand Typed,Qi', await native('bal.mp4'));

    const pluginConfig = await api('GET', `/Plugins/${plugin.Id}/Configuration`);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...pluginConfig, EnableCasting: false });
    const off = [await names('phare.jpg'), (await api('GET', '/CustomMetadata/AlbumCast/People')).length, (await api('GET', '/CustomMetadata/AlbumCast/Items?person=Bo%20Test')).length];
    check('casting off: nobody is named, counted or listed', off[0] === '' && off[1] === 0 && off[2] === 0, off);
    await relink(cascading('al-b', 'pzed01'), 'al-b');
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, pluginConfig);
    await expect('casting off: nothing was changed meanwhile', 'seul.jpg', 'Ana,Bo');

    await edit('phare.jpg', dto => { dto.LockData = true; });
    await relink(cascading('al-b', 'pzed01'), 'al-b');
    await expect('a locked photo is not changed', 'phare.jpg', 'Ana,Bo,Cy');
    await expect('its neighbour is', 'seul.jpg', 'Ana,Bo,Zed');
    await edit('phare.jpg', dto => { dto.LockData = false; });
    await relink(base, 'al-b');
    await expect('unlocked again', 'phare.jpg', 'Ana,Bo,Cy');

    // the source stops answering (its album answer is kept 30 s: wait it out)
    server.closeAllConnections();
    await new Promise(r => server.close(r));
    await sleep(31000);
    await relink(base, 'al-b');
    await expect('source down: a listed photo keeps its people', 'phare.jpg', 'Ana,Bo,Cy');
    await expect('source down: the album keeps them', SEA, 'Ana,Bo');
    check('source down: the video keeps them in Jellyfin', (await native('clip.mp4')) === 'Ana,Bo,Dee', await native('clip.mp4'));
    await new Promise(r => server.listen(API_PORT, API_HOST, r));
    await sleep(31000);
    await relink(base, 'al-b');
    await expect('source back: the same people', 'phare.jpg', 'Ana,Bo,Cy');

    // ---- screens ---------------------------------------------------------------------------------------------------
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
        const shot = async name => { await page.waitForTimeout(1500); await page.screenshot({ path: `${out}/${name}.png` }); console.log('📸', `${out}/${name}.png`); };
        const chipNames = async selector => (await page.$$eval(`${selector} .pj-ph-person > span:nth-child(2)`, a => a.map(e => e.firstChild.textContent.replace(' Test', '')))).sort().join();

        await page.goto(`${url}/web/#/list?parentId=${at(SEA).Id}&serverId=${login.ServerId}`);
        await page.waitForSelector(`${P} .pj-ph-under .pj-ph-person`, { timeout: 20000 });
        check('album page: its people', (await chipNames(`${P} .pj-ph-under`)) === 'Ana,Bo', await chipNames(`${P} .pj-ph-under`));
        await shot('Q1-album-1440');
        await page.locator(`${P} .pj-ph-tile`, { hasText: 'Le phare' }).click();
        await page.waitForSelector('.pj-ph-panel .pj-ph-person', { timeout: 15000 });
        check('viewer: the photo\'s people in the column', (await chipNames('.pj-ph-panel')) === 'Ana,Bo,Cy', await chipNames('.pj-ph-panel'));
        await shot('Q2-viewer-1440');
        await page.locator('.pj-ph-panel .pj-ph-person', { hasText: 'Bo Test' }).click();
        await page.waitForFunction(() => /avec Bo Test/.test(document.querySelector('.page:not(.hide) .pj-ph-results')?.textContent || ''), null, { timeout: 20000 });
        check('a person chip opens the library on their albums and photos', (await page.$$('.pj-ph-viewer')).length === 0 && (await page.$$(`${P} .pj-ph-card`)).length === 1 && (await page.$$(`${P} .pj-ph-tile`)).length === 4,
            [(await page.$$(`${P} .pj-ph-card`)).length, (await page.$$(`${P} .pj-ph-tile`)).length]);
        check('library: the chosen person is marked', (await page.$$eval(`${P} .pj-ph-peoplebar .pj-ph-on-chip`, a => a.map(e => e.textContent))).join().includes('Bo Test'));
        await shot('Q3-library-person-1440');
        await page.locator(`${P} .pj-ph-peoplebar .pj-ph-person`, { hasText: 'Pa Test' }).click();
        await page.waitForFunction(() => /avec Pa Test/.test(document.querySelector('.page:not(.hide) .pj-ph-results')?.textContent || ''), null, { timeout: 20000 });
        check('library: another person, from the bar', (await page.$$(`${P} .pj-ph-tile`)).length === 1);
        await page.locator(`${P} .pj-ph-ribbon .pj-ph-chip`, { hasText: /^Tout$/ }).click();
        await page.waitForTimeout(1500);
        check('library: « Tout » leaves the person', (await page.$$(`${P} .pj-ph-card`)).length === 2);

        const dee = await api('GET', `/Persons/${encodeURIComponent('Dee Test')}?userId=${login.User.Id}`);
        await page.goto(`${url}/web/#/details?id=${dee.Id}&serverId=${login.ServerId}`);
        const section = await page.waitForSelector('.page:not(.hide) .pj-ph-appears .pj-ph-tile', { timeout: 20000 }).then(() => true, () => false);
        check('person page: their album media are listed', section && (await page.$$('.page:not(.hide) .pj-ph-appears .pj-ph-tile')).length === 1);
        await shot('Q4-person-page-1440');
        await page.setViewportSize({ width: 412, height: 880 });
        await page.goto(`${url}/web/#/list?parentId=${at(SEA).Id}&serverId=${login.ServerId}`);
        await page.waitForSelector(`${P} .pj-ph-under .pj-ph-person`, { timeout: 20000 });
        check('phone: the people fit', await page.$$eval(`${P} .pj-ph-under *`, a => a.every(e => !e.offsetParent || e.getBoundingClientRect().right <= innerWidth + 1)));
        await shot('Q5-album-mobile');
        await page.setViewportSize({ width: 1440, height: 900 });

        const root = '#CustomMetadataConfigPage';
        await page.goto(url + '/web/#/dashboard'); await page.waitForTimeout(800);
        await page.goto(url + '/web/#/configurationpage?name=Custom%20Metadata%20Provider');
        await page.locator(`${root} article.cm-card`, { hasText: 'Personnes locales (test)' }).getByRole('button', { name: 'Modifier' }).click();
        await page.waitForTimeout(900);
        await page.click(`${root} [data-go="albums"]`); await page.click(`${root} [data-node="a:fete"]`); await page.click(`${root} [data-ltab-btn="cast"]`);
        check('editor: the album has its people', (await page.locator(`${root} .cm-cast-row`).count()) === 2);
        await page.fill(`${root} #lfCastAdd`, 'Zed Test'); await page.keyboard.press('Enter'); await page.waitForTimeout(500);
        await page.locator(`${root} [data-cast-all="2"]`).check();
        const box = await page.$eval(`${root} .cm-cast-all`, label => { const input = label.querySelector('input').getBoundingClientRect(), words = label.querySelector('span').getBoundingClientRect(); return { box: [input.width, input.height], words: [words.width, words.height] }; });
        check('editor: « whole content » is a small box with its words on one line', box.box[0] <= 20 && box.box[1] <= 20 && box.words[1] < 30 && box.words[0] > 150, box);
        await page.locator(`${root} .cm-cast`).scrollIntoViewIfNeeded();
        await shot('Q6-editor-album-1440');
        await page.click(`${root} [data-node="a:soir"]`); await page.click(`${root} [data-ltab-btn="photos"]`);
        await page.click(`${root} [data-photo="0"]`);
        await page.fill(`${root} #lfCastAdd`, 'Yu Test'); await page.keyboard.press('Enter'); await page.waitForTimeout(500);
        await page.locator(`${root} .cm-cast`).scrollIntoViewIfNeeded(); await shot('Q7-editor-photo-1440');
        await page.click(`${root} #btnSaveStay`); await page.waitForTimeout(3000);
        const stored = await api('GET', `/CustomMetadata/Local/${DATA_ID}`);
        check('editor: the album link and its « whole content » are saved', stored.Albums[0].Cast.find(c => c.PersonId === 'pzed01')?.All === true, stored.Albums[0].Cast);
        check('editor: the photo link is saved', stored.Albums[0].Albums[0].Photos[0].Cast.map(c => c.PersonId).sort().join() === 'ppa001,pyu001', stored.Albums[0].Albums[0].Photos[0].Cast);
        const source = (await api('GET', `/Plugins/${plugin.Id}/Configuration`)).Sources.find(s => s.Prefix === 'al');
        check('editor: a saved source keeps the option', source.PhotoInheritAlbumCast === true);

        await page.goto(url + '/web/#/dashboard'); await page.waitForTimeout(800);
        await page.goto(url + '/web/#/configurationpage?name=CustomMetadataPeople');
        const Q = '#CustomMetadataPeoplePage';
        await page.waitForTimeout(2500);
        await page.getByRole('tab', { name: /Distribution/ }).click().catch(async () => page.locator(`${Q} [data-tab="episodes"], ${Q} button:has-text("Distribution")`).first().click());
        await page.waitForTimeout(800);
        await page.locator(`${Q} button:has-text("Par identifiant")`).click().catch(() => {});
        await page.waitForSelector(`${Q} #ppIdType`, { timeout: 10000 });
        await page.selectOption(`${Q} #ppIdType`, 'album'); await page.fill(`${Q} #ppIdKey`, '{al-v}'); await page.click(`${Q} #ppIdAdd`);
        await page.waitForSelector(`${Q} #ppCascade`, { timeout: 10000 });
        await page.fill(`${Q} #ppCastAdd`, 'Qi Test'); await page.keyboard.press('Enter'); await page.waitForTimeout(500);
        // the suggestions stay open on the focused field: leave it first, as one does before ticking the box
        await page.keyboard.press('Escape'); await page.waitForTimeout(300);
        await page.locator(`${Q} #ppCascade`).check(); await page.waitForTimeout(500);
        await shot('Q8-people-album-1440');
        await page.locator(`${Q} button:has-text("Enregistrer")`).filter({ visible: true }).first().click(); await page.waitForTimeout(2500);
        const typed = (await api('GET', '/CustomMetadata/People/EpisodeCast')).Episodes.find(e => e.Type === 'album' && e.Key === 'al-v');
        check('People screen: an album is saved with « whole content »', typed?.Cascade === true && typed.Cast.some(c => c.PersonId === 'pqi001'), typed);
        check('no script error nor warning', errors.length === 0 && warnings.length === 0, [errors, warnings]);
    } finally {
        await browser.close();
    }
} finally {
    if (limited) await api('DELETE', `/Users/${limited.Id}`).catch(() => {});
    await api('PUT', '/CustomMetadata/People', directory);
    await api('PUT', '/CustomMetadata/People/EpisodeCast', casts);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, saved);
    await api('POST', '/System/Configuration/branding', branding);
    try { await api('DELETE', `/CustomMetadata/Local/${DATA_ID}`); } catch { /* nothing stored */ }
    try { await api('DELETE', `/Library/VirtualFolders?name=${encodeURIComponent(LIBRARY)}&refreshLibrary=true`); } catch { /* not created */ }
    fs.rmSync(ROOT, { recursive: true, force: true });
    server.close();
}
console.log(`\n${passed} PASS, ${failed} FAIL`);
process.exit(failed ? 1 : 0);
