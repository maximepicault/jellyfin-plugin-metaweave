// "Dump" source in the REAL Jellyfin: one call downloads a catalogue, files are matched by tag AND by name, the coverage screen
// shows what is missing. REST checks + the editor and the coverage screen in jellyfin-web driven with Playwright
// (system Google Chrome) + screenshots. Needs the test container of ui-test.sh (whose gen.py makes /media/dump and server.py serves /dump).
// Usage: node dump-real.mjs <base_url> <out_dir> [user] [password]
// Creates a source "dp" and a movie library "Dump" (/media/dump), restores the configuration and removes the library at the end.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { execSync } from 'node:child_process';

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
let seriesLibraryCreated = false;

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
  // a series: the right episode is at 69 (capped) and the rest of the series at 65 - the right one is accepted because it stands out
  const series1 = await test('Chemins hiver nordique S01E01.mkv', { ...dumpSource, BaseUrl: `${API}/dump-series` });
  const cs1 = pick(series1, 'Candidates') || [];
  check('test: in a series, the episode that stands out from the others (70 against 65) is accepted', cs1.length === 3 && pick(cs1[0], 'Id') === '2001' && pick(cs1[0], 'Accepted') === true && pick(cs1[0], 'How') === 'dominant' && pick(cs1[1], 'Accepted') === false, JSON.stringify(cs1));
  // the format "name1 3 name2 name3": the number is glued after the entry's own name, the title found is one of its other titles
  const series3 = await test('Chemins hiver nordique S01E03.mkv', { ...dumpSource, BaseUrl: `${API}/dump-numafter` });
  const cs3 = pick(series3, 'Candidates') || [];
  check('test: the number glued after the entry\'s own name confirms it (episode 3 → "Le col gele", high score)', cs3.length >= 1 && pick(cs3[0], 'Id') === '3003' && pick(cs3[0], 'Accepted') === true && pick(cs3[0], 'Score') >= 75 && (cs3.length === 1 || pick(cs3[1], 'Accepted') === false), JSON.stringify(cs3));
  const series3b = await test('Chemins hiver nordique S01E13.mkv', { ...dumpSource, BaseUrl: `${API}/dump-numafter` });
  check('test: "13" is not read as "1" or "3" (no entry confirmed for episode 13)', !(pick(series3b, 'Candidates') || []).some(c => pick(c, 'Accepted') === true), JSON.stringify(pick(series3b, 'Candidates')));
  // the number written "ep 3" next to the name
  const series4 = await test('Chemins hiver nordique S01E03.mkv', { ...dumpSource, BaseUrl: `${API}/dump-numafter?fmt=ep` });
  const cs4 = pick(series4, 'Candidates') || [];
  check('test: "ep 3" glued next to the entry\'s name confirms it as well', cs4.length >= 1 && pick(cs4[0], 'Id') === '3003' && pick(cs4[0], 'Accepted') === true && pick(cs4[0], 'Score') >= 75, JSON.stringify(cs4));
  // "Episode N" (not only "ep N"), after or before the name, in the other titles
  for (const fmt of ['episode', 'epbefore', 'episodebefore']) {
    const r = await test('Chemins hiver nordique S01E03.mkv', { ...dumpSource, BaseUrl: `${API}/dump-numafter?fmt=${fmt}` });
    const cs = pick(r, 'Candidates') || [];
    check(`test: the number written "${fmt}" next to the entry's name confirms it`, cs.length >= 1 && pick(cs[0], 'Id') === '3003' && pick(cs[0], 'Accepted') === true && pick(cs[0], 'Score') >= 75, JSON.stringify(cs));
  }
  // "Episode 3" / "Ep 3" written in the FILE name is an episode number too (like S01E03)
  for (const name of ['Chemins hiver nordique Episode 3.mkv', 'Chemins hiver nordique Ep 3.mkv', 'Chemins hiver nordique Épisode 03.mkv', 'Chemins hiver nordique Ep. 3 [1080p].mkv']) {
    const r = await test(name, { ...dumpSource, BaseUrl: `${API}/dump-numafter` });
    const cs = pick(r, 'Candidates') || [];
    check(`test: "${name.slice(21, -4)}" in the file name is read as episode 3`, cs.length >= 1 && pick(cs[0], 'Id') === '3003' && pick(cs[0], 'Accepted') === true, JSON.stringify(cs));
  }
  // a file that IS "Episode 12" keeps its name whole (an entry named so is found by its name)
  const wholeName = await test('Episode 12.mkv');
  check('test: a file named just "Episode 12" is not emptied by the episode word', (pick(wholeName, 'Candidates') || []).every(c => !/Les Sentiers/.test(c.Name)), JSON.stringify(pick(wholeName, 'Candidates')));
  // "Step 3" is not "Ep 3"
  const step = await test('Les Sentiers du Nord Step 3.mkv');
  check('test: a word that merely contains "ep" (Step 3) is not an episode number', !(pick(step, 'Candidates') || []).some(c => pick(c, 'Id') === '1002' && c.Score === 100), JSON.stringify(pick(step, 'Candidates')));
  // two entries equal through their other titles: the one whose main name is much closer to the file wins
  const close = await test('Chemins hiver nordique S01E01.mkv', { ...dumpSource, BaseUrl: `${API}/dump-close` });
  const csc = pick(close, 'Candidates') || [];
  check('test: between two entries, the one with the name closest to the file ranks first and is accepted', csc.length === 2 && pick(csc[0], 'Id') === '4002' && pick(csc[0], 'Accepted') === true && pick(csc[1], 'Accepted') === false, JSON.stringify(csc));
  // both names hold the whole title: the one with fewer extra words ranks first (and, 6 points ahead, is accepted)
  const extra = await test('Chemins hiver nordique S01E01.mkv', { ...dumpSource, BaseUrl: `${API}/dump-extra` });
  const cse = pick(extra, 'Candidates') || [];
  check('test: with the whole title in both names, the one with fewer extra words ranks first and is accepted', cse.length === 2 && pick(cse[0], 'Id') === '5002' && pick(cse[0], 'Accepted') === true && pick(cse[1], 'Accepted') === false, JSON.stringify(cse));
  // the same series where nothing tells the episodes apart: nothing is accepted (the wrong-episode risk the cap guards against)
  const series2 = await test('Chemins hiver nordique S01E02.mkv', { ...dumpSource, BaseUrl: `${API}/dump-series?flat=1` });
  const cs2 = pick(series2, 'Candidates') || [];
  check('test: when no episode stands out, none is accepted (proposed only)', cs2.length === 3 && cs2.every(c => pick(c, 'Accepted') === false), JSON.stringify(cs2));
  const t4 = await test('Documentaire inconnu sans rapport.mkv');
  check('test: a file that looks like nothing finds nothing', (pick(t4, 'Candidates') || []).length === 0, JSON.stringify(pick(t4, 'Candidates')));
  const t5 = await test('x', { ...dumpSource, Dump: { ...dumpSource.Dump, ItemsPath: 'nope' } });
  check('test: a wrong list path says so', pick(t5, 'Ok') === false && /nope/.test(pick(t5, 'Error') || ''), JSON.stringify(t5));
  check('test: a wrong list path says where the lists are and gives the raw response', /Listes trouvées : « data »/.test(pick(t5, 'Error') || '') && /"data"/.test(pick(t5, 'ResponsePreview') || ''), JSON.stringify(t5)?.slice(0, 300));
  check('test: the raw response is indented JSON with long lists cut to two elements', /\n  "data": \[/.test(pick(t5, 'ResponsePreview') || '') && /38 autre/.test(pick(t5, 'ResponsePreview') || ''), (pick(t5, 'ResponsePreview') || '').slice(0, 200));
  const badSource = { ...dumpSource, Dump: { ...dumpSource.Dump, ItemsPath: 'nope' } };
  for (const id of ['', '1002']) {
    const r = (await api('POST', '/CustomMetadata/TestSource', { Source: badSource, ItemType: 'video', Id: id })).json;
    check(`source test: an unreadable dump (id "${id}") says why and shows the raw response`, pick(r, 'Success') === false && /Listes trouvées/.test(pick(r, 'Error') || '') && /"data"/.test(pick(r, 'ErrorBody') || ''), JSON.stringify(r)?.slice(0, 300));
  }
  const t6 = await test('x', { ...dumpSource, BaseUrl: `${API}/dump-bare`, Dump: { ...dumpSource.Dump, ItemsPath: '' } });
  check('test: an empty list path reads a response that IS the list', pick(t6, 'Ok') === true && pick(t6, 'Count') === 40, JSON.stringify(t6)?.slice(0, 200));
  // pagination: the dump split into pages of 3, read through the page parameter until an empty page (or a 404)
  const pg = { ...dumpSource, BaseUrl: `${API}/dump-paged`, Dump: { ...dumpSource.Dump, PageParameter: 'page', FirstPage: 1, MaxPages: 50 } };
  const t7 = await test('x', pg);
  check('test: a paginated dump is read page after page (all 40 entries)', pick(t7, 'Ok') === true && pick(t7, 'Count') === 40, JSON.stringify(t7)?.slice(0, 200));
  const t8 = await test('x', { ...pg, BaseUrl: `${API}/dump-paged?mode=404` });
  check('test: a 404 after the last page ends the dump (40 entries, no error)', pick(t8, 'Ok') === true && pick(t8, 'Count') === 40, JSON.stringify(t8)?.slice(0, 200));
  const t9 = await test('x', { ...pg, Dump: { ...pg.Dump, MaxPages: 3 } });
  check('test: reaching the page limit before the end is refused, not kept half-loaded', pick(t9, 'Ok') === false && /limite/.test(pick(t9, 'Error') || ''), JSON.stringify(t9)?.slice(0, 200));
  const tj = await test('x', { ...pg, BaseUrl: `${API}/dump-paged-json`, Dump: { ...pg.Dump, PageParameter: 'options={"pagination":{"page":{page},"pageSize":7}}' } });
  check('test: a {page} template sends a JSON query parameter (40 entries in pages of 7)', pick(tj, 'Ok') === true && pick(tj, 'Count') === 40, JSON.stringify(tj)?.slice(0, 200));
  // the pagination typed by hand in the base URL (a query string): the dump and the entry test must both still work
  const hard = { ...dumpSource, BaseUrl: `${API}/dump-paged-json?options=${encodeURIComponent('{"pagination":{"page":1,"pageSize":100}}')}` };
  const th = await test('x', hard);
  check('test: a base URL that already holds the pagination query downloads', pick(th, 'Ok') === true && pick(th, 'Count') === 40, JSON.stringify(th)?.slice(0, 200));
  const thr = (await api('POST', '/CustomMetadata/TestSource', { Source: hard, ItemType: 'video', Id: '1002' })).json;
  check('source test: an id with a query in the base URL is read (no ".../items/1002" glued after the query)', pick(thr, 'Success') === true && !/\}\/items/.test(pick(thr, 'Url') || ''), JSON.stringify(thr)?.slice(0, 300));
  const thn = (await api('POST', '/CustomMetadata/TestSource', { Source: hard, ItemType: 'video', Id: '' })).json;
  check('source test: no id with a query in the base URL reads the first entry', pick(thn, 'Success') === true && pick(thn, 'Id') === '1001', JSON.stringify(thn)?.slice(0, 300));
  const thp = await test('x', { ...hard, Dump: { ...hard.Dump, PageParameter: 'options={"pagination":{"page":{page},"pageSize":7}}' } });
  check('test: the pagination field replaces the same parameter typed in the URL (pages of 7, 40 entries)', pick(thp, 'Ok') === true && pick(thp, 'Count') === 40, JSON.stringify(thp)?.slice(0, 200));
  const t10 = await test('x', { ...dumpSource, BaseUrl: `${API}/dump-bare`, Dump: { ...dumpSource.Dump, ItemsPath: '', PageParameter: 'page' } });
  check('test: an API that ignores the page parameter stops at the first repeated page', pick(t10, 'Ok') === true && pick(t10, 'Count') === 40, JSON.stringify(t10)?.slice(0, 200));
  await test('x');   // the real dump is the loaded one again

  // ---- 2. an entry through the usual source test (the mappings applied)
  const read = (await api('POST', '/CustomMetadata/TestSource', { Source: dumpSource, ItemType: 'video', Id: '1002' })).json;
  check('source test: an entry is read through the mappings', pick(read, 'Success') === true && /Sentiers/.test(JSON.stringify(read)), JSON.stringify(read)?.slice(0, 300));
  const noId = (await api('POST', '/CustomMetadata/TestSource', { Source: dumpSource, ItemType: 'video', Id: '' })).json;
  check('source test: a dump needs no id (its first entry is read)', pick(noId, 'Success') === true && pick(noId, 'Id') === '1001', JSON.stringify(noId)?.slice(0, 300));
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
  // series inferred from the names, linked to the library: the first level of the screen
  const sp = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&pageSize=50')).json;
  const cards = pick(sp, 'Items') || [];
  const foret = cards.find(x => x.Name === 'Chroniques de la forêt');
  check('series: the page tells it has series and the preview ratio (16:9 by default)', pick(sp, 'HasSeries') === true && pick(sp, 'PreviewRatio') === '16:9', JSON.stringify({ h: pick(sp, 'HasSeries'), r: pick(sp, 'PreviewRatio') }));
  check('series: "Chroniques de la forêt" and "… 2" are grouped (2 entries, 2 owned)', !!foret && foret.Total === 2 && foret.Owned === 2, JSON.stringify(cards.slice(0, 3)));
  check('series: the group is linked to the library series that holds one of its entries', foret?.LibrarySeries === 'Série liée au dump' && (foret?.LibrarySeasons || []).join() === '1', JSON.stringify(foret));
  const demo = cards.find(x => x.Name === 'Épisode démo');
  check('series: "Épisode démo N" entries form one group of 33, with a picture', !!demo && demo.Total === 33 && demo.Missing === 33 && !!demo.ImageId, JSON.stringify(demo));
  const lone = cards.filter(x => x.Total === 1);
  check('series: each entry that belongs to no group is its own series of ONE episode (5 cards), none called "Épisodes seuls"', lone.length === 5 && !cards.some(x => x.Name === 'Épisodes seuls') && cards.length === 7, JSON.stringify(cards.map(c => [c.Name, c.Total])));
  const phare = cards.find(x => /Dernier Phare/.test(x.Name));
  check('series: a one-off title is a series of one episode with its own picture, score and date', !!phare && phare.Total === 1 && phare.Owned === 1 && !!phare.ImageId && phare.WorstScore != null && !!phare.FirstDate, JSON.stringify(phare));
  const incomplete = pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&onlyIncomplete=true')).json, 'Items') || [];
  check('series: "only incomplete" hides the complete ones (the series of one owned episode too)', incomplete.length === 1 && incomplete[0].Name === 'Épisode démo', JSON.stringify(incomplete.map(x => x.Name)));
  const bySeries = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&series=' + encodeURIComponent(foret?.Key || 'x'))).json;
  check('series: the entries of a series, number hint kept, series info given', pick(bySeries, 'Matching') === 2 && (pick(bySeries, 'Items') || []).some(i => i.Number === 2) && pick(pick(bySeries, 'SeriesInfo'), 'LibrarySeries') === 'Série liée au dump', JSON.stringify(pick(bySeries, 'Items')?.map(i => [i.Name, i.Number])));
  const loneRows = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&series=' + encodeURIComponent(phare?.Key || 'x'))).json;
  check('series: opening a series of one episode lists that one entry', pick(loneRows, 'Matching') === 1 && pick(loneRows, 'Items')[0].Id === '1006' && pick(loneRows, 'Items')[0].Grouped === false, JSON.stringify(pick(loneRows, 'Items')?.map(i => [i.Id, i.Grouped])));
  // how well each owned file was recognised: to find the poor matches and fix their names by hand
  const ownedAll = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=owned&pageSize=50')).json;
  const byId = Object.fromEntries((pick(ownedAll, 'Items') || []).map(i => [i.Id, i]));
  check('score: a file with the tag is certain (100), an exact name too', byId['1001']?.MatchScore === 100 && byId['1001']?.MatchHow === 'tag' && byId['1002']?.MatchScore === 100 && byId['1002']?.MatchHow === 'title', JSON.stringify([byId['1001'], byId['1002']]));
  check('score: a file found through another title has its real score and says so', byId['1006']?.MatchHow === 'fuzzy' && byId['1006']?.MatchScore >= 60 && byId['1006']?.MatchScore < 100 && byId['1006']?.FileName === 'The Last Lighthouse.mkv', JSON.stringify(byId['1006']));
  const weak = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&maxScore=99')).json;
  check('score: the filter "0 to 99" keeps only the files that were not recognised for sure (the owned ones, whatever "Afficher" says)', pick(weak, 'Matching') === 1 && pick(weak, 'Items')[0].Id === '1006', JSON.stringify(pick(weak, 'Items')?.map(i => [i.Id, i.MatchScore])));
  const noneUnder50 = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&maxScore=50')).json;
  check('score: nothing under 50', pick(noneUnder50, 'Matching') === 0);
  const sortedScore = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=owned&sort=score')).json;
  check('score: sorting by score puts the poorest recognised first', pick(sortedScore, 'Items')[0].Id === '1006', JSON.stringify(pick(sortedScore, 'Items')?.map(i => [i.Id, i.MatchScore])));
  const wCards = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&maxScore=99')).json;
  check('score: series filtered by score keep the one that holds a poor match, with its worst score', (pick(wCards, 'Items') || []).length === 1 && /Dernier Phare/.test(pick(wCards, 'Items')[0].Name) && pick(wCards, 'Items')[0].WorstScore === byId['1006']?.MatchScore, JSON.stringify(pick(wCards, 'Items')));
  const sCards = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&sort=score')).json;
  check('score: series sorted by score: the poorest first', pick(sCards, 'Items')[0].WorstScore === byId['1006']?.MatchScore, JSON.stringify(pick(sCards, 'Items')?.map(i => [i.Name, i.WorstScore])));
  // release date: series sorted by their latest / earliest entry
  const newer = pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&sort=datenew')).json, 'Items').filter(c => !c.Alone && c.LastDate);
  const older = pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&sort=dateold')).json, 'Items').filter(c => !c.Alone && c.FirstDate);
  check('dates: series carry their first and last release dates', newer.length >= 2 && newer.every(c => c.FirstDate && c.LastDate && c.FirstDate <= c.LastDate), JSON.stringify(newer));
  check('dates: "most recent first" orders series by their latest entry, "oldest first" by their earliest', newer.every((c, i) => i === 0 || newer[i - 1].LastDate >= c.LastDate) && older.every((c, i) => i === 0 || older[i - 1].FirstDate <= c.FirstDate), JSON.stringify([newer.map(c => c.LastDate), older.map(c => c.FirstDate)]));
  // free interval, both ends included
  const between = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&minScore=70&maxScore=80')).json;
  check('score interval: 70 to 80 keeps the file recognised at ' + byId['1006']?.MatchScore, pick(between, 'Matching') === 1 && pick(between, 'Items')[0].Id === '1006', JSON.stringify(pick(between, 'Items')?.map(i => [i.Id, i.MatchScore])));
  const above = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&minScore=' + (byId['1006'].MatchScore + 1))).json;
  check('score interval: from above that score keeps the 6 certain ones (and not the poor one)', pick(above, 'Matching') === 6 && !(pick(above, 'Items') || []).some(i => i.Id === '1006'), JSON.stringify(pick(above, 'Items')?.map(i => [i.Id, i.MatchScore])));
  const exact = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&minScore=100&maxScore=100')).json;
  check('score interval: 100 to 100 is exactly the certain ones', pick(exact, 'Matching') === 6, String(pick(exact, 'Matching')));
  const sCardsInterval = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&minScore=70&maxScore=80')).json;
  check('score interval: series are kept when one of their files is in it', pick(sCardsInterval, 'Items').length === 1 && /Dernier Phare/.test(pick(sCardsInterval, 'Items')[0].Name), JSON.stringify(pick(sCardsInterval, 'Items')?.map(c => c.Name)));
  // complete / incomplete / all series
  const nComplete = async v => (pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&completeness=' + v)).json, 'Items') || []).map(c => c.Name + ':' + c.Missing);
  const cAll = await nComplete('all'), cInc = await nComplete('incomplete'), cCom = await nComplete('complete');
  check('series: completeness all / incomplete / complete = 7 / 1 / 6 series', cAll.length === 7 && cInc.length === 1 && cCom.length === 6 && cCom.every(x => x.endsWith(':0')) && cInc.every(x => !x.endsWith(':0')), JSON.stringify([cAll, cInc, cCom]));
  // several sort keys chained: the second breaks the ties of the first
  const chained = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&sort=group,name&pageSize=60')).json;
  const rowsG = pick(chained, 'Items') || [];
  const sortedOk = rowsG.every((r, i) => i === 0 || rowsG[i - 1].Group < r.Group || (rowsG[i - 1].Group === r.Group && rowsG[i - 1].Name.localeCompare(r.Name, 'fr', { sensitivity: 'base' }) <= 0));
  check('sort: "group,name" orders by group then by name inside each group', rowsG.length >= 38 && sortedOk, JSON.stringify(rowsG.slice(0, 4).map(r => [r.Group, r.Name])));
  const chained2 = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=owned&sort=score,name')).json;
  check('sort: "score,name" puts the poorly recognised first, the certain ones by name after', pick(chained2, 'Items')[0].Id === '1006' && pick(chained2, 'Items').slice(1).every((r, i, a) => i === 0 || a[i - 1].Name.localeCompare(r.Name, 'fr', { sensitivity: 'base' }) <= 0), JSON.stringify(pick(chained2, 'Items')?.map(r => [r.Name, r.MatchScore])));
  const best = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&sort=scoredesc')).json;
  check('sort: series "best recognised first" starts with a certain one and ends with the poor one', pick(best, 'Items')[0].WorstScore === 100 && pick(best, 'Items').filter(c => c.WorstScore != null).slice(-1)[0].WorstScore === byId['1006']?.MatchScore, JSON.stringify(pick(best, 'Items')?.map(c => [c.Name, c.WorstScore])));
  const twoKeys = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&sort=scoredesc,datenew')).json;
  check('sort: two keys on series are accepted', (pick(twoKeys, 'Items') || []).length === 7);
  // release-date filter on entries (the "Année" list): 2018 = the entries i = 0, 7, 14, 21, 28, 35 of the demo dump
  const y2018 = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&year=2018&pageSize=60')).json;
  check('year filter: 2018 keeps exactly the 6 entries released that year', pick(y2018, 'Matching') === 6 && (pick(y2018, 'Items') || []).every(i => /^2018-/.test(i.Date)), JSON.stringify([pick(y2018, 'Matching'), pick(y2018, 'Items')?.map(i => i.Date)]));
  // the interval of release years, on the entries and on the series (the demo dump: year = 2018 + i % 7)
  const nEntries = async q => pick((await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&pageSize=60&' + q)).json, 'Matching');
  check('year interval on entries: 2018 to 2019 = 12, from 2024 = 5, up to 2018 = 6, 2020 to 2020 = 6', (await nEntries('minYear=2018&maxYear=2019')) === 12 && (await nEntries('minYear=2024')) === 5 && (await nEntries('maxYear=2018')) === 6 && (await nEntries('minYear=2020&maxYear=2020')) === 6);
  const sy = (await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&minYear=2023&maxYear=2023&pageSize=50')).json;
  const syNames = (pick(sy, 'Items') || []).map(c => c.Name);
  check('year interval on series: kept when one of their entries is in it (2023 = "Le Dernier Phare", the big series; not "Les Sentiers du Nord", 2019)', syNames.some(n => /Dernier Phare/.test(n)) && syNames.some(n => /Épisode démo/.test(n)) && !syNames.some(n => /Sentiers/.test(n)), JSON.stringify(syNames));
  check('year interval on series: the page gives the years of the dump for the filter', (pick(sy, 'Years') || []).length === 7 && pick(sy, 'Years')[0] === 2024, JSON.stringify(pick(sy, 'Years')));
  // a series id in the dump decides the series (here the brand id), with its name from the dump or, when absent, from the titles
  const withSeriesId = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, SeriesIdPath: 'brand_id', SeriesNamePath: 'brand' } } : x) };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, withSeriesId);
  const sid = (pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&pageSize=50')).json, 'Items') || []);
  check('series id: the entries are grouped by it (4 brands, 40 entries) and named by the name path', sid.length === 4 && sid.reduce((n, c) => n + c.Total, 0) === 40 && sid.map(c => c.Name).sort().join('|') === 'Aurora Studio|Boréal Films|Cinéma Lumen|Delta Prod', JSON.stringify(sid.map(c => [c.Name, c.Total])));
  const withSeriesIdNoName = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, SeriesIdPath: 'brand_id', SeriesNamePath: '' } } : x) };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, withSeriesIdNoName);
  const sidn = (pick((await api('GET', '/CustomMetadata/Dump/Series?prefix=dp&pageSize=50')).json, 'Items') || []);
  check('series id: without a name path every series still has a name', sidn.length === 4 && sidn.every(c => c.Name && c.Name.length >= 3), JSON.stringify(sidn.map(c => [c.Name, c.Total])));
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  // tags that are a list of objects: read like the mapping reads them ("tags[].label", or "tags" + the item field), not dropped
  for (const [tagsPath, itemField] of [['tags[].label', ''], ['tags', 'label']]) {
    const objTags = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, BaseUrl: `${API}/dump-tags`, VideoFieldMappings: { ...x.VideoFieldMappings, Tags: tagsPath, TagsItemField: itemField } } : x) };
    await api('POST', `/Plugins/${PLUGIN}/Configuration`, objTags);
    const tc = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&refresh=true&pageSize=5')).json;
    const names = (pick(tc, 'Tags') || []).map(t => t.Name + ':' + t.Count).sort().join();
    check(`tags: a list of objects (${tagsPath}${itemField ? ' + ' + itemField : ''}) gives the catalogue its tag list`, names === 'aventure:4,jour:2,nuit:2', names);
    const byTag = (await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&status=all&tag=nuit')).json;
    check('tags: filtering on one of them keeps its entries', pick(byTag, 'Matching') === 2, String(pick(byTag, 'Matching')));
  }
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await api('GET', '/CustomMetadata/Dump/Coverage?prefix=dp&refresh=true&pageSize=1');
  // the date for the catalogue is its own path: the video mapping's date can stay EMPTY (kept out of Jellyfin) and the filters still work
  const unmapped = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, ReleaseDatePath: 'released_at' }, VideoFieldMappings: { ...x.VideoFieldMappings, PremiereDate: '' } } : x) };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, unmapped);
  check('release date: with the mapping\'s date EMPTY, the catalogue still filters by year through its own path (2018 = 6 entries)', (await nEntries('year=2018')) === 6);
  const withUnix = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, ReleaseDatePath: 'released_at_unix' }, VideoFieldMappings: { ...x.VideoFieldMappings, PremiereDate: '' } } : x) };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, withUnix);
  check('release date: a number path is read as Unix seconds (2017 and later)', (await nEntries('minYear=2017&maxYear=2017')) > 0 && (await nEntries('year=2018')) !== 6, String(await nEntries('minYear=2017&maxYear=2017')));
  const fallback = { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, ReleaseDatePath: 'nope || released_at_unix || released_at' }, VideoFieldMappings: { ...x.VideoFieldMappings, PremiereDate: '' } } : x) };
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, fallback);
  check('release date: alternatives with "||" fall back to the next path that holds a date', (await nEntries('minYear=2017&maxYear=2017')) > 0);
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  const years = pick(cov, 'Years') || [];
  check('year filter: the list of years is the dump\'s (2018 to 2024)', years.length === 7 && years[0] === 2024 && years[6] === 2018, JSON.stringify(years));
  const csvScore = await api('GET', '/CustomMetadata/Dump/Export?prefix=dp&status=owned');
  check('export: the CSV has the library file name and the score', /statut;fichier;score/.test(csvScore.text) && /The Last Lighthouse\.mkv;\d+/.test(csvScore.text), csvScore.text.slice(0, 160));
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
    // the refresh bar of a dump: no series / season boxes (a dump has none), episodes + movies, a library picker
    const head = page.locator(`${C} .cm-group-head[data-group="dp"]`);
    check('refresh bar of a dump: Épisodes and Films boxes, no Séries / Saisons', (await head.locator('[data-rf="episode"]').count()) === 1 && (await head.locator('[data-rf="movie"]').count()) === 1 && (await head.locator('[data-rf="series"]').count()) === 0 && (await head.locator('[data-rf="season"]').count()) === 0);
    await page.waitForSelector(`${C} .cm-libpick summary`, { timeout: 10000 });
    check('refresh bar of a dump: a library picker, all libraries by default', /Médiathèques : toutes/.test(await head.locator('.cm-libpick summary').innerText()), await head.locator('.cm-libpick summary').innerText());
    await head.locator('.cm-libpick summary').click();
    await page.waitForTimeout(300);
    await shot('D1b-refresh-library-picker-1440');
    await head.locator('.cm-libpick summary').click();
    await head.locator('button[data-refresh]').click();
    await page.waitForFunction(sel => /étape|Terminé|Aucun/.test(document.querySelector(sel).textContent), `${C} [data-refresh-status="dp"]`, { timeout: 30000 });
    check('refresh button of a dump: it says something right away (it used to do nothing)', true);
    await page.waitForFunction(sel => /Terminé/.test(document.querySelector(sel).textContent), `${C} [data-refresh-status="dp"]`, { timeout: 120000 });
    check('refresh button of a dump: it ends with a summary, two steps', /Terminé : étape 2\/2/.test(await head.locator('[data-refresh-status]').innerText()), await head.locator('[data-refresh-status]').innerText());
    await page.waitForTimeout(600);
    check('refresh button of a dump: usable again once it is over', await head.locator('button[data-refresh]').isEnabled());
    await shot('D1c-refresh-done-1440');
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

    // the coverage screen: series cards first, like the series of the first view
    await page.evaluate(() => sessionStorage.setItem('cm-missing-dump', 'dp'));
    await open('CustomMetadataMissingEpisodes', M, '.cmn-nav');
    await page.waitForSelector(`${M} #dcResults .dc-series-card`, { state: 'visible', timeout: 30000 });
    check('coverage screen: opens on the catalogue and shows the totals', /40/.test(await page.locator(`${M} #dcSummary`).innerText()) && /33/.test(await page.locator(`${M} #dcSummary`).innerText()), await page.locator(`${M} #dcSummary`).innerText());
    check('coverage screen: level 1 is series cards (only the incomplete one by default)', (await page.locator(`${M} .dc-series-card`).count()) === 1, String(await page.locator(`${M} .dc-series-card`).count()));
    check('coverage screen: no entry list at level 1', (await page.locator(`${M} .dc-entry`).count()) === 0);
    const ratio = await page.evaluate(sel => { const r = document.querySelector(sel).getBoundingClientRect(); return r.width / r.height; }, `${M} .dc-series-card .me-poster`);
    check('coverage screen: series cards are 16:9 (the source\'s preview format)', Math.abs(ratio - 16 / 9) < 0.05, String(ratio));
    // the pictures come through the server (never straight from the dump's site): every card ends up with a real image
    const loaded = await page.waitForFunction(sel => { const imgs = [...document.querySelectorAll(sel)]; return imgs.length > 0 && imgs.every(i => i.complete && i.naturalWidth > 0); }, `${M} .dc-series-card img`, { timeout: 15000 }).then(() => true, () => false);
    check('coverage screen: the series pictures load (served by the plugin)', loaded);
    await shot('D5-coverage-1440');
    await page.selectOption(`${M} #dcComplete`, 'all');
    await page.waitForFunction(sel => document.querySelectorAll(sel).length === 7, `${M} .dc-series-card`, { timeout: 8000 });
    check('coverage screen: "Séries : Toutes" shows the complete series too, and each lone entry as a series of one (7 cards)', (await page.locator(`${M} .dc-series-card`).count()) === 7);
    const loadedAll = await page.waitForFunction(sel => { const imgs = [...document.querySelectorAll(sel)]; return imgs.length === 7 && imgs.every(i => i.complete && i.naturalWidth > 0); }, `${M} .dc-series-card img`, { timeout: 15000 }).then(() => true, () => false);
    check('coverage screen: all the cards with a picture show it', loadedAll);
    check('coverage screen: the card of a series found in the library says so', /Dans votre médiathèque/.test(await page.locator(`${M} .dc-series-card`, { hasText: 'Série liée au dump' }).innerText()));
    await shot('D6-coverage-all-series-1440');
    await page.selectOption(`${M} #dcComplete`, 'incomplete');
    await page.waitForFunction(sel => document.querySelectorAll(sel).length === 1, `${M} .dc-series-card`, { timeout: 8000 });
    // a search over the series
    await page.fill(`${M} #dcQ`, 'zzzz-aucun');
    await page.waitForSelector(`${M} .dc-empty`, { timeout: 8000 });
    check('coverage screen: a search without result says what to do', /Retirez un filtre/.test(await page.locator(`${M} .dc-empty`).innerText()));
    await shot('D7-coverage-empty-1440');
    await page.fill(`${M} #dcQ`, '');
    await page.waitForSelector(`${M} .dc-series-card`, { timeout: 8000 });
    // open a series
    await page.locator(`${M} .dc-series-card`, { hasText: 'Épisode démo' }).click();
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { timeout: 8000 });
    check('coverage screen: a click opens the entries of the series, with a breadcrumb back', /Toutes les séries/.test(await page.locator(`${M} #dcCrumbs`).innerText()) && /Épisode démo/.test(await page.locator(`${M} #dcCrumbs`).innerText()));
    check('coverage screen: 33 missing entries over 2 pages of 24', /33 entrées manquantes/.test(await page.locator(`${M} #dcCount`).innerText()) && (await page.locator(`${M} #dcResults .dc-entry`).count()) === 24);
    check('coverage screen: the title keeps its number in the copied name', /Épisode démo \d+ \{dp-\d+\}/.test(await page.locator(`${M} #dcResults .dc-entry`).first().getAttribute('data-file')));
    const thumbRatio = await page.evaluate(sel => { const r = document.querySelector(sel).getBoundingClientRect(); return r.width / r.height; }, `${M} .dc-entry .me-thumb`);
    check('coverage screen: entry thumbnails are 16:9', Math.abs(thumbRatio - 16 / 9) < 0.05, String(thumbRatio));
    await shot('D8-coverage-series-entries-1440');
    // real mouse click on the copy button (clipboard needs a user gesture)
    const firstName = await page.locator(`${M} #dcResults .dc-entry`).first().getAttribute('data-file');
    await page.locator(`${M} #dcResults .me-copy`).first().click();
    await page.waitForTimeout(300);
    const clip = await page.evaluate(() => navigator.clipboard.readText()).catch(() => null);
    check('coverage screen: "Copier" puts "Name {dp-id}" on the clipboard', clip === firstName && /\{dp-\d+\}$/.test(clip || ''), String(clip));
    // pager
    await page.click(`${M} #dcPager [data-page="2"]`);
    await page.waitForFunction(sel => /Page 2 sur 2/.test(document.querySelector(sel).textContent), `${M} #dcPager`, { timeout: 8000 });
    check('coverage screen: the second page holds the 9 others', (await page.locator(`${M} #dcResults .dc-entry`).count()) === 9);
    await page.click(`${M} #dcPager [data-page="1"]`);
    await page.waitForFunction(sel => /Page 1 sur 2/.test(document.querySelector(sel).textContent), `${M} #dcPager`, { timeout: 8000 });
    // list view: the 16:9 thumbnail is not cropped to a strip, and the tag stays visible
    await page.click(`${M} #dcView [data-view="list"]`);
    await page.waitForTimeout(300);
    const listRatio = await page.evaluate(sel => { const r = document.querySelector(sel).getBoundingClientRect(); return r.width / r.height; }, `${M} .me-list .me-thumb`);
    check('coverage screen: in the list, thumbnails stay 16:9', Math.abs(listRatio - 16 / 9) < 0.05, String(listRatio));
    const tagVisible = await page.evaluate(sel => { const t = document.querySelector(sel + ' .dc-fn-tag'); const code = t.closest('code'); return t.getBoundingClientRect().right <= code.getBoundingClientRect().right + 1; }, `${M} #dcResults .me-list`);
    check('coverage screen: in the list, a long name gives way before its tag', tagVisible);
    await shot('D9-coverage-list-1440');
    await page.click(`${M} #dcView [data-view="cards"]`);
    // back to the series
    await page.click(`${M} #dcCrumbs [data-nav="series"]`);
    await page.waitForSelector(`${M} .dc-series-card`, { timeout: 8000 });
    check('coverage screen: the breadcrumb goes back to the series cards', (await page.locator(`${M} .dc-entry`).count()) === 0);
    // the recognition filter: the series that hold a poorly recognised file, then that file with its name to fix
    await page.locator(`${M} [data-score-preset="uncertain"]`).click();
    await page.waitForFunction(sel => /Pire reconnaissance/.test(document.querySelector(sel)?.innerText || ''), `${M} #dcResults`, { timeout: 8000 });
    check('coverage screen: the "Pas certains" preset (0 to 99) keeps the series that hold a file not recognised for sure, and lifts "incomplete only"', /Pire reconnaissance : \d+ %/.test(await page.locator(`${M} .dc-series-card`).innerText()));
    await shot('D13-coverage-score-filter-1440');
    await page.locator(`${M} .dc-series-card`).click();
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { timeout: 8000 });
    const weakCard = await page.locator(`${M} #dcResults .dc-entry`).innerText();
    check('coverage screen: that entry shows how it was recognised and the library file name to fix', /Reconnu par ressemblance : \d+ %/.test(weakCard) && /The Last Lighthouse\.mkv/.test(weakCard), weakCard.slice(0, 200));
    await shot('D14-coverage-weak-entry-1440');
    await page.click(`${M} #dcCrumbs [data-nav="series"]`);
    await page.waitForSelector(`${M} .dc-series-card`, { timeout: 8000 });
    // the interval, typed then with the sliders; the chip clears it
    await page.locator(`${M} #dcScoreMaxNum`).fill('80');
    await page.locator(`${M} #dcScoreMinNum`).fill('70');
    await page.locator(`${M} #dcScoreMinNum`).dispatchEvent('change');
    await page.waitForFunction(sel => /de 70 à 80 %/.test(document.querySelector(sel).textContent), `${M} #dcRangeOut`, { timeout: 8000 });
    await page.waitForFunction(sel => document.querySelectorAll(sel).length === 1, `${M} .dc-series-card`, { timeout: 8000 });
    check('coverage screen: an interval typed in the two fields (70 to 80 %) keeps the series with a file in it, the sliders follow', (await page.inputValue(`${M} #dcScoreMin`)) === '70' && (await page.inputValue(`${M} #dcScoreMax`)) === '80' && /Reconnu entre 70 et 80 %/.test(await page.locator(`${M} #dcChips`).innerText()));
    await page.locator(`${M} #dcScoreMin`).fill('90');
    await page.waitForFunction(sel => /de 80 à 80 %|exactement 80 %/.test(document.querySelector(sel).textContent), `${M} #dcRangeOut`, { timeout: 8000 });
    check('coverage screen: the minimum slider cannot go above the maximum (it pushes it back)', (await page.inputValue(`${M} #dcScoreMin`)) === '80' && (await page.inputValue(`${M} #dcScoreMax`)) === '80');
    await page.waitForFunction(sel => document.querySelector(sel).querySelectorAll('.dc-series-card').length === 0, `${M} #dcResults`, { timeout: 8000 });
    await shot('D13b-coverage-interval-1440');
    await page.locator(`${M} .dc-chip`).click();
    await page.waitForFunction(sel => document.querySelectorAll(sel).length >= 1, `${M} .dc-series-card`, { timeout: 8000 });
    check('coverage screen: clearing the score chip resets the interval to 0-100', (await page.inputValue(`${M} #dcScoreMinNum`)) === '0' && (await page.inputValue(`${M} #dcScoreMaxNum`)) === '100');
    // the release-date filter exists at BOTH levels (it used to exist only inside a series)
    check('coverage screen: the release-date filter is there at the series level', await page.locator(`${M} #dcYearFrom`).isVisible() && await page.locator(`${M} #dcYearTo`).isVisible());
    await page.selectOption(`${M} #dcYearFrom`, '2023');
    await page.selectOption(`${M} #dcYearTo`, '2023');
    await page.waitForFunction(sel => /Sorti en 2023/.test(document.querySelector(sel).textContent), `${M} #dcChips`, { timeout: 8000 });
    await page.waitForFunction(sel => !/Sentiers/.test(document.querySelector(sel).innerText) && /Dernier Phare/.test(document.querySelector(sel).innerText), `${M} #dcResults`, { timeout: 8000 });
    check('coverage screen: 2023 keeps the series released that year and drops the others', /Dernier Phare/.test(await page.locator(`${M} #dcResults`).innerText()) && !/Sentiers/.test(await page.locator(`${M} #dcResults`).innerText()));
    await shot('D13c-coverage-year-filter-1440');
    await page.selectOption(`${M} #dcYearFrom`, '2024');
    await page.waitForFunction(sel => document.querySelector(sel).value === '2024', `${M} #dcYearTo`, { timeout: 8000 });
    check('coverage screen: a start year after the end year pulls the end back to it', (await page.inputValue(`${M} #dcYearFrom`)) === '2024' && (await page.inputValue(`${M} #dcYearTo`)) === '2024');
    await page.locator(`${M} .dc-chip`).click();
    await page.waitForFunction(sel => document.querySelector(sel).value === '' && true, `${M} #dcYearFrom`, { timeout: 8000 });
    check('coverage screen: clearing the date chip resets both years', (await page.inputValue(`${M} #dcYearFrom`)) === '' && (await page.inputValue(`${M} #dcYearTo`)) === '');
    // the sort options: release date and score
    const sortOptions = await page.$$eval(`${M} #dcSeriesSort option`, os => os.map(o => o.value).join());
    check('coverage screen: the series can be sorted by release date and by recognition', sortOptions === 'missing,name,progress,datenew,dateold,score,scoredesc', sortOptions);
    await page.selectOption(`${M} #dcSeriesSort`, 'datenew');
    await page.waitForTimeout(600);
    check('coverage screen: a series card shows its years', /20\d\d/.test(await page.locator(`${M} .dc-series-card`).first().innerText()), await page.locator(`${M} .dc-series-card`).first().innerText());
    await page.selectOption(`${M} #dcSeriesSort`, 'missing');
    // the group filter works at level 1
    await page.selectOption(`${M} #dcGroup`, 'Delta Prod');
    await page.waitForFunction(sel => /Groupe : Delta Prod/.test(document.querySelector(sel).textContent), `${M} #dcChips`, { timeout: 8000 });
    check('coverage screen: a group filter adds a removable chip', (await page.locator(`${M} .dc-chip`).count()) === 1);
    await page.locator(`${M} .dc-chip`).click();
    await page.waitForFunction(sel => document.querySelector(sel).children.length === 0, `${M} #dcChips`);
    // the series view is still there
    await page.click(`${M} #modeSwitch [data-mode="series"]`);
    check('mode switch: back to the series view', await page.locator(`${M} #seriesView`).isVisible() && !(await page.locator(`${M} #dumpView`).isVisible()));
    await page.click(`${M} #modeSwitch [data-mode="dump"]`);
    await page.waitForSelector(`${M} .dc-series-card`, { timeout: 8000 });

    // portrait pictures (2:3): narrow cards, the picture keeps its exact shape instead of being cut into a square
    await api('POST', `/Plugins/${PLUGIN}/Configuration`, { ...config, Sources: config.Sources.map(x => x.Prefix === 'dp' ? { ...x, Dump: { ...x.Dump, PreviewRatio: '2:3' } } : x) });
    await page.evaluate(() => sessionStorage.setItem('cm-missing-dump', 'dp'));
    await open('CustomMetadataMissingEpisodes', M, '.cmn-nav');
    await page.waitForSelector(`${M} .dc-series-card`, { state: 'visible', timeout: 30000 });
    await page.locator(`${M} .dc-series-card`, { hasText: 'Épisode démo' }).click();
    await page.waitForSelector(`${M} #dcResults .dc-entry`, { timeout: 8000 });
    const portrait = await page.evaluate(sel => { const r = document.querySelector(sel).getBoundingClientRect(); const c = document.querySelector(sel).closest('.dc-entry').getBoundingClientRect(); return { ratio: r.width / r.height, card: c.width }; }, `${M} .dc-entry .me-thumb`);
    check('2:3 format: entry thumbnails are really 2:3 (not squeezed into a square)', Math.abs(portrait.ratio - 2 / 3) < 0.03, JSON.stringify(portrait));
    check('2:3 format: entry cards are narrow, several per row', portrait.card < 320 && (await page.locator(`${M} #dcResults .dc-entry`).evaluateAll(els => new Set(els.map(e => Math.round(e.getBoundingClientRect().top))).size < els.length)), JSON.stringify(portrait));
    await page.waitForTimeout(1500);
    await shot('D12-coverage-portrait-1440');
    await page.click(`${M} #dcView [data-view="list"]`);
    const portraitList = await page.evaluate(sel => { const r = document.querySelector(sel).getBoundingClientRect(); return r.width / r.height; }, `${M} .me-list .me-thumb`);
    check('2:3 format: the list keeps 2:3 thumbnails', Math.abs(portraitList - 2 / 3) < 0.03, String(portraitList));
    await page.click(`${M} #dcView [data-view="cards"]`);
    await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);

    // widths
    await page.setViewportSize({ width: 1920, height: 1100 }); await page.waitForTimeout(500); await shot('D10-coverage-1920');
    await page.setViewportSize({ width: 412, height: 900 }); await page.waitForTimeout(500); await shot('D11-coverage-mobile');
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

  // ---- 8. the EPISODE path: files of a series library with no tag, recognised by their name
  const episodes = async () => ((await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&Fields=ProviderIds,Path&userId=${auth.User.Id}`)).json?.Items || []).filter(e => /seriesdump/.test(e.Path || ''));
  const typed = t => ({ Type: t, MetadataFetchers: ['Custom Metadata Provider'], MetadataFetcherOrder: ['Custom Metadata Provider'], ImageFetchers: ['Custom Image Provider'], ImageFetcherOrder: ['Custom Image Provider'] });
  await api('DELETE', '/Library/VirtualFolders?name=DumpSeries&refreshLibrary=true');
  const slib = await api('POST', '/Library/VirtualFolders?name=DumpSeries&collectionType=tvshows&refreshLibrary=true', {
    LibraryOptions: { EnableRealtimeMonitor: false, PathInfos: [{ Path: '/media/seriesdump' }], TypeOptions: [typed('Series'), typed('Season'), typed('Episode')], SaveLocalMetadata: false, EnableChapterImageExtraction: false }
  });
  seriesLibraryCreated = slib.status < 300;
  check('a series library over /media/seriesdump (no tag anywhere) is created', seriesLibraryCreated, String(slib.status));
  let eps = [];
  for (let i = 0; i < 60; i++) {
    eps = await episodes();
    if (eps.length >= 2 && eps.every(e => Object.keys(e.ProviderIds || {}).some(k => k.toLowerCase() === 'custom'))) { break; }
    // an item scanned before an earlier run's cleanup is not refreshed by itself: force it
    if (i === 15) { for (const e of eps) { await api('POST', `/Items/${e.Id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=true&ReplaceAllImages=false`); } }
    await wait(2000);
  }
  check('2 episodes scanned', eps.length === 2, String(eps.length));
  const byName = n => eps.find(e => e.Name === n);
  check('episode with no tag: "Épisode démo 10 S01E01" → the dump entry 1010 (id stored, title from the dump)', !!byName('Épisode démo 10') && Object.values(byName('Épisode démo 10').ProviderIds || {}).includes('dp-1010'), JSON.stringify(eps.map(e => [e.Name, e.ProviderIds])));
  check('episode with no tag: "Épisode démo 11 S01E02" → 1011', !!byName('Épisode démo 11') && Object.values(byName('Épisode démo 11').ProviderIds || {}).includes('dp-1011'), JSON.stringify(eps.map(e => [e.Name, e.ProviderIds])));
  let log = '';
  try { log = execSync('docker logs cm-uitest 2>&1', { maxBuffer: 256 * 1024 * 1024 }).toString(); } catch (e) { log = String(e.stdout || ''); }
  check('the server log says it: "EPISODE PROVIDER ... Matched in the dump" for each file', (log.match(/EPISODE PROVIDER\] 📦 Matched in the dump/g) || []).length >= 2, 'none found');
  check('the server log also says when nothing matches (Information level, not Debug)', /\[DUMP\] ❔ No match for 'Documentaire inconnu sans rapport/.test(log));
  // ---- 9. targeted refresh of a dump: files with NO tag, chosen libraries
  const libs = (await api('GET', '/Library/VirtualFolders')).json || [];
  const libId = name => libs.find(l => l.Name === name)?.ItemId;
  const runRefresh = async body => {
    await api('POST', '/CustomMetadata/Refresh', body);
    let st = null;
    for (let i = 0; i < 90; i++) { await wait(1000); st = (await api('GET', '/CustomMetadata/Refresh/Status')).json; if (st && !pick(st, 'Running')) { break; } }
    return st;
  };
  // a title changed by hand in Jellyfin comes back from the dump: the refresh really writes the item
  const victim = (await movies()).find(m => Object.values(m.ProviderIds || {}).includes('dp-1002'));
  const full = (await api('GET', `/Items/${victim.Id}?userId=${auth.User.Id}`)).json;
  await api('POST', `/Items/${victim.Id}`, { ...full, Name: 'Titre changé à la main', Overview: 'Résumé changé à la main' });
  const changed = (await movies()).find(m => m.Id === victim.Id);
  check('setup: the title was changed by hand', changed?.Name === 'Titre changé à la main', String(changed?.Name));
  const stAll = await runRefresh({ Prefix: 'dp', Types: ['episode', 'movie'], ReplaceImages: false, LibraryIds: [libId('Dump')] });
  check('dump refresh, one library: looks at its 7 files, recognises 6 (the tagged one, by name, by id…), refreshes them', pick(stAll, 'Scanned') === 7 && pick(stAll, 'Matched') === 6 && pick(stAll, 'Total') === 6 && pick(stAll, 'Done') === 6, JSON.stringify(stAll));
  const restored = (await movies()).find(m => m.Id === victim.Id);
  check('dump refresh: the title and the overview come back from the dump', restored?.Name === 'Les Sentiers du Nord' && /Synopsis de Les Sentiers/.test(restored?.Overview || ''), JSON.stringify([restored?.Name, restored?.Overview]));
  const stSeries = await runRefresh({ Prefix: 'dp', Types: ['episode'], ReplaceImages: false, LibraryIds: [libId('DumpSeries')] });
  check('dump refresh, the series library: its 2 untagged episodes are found by name', pick(stSeries, 'Scanned') === 2 && pick(stSeries, 'Matched') === 2 && pick(stSeries, 'Done') === 2, JSON.stringify(stSeries));
  const stNone = await runRefresh({ Prefix: 'dp', Types: ['movie'], ReplaceImages: false, LibraryIds: [libId('DumpSeries')] });
  check('dump refresh: a type with no file in the chosen library says nothing to do', pick(stNone, 'Total') === 0 && !pick(stNone, 'Running'), JSON.stringify(stNone));
  log = ''; try { log = execSync('docker logs cm-uitest 2>&1', { maxBuffer: 256 * 1024 * 1024 }).toString(); } catch (e) { log = String(e.stdout || ''); }
  check('the log of a dump entry says it is read from memory (no request to the dump\'s site)', /\[DUMP\] Entry \d+ of 'Démo dump' read from the catalogue in memory/.test(log), 'old wording still there');
  check('the log gives the time of each item, written directly from the dump (no Jellyfin refresh)', /SOURCE REFRESH\] ⏱️ '[^']+': \d+ ms, written directly from the dump \(metadata \d+ ms, images not replaced\)/.test(log), 'no timing line');
  check('the log tells the refresh: files looked at, files recognised, done', /SOURCE REFRESH\] 📦 Dump 'Démo dump' \(prefix 'dp'\): looking at 7 file/.test(log) && /6 of 7 file\(s\) recognised/.test(log) && /Done for dump prefix 'dp': 6\/6/.test(log));
  check('the server log says it: "[DUMP] 🎯 … → …" lines', /\[DUMP\] 🎯 'Épisode démo 10 S01E01/.test(log));
} finally {
  if (seriesLibraryCreated) {
    await api('DELETE', '/Library/VirtualFolders?name=DumpSeries&refreshLibrary=true');
  }
  if (libraryCreated) {
    await api('DELETE', '/Library/VirtualFolders?name=Dump&refreshLibrary=true');
  }
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
}
console.log(`\n${pass} PASS, ${fail} FAIL`);
process.exit(fail ? 1 : 0);
