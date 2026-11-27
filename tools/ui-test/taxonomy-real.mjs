// Rules on genres and tags in the REAL Jellyfin: REST for what Jellyfin holds, Playwright for the "Genres et tags" screen.
// Usage: node taxonomy-real.mjs <base_url> <out_dir> [user] [password]
// Imports (API source, local source, missing episode) and the application to items that already hold the terms. Cleans up after itself.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node taxonomy-real.mjs <base_url> <out_dir>'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc', DATA = 'txtest00000001';
const pick = (o, k) => o == null ? undefined : o[k] ?? o[k[0].toLowerCase() + k.slice(1)];
const wait = ms => new Promise(r => setTimeout(r, ms));
let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };

const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-taxonomy", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (method, url, body) => {
  const r = await fetch(J + url, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, json: text && text[0] !== '<' ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null };
};
const refreshDm = async () => {
  await api('POST', '/CustomMetadata/Refresh', { Prefix: 'dm', Types: ['episode'], ReplaceImages: false });
  for (let i = 0; i < 60; i++) { await wait(2000); const s = (await api('GET', '/CustomMetadata/Refresh/Status')).json; if (s && !pick(s, 'Running')) break; }
  await wait(2000);
};
const analyze = async () => {
  const before = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
  await api('POST', '/CustomMetadata/MissingEpisodes/Analyze?full=true');
  for (let i = 0; i < 120; i++) {
    await wait(1000);
    const now = pick(pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report'), 'GeneratedAt');
    if (now && now !== before) { return; }
  }
};
const episodes = async () => (await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&Fields=Path,Genres,Tags&userId=${auth.User.Id}`)).json?.Items || [];
const setTerms = async (id, genres, tags) => {
  const d = (await api('GET', `/Users/${auth.User.Id}/Items/${id}`)).json;
  d.Genres = genres; d.GenreItems = genres.map(n => ({ Name: n, Id: '00000000000000000000000000000000' }));
  d.Tags = tags; d.TagItems = tags.map(n => ({ Name: n, Id: '00000000000000000000000000000000' }));
  await api('POST', `/Items/${id}`, d);
};
const applyAndWait = async () => {
  await api('POST', '/CustomMetadata/Taxonomy/Apply');
  for (let i = 0; i < 60; i++) { await wait(1000); const s = (await api('GET', '/CustomMetadata/Taxonomy/Apply/Status')).json; if (s && !pick(s, 'Running')) return s; }
};
const text = (page, sel) => page.$eval(sel, e => e.textContent.trim());
const term = (catalog, kind, name) => (pick(catalog, 'Terms') || []).find(t => pick(t, 'Kind') === kind && pick(t, 'Key') === name.toLowerCase());

const original = (await api('GET', `/Plugins/${PLUGIN}/Configuration`)).json;
const config = JSON.parse(JSON.stringify(original));
const browser = await chromium.launch({ channel: 'chrome' });
let touched = [];
try {
  // ---- without rules, the API's terms arrive as they are (Jellyfin keeps the case variants apart)
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  await refreshDm();
  let eps = await episodes();
  const t1 = () => eps.find(e => e.Name === 'Talk #1' && (e.Path || '').includes('d1v1'));
  check('no rules: the terms of the API arrive untouched, case variants included', (t1()?.Genres || []).includes('Sponsorisé') && (t1()?.Genres || []).includes('Docu') && (t1()?.Tags || []).includes('tech'), JSON.stringify([t1()?.Genres, t1()?.Tags]));

  // ---- an episode with a file already holds terms (set by hand in Jellyfin) BEFORE any rule exists: only "Appliquer" reaches it
  // (once a rule exists, saving an item applies it at once, which is checked further down)
  const target = eps.find(e => e.Name === 'Talk #2' && (e.Path || '').includes('d1v2'));
  touched = [target.Id];
  await setTerms(target.Id, ['DOCU', 'docu', 'Sponsorisé', 'Aventure'], ['Tech', 'sponsorisé', 'Perso']);
  await wait(1500);
  // ---- the rules
  const rules = [
    { Kind: 'genre', Name: 'Sponsorisé', Ignored: true },
    { Kind: 'genre', Name: 'Docu', ReplaceWith: 'Documentaire' },
    { Kind: 'tag', Name: 'sponsorisé', Ignored: true },
    { Kind: 'tag', Name: 'tech', ReplaceWith: 'Technologie' },
    { Kind: 'genre', Name: 'Nouveau genre', Created: true },
    { Kind: 'genre', Name: 'Sans effet' }
  ];
  const saved = await api('PUT', '/CustomMetadata/Taxonomy', { Rules: rules });
  const catalog = saved.json;
  check('rules: saved, a rule that does nothing and was not created is dropped', saved.status === 200 && !term(catalog, 'genre', 'Sans effet') || (term(catalog, 'genre', 'Sans effet') && !pick(term(catalog, 'genre', 'Sans effet'), 'Ignored')), String(saved.status));
  check('catalog: a term created by hand is listed with no item', pick(term(catalog, 'genre', 'Nouveau genre'), 'Created') === true && pick(term(catalog, 'genre', 'Nouveau genre'), 'Items') === 0);
  check('catalog: the target of a rename is listed too', !!term(catalog, 'genre', 'Documentaire') && !!term(catalog, 'tag', 'Technologie'));

  const impact = (await api('GET', '/CustomMetadata/Taxonomy/Impact')).json;
  check('impact: counts the items a rule would change, the terms removed and renamed', pick(impact, 'Items') >= 1 && pick(impact, 'Removed') >= 2 && pick(impact, 'Renamed') >= 2, JSON.stringify(impact));
  const done = await applyAndWait();
  check('apply: runs in the background and reports what it rewrote', pick(done, 'Changed') >= 1 && pick(done, 'Failed') === 0, JSON.stringify(done));
  eps = await episodes();
  const after = eps.find(e => e.Id === target.Id);
  check('apply: the episode with a file holds the new terms, the others are left alone', JSON.stringify([...(after?.Genres || [])].sort()) === JSON.stringify(['Aventure', 'Documentaire']) && JSON.stringify([...(after?.Tags || [])].sort()) === JSON.stringify(['Perso', 'Technologie']), JSON.stringify([after?.Genres, after?.Tags]));
  const again = (await api('GET', '/CustomMetadata/Taxonomy/Impact')).json;
  check('apply: nothing is left to change afterwards', pick(again, 'Items') === 0, JSON.stringify(again));

  // ---- API source: imports go through the rules
  await refreshDm();
  await wait(2500);   // a managed item is rewritten right after it is saved, in the background
  eps = await episodes();
  check('import (API): an ignored genre is dropped, a renamed one is renamed, variants merge', JSON.stringify([...(t1()?.Genres || [])].sort()) === JSON.stringify(['Documentaire', 'Voyage']), JSON.stringify(t1()?.Genres));
  check('import (API): tags too (sponsorisé dropped in any case, tech → Technologie)', JSON.stringify([...(t1()?.Tags || [])].sort()) === JSON.stringify(['Technologie', 'conférence']), JSON.stringify(t1()?.Tags));

  // ---- local source and missing episode: the rules win over what the user typed
  dmEndpointOff(config);
  config.Sources = config.Sources.filter(s => s.Prefix !== 'ov');
  config.Sources.push({ Name: 'Termes', Prefix: 'ov', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local',
    VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, config);
  await api('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'dm-aventuriers', Title: 'Les Aventuriers', Seasons: [{ Id: 'dm-a1', Title: 'Saison 2024',
    Episodes: [{ Id: 'tx1', Title: 'Épisode avec termes', IndexNumber: 9, Tags: ['tech', 'Sponsorisé', 'sponsorisé', 'Mien'], Genres: ['docu', 'Sponsorisé', 'Voyage'] }] }] }] });
  await analyze();
  const report = pick((await api('GET', '/CustomMetadata/MissingEpisodes')).json, 'Report');
  const missing = (pick(report, 'Seasons') || []).flatMap(s => pick(s, 'Missing') || []).find(e => pick(e, 'Tag') === 'ov-tx1');
  const virtual = missing ? (await api('GET', `/Items/${pick(missing, 'EpisodeId')}?userId=${auth.User.Id}&Fields=Genres,Tags`)).json : null;
  check('local source (missing episode): the rules win over what was typed', !!virtual && JSON.stringify([...(virtual.Genres || [])].sort()) === JSON.stringify(['Documentaire', 'Voyage']) && JSON.stringify([...(virtual.Tags || [])].sort()) === JSON.stringify(['Mien', 'Technologie']), JSON.stringify([virtual?.Genres, virtual?.Tags]));
  const names = (await api('GET', '/CustomMetadata/Taxonomy/Names')).json;
  check('suggestions: not ignored, not renamed away, the created term included', (pick(names, 'genre') || []).includes('Nouveau genre') && !(pick(names, 'genre') || []).includes('Sponsorisé') && !(pick(names, 'genre') || []).includes('Docu'), JSON.stringify(pick(names, 'genre')));

  // ---- the screen, in the real jellyfin-web
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  eps = await episodes();
  const itemA = eps.find(e => e.Name === 'Talk #1' && (e.Path || '').includes('d1v1')), itemB = eps.find(e => e.Name === 'Talk #2' && (e.Path || '').includes('d1v2'));
  touched = [itemA.Id, itemB.Id];
  await setTerms(itemA.Id, ['Docu', 'Voyage'], ['Tech']);
  await setTerms(itemB.Id, ['docu', 'DOCU', 'Aventure'], ['Perso', 'Tech']);
  await wait(1500);
  const context = await browser.newContext({ viewport: { width: 1440, height: 1300 } });
  const page = await context.newPage();
  page.setDefaultTimeout(60000);
  page.on('pageerror', e => { if (!/pause is not a function/.test(e.message)) console.log('PAGEERROR', e.message.slice(0, 200)); });
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user); await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  await page.goto(`${J}/web/#/configurationpage?name=CustomMetadataTaxonomy`);
  const T = '#CustomMetadataTaxonomyPage';
  await page.waitForSelector(`${T} .tx-row[data-key]`, { state: 'visible', timeout: 60000 });
  await page.evaluate(() => { window.__cmdAnswer = 'ok'; new MutationObserver(() => { const b = document.querySelector('.cmd-backdrop [data-cmd=ok]'); if (b) b.click(); }).observe(document.documentElement, { childList: true, subtree: true }); });
  const row = key => `${T} .tx-row[data-key="${key}"]`;
  check('screen: the genres of the library are listed with their use', (await page.$$eval(`${T} .tx-row[data-key]`, els => els.map(e => e.getAttribute('data-key')))).includes('docu') && (await page.$eval(row('docu'), e => e.querySelector('.tx-counts').textContent)).startsWith('2 éléments'));
  // Jellyfin 12 keeps one spelling of a term on each item (["docu", "DOCU"] is saved as one); two items may still differ
  check('screen: Jellyfin\'s separate spellings of a term are shown together, with how to unify them', (await page.$eval(row('docu'), e => e.querySelector('.tx-variants')?.textContent || '')).includes('docu') && !!(await page.$(`${row('docu')} [data-unify]`)), (await page.$eval(row('docu'), e => e.innerText)).replace(/\s+/g, ' '));
  await shot('X1-genres-1440');

  await page.fill(`${T} #txAddName`, 'Zeste');
  await page.click(`${T} #txAddForm button[type=submit]`);
  check('screen: a created term is listed at once, marked "créé ici"', (await page.$eval(row('zeste'), e => e.textContent)).includes('créé ici') && /non enregistrée/.test(await text(page, `${T} #txDirty`)) && (await page.$eval(row('zeste'), e => e.getAttribute('data-changed'))) === 'true');
  await page.click(`${row('docu')} [data-unify]`);
  check('screen: "Unifier" turns it into a spelling rule', (await page.$eval(row('docu'), e => e.getAttribute('data-state'))) === 'replace' && (await page.$eval(`${row('docu')} .tx-target`, e => e.value)) === 'Docu' && (await page.$eval(row('docu'), e => e.textContent)).includes('orthographe fixée'));
  await page.click(`${row('voyage')} [data-act="replace"]`);
  await page.fill(`${row('voyage')} .tx-target`, 'Voyages');
  await page.check(`${row('aventure')} .tx-check`);
  await page.check(`${row('zeste')} .tx-check`);
  check('screen: selecting two terms offers to merge them', await page.isVisible(`${T} #txMerge`));
  await page.fill(`${T} #txMergeTarget`, 'Exploration');
  await page.click(`${T} #txMergeBtn`);
  check('screen: merging makes both terms point to the common name', (await page.$eval(`${row('aventure')} .tx-target`, e => e.value)) === 'Exploration' && (await page.$eval(`${row('zeste')} .tx-target`, e => e.value)) === 'Exploration');
  check('screen: the mirror button offers to copy the genre rules to the tags', await page.isVisible(`${T} #txMirror`) && /règle/.test(await text(page, `${T} #txMirror`)), await text(page, `${T} #txMirror`));
  await page.click(`${T} #txMirror`);
  check('screen: mirrored rules appear in the other list, as unsaved changes', /reportée/.test(await text(page, `${T} #txAddMsg`)) && !(await page.isVisible(`${T} #txMirror`)));
  await page.click(`${T} .tx-pill[data-kind="tag"]`);
  await page.waitForSelector(row('voyage'));
  check('screen: the tag carries the same rename as the genre', (await page.$eval(row('voyage'), e => e.getAttribute('data-state'))) === 'replace' && (await page.$eval(`${row('voyage')} .tx-target`, e => e.value)) === 'Voyages');
  await page.waitForSelector(row('perso'));
  await page.click(`${row('perso')} [data-act="ignore"]`);
  check('screen: an ignored term is struck through', (await page.$eval(row('perso'), e => e.getAttribute('data-state'))) === 'ignore');
  await page.click(`${T} .tx-pill[data-kind="genre"]`);
  check('screen: the filter chips count the changed terms and list only them', await (async () => { await page.click(`${T} [data-filter="changed"]`); const keys = await page.$$eval(`${T} .tx-row[data-key]`, els => els.map(e => e.getAttribute('data-key'))); await page.click(`${T} [data-filter=""]`); return keys.includes('voyage') && keys.includes('zeste') && keys.includes('docu'); })());
  await shot('X2-edited-1440');

  // a rename without a name is refused with the term named, not dropped in silence
  await page.click(`${row('docu')} [data-act="keep"]`);
  await page.click(`${row('docu')} [data-act="replace"]`);
  await page.click(`${T} #txSave`);
  check('screen: a rename without a new name stops the save and says which term', (await text(page, `${T} #txDirty`)).includes('nouveau nom') && (await page.$eval(`${row('docu')} .tx-target`, e => e.classList.contains('tx-bad'))));
  await page.fill(`${row('docu')} .tx-target`, 'Docu');

  await page.click(`${T} #txSave`);
  await page.waitForSelector(`${T} #txNotice:not([hidden])`, { timeout: 15000 }).catch(async () => { console.log('NO NOTICE; dirty text =', await text(page, `${T} #txDirty`)); throw new Error('the notice did not show after saving'); });
  check('screen: after saving, the notice says how many items still hold the old terms', /élément/.test(await text(page, `${T} #txNoticeText`)) && (await text(page, `${T} #txDirty`)) === 'Aucune modification', await text(page, `${T} #txNoticeText`));
  const stored = (await api('GET', '/CustomMetadata/Taxonomy')).json;
  check('screen: the rules are stored (spelling, rename, merge, ignored tag, created term)', !!term(stored, 'genre', 'Voyage') && pick(term(stored, 'genre', 'Voyage'), 'ReplaceWith') === 'Voyages' && pick(term(stored, 'genre', 'Aventure'), 'ReplaceWith') === 'Exploration' && pick(term(stored, 'tag', 'Perso'), 'Ignored') === true);
  check('screen: after saving, a renamed term folds into the row of the term it joins', !(await page.$(row('aventure'))) && (await page.$eval(row('exploration'), e => e.textContent)).includes('Fusionné ici') && !!(await page.$(`${row('exploration')} [data-unfold]`)));
  // A rename that is already saved (folded under its target) can still be copied to the other list
  check('screen: a saved, folded rename is mirrored to the tags after the tag side was undone', await (async () => {
    await page.click(`${T} .tx-pill[data-kind="tag"]`);
    await page.click(`${row('exploration')} [data-unfold]`);
    await page.click(`${T} .tx-pill[data-kind="genre"]`);
    const offered = await page.isVisible(`${T} #txMirror`);
    await page.click(`${T} #txMirror`);
    await page.click(`${T} .tx-pill[data-kind="tag"]`);
    const back = (await page.$eval(row('aventure'), e => e.getAttribute('data-state'))) === 'replace' && (await page.$eval(`${row('aventure')} .tx-target`, e => e.value)) === 'Exploration';
    await page.click(`${T} .tx-pill[data-kind="genre"]`);
    return offered && back;
  })());
  check('screen: "Masquer les ignorés" hides the ignored terms that are saved', await (async () => { await page.click(`${T} .tx-pill[data-kind="tag"]`); await page.uncheck(`${T} #txHideIgnored`); await page.uncheck(`${T} #txHideIgnored`); const before = await page.$(row('perso')); await page.check(`${T} #txHideIgnored`); const hidden = !(await page.$(row('perso'))); await page.uncheck(`${T} #txHideIgnored`); await page.click(`${T} .tx-pill[data-kind="genre"]`); await page.click(`${T} .tx-pill[data-kind="tag"]`); const back = !!(await page.$(row('perso'))); await page.click(`${T} .tx-pill[data-kind="genre"]`); return !!before && hidden && back; })());
  await shot('X3-saved-1440');
  await page.click(`${T} #txApply`);
  await page.waitForFunction(sel => /réécrit|Rien à réécrire/.test(document.querySelector(sel).textContent), `${T} #txNoticeText`, { timeout: 30000 });
  eps = await episodes();
  const a2 = eps.find(e => e.Id === itemA.Id), b2 = eps.find(e => e.Id === itemB.Id);
  check('apply from the screen: variants unified, renamed, merged, ignored tag removed', JSON.stringify([...(a2?.Genres || [])].sort()) === JSON.stringify(['Docu', 'Voyages']) && JSON.stringify([...(b2?.Genres || [])].sort()) === JSON.stringify(['Docu', 'Exploration']) && !(b2?.Tags || []).includes('Perso'), JSON.stringify([a2?.Genres, b2?.Genres, b2?.Tags]));
  check('tab bar: the Genres et tags tab counts the rules that ignore or rename', Number(await text(page, `${T} [data-cmn-count="taxonomy"]`)) >= 4, await text(page, `${T} [data-cmn-count="taxonomy"]`));
  // A rename lands on the term that already exists, and a genre can be opened (its click finds the episodes)
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  await setTerms(itemA.Id, ['Zorro'], []);
  await setTerms(itemB.Id, ['ZORRO', 'Mauvais', 'Zeb/Test9'], []);
  await wait(1500);
  const merged = (await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [{ Kind: 'genre', Name: 'Mauvais', ReplaceWith: 'zorro' }] })).json;
  check('rename onto an existing term: the target is written as the library spells it', pick(term(merged, 'genre', 'Mauvais'), 'ReplaceWith') === 'Zorro', JSON.stringify(pick(term(merged, 'genre', 'Mauvais') || {}, 'ReplaceWith')));
  check('rename onto an existing term: a spelling rule unifies the target\'s variants', pick(term(merged, 'genre', 'Zorro'), 'ReplaceWith') === 'Zorro');
  await applyAndWait();
  eps = await episodes();
  const a3 = eps.find(e => e.Id === itemA.Id), b3 = eps.find(e => e.Id === itemB.Id);
  check('rename onto an existing term: every item ends with the one spelling', JSON.stringify(a3?.Genres) === JSON.stringify(['Zorro']) && JSON.stringify([...(b3?.Genres || [])].sort()) === JSON.stringify(['Zeb/Test9', 'Zorro']), JSON.stringify([a3?.Genres, b3?.Genres]));
  const detail = (await api('GET', `/Users/${auth.User.Id}/Items/${itemA.Id}`)).json;
  const zorro = (detail.GenreItems || []).find(g => g.Name === 'Zorro');
  const clicked = (await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&GenreIds=${zorro?.Id}&userId=${auth.User.Id}`)).json;
  check('genre click: filtering on the genre id finds both episodes', pick(clicked, 'TotalRecordCount') === 2, String(pick(clicked, 'TotalRecordCount')));
  const detailB = (await api('GET', `/Users/${auth.User.Id}/Items/${itemB.Id}`)).json;
  const slash = (detailB.GenreItems || []).find(g => g.Name === 'Zeb/Test9');
  const clickedSlash = (await api('GET', `/Items?IncludeItemTypes=Episode&Recursive=true&GenreIds=${slash?.Id}&userId=${auth.User.Id}`)).json;
  check('genre click: a name with a slash finds its episode too', pick(clickedSlash, 'TotalRecordCount') === 1, String(pick(clickedSlash, 'TotalRecordCount')));
  await page.setViewportSize({ width: 412, height: 1500 });
  await page.waitForTimeout(600);
  check('phone: no horizontal scroll on the Genres et tags page', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
  await shot('X4-mobile');
  const { AxeBuilder } = require('@axe-core/playwright');
  const axe = await new AxeBuilder({ page }).include(T).exclude('.button-submit').withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  check('accessibility: no serious axe violation on the page', serious.length === 0, serious.map(v => `${v.id}: ${v.nodes.slice(0, 2).map(n => n.target.join(' ')).join(' ; ')}`).join(', '));
} catch (e) {
  console.log('ERROR', e.message.split('\n').slice(0, 3).join(' | ')); fail++;
} finally {
  await api('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await api('DELETE', `/CustomMetadata/Local/${DATA}`);
  await api('PUT', '/CustomMetadata/Taxonomy', { Rules: [] });
  for (const id of touched) { await setTerms(id, [], []); }
  await analyze();
  console.log(`\n${pass} PASS, ${fail} FAIL\nconfiguration restored`);
  await browser.close();
  process.exit(fail ? 1 : 0);
}

function dmEndpointOff(cfg) { cfg.Sources.find(s => (s.Prefix || '').toLowerCase() === 'dm').EpisodeListEndpoint = ''; }
