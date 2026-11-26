// Gives the test Jellyfin the variety a theme must survive: long synopses with and without tags, taglines, many tags,
// genres, studios, ratings, an episode in progress, a watched one, missing episodes shown, a movie library (movies without
// any image, one with a very long title), a series with a long title and no artwork at all, and a 32-arc series
// (One Piece-like) for season layouts, and a 60 s episode with trickplay thumbnails for the hover preview.
// Usage: node theme-seed.mjs <base_url>     (idempotent: run it before theme-audit.mjs; nothing is locked, so a plugin
// refresh may overwrite the texts, and the next run puts them back)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [J] = process.argv.slice(2);
if (!J) { console.error('usage: node theme-seed.mjs <base_url>'); process.exit(2); }
const WORK = process.env.CM_UI_WORK || path.join(os.homedir(), '.cache/cm-ui-test');
const wait = ms => new Promise(r => setTimeout(r, ms));

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="theme-seed", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const U = auth.User.Id;
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const t = await r.text();
  if (!r.ok) { throw new Error(`${method} ${url} → ${r.status} ${t.slice(0, 200)}`); }
  return t ? JSON.parse(t) : null;
};
const items = q => api('GET', `/Items?userId=${U}&Recursive=true&${new URLSearchParams(q)}`).then(r => r.Items);
const update = async (id, patch) => {
  const it = await api('GET', `/Items/${id}?userId=${U}`);
  await api('POST', `/Items/${id}`, { ...it, LockData: false, ...patch });
};

const LONG = [
  "Après des semaines de préparation, l'équipe atteint enfin le camp de base. La météo se dégrade dès la première nuit et le guide doit choisir entre attendre une fenêtre favorable ou redescendre.",
  "Les tensions montent entre les membres de l'expédition, chacun ayant ses raisons de vouloir continuer. Au petit matin, une décision s'impose, qui changera la suite du voyage pour tous.",
  "Tourné sur plus de deux ans entre les Alpes, le Karakoram et la Patagonie, ce récit suit des femmes et des hommes ordinaires face à des choix qui ne le sont pas.",
].join('\n\n');
const TAGS = ['Aventure', 'Montagne', 'Reportage', 'Hiver', 'Expédition', 'Haute altitude', 'Documentaire d’auteur', 'Tourné en 4K', 'Récompensé'];

// 1. the user shows missing episodes (the maintainer does)
const user = await api('GET', `/Users/${U}`);
if (!user.Configuration.DisplayMissingEpisodes) { await api('POST', `/Users/${U}/Configuration`, { ...user.Configuration, DisplayMissingEpisodes: true }); }

// 2. texts on the main series, its first season and two episodes
const series = (await items({ IncludeItemTypes: 'Series' }));
const adv = series.find(s => s.Name === 'Les Aventuriers');
if (adv) {
  await update(adv.Id, { Overview: LONG, Taglines: ['Plus haut, plus loin, ensemble'], Tags: TAGS, Genres: ['Documentaire', 'Aventure', 'Nature'], Studios: [{ Name: 'Studio Altitude' }, { Name: 'Nord Films' }], OfficialRating: 'TP', CommunityRating: 7.8, ProductionYear: 2023, PremiereDate: '2023-01-15T00:00:00Z' });
  const seasons = await items({ ParentId: adv.Id, IncludeItemTypes: 'Season' });
  const s2023 = seasons.find(s => s.Name === 'Saison 2023') || seasons[0];
  await update(s2023.Id, { Overview: 'La première saison suit la préparation et le départ de l’expédition, du recrutement de l’équipe aux premiers jours sur la glace.', Tags: [], Genres: [] });
  const eps = await items({ ParentId: s2023.Id, IncludeItemTypes: 'Episode' });
  if (eps[0]) { await update(eps[0].Id, { Overview: LONG.split('\n\n').slice(0, 2).join('\n\n'), Tags: TAGS.slice(0, 5), Genres: ['Documentaire', 'Aventure'], Taglines: ['Le départ'] }); }
  if (eps[1]) { await update(eps[1].Id, { Overview: LONG, Tags: [], Genres: [], Taglines: [] }); }
  // an episode of another season watched, one of Tech Talks in progress
  if (eps[0]) { await api('POST', `/UserPlayedItems/${eps[0].Id}?userId=${U}`); }
}
const tech = series.find(s => s.Name === 'Tech Talks');
if (tech) {
  const ep = (await items({ ParentId: tech.Id, IncludeItemTypes: 'Episode', IsMissing: false, Fields: 'RunTimeTicks' }))[1];
  if (ep) { await api('POST', `/UserItems/${ep.Id}/UserData?userId=${U}`, { PlaybackPositionTicks: Math.round((ep.RunTimeTicks || 20000000) * 0.4) }); }
}

// 3. a movie library and a series with a long title, no artwork at all (files copied from a demo episode)
const sample = fs.readdirSync(path.join(WORK, 'media/series/Les Aventuriers {dm-aventuriers}/Saison 2023 {dm-a3}')).find(f => f.endsWith('.mkv'));
const src = path.join(WORK, 'media/series/Les Aventuriers {dm-aventuriers}/Saison 2023 {dm-a3}', sample);
const files = [
  'films/Le Grand Voyage (2021)/Le Grand Voyage (2021).mkv',
  'films/Les Chroniques improbables d’un voyage au bout du monde et du retour à la maison (2019)/film.mkv',
  'theme-series/Une série au titre vraiment très long pour vérifier les retours à la ligne/Season 1/S01E01.mkv',
  'theme-series/Une série au titre vraiment très long pour vérifier les retours à la ligne/Season 1/S01E02.mkv',
];
let created = false;
for (const f of files) { const dst = path.join(WORK, 'media', f); if (!fs.existsSync(dst)) { fs.mkdirSync(path.dirname(dst), { recursive: true }); fs.copyFileSync(src, dst); created = true; } }
const folders = await api('GET', '/Library/VirtualFolders');
const addLib = async (name, type, p) => {
  if (folders.some(f => f.Name === name)) { return; }
  await api('POST', `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=${type}&refreshLibrary=true`, { LibraryOptions: { EnableRealtimeMonitor: false, EnableChapterImageExtraction: false, PathInfos: [{ Path: p }] } });
  created = true;
};
await addLib('Films', 'movies', '/media/films');
await addLib('Séries (thème)', 'tvshows', '/media/theme-series');
if (created) {
  await api('POST', '/Library/Refresh');
  for (let i = 0; i < 60; i++) { await wait(2000); const m = await items({ IncludeItemTypes: 'Movie' }); if (m.length >= 2) { break; } }
  await wait(3000);
}
const movies = await items({ IncludeItemTypes: 'Movie', Fields: 'Overview' });
for (const m of movies) {
  if (m.Name.startsWith('Le Grand Voyage')) {
    await update(m.Id, { Overview: LONG, Taglines: ['Un voyage, mille détours'], Tags: TAGS.slice(2, 8), Genres: ['Aventure', 'Comédie dramatique'], Studios: [{ Name: 'Nord Films' }], OfficialRating: '12', CommunityRating: 6.9, CriticRating: 81, ProductionYear: 2021 });
  }
}
// 4. a long-running series split by arc, the reference case for season layouts (One Piece: ~30 arcs). 32 seasons of
// two episodes, arc names, generated posters; the first 26 arcs watched, the 27th started
const ARCS = ['Romance Dawn', 'Orange Town', 'Village de Sirop', 'Baratie', 'Arlong Park', 'Loguetown', 'Île de Warship', 'Reverse Mountain', 'Whisky Peak', 'Little Garden', 'Royaume de Drum', 'Alabasta', 'Jaya', 'Skypiea', 'Long Ring Long Land', 'Water Seven', 'Enies Lobby', 'Thriller Bark', 'Archipel des Sabaody', 'Amazon Lily', 'Impel Down', 'Marineford', 'Après-guerre', 'Retour à Sabaody', 'Île des Hommes-Poissons', 'Punk Hazard', 'Dressrosa', 'Zou', 'Whole Cake Island', 'Rêverie', 'Pays des Wa', 'Egghead'];
const OPDIR = path.join(WORK, 'media/theme-series/One Piece (démo)');
let opNew = false;
ARCS.forEach((_, i) => { for (const e of [1, 2]) { const n = String(i + 1).padStart(2, '0'); const dst = path.join(OPDIR, `Season ${n}`, `S${n}E0${e}.mkv`); if (!fs.existsSync(dst)) { fs.mkdirSync(path.dirname(dst), { recursive: true }); fs.copyFileSync(src, dst); opNew = true; } } });
if (opNew) {
  await api('POST', '/Library/Refresh');
  for (let i = 0; i < 90; i++) { await wait(2000); const s = (await items({ IncludeItemTypes: 'Series' })).find(x => x.Name.startsWith('One Piece')); if (s && (await items({ ParentId: s.Id, IncludeItemTypes: 'Season' })).length >= ARCS.length) { break; } }
}
const op = (await items({ IncludeItemTypes: 'Series' })).find(x => x.Name.startsWith('One Piece'));
if (op) {
  await update(op.Id, { Overview: 'Monkey D. Luffy prend la mer pour devenir le roi des pirates. Série de démonstration découpée par arcs, pour tester l’affichage d’une série aux nombreuses saisons.', Genres: ['Animation', 'Aventure'], ProductionYear: 1999 });
  const posters = path.join(WORK, 'img-theme'); fs.mkdirSync(posters, { recursive: true });
  const { execFileSync } = await import('node:child_process');
  execFileSync('python3', ['-c', `
import sys, json, os
from PIL import Image, ImageDraw, ImageFont
out, names = sys.argv[1], json.loads(sys.argv[2])
f = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 34)
cols = [(196,72,52),(52,120,180),(60,150,110),(170,120,40),(120,80,160),(40,140,150),(180,90,120)]
for i, n in enumerate(names):
    p = os.path.join(out, f"op{i+1:02d}.png")
    if os.path.exists(p): continue
    w, h = 400, 600; im = Image.new("RGB", (w, h)); d = ImageDraw.Draw(im); c = cols[i % len(cols)]
    for y in range(h): k = 1 - y / h * .75; d.line([(0, y), (w, y)], fill=tuple(int(v * k) for v in c))
    words, lines, cur = n.split(), [], ""
    for wd in words:
        t = (cur + " " + wd).strip()
        if d.textlength(t, font=f) > w - 40 and cur: lines.append(cur); cur = wd
        else: cur = t
    lines.append(cur)
    y0 = h / 2 - len(lines) * 22
    for k, ln in enumerate(lines): d.text(((w - d.textlength(ln, font=f)) / 2, y0 + k * 44), ln, font=f, fill=(255, 255, 255))
    d.text((24, 24), f"ARC {i+1}", font=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 22), fill=(255, 255, 255))
    im.save(p)
`, posters, JSON.stringify(ARCS)]);
  const seasonsOp = (await items({ ParentId: op.Id, IncludeItemTypes: 'Season', Fields: 'ImageTags' })).filter(s => s.IndexNumber).sort((a, b) => a.IndexNumber - b.IndexNumber);
  for (const s of seasonsOp) {
    const i = s.IndexNumber - 1; if (!ARCS[i]) { continue; }
    if (s.Name !== ARCS[i]) { await update(s.Id, { Name: ARCS[i], LockData: true, LockedFields: ['Name'] }); }
    if (!(s.ImageTags && s.ImageTags.Primary)) {
      const b64 = fs.readFileSync(path.join(posters, `op${String(i + 1).padStart(2, '0')}.png`)).toString('base64');
      await fetch(`${J}/Items/${s.Id}/Images/Primary`, { method: 'POST', headers: { ...H, 'Content-Type': 'image/png' }, body: b64 });
    }
    const eps = await items({ ParentId: s.Id, IncludeItemTypes: 'Episode' });
    const watched = i < 26 ? eps : i === 26 ? eps.slice(0, 1) : [];
    for (const e of watched) { if (!e.UserData?.Played) { await api('POST', `/UserPlayedItems/${e.Id}?userId=${U}`); } }
  }
}

// 5. an episode long enough for trickplay thumbnails (the hover preview of projection.js): 60 s of moving test
// pattern, trickplay turned on for the theme library, and Jellyfin's trickplay task run once
const tpFile = path.join(WORK, 'media/theme-series/Aperçu au survol (démo)/Season 1/S01E01.mkv');
// and a portrait one (a phone video): previews must keep its shape
const tpPortrait = path.join(WORK, 'media/theme-series/Aperçu au survol (démo)/Season 1/S01E02.mp4');
if (!fs.existsSync(tpPortrait)) {
  fs.mkdirSync(path.dirname(tpPortrait), { recursive: true });
  const { execFileSync } = await import('node:child_process');
  execFileSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=360x640:d=40:r=10', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', tpPortrait]);
  if (fs.existsSync(tpFile)) {
    await api('POST', '/Library/Refresh'); await wait(15000);
    const task = (await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshTrickplayImages');
    if (task) { await fetch(`${J}/ScheduledTasks/Running/${task.Id}`, { method: 'POST', headers: H }); }
    for (let i = 0; i < 60; i++) { await wait(3000); const t = (await api('GET', '/ScheduledTasks')).find(x => x.Key === 'RefreshTrickplayImages'); if (t && t.State === 'Idle') { break; } }
  }
}
if (!fs.existsSync(tpFile)) {
  fs.mkdirSync(path.dirname(tpFile), { recursive: true });
  const { execFileSync } = await import('node:child_process');
  execFileSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=60:r=10', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', tpFile]);
  const lib = (await api('GET', '/Library/VirtualFolders')).find(l => l.Name === 'Séries (thème)');
  if (lib) {
    await api('POST', '/Library/VirtualFolders/LibraryOptions', { Id: lib.ItemId, LibraryOptions: { ...lib.LibraryOptions, EnableTrickplayImageExtraction: true, ExtractTrickplayImagesDuringLibraryScan: true } });
  }
  await api('POST', '/Library/Refresh');
  await wait(15000);
  const task = (await api('GET', '/ScheduledTasks')).find(t => t.Key === 'RefreshTrickplayImages');
  if (task) { await fetch(`${J}/ScheduledTasks/Running/${task.Id}`, { method: 'POST', headers: H }); }
  for (let i = 0; i < 60; i++) { await wait(3000); const t = (await api('GET', '/ScheduledTasks')).find(x => x.Key === 'RefreshTrickplayImages'); if (t && t.State === 'Idle') { break; } }
}

console.log('🌱 seeded:', { series: series.length, movies: movies.map(m => m.Name), libraries: (await api('GET', '/Library/VirtualFolders')).map(f => f.Name) });
