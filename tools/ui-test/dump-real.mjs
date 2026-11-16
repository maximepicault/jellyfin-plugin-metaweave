// "Dump" source in the REAL Jellyfin: one call downloads a catalogue, files are matched by tag AND by name, the coverage screen
// shows what is missing. REST checks + the editor and the coverage screen in jellyfin-web driven with Playwright
// (system Google Chrome) + screenshots. Needs the test container of ui-test.sh (whose gen.py makes /media/dump and server.py serves /dump).
// Usage: node dump-real.mjs <base_url> <out_dir> [user] [password]
// Creates a source "dp" and a movie library "Dump" (/media/dump), restores the configuration and removes the library at the end.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const { AxeBuilder } = require('@axe-core/playwright');

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node dump-real.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });

const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const API = 'http://172.17.0.1:8765';
const wait = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${ok || !detail ? '' : ' — ' + detail}`); ok ? pass++ : fail++; };
// camelCase from the plain controllers, PascalCase from others: read both
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-dump", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password })
})).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = text && text[0] !== '<' ? JSON.parse(text) : null; } catch { /* not JSON */ }
  return { status: r.status, json, text };
};
const movies = async () => ((await api('GET', `/Items?IncludeItemTypes=Movie&Recursive=true&Fields=Overview,Studios,ProviderIds,Tags,Genres,Path,CommunityRating,PremiereDate&userId=${auth.User.Id}`)).json?.Items || []);

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const config = JSON.parse(JSON.stringify(original));
config.Sources = (config.Sources || []).filter(s => (s.Prefix || '').toLowerCase() !== 'dp');
const dumpSource = {
  Name: 'Démo dump', Prefix: 'dp', SourceType: 'Dump', BaseUrl: `${API}/dump`, Enabled: true, MaxRetries: 0, TimeoutSeconds: 20,
  VideoEndpoint: '/items/{id}', SourceEndpoint: '', SeriesEndpoint: '', EpisodeListEndpoint: '',
  Dump: { ItemsPath: 'data', IdPath: 'id', NamePath: 'name', SlugPath: 'slug', SearchTitlesPath: 'search_titles', GroupPath: 'brand', LikesPath: 'likes', DislikesPath: 'dislikes',
    CacheMinutes: 60, MatchWithoutTag: true, MatchBySlug: true, MatchByName: true, MinScore: 70, NoiseWords: 'raw vostfr vosta vf vo multi 1080p 720p x264 x265 hevc' },
  VideoFieldMappings: { Title: 'name', Overview: 'description', OverviewIsHtml: true, PremiereDate: 'released_at || created_at', CommunityRating: 'communityRating', Tags: 'tags', Genres: '',
    Studio: 'brand', PosterUrl: 'poster_url', BackdropUrl: 'cover_url', ThumbUrl: 'cover_url', IndexNumber: '', ChaptersArray: '', ImagesArray: '', SeriesName: '' }
};
config.Sources.push(dumpSource);
config.EnableCache = false;
let libraryCreated = false;

try {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

  // ---- 1. the editor's test: does the dump load, which entry does a file name give
  const test = async (fileName, source = dumpSource) => (await api('POST', '/CustomMetadata/Dump/Test', { Source: source, FileName: fileName })).json;
  const t1 = await test('Les Sentiers du Nord.mkv');
  check('test: the dump loads and counts its entries', pick(t1, 'Ok') === true && pick(t1, 'Count') === 40, JSON.stringify(t1)?.slice(0, 300));
  check('test: 4 groups found', pick(t1, 'GroupCount') === 4, String(pick(t1, 'GroupCount')));
  const c1 = pick(t1, 'Candidates') || [];
  check('test: an exact name answers alone with 100', c1.length === 1 && pick(c1[0], 'Score') === 100 && pick(c1[0], 'Id') === '1002' && pick(c1[0], 'Accepted') === true, JSON.stringify(c1));
  const t2 = await test('The Last Lighthouse.mkv');
  const c2 = pick(t2, 'Candidates') || [];
  check('test: another title (search_titles) finds its entry', pick(c2[0], 'Id') === '1006' && pick(c2[0], 'Accepted') === true, JSON.stringify(c2));
  const t3 = await test('Chroniques de la foret S01E03.mkv');
  const c3 = pick(t3, 'Candidates') || [];
  check('test: an unconfirmed episode number is proposed but not accepted', c3.length > 0 && pick(c3[0], 'Accepted') === false, JSON.stringify(c3));
  const t4 = await test('Documentaire inconnu sans rapport.mkv');
  check('test: a file that looks like nothing finds nothing', (pick(t4, 'Candidates') || []).length === 0, JSON.stringify(pick(t4, 'Candidates')));
  const t5 = await test('x', { ...dumpSource, Dump: { ...dumpSource.Dump, ItemsPath: 'nope' } });
  check('test: a wrong list path says so', pick(t5, 'Ok') === false && /nope/.test(pick(t5, 'Error') || ''), JSON.stringify(t5));
  const t6 = await test('x', { ...dumpSource, BaseUrl: `${API}/dump-bare`, Dump: { ...dumpSource.Dump, ItemsPath: '' } });
  check('test: an empty list path reads a response that IS the list', pick(t6, 'Ok') === true && pick(t6, 'Count') === 40, JSON.stringify(t6)?.slice(0, 200));
  await test('x');   // the real dump is the loaded one again

  // ---- 2. an entry through the usual source test (the mappings applied)
  const read = (await api('POST', '/CustomMetadata/TestSource', { Source: dumpSource, ItemType: 'video', Id: '1002' })).json;
  check('source test: an entry is read through the mappings', pick(read, 'Success') === true && /Sentiers/.test(JSON.stringify(read)), JSON.stringify(read)?.slice(0, 300));
  const series = (await api('POST', '/CustomMetadata/TestSource', { Source: dumpSource, ItemType: 'series', Id: '1002' })).json;
  check('source test: a dump has no series (said, not a crash)', pick(series, 'Success') === false && /autonomes|série/.test(pick(series, 'Error') || ''), JSON.stringify(series)?.slice(0, 200));

  // ---- 3. a library of standalone files: tagged, by name, by id, and one that matches nothing
  const typeOptions = { Type: 'Movie', MetadataFetchers: ['Custom Metadata Provider'], MetadataFetcherOrder: ['Custom Metadata Provider'], ImageFetchers: ['Custom Image Provider'], ImageFetcherOrder: ['Custom Image Provider'] };
  // A library deleted earlier can leave its items behind: start from none so that the scan really calls the providers
  await api('DELETE', '/Library/VirtualFolders?name=Dump&refreshLibrary=true');
  for (let i = 0; i < 30 && (await movies()).length > 0; i++) { await wait(2000); }
  const lib = await api('POST', '/Library/VirtualFolders?name=Dump&collectionType=movies&refreshLibrary=true', {
    LibraryOptions: { EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/dump' }], TypeOptions: [typeOptions], SaveLocalMetadata: false, EnableChapterImageExtraction: false }
  });
  libraryCreated = lib.status < 300;
  check('a movie library over /media/dump is created', libraryCreated, String(lib.status));
  let items = [];
  // Items left by an earlier run are not scanned again (no provider call): force a full refresh of what is there
  for (let i = 0; i < 20 && (await movies()).length < 7; i++) { await wait(2000); }
  for (const m of await movies()) {
    await api('POST', `/Items/${m.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true`);
  }
  for (let i = 0; i < 90; i++) {
    items = await movies();
    if (items.length >= 7 && items.filter(m => m.ProviderIds && Object.keys(m.ProviderIds).some(k => k.toLowerCase() === 'custom')).length >= 6) { break; }
    await wait(2000);
  }
  const byTag = tag => items.find(m => Object.entries(m.ProviderIds || {}).some(([k, v]) => k.toLowerCase() === 'custom' && v === tag));
  check('7 files scanned', items.length === 7, String(items.length));
  for (const [tag, expected, how] of [['dp-1001', 'Voyage au bout de la nuit', 'tag'], ['dp-1002', 'Les Sentiers du Nord', 'exact name'], ['dp-1003', 'Rivages oubliés', 'name with noise words and no accents'],
    ['dp-1005', 'Chroniques de la forêt 2', 'S01E02 + title'], ['dp-1006', 'Le Dernier Phare', 'other title'], ['dp-1007', 'Un titre & son <b>secret</b>', 'bare {id}']]) {
    const m = byTag(tag);
    check(`${how}: ${tag} → "${expected}"`, !!m && m.Name === expected, m ? `got "${m.Name}"` : 'no item carries that id');
  }
  const none = items.find(m => /Documentaire inconnu/.test(m.Path || ''));
  check('a file that matches nothing is left alone (no id, its own name)', !!none && !Object.keys(none.ProviderIds || {}).some(k => k.toLowerCase() === 'custom'), JSON.stringify(none)?.slice(0, 200));

  const m1 = byTag('dp-1001');
  if (m1) {
    check('overview: HTML converted to text (lists as bullets, no tag left)', /Synopsis de Voyage/.test(m1.Overview || '') && /• Premier point/.test(m1.Overview || '') && !/[<>]|&nbsp;/.test(m1.Overview || ''), JSON.stringify(m1.Overview));
    check('studio comes from the group field', (m1.Studios || []).some(s => s.Name === 'Aurora Studio'), JSON.stringify(m1.Studios));
    check('tags come from the tags field', (m1.Tags || []).includes('aventure') && (m1.Tags || []).includes('nuit'), JSON.stringify(m1.Tags));
    check('rating = likes / (likes + dislikes) × 10 (30 likes, 0 dislikes → 10)', m1.CommunityRating === 10, String(m1.CommunityRating));
    check('premiere date from released_at', /^2018-01-01/.test(m1.PremiereDate || ''), String(m1.PremiereDate));
  }
  const m6 = byTag('dp-1006');
  check('rating of another entry is a ratio (36 likes, 10 dislikes → 7.8)', !!m6 && Math.abs(m6.CommunityRating - 7.8) < 0.06, String(m6?.CommunityRating));
  const m7 = byTag('dp-1007');
  check('overview encoded twice (&amp;amp;) is decoded', !!m7 && m7.Overview === 'Texte & simple', JSON.stringify(m7?.Overview));
  const detail = m1 ? (await api('GET', `/Items/${m1.Id}?userId=${auth.User.Id}`)).json : null;
  check('images: the poster and the backdrop of the entry are downloaded', !!detail?.ImageTags?.Primary && (detail?.BackdropImageTags || []).length > 0, JSON.stringify(detail?.ImageTags));

  // ---- 4. "Identify": candidates from the dump for a file the plugin did not match
  if (none) {
    const search = await api('POST', `/Items/RemoteSearch/Movie`, { SearchInfo: { Name: 'Sentiers du Nord' }, ItemId: none.Id });
    const hit = (search.json || []).find(r => Object.entries(r.ProviderIds || {}).some(([k, v]) => k.toLowerCase() === 'custom' && v === 'dp-1002'));
    check('Identify: the dump proposes "Les Sentiers du Nord" with its id', !!hit && hit.Name === 'Les Sentiers du Nord', JSON.stringify(search.json)?.slice(0, 300));
  }

  // ---- 5. a full refresh goes straight to the stored id (and keeps it)
  if (m1) {
    await api('POST', `/Items/${m1.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=true&ReplaceAllImages=false`);
    await wait(5000);
    const again = (await movies()).find(m => m.Id === m1.Id);
    check('a forced refresh keeps the match', again?.Name === 'Voyage au bout de la nuit' && Object.values(again.ProviderIds || {}).includes('dp-1001'), JSON.stringify(again?.ProviderIds));
  }

  // ---- 6. coverage
  const cov = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=missing&pageSize=10')).json;
  check('coverage: 40 in the dump, 7 owned, 33 missing', pick(cov, 'Total') === 40 && pick(cov, 'Owned') === 7 && pick(cov, 'Missing') === 33, JSON.stringify({ t: pick(cov, 'Total'), o: pick(cov, 'Owned'), m: pick(cov, 'Missing') }));
  check('coverage: a page of 10 over 33 missing, 4 pages', (pick(cov, 'Items') || []).length === 10 && pick(cov, 'Matching') === 33 && pick(cov, 'Pages') === 4, JSON.stringify({ n: (pick(cov, 'Items') || []).length, m: pick(cov, 'Matching'), p: pick(cov, 'Pages') }));
  const groups = pick(cov, 'Groups') || [];
  check('coverage: 4 groups, most missing first', groups.length === 4 && groups[0].Missing >= groups[3].Missing, JSON.stringify(groups));
  const owned = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=owned')).json;
  check('coverage: the owned ones are the 6 matched entries and the tagged episode of a series', pick(owned, 'Matching') === 7 && (pick(owned, 'Items') || []).map(i => i.Id).sort().join() === '1001,1002,1003,1004,1005,1006,1007', JSON.stringify((pick(owned, 'Items') || []).map(i => i.Id)));
  const q = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&q=' + encodeURIComponent('PHARE'))).json;
  check('coverage: the search ignores case and accents', pick(q, 'Matching') === 1 && pick(q, 'Items')[0].Id === '1006', JSON.stringify(pick(q, 'Items')?.map(i => i.Id)));
  const byGroup = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=missing&group=' + encodeURIComponent('Boréal Films'))).json;
  check('coverage: filter by group', (pick(byGroup, 'Items') || []).length > 0 && (pick(byGroup, 'Items') || []).every(i => i.Group === 'Boréal Films'), JSON.stringify(pick(byGroup, 'Matching')));
  const csv = await api('GET', '/CustomMetadata/Dump/Export?prefix=dp&status=owned');
  check('export: a CSV with the owned entries', csv.status === 200 && /tag;id;nom/.test(csv.text) && /dp-1001/.test(csv.text) && /possédé/.test(csv.text) && csv.text.trim().split('\n').length === 8, csv.text.slice(0, 200));
  const withImage = (pick(cov, 'Items') || []).filter(i => i.HasImage || i.hasImage);
  check('coverage: entries tell whether they have a preview picture (the address stays on the server)', withImage.length === 10 && !JSON.stringify(cov).includes('172.17.0.1'), JSON.stringify(pick(cov, 'Items')?.[0]));
  const img = await fetch(`${J}/CustomMetadata/Dump/Image?prefix=dp&id=1008`, { headers: H });
  check('preview: the server downloads the picture itself and serves it', img.status === 200 && /^image\//.test(img.headers.get('content-type') || '') && (await img.arrayBuffer()).byteLength > 500, String(img.status));
  const noImg = await fetch(`${J}/CustomMetadata/Dump/Image?prefix=dp&id=nope`, { headers: H });
  check('preview: an unknown entry is a 404', noImg.status === 404, String(noImg.status));
  // series inferred from the names, linked to the library
  const seriesList = pick(cov, 'Series') || [];
  const foret = seriesList.find(x => x.Name === 'Chroniques de la forêt');
  check('series: "Chroniques de la forêt" and "… 2" are grouped (2 entries, 2 owned)', !!foret && foret.Total === 2 && foret.Owned === 2, JSON.stringify(seriesList.slice(0, 3)));
  check('series: the group is linked to the library series that holds one of its entries', foret?.LibrarySeries === 'Série liée au dump' && (foret?.LibrarySeasons || []).join() === '1', JSON.stringify(foret));
  const demo = seriesList.find(x => x.Name === 'Épisode démo');
  check('series: "Épisode démo N" entries form one group of 33', !!demo && demo.Total === 33 && demo.Missing === 33, JSON.stringify(demo));
  check('series: a one-off title is not a series', !seriesList.some(x => /Voyage|Sentiers|Rivages|Dernier/.test(x.Name)), JSON.stringify(seriesList.map(x => x.Name)));
  const bySeries = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&series=' + encodeURIComponent(foret?.Key || 'x'))).json;
  check('series: filtering by a series lists its entries, number hint kept in the title', pick(bySeries, 'Matching') === 2 && (pick(bySeries, 'Items') || []).some(i => i.Number === 2), JSON.stringify(pick(bySeries, 'Items')?.map(i => [i.Name, i.Number])));
  const unknown = await api('GET', '/CustomMetadata/Dump/Coverage?prefix=nope');
  check('coverage: an unknown source is a 404', unknown.status === 404, String(unknown.status));

  // ---- 7. the screens in jellyfin-web
  const browser = await chromium.launch({ channel: 'chrome' });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, permissions: ['clipboard-read', 'clipboard-write'] });
    const page = await context.newPage();
    const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
    await page.goto(`${J}/web/#/login`);
    await page.fill('#txtManualName', user);
    await page.fill('#txtManualPassword', password);
    await page.click('.manualLoginForm button[type=submit]');
    await page.waitForFunction(() => !location.hash.includes('login'));
    const C = '#CustomMetadataConfigPage', M = '#CustomMetadataMissingPage';
    const open = async (name, root, ready) => { await page.goto('about:blank'); await page.goto(`${J}/web/#/configurationpage?name=${name}`); await page.waitForSelector(`${root} ${ready}`, { state: 'visible', timeout: 30000 }); };

    // sources list: the dump card
    await open('Custom%20Metadata%20Provider', C, '#sourcesList .cm-card');
    await page.waitForTimeout(800);
    const card = page.locator(`${C} #sourcesList .cm-card`, { hasText: 'Démo dump' });
    check('sources: the dump card is badged "Dump" and links to its coverage', (await card.locator('.cm-kind').innerText()) === 'Dump' && (await card.locator('[data-open-coverage]').count()) === 1);
    check('sources: the dump card tells what it holds', /entrées en mémoire/.test(await card.locator('.cm-stats').innerText()), await card.locator('.cm-stats').innerText());
    await shot('D1-sources-1440');

    // editor
    await card.locator('button:has-text("Modifier")').click();
    await page.waitForSelector(`${C} #editorView`, { state: 'visible' });
    const nav = await page.$$eval(`${C} .cm-nav-item`, els => els.filter(e => e.offsetParent !== null).map(e => e.firstChild.textContent.trim()));
    check('editor: the sections of a dump (Général, Catalogue, Vidéo / épisode, Réseau, Miniatures)', nav.join('|') === 'Général|Catalogue|Vidéo / épisode|Réseau|Miniatures', nav.join('|'));
    check('editor: the URL field is labelled for a dump', (await page.locator(`${C} #lblBaseUrl`).innerText()) === 'URL du dump *');
    await page.screenshot({ path: path.join(outDir, 'D2-editor-general-1440.png') });
    await page.click(`${C} .cm-nav-item[data-go="dump"]`);
    await page.waitForTimeout(500);
    check('editor: the catalogue pane shows the stored settings', (await page.inputValue(`${C} #srcDumpItems`)) === 'data' && (await page.inputValue(`${C} #srcDumpMinScore`)) === '70' && (await page.inputValue(`${C} #srcDumpGroup`)) === 'brand');
    await shot('D3-editor-catalogue-1440');
    // test panel
    await page.fill(`${C} #dumpTestName`, 'The Last Lighthouse VOSTFR.mkv');
    await page.click(`${C} #btnTestDump`);
    await page.waitForSelector(`${C} #dumpTestResult .cm-dt-list li`, { timeout: 20000 });
    const result = await page.locator(`${C} #dumpTestResult`).innerText();
    check('test panel: count of entries and the candidate with its score', /40 entrées/.test(result) && /Le Dernier Phare/.test(result) && /rapproché/.test(result), result.slice(0, 200));
    await shot('D4-editor-test-1440');
    await page.fill(`${C} #dumpTestName`, 'Chroniques de la foret S01E03.mkv');
    await page.click(`${C} #btnTestDump`);
    await page.waitForFunction(sel => /trop bas/.test(document.querySelector(sel).innerText), `${C} #dumpTestResult`, { timeout: 20000 });
    check('test panel: a score under the minimum is shown "trop bas" with advice', /sous le minimum/.test(await page.locator(`${C} #dumpTestResult`).innerText()));
    // the video mapping holds the studio and the HTML switch
    await page.click(`${C} .cm-nav-item[data-go="video"]`);
    check('editor: video mapping shows studio and the HTML overview switch', (await page.inputValue(`${C} #srcVideoStudio`)) === 'brand' && await page.isChecked(`${C} #srcVideoOverviewHtml`));
    // saving keeps the dump settings
    await page.click(`${C} .cm-nav-item[data-go="dump"]`);
    await page.fill(`${C} #srcDumpCache`, '45');
    await page.click(`${C} #sourceForm button[type=submit]`);
    await page.waitForSelector(`${C} #listView`, { state: 'visible' });
    await wait(1500);
    const saved = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json.Sources.find(s => s.Prefix === 'dp');
    check('save: dump settings stored (cache 45, mapping kept, endpoints forced)', saved?.SourceType === 'Dump' && saved.Dump?.CacheMinutes === 45 && saved.Dump?.GroupPath === 'brand' && saved.VideoFieldMappings?.Studio === 'brand' && saved.VideoEndpoint === '/items/{id}' && saved.EpisodeListEndpoint === '', JSON.stringify(saved)?.slice(0, 400));

    // new dump through the type chooser
    await page.click(`${C} #sourcesList .cm-add`);
    await page.waitForSelector(`${C} #typeChooser [data-new-type="dump"]`, { state: 'visible' });
    await page.click(`${C} #typeChooser [data-new-type="dump"]`);
    await page.waitForSelector(`${C} #editorView`, { state: 'visible' });
    check('new dump: the mapping starts with the usual dump paths', (await page.inputValue(`${C} #srcVideoTitle`)) === 'name' && (await page.inputValue(`${C} #srcVideoPoster`)) === 'poster_url' && (await page.inputValue(`${C} #srcVideoDate`)) === 'released_at || created_at');
    await page.click(`${C} #btnCancelEdit`);

    // the coverage screen
    await page.evaluate(() => sessionStorage.setItem('cm-missing-dump', 'dp'));
    await open('CustomMetadataMissingEpisodes', M, '.cmn-nav');
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { state: 'visible', timeout: 30000 });
    check('coverage screen: opens on the catalogue and shows the totals', /40/.test(await page.locator(`${M} #dcSummary`).innerText()) && /33/.test(await page.locator(`${M} #dcSummary`).innerText()), await page.locator(`${M} #dcSummary`).innerText());
    check('coverage screen: groups listed with their missing count', (await page.locator(`${M} .dc-group[data-group]`).count()) === 4, String(await page.locator(`${M} .dc-group[data-group]`).count()));
    await page.waitForTimeout(600);
    await shot('D5-coverage-1440');
    await page.locator(`${M} .dc-group[data-group]`).first().click();
    await page.waitForFunction(sel => document.querySelector(sel).textContent.includes('Groupe :'), `${M} #dcChips`);
    check('coverage screen: clicking a group filters and adds a removable chip', (await page.locator(`${M} .dc-chip`).count()) === 1);
    await shot('D6-coverage-group-1440');
    await page.locator(`${M} .dc-chip`).click();
    await page.waitForFunction(sel => document.querySelector(sel).children.length === 0, `${M} #dcChips`);
    await page.fill(`${M} #dcQ`, 'zzzz-aucun');
    await page.waitForSelector(`${M} .dc-empty`, { timeout: 8000 });
    check('coverage screen: a search without result says what to do', /Retirez un filtre/.test(await page.locator(`${M} .dc-empty`).innerText()));
    await shot('D7-coverage-empty-1440');
    await page.fill(`${M} #dcQ`, '');
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { timeout: 8000 });
    // real mouse click on the copy button (clipboard needs a user gesture)
    const firstName = await page.locator(`${M} #dcResults .dc-entry`).first().getAttribute('data-file');
    await page.locator(`${M} #dcResults .me-copy`).first().click();
    await page.waitForTimeout(300);
    const clip = await page.evaluate(() => navigator.clipboard.readText()).catch(() => null);
    check('coverage screen: "Copier" puts "Name {dp-id}" on the clipboard', clip === firstName && /\{dp-\d+\}$/.test(clip || ''), String(clip));
    // pager
    await page.selectOption(`${M} #dcStatusSel`, 'all');
    await page.waitForFunction(sel => /40 entrées/.test(document.querySelector(sel).textContent), `${M} #dcCount`, { timeout: 8000 });
    check('coverage screen: 40 entries make 2 pages of 24', /Page 1 sur 2/.test(await page.locator(`${M} #dcPager`).innerText()) && (await page.locator(`${M} #dcResults .dc-entry`).count()) === 24);
    await page.click(`${M} #dcPager [data-page="2"]`);
    await page.waitForFunction(sel => /Page 2 sur 2/.test(document.querySelector(sel).textContent), `${M} #dcPager`, { timeout: 8000 });
    check('coverage screen: the second page holds the 16 others', (await page.locator(`${M} #dcResults .dc-entry`).count()) === 16);
    await page.click(`${M} #dcPager [data-page="1"]`);
    await page.waitForFunction(sel => /Page 1 sur 2/.test(document.querySelector(sel).textContent), `${M} #dcPager`, { timeout: 8000 });
    // list view
    await page.click(`${M} #dcView [data-view="list"]`);
    await page.waitForTimeout(300);
    const tagVisible = await page.evaluate(sel => { const t = document.querySelector(sel + ' .dc-fn-tag'); const code = t.closest('code'); return t.getBoundingClientRect().right <= code.getBoundingClientRect().right + 1; }, `${M} #dcResults .me-list`);
    check('coverage screen: in the list, a long name gives way before its tag', tagVisible);
    await shot('D8-coverage-list-1440');
    await page.click(`${M} #dcView [data-view="cards"]`);
    // the series view is still there
    await page.click(`${M} #modeSwitch [data-mode="series"]`);
    check('mode switch: back to the series view', await page.locator(`${M} #seriesView`).isVisible() && !(await page.locator(`${M} #dumpView`).isVisible()));
    await page.click(`${M} #modeSwitch [data-mode="dump"]`);
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { timeout: 8000 });

    // widths
    await page.setViewportSize({ width: 1920, height: 1100 }); await page.waitForTimeout(500); await shot('D9-coverage-1920');
    await page.setViewportSize({ width: 412, height: 900 }); await page.waitForTimeout(500); await shot('D10-coverage-mobile');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    check('coverage screen: no horizontal scroll on a phone', !overflow);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.waitForTimeout(400);
    const axe = await new AxeBuilder({ page }).include(`${M} #dumpView`).analyze();
    const serious = axe.violations.filter(v => ['serious', 'critical'].includes(v.impact));
    check('axe: no serious accessibility violation on the catalogue view', serious.length === 0, serious.map(v => `${v.id} (${v.nodes.length})`).join(', '));
  } finally {
    await browser.close();
  }
} finally {
  if (libraryCreated) {
    await api('DELETE', '/Library/VirtualFolders?name=Dump&refreshLibrary=true');
  }
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
}
console.log(`\n${pass} PASS, ${fail} FAIL`);
process.exit(fail ? 1 : 0);
