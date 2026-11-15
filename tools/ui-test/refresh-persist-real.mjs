// "Actualiser les éléments" must WRITE the images it applies into Jellyfin's database, not only into the item held in memory.
// Scenario of the bug: a classic series with poster.jpg + backdrop.jpg in its folder, the folder renamed with a local source's tag,
// the two files moved into Primary/ and Backdrop/ sub-folders, imported into the source's gallery, then the targeted refresh.
// The library's image fetchers are switched off for the run, so Jellyfin's own refresh brings nothing and the plugin has to apply
// the images itself: that path used to leave the database without any image (they vanished when Jellyfin reloaded the item).
// The check reads the database file (read-only) right after the refresh: the API alone answers from memory and would not show it.
// Usage: node refresh-persist-real.mjs <base_url> <series_root_dir> <images_dir> <jellyfin.db> [user] [password]
//   series_root_dir: the host folder of the TV library (this script writes in it; Jellyfin only reads it)
//   images_dir: a folder with poster1.jpg and back1.jpg
// Restores the plugin configuration and the library options, removes its folder. Prints PASS / FAIL.
import fs from 'node:fs';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';

const [J, ROOT, IMAGES, DB, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !ROOT || !IMAGES || !DB) { console.error('usage: node refresh-persist-real.mjs <base_url> <series_root_dir> <images_dir> <jellyfin.db> [user] [password]'); process.exit(2); }
const PLUGIN = '12345678-1234-1234-1234-123456789abc';
const DATA = 'persisttest00001';
const PLAIN = path.join(ROOT, 'Persist test'), TAGGED = path.join(ROOT, 'Persist test {pt-ser1}');
const wait = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, extra = '') => { console.log((ok ? 'PASS' : 'FAIL') + ' ' + name + (ok ? '' : '  ' + extra)); if (!ok) failed++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-persist", Version="1.0"' },
  body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => { const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const scan = async () => { await api('POST', '/Library/Refresh'); await wait(6000);
  for (let i = 0; i < 80; i++) { if ((await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshLibrary')?.State === 'Idle') break; await wait(1500); } await wait(2000); };
const series = async () => ((await api('GET', `/Items?Recursive=true&IncludeItemTypes=Series&Fields=Path&userId=${auth.User.Id}`)).Items || []).find(i => /Persist test/.test(i.Path || ''));
// What the database holds for the series: [{ImageType, Path}] (0 = Primary, 2 = Backdrop)
const stored = () => { const db = new DatabaseSync(DB, { readOnly: true });
  try { return db.prepare("select im.ImageType as t, im.Path as p from BaseItems i join BaseItemImageInfos im on im.ItemId = i.Id where i.Path like '%Persist test {pt-ser1}'").all(); } finally { db.close(); } };

const original = await api('GET', `/Plugins/${PLUGIN}/Configuration`);
const library = (await api('GET', '/Library/VirtualFolders')).find(l => l.CollectionType === 'tvshows');
const setOptions = o => fetch(`${J}/Library/VirtualFolders/LibraryOptions`, { method: 'POST', headers: H, body: JSON.stringify({ Id: library.ItemId, LibraryOptions: o }) });
try {
  // No image fetcher in the library: the plugin's own "apply" is the only thing that can bring the images
  const options = JSON.parse(JSON.stringify(library.LibraryOptions));
  options.SaveLocalMetadata = false;
  for (const t of options.TypeOptions || []) { t.ImageFetchers = []; t.ImageFetcherOrder = []; }
  await setOptions(options);

  for (const d of [PLAIN, TAGGED]) fs.rmSync(d, { recursive: true, force: true });
  fs.mkdirSync(path.join(PLAIN, 'Saison 1'), { recursive: true });
  execSync(`ffmpeg -loglevel error -y -f lavfi -i color=c=gray:s=320x180:d=2 -c:v libx264 -t 2 "${path.join(PLAIN, 'Saison 1', 'E01 Un.mkv')}"`);
  fs.copyFileSync(path.join(IMAGES, 'poster1.jpg'), path.join(PLAIN, 'poster.jpg'));
  fs.copyFileSync(path.join(IMAGES, 'back1.jpg'), path.join(PLAIN, 'backdrop.jpg'));
  await scan();
  check('classic series: Jellyfin shows its poster.jpg', !!((await api('GET', `/Items/${(await series())?.Id}?userId=${auth.User.Id}`)).ImageTags || {}).Primary);

  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'ser1', Title: 'Persist locale', Seasons: [] }] });
  const config = JSON.parse(JSON.stringify(original));
  config.Sources = config.Sources.filter(s => s.LocalDataId !== DATA);
  config.Sources.push({ Name: 'Persist test', Prefix: 'pt', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  fs.renameSync(PLAIN, TAGGED);
  await scan();
  check('folder renamed with the tag: the title comes from the local source', (await series())?.Name === 'Persist locale', (await series())?.Name);

  fs.mkdirSync(path.join(TAGGED, 'Primary')); fs.mkdirSync(path.join(TAGGED, 'Backdrop'));
  fs.renameSync(path.join(TAGGED, 'poster.jpg'), path.join(TAGGED, 'Primary', 'poster.jpg'));
  fs.renameSync(path.join(TAGGED, 'backdrop.jpg'), path.join(TAGGED, 'Backdrop', 'backdrop.jpg'));
  const imported = await api('POST', '/CustomMetadata/FolderImages/Import', { Tag: 'pt-ser1', DataId: DATA, NodeId: 'ser1' });
  check('the two folder images are imported', (imported.Images || []).length === 2, JSON.stringify(imported));
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'ser1', Title: 'Persist locale', Seasons: [],
    Gallery: (imported.Images || []).map(i => ({ Type: i.Type, Url: i.Url, Name: i.Name })) }] });

  const started = await api('POST', '/CustomMetadata/Refresh', { Prefix: 'pt', Types: [], ReplaceImages: true });
  check('the refresh finds the series', started.Total === 1, JSON.stringify(started));
  let status = started;
  for (let i = 0; i < 90 && (status.Running || i === 0); i++) { await wait(1000); status = await api('GET', '/CustomMetadata/Refresh/Status'); }
  check('the refresh ends without failure', !status.Running && status.Failed === 0, JSON.stringify(status));

  // The database, read before anything else touches the item
  const rows = stored();
  check('database: the Primary image is stored', rows.some(r => r.t === 0), JSON.stringify(rows));
  check('database: the Backdrop image is stored', rows.some(r => r.t === 2), JSON.stringify(rows));
  check('database: no image points to the moved files', !rows.some(r => /Persist test \{pt-ser1\}\/(poster|backdrop)\.jpg$/.test(r.p)), JSON.stringify(rows));
  const item = await api('GET', `/Items/${(await series())?.Id}?userId=${auth.User.Id}`);
  check('API: the series has a Primary image and one backdrop', !!(item.ImageTags || {}).Primary && (item.BackdropImageTags || []).length === 1, JSON.stringify([item.ImageTags, item.BackdropImageTags]));
  check('no old image file reported as kept', !status.LocalFilesKept, String(status.LocalFilesKept));
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  await setOptions(library.LibraryOptions);
  for (const d of [PLAIN, TAGGED]) fs.rmSync(d, { recursive: true, force: true });
  await api('POST', '/Library/Refresh');
  console.log(failed ? `${failed} check(s) FAILED` : 'ALL PASS');
  process.exit(failed ? 1 : 0);
}
