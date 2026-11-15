// Shared tab bar, Diagnostic page and click-to-fill mappings in the REAL jellyfin-web, driven with Playwright
// (system Google Chrome: no Playwright browser to download). Needs the test container of ui-test.sh.
// Usage: node diagnostic-real.mjs <base_url> <out_dir> [user] [password]
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const { AxeBuilder } = require('@axe-core/playwright');

const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
if (!J || !outDir) { console.error('usage: node diagnostic-real.mjs <base_url> <out_dir> [user] [password]'); process.exit(2); }
fs.mkdirSync(outDir, { recursive: true });

let pass = 0, fail = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); ok ? pass++ : fail++; };
const D = '#CustomMetadataDiagnosticPage', C = '#CustomMetadataConfigPage', M = '#CustomMetadataMissingPage', P = '#CustomMetadataPeoplePage';
const text = (page, sel) => page.$eval(sel, e => e.textContent.trim());
const url = name => `${J}/web/#/configurationpage?name=${name}`;

const browser = await chromium.launch({ channel: 'chrome' });
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, permissions: ['clipboard-read', 'clipboard-write'] });
  const page = await context.newPage();
  const shot = async name => { const f = path.join(outDir, `${name}.png`); await page.screenshot({ path: f }); console.log('📸', f); };
  // jellyfin-web keeps earlier pages in the DOM, hidden: always address the visible one
  const open = async (name, root) => { await page.goto('about:blank'); await page.goto(url(name)); await page.waitForSelector(`${root} .cmn-nav`, { state: 'visible', timeout: 30000 }); };

  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user);
  await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));

  // ---- 1. the shared tab bar on the four pages
  for (const [name, root, label] of [['Custom%20Metadata%20Provider', C, 'Configuration'], ['CustomMetadataMissingEpisodes', M, 'Épisodes manquants'], ['CustomMetadataPeople', P, 'Personnes'], ['CustomMetadataDiagnostic', D, 'Diagnostic']]) {
    await open(name, root);
    const tabs = await page.$$eval(`${root} .cmn-tab`, els => els.map(e => e.firstChild.textContent.trim()));
    check(`tab bar on ${label}: four tabs`, tabs.join('|') === 'Configuration|Épisodes manquants|Personnes|Diagnostic', tabs.join('|'));
    check(`tab bar on ${label}: current tab marked`, (await page.$eval(`${root} .cmn-tab[aria-current="page"]`, e => e.firstChild.textContent.trim())) === label);
  }
  await open('CustomMetadataMissingEpisodes', M);
  await page.waitForFunction(sel => document.querySelector(sel).textContent !== '', `${M} [data-cmn-count="sources"]`, { timeout: 15000 });
  const counts = await page.$$eval(`${M} .cmn-count`, els => els.map(e => e.textContent));
  check('tab bar: counters filled (sources, missing, people)', counts[0] !== '' && counts[1] !== '' && counts[2] !== '', counts.join('|'));
  check('tab bar: missing episodes shown as an alert', await page.$eval(`${M} [data-cmn-count="missing"]`, e => e.classList.contains('cmn-alert')));
  await page.waitForSelector(`${M} .me-card`, { state: 'visible' });
  await shot('N1-missing-with-tabs-1440');
  await page.click(`${M} .cmn-tab:has-text("Personnes")`);
  await page.waitForSelector(`${P} .cmn-nav`, { state: 'visible' });
  check('tab bar: a tab opens its page', page.url().includes('CustomMetadataPeople'));
  await page.waitForTimeout(800); await shot('N2-people-with-tabs-1440');
  await open('Custom%20Metadata%20Provider', C);
  await page.waitForSelector(`${C} #sourcesList .cm-card`, { state: 'visible' });
  await page.waitForTimeout(600); await shot('N3-config-with-tabs-1440');

  // ---- 2. Diagnostic: the library list
  await open('CustomMetadataDiagnostic', D);
  await page.click(`${D} #dgScan`);
  await page.waitForSelector(`${D} .dg-tile`, { state: 'visible', timeout: 30000 });
  const rows = await page.$$eval(`${D} .dg-row:not(.dg-row-head)`, els => els.map(e => e.textContent));
  check('list: the episode without a tag is reported', rows.some(r => r.includes('E08 Sans identifiant') && r.includes('Aucun tag')), `${rows.length} rows`);
  check('list: a row without a tag has no diagnose button', await page.$$eval(`${D} .dg-row`, els => els.filter(e => e.textContent.includes('Sans identifiant')).every(e => !e.querySelector('[data-diagnose]'))));
  check('tab bar: the diagnostic counter follows the list', (await page.$eval(`${D} [data-cmn-count="diagnostic"]`, e => e.textContent)) === (await page.$eval(`${D} .dg-tile b`, e => e.textContent)));
  await shot('D1-list-1440');

  // ---- 3. Diagnostic: one element, step by step
  const diagnose = async (name, kind = '') => {
    await page.fill(`${D} #dgName`, name);
    await page.selectOption(`${D} #dgKind`, kind);
    await page.click(`${D} #dgRun`);
    await page.waitForFunction(sel => { const v = document.querySelector(sel + ' .dg-verdict'); return v && !document.querySelector(sel + ' #dgRun').disabled; }, D, { timeout: 30000 });
    return {
      verdict: await page.$eval(`${D} .dg-verdict strong`, e => e.textContent),
      states: (await page.$$eval(`${D} .dg-step`, els => els.map(e => e.getAttribute('data-state')))).join(','),
      action: await page.$$eval(`${D} .dg-step .dg-action`, els => els.map(e => e.textContent).join('|'))
    };
  };
  let r = await diagnose('E01 Talk #1 {dm-d1v1}.mkv');
  check('element: a good episode passes the six steps', r.states === 'ok,ok,ok,ok,ok,ok', `${r.states} / ${r.verdict}`);
  await shot('D2-element-ok-1440');
  r = await diagnose('E08 Sans identifiant.mkv');
  check('element: no tag stops at step 1', r.states === 'fail,skip,skip,skip,skip,skip' && r.verdict.includes('étape 1'), r.states);
  r = await diagnose('Inconnu {zz-12}.mkv');
  check('element: unknown prefix stops at step 2', r.states.startsWith('ok,fail,skip') && r.action === 'Voir les sources', `${r.states} / ${r.action}`);
  r = await diagnose('Saison fantôme {dm-nexistepas}', 'season');
  check('element: an id the API does not know stops at step 4', r.states.startsWith('ok,ok,info,fail') && r.action === 'Tester la source', `${r.states} / ${r.verdict}`);
  await shot('D3-element-api-error-1440');
  await page.click(`${D} .dg-step .dg-action`);
  await page.waitForSelector(`${C} #editorView:not([hidden])`, { state: 'visible', timeout: 20000 });
  check('element: "Tester la source" opens the editor of that source', (await page.$eval(`${C} #srcPrefix`, e => e.value)) === 'dm');

  // ---- 4. click-to-fill mappings (still in the editor of the dm source)
  await page.click(`${C} [data-go=video]`);
  await page.fill(`${C} #srcVideoOverview`, '');
  await page.focus(`${C} #srcVideoOverview`);
  await page.selectOption(`${C} #testItemType`, 'video');
  await page.fill(`${C} #testItemId`, 'd1v1');
  await page.click(`${C} #btnTestSource`);
  await page.waitForSelector(`${C} #pickTree .cm-pick-leaf`, { state: 'visible', timeout: 20000 });
  check('pick: the hint names the selected field', (await page.$eval(`${C} #pickHint`, e => e.textContent)).includes('Synopsis'));
  const leaves = await page.$$eval(`${C} #pickTree .cm-pick-leaf`, els => els.map(e => e.getAttribute('data-path')));
  const overviewPath = leaves.find(p => /overview/i.test(p));
  await page.click(`${C} #pickTree .cm-pick-leaf[data-path="${overviewPath}"]`);
  check('pick: a click writes the path into the field', (await page.$eval(`${C} #srcVideoOverview`, e => e.value)) === overviewPath, overviewPath);
  check('pick: the editor is marked as modified', (await page.$eval(`${C} #dirtyState`, e => e.textContent)).includes('non enregistrées'));
  await page.setViewportSize({ width: 1440, height: 1300 }); await page.waitForTimeout(400);
  await shot('M1-pick-video-1440');
  // episode list: paths are relative to one element of the list
  await page.click(`${C} [data-go=episodes]`);
  await page.fill(`${C} #srcListTitle`, '');
  await page.focus(`${C} #srcListTitle`);
  await page.selectOption(`${C} #testItemType`, 'episodelist');
  await page.fill(`${C} #testItemId`, 'a2');
  await page.click(`${C} #btnTestSource`);
  await page.waitForFunction(sel => { const s = document.querySelector(sel + ' #testResult .cm-test-status'); return s && s.textContent.includes('videos'); }, C, { timeout: 20000 });
  const listLeaves = await page.$$eval(`${C} #pickTree .cm-pick-leaf`, els => els.map(e => e.getAttribute('data-path')));
  const titlePath = listLeaves.find(p => /\[0\]\.?title$/i.test(p));
  if (titlePath) {
    await page.click(`${C} #pickTree .cm-pick-leaf[data-path="${titlePath}"]`);
    check('pick: an episode-list field gets a path relative to the element', (await page.$eval(`${C} #srcListTitle`, e => e.value)) === 'title', `${titlePath} → ${await page.$eval(`${C} #srcListTitle`, e => e.value)}`);
  } else {
    check('pick: the episode list is clickable', false, listLeaves.slice(0, 6).join(' | ') || 'no tree (answer too long?)');
  }
  await shot('M2-pick-list-1440');
  // A list far longer than the 20 KB of raw text the screen keeps (120 episodes inside an object): still clickable, with its real length
  await page.selectOption(`${C} #testItemType`, 'episodelist');
  await page.fill(`${C} #testItemId`, 'longlist');
  await page.focus(`${C} #srcListItemsPath`);
  await page.click(`${C} #btnTestSource`);
  await page.waitForSelector(`${C} #pickTree .cm-pick-leaf[data-path="data.videos"]`, { state: 'visible', timeout: 20000 });
  const rawText = await page.$eval(`${C} #testResult details:last-of-type pre`, e => e.textContent);
  check('episode list: the raw answer comes in full (past 20 KB, up to its last episode) and the tree stays clickable', rawText.length > 40000 && rawText.includes('"id": "long120"') && !rawText.includes('tronqué') && (await text(page, `${C} #pickTree .cm-pick-leaf[data-path="data.videos"] .cm-pick-val`)) === '[120 éléments]', `raw ${rawText.length} chars`);
  await page.click(`${C} #testResult details:last-of-type summary`);   // the raw answer is folded until asked for
  check('episode list: the raw answer shows its size', (await text(page, `${C} #testResult details:last-of-type summary`)).includes('Ko'));
  await page.click(`${C} #btnCopyRaw`);
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  check('episode list: "Copier la réponse brute" copies the whole answer', copied.length > 40000 && copied.includes('"id": "long120"') && copied.trimEnd().endsWith('}'), `${copied.length} chars`);
  await page.click(`${C} #pickTree .cm-pick-leaf[data-path="data.videos"]`);
  check('episode list: a click on the list writes its path into "Tableau d\'épisodes"', (await page.$eval(`${C} #srcListItemsPath`, e => e.value)) === 'data.videos');
  await page.focus(`${C} #srcListTitle`);
  await page.click(`${C} #btnTestSource`);
  await page.waitForFunction(sel => document.querySelector(sel + ' #testResult .cm-test-status') && /\b120\b/.test(document.querySelector(sel + ' #testResult').textContent), C, { timeout: 20000 });
  check('episode list: all 120 episodes are listed, not the first 20', (await text(page, `${C} #testResult h4:has-text("épisodes")`)) === '120 épisodes', await text(page, `${C} #testResult h4:has-text("épisodes")`));
  const nested = await page.$$eval(`${C} #pickTree .cm-pick-leaf`, els => els.map(e => e.getAttribute('data-path')));
  check('episode list: the example is the most complete element, not the first (it has fields the first lacks)', nested.includes('data.videos[0].guest.role') && nested.includes('data.videos[0].overview') && nested.includes('data.videos[0].rating'), nested.join(' | ').slice(0, 300));
  check('episode list: a field that only ANOTHER episode has (sponsor, meta.award) is still offered', nested.includes('data.videos[0].sponsor') && nested.includes('data.videos[0].meta.award'), nested.filter(p => /sponsor|award/.test(p)).join(','));
  check('episode list: a field nested eight levels deep is offered', nested.includes('data.videos[0].deep.a.b.c.d.e.f'), nested.filter(p => /deep/.test(p)).join(','));
  const overviewTip = await page.$eval(`${C} #pickTree .cm-pick-leaf[data-path="data.videos[0].overview"]`, e => e.getAttribute('title') || '');
  check('episode list: a long value is cut on screen but whole in its tooltip', overviewTip.length > 300 && overviewTip.length <= 2000, `${overviewTip.length} chars`);
  const longTitle = nested.find(p => /data\.videos\[0\]\.title$/.test(p));
  if (longTitle) {
    await page.click(`${C} #pickTree .cm-pick-leaf[data-path="${longTitle}"]`);
    check('episode list: a field gets its path relative to one element of the list', (await page.$eval(`${C} #srcListTitle`, e => e.value)) === 'title', longTitle);
  } else {
    check('episode list: the first element of the list is shown', false, nested.slice(0, 6).join(' | '));
  }
  // Fold, hide and filter the fields of the answer (what one does not care about stays out of the way)
  const leaf = path => `${C} #pickTree .cm-pick-leaf[data-path="${path}"]`;
  await page.click(`${C} [data-pick-toggle="data.videos"]`);
  check('tree: folding a list hides its fields', !(await page.$(leaf('data.videos[0].title'))) && (await page.$eval(`${C} [data-pick-toggle="data.videos"]`, e => e.getAttribute('aria-expanded'))) === 'false');
  await page.click(`${C} [data-pick-toggle="data.videos"]`);
  check('tree: unfolding brings them back', !!(await page.$(leaf('data.videos[0].title'))));
  const before = (await page.$$(`${C} #pickTree .cm-pick-leaf`)).length;
  await page.click(`${C} [data-pick-hide="data.videos[0].overview"]`);
  check('tree: a hidden field is gone and a "Réafficher" link says how many', !(await page.$(leaf('data.videos[0].overview'))) && (await page.$$(`${C} #pickTree .cm-pick-leaf`)).length === before - 1 && (await text(page, `${C} #pickReveal`)).startsWith('1 champ masqué'), await text(page, `${C} #pickReveal`));
  await page.focus(`${C} #srcListTitle`);
  await page.click(`${C} #btnTestSource`);
  await page.waitForFunction(sel => document.querySelector(sel + ' #pickTree .cm-pick-leaf'), C, { timeout: 20000 });
  check('tree: the hidden field stays hidden at the next test (kept in this browser)', !(await page.$(leaf('data.videos[0].overview'))) && await page.isVisible(`${C} #pickReveal`));
  await page.fill(`${C} #pickFilter`, 'guest');
  const filtered = await page.$$eval(`${C} #pickTree .cm-pick-leaf`, els => els.map(e => e.getAttribute('data-path')));
  check('tree: the filter keeps the matching fields and their parents only', filtered.includes('data.videos[0].guest.name') && filtered.includes('data.videos[0].guest.role') && !filtered.includes('data.videos[0].title'), filtered.join(','));
  await page.fill(`${C} #pickFilter`, 'zzzz');
  check('tree: a filter with no match says so', (await text(page, `${C} #pickTree`)).includes('Aucun champ ne correspond'));
  await page.fill(`${C} #pickFilter`, '');
  await page.click(`${C} #pickCollapseAll`);
  const collapsed = (await page.$$(`${C} #pickTree .cm-pick-leaf`)).length;
  await page.click(`${C} #pickExpandAll`);
  const expanded = (await page.$$(`${C} #pickTree .cm-pick-leaf`)).length;
  check('tree: "Tout replier" keeps the top level, "Tout déplier" brings the rest back', collapsed > 0 && collapsed < expanded, `${collapsed} → ${expanded}`);
  await page.click(`${C} #pickReveal`);
  check('tree: "Réafficher" brings the hidden field back', !!(await page.$(leaf('data.videos[0].overview'))) && !(await page.isVisible(`${C} #pickReveal`)));
  await shot('M3-pick-long-list-1440');
  // nothing is saved: leave the editor without saving
  await page.evaluate(() => { window.__cmdAnswer='ok'; if(!window.__cmdAuto){window.__cmdAuto=new MutationObserver(function(){var b=document.querySelector('.cmd-backdrop [data-cmd='+window.__cmdAnswer+']'); if(b){b.click();}}); window.__cmdAuto.observe(document.documentElement,{childList:true,subtree:true});} });
  await page.click(`${C} #btnCancelEdit`);

  // ---- 5. wide screen, phone, accessibility
  await page.setViewportSize({ width: 1920, height: 1100 });
  await open('CustomMetadataDiagnostic', D);
  await diagnose('E01 Talk #1 {dm-d1v1}.mkv');
  await shot('D4-diagnostic-1920');
  await page.setViewportSize({ width: 412, height: 900 });
  await open('CustomMetadataDiagnostic', D);
  await diagnose('Inconnu {zz-12}.mkv');
  check('phone: no horizontal scroll on the Diagnostic page', await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
  await shot('D5-diagnostic-mobile');
  // .button-submit is Jellyfin's own themed button (white on its accent blue, 2.85:1 on every page of the dashboard): not ours to restyle
  const axe = await new AxeBuilder({ page }).include(D).exclude('.button-submit').withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  check('accessibility: no serious axe violation on the Diagnostic page', serious.length === 0, serious.map(v => `${v.id}: ${v.nodes.map(n => n.target.join(' ') + ' ' + (n.any[0]?.message || '')).join(' ; ')}`).join(', '));
} catch (e) {
  console.log('ERROR', e.message.split('\n')[0]); fail++;
} finally {
  console.log(`\n${pass} PASS, ${fail} FAIL`);
  await browser.close();
  process.exit(fail ? 1 : 0);
}
